using Nostos.Backend.Integrations.Mcp;

namespace Nostos.Backend.Services.Mcp.OAuth;

/// <summary>
/// Where the links MCP tools return point on SelfHosted. A hosted app such as
/// claude.ai calls the public address, which answers only the MCP and OAuth
/// paths, so a link on that address would answer 404. Links use
/// <c>Mcp:AppBaseUrl</c> (the address the owner opens Nostos at, such as a
/// Tailscale or LAN address) when it is set, the request's own origin for a
/// request on the private network, and a bare path for a public request
/// with no app address set.
/// </summary>
public sealed class SelfHostedMcpAppOrigin(McpOAuthSettings settings, IConfiguration configuration) : IMcpAppOrigin
{
    public const string ConfigurationKey = "Mcp:AppBaseUrl";

    private readonly string? _appBase = Parse(configuration[ConfigurationKey]);

    public string? AppOrigin(HttpContext context)
    {
        if (_appBase is not null)
            return _appBase;
        if (McpPublicHost.IsPublicRequest(context, settings))
            return null;
        var request = context.Request;
        return $"{request.Scheme}://{request.Host}{request.PathBase}";
    }

    /// <summary>An absolute http(s) address without a trailing slash, or null.</summary>
    public static string? Parse(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;
}
