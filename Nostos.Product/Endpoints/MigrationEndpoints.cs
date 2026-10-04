using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Product.Composition;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Migration transfer transport (issue #679, Slice 8). The legacy
/// <c>GET /api/portability/export</c> and <c>POST /api/portability/import</c>
/// routes are untouched; this group adds the durable job/session/chunk API.
/// No route here owns admission: they are ordinary library requests under the
/// ambient shared maintenance lease, and exclusive maintenance answers 503.
/// </summary>
public static class MigrationEndpoints
{
    public const string BasePath = "/api/portability/migration";

    public static IEndpointRouteBuilder MapMigrationEndpoints(
        this IEndpointRouteBuilder routes,
        NostosProductEndpointPolicies? policies = null)
    {
        policies ??= NostosProductEndpointPolicies.None;

        var group = routes.MapGroup(BasePath);
        if (!string.IsNullOrWhiteSpace(policies.LargeTransferRateLimitPolicy))
            group.RequireRateLimiting(policies.LargeTransferRateLimitPolicy);
        if (!string.IsNullOrWhiteSpace(policies.MigrationAuthorizationPolicy))
            group.RequireAuthorization(policies.MigrationAuthorizationPolicy);

        group.MapPost("/preflight", PreflightAsync);
        group.MapPost("/jobs", CreateJobAsync);
        group.MapGet("/jobs/{id:guid}", GetJobAsync);
        group.MapPost("/jobs/{id:guid}/cancel", CancelAsync);
        group.MapPost("/jobs/{id:guid}/retry", RetryAsync);
        group.MapPost("/jobs/{id:guid}/upload-session", CreateUploadSessionAsync);
        group.MapGet("/jobs/{id:guid}/upload-session", GetUploadSessionAsync);
        group.MapPut("/jobs/{id:guid}/upload-session/chunks/{index:int}", UploadChunkAsync);
        group.MapPost("/jobs/{id:guid}/upload-session/complete", CompleteUploadAsync);

        // Slice 10 adds GET /jobs/{id}/export-download in this group once the
        // export artifact writer exists. It is intentionally not mapped here.
        return routes;
    }

    private static Task<IResult> PreflightAsync(
        MigrationPreflightRequest? request,
        IMigrationPreflightService preflight,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (request?.IncomingCounts is null)
            return MigrationHttpErrors.Result(MigrationHttpErrors.InvalidRequest, StatusCodes.Status400BadRequest);
        return Results.Ok(await preflight.EvaluateAsync(request, ct));
    });

    private static Task<IResult> CreateJobAsync(
        MigrationCreateJobRequest? request,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (request is null)
            return MigrationHttpErrors.Result(MigrationHttpErrors.InvalidRequest, StatusCodes.Status400BadRequest);

        var result = await service.CreateAsync(request, ct);
        if (result.IsConflict)
        {
            return MigrationHttpErrors.Result(
                MigrationHttpErrors.IdempotencyConflict,
                StatusCodes.Status409Conflict);
        }

        var status = await service.GetStatusAsync(result.Resource!.Id, ct);
        return result.WasReplay
            ? Results.Ok(status)
            : Results.Created($"{BasePath}/jobs/{status.Job.Id}", status);
    });

    private static Task<IResult> GetJobAsync(
        Guid id,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
        Results.Ok(await service.GetStatusAsync(id, ct)));

    private static Task<IResult> CancelAsync(
        Guid id,
        MigrationCancelRequest? request,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
        Results.Ok(await service.CancelAsync(id, request ?? new MigrationCancelRequest(), ct)));

    private static Task<IResult> RetryAsync(
        Guid id,
        MigrationRetryRequest? request,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
        Results.Ok(await service.RetryAsync(id, request ?? new MigrationRetryRequest(), ct)));

    private static Task<IResult> CreateUploadSessionAsync(
        Guid id,
        MigrationSessionRequest? request,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (request is null)
            return MigrationHttpErrors.Result(MigrationHttpErrors.InvalidRequest, StatusCodes.Status400BadRequest);

        var result = await service.CreateUploadSessionAsync(id, request, ct);
        if (result.IsConflict)
        {
            return MigrationHttpErrors.Result(
                MigrationHttpErrors.IdempotencyConflict,
                StatusCodes.Status409Conflict);
        }

        return result.WasReplay
            ? Results.Ok(result.Resource)
            : Results.Created($"{BasePath}/jobs/{id}/upload-session", result.Resource);
    });

    private static Task<IResult> GetUploadSessionAsync(
        Guid id,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
        Results.Ok(await service.GetUploadSessionAsync(id, ct)));

    private static Task<IResult> UploadChunkAsync(
        Guid id,
        int index,
        HttpRequest request,
        SelfHostedMigrationJobService service,
        IOptions<TransferStorageOptions> options,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (!MigrationChunkHeaders.TryParseContentRange(
                request.Headers["Content-Range"],
                out var start,
                out var end,
                out var total)
            || !MigrationChunkHeaders.IsSha256(
                request.Headers[MigrationChunkHeaders.ChunkHashHeaderName]))
        {
            return MigrationHttpErrors.Result(
                MigrationHttpErrors.InvalidRequest,
                StatusCodes.Status400BadRequest);
        }

        // Endpoint-specific body limit: one configured chunk, never the global
        // 4 GiB portability cap. Requests with a declared oversize body are
        // refused before any byte is read; a chunked body that grows past the
        // limit makes Kestrel throw 413, mapped below.
        var bodyLimit = options.Value.MaxChunkBytes;
        if (request.ContentLength > bodyLimit)
            return ChunkTooLarge();

        var bodySizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
            bodySizeFeature.MaxRequestBodySize = bodyLimit;

        var metadata = new MigrationChunkMetadata(
            start,
            end,
            total,
            request.Headers[MigrationChunkHeaders.ChunkHashHeaderName].ToString());

        try
        {
            // The request body is handed to the upload engine as a stream: the
            // endpoint never buffers, materializes or seeks a chunk.
            var result = await service.UploadChunkAsync(id, index, metadata, request.Body, ct);
            return Results.Ok(result);
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ChunkTooLarge();
        }
    });

    private static Task<IResult> CompleteUploadAsync(
        Guid id,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
        Results.Ok(await service.CompleteUploadAsync(id, ct)));

    private static IResult ChunkTooLarge() =>
        Results.Json(
            new MigrationErrorResponse(
                MigrationHttpErrors.InvalidRequest,
                "The chunk request body exceeds the maximum accepted size."),
            statusCode: StatusCodes.Status413PayloadTooLarge);

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (MigrationJobStoreException exception)
        {
            return MigrationHttpErrors.FromStore(exception);
        }
        catch (MigrationTransferException exception)
        {
            return MigrationHttpErrors.FromTransfer(exception);
        }
        catch (TransferReservationException)
        {
            return MigrationHttpErrors.Result(
                MigrationHttpErrors.ReservationRequired,
                StatusCodes.Status409Conflict);
        }
    }
}
