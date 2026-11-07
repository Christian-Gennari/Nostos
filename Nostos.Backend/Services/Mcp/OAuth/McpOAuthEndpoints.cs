using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Nostos.Backend.Integrations.Mcp;

namespace Nostos.Backend.Services.Mcp.OAuth;

/// <summary>Approving a request: <c>read</c> or <c>read_write</c>.</summary>
public sealed record McpOAuthApproveRequest(string? Scope);

/// <summary>Settings' view of OAuth: whether it is on, the connector URL, and the requests waiting.</summary>
public sealed record McpOAuthOwnerStatus(bool Enabled, string? ConnectorUrl, IReadOnlyList<McpOAuthPendingRequestDto> Requests);

/// <summary>
/// The OAuth 2.1 authorization server for <c>/mcp</c> on SelfHosted (issue
/// #1470), hand-written and small: metadata (RFC 9728, RFC 8414), authorize
/// with PKCE S256 and owner approval, token (authorization code and rotating
/// refresh tokens), revoke (RFC 7009). Clients identify themselves only by a
/// client metadata document URL (CIMD); there is no dynamic registration.
///
/// The public routes are mapped only when <c>Mcp:PublicBaseUrl</c> is set.
/// The owner's routes (<see cref="McpOAuthPaths.OwnerApi"/>) are always
/// mapped, answer 404 on the public host, and report whether OAuth is on.
/// </summary>
public static class McpOAuthEndpoints
{
    public static IEndpointRouteBuilder MapMcpOAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var settings = routes.ServiceProvider.GetRequiredService<McpOAuthSettings>();
        MapOwnerEndpoints(routes, settings);
        if (!settings.Enabled)
            return routes;

        var protectedResource = new
        {
            resource = settings.Resource,
            authorization_servers = new[] { settings.Issuer },
            scopes_supported = new[] { NostosMcp.ReadScope, NostosMcp.WriteScope },
            bearer_methods_supported = new[] { "header" },
            resource_name = "Nostos",
        };
        routes.MapGet(McpOAuthPaths.ProtectedResource, () => Results.Json(protectedResource)).AllowAnonymous();
        routes.MapGet(McpOAuthPaths.ProtectedResourceMcp, () => Results.Json(protectedResource)).AllowAnonymous();

        var authorizationServer = new
        {
            issuer = settings.Issuer,
            authorization_endpoint = settings.AuthorizationEndpoint,
            token_endpoint = settings.TokenEndpoint,
            revocation_endpoint = settings.RevocationEndpoint,
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" },
            revocation_endpoint_auth_methods_supported = new[] { "none" },
            scopes_supported = new[] { NostosMcp.ReadScope, NostosMcp.WriteScope },
            client_id_metadata_document_supported = true,
            authorization_response_iss_parameter_supported = true,
        };
        routes.MapGet(McpOAuthPaths.AuthorizationServer, () => Results.Json(authorizationServer)).AllowAnonymous();

