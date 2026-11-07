using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Integrations.Mcp;

namespace Nostos.Backend.Services.Mcp;

/// <summary>The token a request presented, once it checked out.</summary>
public sealed record McpAccessTokenIdentity(Guid Id, string Name, string Scope);

/// <summary>A token just made: <paramref name="Token"/> is shown once and never again.</summary>
public sealed record McpAccessTokenCreatedDto(McpConnectedAppDto Details, string Token);

/// <summary>The body of a request to make a token.</summary>
/// <param name="Scope"><c>read</c> or <c>read_write</c>.</param>
public sealed record CreateMcpAccessTokenRequest(string? Name, string? Scope);

/// <summary>
/// SelfHosted access tokens for <c>/mcp</c> (issue #1469).
///
/// A token is <c>nst_</c> plus 256 random bits in base64url. Only its SHA-256
/// is stored; the token is returned once, from <see cref="CreateAsync"/>, and
/// is never logged. Revoking deletes the row, so the next request with it is
/// refused: every request looks the token up again. It is also SelfHosted's
/// record of connected apps (<see cref="IMcpConnectedApps"/>): one per token.
/// </summary>
public sealed class McpAccessTokenService(
    IDbContextFactory<NostosDbContext> contexts,
    TimeProvider? clock = null) : IMcpConnectedApps
{
    public const string Prefix = "nst_";

    /// <summary><see cref="Prefix"/> plus 32 bytes in unpadded base64url.</summary>
    public const int TokenLength = 4 + 43;

    /// <summary>How stale "last used" may get before a request writes it again.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    /// <summary>At most this many tokens, so a script cannot fill the table.</summary>
    public const int MaxTokens = 100;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The owner makes tokens here.</summary>
    public bool CanCreate => true;

    public async Task<IReadOnlyList<McpConnectedAppDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.McpAccessTokens.AsNoTracking().ToListAsync(cancellationToken);
        return rows.OrderByDescending(t => t.CreatedAtUtc).Select(ToDto).ToList();
    }

    public async Task<McpAccessTokenCreatedDto> CreateAsync(
        string name, string scope, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > McpAccessTokenModel.MaxNameLength)
            throw new ArgumentException($"A name of 1 to {McpAccessTokenModel.MaxNameLength} characters is required.", nameof(name));
        if (scope is not (McpAccessTokenModel.ReadScope or McpAccessTokenModel.ReadWriteScope))
            throw new ArgumentException("Scope must be 'read' or 'read_write'.", nameof(scope));

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.McpAccessTokens.CountAsync(cancellationToken) >= MaxTokens)
            throw new InvalidOperationException($"This server already has {MaxTokens} access tokens. Revoke one first.");

        var token = Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var row = new McpAccessTokenModel
        {
            Name = trimmed,
            Scope = scope,
            TokenHash = Hash(token),
            CreatedAtUtc = _clock.GetUtcNow().UtcDateTime,
        };
        db.McpAccessTokens.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return new McpAccessTokenCreatedDto(ToDto(row), token);
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return false;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.McpAccessTokens.Where(t => t.Id == guid).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// The token's owner record, or null when it is malformed, unknown or
    /// revoked. Notes when it was used, at most once per
    /// <see cref="LastUsedResolution"/>.
    /// </summary>
    public async Task<McpAccessTokenIdentity?> AuthenticateAsync(
        string token, CancellationToken cancellationToken = default)
    {
        if (!LooksLikeToken(token))
            return null;

        var hash = Hash(token);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.McpAccessTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        // The lookup is by hash, which tells an attacker nothing; the
        // comparison that decides is constant-time all the same.
        if (row is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(row.TokenHash), Encoding.ASCII.GetBytes(hash)))
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        if (row.LastUsedAtUtc is not { } last || now - last >= LastUsedResolution)
        {
            await db.McpAccessTokens.Where(t => t.Id == row.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.LastUsedAtUtc, now), cancellationToken);
        }

        return new McpAccessTokenIdentity(row.Id, row.Name, row.Scope);
    }

    public static bool LooksLikeToken(string token) =>
        token.Length == TokenLength
        && token.StartsWith(Prefix, StringComparison.Ordinal)
        && token.AsSpan(Prefix.Length).IndexOfAnyExcept(Base64UrlAlphabet) < 0;

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static readonly System.Buffers.SearchValues<char> Base64UrlAlphabet =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private static McpConnectedAppDto ToDto(McpAccessTokenModel row) =>
        new(row.Id.ToString("D"), row.Name, McpConnectedAppKind.Token, row.Scope, row.CreatedAtUtc, row.LastUsedAtUtc);
}
