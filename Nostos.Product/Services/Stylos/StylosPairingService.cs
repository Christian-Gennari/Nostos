using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Stylos;

/// <summary>Optional host settings for the Stylos Sync Protocol.</summary>
public sealed class StylosOptions
{
    public const string SectionName = "Stylos";

    public const int MaxServerNameLength = 100;

    /// <summary>
    /// The label devices put in conflict-copy names (<c>… from study.md</c>).
    /// Null lets a device fall back to the URL's host.
    /// </summary>
    public string? ServerName { get; set; }
}

/// <summary>A device as Settings → Devices lists it. Never carries a token.</summary>
public sealed record StylosDeviceDto(
    Guid Id, string Name, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>A pairing code waiting for its owner.</summary>
public sealed record StylosPendingPairing(string Code, string DeviceName, DateTime ExpiresAtUtc);

/// <summary>The token's owner, once it checked out.</summary>
public sealed record StylosDeviceIdentity(Guid DeviceId, string OwnerKey, string Name);

/// <summary>What approving a code did.</summary>
public abstract record StylosApprovalOutcome
{
    public sealed record Approved(string DeviceName, Guid DeviceId) : StylosApprovalOutcome;

    public sealed record AlreadyApproved : StylosApprovalOutcome;

    public sealed record Expired : StylosApprovalOutcome;

    public sealed record NotFound : StylosApprovalOutcome;
}

/// <summary>
/// Pairing and device credentials for the Stylos Sync Protocol v1.
///
/// <para>A device asks for a code, the owner approves it at <c>/pair</c>, and
/// the device collects its token by polling. The device row stores only the
/// token's SHA-256; the token waiting to be collected is Data-Protection
/// ciphertext, cleared when the handoff window closes. Revoking deletes the
/// device row, so the next request with that token is refused.</para>
/// </summary>
public sealed class StylosPairingService(
    IDbContextFactory<NostosDbContext> contexts,
    IDataProtectionProvider dataProtection,
    IOptions<StylosOptions>? options = null,
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

    public StylosOptions Options => options?.Value ?? new StylosOptions();

    /// <summary>What <c>GET /pair/{pair_id}</c> answers, or null for an unknown pairing.</summary>
    public async Task<StylosPairStatusResponse?> StatusAsync(Guid pairId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var pairing = await db.StylosPairings.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == pairId, ct);
        if (pairing is null)
            return null;

        if (pairing.Status == StylosPairingStatus.Pending && now >= pairing.ExpiresAtUtc)
        {
            await db.StylosPairings
                .Where(p => p.Id == pairId && p.Status == StylosPairingStatus.Pending)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.Status, StylosPairingStatus.Expired)
                        .SetProperty(p => p.TokenEncrypted, (string?)null),
                    ct);
            return Response(StylosPairStatus.Expired, null);
        }

        if (pairing.Status == StylosPairingStatus.Pending)
            return Response(StylosPairStatus.Pending, null);

        if (pairing.Status != StylosPairingStatus.Approved)
            return Response(StylosPairStatus.Expired, null);

        // The device polls every two seconds; a token that was approved but
        // never collected inside the handoff window is not handed out later.
        var handoffEnds = (pairing.ApprovedAtUtc ?? pairing.CreatedAtUtc) + TokenHandoff;
        if (now > handoffEnds || pairing.TokenEncrypted is null)
        {
            await db.StylosPairings
                .Where(p => p.Id == pairId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.TokenEncrypted, (string?)null),
                    ct);
            return Response(StylosPairStatus.Expired, null);
        }

        return Response(StylosPairStatus.Approved, Unprotect(pairing.TokenEncrypted));
    }

    private StylosPairStatusResponse Response(string status, string? token) =>
        new(status, token, null, string.IsNullOrWhiteSpace(_serverName) ? null : _serverName.Trim());

    /// <summary>A fresh pairing, or null when eight generated codes were all taken.</summary>
    public async Task<StylosPairStartResponse?> StartAsync(
        string deviceName, string devicePublicKey, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(ct);

        // Old pairing rows are operational leftovers, not history worth keeping.
        await db.StylosPairings
            .Where(p => p.CreatedAtUtc < now - PairingRetention)
            .ExecuteDeleteAsync(ct);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var code = StylosPairingCodes.Generate();
            var taken = await db.StylosPairings.AsNoTracking().AnyAsync(
                p => p.Code == code
                    && p.Status == StylosPairingStatus.Pending
                    && p.ExpiresAtUtc > now,
                ct);
            if (taken)
                continue;

            var pairing = new StylosPairingModel
            {
                Id = Guid.CreateVersion7(),
                Code = code,
                DeviceName = deviceName,
                DevicePublicKey = devicePublicKey,
                Status = StylosPairingStatus.Pending,
                CreatedAtUtc = now,
                ExpiresAtUtc = now + StylosPairingCodes.Lifetime,
            };
            db.StylosPairings.Add(pairing);
            await db.SaveChangesAsync(ct);
            return new StylosPairStartResponse(
                pairing.Id.ToString("D"),
                pairing.Code,
                new DateTimeOffset(pairing.ExpiresAtUtc, TimeSpan.Zero).ToUnixTimeSeconds());
        }

        return null;
    }

    /// <summary>The pending pairing a code names, for the approval page to show the device.</summary>
    public async Task<StylosPendingPairing?> PendingAsync(string? code, CancellationToken ct = default)
    {
        var normalized = StylosPairingCodes.Normalize(code);
        if (normalized is null)
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var pairing = await db.StylosPairings.AsNoTracking()
            .Where(p => p.Code == normalized
                && p.Status == StylosPairingStatus.Pending
                && p.ExpiresAtUtc > now)
            .OrderBy(p => p.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        return pairing is null
            ? null
            : new StylosPendingPairing(pairing.Code, pairing.DeviceName, pairing.ExpiresAtUtc);
    }

    public async Task<StylosApprovalOutcome> ApproveAsync(
        string ownerKey, string? code, CancellationToken ct = default)
    {
        var normalized = StylosPairingCodes.Normalize(code);
        if (normalized is null)
            return new StylosApprovalOutcome.NotFound();

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var pairing = await db.StylosPairings
            .Where(p => p.Code == normalized)
            .OrderByDescending(p => p.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (pairing is null || pairing.Status == StylosPairingStatus.Denied)
            return new StylosApprovalOutcome.NotFound();
        if (pairing.Status == StylosPairingStatus.Approved)
            return new StylosApprovalOutcome.AlreadyApproved();
        if (now >= pairing.ExpiresAtUtc)
        {
            pairing.Status = StylosPairingStatus.Expired;
            await db.SaveChangesAsync(ct);
            return new StylosApprovalOutcome.Expired();
        }

        var token = StylosTokens.Create();
        var device = new StylosDeviceModel
        {
            Id = Guid.CreateVersion7(),
            OwnerKey = ownerKey,
            Name = pairing.DeviceName,
            PublicKey = pairing.DevicePublicKey,
            TokenHash = StylosTokens.Hash(token),
            CreatedAtUtc = now,
        };

        // One approval wins; a second click (or another window) sees 0 rows and
        // must not make a second device for the same code.
        var claimed = await db.StylosPairings
            .Where(p => p.Id == pairing.Id && p.Status == StylosPairingStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, StylosPairingStatus.Approved)
                    .SetProperty(p => p.OwnerKey, ownerKey)
                    .SetProperty(p => p.ApprovedAtUtc, now)
                    .SetProperty(p => p.DeviceId, device.Id)
                    .SetProperty(p => p.TokenEncrypted, Protect(token)),
                ct);
        if (claimed == 0)
            return new StylosApprovalOutcome.AlreadyApproved();

        db.StylosDevices.Add(device);
        await db.SaveChangesAsync(ct);
        return new StylosApprovalOutcome.Approved(pairing.DeviceName, device.Id);
    }

    /// <summary>Refuses a pending code. A denied pairing polls as expired.</summary>
    public async Task<bool> DenyAsync(string code, CancellationToken ct = default)
    {
        var normalized = StylosPairingCodes.Normalize(code);
        if (normalized is null)
            return false;

        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var denied = await db.StylosPairings
            .Where(p => p.Code == normalized
                && p.Status == StylosPairingStatus.Pending
                && p.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, StylosPairingStatus.Denied)
                    .SetProperty(p => p.TokenEncrypted, (string?)null),
                ct);
        return denied > 0;
    }

    public async Task<IReadOnlyList<StylosDeviceDto>> ListDevicesAsync(
        string ownerKey, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.StylosDevices
            .AsNoTracking()
            .Where(d => d.OwnerKey == ownerKey)
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToListAsync(ct);
        return rows
            .Select(d => new StylosDeviceDto(d.Id, d.Name, d.CreatedAtUtc, d.LastUsedAtUtc))
            .ToList();
    }

    public async Task<bool> RevokeAsync(string ownerKey, Guid deviceId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.StylosDevices
            .Where(d => d.Id == deviceId && d.OwnerKey == ownerKey)
            .ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>The token's device, or null when it is malformed, unknown or revoked.</summary>
    public async Task<StylosDeviceIdentity?> AuthenticateAsync(
        string? token, CancellationToken ct = default)
    {
        if (!StylosTokens.LooksLikeToken(token))
            return null;

        var hash = StylosTokens.Hash(token!);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var device = await db.StylosDevices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TokenHash == hash, ct);
        if (device is null || !StylosTokens.HashEquals(device.TokenHash, hash))
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        if (device.LastUsedAtUtc is not { } last || now - last >= LastUsedResolution)
        {
            await db.StylosDevices
                .Where(d => d.Id == device.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastUsedAtUtc, now), ct);
        }

        return new StylosDeviceIdentity(device.Id, device.OwnerKey, device.Name);
    }

    private string Protect(string token) =>
        dataProtection.CreateProtector("Nostos.Stylos.DeviceToken").Protect(token);

    private string? Unprotect(string ciphertext)
    {
        try
        {
            return dataProtection.CreateProtector("Nostos.Stylos.DeviceToken").Unprotect(ciphertext);
        }
        catch (CryptographicException)
        {
            // The data-protection keys differ (a restore from another machine):
            // the device pairs again rather than receiving an unusable token.
            return null;
        }
    }
}
