using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Nostos.Product.Composition;
using Nostos.Product.Http;
using Nostos.Product.BookText;
using Nostos.Backend.Providers.Acquisition;

namespace Nostos.Backend.Endpoints;

public static class BooksEndpoints
{
    public static IEndpointRouteBuilder MapBooksEndpoints(
        this IEndpointRouteBuilder routes,
        NostosProductEndpointPolicies? policies = null)
    {
        policies ??= NostosProductEndpointPolicies.None;

        var group = routes.MapGroup("/api/books");
        var uploadGroup = string.IsNullOrWhiteSpace(policies.ExpensiveMutationRateLimitPolicy)
            ? group
            : routes.MapGroup("/api/books")
                .RequireRateLimiting(policies.ExpensiveMutationRateLimitPolicy);
        var metadataGroup = string.IsNullOrWhiteSpace(policies.ProviderFetchRateLimitPolicy)
            ? group
            : routes.MapGroup("/api/books")
                .RequireRateLimiting(policies.ProviderFetchRateLimitPolicy);

        // GET all books
        group.MapGet(
            "/",
            async (
                ILibraryService library,
                string? filter,
                string? sort,
                string? search,
                int? page,
                int? pageSize,
                Guid? collectionId,
                bool? groupByWork,
                string? format,
                CancellationToken ct
            ) =>
            {
                Enum.TryParse<BookFilter>(filter, true, out var filterEnum);
                Enum.TryParse<BookSort>(sort, true, out var sortEnum);

                var result = await library.ListBooksAsync(
                    filterEnum, sortEnum, search, page ?? 1, pageSize ?? 20,
                    collectionId, groupByWork, format, ct);

                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // GET aggregate status counts for the library sidebar
        group.MapGet(
            "/status-counts",
            async (ILibraryService library, CancellationToken ct) =>
            {
                var result = await library.GetStatusCountsAsync(ct);
                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // GET one
        group.MapGet(
            "/{id}",
            async (Guid id, ILibraryService library, CancellationToken ct) =>
            {
                var result = await library.GetBookAsync(id, ct);
                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // CREATE (create-or-match, legacy permissive semantics: ambiguity
        // creates rather than asking; exact matches return the existing book)
        group.MapPost(
            "/",
            async (CreateBookDto dto, ILibraryService library, CancellationToken ct) =>
            {
                var request = new LibraryCreateBookRequest(
                    "rest", $"rest-create-{Guid.NewGuid():N}",
                    dto.Type, dto.Title,
                    Subtitle: dto.Subtitle, Author: dto.Author, Editor: dto.Editor,
                    Translator: dto.Translator, Narrator: dto.Narrator,
                    Description: dto.Description, Isbn: dto.Isbn, Asin: dto.Asin, Duration: dto.Duration,
                    Publisher: dto.Publisher, PlaceOfPublication: dto.PlaceOfPublication,
                    PublishedDate: dto.PublishedDate, Edition: dto.Edition,
                    PageCount: dto.PageCount, Language: dto.Language, Categories: dto.Categories,
                    Series: dto.Series, VolumeNumber: dto.VolumeNumber,
                    // REST keeps accepting the singular collectionId as a single-
                    // element membership set: it is the shape the mobile/OPDS
                    // callers and the endpoint tests already use, and the
                    // service translates it into membership rows.
                    CollectionIds: dto.CollectionIds
                        ?? (dto.CollectionId.HasValue ? [dto.CollectionId.Value] : null),
                    Rating: dto.Rating, IsFavorite: dto.IsFavorite,
                    PersonalReview: dto.PersonalReview, FinishedAt: dto.FinishedAt,
                    FileUploadExpected: dto.FileUploadExpected);

                var result = await library.CreateOrMatchBookAsync(request, strictConfirmation: false, ct);
                if (LibraryHttpMapper.MapError(result) is { } error)
                    return error;

                var outcome = (LibraryCreateOrMatchResultDto)result.Data!;
                return outcome.Outcome == "created"
                    ? Results.Created($"/api/books/{outcome.BookId}", outcome.Book)
                    : Results.Ok(outcome.Book);
            }
        );

        // UPDATE
        group.MapPut(
            "/{id}",
            async (Guid id, UpdateBookDto dto, ILibraryService library, CancellationToken ct) =>
            {
                var request = new LibraryUpdateBookRequest(
                    "rest", $"rest-update-{Guid.NewGuid():N}",
                    id,
                    Title: dto.Title, Subtitle: dto.Subtitle, Author: dto.Author,
                    Editor: dto.Editor, Translator: dto.Translator, Narrator: dto.Narrator,
                    Description: dto.Description, Isbn: dto.Isbn, Asin: dto.Asin, Duration: dto.Duration,
                    Publisher: dto.Publisher, PlaceOfPublication: dto.PlaceOfPublication,
                    PublishedDate: dto.PublishedDate, Edition: dto.Edition,
                    PageCount: dto.PageCount, Language: dto.Language, Categories: dto.Categories,
                    Series: dto.Series, VolumeNumber: dto.VolumeNumber,
                    // Singular collectionId (REST compatibility shape) becomes a
                    // single-element set; ClearCollection stays honoured.
                    CollectionIds: dto.CollectionIds
                        ?? (dto.CollectionId.HasValue ? [dto.CollectionId.Value] : null),
                    ClearCollection: dto.ClearCollection,
                    Rating: dto.Rating, IsFavorite: dto.IsFavorite,
                    PersonalReview: dto.PersonalReview, FinishedAt: dto.FinishedAt,
                    IsFinished: dto.IsFinished);

                var result = await library.UpdateBookAsync(request, ct);
                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // SET collection membership (full replacement set). A dedicated route so
        // the client intent is explicit — "this book belongs to exactly these
        // collections" — and it cannot be confused with a metadata save. Set
        // semantics express add, remove and clear-all in one idempotent call.
        group.MapPut(
            "/{id}/collections",
            async (Guid id, UpdateBookCollectionsDto dto, ILibraryService library, CancellationToken ct) =>
            {
                var request = new LibraryUpdateBookRequest(
                    "rest", $"rest-collections-{Guid.NewGuid():N}",
                    id, CollectionIds: dto.CollectionIds);

                var result = await library.UpdateBookAsync(request, ct);
                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // UPDATE progress (canonical service; validated 0..100, FinishedAt
        // alignment, version bump; not receipt-guarded by design)
        group.MapPut(
            "/{id}/progress",
            async (Guid id, UpdateProgressDto dto, ILibraryService library, CancellationToken ct) =>
            {
                var result = await library.UpdateProgressAsync(id, dto.Location, dto.Percentage, ct);
                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // LINK this book into another book's work (manual multi-edition
        // override). A dedicated route, like /collections: the client intent is
        // explicit, and WorkId is never written directly by a client — the
        // domain service owns the merge, the orphan cleanup and the version.
        group.MapPost(
            "/{id}/work/link",
            async (Guid id, LinkWorkDto dto, ILibraryService library, CancellationToken ct) =>
            {
                var request = new LibraryLinkWorkRequest(
                    "rest", $"rest-work-link-{Guid.NewGuid():N}", id, dto.TargetBookId);

                var result = await library.LinkWorkAsync(request, ct);
                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // UNLINK this book into its own new work (manual split). The book
        // always ends up with a valid work; there is no "no work" state to
        // fall into.
        group.MapPost(
            "/{id}/work/unlink",
            async (Guid id, ILibraryService library, CancellationToken ct) =>
            {
                var request = new LibraryUnlinkWorkRequest(
                    "rest", $"rest-work-unlink-{Guid.NewGuid():N}", id);

                var result = await library.UnlinkWorkAsync(request, ct);
                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // RESET Progress (explicit reset intent: clears location, percent,
        // finished and recency; deliberately NOT a 0% progress update)
        group.MapPost(
            "/{id}/progress/reset",
            async (Guid id, ILibraryService library, CancellationToken ct) =>
            {
                var result = await library.ResetProgressAsync(id, ct);
                return LibraryHttpMapper.MapError(result) ?? Results.Ok(result.Data);
            }
        );

        // GET Epub cached locations (Cached)
        group.MapGet(
            "/{id}/locations",
            async (Guid id, IBookRepository repo, CancellationToken ct) =>
            {
                var book = await repo.GetByIdAsync(id);
                if (book is null)
                    return Results.NotFound();

                if (string.IsNullOrWhiteSpace(book.FileDetails.LocationsJson))
                    return Results.NotFound();

                return Results.Ok(new BookLocationsDto(book.FileDetails.LocationsJson));
            }
        );

        // SAVE Locations (Cache them)
        group.MapPost(
            "/{id}/locations",
            async (Guid id, BookLocationsDto dto, IBookRepository repo, CancellationToken ct) =>
            {
                var book = await repo.GetByIdAsync(id);
                if (book is null)
                    return Results.NotFound();

                book.FileDetails.LocationsJson = dto.Locations;
                await repo.UpdateAsync(book);

                return Results.Ok();
            }
        );

        // Book-text indexing state is deliberately separate from the core book
        // import status: a valid book may still be Pending/Failed/Unsupported for
        // Ask Nostos and can be retried without re-uploading the publication.
        group.MapGet(
            "/{id}/text-index",
            async (Guid id, ILibraryService library, IBookTextIndex index, CancellationToken ct) =>
            {
                var book = await library.GetBookAsync(id, ct);
                if (LibraryHttpMapper.MapError(book) is { } error)
                    return error;

                var state = await index.GetStateAsync(id, ct);
                return state is null
                    ? Results.Ok(new
                    {
                        bookId = id,
                        status = "NotIndexed",
                    })
                    : Results.Ok(state);
            }
        );

        group.MapPost(
            "/{id}/text-index/retry",
            async (
                Guid id,
                ILibraryService library,
                IBookTextIngestionScheduler scheduler,
                CancellationToken ct) =>
            {
                var result = await library.GetBookAsync(id, ct);
                if (LibraryHttpMapper.MapError(result) is { } error)
                    return error;
                if (result.Data is not BookDto book || !book.HasFile || string.IsNullOrWhiteSpace(book.FileName))
                    return Results.BadRequest("This book has no digital source file to index.");

                await scheduler.ScheduleAsync(id, book.FileName, ct);
                return Results.Accepted($"/api/books/{id}/text-index");
            }
        );

        // DELETE (row first through the canonical service; storage files are
        // removed only after the row is gone, so an in-use book keeps its
        // files)
        group.MapDelete(
            "/{id}",
            async (Guid id, ILibraryService library, IBookAssetStorage storage, IBookTextLifecycle bookText, CancellationToken ct) =>
            {
                var result = await library.DeleteBookAsync(id, ct);
                if (LibraryHttpMapper.MapError(result) is { } error)
                    return error;

                await bookText.DeleteAsync(id, ct);
                await storage.DeleteBookFilesAsync(id, ct);
                return Results.NoContent();
            }
        );

        // Upload file
        uploadGroup.MapPost(
            "/{id}/file",
            async (
                Guid id,
                HttpRequest request,
                IBookRepository repo,
                IBookAssetStorage storage,
                MediaMetadataService metadataService,
                IBookTextIngestionScheduler bookTextScheduler,
                BookFileMutationGate fileMutationGate,
                CancellationToken ct
            ) =>
            {
                // Reject rather than queue behind an active acquisition; waiting
                // would turn a stale upload click into an unexpected replacement.
                using var fileLease = fileMutationGate.TryEnter(id);
                if (fileLease is null)
                    return Results.Conflict(new { code = "book_import_in_progress",
                        message = "Wait for this book's current import or upload to finish." });

                var book = await repo.GetByIdAsync(id);
                if (book is null)
                    return Results.NotFound();

                if (book is PhysicalBookModel)
                {
                    return Results.BadRequest(
                        "Physical books are metadata-only. Add the digital file as a separate edition."
                    );
                }

                if (book.Status is BookStatus.Downloading or BookStatus.Transcoding)
                    return Results.Conflict(new { code = "book_import_in_progress",
                        message = "This book is still importing. Wait until it finishes before uploading a file." });

                var form = await request.ReadFormAsync(ct);
                var file = form.Files.FirstOrDefault();
                if (file is null)
                    return Results.BadRequest("Missing file.");

                var allowed = BookAssetFormats.IsAllowedUpload(file.ContentType, file.FileName);
                if (!allowed)
                    return Results.BadRequest($"Unsupported file type: {file.ContentType}");

                await using (var metadataStream = file.OpenReadStream())
                {
                    metadataService.EnrichBookMetadata(book, metadataStream);
                }

                await using (var uploadStream = file.OpenReadStream())
                {
                    await storage.SaveBookFileAsync(id, uploadStream, file.FileName, ct);
                }

                // A book holds either one primary file or a track list, never
                // both. The uploaded file replaces the tracks; they are removed
                // only now that the replacement is safely stored.
                if (book.FileDetails.TracksJson is not null)
                {
                    book.FileDetails.TracksJson = null;
                    if (request.HttpContext.RequestServices.GetService<IBookTrackStorage>() is { } trackStorage)
                        await trackStorage.DeleteTracksAsync(id, ct);
                }

                book.FileDetails.HasFile = true;
                book.FileDetails.FileName = $"book{Path.GetExtension(file.FileName)}";
                // A local-upload row is incomplete until the canonical file
                // metadata is persisted. Success is the lifecycle boundary;
                // failed/interrupted attempts deliberately leave UploadPending.
                if (book.Status == BookStatus.UploadPending)
                {
                    book.Status = BookStatus.Ready;
                    book.StatusMessage = null;
                }

                // Clear old locations/chapters if a new file is uploaded
                book.FileDetails.LocationsJson = null;

                await repo.UpdateAsync(book);

                // Upload/replacement is a source-revision event. Scheduling
                // invalidates any old searchable revision immediately; the
                // scheduler deliberately degrades without failing the valid
                // primary file upload if extraction/indexing cannot start.
                await bookTextScheduler.ScheduleAsync(
                    id,
                    book.FileDetails.FileName,
                    ct);

                return Results.Ok(new { uploaded = true });
            }
        );

        // Stream file (inline) for media playback; supports HTTP Range requests
        group.MapGet(
            "/{id}/file",
            async (
                Guid id,
                IBookAssetStorage storage,
                HttpContext http,
                CancellationToken ct
            ) =>
                await StoredAssetHttpResult.CreateBookFileAsync(
                    http,
                    storage,
                    id,
                    attachment: false,
                    enableRanges: true,
                    cacheControl: "private, max-age=300",
                    ct)
        );

        // Download file (attachment) — used by the book detail "Download File" button.
        // A multi-track audiobook has no single file to hand over, so it is
        // delivered as one archive computed over its stored tracks.
        group.MapGet(
            "/{id}/file/download",
            async (
                Guid id,
                IBookAssetStorage storage,
                AudiobookPackageService packages,
                HttpContext http,
                CancellationToken ct
            ) =>
            {
                if (await packages.BuildAsync(id, ct) is { } package)
                {
                    return await StoredAssetHttpResult.CreateAsync(
                        http,
                        _ => Task.FromResult<StoredAssetInfo?>(package.Info(".zip", "application/zip")),
                        (range, _) => Task.FromResult<StoredAssetRead?>(
                            package.Open(".zip", "application/zip", range)),
                        attachment: true,
                        enableRanges: true,
                        cacheControl: null,
                        ct);
                }

                return await StoredAssetHttpResult.CreateBookFileAsync(
                    http,
                    storage,
                    id,
                    attachment: true,
                    enableRanges: true,
                    cacheControl: null,
                    ct);
            }
        );

        // Stream one track of a multi-track audiobook (inline) for playback;
        // supports HTTP Range requests like the single-file stream above.
        group.MapGet(
            "/{id}/tracks/{number:int}",
            async (
                Guid id,
                int number,
                HttpContext http,
                CancellationToken ct
            ) =>
                http.RequestServices.GetService<IBookTrackStorage>() is not { } tracks
                    ? Results.NotFound()
                    : await StoredAssetHttpResult.CreateAsync(
                        http,
                        token => tracks.GetTrackInfoAsync(id, number, token),
                        (range, token) => tracks.OpenTrackAsync(id, number, range, token),
                        attachment: false,
                        enableRanges: true,
                        cacheControl: "private, max-age=300",
                        ct)
        );

        // Download one track (attachment), named after its place and title in
        // the book rather than its storage name.
        group.MapGet(
            "/{id}/tracks/{number:int}/download",
            async (
                Guid id,
                int number,
                IBookRepository repo,
                HttpContext http,
                CancellationToken ct
            ) =>
            {
                if (http.RequestServices.GetService<IBookTrackStorage>() is not { } tracks)
                    return Results.NotFound();

                var book = await repo.GetByIdAsync(id);
                var list = BookTrackList.Parse(book?.FileDetails.TracksJson);
                var track = list.FirstOrDefault(candidate => candidate.Number == number);
                if (track is null)
                    return Results.NotFound();

                var downloadName = AudiobookPackageNaming.TrackEntryName(track, list.Count);
                return await StoredAssetHttpResult.CreateAsync(
                    http,
                    async token =>
                        await tracks.GetTrackInfoAsync(id, number, token) is { } info
                            ? info with { FileName = downloadName }
                            : null,
                    (range, token) => tracks.OpenTrackAsync(id, number, range, token),
                    attachment: true,
                    enableRanges: true,
                    cacheControl: null,
                    ct);
            }
        );

        // Upload cover
        uploadGroup.MapPost(
            "/{id}/cover",
            async (
                Guid id,
                HttpRequest request,
                IBookRepository repo,
                IBookAssetStorage storage,
                CancellationToken ct
            ) =>
            {
                var book = await repo.GetByIdAsync(id);
                if (book is null)
                    return Results.NotFound();

                if (request.ContentLength is > NostosProductRequestLimits.MaxCoverRequestBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                var bodySizeFeature = request.HttpContext.Features
                    .Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (bodySizeFeature is { IsReadOnly: false })
                {
                    bodySizeFeature.MaxRequestBodySize =
                        NostosProductRequestLimits.MaxCoverRequestBytes;
                }

                var form = await request.ReadFormAsync(ct);
                var file = form.Files.FirstOrDefault();
                if (file is null)
                    return Results.BadRequest("Missing cover file.");

                if (file.Length > NostosProductRequestLimits.MaxCoverUploadBytes)
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

                if (!BookAssetFormats.IsAllowedCoverUpload(file.ContentType, file.FileName))
                    return Results.BadRequest("Cover file type does not match a supported PNG or JPEG filename.");

                await using (var coverStream = file.OpenReadStream())
                {
                    await storage.SaveBookCoverAsync(id, coverStream, file.FileName, ct);
                }

                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                book.FileDetails.CoverFileName = $"cover{ext}";

                await repo.UpdateAsync(book);

                return Results.Ok(new { uploaded = true });
            }
        );

        // Download a cached, resized WebP cover for card/list views
        group.MapGet(
            "/{id}/cover/thumbnail",
            async (
                Guid id,
                int? width,
                IBookAssetStorage storage,
                HttpContext http,
                CancellationToken ct
            ) =>
            {
                var safeWidth = width ?? 320;
                return await StoredAssetHttpResult.CreateAsync(
                    http,
                    token => storage.GetBookCoverThumbnailInfoAsync(id, safeWidth, token),
                    (_, token) => storage.OpenBookCoverThumbnailAsync(id, safeWidth, token),
                    attachment: false,
                    enableRanges: false,
                    cacheControl: "public, max-age=86400, stale-while-revalidate=2592000",
                    ct);
            }
        );

        // Download cover
        group.MapGet(
            "/{id}/cover",
            async (
                Guid id,
                IBookAssetStorage storage,
                HttpContext http,
                CancellationToken ct
            ) =>
                await StoredAssetHttpResult.CreateAsync(
                    http,
                    token => storage.GetBookCoverInfoAsync(id, token),
                    (_, token) => storage.OpenBookCoverAsync(id, token),
                    attachment: false,
                    enableRanges: false,
                    cacheControl: "public, max-age=86400, stale-while-revalidate=2592000",
                    ct)
        );

        // DELETE cover
        group.MapDelete(
            "/{id}/cover",
            async (Guid id, IBookRepository repo, IBookAssetStorage storage, CancellationToken ct) =>
            {
                var book = await repo.GetByIdAsync(id);
                if (book is null)
                    return Results.NotFound();

                if (!await storage.DeleteCoverAsync(id, ct))
                    return Results.NotFound();

                book.FileDetails.CoverFileName = null;
                await repo.UpdateAsync(book);

                return Results.NoContent();
            }
        );

        // ISBN metadata lookup (validated before any external call)
        metadataGroup.MapGet(
            "/lookup/{isbn}",
            async (string isbn, BookLookupService service, CancellationToken ct) =>
            {
                var normalizedIsbn = BookIdentityNormalizer.NormalizeIsbn(isbn);
                if (normalizedIsbn is null)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Invalid ISBN",
                        detail: "Enter a valid ISBN-10 or ISBN-13.",
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = "invalid_isbn",
                        });
                }

                var outcome = await service.LookupCombinedDetailedAsync(normalizedIsbn, ct);
                if (outcome.Metadata is not null)
                    return Results.Ok(outcome.Metadata);

                if (outcome.Failed)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Book metadata services unavailable",
                        detail: "Book metadata services are temporarily unavailable. Please try again.",
                        extensions: new Dictionary<string, object?>
                        {
                            ["code"] = "book_metadata_unavailable",
                        });
                }

                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Book metadata not found",
                    detail: "No book metadata found for this ISBN.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "book_metadata_not_found",
                    });
            }
        );

        return routes;
    }

}
