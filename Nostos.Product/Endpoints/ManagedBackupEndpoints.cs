using Nostos.Backend.Services.Recovery;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Provider-neutral customer managed-backup history and restore API.
/// </summary>
public static class ManagedBackupEndpoints
{
    public const string Route = "/api/managed-backups";

    public static IEndpointRouteBuilder MapManagedBackupEndpoints(
        this IEndpointRouteBuilder routes,
        string? authorizationPolicy = null,
        string? restoreRateLimitPolicy = null)
    {
        var list = routes.MapGet(Route, ListAsync);
        var restore = routes.MapPost(
            Route + "/{backupId:guid}/restore",
            RestoreAsync);

        if (string.IsNullOrWhiteSpace(authorizationPolicy))
        {
            list.RequireAuthorization();
            restore.RequireAuthorization();
        }
        else
        {
            list.RequireAuthorization(authorizationPolicy);
            restore.RequireAuthorization(authorizationPolicy);
        }

        if (!string.IsNullOrWhiteSpace(restoreRateLimitPolicy))
            restore.RequireRateLimiting(restoreRateLimitPolicy);

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

    private static async Task<IResult> RestoreAsync(
        Guid backupId,
        ManagedBackupRestoreRequest request,
        IManagedBackupRestorer restorer,
        CancellationToken cancellationToken)
    {
        if (!request.Confirm)
        {
            return Results.Problem(
                title: "Explicit restore confirmation is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var result = await restorer.RestoreAsync(
                backupId,
                cancellationToken);

            return Results.Ok(result);
        }
        catch (ManagedBackupsNotSupportedException)
        {
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }
        catch (ManagedBackupRestoreException exception)
        {
            var status = exception.Code switch
            {
                ManagedBackupRestoreError.NotFound =>
                    StatusCodes.Status404NotFound,
                ManagedBackupRestoreError.Conflict =>
                    StatusCodes.Status409Conflict,
                ManagedBackupRestoreError.InvalidBackup =>
                    StatusCodes.Status422UnprocessableEntity,
                _ => StatusCodes.Status500InternalServerError
            };

            return Results.Problem(
                title: exception.Message,
                statusCode: status);
        }
    }
}
