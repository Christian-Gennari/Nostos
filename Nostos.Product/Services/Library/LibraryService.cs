using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Canonical library domain service (issue #34). REST endpoints, MCP tools and
/// the Angular UI all call this one service; it is the single writer for
/// books and collections. Mutations are exact-once through
/// LibraryCommandReceipt (separate from reading receipts on purpose).
/// </summary>
public sealed class LibraryService : ILibraryService
{
    private readonly IDbContextFactory<NostosDbContext> _contexts;
    private readonly LibraryReadService _reads;
    private readonly LibraryMutationExecutor _mutations;

    public LibraryService(IDbContextFactory<NostosDbContext> contexts, BookLookupService lookup)
    {
        _contexts = contexts;
        _reads = new LibraryReadService(contexts, lookup);
        _mutations = new LibraryMutationExecutor(contexts);
    }

    private DateTime Now => DateTime.UtcNow;

    // ------------------------------------------------------------------
    // Read-only surface
    // ------------------------------------------------------------------

    public Task<LibraryCommandResultDto> ListBooksAsync(
        BookFilter filter,
        BookSort sort,
        string? search,
        int page,
        int pageSize,
        Guid? collectionId,
        bool? groupByWork = false,
        string? format = null,
        CancellationToken ct = default) =>
        _reads.ListBooksAsync(filter, sort, search, page, pageSize, collectionId, groupByWork, format, ct);

    public Task<LibraryCommandResultDto> GetStatusCountsAsync(CancellationToken ct = default) =>
        _reads.GetStatusCountsAsync(ct);

    public Task<LibraryCommandResultDto> GetBookAsync(Guid bookId, CancellationToken ct = default) =>
        _reads.GetBookAsync(bookId, ct);

    public Task<LibraryResolveResult> ResolveBookAsync(
        LibraryResolveBookRequest request,
        CancellationToken ct = default) =>
        _reads.ResolveBookAsync(request, ct);

    public Task<LibraryCommandResultDto> ListCollectionsAsync(CancellationToken ct = default) =>
        _reads.ListCollectionsAsync(ct);

    public Task<LibraryCommandResultDto> ListCollectionCountsAsync(CancellationToken ct = default) =>
        _reads.ListCollectionCountsAsync(ct);

    public Task<LibraryCommandResultDto> GetCollectionAsync(
        Guid collectionId,
        CancellationToken ct = default) =>
        _reads.GetCollectionAsync(collectionId, ct);

    // ------------------------------------------------------------------
    // Mutations (exact-once)
    // ------------------------------------------------------------------

    public Task<LibraryCommandResultDto> CreateOrMatchBookAsync(
        LibraryCreateBookRequest request,
        bool strictConfirmation,
        CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "CreateOrMatchBook",
            (db, token) => CreateOrMatchCoreAsync(db, request, strictConfirmation, token), ct);

    public Task<LibraryCommandResultDto> UpdateBookAsync(LibraryUpdateBookRequest request, CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "UpdateBook",
            (db, token) => UpdateBookCoreAsync(db, request, token), ct);

    public Task<LibraryCommandResultDto> AttachAcquiredAssetAsync(
        LibraryAttachAcquiredAssetRequest request,
        CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "AttachAcquiredAsset",
            (db, token) => AttachAcquiredAssetCoreAsync(db, request, token), ct);

    public Task<LibraryCommandResultDto> LinkWorkAsync(LibraryLinkWorkRequest request, CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "LinkWork",
            (db, token) => LinkWorkCoreAsync(db, request, token), ct);

    public Task<LibraryCommandResultDto> UnlinkWorkAsync(LibraryUnlinkWorkRequest request, CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "UnlinkWork",
            (db, token) => UnlinkWorkCoreAsync(db, request, token), ct);

    public async Task<LibraryCommandResultDto> UpdateProgressAsync(
        Guid bookId,
        string location,
        int percentage,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await EnsureStateAsync(db, ct);

        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == bookId, ct);
        if (book is null)
            return Failure("book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion);

        var clamped = Math.Clamp(percentage, 0, 100);
        book.Progress.LastLocation = string.IsNullOrWhiteSpace(location) ? null : location;
        book.Progress.ProgressPercent = clamped;
        book.Progress.LastReadAt = Now;

        // Keep FinishedAt aligned with the percentage (regression fix: a
        // finished book that is read again becomes unfinished).
        if (clamped >= 100)
            book.Progress.FinishedAt ??= Now;
        else if (book.Progress.FinishedAt is not null)
            book.Progress.FinishedAt = null;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var version = await LibraryRevision.AdvanceAndGetAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Result(LibraryReplyFormatter.ProgressUpdated(book.Title), new { updated = true }, version);
    }

    public async Task<LibraryCommandResultDto> ResetProgressAsync(Guid bookId, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await EnsureStateAsync(db, ct);

        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == bookId, ct);
        if (book is null)
            return Failure("book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion);

        // Canonical reset state; an already-reset book is a successful no-op
        // (no state-version bump).
        var progress = book.Progress;
        var alreadyReset = progress.LastLocation is null
            && progress.ProgressPercent == 0
            && progress.FinishedAt is null
            && progress.LastReadAt is null;

        if (alreadyReset)
            return Result(LibraryReplyFormatter.ProgressReset(book.Title), new { updated = false }, state.StateVersion);

        progress.LastLocation = null;
        progress.ProgressPercent = 0;
        progress.FinishedAt = null;
        progress.LastReadAt = null;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var version = await LibraryRevision.AdvanceAndGetAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Result(LibraryReplyFormatter.ProgressReset(book.Title), new { updated = true }, version);
    }

    public async Task<LibraryCommandResultDto> DeleteBookAsync(Guid bookId, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await EnsureStateAsync(db, ct);

        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == bookId, ct);
        if (book is null)
            return Failure("book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var version = await LibraryRevision.AdvanceAndGetAsync(db, ct);
        db.Books.Remove(book);
        try
        {
            await db.SaveChangesAsync(ct);

            if (book.WorkId != Guid.Empty)
            {
                var remainingInWork = await db.Books.AnyAsync(b => b.WorkId == book.WorkId, ct);
                if (!remainingInWork)
                {
                    var work = await db.Works.FindAsync(new object[] { book.WorkId }, ct);
                    if (work is not null)
                    {
                        db.Works.Remove(work);
                        await db.SaveChangesAsync(ct);
                    }
                }
            }
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            return Failure("book_in_use", LibraryReplyFormatter.BookInUse(book.Title), state.StateVersion);
        }

        await transaction.CommitAsync(ct);
        return Result(LibraryReplyFormatter.BookDeleted(book.Title), new { deleted = true }, version);
    }

    public async Task<LibraryCommandResultDto> SetBookStatusAsync(
        Guid bookId,
        BookStatus status,
        string? statusMessage = null,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await EnsureStateAsync(db, ct);

        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == bookId, ct);
        if (book is null)
            return Failure("book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion);

        book.Status = status;
        book.StatusMessage = NullIfEmpty(statusMessage);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var version = await LibraryRevision.AdvanceAndGetAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Result("Book status updated.", new { updated = true }, version);
    }

    public Task<LibraryCommandResultDto> CreateCollectionAsync(LibraryCreateCollectionRequest request, CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "CreateCollection",
            (db, token) => CreateCollectionCoreAsync(db, request, token), ct);

    public Task<LibraryCommandResultDto> RenameCollectionAsync(LibraryRenameCollectionRequest request, CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "RenameCollection",
            (db, token) => RenameCollectionCoreAsync(db, request, token), ct);

    public Task<LibraryCommandResultDto> MoveCollectionAsync(LibraryMoveCollectionRequest request, CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "MoveCollection",
            (db, token) => MoveCollectionCoreAsync(db, request, token), ct);

    public Task<LibraryCommandResultDto> UpdateCollectionAsync(
        string clientId,
        string idempotencyKey,
        Guid collectionId,
        string name,
        Guid? parentId,
        CancellationToken ct = default) =>
        _mutations.ExecuteAsync(clientId, idempotencyKey, "UpdateCollection",
            (db, token) => UpdateCollectionCoreAsync(db, collectionId, name, parentId,
                treatUnchangedAsNoOp: true, token), ct);

    public Task<LibraryCommandResultDto> DeleteCollectionAsync(LibraryDeleteCollectionRequest request, CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "DeleteCollection",
            (db, token) => DeleteCollectionCoreAsync(db, request, token), ct);

    private async Task<(bool DidChange, LibraryCommandResultDto Result)> CreateOrMatchCoreAsync(
        NostosDbContext db,
        LibraryCreateBookRequest request,
        bool strictConfirmation,
        CancellationToken ct)
    {
        var state = await EnsureStateAsync(db, ct);

        if (string.IsNullOrWhiteSpace(request.Title))
            return NoChange(Failure("invalid_book_identity", "Title is required.", state.StateVersion));

        var nIsbn = BookIdentityNormalizer.NormalizeIsbn(request.Isbn);
        var nAsin = BookIdentityNormalizer.NormalizeAsin(request.Asin);
        var nTitle = BookIdentityNormalizer.NormalizeTitle(request.Title);
        var nAuthor = BookIdentityNormalizer.NormalizeAuthor(request.Author);

        // Type and identifier/type consistency: an identifier the target
        // model cannot store must be rejected, never silently dropped after
        // being used for matching (strong-identity contract). The unknown-type
        // check runs first; identifier/type consistency runs AFTER identity
        // resolution so exact identifier matches (and identity_conflict) stay
        // reachable.
        var normalizedType = string.IsNullOrWhiteSpace(request.Type) ? "physical" : request.Type.Trim().ToLowerInvariant();
        if (normalizedType is not ("physical" or "ebook" or "audiobook"))
            return NoChange(Failure("invalid_book_identity",
                $"Unknown book type: {request.Type}. Use physical, ebook, or audiobook.", state.StateVersion));

        // 1. Explicit confirmation: use the caller-selected existing row.
        if (request.ConfirmedBookId.HasValue)
        {
            var confirmed = await db.Books.AsNoTracking()
                .Include(b => b.Acquisition)
                .Include(b => b.BookCollections)
                .SingleOrDefaultAsync(b => b.Id == request.ConfirmedBookId.Value, ct);
            if (confirmed is null)
                return NoChange(Failure("book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

            return NoChange(Result(
                LibraryReplyFormatter.BookMatched(confirmed.Title),
                new LibraryCreateOrMatchResultDto("matched", confirmed.Id, confirmed.ToDto()),
                state.StateVersion));
        }

        // 2. Identifier resolution.
        BookModel? isbnMatch = null;
        BookModel? asinMatch = null;
        if (nIsbn is not null)
            isbnMatch = await db.Books.AsNoTracking()
                .Include(b => b.Acquisition)
                .Include(b => b.BookCollections)
                .SingleOrDefaultAsync(b => b.NormalizedIsbn == nIsbn, ct);
        if (nAsin is not null)
            asinMatch = await db.Books.AsNoTracking()
                .Include(b => b.Acquisition)
                .Include(b => b.BookCollections)
                .SingleOrDefaultAsync(b => b.NormalizedAsin == nAsin, ct);

        if (isbnMatch is not null && asinMatch is not null && isbnMatch.Id != asinMatch.Id)
            return NoChange(Failure("identity_conflict",
                LibraryReplyFormatter.IdentityConflict, state.StateVersion));

        if (isbnMatch is not null)
            return NoChange(Result(
                LibraryReplyFormatter.BookMatched(isbnMatch.Title),
                new LibraryCreateOrMatchResultDto("matched", isbnMatch.Id, isbnMatch.ToDto()),
                state.StateVersion));
        if (asinMatch is not null)
            return NoChange(Result(
                LibraryReplyFormatter.BookMatched(asinMatch.Title),
                new LibraryCreateOrMatchResultDto("matched", asinMatch.Id, asinMatch.ToDto()),
                state.StateVersion));

        // Identifier/type consistency (after resolution, before creation): a
        // fresh audiobook with an ISBN, or a physical/ebook with an ASIN, is
        // rejected rather than silently dropping the identifier.
        if (normalizedType == "audiobook" && nIsbn is not null)
            return NoChange(Failure("invalid_book_identity",
                "Audiobooks use ASIN, not ISBN.", state.StateVersion));
        if (normalizedType != "audiobook" && nAsin is not null)
            return NoChange(Failure("invalid_book_identity",
                "Only audiobooks use ASIN.", state.StateVersion));

        // 3. Title/author resolution. forceCreate bypasses ambiguity; exact
        //    identifier matches above always win over forceCreate. Exact
        //    matching requires BOTH title and author: title-only requests
        //    yield candidates below, never an automatic match.
        Guid? targetWorkId = null;
        if (!request.ForceCreate && !string.IsNullOrEmpty(nTitle))
        {
            var all = await db.Books.AsNoTracking()
                .Include(b => b.Acquisition)
                .Include(b => b.BookCollections)
                .ToListAsync(ct);
            var hasAuthor = !string.IsNullOrEmpty(nAuthor);
            var exact = hasAuthor
                ? all
                    .Where(b =>
                        string.Equals(BookIdentityNormalizer.NormalizeTitle(b.Title), nTitle, StringComparison.Ordinal) &&
                        string.Equals(BookIdentityNormalizer.NormalizeAuthor(b.Author), nAuthor, StringComparison.Ordinal))
                    .ToList()
                : [];

            if (exact.Count > 0)
            {
                targetWorkId = exact[0].WorkId;

                var sameTypeEdition = exact.FirstOrDefault(b =>
                    (normalizedType == "audiobook" && b is AudioBookModel) ||
                    (normalizedType == "ebook" && b is EBookModel) ||
                    (normalizedType == "physical" && b is PhysicalBookModel));

                if (sameTypeEdition != null)
                {
                    var sameTypeCandidates = exact.Where(b => b.GetType() == sameTypeEdition.GetType()).ToList();
                    if (sameTypeCandidates.Count > 1)
                    {
                        if (strictConfirmation)
                        {
                            var candidates = sameTypeCandidates
                                .Select(b => ToCandidate(b, "exact title" + (string.IsNullOrEmpty(nAuthor) ? "" : "+author match")))
                                .ToList();
                            return NoChange(Failure("confirmation_required",
                                LibraryReplyFormatter.ConfirmationRequired(candidates.Count),
                                state.StateVersion, candidates));
                        }
                        // Non-strict (legacy REST path): multiple same-type editions exist; create anyway as before.
                    }
                    else
                    {
                        return NoChange(Result(
                            LibraryReplyFormatter.BookMatched(sameTypeEdition.Title),
                            new LibraryCreateOrMatchResultDto("matched", sameTypeEdition.Id, sameTypeEdition.ToDto()),
                            state.StateVersion));
                    }
                }

                // If same work exists but in different format/type:
                // We do not return matched! We proceed to create the new edition attached to targetWorkId.
            }
            else if (string.IsNullOrEmpty(nAuthor) && strictConfirmation)
            {
                // Title-only with no exact match: fuzzy candidates before
                // guessing. Non-strict legacy path creates (old UI behavior).
                var fuzzy = all
                    .Where(b =>
                    {
                        var bt = BookIdentityNormalizer.NormalizeTitle(b.Title);
                        return !string.IsNullOrEmpty(bt) && bt.Contains(nTitle, StringComparison.Ordinal);
                    })
                    .Select(b => ToCandidate(b, "fuzzy title match"))
                    .ToList();
                if (fuzzy.Count > 0)
                    return NoChange(Failure("confirmation_required",
                        LibraryReplyFormatter.ConfirmationRequired(fuzzy.Count),
                        state.StateVersion, fuzzy));
            }
        }

        // 4. No safe match: create (requires valid identifier, title+author,
        //    or explicit forceCreate; a bare title in strict mode needs more
        //    information).
        if (strictConfirmation && nIsbn is null && nAsin is null && string.IsNullOrEmpty(nAuthor) && !request.ForceCreate)
            return NoChange(Failure("confirmation_required",
                LibraryReplyFormatter.MoreInformationRequired, state.StateVersion));

        if (request.CollectionId.HasValue)
        {
            var collectionExists = await db.Collections.AsNoTracking()
                .AnyAsync(c => c.Id == request.CollectionId.Value, ct);
            if (!collectionExists)
                return NoChange(Failure("collection_not_found",
                    LibraryReplyFormatter.CollectionNotFound, state.StateVersion));
        }

        // Create-time membership. CollectionIds (the set) is authoritative when
        // supplied; the singular CollectionId is the legacy MCP/REST shape and is
        // still honoured so no existing caller changes behaviour. The join rows
        // are added after SaveChanges, once the book id exists.
        var membershipIds = request.CollectionIds is { Count: > 0 }
            ? request.CollectionIds.Distinct().ToList()
            : (request.CollectionId.HasValue ? [request.CollectionId.Value] : []);

        var createDto = new CreateBookDto(
            request.Type,
            request.Title,
            request.Subtitle,
            request.Author,
            request.Editor,
            request.Translator,
            request.Narrator,
            request.Description,
            request.Isbn,
            request.Asin,
            request.Duration,
            request.Publisher,
            request.PlaceOfPublication,
            request.PublishedDate,
            request.Edition,
            request.PageCount,
            request.Language,
            request.Categories,
            request.Series,
            request.VolumeNumber,
            request.Rating,
            request.IsFavorite,
            request.PersonalReview,
            request.FinishedAt);

        var model = createDto.ToModel();
        // Local-file creation is a two-step lifecycle: create metadata, then
        // POST /file. The explicit intent distinguishes an abandoned upload
        // from a valid metadata-only ebook/audiobook; never infer this from
        // HasFile=false because that is a supported steady state.
        if (request.FileUploadExpected)
        {
            model.Status = BookStatus.UploadPending;
            model.StatusMessage = "Waiting for local file upload.";
        }

        var (normIsbn, normAsin) = LibraryIdentityBackfill.ComputeNormalizedIdentity(model);
        model.NormalizedIsbn = normIsbn;
        model.NormalizedAsin = normAsin;

        if (targetWorkId.HasValue && targetWorkId.Value != Guid.Empty)
        {
            model.WorkId = targetWorkId.Value;
        }
        else
        {
            var work = await db.Works.FirstOrDefaultAsync(w =>
                w.NormalizedTitle == nTitle &&
                w.NormalizedAuthor == (string.IsNullOrEmpty(nAuthor) ? null : nAuthor), ct);

            if (work is null)
            {
                work = new WorkModel
                {
                    Id = Guid.NewGuid(),
                    Title = request.Title,
                    Author = request.Author,
                    NormalizedTitle = nTitle,
                    NormalizedAuthor = string.IsNullOrEmpty(nAuthor) ? null : nAuthor,
                    CreatedAt = DateTime.UtcNow,
                };
                db.Works.Add(work);
            }

            model.WorkId = work.Id;
            model.Work = work;
        }

        db.Books.Add(model);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Multi-process race on the filtered unique indexes: another
            // process committed the same ISBN/ASIN between our pre-check and
            // insert. Convert to a typed conflict, never a 500.
            var raced = await FindByIdentifiersAsync(db, nIsbn, nAsin, ct);
            if (raced is not null)
                return NoChange(Failure("duplicate_identifier",
                    LibraryReplyFormatter.DuplicateIdentifier(raced.Title), state.StateVersion));
            throw;
        }

        if (membershipIds.Count > 0)
        {
            var known = await db.Collections.AsNoTracking()
                .Where(c => membershipIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(ct);
            if (known.Count != membershipIds.Count)
                return NoChange(Failure("collection_not_found",
                    LibraryReplyFormatter.CollectionNotFound, state.StateVersion));

            foreach (var id in membershipIds)
                db.BookCollections.Add(new BookCollectionModel
                {
                    BookId = model.Id,
                    CollectionId = id,
                    AddedAt = model.CreatedAt,
                });

            await db.SaveChangesAsync(ct);
            model.BookCollections = membershipIds
                .Select(id => new BookCollectionModel { BookId = model.Id, CollectionId = id })
                .ToList();
        }

        return Change(Result(
            LibraryReplyFormatter.BookCreated(model.Title),
            new LibraryCreateOrMatchResultDto("created", model.Id, model.ToDto()),
            state.StateVersion));
    }

    private async Task<(bool DidChange, LibraryCommandResultDto Result)> AttachAcquiredAssetCoreAsync(
        NostosDbContext db,
        LibraryAttachAcquiredAssetRequest request,
        CancellationToken ct)
    {
        var state = await EnsureStateAsync(db, ct);

        // A bare file name only. Anything with a directory component would let
        // the caller choose where a book's file is read from.
        var fileName = request.FileName?.Trim() ?? string.Empty;
        if (fileName.Length is 0 or > 64
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return NoChange(Failure("invalid_file_name",
                "The stored file name must be a bare file name.", state.StateVersion));
        }

        if (string.IsNullOrWhiteSpace(request.ProviderId)
            || string.IsNullOrWhiteSpace(request.ExternalId)
            || string.IsNullOrWhiteSpace(request.AssetId))
        {
            return NoChange(Failure("invalid_provenance",
                "Provider id, external id and asset id are required.", state.StateVersion));
        }

        var book = await db.Books
            .Include(b => b.BookCollections)
            .Include(b => b.Acquisition)
            .SingleOrDefaultAsync(b => b.Id == request.BookId, ct);

        if (book is null)
            return NoChange(Failure("book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

        // Provenance is unique per (provider, item, asset). Re-attaching the
        // same asset to the same book is a successful no-op; attaching it to a
        // different book is a typed conflict rather than a second row that
        // would make "which book did this come from" ambiguous.
        var alreadyAcquired = await db.BookAcquisitions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                a => a.ProviderId == request.ProviderId
                     && a.ExternalId == request.ExternalId
                     && a.AssetId == request.AssetId,
                ct);

        if (alreadyAcquired is not null)
        {
            if (alreadyAcquired.BookId != book.Id)
                return NoChange(Failure("acquisition_conflict",
                    LibraryReplyFormatter.AcquisitionConflict, state.StateVersion));

            return NoChange(Result(
                LibraryReplyFormatter.AssetAlreadyAttached(book.Title),
                new LibraryAttachAcquiredAssetResultDto(book.Id, book.ToDto()),
                state.StateVersion));
        }

        book.FileDetails.HasFile = true;
        book.FileDetails.FileName = fileName;
        // The cover is written to disk by the importer, so the row has to be told
        // its name: the cover URL is derived from this column, and writing only
        // the file left imported books with a cover nothing could display.
        if (!string.IsNullOrWhiteSpace(request.CoverFileName))
            book.FileDetails.CoverFileName = request.CoverFileName;
        // A different file invalidates the cached epub locations, exactly as a
        // manual re-upload does.
        book.FileDetails.LocationsJson = null;
        book.FileDetails.ChaptersJson = request.Chapters is { Count: > 0 }
            ? JsonSerializer.Serialize(request.Chapters)
            : null;

        // Duration only exists on audiobooks, and only an audiobook can carry
        // it — an ebook that somehow received one would be a modelling error.
        if (book is AudioBookModel audioBook && !string.IsNullOrWhiteSpace(request.Duration))
            audioBook.Duration = request.Duration;

        // Written in the same SaveChanges as the file details above: the book
        // must never claim a file it has no provenance for, or provenance
        // pointing at a file that was never attached.
        // Explicitly added rather than only assigned to the navigation: EF's
        // change detection discovers an untracked one-to-one dependent through
        // navigation fixup, but with a non-default key it concludes the row
        // already exists and issues an UPDATE — which matches nothing and fails
        // as a concurrency error.
        book.Status = BookStatus.Ready;
        book.StatusMessage = null;

        var acquisition = new BookAcquisitionModel
        {
            BookId = book.Id,
            ProviderId = request.ProviderId.Trim(),
            ProviderDisplayName = NullIfEmpty(request.ProviderDisplayName) ?? request.ProviderId.Trim(),
            ExternalId = request.ExternalId.Trim(),
            AssetId = request.AssetId.Trim(),
            AssetFormat = NullIfEmpty(request.AssetFormat),
            ImportedExtension = Path.GetExtension(fileName).ToLowerInvariant(),
            SourceUrl = NullIfEmpty(request.SourceUrl),
            RightsStatement = NullIfEmpty(request.RightsStatement),
            AcquiredAt = request.AcquiredAt ?? Now,
        };

        book.Acquisition = acquisition;
        db.BookAcquisitions.Add(acquisition);

        await db.SaveChangesAsync(ct);

        return Change(Result(
            LibraryReplyFormatter.AssetAttached(book.Title),
            new LibraryAttachAcquiredAssetResultDto(book.Id, book.ToDto()),
            state.StateVersion));
    }

    private async Task<(bool DidChange, LibraryCommandResultDto Result)> UpdateBookCoreAsync(
        NostosDbContext db,
        LibraryUpdateBookRequest request,
        CancellationToken ct)
    {
        var state = await EnsureStateAsync(db, ct);

        // Loaded so the returned BookDto reports real membership even when
        // this update does not touch it.
        var book = await db.Books
            .Include(b => b.BookCollections)
            .SingleOrDefaultAsync(b => b.Id == request.BookId, ct);
        if (book is null)
            return NoChange(Failure("book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

        if (request.Title is not null && string.IsNullOrWhiteSpace(request.Title))
            return NoChange(Failure("invalid_book_identity", "Title cannot be empty.", state.StateVersion));

        // Identifier/type consistency: identifiers the model cannot store are
        // rejected, never silently dropped.
        if (book is AudioBookModel && !string.IsNullOrWhiteSpace(request.Isbn))
            return NoChange(Failure("invalid_book_identity",
                "Audiobooks use ASIN, not ISBN.", state.StateVersion));
        if (book is not AudioBookModel && !string.IsNullOrWhiteSpace(request.Asin))
            return NoChange(Failure("invalid_book_identity",
                "Only audiobooks use ASIN.", state.StateVersion));

        // Null = leave unchanged; empty string = clear (repo convention).
        if (request.Title is not null)
            book.Title = request.Title;
        if (request.Author is not null)
            book.Author = NullIfEmpty(request.Author);
        if (request.Subtitle is not null)
            book.Metadata.Subtitle = NullIfEmpty(request.Subtitle);
        if (request.Editor is not null)
            book.Metadata.Editor = NullIfEmpty(request.Editor);
        if (request.Translator is not null)
            book.Metadata.Translator = NullIfEmpty(request.Translator);
        if (request.Description is not null)
            book.Metadata.Description = NullIfEmpty(request.Description);
        if (request.Publisher is not null)
            book.Metadata.Publisher = NullIfEmpty(request.Publisher);
        if (request.PlaceOfPublication is not null)
            book.Metadata.PlaceOfPublication = NullIfEmpty(request.PlaceOfPublication);
        if (request.PublishedDate is not null)
            book.Metadata.PublishedDate = NullIfEmpty(request.PublishedDate);
        if (request.Edition is not null)
            book.Metadata.Edition = NullIfEmpty(request.Edition);
        if (request.Language is not null)
            book.Metadata.Language = NullIfEmpty(request.Language);
        if (request.Categories is not null)
            book.Metadata.Categories = NullIfEmpty(request.Categories);
        if (request.Series is not null)
            book.Metadata.Series = NullIfEmpty(request.Series);
        if (request.VolumeNumber is not null)
            book.Metadata.VolumeNumber = NullIfEmpty(request.VolumeNumber);

        if (request.Rating.HasValue)
            book.Progress.Rating = Math.Clamp(request.Rating.Value, 0, 5);
        if (request.IsFavorite.HasValue)
            book.Progress.IsFavorite = request.IsFavorite.Value;
        if (request.PersonalReview is not null)
            book.Progress.PersonalReview = NullIfEmpty(request.PersonalReview);

        // Finished semantics mirror UpdateBookDto.Apply.
        if (request.FinishedAt is not null)
        {
            book.Progress.FinishedAt = request.FinishedAt;
        }
        else if (request.IsFinished.HasValue)
        {
            if (request.IsFinished.Value)
            {
                book.Progress.FinishedAt ??= Now;
                book.Progress.ProgressPercent = 100;
            }
            else
            {
                book.Progress.FinishedAt = null;
            }
        }

        // --- COLLECTION MEMBERSHIP ---
        // CollectionIds (the full replacement set) is the authoritative write.
        // The singular CollectionId / ClearCollection pair is the legacy
        // contract, kept working by translating it into the same set operation
        // so the two can never disagree.
        if (request.CollectionIds is not null)
        {
            var wanted = request.CollectionIds.Distinct().ToList();

            var known = await db.Collections.AsNoTracking()
                .Where(c => wanted.Contains(c.Id))
                .Select(c => c.Id)
                .ToListAsync(ct);
            if (known.Count != wanted.Count)
                return NoChange(Failure("collection_not_found",
                    LibraryReplyFormatter.CollectionNotFound, state.StateVersion));

            var current = await db.BookCollections
                .Where(bc => bc.BookId == book.Id)
                .ToListAsync(ct);

            db.BookCollections.RemoveRange(
                current.Where(bc => !wanted.Contains(bc.CollectionId)));
            foreach (var id in wanted.Where(id => current.All(bc => bc.CollectionId != id)))
                db.BookCollections.Add(new BookCollectionModel
                {
                    BookId = book.Id,
                    CollectionId = id,
                });
        }
        else if (request.CollectionId.HasValue)
        {
            var collectionExists = await db.Collections.AsNoTracking()
                .AnyAsync(c => c.Id == request.CollectionId.Value, ct);
            if (!collectionExists)
                return NoChange(Failure("collection_not_found",
                    LibraryReplyFormatter.CollectionNotFound, state.StateVersion));

            // Legacy singular set: replace whatever membership exists with this
            // one collection. (The MCP surface is frozen on this shape, so the
            // translation lives here rather than in the tool signature.)
            var current = await db.BookCollections
                .Where(bc => bc.BookId == book.Id)
                .ToListAsync(ct);
            db.BookCollections.RemoveRange(current);
            db.BookCollections.Add(new BookCollectionModel
            {
                BookId = book.Id,
                CollectionId = request.CollectionId.Value,
            });
        }
        else if (request.ClearCollection)
        {
            var current = await db.BookCollections
                .Where(bc => bc.BookId == book.Id)
                .ToListAsync(ct);
            db.BookCollections.RemoveRange(current);
        }

        switch (book)
        {
            case PhysicalBookModel p:
                if (request.Isbn is not null)
                    p.Isbn = NullIfEmpty(request.Isbn);
                if (request.PageCount.HasValue)
                    p.PageCount = request.PageCount;
                break;
            case EBookModel e:
                if (request.Isbn is not null)
                    e.Isbn = NullIfEmpty(request.Isbn);
                if (request.PageCount.HasValue)
                    e.PageCount = request.PageCount;
                break;
            case AudioBookModel a:
                if (request.Asin is not null)
                    a.Asin = NullIfEmpty(request.Asin);
                if (request.Duration is not null)
                    a.Duration = NullIfEmpty(request.Duration);
                if (request.Narrator is not null)
                    a.Narrator = NullIfEmpty(request.Narrator);
                break;
        }

        // Recompute normalized identity after possible identifier changes.
        var (nIsbn, nAsin) = LibraryIdentityBackfill.ComputeNormalizedIdentity(book);
        book.NormalizedIsbn = nIsbn;
        book.NormalizedAsin = nAsin;

        // Conflict check: another book holds the same normalized identity.
        // Runs through a FRESH context: the current context carries tracked
        // pending changes, and EF's query pipeline must never be trusted to
        // see the pre-change database state for the same entity graph.
        if (nIsbn is not null || nAsin is not null)
        {
            await using var probeDb = await _contexts.CreateDbContextAsync(ct);
            var conflict = await probeDb.Books.AsNoTracking()
                .Where(b => b.Id != book.Id &&
                            ((nIsbn != null && b.NormalizedIsbn == nIsbn) ||
                             (nAsin != null && b.NormalizedAsin == nAsin)))
                .Select(b => new { b.Id, b.Title, b.NormalizedIsbn, b.NormalizedAsin })
                .ToListAsync(ct);
            if (conflict.Count > 0)
                return NoChange(Failure("duplicate_identifier",
                    LibraryReplyFormatter.DuplicateIdentifier(conflict[0].Title), state.StateVersion));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another writer committed the identifier between the probe and
            // this save; the filtered unique index is the final guard. Convert
            // to a typed conflict, never a 500 (mirror of the create path).
            await using var probeDb = await _contexts.CreateDbContextAsync(ct);
            var raced = await probeDb.Books.AsNoTracking()
                .Where(b => b.Id != book.Id &&
                            ((nIsbn != null && b.NormalizedIsbn == nIsbn) ||
                             (nAsin != null && b.NormalizedAsin == nAsin)))
                .Select(b => new { b.Title })
                .ToListAsync(ct);
            if (raced.Count > 0)
                return NoChange(Failure("duplicate_identifier",
                    LibraryReplyFormatter.DuplicateIdentifier(raced[0].Title), state.StateVersion));
            throw;
        }

        return Change(Result(
            LibraryReplyFormatter.BookUpdated(book.Title),
            book.ToDto(),
            state.StateVersion));
    }

    // ------------------------------------------------------------------
    // Work membership (multi-edition grouping): the manual override
    // ------------------------------------------------------------------

    /// <summary>
    /// Merge <c>BookId</c>'s work into <c>TargetBookId</c>'s work.
    ///
    /// Group semantics, deliberately: the target's work survives as the merged
    /// group's identity and EVERY book from the source group is moved onto it,
    /// rather than only the two named books. Moving just the named book would
    /// silently strip one edition out of an already-valid group — a side effect
    /// the user did not ask for.
    ///
    /// Two book-level rows are never touched except for <c>WorkId</c>: files,
    /// progress, notes, ratings/reviews, metadata and collection membership all
    /// belong to the book and stay exactly as they were.
    /// </summary>
    private async Task<(bool DidChange, LibraryCommandResultDto Result)> LinkWorkCoreAsync(
        NostosDbContext db,
        LibraryLinkWorkRequest request,
        CancellationToken ct)
    {
        var state = await EnsureStateAsync(db, ct);

        if (request.BookId == request.TargetBookId)
            return NoChange(Failure("invalid_work_link",
                "A book cannot be linked to itself.", state.StateVersion));

        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == request.BookId, ct);
        if (book is null)
            return NoChange(Failure("book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

        var target = await db.Books.SingleOrDefaultAsync(b => b.Id == request.TargetBookId, ct);
        if (target is null)
            return NoChange(Failure("target_book_not_found",
                LibraryReplyFormatter.BookNotFound, state.StateVersion));

        // Already grouped (in either direction, and including a replayed
        // request with the arguments swapped): a successful no-op, so the
        // operation is idempotent regardless of how the client orders the pair.
        if (book.WorkId == target.WorkId)
            return NoChange(Result(
                LibraryReplyFormatter.WorkAlreadyLinked(book.Title, target.Title),
                await BuildWorkMembershipResultAsync(db, book, removedWorkId: null, ct),
                state.StateVersion));

        var sourceWorkId = book.WorkId;
        var survivorWorkId = target.WorkId;

        // Fix up every member of the source group, not just the named book.
        var sourceMembers = await db.Books
            .Where(b => b.WorkId == sourceWorkId)
            .ToListAsync(ct);
        foreach (var member in sourceMembers)
            member.WorkId = survivorWorkId;

        // Remove the source work once nothing references it. Its row carries no
        // book-level data, so nothing is lost: the group's remaining identity is
        // the survivor's own title/author. The books are updated first so the
        // FK is already re-pointed when the delete runs.
        var sourceWork = await db.Works.SingleOrDefaultAsync(w => w.Id == sourceWorkId, ct);
        Guid? removedWorkId = null;
        if (sourceWork is not null)
        {
            await db.SaveChangesAsync(ct);
            var stillReferenced = await db.Books.AnyAsync(b => b.WorkId == sourceWorkId, ct);
            if (!stillReferenced)
            {
                db.Works.Remove(sourceWork);
                removedWorkId = sourceWorkId;
            }
        }

        await db.SaveChangesAsync(ct);

        return Change(Result(
            LibraryReplyFormatter.WorkLinked(book.Title, target.Title),
            await BuildWorkMembershipResultAsync(db, book, removedWorkId, ct),
            state.StateVersion));
    }

    /// <summary>
    /// Split <c>BookId</c> out of its work into a fresh single-book work built
    /// from the book's OWN current title/author identity. The book therefore
    /// always ends up with a valid work id; an unlinked-to-nothing state is not
    /// representable.
    /// </summary>
    private async Task<(bool DidChange, LibraryCommandResultDto Result)> UnlinkWorkCoreAsync(
        NostosDbContext db,
        LibraryUnlinkWorkRequest request,
        CancellationToken ct)
    {
        var state = await EnsureStateAsync(db, ct);

        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == request.BookId, ct);
        if (book is null)
            return NoChange(Failure("book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

        var previousWorkId = book.WorkId;
        var remaining = await db.Books.CountAsync(b => b.WorkId == previousWorkId && b.Id != book.Id, ct);

        // Nothing to split: the book is already the only member of its work.
        if (remaining == 0)
            return NoChange(Result(
                LibraryReplyFormatter.WorkAlreadyStandalone(book.Title),
                await BuildWorkMembershipResultAsync(db, book, removedWorkId: null, ct),
                state.StateVersion));

        // The book's own identity, NOT the old work's: that work exists to
        // describe the group, and copying its title onto a single book would
        // label the split-off edition with whichever sibling happened to be the
        // group's representative.
        var newWork = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = book.Title,
            Author = book.Author,
            NormalizedTitle = BookIdentityNormalizer.NormalizeTitle(book.Title),
            NormalizedAuthor = BookIdentityNormalizer.NormalizeAuthor(book.Author),
            CreatedAt = Now,
        };
        db.Works.Add(newWork);

        book.WorkId = newWork.Id;
        book.Work = newWork;
        await db.SaveChangesAsync(ct);

        return Change(Result(
            LibraryReplyFormatter.WorkUnlinked(book.Title),
            await BuildWorkMembershipResultAsync(db, book, removedWorkId: null, ct),
            state.StateVersion));
    }

    /// <summary>
    /// Post-mutation membership snapshot for the command payload. Read after the
    /// write so the client can render the new group without a follow-up read,
    /// and so the count is the group's real size rather than a prediction.
    /// </summary>
    private static async Task<LibraryWorkMembershipResultDto> BuildWorkMembershipResultAsync(
        NostosDbContext db,
        BookModel book,
        Guid? removedWorkId,
        CancellationToken ct)
    {
        var workId = book.WorkId;
        var count = await db.Books.CountAsync(b => b.WorkId == workId, ct);
        return new LibraryWorkMembershipResultDto(book.Id, workId, count, removedWorkId);
    }

    private async Task<(bool DidChange, LibraryCommandResultDto Result)> CreateCollectionCoreAsync(
        NostosDbContext db,
        LibraryCreateCollectionRequest request,
        CancellationToken ct)
    {
        var state = await EnsureStateAsync(db, ct);

        if (string.IsNullOrWhiteSpace(request.Name))
            return NoChange(Failure("invalid_collection_name", "Collection name is required.", state.StateVersion));

        if (request.ParentId.HasValue)
        {
            var parentExists = await db.Collections.AsNoTracking()
                .AnyAsync(c => c.Id == request.ParentId.Value, ct);
            if (!parentExists)
                return NoChange(Failure("invalid_collection_parent",
                    LibraryReplyFormatter.CollectionParentNotFound, state.StateVersion));
        }

        // Duplicate sibling name: return the existing collection.
        var nName = BookIdentityNormalizer.NormalizeTitle(request.Name);
        var existing = await db.Collections.AsNoTracking()
            .Where(c => c.ParentId == request.ParentId)
            .ToListAsync(ct);
        var duplicate = existing.FirstOrDefault(c =>
            string.Equals(BookIdentityNormalizer.NormalizeTitle(c.Name), nName, StringComparison.Ordinal));
        if (duplicate is not null)
            return NoChange(Result(
                LibraryReplyFormatter.CollectionExists(duplicate.Name),
                new CollectionDto(duplicate.Id, duplicate.Name, duplicate.ParentId),
                state.StateVersion));

        var model = new CollectionModel { Name = request.Name.Trim(), ParentId = request.ParentId };
        db.Collections.Add(model);
        await db.SaveChangesAsync(ct);

        return Change(Result(
            LibraryReplyFormatter.CollectionCreated(model.Name),
            new CollectionDto(model.Id, model.Name, model.ParentId),
            state.StateVersion));
    }

    private async Task<(bool DidChange, LibraryCommandResultDto Result)> RenameCollectionCoreAsync(
        NostosDbContext db,
        LibraryRenameCollectionRequest request,
        CancellationToken ct)
    {
        // Preserve the historical check order (invalid name wins over
        // collection_not_found) that the frozen MCP surface relies on; the
        // shared core validates the name after loading the collection.
        var state = await EnsureStateAsync(db, ct);
        if (string.IsNullOrWhiteSpace(request.Name))
            return NoChange(Failure("invalid_collection_name", "Collection name is required.", state.StateVersion));

        var outcome = await UpdateCollectionCoreAsync(db, request.CollectionId, request.Name,
            Optional<Guid?>.None, treatUnchangedAsNoOp: false, ct);
        if (!outcome.DidChange)
            return outcome;
        return Change(outcome.Result with
        {
            Reply = LibraryReplyFormatter.CollectionRenamed(((CollectionDto)outcome.Result.Data!).Name),
        });
    }

    private async Task<(bool DidChange, LibraryCommandResultDto Result)> MoveCollectionCoreAsync(
        NostosDbContext db,
        LibraryMoveCollectionRequest request,
        CancellationToken ct)
    {
        var outcome = await UpdateCollectionCoreAsync(db, request.CollectionId, Optional<string>.None,
            request.NewParentId, treatUnchangedAsNoOp: false, ct);
        if (!outcome.DidChange)
            return outcome;
        return Change(outcome.Result with
        {
            Reply = LibraryReplyFormatter.CollectionMoved(((CollectionDto)outcome.Result.Data!).Name),
        });
    }

    /// <summary>
    /// Shared atomic rename/move core (collections Phase 1). Runs inside one
    /// MutateAsync transaction: loads the collection, validates the proposed
    /// name, rejects invalid/self/descendant parents, checks the normalized
    /// final name against the final parent's siblings, then assigns both
    /// values with a single SaveChanges. When
    /// <paramref name="treatUnchangedAsNoOp"/> is set (combined update), a
    /// request whose name and parent are both unchanged succeeds without a
    /// stateVersion bump; the two frozen MCP ops (rename/move) keep their
    /// historical always-bump-on-success behavior.
    /// </summary>
    private async Task<(bool DidChange, LibraryCommandResultDto Result)> UpdateCollectionCoreAsync(
        NostosDbContext db,
        Guid collectionId,
        Optional<string> name,
        Optional<Guid?> parentId,
        bool treatUnchangedAsNoOp,
        CancellationToken ct)
    {
        var state = await EnsureStateAsync(db, ct);

        var collection = await db.Collections.SingleOrDefaultAsync(c => c.Id == collectionId, ct);
        if (collection is null)
            return NoChange(Failure("collection_not_found", LibraryReplyFormatter.CollectionNotFound, state.StateVersion));

        if (name.HasValue && string.IsNullOrWhiteSpace(name.Value))
            return NoChange(Failure("invalid_collection_name", "Collection name is required.", state.StateVersion));

        var finalName = name.HasValue ? name.Value.Trim() : collection.Name;
        var finalParentId = parentId.HasValue ? parentId.Value : collection.ParentId;

        if (finalParentId.HasValue)
        {
            if (finalParentId.Value == collection.Id)
                return NoChange(Failure("collection_cycle", LibraryReplyFormatter.CollectionCycle, state.StateVersion));

            var parentExists = await db.Collections.AsNoTracking()
                .AnyAsync(c => c.Id == finalParentId.Value, ct);
            if (!parentExists)
                return NoChange(Failure("invalid_collection_parent",
                    LibraryReplyFormatter.CollectionParentNotFound, state.StateVersion));

            // Ancestor walk: moving under one of our own descendants cycles.
            var cursor = finalParentId.Value;
            var visited = new HashSet<Guid>();
            while (cursor != Guid.Empty)
            {
                if (cursor == collection.Id)
                    return NoChange(Failure("collection_cycle", LibraryReplyFormatter.CollectionCycle, state.StateVersion));
                if (!visited.Add(cursor))
                    return NoChange(Failure("collection_cycle", LibraryReplyFormatter.CollectionCycle, state.StateVersion));

                var parent = await db.Collections.AsNoTracking()
                    .Where(c => c.Id == cursor)
                    .Select(c => c.ParentId)
                    .SingleOrDefaultAsync(ct);
                if (parent is null)
                    break;
                cursor = parent.Value;
            }
        }

        // Sibling-name collision at the FINAL destination: the normalized
        // final name must not collide with any OTHER sibling there (the
        // root level is a sibling set like any other, parentId = null).
        var nName = BookIdentityNormalizer.NormalizeTitle(finalName);
        var siblings = await db.Collections.AsNoTracking()
            .Where(c => c.Id != collection.Id && c.ParentId == finalParentId)
            .ToListAsync(ct);
        var collision = siblings.FirstOrDefault(c =>
            string.Equals(BookIdentityNormalizer.NormalizeTitle(c.Name), nName, StringComparison.Ordinal));
        if (collision is not null)
            return NoChange(Failure("collection_name_conflict",
                LibraryReplyFormatter.CollectionNameConflict(collision.Name), state.StateVersion));

        var dto = new CollectionDto(collection.Id, collection.Name, collection.ParentId);

        // No-op policy (combined update only): same name and same parent is
        // a successful no-op — one receipt is still written, but the
        // stateVersion is not bumped.
        if (treatUnchangedAsNoOp &&
            string.Equals(collection.Name, finalName, StringComparison.Ordinal) &&
            collection.ParentId == finalParentId)
        {
            return NoChange(Result(
                LibraryReplyFormatter.CollectionUpdated(finalName),
                dto, state.StateVersion));
        }

        collection.Name = finalName;
        collection.ParentId = finalParentId;
        await db.SaveChangesAsync(ct);

        return Change(Result(
            LibraryReplyFormatter.CollectionUpdated(collection.Name),
            new CollectionDto(collection.Id, collection.Name, collection.ParentId),
            state.StateVersion));
    }

    private async Task<(bool DidChange, LibraryCommandResultDto Result)> DeleteCollectionCoreAsync(
        NostosDbContext db,
        LibraryDeleteCollectionRequest request,
        CancellationToken ct)
    {
        var state = await EnsureStateAsync(db, ct);

        if (!request.Confirm)
            return NoChange(Failure("confirmation_required",
                LibraryReplyFormatter.DeleteConfirmationRequired, state.StateVersion));

        var collection = await db.Collections.SingleOrDefaultAsync(c => c.Id == request.CollectionId, ct);
        if (collection is null)
            return NoChange(Failure("collection_not_found", LibraryReplyFormatter.CollectionNotFound, state.StateVersion));

        // Children must be moved/deleted first; never cascade silently.
        var childCount = await db.Collections.CountAsync(c => c.ParentId == collection.Id, ct);
        if (childCount > 0)
            return NoChange(Failure("collection_has_children",
                LibraryReplyFormatter.CollectionHasChildren, state.StateVersion));

        var bookCount = await db.BookCollections
            .CountAsync(bc => bc.CollectionId == collection.Id, ct);

        // Membership rows go first: their collection FK is RESTRICT, so leaving
        // them would block the delete. Books themselves survive untouched.
        // The revision row is advanced FIRST, before the portable rows, so every
        // portable writer takes the singleton lock in the same order. The
        // CollectionModel delete below would also advance it, and the
        // per-transaction marker keeps that to exactly one advance.
        await LibraryRevision.AdvanceAndGetAsync(db, ct);
        await db.BookCollections
            .Where(bc => bc.CollectionId == collection.Id)
            .ExecuteDeleteAsync(ct);

        db.Collections.Remove(collection);
        await db.SaveChangesAsync(ct);

        return Change(Result(
            LibraryReplyFormatter.CollectionDeleted(collection.Name),
            new LibraryDeleteCollectionResultDto(collection.Id, bookCount, 0),
            state.StateVersion));
    }

    // ------------------------------------------------------------------
    // Exact-once receipt plumbing (mirror of the reading service)
    // ------------------------------------------------------------------

    private static async Task<LibraryState> EnsureStateAsync(NostosDbContext db, CancellationToken ct)
    {
        var state = await db.LibraryStates.SingleOrDefaultAsync(ct);
        if (state is not null)
            return state;

        state = new LibraryState
        {
            Id = LibraryState.WellKnownId,
            SingletonSlot = LibraryState.SingletonSentinel,
        };
        db.LibraryStates.Add(state);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Concurrent ensure: the singleton already exists.
            db.ChangeTracker.Clear();
            state = await db.LibraryStates.SingleAsync(ct);
        }

        return state;
    }

    private static async Task<BookModel?> FindByIdentifiersAsync(
        NostosDbContext db,
        string? nIsbn,
        string? nAsin,
        CancellationToken ct)
    {
        if (nIsbn is not null)
        {
            var byIsbn = await db.Books.AsNoTracking()
                .SingleOrDefaultAsync(b => b.NormalizedIsbn == nIsbn, ct);
            if (byIsbn is not null)
                return byIsbn;
        }

        if (nAsin is not null)
        {
            var byAsin = await db.Books.AsNoTracking()
                .SingleOrDefaultAsync(b => b.NormalizedAsin == nAsin, ct);
            if (byAsin is not null)
                return byAsin;
        }

        return null;
    }

    private static LibraryCandidate ToCandidate(BookModel book, string reason) =>
        new(book.Id, book.Title, book.Author,
            book switch
            {
                PhysicalBookModel p => p.Isbn,
                EBookModel e => e.Isbn,
                _ => null,
            },
            book is AudioBookModel a ? a.Asin : null,
            reason);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static LibraryCommandResultDto Result(string reply, object? data, string version) =>
        new(reply, data, version);

    private static LibraryCommandResultDto Failure(string code, string reply, string version = "0",
        IReadOnlyList<LibraryCandidate>? candidates = null)
    {
        if (candidates is not null)
            return new LibraryCommandResultDto(reply,
                new LibraryConfirmationErrorDto(code, candidates), version);
        return new LibraryCommandResultDto(reply, new LibraryErrorDto(code), version);
    }

    private static (bool DidChange, LibraryCommandResultDto Result) Change(LibraryCommandResultDto result) =>
        (true, result);

    private static (bool DidChange, LibraryCommandResultDto Result) NoChange(LibraryCommandResultDto result) =>
        (false, result);

    // ------------------------------------------------------------------
    // Collections Phase 1 helpers
    // ------------------------------------------------------------------

    // Tri-state argument for the shared rename/move core: HasValue=false
    // means "leave the field unchanged" (distinct from an explicit null).
    private readonly record struct Optional<T>(T Value, bool HasValue)
    {
        public static implicit operator Optional<T>(T value) => new(value, true);

        public static Optional<T> None => default;
    }

}
