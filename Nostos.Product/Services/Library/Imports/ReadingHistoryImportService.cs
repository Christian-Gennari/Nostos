using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Reading-history import in two steps (issue #1521): <see cref="PreviewAsync"/>
/// reads a Goodreads or StoryGraph export and proposes, per row, the library
/// book and the values it would set, writing nothing; <see cref="CommitAsync"/>
/// applies exactly the rows the owner decided on. Each commit is a batch that
/// <see cref="UndoAsync"/> can take back.
///
/// The values go where Nostos keeps them: rating and review on the work (through
/// <see cref="BookValueRollUp"/>, so an older export never displaces a newer
/// rating), the finish date on the edition, to-read on the reading list, and
/// shelves in collections only when the owner maps them.
/// </summary>
public sealed class ReadingHistoryImportService
{
    public const string ClientId = "reading-history-import";
    private const string NoteAnchorKind = "reading_history_note";
    private const int MaxCandidates = 3;

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    private readonly NostosDbContext _db;
    private readonly INoteService _notes;
    private readonly ILibraryService _library;

    public ReadingHistoryImportService(NostosDbContext db, INoteService notes, ILibraryService library)
    {
        _db = db;
        _notes = notes;
        _library = library;
    }

    // ------------------------------------------------------------------
    // Preview
    // ------------------------------------------------------------------

    public async Task<ReadingHistoryImportPreview> PreviewAsync(string path, CancellationToken ct = default)
    {
        var (source, sourceBooks, skipped) = ReadingHistoryImportSourceReader.Read(path);
        var library = await LoadLibraryAsync(ct);
        var meta = await LoadLibraryMetaAsync(ct);
        var writtenNotes = (await _db.NoteCommandReceipts
                .AsNoTracking()
                .Where(receipt => receipt.ClientId == ClientId)
                .Select(receipt => receipt.IdempotencyKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var books = new List<ReadingHistoryPreviewBook>(sourceBooks.Count);
        foreach (var row in sourceBooks)
        {
            var (match, bookId, candidates) = Match(row, library);
            var existing = bookId is { } id && meta.TryGetValue(id, out var found) ? found : null;

            books.Add(new ReadingHistoryPreviewBook(
                row.SourceKey,
                row.Title,
                row.Author,
                row.Isbn,
                match,
                bookId,
                candidates,
                Values(row),
                existing is null || WouldChange(row, existing, writtenNotes),
                existing?.Title,
                row.Notice));
        }

        return new ReadingHistoryImportPreview(source, books, skipped);
    }

    /// <summary>
    /// What the row holds, as the commit would store it. A to-read row says so
    /// once; a read row carries its date.
    /// </summary>
    private static ReadingHistoryValues Values(ReadingHistorySourceBook row) =>
        new(
            row.Rating,
            row.Review,
            row.PrivateNotes,
            row.FinishedAt,
            row.ToRead,
            row.ReadStatus,
            row.ReadCount,
            row.Shelves,
            row.Notice);

    /// <summary>
    /// Whether a commit would write anything for an existing book. A value the
    /// book already holds is not a change, which is what makes a second import
    /// of the same file report "nothing new".
    /// </summary>
    private static bool WouldChange(
        ReadingHistorySourceBook row,
        LibraryBookMeta book,
        IReadOnlySet<string> writtenNotes)
    {
        if (row.Rating != 0 && row.Rating != book.Rating)
            return true;
        if (!string.IsNullOrEmpty(row.Review) && !string.Equals(row.Review, book.Review, StringComparison.Ordinal))
            return true;
        if (row.FinishedAt is { } finished && (book.FinishedAt is null || finished > book.FinishedAt))
            return true;
        if (!string.IsNullOrEmpty(row.PrivateNotes) && !writtenNotes.Contains(PrivateNoteKey(row)))
            return true;
        return false;
    }

    // ------------------------------------------------------------------
    // Commit
    // ------------------------------------------------------------------

    public async Task<ReadingHistoryImportResult> CommitAsync(
        string path,
        string? fileName,
        IReadOnlyList<ReadingHistoryImportDecision> decisions,
        CancellationToken ct = default)
    {
        var (source, sourceBooks, _) = ReadingHistoryImportSourceReader.Read(path);
        var bySourceKey = decisions
            .GroupBy(decision => decision.SourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var batch = new BookImportBatch { Source = source, FileName = Truncate(fileName, 260) };
        var batchAdded = false;
        var results = new List<ReadingHistoryImportResultBook>(sourceBooks.Count);

        // Loaded once per commit; rows share them.
        var collections = await _db.Collections.ToListAsync(ct);
        var readingList = await _db.ReadingListEntries.ToListAsync(ct);
        var readingListKeys = readingList
            .Select(entry => ReadingListKey(entry.Title, entry.Author, entry.Isbn))
            .ToHashSet(StringComparer.Ordinal);
        // One read for the whole import: when each work was last read decides
        // whether a file's older value may displace what the library says.
        var workLastRead = (await _db.Books
                .AsNoTracking()
                .Select(book => new { book.WorkId, book.Progress.LastReadAt, book.Progress.FinishedAt })
                .ToListAsync(ct))
            .GroupBy(book => book.WorkId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(book => book.LastReadAt ?? book.FinishedAt).Where(date => date.HasValue).Max());

        foreach (var row in sourceBooks)
        {
            bySourceKey.TryGetValue(row.SourceKey, out var decision);
            if (decision is null || (decision.BookId is null && !decision.Create))
            {
                results.Add(Result(row, "skipped", null, null, 0, false, null));
                continue;
            }

            // The batch is added before the first write so a note capture that
            // saves on its own can never leave imported values without the row
            // that takes them back. An empty batch is dropped before the save.
            if (!batchAdded)
            {
                _db.BookImportBatches.Add(batch);
                batchAdded = true;
            }

            // A to-read row is a wish, not a book: it goes to the reading list,
            // where Nostos keeps books the owner does not have yet.
            if (decision.BookId is null && row.ToRead)
            {
                var entryId = await AddReadingListEntryAsync(row, readingListKeys, ct);
                if (entryId is null)
                {
                    results.Add(Result(row, "unchanged", null, null, 0, false, "Already on your reading list."));
                    continue;
                }

                AddBatchRow(batch, row, readingListEntryId: entryId.Value, null, null, null, null, new BookImportSnapshot(
                    RatingBefore: 0, RatingAfter: 0,
                    ReviewBefore: null, ReviewAfter: null,
                    EditionRatingBefore: 0, EditionRatingAfter: 0,
                    EditionReviewBefore: null, EditionReviewAfter: null,
                    FinishedAtBefore: null, FinishedAtAfter: null,
                    AddedCollectionIds: [],
                    CreatedCollectionIds: []), 1);
                results.Add(Result(row, "imported", null, null, 1, false, "Added to your reading list."));
                continue;
            }

            var created = false;
            Guid? bookId = decision.BookId;
            if (bookId is null)
            {
                if (row.Title is null)
                {
                    results.Add(Result(row, "skipped", null, null, 0, false, "The file has no title for this book."));
                    continue;
                }

                var outcome = await _library.CreateOrMatchBookAsync(
                    new LibraryCreateBookRequest(
                        ClientId,
                        $"{ClientId}-{Guid.NewGuid():N}",
                        "physical",
                        row.Title,
                        Author: row.Author,
                        Isbn: row.Isbn),
                    strictConfirmation: false,
                    ct);
                if (outcome.Data is not LibraryCreateOrMatchResultDto { BookId: not null } createdBook)
                {
                    results.Add(Result(row, "skipped", null, null, 0, false, "The book could not be added to the library."));
                    continue;
                }

                bookId = createdBook.BookId;
                created = createdBook.Outcome == "created";
            }

            var book = await _db.Books
                .Include(candidate => candidate.BookCollections)
                .FirstOrDefaultAsync(candidate => candidate.Id == bookId.Value, ct);
            if (book is null)
            {
                results.Add(Result(row, "skipped", null, null, 0, false, "That book is no longer in the library."));
                continue;
            }

            var work = book.Work
                ?? throw new InvalidOperationException("A library book must have a work.");

            // Change the values, keeping what was there so undo can restore it.
            workLastRead.TryGetValue(work.Id, out var existingLastRead);
            var ratingBefore = work.Rating;
            var reviewBefore = work.PersonalReview;
            var editionRatingBefore = book.Progress.Rating;
            var editionReviewBefore = book.Progress.PersonalReview;
            var finishedBefore = book.Progress.FinishedAt;

            ApplyValues(book, work, row, existingLastRead);
            if (row.FinishedAt is { } readAt && (existingLastRead is null || readAt > existingLastRead))
                workLastRead[work.Id] = readAt;

            var addedCollections = new List<Guid>();
            var createdCollections = new List<Guid>();
            if (decision.MapShelves && row.Shelves.Count > 0)
                MapShelves(book, row.Shelves, collections, addedCollections, createdCollections);

            var noteKey = string.IsNullOrWhiteSpace(row.PrivateNotes) ? null : PrivateNoteKey(row);
            var writeNote = noteKey is not null
                && !await _db.NoteCommandReceipts
                    .AsNoTracking()
                    .AnyAsync(receipt => receipt.ClientId == ClientId && receipt.IdempotencyKey == noteKey, ct);

            var changes = new BookImportSnapshot(
                ratingBefore, work.Rating,
                reviewBefore, work.PersonalReview,
                editionRatingBefore, book.Progress.Rating,
                editionReviewBefore, book.Progress.PersonalReview,
                finishedBefore, book.Progress.FinishedAt,
                addedCollections,
                createdCollections);

            var changeCount = ChangeCount(changes) + (writeNote ? 1 : 0);
            if (changeCount == 0 && !created)
            {
                results.Add(Result(row, "unchanged", book.Id, book.Title, 0, false, "Already in Nostos."));
                continue;
            }

            var batchRow = AddBatchRow(
                batch, row, null, book.Id, created ? book.Id : null, null, writeNote ? noteKey : null, changes, changeCount);
            if (writeNote)
            {
                // The note service saves on its own, so everything this row
                // changed is committed (with the batch row) first; the note then
                // joins it, and a failure can never leave values without undo.
                await _db.SaveChangesAsync(ct);
                batchRow.NoteId = await CapturePrivateNoteAsync(book.Id, row, noteKey!, ct);
                await _db.SaveChangesAsync(ct);
            }

            results.Add(Result(
                row,
                "imported",
                book.Id,
                book.Title,
                changeCount,
                created,
                changeCount == 0 && !created ? "Already in your library." : null));
        }

        if (batch.Books.Count == 0 && batchAdded)
            _db.BookImportBatches.Remove(batch);
        await _db.SaveChangesAsync(ct);

        return new ReadingHistoryImportResult(
            batchAdded && batch.Books.Count > 0 ? batch.Id : null, source, results);
    }

    /// <summary>
    /// Rating and review move to the work through the one roll-up rule; the
    /// finish date goes on the edition and never moves backwards.
    /// </summary>
    private static void ApplyValues(BookModel book, WorkModel work, ReadingHistorySourceBook row, DateTime? existingLastRead)
    {
        // An older export must not displace a newer value: the library ranks
        // first unless the file was read strictly later than the library's last
        // read date, and on a tie the library wins (a tie is not newer).
        var importIsNewer = row.FinishedAt is { } readAt
            && (existingLastRead is null || readAt > existingLastRead);
        var workSide = new BookValueSide(
            work.Id, existingLastRead, work.CreatedAt, work.Rating, work.PersonalReview, work.IsFavorite);
        var importSide = new BookValueSide(
            book.Id,
            row.FinishedAt,
            importIsNewer ? row.FinishedAt ?? DateTime.MinValue : DateTime.MinValue,
            row.Rating,
            row.Review,
            work.IsFavorite);

        // Only this edition may be written: an import is about the copy the
        // owner matched, not about re-writing every edition of the work.
        BookValueRollUp.Merge(
            work,
            [
                new BookValueParty(workSide, [book]),
                new BookValueParty(importSide, [book]),
            ],
            [book]);

        if (row.FinishedAt is { } finished
            && (book.Progress.FinishedAt is null || finished > book.Progress.FinishedAt))
        {
            book.Progress.FinishedAt = finished;
        }
    }

    private void MapShelves(
        BookModel book,
        IReadOnlyList<string> shelves,
        List<CollectionModel> collections,
        List<Guid> added,
        List<Guid> created)
    {
        foreach (var shelf in shelves)
        {
            var name = shelf.Trim();
            if (name.Length == 0)
                continue;

            var normalized = BookIdentityNormalizer.NormalizeTitle(name);
            var collection = collections.FirstOrDefault(candidate =>
                string.Equals(BookIdentityNormalizer.NormalizeTitle(candidate.Name), normalized, StringComparison.Ordinal));
            if (collection is null)
            {
                collection = new CollectionModel { Name = name };
                _db.Collections.Add(collection);
                collections.Add(collection);
                created.Add(collection.Id);
            }

            if (book.BookCollections.Any(membership => membership.CollectionId == collection.Id))
                continue;

            book.BookCollections.Add(new BookCollectionModel { BookId = book.Id, CollectionId = collection.Id });
            added.Add(collection.Id);
        }
    }

    /// <summary>
    /// A private note becomes a note on the book through the canonical note
    /// service. The key is derived from the row and the words, so importing the
    /// same file again adds nothing, while a changed note is a new note.
    /// </summary>
    private async Task<Guid> CapturePrivateNoteAsync(
        Guid bookId,
        ReadingHistorySourceBook row,
        string key,
        CancellationToken ct)
    {
        var result = await _notes.CaptureAsync(new CaptureNoteRequest(
            BookId: bookId,
            Content: row.PrivateNotes!.Trim(),
            CaptureSource: "import",
            ProcessingMode: "verbatim",
            SourceAnchorKind: NoteAnchorKind,
            SourceAnchorValue: row.SourceKey,
            AnchorVerified: false,
            ClientId: ClientId,
            IdempotencyKey: key), ct);
        if (!result.Success)
            throw new InvalidOperationException(result.ErrorMessage ?? "The private note could not be saved.");

        return result.Value!.Id;
    }

    private static string PrivateNoteKey(ReadingHistorySourceBook row) =>
        ReadingHistoryCsv.Hash($"reading-history-note\n{row.SourceKey}\n{row.PrivateNotes}");

    private async Task<Guid?> AddReadingListEntryAsync(
        ReadingHistorySourceBook row,
        HashSet<string> existing,
        CancellationToken ct)
    {
        if (row.Title is null)
            return null;

        var isbn = BookIdentityNormalizer.NormalizeIsbn(row.Isbn);
        var key = ReadingListKey(row.Title, row.Author, isbn);
        if (!existing.Add(key))
            return null;

        var entry = new ReadingListEntryModel
        {
            Title = Truncate(row.Title, ReadingListEntryModel.MaxTitleLength)!,
            Author = Truncate(row.Author, ReadingListEntryModel.MaxAuthorLength),
            Isbn = isbn,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ReadingListEntries.Add(entry);
        return entry.Id;
    }

    private static string ReadingListKey(string? title, string? author, string? isbn)
    {
        var normalizedIsbn = BookIdentityNormalizer.NormalizeIsbn(isbn);
        return normalizedIsbn is not null
            ? $"isbn:{normalizedIsbn}"
            : $"meta:{BookIdentityNormalizer.NormalizeTitle(title)}|{BookIdentityNormalizer.NormalizeAuthor(author)}";
    }

    private BookImportBatchBook AddBatchRow(
        BookImportBatch batch,
        ReadingHistorySourceBook row,
        Guid? readingListEntryId,
        Guid? bookId,
        Guid? createdBookId,
        Guid? noteId,
        string? noteKey,
        BookImportSnapshot changes,
        int changeCount)
    {
        var entry = new BookImportBatchBook
        {
            BatchId = batch.Id,
            SourceKey = row.SourceKey.Length <= 1024 ? row.SourceKey : row.SourceKey[..1024],
            BookId = bookId,
            CreatedBookId = createdBookId,
            ReadingListEntryId = readingListEntryId,
            NoteId = noteId,
            NoteIdempotencyKey = noteKey,
            ChangesJson = JsonSerializer.Serialize(changes, SnapshotJson),
        };
        batch.Books.Add(entry);
        // Explicit Add: once the batch itself is already saved, a child with a
        // generated key added through the navigation would be inferred as an
        // existing row (Modified) and its UPDATE would affect nothing.
        _db.BookImportBatchBooks.Add(entry);
        batch.ChangeCount += changeCount;
        return entry;
    }

    private static int ChangeCount(BookImportSnapshot changes) =>
        (changes.RatingBefore != changes.RatingAfter ? 1 : 0)
        + (!string.Equals(changes.ReviewBefore, changes.ReviewAfter, StringComparison.Ordinal) ? 1 : 0)
        + (changes.EditionRatingBefore != changes.EditionRatingAfter ? 1 : 0)
        + (!string.Equals(changes.EditionReviewBefore, changes.EditionReviewAfter, StringComparison.Ordinal) ? 1 : 0)
        + (changes.FinishedAtBefore != changes.FinishedAtAfter ? 1 : 0)
        + (changes.AddedCollectionIds?.Count ?? 0);

    // ------------------------------------------------------------------
    // Batches and undo
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ReadingHistoryImportBatchSummary>> ListBatchesAsync(
        int take,
        CancellationToken ct = default)
    {
        var batches = await _db.BookImportBatches
            .AsNoTracking()
            .Where(batch => batch.Books.Count > 0)
            .OrderByDescending(batch => batch.CreatedAtUtc)
            .Take(take)
            .Select(batch => new
            {
                batch.Id,
                batch.Source,
                batch.FileName,
                batch.CreatedAtUtc,
                batch.ChangeCount,
                BookCount = batch.Books.Count,
            })
            .ToListAsync(ct);

        return batches
            .Select(batch => new ReadingHistoryImportBatchSummary(
                batch.Id, batch.Source, batch.FileName, batch.CreatedAtUtc, batch.BookCount, batch.ChangeCount))
            .ToList();
    }

    /// <summary>
    /// Takes a whole import back: the values it wrote (only while they still
    /// say what it wrote, so a later edit is never lost), the memberships and
    /// reading-list entries it added, its private notes, and the books it
    /// created when nothing else has come to them since.
    /// </summary>
    public async Task<int?> UndoAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await _db.BookImportBatches
            .Include(candidate => candidate.Books)
            .FirstOrDefaultAsync(candidate => candidate.Id == batchId, ct);
        if (batch is null)
            return null;

        foreach (var row in batch.Books)
        {
            var changes = Deserialize(row.ChangesJson);
            if (row.BookId is { } bookId)
            {
                var book = await _db.Books
                    .Include(candidate => candidate.BookCollections)
                    .FirstOrDefaultAsync(candidate => candidate.Id == bookId, ct);
                if (book is not null)
                    Restore(book, changes);
            }

            if (row.ReadingListEntryId is { } entryId)
            {
                var entry = await _db.ReadingListEntries.FirstOrDefaultAsync(candidate => candidate.Id == entryId, ct);
                if (entry is not null)
                    _db.ReadingListEntries.Remove(entry);
            }

            if (row.NoteId is { } noteId)
            {
                await _notes.DeleteAsync(noteId, ct);
                if (row.NoteIdempotencyKey is { } key)
                {
                    await _db.NoteCommandReceipts
                        .Where(receipt => receipt.ClientId == ClientId && receipt.IdempotencyKey == key)
                        .ExecuteDeleteAsync(ct);
                }
            }
        }

        _db.BookImportBatches.Remove(batch);
        await _db.SaveChangesAsync(ct);

        await RemoveCreatedCollectionsAsync(batch, ct);
        await RemoveCreatedBooksAsync(batch, ct);
        return batch.Books.Count;
    }

    private void Restore(BookModel book, BookImportSnapshot changes)
    {
        var work = book.Work;
        if (work is not null)
        {
            if (changes.RatingBefore != changes.RatingAfter && work.Rating == changes.RatingAfter)
                work.Rating = changes.RatingBefore;
            if (!string.Equals(changes.ReviewBefore, changes.ReviewAfter, StringComparison.Ordinal)
                && string.Equals(work.PersonalReview, changes.ReviewAfter, StringComparison.Ordinal))
            {
                work.PersonalReview = changes.ReviewBefore;
            }
        }

        if (changes.EditionRatingBefore != changes.EditionRatingAfter
            && book.Progress.Rating == changes.EditionRatingAfter)
        {
            book.Progress.Rating = changes.EditionRatingBefore;
        }

        if (!string.Equals(changes.EditionReviewBefore, changes.EditionReviewAfter, StringComparison.Ordinal)
            && string.Equals(book.Progress.PersonalReview, changes.EditionReviewAfter, StringComparison.Ordinal))
        {
            book.Progress.PersonalReview = changes.EditionReviewBefore;
        }

        if (changes.FinishedAtBefore != changes.FinishedAtAfter
            && book.Progress.FinishedAt == changes.FinishedAtAfter)
        {
            book.Progress.FinishedAt = changes.FinishedAtBefore;
        }

        if (changes.AddedCollectionIds is { Count: > 0 } addedIds)
        {
            var added = addedIds.ToHashSet();
            _db.BookCollections.RemoveRange(
                book.BookCollections.Where(membership => added.Contains(membership.CollectionId)));
        }

        // The roll-up invariant again: an edition value that now says the same
        // as the work is not an exception.
        if (work is not null && book.Progress.Rating == work.Rating)
            book.Progress.Rating = 0;
        if (work is not null && string.Equals(book.Progress.PersonalReview, work.PersonalReview, StringComparison.Ordinal))
            book.Progress.PersonalReview = null;
    }

    private async Task RemoveCreatedCollectionsAsync(BookImportBatch batch, CancellationToken ct)
    {
        var created = batch.Books
            .SelectMany(row => Deserialize(row.ChangesJson).CreatedCollectionIds ?? [])
            .Distinct()
            .ToList();
        if (created.Count == 0)
            return;

        var empty = await _db.Collections
            .Where(collection => created.Contains(collection.Id))
            .Where(collection => !_db.BookCollections.Any(membership => membership.CollectionId == collection.Id))
            .Where(collection => !_db.Collections.Any(child => child.ParentId == collection.Id))
            .ToListAsync(ct);
        if (empty.Count > 0)
        {
            _db.Collections.RemoveRange(empty);
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// The books this import added go with it when nothing else has come to
    /// them: no other note, no file, no membership. Mirrors the highlight
    /// import's rule.
    /// </summary>
    private async Task RemoveCreatedBooksAsync(BookImportBatch batch, CancellationToken ct)
    {
        foreach (var bookId in batch.Books
                     .Where(row => row.CreatedBookId is not null)
                     .Select(row => row.CreatedBookId!.Value)
                     .Distinct())
        {
            var book = await _db.Books
                .AsNoTracking()
                .Where(candidate => candidate.Id == bookId)
                .Select(candidate => new { IsPhysical = candidate is PhysicalBookModel, candidate.FileDetails.FileName })
                .FirstOrDefaultAsync(ct);
            if (book is null || !book.IsPhysical || !string.IsNullOrEmpty(book.FileName))
                continue;
            if (await _db.Notes.AnyAsync(note => note.BookId == bookId, ct))
                continue;

            await _library.DeleteBookAsync(bookId, ct);
        }
    }

    private static BookImportSnapshot Deserialize(string json) =>
        JsonSerializer.Deserialize<BookImportSnapshot>(json, SnapshotJson) ?? new BookImportSnapshot();

    // ------------------------------------------------------------------
    // Library reading
    // ------------------------------------------------------------------

    private async Task<List<ImportLibraryBook>> LoadLibraryAsync(CancellationToken ct)
    {
        var rows = await _db.Books
            .AsNoTracking()
            .Select(book => new
            {
                book.Id,
                book.Title,
                book.Author,
                book.NormalizedIsbn,
                IsAudiobook = book is AudioBookModel,
                IsPhysical = book is PhysicalBookModel,
                book.FileDetails.FileName,
            })
            .ToListAsync(ct);
        return rows
            .Select(row => new ImportLibraryBook(
                row.Id,
                row.Title,
                row.Author,
                row.NormalizedIsbn,
                HighlightImportBookFormat.Of(row.IsAudiobook, row.IsPhysical, row.FileName)))
            .ToList();
    }

    private async Task<Dictionary<Guid, LibraryBookMeta>> LoadLibraryMetaAsync(CancellationToken ct)
    {
        var rows = await _db.Books
            .AsNoTracking()
            .Select(book => new
            {
                book.Id,
                book.Title,
                book.Progress.FinishedAt,
                book.Work!.Rating,
                BookReview = book.Work!.PersonalReview,
            })
            .ToListAsync(ct);

        return rows.ToDictionary(
            row => row.Id,
            row => new LibraryBookMeta(row.Title, row.Rating, row.BookReview, row.FinishedAt));
    }

    /// <summary>
    /// ISBN-13 first, then ISBN-10, then title and author, as the issue asks;
    /// every attempt reuses the highlight import's one matcher.
    /// </summary>
    private static (string Match, Guid? BookId, IReadOnlyList<HighlightImportCandidate> Candidates) Match(
        ReadingHistorySourceBook source,
        IReadOnlyList<ImportLibraryBook> library)
    {
        var candidates = new List<HighlightImportCandidate>();
        void Add(IEnumerable<HighlightImportCandidate> found)
        {
            foreach (var candidate in found)
            {
                if (candidates.All(existing => existing.BookId != candidate.BookId))
                    candidates.Add(candidate);
            }
        }

        foreach (var isbn in new[] { source.Isbn13, source.Isbn10 }.Where(value => value is not null))
        {
            var (match, found) = HighlightImportMatcher.Match(WithIsbn(source, isbn), library);
            if (match == HighlightImportMatcher.Exact && found.Count > 0)
                return (HighlightImportMatcher.Exact, found[0].BookId, found);
            Add(found);
        }

        var (titleMatch, titleCandidates) = HighlightImportMatcher.Match(WithIsbn(source, null), library);
        if (titleMatch == HighlightImportMatcher.Exact && titleCandidates.Count > 0)
            return (HighlightImportMatcher.Exact, titleCandidates[0].BookId, titleCandidates);

        Add(titleCandidates);
        var listed = candidates.Take(MaxCandidates).ToList();
        return (listed.Count == 0 ? HighlightImportMatcher.None : HighlightImportMatcher.Suggested, null, listed);
    }

    private static ImportSourceBook WithIsbn(ReadingHistorySourceBook source, string? isbn) =>
        new(source.SourceKey, source.Title, source.Author, isbn, []);

    private static ReadingHistoryImportResultBook Result(
        ReadingHistorySourceBook row,
        string status,
        Guid? bookId,
        string? bookTitle,
        int changeCount,
        bool created,
        string? message) =>
        new(row.SourceKey, status, bookId, bookTitle, row.Title, row.Author, changeCount, created, message);

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    private sealed record LibraryBookMeta(string Title, int Rating, string? Review, DateTime? FinishedAt);

    private sealed record BookImportSnapshot(
        int RatingBefore = 0,
        int RatingAfter = 0,
        string? ReviewBefore = null,
        string? ReviewAfter = null,
        int EditionRatingBefore = 0,
        int EditionRatingAfter = 0,
        string? EditionReviewBefore = null,
        string? EditionReviewAfter = null,
        DateTime? FinishedAtBefore = null,
        DateTime? FinishedAtAfter = null,
        IReadOnlyList<Guid>? AddedCollectionIds = null,
        IReadOnlyList<Guid>? CreatedCollectionIds = null);
}
