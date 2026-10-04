using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Product.Composition;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Slice 10 resumable download: serves the sealed export artifact of a
/// completed export job over the framework's range-enabled physical-file
/// result. The file is streamed by the host (<c>SendFileAsync</c>), never
/// buffered in memory. Range, <c>If-Range</c>, ETag and 416 handling are the
/// framework's; this endpoint owns only selection and truthful typed failures.
/// Unknown/foreign identifiers and unavailable artifacts return 404; an expired
/// or deleted artifact returns 410. The endpoint stays in its own file so the
/// Slice 8 mapping can adopt it without touching this logic.
/// </summary>
public static class MigrationExportDownloadEndpoints
{
    public const string BasePath = "/api/portability/migration";

    public static IEndpointRouteBuilder MapMigrationExportDownloadEndpoints(
        this IEndpointRouteBuilder routes,
        NostosProductEndpointPolicies? policies = null)
    {
        policies ??= NostosProductEndpointPolicies.None;

        var group = routes.MapGroup(BasePath);
        if (!string.IsNullOrWhiteSpace(policies.LargeTransferRateLimitPolicy))
        {
            group.RequireRateLimiting(policies.LargeTransferRateLimitPolicy);
        }

        var download = group.MapGet(
            "/jobs/{id:guid}/export-download",
            async Task<IResult> (
                Guid id,
                NostosDbContext db,
                TransferPathResolver paths,
                TimeProvider clock,
                CancellationToken ct) =>
            {
                var job = await db.MigrationJobRecords.AsNoTracking()
                    .Where(j => j.Id == id)
                    .Select(j => new { j.Direction, j.State })
                    .SingleOrDefaultAsync(ct);
                if (job is null || job.Direction != (int)MigrationDirection.Export)
                {
                    return Results.Json(
                        new
                        {
                            error = "migration_not_found",
                            message = "No export job matches this identifier.",
                        },
                        statusCode: StatusCodes.Status404NotFound);
                }

                var artifact = await db.MigrationExportArtifactRecords.AsNoTracking()
                    .SingleOrDefaultAsync(a => a.JobId == id, ct);
                if (artifact is null || artifact.State == (int)MigrationExportArtifactState.Deleted)
                {
                    return Results.Json(
                        new
                        {
                            error = "migration_export_not_available",
                            message = "This export job has no downloadable artifact.",
                        },
                        statusCode: StatusCodes.Status404NotFound);
                }

                var now = clock.GetUtcNow().UtcDateTime;
                if (artifact.State == (int)MigrationExportArtifactState.Expired
                    || artifact.ExpiresAtUtc <= now)
                {
                    return Results.Json(
                        new
                        {
                            error = "migration_export_expired",
                            message = "The export artifact has expired.",
                        },
                        statusCode: StatusCodes.Status410Gone);
                }

                if (artifact.State != (int)MigrationExportArtifactState.Available
                    || job.State != (int)MigrationJobState.Completed)
                {
                    return Results.Json(
                        new
                        {
                            error = "migration_export_not_available",
                            message = "The export artifact is not available yet.",
                        },
                        statusCode: StatusCodes.Status404NotFound);
                }

                // Never serve an unknown key, a missing file, or a partial file
                // whose size no longer matches the sealed identity.
                if (!paths.TryResolveStorageKey(artifact.StorageKey, out var path)
                    || !File.Exists(path)
                    || new FileInfo(path).Length != artifact.SizeBytes)
                {
                    return Results.Json(
                        new
                        {
                            error = "migration_export_not_available",
                            message = "The export artifact is not available.",
                        },
                        statusCode: StatusCodes.Status404NotFound);
                }

                var entityTag = artifact.Sha256 is { Length: 64 } sha256
                    ? new EntityTagHeaderValue($"\"{sha256}\"")
                    : null;
                var lastModified = artifact.AvailableAtUtc is { } availableAtUtc
                    ? new DateTimeOffset(DateTime.SpecifyKind(availableAtUtc, DateTimeKind.Utc))
                    : (DateTimeOffset?)null;

                return Results.File(
                    path,
                    artifact.ContentType,
                    fileDownloadName: artifact.FileName,
                    enableRangeProcessing: true,
                    lastModified: lastModified,
                    entityTag: entityTag);
            });

        if (!string.IsNullOrWhiteSpace(policies.PortableExportAuthorizationPolicy))
        {
            download.RequireAuthorization(policies.PortableExportAuthorizationPolicy);
        }

        return routes;
    }
}
