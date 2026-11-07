using Nostos.Backend.Integrations.Mcp;
using Microsoft.Extensions.Primitives;

namespace Nostos.Backend.Services.Mcp.OAuth;

/// <summary>
/// The public-host allowlist (issue #1470), the backstop behind the tunnel's
/// own path rules: a request that came in through the public address answers
/// only on <c>/mcp</c>, the OAuth endpoints and their <c>/.well-known</c>
/// metadata. The app, <c>/api</c>, <c>/opds</c>, the health checks and the
/// owner's approve routes answer 404 there, whatever the tunnel forwards.
///
/// A request is public when its host and port, or a forwarded host a local
/// proxy put on it, are the <c>Mcp:PublicBaseUrl</c> ones (a host without a
/// port counts as 443), or when Tailscale says it
/// came through Funnel (<c>Tailscale-Funnel-Request</c>) — that one applies
/// even while OAuth is off, so a Funnel that forwards everything still shows
/// nothing but <c>/mcp</c>.
/// </summary>
public static class McpPublicHost
{
    /// <summary>The header tailscaled adds to every request that came through Funnel.</summary>
    public const string TailscaleFunnelHeader = "Tailscale-Funnel-Request";

    private static readonly string[] AllowedExact =
    [
        McpOAuthPaths.Authorize,
        McpOAuthPaths.AuthorizeStatus,
        McpOAuthPaths.Token,
        McpOAuthPaths.Revoke,
        McpOAuthPaths.ProtectedResource,
        McpOAuthPaths.ProtectedResourceMcp,
        McpOAuthPaths.AuthorizationServer,
    ];

    public static bool IsPublicRequest(HttpContext context, McpOAuthSettings settings)
    {
        var headers = context.Request.Headers;
        if (headers.ContainsKey(TailscaleFunnelHeader))
            return true;
        if (!settings.Enabled)
            return false;

        return Matches(context.Request.Host.Value, settings)
            || headers["X-Original-Host"].Any(h => Matches(h, settings))
            || AnyForwardedHost(headers["X-Forwarded-Host"], settings);
    }

    public static bool IsAllowedPublicPath(PathString path)
    {
        if (path.StartsWithSegments(NostosMcp.Path, StringComparison.Ordinal, out var rest)
            && (!rest.HasValue || rest.Value == "/"))
            return true;
        var value = path.Value ?? string.Empty;
        foreach (var allowed in AllowedExact)
        {
            if (string.Equals(value, allowed, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Answers 404 for everything outside the allowlist on the public host.</summary>
    public static IApplicationBuilder UseMcpPublicHostAllowlist(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<McpOAuthSettings>();
        return app.Use(async (context, next) =>
        {
            if (IsPublicRequest(context, settings) && !IsAllowedPublicPath(context.Request.Path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.Headers.CacheControl = "no-store";
                return;
            }

            await next(context);
        });
    }

    private static bool AnyForwardedHost(StringValues values, McpOAuthSettings settings)
    {
        foreach (var value in values)
        {
            if (value is null)
                continue;
            foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (Matches(part, settings))
                    return true;
            }
        }

        return false;
    }

    private static bool Matches(string? hostHeader, McpOAuthSettings settings)
    {
        if (string.IsNullOrEmpty(hostHeader))
            return false;
        var parsed = HostString.FromUriComponent(hostHeader);
        return string.Equals(parsed.Host.TrimEnd('.'), settings.Host, StringComparison.OrdinalIgnoreCase)
            && (parsed.Port ?? 443) == settings.Port;
    }
}