        routes.MapGet(McpOAuthPaths.Authorize, AuthorizeAsync).AllowAnonymous();
        routes.MapGet(McpOAuthPaths.AuthorizeStatus, Status).AllowAnonymous();
        routes.MapPost(McpOAuthPaths.Token, TokenAsync).AllowAnonymous().DisableAntiforgery();
        routes.MapPost(McpOAuthPaths.Revoke, RevokeAsync).AllowAnonymous().DisableAntiforgery();
        return routes;
    }

    private static void MapOwnerEndpoints(IEndpointRouteBuilder routes, McpOAuthSettings settings)
    {
        var owner = routes.MapGroup(McpOAuthPaths.OwnerApi);
        // Defence in depth: the public-host allowlist already answers 404
        // here; this answers 404 even if that middleware were missing.
        owner.AddEndpointFilter(async (context, next) =>
            McpPublicHost.IsPublicRequest(context.HttpContext, settings)
                ? Results.NotFound()
                : await next(context));
        // Approve and deny give an outside app access, so they must come from
        // Settings itself: JSON (a cross-site form or bodiless fetch cannot
        // send it without a CORS preflight, which Nostos never grants), from
        // this site, and from the owner's own network.
        owner.AddEndpointFilter(async (context, next) =>
            HttpMethods.IsGet(context.HttpContext.Request.Method) || IsOwnerDecision(context.HttpContext)
                ? await next(context)
                : Results.Problem(
                    "Approve and deny connection requests in Settings, on your own network.",
                    statusCode: StatusCodes.Status403Forbidden));

        owner.MapGet("/", (McpOAuthRequests requests) => Results.Ok(new McpOAuthOwnerStatus(
            settings.Enabled,
            settings.Enabled ? settings.Resource : null,
            settings.Enabled ? requests.ListPending() : [])));

        owner.MapPost("/requests/{id}/approve", (string id, McpOAuthApproveRequest body, McpOAuthRequests requests) =>
        {
            var scope = body.Scope ?? McpConnectedAppScope.Read;
            if (scope is not (McpConnectedAppScope.Read or McpConnectedAppScope.ReadWrite))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["scope"] = ["Scope must be 'read' or 'read_write'."],
                });
            }

            return settings.Enabled && requests.Approve(id, scope) ? Results.NoContent() : Results.NotFound();
        });

        owner.MapPost("/requests/{id}/deny", (string id, McpOAuthRequests requests) =>
            settings.Enabled && requests.Deny(id) ? Results.NoContent() : Results.NotFound());
    }

    /// <summary>
    /// An approve or deny the owner sent from Settings: a JSON body, no sign of
    /// another site, and, when a proxy forwarded it, a caller on a private,
    /// loopback or Tailscale address. The last rule holds even when the proxy
    /// rewrites the host, so the public-host check cannot see the request came
    /// from outside. A direct connection is not checked by address: a LAN can
    /// use global IPv6 addresses, and an open Kestrel port exposes the whole
    /// app anyway.
    /// </summary>
    public static bool IsOwnerDecision(HttpContext http)
    {
        var request = http.Request;
        if (!request.HasJsonContentType())
            return false;

        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0)
        {
            if (fetchSite is not ("same-origin" or "none"))
                return false;
        }
        else if (request.Headers.Origin.ToString() is { Length: > 0 } origin
            && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                || !string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // UseForwardedHeaders moves the proxy's own address to X-Original-For
        // when it applies X-Forwarded-For; a proxy it does not trust leaves
        // X-Forwarded-For in place. Either way the request was forwarded.
        var forwardedFor = request.Headers["X-Forwarded-For"];
        var proxied = request.Headers.ContainsKey("X-Original-For") || forwardedFor.Count > 0;
        if (!proxied)
            return true;

        if (http.Connection.RemoteIpAddress is not { } remote || McpNetworkAddressFilter.IsPublic(remote))
            return false;
        foreach (var value in forwardedFor)
        {
            foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!System.Net.IPEndPoint.TryParse(part, out var endpoint) || McpNetworkAddressFilter.IsPublic(endpoint.Address))
                    return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------
    // GET /oauth/authorize

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http,
        McpOAuthSettings settings,
        McpOAuthRateLimiter limiter,
        IMcpClientMetadataFetcher fetcher,
        McpOAuthRequests requests,
        CancellationToken ct)
    {
        if (!limiter.TryAcquire(McpOAuthRateLimiter.Endpoint.Authorize, http))
            return TooMany();

        var query = http.Request.Query;
        string? Single(string key) => query[key].Count == 1 ? query[key][0] : null;

        // Until the client and its redirect URI check out, errors are shown
        // here and never sent anywhere (RFC 6749 §4.1.2.1).
        var clientId = Single("client_id");
        if (McpClientMetadataFetcher.ValidateClientId(clientId) is { } clientProblem)
            return McpOAuthPages.Error(clientProblem);
        var fetched = await fetcher.FetchAsync(clientId!, ct);
        if (fetched.Metadata is not { } client)
            return McpOAuthPages.Error(fetched.Error ?? "The app's details could not be loaded.");

        var redirectUri = Single("redirect_uri");
        if (redirectUri is null || !client.AllowsRedirect(redirectUri))
            return McpOAuthPages.Error("The app asked to return to an address its details do not list.");

        var state = Single("state");
        IResult Fail(string error, string description) =>
            Results.Redirect(McpOAuthRequests.ErrorRedirect(redirectUri, error, description, state, settings.Issuer));

        if (Single("response_type") != "code")
            return Fail("unsupported_response_type", "Only response_type=code is supported.");
        var challenge = Single("code_challenge");
        if (Single("code_challenge_method") != "S256" || challenge is null || !IsS256Challenge(challenge))
            return Fail("invalid_request", "PKCE with code_challenge_method=S256 is required.");
        if (query["resource"].Count > 1 || (Single("resource") is { } resource && !SameResource(resource, settings)))
            return Fail("invalid_target", $"The only resource here is {settings.Resource}.");
        if (ParseScope(Single("scope")) is not { } requestedScope)
            return Fail("invalid_scope", $"Ask for {NostosMcp.ReadScope} and, to save to the library, {NostosMcp.WriteScope}.");

        var created = requests.Create(client, redirectUri, state, challenge, requestedScope, settings.Issuer,
            http.Connection.RemoteIpAddress?.ToString() ?? "-");
        if (created is not { } pending)
            return Fail("temporarily_unavailable", "Too many connection requests are waiting. Try again in a few minutes.");

        return McpOAuthPages.Waiting(client.ClientName, client.ClientHost, pending.UserCode, pending.Id);
    }

    private static IResult Status(HttpContext http, McpOAuthRateLimiter limiter, McpOAuthRequests requests)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!limiter.TryAcquire(McpOAuthRateLimiter.Endpoint.Status, http))
            return TooMany();
        var id = http.Request.Query["request"].ToString();
        return requests.Poll(id) switch
        {
            McpOAuthPollResult.Done done => Results.Json(new { status = "done", redirect = done.RedirectUrl }),
            McpOAuthPollResult.Pending => Results.Json(new { status = "pending" }),
            _ => Results.Json(new { status = "unknown" }, statusCode: StatusCodes.Status404NotFound),
        };
    }

    // ------------------------------------------------------------------
    // POST /oauth/token

    private static async Task<IResult> TokenAsync(
        HttpContext http,
        McpOAuthSettings settings,
        McpOAuthRateLimiter limiter,
        McpOAuthRequests requests,
        McpOAuthService oauth,
        CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        if (!limiter.TryAcquire(McpOAuthRateLimiter.Endpoint.Token, http))
            return TooMany();
        if (!http.Request.HasFormContentType)
            return OAuthError("invalid_request", "Send the request as application/x-www-form-urlencoded.");

        var form = await http.Request.ReadFormAsync(ct);
        string? Single(string key) => form[key].Count == 1 ? form[key][0] : null;
        if (form["resource"].Count > 1 || (Single("resource") is { } resource && !SameResource(resource, settings)))
            return OAuthError("invalid_target", $"The only resource here is {settings.Resource}.");

        switch (Single("grant_type"))
        {
            case "authorization_code":
            {
                var codeValue = Single("code");
                if (codeValue is null)
                    return OAuthError("invalid_request", "code is required.");
                var clientId = Single("client_id");
                var redirect = Single("redirect_uri");
                var verifier = Single("code_verifier");
                // Checked before the code is spent, so a wrong guess cannot
                // burn it for the real client.
                var code = requests.Redeem(codeValue, issued =>
                        string.Equals(clientId, issued.ClientId, StringComparison.Ordinal)
                        && string.Equals(redirect, issued.RedirectUri, StringComparison.Ordinal)
                        && verifier is not null && VerifyPkce(verifier, issued.CodeChallenge),
                    out var grantId, out var spentGrant);
                if (code is null)
                {
                    // A code used twice: revoke what the first use made.
                    if (spentGrant is { } spent)
                        await oauth.RevokeGrantAsync(spent, ct);
                    return OAuthError("invalid_grant",
                        "The authorization code is unknown, used or expired, or was issued for another client, redirect_uri or code_verifier.");
                }

                var tokens = await oauth.CreateGrantAsync(code, grantId, ct);
                if (tokens is null)
                    return OAuthError("invalid_grant", $"This server already has {McpOAuthService.MaxGrants} connected apps. Revoke one in Settings first.");
                if (requests.FinishRedeem(codeValue))
                {
                    // Someone else tried the code while the grant was written.
                    await oauth.RevokeGrantAsync(grantId, ct);
                    return OAuthError("invalid_grant", "The authorization code was used twice, so the connection was revoked.");
                }

                return TokenResponse(tokens);
            }

            case "refresh_token":
            {
                var refresh = Single("refresh_token");
                if (refresh is null)
                    return OAuthError("invalid_request", "refresh_token is required.");
                var (outcome, tokens) = await oauth.RefreshAsync(refresh, Single("client_id"), ct);
                return outcome == McpOAuthRefreshOutcome.Issued && tokens is not null
                    ? TokenResponse(tokens)
                    : OAuthError("invalid_grant", outcome == McpOAuthRefreshOutcome.Reused
                        ? "The refresh token was already used, so this connection was revoked. Connect the app again."
                        : "The refresh token is unknown, revoked or expired.");
            }

            case null:
                return OAuthError("invalid_request", "grant_type is required.");
            default:
                return OAuthError("unsupported_grant_type", "Use authorization_code or refresh_token.");
        }
    }

    private static async Task<IResult> RevokeAsync(
        HttpContext http, McpOAuthRateLimiter limiter, McpOAuthService oauth, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!limiter.TryAcquire(McpOAuthRateLimiter.Endpoint.Revoke, http))
            return TooMany();
        if (!http.Request.HasFormContentType)
            return OAuthError("invalid_request", "Send the request as application/x-www-form-urlencoded.");
        var form = await http.Request.ReadFormAsync(ct);
        if (form["token"].Count != 1)
            return OAuthError("invalid_request", "token is required.");
        await oauth.RevokeTokenAsync(form["token"][0]!, ct);
        return Results.Ok();
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The scope asked for, as Settings names it: <c>read_write</c> when
    /// <c>nostos:write</c> is asked for or no scope is given, <c>read</c> for
    /// <c>nostos:read</c> alone. Other scopes are ignored; a scope with
    /// neither Nostos scope is an error (null).
    /// </summary>
    public static string? ParseScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return McpConnectedAppScope.ReadWrite;
        var parts = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Contains(NostosMcp.WriteScope, StringComparer.Ordinal))
            return McpConnectedAppScope.ReadWrite;
        return parts.Contains(NostosMcp.ReadScope, StringComparer.Ordinal) ? McpConnectedAppScope.Read : null;
    }

    public static bool VerifyPkce(string verifier, string challenge)
    {
        if (verifier.Length is < 43 or > 128 || verifier.AsSpan().IndexOfAnyExcept(Unreserved) >= 0)
            return false;
        var computed = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    private static bool IsS256Challenge(string challenge) =>
        challenge.Length == 43 && challenge.AsSpan().IndexOfAnyExcept(Base64UrlChars) < 0;

    private static bool SameResource(string resource, McpOAuthSettings settings) =>
        string.Equals(resource.TrimEnd('/'), settings.Resource, StringComparison.Ordinal);

    private static IResult TokenResponse(McpOAuthIssuedTokens tokens) => Results.Json(new
    {
        access_token = tokens.AccessToken,
        token_type = "Bearer",
        expires_in = tokens.ExpiresIn,
        refresh_token = tokens.RefreshToken,
        scope = tokens.Scope,
    });

    private static IResult OAuthError(string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult TooMany() =>
        Results.Json(new { error = "slow_down", error_description = "Too many requests. Wait a minute." },
            statusCode: StatusCodes.Status429TooManyRequests);

    private static readonly System.Buffers.SearchValues<char> Base64UrlChars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private static readonly System.Buffers.SearchValues<char> Unreserved =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~");
}

/// <summary>SelfHosted wiring for OAuth.</summary>
public static class McpOAuthRegistration
{
    public static IServiceCollection AddSelfHostedMcpOAuth(this IServiceCollection services)
    {
        services.AddSingleton(sp => McpOAuthSettings.From(
            sp.GetRequiredService<IConfiguration>()[McpOAuthSettings.ConfigurationKey]));
        services.AddSingleton<McpOAuthService>();
        services.AddSingleton<McpOAuthRequests>();
        services.AddSingleton<McpOAuthRateLimiter>();
        services.AddSingleton<IMcpClientMetadataFetcher>(_ => new McpClientMetadataFetcher());
        services.AddSingleton<IMcpAppOrigin, SelfHostedMcpAppOrigin>();
        return services;
    }
}
