using Nostos.Backend.Data.Models;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Services.Mcp;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Making an access token for <c>/mcp</c> (issue #1469), SelfHosted only.
/// Listing and revoking are the product's
/// <see cref="McpConnectedAppsEndpoints"/>, the same routes Cloud answers for
/// its OAuth apps. The token is in the answer to this request and in no other
/// answer, ever.
/// </summary>
public static class McpAccessTokenEndpoints
{
    public const string Route = "/api/mcp/tokens";

    public static IEndpointRouteBuilder MapMcpAccessTokenEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(Route, async (
            CreateMcpAccessTokenRequest? request,
            McpAccessTokenService tokens,
            HttpContext http,
            CancellationToken ct) =>
        {
            var name = request?.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > McpAccessTokenModel.MaxNameLength)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = [$"Give the token a name of 1 to {McpAccessTokenModel.MaxNameLength} characters."],
                });
            }

            var scope = request?.Scope ?? McpConnectedAppScope.Read;
            if (scope is not (McpConnectedAppScope.Read or McpConnectedAppScope.ReadWrite))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["scope"] = ["Scope must be 'read' or 'read_write'."],
                });
            }

            McpAccessTokenCreatedDto created;
            try
            {
                created = await tokens.CreateAsync(name, scope, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.Message);
            }

            // The one answer that holds the token: no cache may keep it.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"{McpConnectedAppsEndpoints.Route}/{created.Details.Id}", created);
        });

        return routes;
    }
}
