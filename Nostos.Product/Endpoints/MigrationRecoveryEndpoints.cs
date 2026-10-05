using Microsoft.AspNetCore.Http;
using Nostos.Backend.Middleware;
using Nostos.Backend.Services.Portability;
using Nostos.Product.Composition;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Recovery-copy transport for "Restore previous library" (issue #681, Slice 9).
/// These routes live in their own group under the migration base path and carry
/// the same authorization and rate-limit policies as the transfer routes, plus
/// the migration error body. List and status read only durable manifests (never
/// the active database), so they are maintenance-safe and stay usable during the
/// exclusive window; the restore handler takes its own shared operation lease
/// and answers the migration-shaped 503 while another operation owns the library.
/// </summary>
public static class MigrationRecoveryEndpoints
{
    public static IEndpointRouteBuilder MapMigrationRecoveryEndpoints(
        this IEndpointRouteBuilder routes,
        NostosProductEndpointPolicies? policies = null)
    {
        policies ??= NostosProductEndpointPolicies.None;
        if (!policies.MapMigrationTransferEndpoints)
        {
            return routes;
        }

        var group = routes.MapGroup(MigrationEndpoints.BasePath);
        if (!string.IsNullOrWhiteSpace(policies.LargeTransferRateLimitPolicy))
        {
            group.RequireRateLimiting(policies.LargeTransferRateLimitPolicy);
        }

        if (!string.IsNullOrWhiteSpace(policies.MigrationAuthorizationPolicy))
        {
            group.RequireAuthorization(policies.MigrationAuthorizationPolicy);
        }

        group.MapGet("/recovery", ListAsync).WithMetadata(new LibraryMaintenanceSafe());
        group.MapGet("/recovery/{id}", GetAsync).WithMetadata(new LibraryMaintenanceSafe());
        group.MapPost("/recovery/{id}/restore", RestoreAsync)
            .WithMetadata(new LibraryMaintenanceControl());
        return routes;
    }

    private static Task<IResult> ListAsync(
        ISelfHostedRecoveryRestore restore,
        CancellationToken ct) => GuardAsync(async () => Results.Ok(await restore.ListAsync(ct)));

    private static Task<IResult> GetAsync(
        string id,
        ISelfHostedRecoveryRestore restore,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (!TryParseId(id, out var recoveryId)) return InvalidRequest();
        var status = await restore.GetAsync(recoveryId, ct);
        return status is null
            ? MigrationHttpErrors.Result(MigrationHttpErrors.RecoveryNotFound, StatusCodes.Status404NotFound)
            : Results.Ok(status);
    });

    private static Task<IResult> RestoreAsync(
        string id,
        HttpRequest http,
        ISelfHostedRecoveryRestore restore,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (!TryParseId(id, out var recoveryId)) return InvalidRequest();
        var (ok, body) = await MigrationHttpBodies.TryReadAsync<MigrationRecoveryRestoreBody>(http, ct);
        if (!ok || body?.DestinationRevision is null || body.ConfirmReplacement is null)
        {
            return InvalidRequest();
        }

        var status = await restore.RequestRestoreAsync(
            recoveryId,
            new MigrationRecoveryRestoreRequest(body.DestinationRevision, body.ConfirmReplacement.Value),
            ct);
        return Results.Json(status, statusCode: StatusCodes.Status202Accepted);
    });

    private static bool TryParseId(string id, out Guid recoveryId) =>
        Guid.TryParse(id, out recoveryId) && recoveryId != Guid.Empty;

    private static IResult InvalidRequest() =>
        MigrationHttpErrors.Result(MigrationHttpErrors.InvalidRequest, StatusCodes.Status400BadRequest);

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (MigrationActivationException exception)
        {
            return MigrationHttpErrors.FromActivation(exception);
        }
    }
}

/// <summary>
/// Explicit request-body shape: both members are nullable so a missing property
/// is distinguishable from a default and answers the migration 400 model.
/// </summary>
public sealed record MigrationRecoveryRestoreBody(
    string? DestinationRevision = null,
    bool? ConfirmReplacement = null);
