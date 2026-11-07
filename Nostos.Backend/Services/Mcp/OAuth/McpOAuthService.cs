using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Integrations.Mcp;

namespace Nostos.Backend.Services.Mcp.OAuth;

/// <summary>An access token and refresh token pair, as the token endpoint returns them.</summary>
public sealed record McpOAuthIssuedTokens(string AccessToken, string RefreshToken, int ExpiresIn, string Scope);

/// <summary>Why a refresh failed: always <c>invalid_grant</c> to the client.</summary>
public enum McpOAuthRefreshOutcome
{
    Issued,
    Unknown,
    Expired,
    WrongClient,

    /// <summary>A refresh token that was already used: the whole grant is revoked.</summary>
    Reused,
}

/// <summary>
/// The grants and tokens of SelfHosted OAuth (issue #1470). Tokens are opaque:
/// <c>nsa_</c> (access, 1 hour) and <c>nsr_</c> (refresh, rotated on every use)
/// plus 256 random bits; only their SHA-256 is stored, and every request looks
/// the token up again, so revoking a grant stops both at once.
///
/// A refresh token works once. Presenting a used one means two parties hold
/// it, so the grant is revoked and the app must be approved again.
/// </summary>
public sealed class McpOAuthService(
    IDbContextFactory<NostosDbContext> contexts,
    TimeProvider? clock = null)
{
    public const string AccessPrefix = "nsa_";
    public const string RefreshPrefix = "nsr_";
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(1);

    /// <summary>An app unused this long has to be approved again.</summary>
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(60);

    /// <summary>At most this many apps at once, so a script cannot fill the table.</summary>
    public const int MaxGrants = 100;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static string OAuthScope(string scope) =>
        scope == McpConnectedAppScope.ReadWrite ? $"{NostosMcp.ReadScope} {NostosMcp.WriteScope}" : NostosMcp.ReadScope;

    /// <summary>Makes the grant an approved code stands for, and its first tokens. Null when there are too many apps.</summary>
    public async Task<(Guid GrantId, McpOAuthIssuedTokens Tokens)?> CreateGrantAsync(
        McpOAuthCodeGrant code, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        if (await db.McpOAuthGrants.CountAsync(cancellationToken) >= MaxGrants)
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        var grant = new McpOAuthGrantModel
        {
            ClientId = code.ClientId,
            ClientName = code.ClientName,
            Scope = code.Scope,
            CreatedAtUtc = now,
        };
        db.McpOAuthGrants.Add(grant);
        var tokens = AddTokens(db, grant.Id, grant.Scope, now);
        await db.SaveChangesAsync(cancellationToken);
        return (grant.Id, tokens);
    }

    public async Task<(McpOAuthRefreshOutcome Outcome, McpOAuthIssuedTokens? Tokens)> RefreshAsync(
        string refreshToken, string? clientId, CancellationToken cancellationToken = default)
    {
        if (!LooksLike(refreshToken, RefreshPrefix))
            return (McpOAuthRefreshOutcome.Unknown, null);

        var hash = McpAccessTokenService.Hash(refreshToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.McpOAuthTokens.AsNoTracking().Include(t => t.Grant)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == McpOAuthTokenModel.RefreshKind, cancellationToken);
        if (row?.Grant is not { } grant)
            return (McpOAuthRefreshOutcome.Unknown, null);
        if (!string.Equals(grant.ClientId, clientId, StringComparison.Ordinal))
            return (McpOAuthRefreshOutcome.WrongClient, null);

        if (row.ConsumedAtUtc is not null)
        {
            await RevokeGrantAsync(db, grant.Id, cancellationToken);
            return (McpOAuthRefreshOutcome.Reused, null);
        }

        if (row.ExpiresAtUtc <= now)
            return (McpOAuthRefreshOutcome.Expired, null);

        // Spend it first, and only if nobody else just did: two refreshes
        // racing with one token is a reuse, too.
        var spent = await db.McpOAuthTokens
            .Where(t => t.Id == row.Id && t.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.ConsumedAtUtc, now), cancellationToken);
        if (spent == 0)
        {
            await RevokeGrantAsync(db, grant.Id, cancellationToken);
            return (McpOAuthRefreshOutcome.Reused, null);
        }

        // Spent refresh tokens stay until they would have expired, so a
        // replay is recognised; expired rows of this grant go now.
        await db.McpOAuthTokens
            .Where(t => t.GrantId == grant.Id && t.ExpiresAtUtc <= now)
            .ExecuteDeleteAsync(cancellationToken);
        var tokens = AddTokens(db, grant.Id, grant.Scope, now);
        await db.SaveChangesAsync(cancellationToken);
        return (McpOAuthRefreshOutcome.Issued, tokens);
    }

    /// <summary>
    /// RFC 7009: a refresh token revokes its whole grant, an access token
    /// only itself. Unknown tokens are not an error.
    /// </summary>
    public async Task RevokeTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var isRefresh = LooksLike(token, RefreshPrefix);
        if (!isRefresh && !LooksLike(token, AccessPrefix))
            return;

        var hash = McpAccessTokenService.Hash(token);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.McpOAuthTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (row is null)
            return;
        if (row.Kind == McpOAuthTokenModel.RefreshKind)
            await RevokeGrantAsync(db, row.GrantId, cancellationToken);
        else
            await db.McpOAuthTokens.Where(t => t.Id == row.Id).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>The grant behind a live access token, or null. Notes the use at most once a minute.</summary>
    public async Task<McpAccessTokenIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!LooksLike(token, AccessPrefix))
            return null;

        var hash = McpAccessTokenService.Hash(token);
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var row = await db.McpOAuthTokens.AsNoTracking().Include(t => t.Grant)
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Kind == McpOAuthTokenModel.AccessKind, cancellationToken);
        if (row?.Grant is not { } grant || row.ExpiresAtUtc <= now
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(row.TokenHash), System.Text.Encoding.ASCII.GetBytes(hash)))
            return null;

        if (grant.LastUsedAtUtc is not { } last || now - last >= McpAccessTokenService.LastUsedResolution)
        {
            await db.McpOAuthGrants.Where(g => g.Id == grant.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(g => g.LastUsedAtUtc, now), cancellationToken);
        }

        return new McpAccessTokenIdentity(grant.Id, grant.ClientName, grant.Scope);
    }

    public async Task<IReadOnlyList<McpConnectedAppDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.McpOAuthGrants.AsNoTracking().ToListAsync(cancellationToken);
        return rows
            .Select(g => new McpConnectedAppDto(
                g.Id.ToString("D"), g.ClientName, McpConnectedAppKind.OAuthApp, g.Scope, g.CreatedAtUtc, g.LastUsedAtUtc))
            .ToList();
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return false;
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await RevokeGrantAsync(db, guid, cancellationToken);
    }

    public async Task RevokeGrantAsync(Guid grantId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await RevokeGrantAsync(db, grantId, cancellationToken);
    }

    private static async Task<bool> RevokeGrantAsync(NostosDbContext db, Guid grantId, CancellationToken cancellationToken)
    {
        // Tokens first: ExecuteDelete does not run EF's cascade, and SQLite
        // only cascades with foreign keys switched on.
        await db.McpOAuthTokens.Where(t => t.GrantId == grantId).ExecuteDeleteAsync(cancellationToken);
        return await db.McpOAuthGrants.Where(g => g.Id == grantId).ExecuteDeleteAsync(cancellationToken) > 0;
    }

    private static McpOAuthIssuedTokens AddTokens(NostosDbContext db, Guid grantId, string scope, DateTime now)
    {
        var access = AccessPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var refresh = RefreshPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        db.McpOAuthTokens.Add(new McpOAuthTokenModel
        {
            GrantId = grantId,
            Kind = McpOAuthTokenModel.AccessKind,
            TokenHash = McpAccessTokenService.Hash(access),
            ExpiresAtUtc = now + AccessLifetime,
        });
        db.McpOAuthTokens.Add(new McpOAuthTokenModel
        {
            GrantId = grantId,
            Kind = McpOAuthTokenModel.RefreshKind,
            TokenHash = McpAccessTokenService.Hash(refresh),
            ExpiresAtUtc = now + RefreshLifetime,
        });
        return new McpOAuthIssuedTokens(access, refresh, (int)AccessLifetime.TotalSeconds, OAuthScope(scope));
    }

    /// <summary>Same shape as an access token made in Settings, with its own prefix.</summary>
    public static bool LooksLike(string token, string prefix) =>
        token.Length == McpAccessTokenService.TokenLength
        && token.StartsWith(prefix, StringComparison.Ordinal)
        && McpAccessTokenService.LooksLikeToken(McpAccessTokenService.Prefix + token[prefix.Length..]);
}

/// <summary>
/// SelfHosted's record of connected apps: the access tokens the owner made
/// and the apps approved through OAuth, in one list (newest first). Revoking
/// finds either by id.
/// </summary>
public sealed class SelfHostedMcpConnectedApps(McpAccessTokenService tokens, McpOAuthService oauth) : IMcpConnectedApps
{
    public bool CanCreate => true;

    public async Task<IReadOnlyList<McpConnectedAppDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var all = (await tokens.ListAsync(cancellationToken)).Concat(await oauth.ListAsync(cancellationToken));
        return all.OrderByDescending(a => a.CreatedAt).ToList();
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken cancellationToken = default) =>
        await tokens.RevokeAsync(id, cancellationToken) || await oauth.RevokeAsync(id, cancellationToken);
}
