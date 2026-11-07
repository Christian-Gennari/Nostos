using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Services.Mcp.OAuth;

namespace Nostos.Backend.Services.Mcp;

/// <summary>
/// Global bound on token checks, the same guard the Cloud OPDS credentials
/// have: generous for a busy agent, but no unbounded stream of lookups.
/// </summary>
public sealed class McpAccessTokenAttemptLimiter : IDisposable
{
    private readonly FixedWindowRateLimiter _limiter = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 600,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    });

    public bool TryAcquire()
    {
        using var lease = _limiter.AttemptAcquire(1);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Throttles FAILED token attempts per client address, like the OPDS
/// credential throttle: after <see cref="MaxFailures"/> failures inside
/// <see cref="Window"/> the address is refused until the window ends. A
/// token that works never counts. State is in memory.
/// </summary>
public sealed class McpAccessTokenFailedAttemptThrottle(TimeProvider? clock = null)
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private const int MaxTrackedKeys = 50_000;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public DateTimeOffset WindowStart;
        public int Failures;
    }

    /// <summary>The time to wait when the address is blocked, otherwise null.</summary>
    public TimeSpan? RetryAfter(string? ip)
    {
        if (!_entries.TryGetValue(Key(ip), out var entry))
            return null;
        lock (entry)
        {
            var remaining = entry.WindowStart + Window - _clock.GetUtcNow();
            return remaining > TimeSpan.Zero && entry.Failures >= MaxFailures ? remaining : null;
        }
    }

    public void RecordFailure(string? ip)
    {
        var now = _clock.GetUtcNow();
        if (_entries.Count > MaxTrackedKeys)
        {
            foreach (var pair in _entries)
            {
                if (now - pair.Value.WindowStart >= Window)
                    _entries.TryRemove(pair.Key, out _);
            }
        }

        var entry = _entries.GetOrAdd(Key(ip), _ => new Entry { WindowStart = now });
        lock (entry)
        {
            if (now - entry.WindowStart >= Window)
            {
                entry.WindowStart = now;
                entry.Failures = 0;
            }

            entry.Failures++;
        }
    }

    private static string Key(string? ip) => string.IsNullOrEmpty(ip) ? "-" : ip;
}

/// <summary>
/// <c>Authorization: Bearer nst_…</c> for <c>/mcp</c> on SelfHosted (issue
/// #1469). Success carries the token's id and name, and its scopes in the
/// <see cref="NostosMcp.ScopeClaimType"/> claim the MCP tools check. Failure
/// messages never contain the token: ASP.NET logs them.
/// </summary>
public sealed class McpAccessTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    McpAccessTokenService tokens,
    McpOAuthService oauth,
    McpOAuthSettings oauthSettings,
    McpAccessTokenAttemptLimiter attemptLimiter,
    McpAccessTokenFailedAttemptThrottle failedThrottle)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "NostosAccessToken";

    private const string RateLimitedItem = "nostos:mcp-auth-rate-limited";
    private const string RetryAfterItem = "nostos:mcp-auth-retry-after";
    private const string InvalidTokenItem = "nostos:mcp-auth-invalid-token";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization.ToString(), out var authorization)
            || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var ip = Context.Connection.RemoteIpAddress?.ToString();
        if (failedThrottle.RetryAfter(ip) is { } wait)
        {
            Context.Items[RateLimitedItem] = true;
            Context.Items[RetryAfterItem] = (int)Math.Ceiling(wait.TotalSeconds);
            return AuthenticateResult.Fail("Too many failed access token attempts.");
        }

        if (!attemptLimiter.TryAcquire())
        {
            Context.Items[RateLimitedItem] = true;
            return AuthenticateResult.Fail("Access token check rate limit exceeded.");
        }

        // nst_ is a token the owner made; nsa_ an OAuth access token (#1470).
        var presented = authorization.Parameter ?? string.Empty;
        var identity = presented.StartsWith(McpOAuthService.AccessPrefix, StringComparison.Ordinal)
            ? await oauth.AuthenticateAsync(presented, Context.RequestAborted)
            : await tokens.AuthenticateAsync(presented, Context.RequestAborted);
        if (identity is null)
        {
            failedThrottle.RecordFailure(ip);
            Context.Items[InvalidTokenItem] = true;
            return AuthenticateResult.Fail("Invalid access token.");
        }

        var scopes = identity.Scope == McpAccessTokenModel.ReadWriteScope
            ? $"{NostosMcp.ReadScope} {NostosMcp.WriteScope}"
            : NostosMcp.ReadScope;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, identity.Id.ToString("D")),
                new Claim(ClaimTypes.Name, identity.Name),
                new Claim(NostosMcp.ScopeClaimType, scopes),
            ],
            Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.ContainsKey(RateLimitedItem))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            var seconds = Context.Items[RetryAfterItem] as int? ?? 60;
            Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = Challenge(oauthSettings, Context.Items.ContainsKey(InvalidTokenItem));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The 401 challenge. With OAuth on (#1470) it points the app at the
    /// protected resource metadata and the scopes to ask for (RFC 9728 §5.1),
    /// which is how claude.ai and ChatGPT find the authorization server.
    /// </summary>
    public static string Challenge(McpOAuthSettings settings, bool invalidToken)
    {
        var challenge = "Bearer realm=\"Nostos\"";
        if (invalidToken)
            challenge += ", error=\"invalid_token\"";
        if (settings.Enabled)
            challenge += $", resource_metadata=\"{settings.ProtectedResourceMetadataUrl}\", scope=\"{NostosMcp.ReadScope} {NostosMcp.WriteScope}\"";
        return challenge;
    }
}

/// <summary>SelfHosted wiring for MCP access tokens.</summary>
public static class McpAccessTokenRegistration
{
    /// <summary>
    /// Registers the token store and the Bearer scheme, and the
    /// <see cref="NostosMcp.AuthorizationPolicy"/> that switches <c>/mcp</c>
    /// on. The scheme is not the default, so the rest of the API keeps its
    /// own access model.
    /// </summary>
    public static IServiceCollection AddSelfHostedMcpAccessTokens(this IServiceCollection services)
    {
        services.AddSingleton<McpAccessTokenService>();
        // One list in Settings: tokens the owner made and apps approved over OAuth.
        services.AddSelfHostedMcpOAuth();
        services.AddSingleton<SelfHostedMcpConnectedApps>();
        services.AddSingleton<IMcpConnectedApps>(sp => sp.GetRequiredService<SelfHostedMcpConnectedApps>());
        services.AddSingleton<McpAccessTokenAttemptLimiter>();
        services.AddSingleton<McpAccessTokenFailedAttemptThrottle>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, McpAccessTokenAuthenticationHandler>(
                McpAccessTokenAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(options => options.AddPolicy(
            NostosMcp.AuthorizationPolicy,
            policy => policy
                .AddAuthenticationSchemes(McpAccessTokenAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()));
        return services;
    }
}
