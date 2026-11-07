using System.Buffers.Text;
using System.Security.Cryptography;
using Nostos.Backend.Integrations.Mcp;

namespace Nostos.Backend.Services.Mcp.OAuth;

/// <summary>A connection request waiting for the owner, as Settings shows it.</summary>
/// <param name="Code">The short code the app's sign-in page shows, so the owner approves the right request.</param>
/// <param name="RequestedScope"><see cref="McpConnectedAppScope"/>.</param>
public sealed record McpOAuthPendingRequestDto(
    string Id,
    string Code,
    string ClientName,
    string ClientId,
    string ClientHost,
    string RedirectHost,
    string RequestedScope,
    DateTime CreatedAt,
    DateTime ExpiresAt);

/// <summary>What the waiting page learns when it asks.</summary>
public abstract record McpOAuthPollResult
{
    public sealed record Pending : McpOAuthPollResult;

    /// <summary>Approved, denied or expired: send the browser here.</summary>
    public sealed record Done(string RedirectUrl) : McpOAuthPollResult;

    public sealed record Unknown : McpOAuthPollResult;
}

/// <summary>An authorization code being redeemed.</summary>
public sealed record McpOAuthCodeGrant(
    string ClientId,
    string ClientName,
    string RedirectUri,
    string CodeChallenge,
    string Scope);

/// <summary>
/// The approval half of the authorization flow (issue #1470), in memory: a
/// pending request lives 10 minutes and an authorization code 60 seconds, so a
/// restart only means the app asks again.
///
/// The public <c>/oauth/authorize</c> has no user, so it never approves
/// anything itself: it creates a pending request with a four-letter code and
/// shows the code. The owner approves or denies the request in Settings on
/// the private network. The waiting page polls
/// <see cref="Poll"/>; once the request is decided, the poll turns it into a
/// redirect back to the app, with a single-use code when it was approved.
/// </summary>
public sealed class McpOAuthRequests(TimeProvider? clock = null)
{
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>At most this many requests wait at once, so nobody can bury the real one.</summary>
    public const int MaxPending = 20;

    /// <summary>Consonants only: no words, no 0/O or 1/I confusion.</summary>
    private const string CodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (McpOAuthCodeGrant Grant, DateTimeOffset Until)> _codes = new(StringComparer.Ordinal);

