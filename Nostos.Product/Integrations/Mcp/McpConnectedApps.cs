using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// The apps connected to this library over MCP, as Settings → Connected apps
/// lists them (issue #1469). Each host plugs in its own record: SelfHosted the
/// access tokens the owner made, Cloud (#557) the OAuth clients seen on
/// <c>/mcp</c>. When a host registers one, the product maps
/// <see cref="McpConnectedAppsEndpoints.Route"/> and the capabilities endpoint
/// reports <c>supportsMcpConnectedApps</c>, so the Settings section shows.
/// </summary>
public interface IMcpConnectedApps
{
    /// <summary>
    /// Whether the owner can make an access token here (SelfHosted). False
    /// where apps connect themselves by signing in (Cloud).
    /// </summary>
    bool CanCreate { get; }

    Task<IReadOnlyList<McpConnectedAppDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Cuts the app off: its next <c>/mcp</c> request is refused. Returns false
    /// when there is no app with that id.
    /// </summary>
    Task<bool> RevokeAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>What an app may do, as Settings shows it.</summary>
public static class McpConnectedAppScope
{
    public const string Read = "read";
    public const string ReadWrite = "read_write";
}

/// <summary>How an app connected.</summary>
public static class McpConnectedAppKind
{
    /// <summary>An access token the owner made (SelfHosted).</summary>
    public const string Token = "token";

    /// <summary>An app that signed in with OAuth (Cloud; SelfHosted with #1470).</summary>
    public const string OAuthApp = "oauth_app";
}

/// <summary>One connected app. Never carries a token or a secret.</summary>
/// <param name="Id">The host's own id, opaque to the client.</param>
/// <param name="Kind"><see cref="McpConnectedAppKind"/>.</param>
/// <param name="Scope"><see cref="McpConnectedAppScope"/>.</param>
public sealed record McpConnectedAppDto(
    string Id,
    string Name,
    string Kind,
    string Scope,
    DateTime CreatedAt,
    DateTime? LastUsedAt);

/// <summary>The Settings list: the apps, and whether a token can be made here.</summary>
public sealed record McpConnectedAppsResponse(bool CanCreate, IReadOnlyList<McpConnectedAppDto> Apps);

/// <summary>
/// Runs on every authenticated <c>/mcp</c> request, after the host's
/// authentication and authorization and before the MCP server sees it. A gate
/// that answers false refuses the request with <c>401</c>
/// (<c>error="invalid_token"</c>); every registered gate must allow it. This
/// is where a host whose credentials outlive a revoke (a signed OAuth access
/// token) makes revoking take effect at once, and where it may note that the
/// app was used. A host whose authentication already looks the credential up
/// on every request (SelfHosted tokens) needs none.
/// </summary>
public interface IMcpRequestGate
{
    ValueTask<bool> AllowAsync(ClaimsPrincipal user, HttpContext http, CancellationToken cancellationToken);
}
