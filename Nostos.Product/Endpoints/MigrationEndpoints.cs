using System.Globalization;
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
/// ambient shared maintenance lease, and exclusive maintenance answers a
/// migration-shaped 503. Every binding/validation failure on these routes —
/// body, header, or route value — answers the migration error body.
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
        group.MapGet("/jobs/{id}", GetJobAsync);
        group.MapPost("/jobs/{id}/cancel", CancelAsync);
        group.MapPost("/jobs/{id}/retry", RetryAsync);
        group.MapPost("/jobs/{id}/upload-session", CreateUploadSessionAsync);
        group.MapGet("/jobs/{id}/upload-session", GetUploadSessionAsync);
        group.MapPut("/jobs/{id}/upload-session/chunks/{index}", UploadChunkAsync);
        group.MapPost("/jobs/{id}/upload-session/complete", CompleteUploadAsync);

        // Slice 10 adds GET /jobs/{id}/export-download in this group once the
        // export artifact writer exists. It is intentionally not mapped here.
        return routes;
    }

    private static Task<IResult> PreflightAsync(
        HttpRequest http,
        IMigrationPreflightService preflight,
        CancellationToken ct) => GuardAsync(async () =>
    {
        var (ok, body) = await MigrationHttpBodies.TryReadAsync<MigrationPreflightBody>(http, ct);
        if (!ok || body is null
            || body.IncomingCounts is null
            || body.DeclaredArchiveBytes is not { } archiveBytes
            || body.DeclaredMediaBytes is not { } mediaBytes
            || body.MaxSingleEntryBytes is not { } maxEntryBytes
            || body.DeclaredFormatVersion is not { } formatVersion
            || body.DeclaredDataVersion is not { } dataVersion)
        {
            return InvalidRequest();
        }

        var request = new MigrationPreflightRequest(
            body.IncomingCounts,
            archiveBytes,
            mediaBytes,
            maxEntryBytes,
            formatVersion,
            dataVersion,
            body.DeclaredFormatName,
            body.ClientDestinationRevision,
            body.IsOperationalBackup ?? false);
        return Results.Ok(await preflight.EvaluateAsync(request, ct));
    });

    private static Task<IResult> CreateJobAsync(
        HttpRequest http,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
    {
        var (ok, body) = await MigrationHttpBodies.TryReadAsync<MigrationCreateJobBody>(http, ct);
        if (!ok || body is null
            || body.Direction is not { } direction
            || !Enum.IsDefined(direction)
            || string.IsNullOrWhiteSpace(body.IdempotencyKey))
        {
            return InvalidRequest();
        }

        var result = await service.CreateAsync(
            new MigrationCreateJobRequest(direction, body.IdempotencyKey, body.ReservationId),
            ct);
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
        string id,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (!TryParseJobId(id, out var jobId)) return InvalidRequest();
        return Results.Ok(await service.GetStatusAsync(jobId, ct));
    });

    private static Task<IResult> CancelAsync(
        string id,
        HttpRequest http,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (!TryParseJobId(id, out var jobId)) return InvalidRequest();
        var (ok, body) = await MigrationHttpBodies.TryReadAsync<MigrationCancelBody>(http, ct);
        if (!ok) return InvalidRequest();
        return Results.Ok(await service.CancelAsync(
            jobId,
            new MigrationCancelRequest(body?.Reason),
            ct));
    });

    private static Task<IResult> RetryAsync(
        string id,
        HttpRequest http,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (!TryParseJobId(id, out var jobId)) return InvalidRequest();
        var (ok, body) = await MigrationHttpBodies.TryReadAsync<MigrationRetryBody>(http, ct);
        if (!ok) return InvalidRequest();
        return Results.Ok(await service.RetryAsync(
            jobId,
            new MigrationRetryRequest(body?.IdempotencyKey),
            ct));
    });

    private static Task<IResult> CreateUploadSessionAsync(
        string id,
        HttpRequest http,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (!TryParseJobId(id, out var jobId)) return InvalidRequest();
        var (ok, body) = await MigrationHttpBodies.TryReadAsync<MigrationSessionBody>(http, ct);
        if (!ok || body is null
            || body.Purpose is not { } purpose
            || !Enum.IsDefined(purpose)
            || body.TotalBytes is not { } totalBytes
            || body.ChunkSize is not { } chunkSize
            || body.TotalChunks is not { } totalChunks
            || body.FileIdentity is not { } identity
            || identity.TotalSizeBytes is not { } identitySize
            || identity.Sha256Checksum is null
            || body.IdempotencyKey is null)
        {
            return InvalidRequest();
        }

        var request = new MigrationSessionRequest(
            purpose,
            totalBytes,
            chunkSize,
            totalChunks,
            new MigrationFileIdentity(identitySize, identity.Sha256Checksum, identity.ClientFingerprint),
            body.IdempotencyKey);
        var result = await service.CreateUploadSessionAsync(jobId, request, ct);
        if (result.IsConflict)
        {
            return MigrationHttpErrors.Result(
                MigrationHttpErrors.IdempotencyConflict,
                StatusCodes.Status409Conflict);
        }

        return result.WasReplay
            ? Results.Ok(result.Resource)
            : Results.Created($"{BasePath}/jobs/{jobId}/upload-session", result.Resource);
    });

    private static Task<IResult> GetUploadSessionAsync(
        string id,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (!TryParseJobId(id, out var jobId)) return InvalidRequest();
        return Results.Ok(await service.GetUploadSessionAsync(jobId, ct));
    });

    private static Task<IResult> UploadChunkAsync(
        string id,
        string index,
        HttpRequest request,
        SelfHostedMigrationJobService service,
        IOptions<TransferStorageOptions> options,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (!TryParseJobId(id, out var jobId)
            || !TryParseChunkIndex(index, out var chunkIndex))
        {
            return InvalidRequest();
        }

        if (!MigrationChunkHeaders.TryParseContentRange(
                request.Headers["Content-Range"],
                out var start,
                out var end,
                out var total)
            || !MigrationChunkHeaders.IsSha256(
                request.Headers[MigrationChunkHeaders.ChunkHashHeaderName]))
        {
            return InvalidRequest();
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
            var result = await service.UploadChunkAsync(jobId, chunkIndex, metadata, request.Body, ct);
            return Results.Ok(result);
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ChunkTooLarge();
        }
    });

    private static Task<IResult> CompleteUploadAsync(
        string id,
        SelfHostedMigrationJobService service,
        CancellationToken ct) => GuardAsync(async () =>
    {
        if (!TryParseJobId(id, out var jobId)) return InvalidRequest();
        return Results.Ok(await service.CompleteUploadAsync(jobId, ct));
    });

    private static bool TryParseJobId(string id, out Guid jobId) =>
        Guid.TryParse(id, out jobId);

    private static bool TryParseChunkIndex(string index, out int chunkIndex) =>
        int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out chunkIndex);

    private static IResult InvalidRequest() =>
        MigrationHttpErrors.Result(
            MigrationHttpErrors.InvalidRequest,
            StatusCodes.Status400BadRequest);

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
        catch (TransferReservationException exception)
        {
            return MigrationHttpErrors.FromReservation(exception);
        }
    }
}
