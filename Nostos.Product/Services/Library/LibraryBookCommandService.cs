using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// The book row's own commands (issue #1611): create-or-match, update, attach an
/// acquired file, and delete. Identity resolution is decided by
/// <see cref="LibraryBookMatcher"/>; what a field change means is decided by
/// <c>BookUpdateMapping</c> (issue #1612); collection membership goes through
/// <see cref="LibraryCollectionMembership"/>. What is left here is the row.
///
/// Receipt-guarded commands run through <see cref="LibraryMutationExecutor"/>;
/// delete is last-write-wins like the other high-frequency writes and takes the
/// same gate and revision pipeline through
/// <see cref="LibraryMutationExecutor.RunInTransactionAsync"/>.
/// </summary>
public sealed class LibraryBookCommandService
{
    private readonly IDbContextFactory<NostosDbContext> _contexts;
    private readonly LibraryMutationExecutor _mutations;
    private DateTime Now => DateTime.UtcNow;

    public LibraryBookCommandService(
        IDbContextFactory<NostosDbContext> contexts,
        LibraryMutationExecutor mutations)
    {
        _contexts = contexts;
        _mutations = mutations;
    }

    // ------------------------------------------------------------------
    // Exact-once commands
    // ------------------------------------------------------------------

    public Task<LibraryCommandResultDto> CreateOrMatchAsync(
        LibraryCreateBookRequest request,
        bool strictConfirmation,
        CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "CreateOrMatchBook",
            (db, token) => CreateOrMatchCoreAsync(db, request, strictConfirmation, token), ct);

    public Task<LibraryCommandResultDto> UpdateAsync(
        LibraryUpdateBookRequest request,
        CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "UpdateBook",
            (db, token) => UpdateBookCoreAsync(db, request, token), ct);

