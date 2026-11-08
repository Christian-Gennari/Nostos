using System.Security.Claims;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// What a connected app may do (issue #1469), checked once for every tool
/// call by the filter <see cref="NostosMcp.AddNostosMcp"/> installs.
///
/// The host's authentication says it in OAuth's own words: a
/// <see cref="NostosMcp.ScopeClaimType"/> claim holding space-separated scopes
/// (several claims are read together). <see cref="NostosMcp.ReadScope"/> is
/// needed for every tool; <see cref="NostosMcp.WriteScope"/> also for the
/// tools that change something (<c>save_note</c>, <c>save_passage</c>,
/// <c>create_draft</c>, <c>add_to_reading_list</c>, <c>add_book</c>,
/// <c>update_book</c>). Scopes match ordinally. A caller with no scope
/// claim at all counts as read-only.
///
/// Hosts only fill the claim: SelfHosted from the access token the owner made
/// in Settings, Cloud from the Clerk OAuth access token's own <c>scope</c>.
/// </summary>
public static class McpScopes
{
    /// <summary>
    /// The claim type ASP.NET's JWT handler maps <c>scope</c> to when inbound
    /// claim mapping is on; read as well, so a host's handler setting cannot
    /// silently turn every caller read-only.
    /// </summary>
    public const string MappedClaimType = "http://schemas.microsoft.com/identity/claims/scope";

    /// <summary>The message a read-only connection gets from a write tool.</summary>
    public const string ReadOnlyRefusal =
        "This connection to Nostos is read-only, so nothing was saved. To let this app save notes, drafts and books to read, add books and edit book details, connect it with read and write access (on a self-hosted Nostos: make a read and write token in Settings → Connected apps).";

    /// <summary>The message a caller without <see cref="NostosMcp.ReadScope"/> gets from every tool.</summary>
    public const string NoReadRefusal =
        "This connection was not given access to read the Nostos library (scope nostos:read), so nothing was read. Reconnect the app and allow it to read your library.";

    /// <summary>The scopes the caller was granted; null when it has no scope claim at all.</summary>
    public static IReadOnlySet<string>? Granted(ClaimsPrincipal? user)
    {
        var claims = user?.Claims
            .Where(c => c.Type is NostosMcp.ScopeClaimType or MappedClaimType)
            .ToList();
        if (claims is null || claims.Count == 0)
            return null;
        return claims
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Whether the caller may use the read tools: no scope claim, or one with <see cref="NostosMcp.ReadScope"/>.</summary>
    public static bool CanRead(ClaimsPrincipal? user) =>
        Granted(user) is not { } scopes || scopes.Contains(NostosMcp.ReadScope);

    /// <summary>Whether the caller may use the write tools: both scopes granted.</summary>
    public static bool CanWrite(ClaimsPrincipal? user) =>
        Granted(user) is { } scopes
        && scopes.Contains(NostosMcp.ReadScope)
        && scopes.Contains(NostosMcp.WriteScope);

    /// <summary>
    /// A tool changes the library unless its annotations say it only reads.
    /// A tool without annotations counts as one that writes, so a new tool is
    /// refused to a read-only caller until it is marked read-only.
    /// </summary>
    internal static bool IsWriteTool(McpServerTool tool) =>
        tool.ProtocolTool.Annotations?.ReadOnlyHint != true;

    /// <summary>
    /// The call-tool filter: a tool the caller's scopes do not cover answers
    /// with a tool error and never runs.
    /// </summary>
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Enforce(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, cancellationToken) =>
        {
            if (context.MatchedPrimitive is McpServerTool tool)
            {
                var refusal = !CanRead(context.User) ? NoReadRefusal
                    : IsWriteTool(tool) && !CanWrite(context.User) ? ReadOnlyRefusal
                    : null;
                if (refusal is not null)
                {
                    return ValueTask.FromResult(new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = refusal }],
                    });
                }
            }

            return next(context, cancellationToken);
        };
}
