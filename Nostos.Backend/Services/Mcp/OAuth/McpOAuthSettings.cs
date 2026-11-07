namespace Nostos.Backend.Services.Mcp.OAuth;

/// <summary>
/// SelfHosted OAuth for hosted AI apps (issue #1470), from
/// <c>Mcp:PublicBaseUrl</c> (environment <c>Mcp__PublicBaseUrl</c>): the
/// public HTTPS address a tunnel or reverse proxy gives this server, such as
/// <c>https://nostos.example.ts.net</c>.
///
/// OAuth is off unless it is an absolute https URL with no path, query or
/// fragment. It is the issuer and the base of every URL in the metadata, and
/// <c>{PublicBaseUrl}/mcp</c> is the protected resource. Requests that arrive
/// on its host are the public ones: only the OAuth and MCP paths answer them
/// (<see cref="McpPublicHost"/>).
/// </summary>
public sealed class McpOAuthSettings
{
    public const string ConfigurationKey = "Mcp:PublicBaseUrl";

    private McpOAuthSettings(bool enabled, string issuer, string host, int port, string? problem)
    {
        Enabled = enabled;
        Issuer = issuer;
        Host = host;
        Port = port;
        Problem = problem;
    }

    public bool Enabled { get; }

    /// <summary>The public base URL without a trailing slash; empty when off.</summary>
    public string Issuer { get; }

    /// <summary>The public host, lowercase and without a port; empty when off.</summary>
    public string Host { get; }

    /// <summary>
    /// The public port (443 unless the URL names one). The same name on
    /// another port is not the public address: Tailscale can serve the app
    /// privately on <c>https://node.ts.net:8443</c> while Funnel publishes 443.
    /// </summary>
    public int Port { get; }

    /// <summary>Why a configured value was not used, for the startup log; null otherwise.</summary>
    public string? Problem { get; }

    /// <summary>The protected resource (RFC 9728 <c>resource</c>).</summary>
    public string Resource => Issuer + "/mcp";

    public string ProtectedResourceMetadataUrl => Issuer + "/.well-known/oauth-protected-resource/mcp";

    public string AuthorizationEndpoint => Issuer + McpOAuthPaths.Authorize;

    public string TokenEndpoint => Issuer + McpOAuthPaths.Token;

    public string RevocationEndpoint => Issuer + McpOAuthPaths.Revoke;

    public static McpOAuthSettings Disabled { get; } = new(false, string.Empty, string.Empty, 0, null);

    public static McpOAuthSettings From(string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
            return Disabled;

        var trimmed = publicBaseUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return new McpOAuthSettings(false, string.Empty, string.Empty, 0,
                $"{ConfigurationKey} must be an https address with no path, such as https://nostos.example.ts.net. OAuth for hosted AI apps stays off.");
        }

        var issuer = uri.GetLeftPart(UriPartial.Authority);
        return new McpOAuthSettings(true, issuer, uri.IdnHost.ToLowerInvariant(), uri.Port, null);
    }
}

/// <summary>The routes of the OAuth server and its metadata.</summary>
public static class McpOAuthPaths
{
    public const string Authorize = "/oauth/authorize";
    public const string AuthorizeStatus = "/oauth/authorize/status";
    public const string Token = "/oauth/token";
    public const string Revoke = "/oauth/revoke";
    public const string ProtectedResource = "/.well-known/oauth-protected-resource";
    public const string ProtectedResourceMcp = "/.well-known/oauth-protected-resource/mcp";
    public const string AuthorizationServer = "/.well-known/oauth-authorization-server";

    /// <summary>The owner's side, on the private network only: list, approve, deny.</summary>
    public const string OwnerApi = "/api/mcp/oauth";
}