    public Task<LibraryCommandResultDto> AttachAcquiredAssetAsync(
        LibraryAttachAcquiredAssetRequest request,
        CancellationToken ct = default) =>
        _mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "AttachAcquiredAsset",
            (db, token) => AttachAcquiredAssetCoreAsync(db, request, token), ct);

    /// <summary>
    /// Deletes the row, and its work when this was the work's last book. The
    /// reading/notes foreign keys reject an in-use book as
    /// <c>book_in_use</c>; the caller removes storage files only after this
    /// succeeds.
    /// </summary>
    public Task<LibraryCommandResultDto> DeleteAsync(Guid bookId, CancellationToken ct = default) =>
        _mutations.RunInTransactionAsync(async (db, token) =>
        {
            var state = await LibraryStateGuard.EnsureAsync(db, token);

            var book = await db.Books.SingleOrDefaultAsync(b => b.Id == bookId, token);
            if (book is null)
                return (false,
                    LibraryCommandResults.Failure("book_not_found", LibraryReplyFormatter.BookNotFound,
                        state.StateVersion));

            var version = await LibraryRevision.AdvanceAndGetAsync(db, token);
            db.Books.Remove(book);
            try
            {
                await db.SaveChangesAsync(token);

                if (book.WorkId != Guid.Empty)
                {
                    var remainingInWork = await db.Books.AnyAsync(b => b.WorkId == book.WorkId, token);
                    if (!remainingInWork)
                    {
                        var work = await db.Works.FindAsync([book.WorkId], token);
                        if (work is not null)
                        {
                            db.Works.Remove(work);
                            await db.SaveChangesAsync(token);
                        }
                    }
                }
            }
            catch (DbUpdateException)
            {
                // An in-use book: the gate rolls the transaction back, so the
                // revision never advances for a delete that wrote nothing.
                db.ChangeTracker.Clear();
                return (false,
                    LibraryCommandResults.Failure("book_in_use", LibraryReplyFormatter.BookInUse(book.Title),
                        state.StateVersion));
            }

            return (true, LibraryCommandResults.Result(
                LibraryReplyFormatter.BookDeleted(book.Title), new { deleted = true }, version));
        }, ct);

    // ------------------------------------------------------------------
    // Cores (also run inside a changeset's single transaction)
    // ------------------------------------------------------------------

    internal async Task<(bool DidChange, LibraryCommandResultDto Result)> CreateOrMatchCoreAsync(
        NostosDbContext db,
        LibraryCreateBookRequest request,
        bool strictConfirmation,
        CancellationToken ct)
    {
        var state = await LibraryStateGuard.EnsureAsync(db, ct);

        if (!LibraryBookIdentity.TryCreate(request, out var identity, out var errorCode, out var errorMessage))
            return LibraryCommandResults.NoChange(
                LibraryCommandResults.Failure(errorCode, errorMessage, state.StateVersion));

        var decision = await LibraryBookMatcher.DecideAsync(db, request, identity, strictConfirmation, ct);
        switch (decision.Kind)
        {
            case LibraryMatchKind.Matched:
                return LibraryCommandResults.NoChange(LibraryCommandResults.Result(
                    LibraryReplyFormatter.BookMatched(decision.Matched!.Title),
                    new LibraryCreateOrMatchResultDto("matched", decision.Matched.Id, decision.Matched.ToDto()),
                    state.StateVersion));

            case LibraryMatchKind.ConfirmationRequired:
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("confirmation_required",
                    decision.Reply!, state.StateVersion, decision.Candidates));

            case LibraryMatchKind.Failure:
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure(
                    decision.ErrorCode!, decision.Reply!, state.StateVersion));
        }

        // Create-time membership. CollectionIds (the set) is authoritative when
        // supplied; the singular CollectionId is the legacy REST shape and is
        // still honoured so no existing caller changes behaviour. Validate all
        // requested collections before inserting WorkModel/BookModel; the join
        // rows are added after SaveChanges, once the book id exists.
        var membershipIds = request.CollectionIds is { Count: > 0 }
            ? request.CollectionIds.Distinct().ToList()
            : (request.CollectionId.HasValue ? [request.CollectionId.Value] : []);

        if (!await LibraryCollectionMembership.AllExistAsync(db, membershipIds, ct))
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("collection_not_found",
                LibraryReplyFormatter.CollectionNotFound, state.StateVersion));

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

        var targetWorkId = decision.TargetWorkId;
        if (targetWorkId.HasValue && targetWorkId.Value != Guid.Empty)
        {
            // Joining an existing work: what the new edition says about the book
            // is combined with what the work already says by the shared rule.
            var joined = await db.Works.SingleAsync(w => w.Id == targetWorkId.Value, ct);
            var siblings = await db.Books.Where(b => b.WorkId == joined.Id).ToListAsync(ct);
            model.WorkId = joined.Id;
            model.Work = joined;
            if (request.Rating > 0 || request.IsFavorite || !string.IsNullOrEmpty(request.PersonalReview))
            {
                model.Progress.Rating = Math.Clamp(request.Rating, 0, 5);
                model.Progress.PersonalReview = NullIfEmpty(request.PersonalReview);
                BookValueRollUp.Merge(
                    joined,
                    [
                        new BookValueParty(BookValueRollUp.WorkSide(joined, siblings), siblings),
                        new BookValueParty(BookValueRollUp.EditionSide(model, request.IsFavorite), [model]),
                    ],
                    [.. siblings, model]);
            }
        }
        else
        {
            // A work with no author stores a null normalized author, never "".
            var normalizedAuthor = string.IsNullOrEmpty(identity.NormalizedAuthor)
                ? null
                : identity.NormalizedAuthor;
            var work = await db.Works.FirstOrDefaultAsync(w =>
                w.NormalizedTitle == identity.NormalizedTitle &&
                w.NormalizedAuthor == normalizedAuthor, ct);

            if (work is null)
            {
                work = new WorkModel
                {
                    Id = Guid.NewGuid(),
                    Title = request.Title,
                    Author = request.Author,
                    NormalizedTitle = identity.NormalizedTitle,
                    NormalizedAuthor = normalizedAuthor,
                    CreatedAt = DateTime.UtcNow,
                    // A new book: what was said about it is the book's.
                    Rating = Math.Clamp(request.Rating, 0, 5),
                    IsFavorite = request.IsFavorite,
                    PersonalReview = NullIfEmpty(request.PersonalReview),
                };
                db.Works.Add(work);
            }
            else if (request.Rating > 0 || request.IsFavorite || !string.IsNullOrEmpty(request.PersonalReview))
            {
                // Joined an existing work found by title and author: shared rule.
                var siblings = await db.Books.Where(b => b.WorkId == work.Id).ToListAsync(ct);
                model.Progress.Rating = Math.Clamp(request.Rating, 0, 5);
                model.Progress.PersonalReview = NullIfEmpty(request.PersonalReview);
                BookValueRollUp.Merge(
                    work,
                    [
                        new BookValueParty(BookValueRollUp.WorkSide(work, siblings), siblings),
                        new BookValueParty(BookValueRollUp.EditionSide(model, request.IsFavorite), [model]),
                    ],
                    [.. siblings, model]);
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
            var raced = await FindByIdentifiersAsync(db, identity.NormalizedIsbn, identity.NormalizedAsin, ct);
            if (raced is not null)
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("duplicate_identifier",
                    LibraryReplyFormatter.DuplicateIdentifier(raced.Title), state.StateVersion));
            throw;
        }

        if (membershipIds.Count > 0)
        {
            LibraryCollectionMembership.Add(db, model.Id, membershipIds, model.CreatedAt);
            await db.SaveChangesAsync(ct);
            model.BookCollections = membershipIds
                .Select(id => new BookCollectionModel { BookId = model.Id, CollectionId = id })
                .ToList();
        }

        // The caller learns from the reply whether this joined a work that
        // already had editions, without a second request.
        var editionCount = await db.Books.CountAsync(b => b.WorkId == model.WorkId, ct);

        return LibraryCommandResults.Change(LibraryCommandResults.Result(
            LibraryReplyFormatter.BookCreated(model.Title),
            new LibraryCreateOrMatchResultDto("created", model.Id,
                model.ToDto() with { EditionCount = editionCount }),
            state.StateVersion));
    }

    internal async Task<(bool DidChange, LibraryCommandResultDto Result)> UpdateBookCoreAsync(
        NostosDbContext db,
        LibraryUpdateBookRequest request,
        CancellationToken ct)
    {
        var state = await LibraryStateGuard.EnsureAsync(db, ct);

        // Loaded so the returned BookDto reports real membership even when
        // this update does not touch it.
        var book = await db.Books
            .Include(b => b.BookCollections)
            .SingleOrDefaultAsync(b => b.Id == request.BookId, ct);
        if (book is null)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure(
                "book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

        if (request.Title is not null && string.IsNullOrWhiteSpace(request.Title))
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure(
                "invalid_book_identity", "Title cannot be empty.", state.StateVersion));

        // Identifier/type consistency: identifiers the model cannot store are
        // rejected, never silently dropped.
        if (book is AudioBookModel && !string.IsNullOrWhiteSpace(request.Isbn))
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure(
                "invalid_book_identity", "Audiobooks use ASIN, not ISBN.", state.StateVersion));
        if (book is not AudioBookModel && !string.IsNullOrWhiteSpace(request.Asin))
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure(
                "invalid_book_identity", "Only audiobooks use ASIN.", state.StateVersion));

        // Every field an update can carry, in one shared mapping (issue #1612):
        // null leaves a field alone, an empty string clears it, the book's values
        // go to its work and the edition keeps only its own exceptions.
        BookUpdateMapping.Apply(book, request.ToPatch(), Now);

        // --- COLLECTION MEMBERSHIP ---
        // CollectionIds (the full replacement set) is the authoritative write.
        // The singular CollectionId / ClearCollection pair is the legacy
        // contract, kept working by translating it into the same set operation
        // so the two can never disagree.
        if (request.CollectionIds is not null)
        {
            if (!await LibraryCollectionMembership.ReplaceAsync(db, book.Id, request.CollectionIds, ct))
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure(
                    "collection_not_found", LibraryReplyFormatter.CollectionNotFound, state.StateVersion));
        }
        else if (request.CollectionId.HasValue)
        {
            // Legacy singular set: replace whatever membership exists with this
            // one collection.
            if (!await LibraryCollectionMembership.ReplaceAsync(db, book.Id, [request.CollectionId.Value], ct))
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure(
                    "collection_not_found", LibraryReplyFormatter.CollectionNotFound, state.StateVersion));
        }
        else if (request.ClearCollection)
        {
            await LibraryCollectionMembership.ReplaceAsync(db, book.Id, [], ct);
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
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("duplicate_identifier",
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
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("duplicate_identifier",
                    LibraryReplyFormatter.DuplicateIdentifier(raced[0].Title), state.StateVersion));
            throw;
        }

        return LibraryCommandResults.Change(LibraryCommandResults.Result(
            LibraryReplyFormatter.BookUpdated(book.Title),
            book.ToDto(),
            state.StateVersion));
    }

    internal async Task<(bool DidChange, LibraryCommandResultDto Result)> AttachAcquiredAssetCoreAsync(
        NostosDbContext db,
        LibraryAttachAcquiredAssetRequest request,
        CancellationToken ct)
    {
        var state = await LibraryStateGuard.EnsureAsync(db, ct);

        // A bare file name only. Anything with a directory component would let
        // the caller choose where a book's file is read from.
        var fileName = request.FileName?.Trim() ?? string.Empty;

        // A multi-track audiobook: the track list is the book's media and
        // there is no primary file beside it.
        IReadOnlyList<BookTrack>? tracks = null;
        if (request.Tracks is { Count: > 0 })
        {
            tracks = request.Tracks
                .Select(track => new BookTrack(
                    track.Number,
                    track.FileName,
                    track.ContentType,
                    track.DurationMs,
                    track.Bytes,
                    track.Crc32,
                    NullIfEmpty(track.Title)))
                .ToList();

            if (fileName.Length != 0 || BookTrackList.Validate(tracks) is not null)
            {
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("invalid_track_list",
                    BookTrackList.Validate(tracks) ?? "A book holds one file or a track list, never both.",
                    state.StateVersion));
            }
        }
        else if (fileName.Length is 0 or > 64
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("invalid_file_name",
                "The stored file name must be a bare file name.", state.StateVersion));
        }

        if (string.IsNullOrWhiteSpace(request.ProviderId)
            || string.IsNullOrWhiteSpace(request.ExternalId)
            || string.IsNullOrWhiteSpace(request.AssetId))
        {
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("invalid_provenance",
                "Provider id, external id and asset id are required.", state.StateVersion));
        }

        var book = await db.Books
            .Include(b => b.BookCollections)
            .Include(b => b.Acquisition)
            .SingleOrDefaultAsync(b => b.Id == request.BookId, ct);

        if (book is null)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure(
                "book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

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
                return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("acquisition_conflict",
                    LibraryReplyFormatter.AcquisitionConflict, state.StateVersion));

            return LibraryCommandResults.NoChange(LibraryCommandResults.Result(
                LibraryReplyFormatter.AssetAlreadyAttached(book.Title),
                new LibraryAttachAcquiredAssetResultDto(book.Id, book.ToDto()),
                state.StateVersion));
        }

        if (tracks is not null && book is not AudioBookModel)
        {
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("invalid_track_list",
                "Only an audiobook can hold a track list.", state.StateVersion));
        }

        book.FileDetails.HasFile = true;
        book.FileDetails.FileName = tracks is null ? fileName : null;
        book.FileDetails.TracksJson = tracks is null ? null : BookTrackList.Serialize(tracks);
        // The cover is written to disk by the importer, so the row has to be told
        // its name: the cover URL is derived from this column, and writing only
        // the file left imported books with a cover nothing could display.
        if (!string.IsNullOrWhiteSpace(request.CoverFileName))
            book.FileDetails.SetCover(request.CoverFileName);
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
            ImportedExtension = Path.GetExtension(tracks is null ? fileName : tracks[0].FileName).ToLowerInvariant(),
            SourceUrl = NullIfEmpty(request.SourceUrl),
            RightsStatement = NullIfEmpty(request.RightsStatement),
            AcquiredAt = request.AcquiredAt ?? Now,
        };

        book.Acquisition = acquisition;
        db.BookAcquisitions.Add(acquisition);

        await db.SaveChangesAsync(ct);

        return LibraryCommandResults.Change(LibraryCommandResults.Result(
            LibraryReplyFormatter.AssetAttached(book.Title),
            new LibraryAttachAcquiredAssetResultDto(book.Id, book.ToDto()),
            state.StateVersion));
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

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