    /// <summary>Codes already redeemed, until they would have expired, with the grant they made.</summary>
    private readonly Dictionary<string, (Guid GrantId, DateTimeOffset Until)> _spentCodes = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public required string Id;
        public required string UserCode;
        public required McpClientMetadata Client;
        public required string RedirectUri;
        public required string? State;
        public required string CodeChallenge;
        public required string RequestedScope;
        public required string Issuer;
        public required DateTimeOffset CreatedAt;
        public required DateTimeOffset ExpiresAt;
        public string? ApprovedScope;
        public bool Denied;
    }

    /// <summary>Creates a pending request; null when <see cref="MaxPending"/> already wait.</summary>
    public (string Id, string UserCode)? Create(
        McpClientMetadata client,
        string redirectUri,
        string? state,
        string codeChallenge,
        string requestedScope,
        string issuer)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            if (_requests.Count >= MaxPending)
                return null;

            string userCode;
            do
            {
                userCode = string.Create(4, 0, (span, _) =>
                {
                    for (var i = 0; i < span.Length; i++)
                        span[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
                });
            }
            while (_requests.Values.Any(r => r.UserCode == userCode));

            var entry = new Entry
            {
                Id = RandomToken(),
                UserCode = userCode,
                Client = client,
                RedirectUri = redirectUri,
                State = state,
                CodeChallenge = codeChallenge,
                RequestedScope = requestedScope,
                Issuer = issuer,
                CreatedAt = now,
                ExpiresAt = now + RequestLifetime,
            };
            _requests[entry.Id] = entry;
            return (entry.Id, userCode);
        }
    }

    public IReadOnlyList<McpOAuthPendingRequestDto> ListPending()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            return _requests.Values
                .Where(r => r.ApprovedScope is null && !r.Denied && r.ExpiresAt > now)
                .OrderBy(r => r.CreatedAt)
                .Select(r => new McpOAuthPendingRequestDto(
                    r.Id,
                    r.UserCode,
                    r.Client.ClientName,
                    r.Client.ClientId,
                    r.Client.ClientHost,
                    new Uri(r.RedirectUri).Host,
                    r.RequestedScope,
                    r.CreatedAt.UtcDateTime,
                    r.ExpiresAt.UtcDateTime))
                .ToList();
        }
    }

    /// <summary>Approves with <paramref name="scope"/> (<see cref="McpConnectedAppScope"/>); false when there is no such undecided request.</summary>
    public bool Approve(string id, string scope)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Prune(now);
            if (!_requests.TryGetValue(id, out var entry) || entry.ApprovedScope is not null || entry.Denied || entry.ExpiresAt <= now)
                return false;
            entry.ApprovedScope = scope;
            return true;
        }
    }

    public bool Deny(string id)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Prune(now);
            if (!_requests.TryGetValue(id, out var entry) || entry.ApprovedScope is not null || entry.Denied || entry.ExpiresAt <= now)
                return false;
            entry.Denied = true;
            return true;
        }
    }

    /// <summary>
    /// What the waiting page should do. A decided request is used up by the
    /// poll that sees it: the code is issued once, and the request is gone.
    /// </summary>
    public McpOAuthPollResult Poll(string id)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var entry))
            {
                Prune(now);
                return new McpOAuthPollResult.Unknown();
            }

            if (entry.ExpiresAt <= now || entry.Denied)
            {
                _requests.Remove(id);
                return new McpOAuthPollResult.Done(ErrorRedirect(entry.RedirectUri, "access_denied",
                    entry.Denied ? "The owner denied the request." : "The request was not approved in time.",
                    entry.State, entry.Issuer));
            }

            if (entry.ApprovedScope is not { } scope)
                return new McpOAuthPollResult.Pending();

            _requests.Remove(id);
            var code = RandomToken();
            _codes[code] = (new McpOAuthCodeGrant(entry.Client.ClientId, entry.Client.ClientName, entry.RedirectUri, entry.CodeChallenge, scope), now + CodeLifetime);
            return new McpOAuthPollResult.Done(Redirect(entry.RedirectUri, [("code", code), ("state", entry.State), ("iss", entry.Issuer)]));
        }
    }

    /// <summary>
    /// Takes a code: it works once. A second try returns null and, through
    /// <paramref name="spentGrant"/>, the grant the first try made, so the
    /// caller can revoke it (RFC 6749 §4.1.2).
    /// </summary>
    public McpOAuthCodeGrant? Redeem(string code, out Guid? spentGrant)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            spentGrant = null;
            if (_spentCodes.TryGetValue(code, out var spent) && spent.Until > now)
            {
                spentGrant = spent.GrantId == Guid.Empty ? null : spent.GrantId;
                return null;
            }

            if (!_codes.Remove(code, out var entry) || entry.Until <= now)
                return null;
            _spentCodes[code] = (Guid.Empty, entry.Until);
            return entry.Grant;
        }
    }

    /// <summary>Records which grant a redeemed code made.</summary>
    public void RecordGrant(string code, Guid grantId)
    {
        lock (_gate)
        {
            if (_spentCodes.TryGetValue(code, out var spent))
                _spentCodes[code] = (grantId, spent.Until);
        }
    }

    public static string ErrorRedirect(string redirectUri, string error, string? description, string? state, string issuer) =>
        Redirect(redirectUri, [("error", error), ("error_description", description), ("state", state), ("iss", issuer)]);

    private static string Redirect(string redirectUri, (string Key, string? Value)[] parameters)
    {
        var query = string.Join('&', parameters
            .Where(p => p.Value is not null)
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}"));
        var fragmentAt = redirectUri.IndexOf('#');
        var target = fragmentAt >= 0 ? redirectUri[..fragmentAt] : redirectUri;
        return target + (target.Contains('?') ? "&" : "?") + query;
    }

    private void Prune(DateTimeOffset now)
    {
        // A decided request waits for its poll only until it would have expired.
        foreach (var expired in _requests.Where(r => r.Value.ExpiresAt + TimeSpan.FromMinutes(1) <= now).Select(r => r.Key).ToList())
            _requests.Remove(expired);
        foreach (var expired in _codes.Where(c => c.Value.Until <= now).Select(c => c.Key).ToList())
            _codes.Remove(expired);
        foreach (var expired in _spentCodes.Where(c => c.Value.Until <= now).Select(c => c.Key).ToList())
            _spentCodes.Remove(expired);
    }

    private static string RandomToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}
