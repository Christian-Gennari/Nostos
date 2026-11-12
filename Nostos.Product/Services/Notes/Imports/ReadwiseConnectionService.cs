using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// The connection status the Settings card shows. It never carries the token
/// or its ciphertext: only whether one is saved and how far imports have run.
/// </summary>
public sealed record ReadwiseConnectionStatusDto(
    bool Connected,
    DateTime? LastImportedAtUtc,
    DateTime? ImportedThroughUtc);

public sealed record ReadwiseConnectRequest(string? Token);

public sealed record ReadwiseCommitRequest(
    IReadOnlyList<HighlightImportDecision>? Decisions,
    Guid? BatchId);

/// <summary>
/// Stores one library's Readwise access token as Data-Protection ciphertext —
/// the mechanism the AI provider keys use — and remembers when the last
/// successful import started so the next one asks Readwise only for changes.
///
/// <para>
/// The token is never returned to a client, logged, put in a portable export
/// or kept in a SelfHosted backup (the backup builder scrubs this table).
/// Methods that need it read and decrypt it inside the process only.
/// </para>
/// </summary>
public sealed class ReadwiseConnectionService
{
    public const string ProtectorPurpose = "Nostos.Readwise.ApiToken.v1";
    public const int MaxTokenLength = 1024;

    private readonly NostosDbContext _db;
    private readonly ReadwiseApiClient _api;
    private readonly IDataProtector _protector;
    private readonly ILogger<ReadwiseConnectionService> _logger;

    public ReadwiseConnectionService(
        NostosDbContext db,
        ReadwiseApiClient api,
        IDataProtectionProvider dataProtection,
        ILogger<ReadwiseConnectionService> logger)
    {
        _db = db;
        _api = api;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _logger = logger;
    }

    public async Task<ReadwiseConnectionStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var row = await _db.ReadwiseConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(connection => connection.Id == ReadwiseConnectionModel.SingletonId, ct);
        return Status(row);
    }

    /// <summary>
    /// Checks the token against <c>GET /api/v2/auth/</c> first, so a typo is
    /// refused while the owner is looking at the form. Reconnecting with the
    /// same token keeps the watermark; a different token starts from the
    /// beginning because it may be a different Readwise account.
    /// </summary>
    public async Task<ReadwiseConnectionStatusDto> ConnectAsync(
        string? token,
        CancellationToken ct = default)
    {
        var value = token?.Trim() ?? string.Empty;
        if (value.Length == 0)
            throw new ArgumentException("Paste the access token from readwise.io/access_token.");
        if (value.Length > MaxTokenLength)
            throw new ArgumentException("That does not look like a Readwise access token.");
        if (value.Any(char.IsWhiteSpace))
            throw new ArgumentException("That does not look like a Readwise access token.");

        await _api.ValidateTokenAsync(value, ct);

        var now = DateTime.UtcNow;
        var row = await _db.ReadwiseConnections
            .FirstOrDefaultAsync(connection => connection.Id == ReadwiseConnectionModel.SingletonId, ct);
        var sameToken = row is not null && ReadsAs(row.TokenEncrypted, value);
        if (row is null)
        {
            row = new ReadwiseConnectionModel
            {
                Id = ReadwiseConnectionModel.SingletonId,
                CreatedAtUtc = now,
            };
            _db.ReadwiseConnections.Add(row);
        }
        else if (!sameToken)
        {
            // A different account's changes would be invisible behind the old
            // watermark.
            row.UpdatedAfterUtc = null;
            row.LastImportedAtUtc = null;
        }

        row.TokenEncrypted = _protector.Protect(value);
        row.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Readwise access token connected; its token and ciphertext are not logged.");
        return Status(row);
    }

    /// <summary>Removes the connection and its watermark. Returns false when none existed.</summary>
    public async Task<bool> DisconnectAsync(CancellationToken ct = default)
    {
        var row = await _db.ReadwiseConnections
            .FirstOrDefaultAsync(connection => connection.Id == ReadwiseConnectionModel.SingletonId, ct);
        if (row is null)
            return false;

        _db.ReadwiseConnections.Remove(row);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Readwise access token disconnected and removed.");
        return true;
    }

    /// <summary>
    /// The decrypted token, for one in-process import. Throws
    /// <see cref="ReadwiseNotConnectedException"/> when there is none or the
    /// host's Data-Protection key ring can no longer read it.
    /// </summary>
    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        var row = await _db.ReadwiseConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(connection => connection.Id == ReadwiseConnectionModel.SingletonId, ct);
        if (row is null || string.IsNullOrEmpty(row.TokenEncrypted))
            throw new ReadwiseNotConnectedException();

        try
        {
            return _protector.Unprotect(row.TokenEncrypted);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // The key ring changed (a restored backup, a new host). The token
            // is unreadable, so the owner reconnects; never log the ciphertext.
            _logger.LogWarning("The saved Readwise token cannot be decrypted on this host; reconnect Readwise.");
            throw new ReadwiseNotConnectedException();
        }
    }

    /// <summary>
    /// Advances the watermark after a successful commit. The watermark is the
    /// instant the export run started, so a change made during the import is
    /// fetched again rather than silently skipped.
    /// </summary>
    public async Task MarkImportedAsync(DateTime startedAtUtc, CancellationToken ct = default)
    {
        var row = await _db.ReadwiseConnections
            .FirstOrDefaultAsync(connection => connection.Id == ReadwiseConnectionModel.SingletonId, ct);
        if (row is null)
            return;

        row.UpdatedAfterUtc = startedAtUtc.ToUniversalTime();
        row.LastImportedAtUtc = DateTime.UtcNow;
        row.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private bool ReadsAs(string encrypted, string expected)
    {
        try
        {
            return string.Equals(_protector.Unprotect(encrypted), expected, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static ReadwiseConnectionStatusDto Status(ReadwiseConnectionModel? row) =>
        new(
            Connected: row is not null && !string.IsNullOrEmpty(row.TokenEncrypted),
            LastImportedAtUtc: row?.LastImportedAtUtc,
            ImportedThroughUtc: row?.UpdatedAfterUtc);
}
