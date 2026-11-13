using Nostos.Backend.Services.BookUpload;
using Nostos.Backend.Services.Library.Imports;
using Nostos.Product.Http;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Importing a Calibre library (issue #1527), and the uploaded-zip path that
/// lets a host without a readable folder offer it too (#1572).
///
/// The import routes are source-agnostic: SelfHosted reads a mounted folder,
/// Cloud reads a safely extracted upload, and both flow through the same
/// preview → match → commit → undo service. The upload routes exist only when
/// the host maps them and registers <see cref="CalibreUploadService"/>.
/// No route accepts a path: the owner names a Calibre book by its key from the
/// preview and nothing else.
/// </summary>
public static class CalibreEndpoints
{
    public const string Route = "/api/calibre";

    private const int MaxDecisions = 50_000;

    public static IEndpointRouteBuilder MapCalibreEndpoints(
        this IEndpointRouteBuilder routes,
        bool mapUpload = false,
        string? largeTransferRateLimitPolicy = null)
    {
        var status = routes.MapGroup(Route);

        var upload = mapUpload ? routes.MapGroup(Route + "/upload") : null;
        if (upload is not null && !string.IsNullOrWhiteSpace(largeTransferRateLimitPolicy))
            upload.RequireRateLimiting(largeTransferRateLimitPolicy);

        var group = routes.MapGroup(Route).AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            var folder = services.GetService<ICalibreLibraryAvailability>();
            var uploads = services.GetService<CalibreUploadService>();
            if (uploads is not null)
                await uploads.GetStagedLibraryAsync(context.HttpContext.RequestAborted);

            var folderConfigured = folder?.IsConfigured == true;
            var folderAvailable = folder?.IsAvailable == true;
            var staged = uploads?.HasStagedLibrary == true;

            if (!folderConfigured && !staged)
            {
                return Results.Conflict(new
                {
                    code = "calibre_not_configured",
                    message = uploads is not null
                        ? "No Calibre library is set up yet. Upload a zipped Calibre library to begin."
                        : "No Calibre library folder is configured on this server.",
                });
            }

            if (!folderAvailable && !staged)
            {
                return Results.Conflict(new
                {
                    code = "calibre_library_unavailable",
                    message = uploads is not null
                        ? "The Calibre folder is not readable. Upload a zipped Calibre library, or check the folder."
                        : "The Calibre library is not available. Check that its folder is mounted and holds metadata.db.",
                });
            }

            return await next(context);
        });

        status.MapGet("/", async (
            IServiceProvider services,
            CalibreImportJob job,
            CancellationToken ct) =>
        {
            var folder = services.GetService<ICalibreLibraryAvailability>();
            var uploads = services.GetService<CalibreUploadService>();
            var current = uploads is null ? null : await uploads.GetCurrentAsync(ct);
            var staged = uploads?.CachedStaged;
            var source = folder?.IsAvailable == true
                ? "folder"
                : staged is not null
                    ? "upload"
                    : null;
            var ownerKey = OwnerKey(services);

            return Results.Ok(new CalibreStatusResponse(
                folder?.IsConfigured == true || staged is not null,
                folder?.IsAvailable == true || staged is not null,
                job.StateFor(ownerKey),
                source,
                FolderSupported: folder is not null,
                UploadSupported: uploads is not null,
                MaxUploadBytes: uploads?.CurrentLimits.MaxLibraryBytes,
                Upload: current));
        });

        // Reads the whole library and proposes every book. Nothing is written.
        group.MapGet("/preview", async (CalibreImportService importer, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await importer.PreviewAsync(ct));
            }
            catch (CalibreLibraryException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
        });

        group.MapPost("/import", (
            CalibreImportRequest? request,
            CalibreImportJob job,
            IHostApplicationLifetime lifetime,
            HttpContext http) =>
        {
            var decisions = request?.Decisions ?? [];
            if (decisions.Count == 0)
            {
                return Results.BadRequest(new
                {
                    code = "calibre_nothing_selected",
                    message = "Choose at least one book and one format to import.",
                });
            }

            if (decisions.Count > MaxDecisions)
            {
                return Results.BadRequest(new
                {
                    code = "calibre_too_many_books",
                    message = $"At most {MaxDecisions} books can be imported at once.",
                });
            }

            var ownerKey = OwnerKey(http.RequestServices);
            return job.TryStart(decisions, ownerKey, lifetime.ApplicationStopping) switch
            {
                CalibreImportJob.StartResult.Started =>
                    Results.Accepted(Route + "/import", job.StateFor(ownerKey)),
                CalibreImportJob.StartResult.AlreadyRunning => Results.Conflict(new
                {
                    code = "calibre_import_running",
                    message = "An import is already running.",
                }),
                CalibreImportJob.StartResult.NothingSelected => Results.BadRequest(new
                {
                    code = "calibre_nothing_selected",
                    message = "Choose at least one book and one format to import.",
                }),
                _ => Results.Conflict(new
                {
                    code = "calibre_library_unavailable",
                    message = "The Calibre library is not available. Check that it is still readable.",
                }),
            };
        });

        // Progress, recent batches and undo stay answerable after the source is
        // gone: an uploaded library is released the moment its import commits,
        // and the result screen and undo must still work then.
        status.MapGet("/import", (CalibreImportJob job, HttpContext http) =>
            Results.Ok(job.StateFor(OwnerKey(http.RequestServices))));

        status.MapGet("/batches", async (CalibreImportService importer, CancellationToken ct) =>
            Results.Ok(await importer.ListBatchesAsync(5, ct)));

        // UNDO one import: removes the files, covers, memberships and books it
        // added. A book the owner has since read or written in is kept.
        status.MapDelete("/batches/{id:guid}", async (
            Guid id,
            CalibreImportService importer,
            CancellationToken ct) =>
            await importer.UndoAsync(id, ct) is { } result
                ? Results.Ok(result)
                : Results.NotFound());

        if (upload is not null)
            upload.MapCalibreUploadRoutes();

        return routes;
    }

    /// <summary>
    /// The owner whose in-memory job state this request sees. SelfHosted has
    /// one; a hosted deployment resolves its tenant from the same provider the
    /// upload service uses, so one customer can never read another's titles.
    /// </summary>
    private static string OwnerKey(IServiceProvider services) =>
        services.GetService<ICalibreUploadOwnerProvider>()?.GetOwnerKey()
        ?? CalibreImportJob.DefaultOwnerKey;

    /// <summary>
    /// The resumable upload contract for a zipped Calibre library. The part
    /// size is server-authoritative (the same 16 MiB part the library transfer
    /// uses), every part is receipted with its SHA-256, and a completed upload
    /// is verified, extracted and checked for metadata.db before review.
    /// </summary>
    private static void MapCalibreUploadRoutes(this IEndpointRouteBuilder upload)
    {
        upload.MapGet("/", async (CalibreUploadService service, CancellationToken ct) =>
            await service.GetCurrentAsync(ct) is { } current
                ? Results.Ok(current)
                : Results.NoContent());

        upload.MapPost("/", async (
            CalibreUploadCreateHttpRequest? body,
            CalibreUploadService service,
            CancellationToken ct) =>
        {
            if (body is null || body.TotalBytes <= 0 || body.Sha256 is null)
                return CalibreUploadHttpErrors.From(
                    CalibreUploadException.Error(
                        CalibreUploadException.InvalidRequest,
                        "The upload needs the file's size and SHA-256."));

            try
            {
                return Results.Ok(await service.CreateOrResumeAsync(
                    new CalibreUploadCreateRequest(body.TotalBytes, body.FileName ?? "library.zip", body.Sha256),
                    ct));
            }
            catch (CalibreUploadException exception)
            {
                return CalibreUploadHttpErrors.From(exception);
            }
        });

        upload.MapGet("/{sessionId:guid}", async (
            Guid sessionId,
            CalibreUploadService service,
            CancellationToken ct) =>
        {
            try
            {
                return await service.GetAsync(sessionId, ct) is { } current
                    ? Results.Ok(current)
                    : Results.NotFound();
            }
            catch (CalibreUploadException exception)
            {
                return CalibreUploadHttpErrors.From(exception);
            }
        });

        upload.MapPut("/{sessionId:guid}/parts/{partIndex:int}", async (
            Guid sessionId,
            int partIndex,
            HttpRequest request,
            CalibreUploadService service,
            CancellationToken ct) =>
        {
            if (!ResumableChunkRequest.TryRead(request, out var chunk, out var sha256))
            {
                return CalibreUploadHttpErrors.From(
                    CalibreUploadException.Error(
                        CalibreUploadException.InvalidRequest,
                        "A part needs a valid Content-Range and SHA-256."));
            }

            var effective = service.CurrentLimits;
            var length = effective.PartLengthFor(chunk.Total, partIndex);
            if (length <= 0 || chunk.Length != length)
            {
                return CalibreUploadHttpErrors.From(
                    CalibreUploadException.Error(
                        CalibreUploadException.PartRangeInvalid,
                        "That part does not match the upload's server-owned part size."));
            }

            if (ResumableChunkRequest.DeclaresMoreThan(request, length))
            {
                return CalibreUploadHttpErrors.From(
                    CalibreUploadException.Error(
                        CalibreUploadException.PartRangeInvalid,
                        "That part is larger than its declared range."));
            }

            ResumableChunkRequest.LimitBodySize(request, effective.PartBytes);

            try
            {
                var receipt = await service.UploadPartAsync(
                    sessionId,
                    partIndex,
                    chunk.Start,
                    chunk.End,
                    chunk.Total,
                    sha256,
                    request.Body,
                    ct);
                return Results.Ok(receipt);
            }
            catch (BadHttpRequestException exception)
                when (ResumableChunkRequest.IsPayloadTooLarge(exception))
            {
                return CalibreUploadHttpErrors.From(
                    CalibreUploadException.Error(
                        CalibreUploadException.PartRangeInvalid,
                        "That part is larger than the server accepts."));
            }
            catch (CalibreUploadException exception)
            {
                return CalibreUploadHttpErrors.From(exception);
            }
        });

        upload.MapPost("/{sessionId:guid}/complete", async (
            Guid sessionId,
            CalibreUploadService service,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.CompleteAsync(sessionId, ct));
            }
            catch (CalibreUploadException exception)
            {
                return CalibreUploadHttpErrors.From(exception);
            }
        });

        upload.MapDelete("/{sessionId:guid}", async (
            Guid sessionId,
            CalibreUploadService service,
            CancellationToken ct) =>
        {
            try
            {
                await service.DiscardAsync(sessionId, ct);
                return Results.NoContent();
            }
            catch (CalibreUploadException exception)
            {
                return CalibreUploadHttpErrors.From(exception);
            }
        });
    }
}

