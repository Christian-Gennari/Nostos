using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// Settings → Connected apps (issue #1469): list and revoke, on any host that
/// registers an <see cref="IMcpConnectedApps"/>. Making a token is the
/// SelfHosted host's own route, because only it has tokens.
/// </summary>
public static class McpConnectedAppsEndpoints
{
    public const string Route = "/api/mcp/apps";

    /// <summary>Maps the routes when the host registered a record of connected apps; returns whether it did.</summary>
    public static bool MapMcpConnectedAppsEndpoints(this IEndpointRouteBuilder routes)
    {
        if (routes.ServiceProvider.GetService<IServiceProviderIsService>()?.IsService(typeof(IMcpConnectedApps)) != true)
            return false;

        var group = routes.MapGroup(Route);

        group.MapGet("/", async (IMcpConnectedApps apps, CancellationToken ct) =>
            Results.Ok(new McpConnectedAppsResponse(apps.CanCreate, await apps.ListAsync(ct))));

        group.MapDelete("/{id}", async (string id, IMcpConnectedApps apps, CancellationToken ct) =>
            await apps.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        return true;
    }
}
