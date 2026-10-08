using Nostos.Backend.Services.Recovery;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Stable, provider-neutral customer API for read-only managed-backup history.
/// </summary>
public static class ManagedBackupEndpoints
{
    public const string Route = "/api/managed-backups";

    public static IEndpointRouteBuilder MapManagedBackupEndpoints(
        this IEndpointRouteBuilder routes,
        string? authorizationPolicy = null)
    {
        var endpoint = routes.MapGet(Route, ListAsync);
        if (string.IsNullOrWhiteSpace(authorizationPolicy))
            endpoint.RequireAuthorization();
        else
            endpoint.RequireAuthorization(authorizationPolicy);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        IManagedBackupCatalog catalog,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await catalog.ListAsync(cancellationToken));
        }
        catch (ManagedBackupsNotSupportedException)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }
    }
}