public sealed record CalibreImportRequest(IReadOnlyList<CalibreImportDecision>? Decisions = null);

public sealed record CalibreStatusResponse(
    bool Configured,
    bool Available,
    CalibreImportJobState Import,
    string? Source = null,
    bool FolderSupported = false,
    bool UploadSupported = false,
    long? MaxUploadBytes = null,
    CalibreUploadStatus? Upload = null);

public sealed record CalibreUploadCreateHttpRequest(long TotalBytes, string? FileName, string? Sha256);

/// <summary>Maps a typed upload failure to its stable code, status and safe words.</summary>
internal static class CalibreUploadHttpErrors
{
    public static IResult From(CalibreUploadException exception) =>
        Results.Json(
            new
            {
                code = exception.Code,
                message = exception.Message,
                missingParts = exception.MissingParts,
            },
            statusCode: StatusFor(exception.Code));

    private static int StatusFor(string code) => code switch
    {
        CalibreUploadException.NotFound => StatusCodes.Status404NotFound,
        CalibreUploadException.InvalidRequest
            or CalibreUploadException.PartRangeInvalid => StatusCodes.Status400BadRequest,
        CalibreUploadException.TooLarge => StatusCodes.Status413PayloadTooLarge,
        CalibreUploadException.PartConflict
            or CalibreUploadException.PartHashMismatch
            or CalibreUploadException.FileHashMismatch
            or CalibreUploadException.Incomplete
            or CalibreUploadException.Failed => StatusCodes.Status409Conflict,
        CalibreUploadException.Expired => StatusCodes.Status410Gone,
        CalibreUploadException.StorageExhausted => StatusCodes.Status507InsufficientStorage,
        CalibreUploadException.InvalidArchive
            or CalibreUploadException.UnsafeEntry
            or CalibreUploadException.TooManyFiles
            or CalibreUploadException.ExtractedTooLarge
            or CalibreUploadException.MetadataMissing
            or CalibreUploadException.NotCalibre => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status400BadRequest,
    };
}
