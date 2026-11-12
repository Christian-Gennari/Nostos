using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Carrel;

/// <summary>Optional host settings for the Carrel Sync Protocol.</summary>
public sealed class CarrelOptions
{
    public const string SectionName = "Carrel";

    public const int MaxServerNameLength = 100;

    /// <summary>
    /// The label devices put in conflict-copy names (<c>… from study.md</c>).
    /// Null lets a device fall back to the URL's host.
    /// </summary>
    public string? ServerName { get; set; }
}

/// <summary>A device as Settings → Devices lists it. Never carries a token.</summary>
public sealed record CarrelDeviceDto(
    Guid Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>A pairing code waiting for its owner.</summary>
public sealed record CarrelPendingPairing(string Code, string DeviceName, DateTime ExpiresAtUtc);

/// <summary>The token's owner, once it checked out.</summary>
public sealed record CarrelDeviceIdentity(Guid DeviceId, string OwnerKey, string Name);

/// <summary>What approving a code did.</summary>
public abstract record CarrelApprovalOutcome
{
    public sealed record Approved(string DeviceName, Guid DeviceId) : CarrelApprovalOutcome;

    public sealed record AlreadyApproved : CarrelApprovalOutcome;

    public sealed record Expired : CarrelApprovalOutcome;

    public sealed record NotFound : CarrelApprovalOutcome;
}

/// <summary>
/// Pairing and device credentials for the Carrel Sync Protocol v1.
///
/// <para>A device asks for a code, the owner approves it at <c>/pair</c>, and
/// the device collects its token by polling. The device row stores only the
/// token's SHA-256; the token waiting to be collected is Data-Protection
/// ciphertext, cleared when the handoff window closes. Revoking deletes the
/// device row, so the next request with that token is refused.</para>
/// </summary>
public sealed class CarrelPairingService(
    ICarrelDeviceDirectory directory,
    IDataProtectionProvider dataProtection,
    IOptions<CarrelOptions>? options = null,
    TimeProvider? clock = null)
{
    /// <summary>How long after approval a device may still collect its token.</summary>
    public static readonly TimeSpan TokenHandoff = TimeSpan.FromMinutes(10);

    /// <summary>Approved pairings and their token ciphertext are forgotten after this.</summary>
    public static readonly TimeSpan PairingRetention = TimeSpan.FromHours(24);

    /// <summary>How stale "last used" may get before a request writes it again.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly string? _serverName = options?.Value.ServerName;

    public CarrelOptions Options => options?.Value ?? new CarrelOptions();

    /// <summary>What <c>GET /pair/{pair_id}</c> answers, or null for an unknown pairing.</summary>
    public async Task<CarrelPairStatusResponse?> StatusAsync(Guid pairId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await directory.OpenPairingsAsync(ct);
        var pairing = await db.CarrelPairings.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == pairId, ct);
        if (pairing is null)
            return null;

        if (pairing.Status == CarrelPairingStatus.Pending && now >= pairing.ExpiresAtUtc)
        {
            await db.CarrelPairings
                .Where(p => p.Id == pairId && p.Status == CarrelPairingStatus.Pending)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.Status, CarrelPairingStatus.Expired)
                        .SetProperty(p => p.TokenEncrypted, (string?)null),
                    ct);
            return Response(CarrelPairStatus.Expired, null);
        }

        if (pairing.Status == CarrelPairingStatus.Pending)
            return Response(CarrelPairStatus.Pending, null);

        if (pairing.Status != CarrelPairingStatus.Approved)
            return Response(CarrelPairStatus.Expired, null);

        // The device polls every two seconds; a token that was approved but
        // never collected inside the handoff window is not handed out later.
        var handoffEnds = (pairing.ApprovedAtUtc ?? pairing.CreatedAtUtc) + TokenHandoff;
        if (now > handoffEnds || pairing.TokenEncrypted is null)
        {
            await db.CarrelPairings
                .Where(p => p.Id == pairId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.TokenEncrypted, (string?)null),
                    ct);
            return Response(CarrelPairStatus.Expired, null);
        }

        return Response(CarrelPairStatus.Approved, Unprotect(pairing.TokenEncrypted));
    }

    private CarrelPairStatusResponse Response(string status, string? token) =>
        new(status, token, null, string.IsNullOrWhiteSpace(_serverName) ? null : _serverName.Trim());

    /// <summary>A fresh pairing, or null when eight generated codes were all taken.</summary>
    public async Task<CarrelPairStartResponse?> StartAsync(
        string deviceName, string devicePublicKey, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await directory.OpenPairingsAsync(ct);

        // Old pairing rows are operational leftovers, not history worth keeping.
        await db.CarrelPairings
            .Where(p => p.CreatedAtUtc < now - PairingRetention)
            .ExecuteDeleteAsync(ct);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var code = CarrelPairingCodes.Generate();
            var taken = await db.CarrelPairings.AsNoTracking().AnyAsync(
                p => p.Code == code
                    && p.Status == CarrelPairingStatus.Pending
                    && p.ExpiresAtUtc > now,
                ct);
            if (taken)
                continue;

            var pairing = new CarrelPairingModel
            {
                Id = Guid.CreateVersion7(),
                Code = code,
                DeviceName = deviceName,
                DevicePublicKey = devicePublicKey,
                Status = CarrelPairingStatus.Pending,
                CreatedAtUtc = now,
                ExpiresAtUtc = now + CarrelPairingCodes.Lifetime,
            };
            db.CarrelPairings.Add(pairing);
            await db.SaveChangesAsync(ct);
            return new CarrelPairStartResponse(
                pairing.Id.ToString("D"),
                pairing.Code,
                new DateTimeOffset(pairing.ExpiresAtUtc, TimeSpan.Zero).ToUnixTimeSeconds());
        }

        return null;
    }

    /// <summary>The pending pairing a code names, for the approval page to show the device.</summary>
    public async Task<CarrelPendingPairing?> PendingAsync(string? code, CancellationToken ct = default)
    {
        var normalized = CarrelPairingCodes.Normalize(code);
        if (normalized is null)
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await directory.OpenPairingsAsync(ct);
        var pairing = await db.CarrelPairings.AsNoTracking()
            .Where(p => p.Code == normalized
                && p.Status == CarrelPairingStatus.Pending
                && p.ExpiresAtUtc > now)
            .OrderBy(p => p.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        return pairing is null
            ? null
            : new CarrelPendingPairing(pairing.Code, pairing.DeviceName, pairing.ExpiresAtUtc);
    }

    public async Task<CarrelApprovalOutcome> ApproveAsync(
        string ownerKey, string? code, CancellationToken ct = default)
    {
        var normalized = CarrelPairingCodes.Normalize(code);
        if (normalized is null)
            return new CarrelApprovalOutcome.NotFound();

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await directory.OpenPairingsAsync(ct);
        var pairing = await db.CarrelPairings
            .Where(p => p.Code == normalized)
            .OrderByDescending(p => p.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (pairing is null || pairing.Status == CarrelPairingStatus.Denied)
            return new CarrelApprovalOutcome.NotFound();
        if (pairing.Status == CarrelPairingStatus.Approved)
            return new CarrelApprovalOutcome.AlreadyApproved();
        if (now >= pairing.ExpiresAtUtc)
        {
            pairing.Status = CarrelPairingStatus.Expired;
            await db.SaveChangesAsync(ct);
            return new CarrelApprovalOutcome.Expired();
        }

        var token = CarrelTokens.Create();
        var device = new CarrelDeviceModel
        {
            Id = Guid.CreateVersion7(),
            OwnerKey = ownerKey,
            Name = pairing.DeviceName,
            PublicKey = pairing.DevicePublicKey,
            TokenHash = CarrelTokens.Hash(token),
            CreatedAtUtc = now,
        };

        // One approval wins; a second click (or another window) sees 0 rows and
        // must not make a second device for the same code.
        var claimed = await db.CarrelPairings
            .Where(p => p.Id == pairing.Id && p.Status == CarrelPairingStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, CarrelPairingStatus.Approved)
                    .SetProperty(p => p.OwnerKey, ownerKey)
                    .SetProperty(p => p.ApprovedAtUtc, now)
                    .SetProperty(p => p.DeviceId, device.Id)
                    .SetProperty(p => p.TokenEncrypted, Protect(token)),
                ct);
        if (claimed == 0)
            return new CarrelApprovalOutcome.AlreadyApproved();

        // The pairing and the owner's devices may live in different databases,
        // so the claim above is committed before the device row exists. If the
        // device cannot be created, the code goes back to waiting and the
        // person can approve it again; nothing is left half-done.
        try
        {
            await using var owner = await directory.OpenOwnerAsync(ownerKey, ct);
            owner.CarrelDevices.Add(device);
            await owner.SaveChangesAsync(ct);
            await directory.RegisterDeviceAsync(ownerKey, device.Id, device.TokenHash, ct);
        }
        catch
        {
            await UndoApprovalAsync(db, pairing.Id, ownerKey, device.Id);
            throw;
        }

        return new CarrelApprovalOutcome.Approved(pairing.DeviceName, device.Id);
    }

    private async Task UndoApprovalAsync(
        NostosDbContext pairings, Guid pairingId, string ownerKey, Guid deviceId)
    {
        try
        {
            await pairings.CarrelPairings
                .Where(p => p.Id == pairingId && p.DeviceId == deviceId
                    && p.Status == CarrelPairingStatus.Approved)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.Status, CarrelPairingStatus.Pending)
                        .SetProperty(p => p.OwnerKey, (string?)null)
                        .SetProperty(p => p.ApprovedAtUtc, (DateTime?)null)
                        .SetProperty(p => p.DeviceId, (Guid?)null)
                        .SetProperty(p => p.TokenEncrypted, (string?)null),
                    CancellationToken.None);
            await using var owner = await directory.OpenOwnerAsync(ownerKey, CancellationToken.None);
            await owner.CarrelDevices
                .Where(d => d.Id == deviceId && d.OwnerKey == ownerKey)
                .ExecuteDeleteAsync(CancellationToken.None);
            await directory.UnregisterDeviceAsync(ownerKey, deviceId, CancellationToken.None);
        }
        catch
        {
            // Best effort: the original failure is what the caller sees. A
            // pairing left approved without a device expires on its own.
        }
    }

    /// <summary>Refuses a pending code. A denied pairing polls as expired.</summary>
    public async Task<bool> DenyAsync(string code, CancellationToken ct = default)
    {
        var normalized = CarrelPairingCodes.Normalize(code);
        if (normalized is null)
            return false;

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await directory.OpenPairingsAsync(ct);
        var denied = await db.CarrelPairings
            .Where(p => p.Code == normalized
                && p.Status == CarrelPairingStatus.Pending
                && p.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, CarrelPairingStatus.Denied)
                    .SetProperty(p => p.TokenEncrypted, (string?)null),
                ct);
        return denied > 0;
    }

    public async Task<IReadOnlyList<CarrelDeviceDto>> ListDevicesAsync(
        string ownerKey, CancellationToken ct = default)
    {
        await using var db = await directory.OpenOwnerAsync(ownerKey, ct);
        var rows = await db.CarrelDevices
            .AsNoTracking()
            .Where(d => d.OwnerKey == ownerKey)
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToListAsync(ct);
        return rows
            .Select(d => new CarrelDeviceDto(d.Id, d.Name, d.CreatedAtUtc, d.LastUsedAtUtc))
            .ToList();
    }

    public async Task<bool> RevokeAsync(string ownerKey, Guid deviceId, CancellationToken ct = default)
    {
        await using var db = await directory.OpenOwnerAsync(ownerKey, ct);
        var removed = await db.CarrelDevices
            .Where(d => d.Id == deviceId && d.OwnerKey == ownerKey)
            .ExecuteDeleteAsync(ct) > 0;

        // The device row is what authenticates a token, so the token is dead
        // once it is gone. The index entry is forgotten even when this call
        // found nothing to delete, so a retry after a failure cleans up too.
        await directory.UnregisterDeviceAsync(ownerKey, deviceId, ct);
        return removed;
    }

    /// <summary>The token's device, or null when it is malformed, unknown or revoked.</summary>
    public async Task<CarrelDeviceIdentity?> AuthenticateAsync(
        string? token, CancellationToken ct = default)
    {
        if (!CarrelTokens.LooksLikeToken(token))
            return null;

        var hash = CarrelTokens.Hash(token!);

        // The directory only says which owner to look in. The owner's own
        // database must still hold the device row, so a wrong index entry
        // makes the request fail and never reaches another owner's data.
        var ownerKey = await directory.FindOwnerAsync(hash, ct);
        if (string.IsNullOrEmpty(ownerKey))
            return null;

        NostosDbContext db;
        try
        {
            db = await directory.OpenOwnerAsync(ownerKey, ct);
        }
        catch (CarrelOwnerUnavailableException)
        {
            return null;
        }

        await using var _ = db;
        var device = await db.CarrelDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TokenHash == hash && d.OwnerKey == ownerKey, ct);
        if (device is null || !CarrelTokens.HashEquals(device.TokenHash, hash))
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        if (device.LastUsedAtUtc is not { } last || now - last >= LastUsedResolution)
        {
            await db.CarrelDevices
                .Where(d => d.Id == device.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastUsedAtUtc, now), ct);
        }

        return new CarrelDeviceIdentity(device.Id, device.OwnerKey, device.Name);
    }

    private string Protect(string token) =>
        dataProtection.CreateProtector("Nostos.Carrel.DeviceToken").Protect(token);

    private string? Unprotect(string ciphertext)
    {
        try
        {
            return dataProtection.CreateProtector("Nostos.Carrel.DeviceToken").Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            // The data-protection keys differ (a restore from another machine):
            // the device pairs again rather than receiving an unusable token.
            return null;
        }
    }
}
