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
        var collections = new ImportCollectionIndex(await _db.Collections.ToListAsync(ct));
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
                .AsNoTracking()
                .Include(candidate => candidate.BookCollections)
                .FirstOrDefaultAsync(candidate => candidate.Id == bookId.Value, ct);
            if (book is null)
            {
                results.Add(Result(row, "skipped", null, null, 0, false, "That book is no longer in the library."));
                continue;
            }

            var work = book.Work
                ?? throw new InvalidOperationException("A library book must have a work.");

            // Decide every value before anything is written: what the row asks
            // for, and what it displaces, so undo can put the library back.
            workLastRead.TryGetValue(work.Id, out var existingLastRead);
            var values = DecideValues(book, work, row, existingLastRead);
            if (row.FinishedAt is { } readAt && (existingLastRead is null || readAt > existingLastRead))
                workLastRead[work.Id] = readAt;

            var addedCollections = new List<Guid>();
            var createdCollections = new List<Guid>();
            var wantedCollections = new List<Guid>();
            if (decision.MapShelves && row.Shelves.Count > 0)
            {
                var shelves = await ResolveShelvesAsync(
                    book.BookCollections.Select(membership => membership.CollectionId).ToHashSet(),
                    row.Shelves,
                    collections,
                    ct);
                wantedCollections.AddRange(shelves.Wanted);
                createdCollections.AddRange(shelves.Created);
                addedCollections.AddRange(shelves.Wanted);
            }

            var noteKey = string.IsNullOrWhiteSpace(row.PrivateNotes) ? null : PrivateNoteKey(row);
            var writeNote = noteKey is not null
                && !await _db.NoteCommandReceipts
                    .AsNoTracking()
                    .AnyAsync(receipt => receipt.ClientId == ClientId && receipt.IdempotencyKey == noteKey, ct);

            var changes = new BookImportSnapshot(
                work.Rating, values.Rating,
                work.PersonalReview, values.Review,
                book.Progress.Rating, values.EditionRating,
                book.Progress.PersonalReview, values.EditionReview,
                book.Progress.FinishedAt, values.FinishedAt,
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

            // The row that takes these values back is durable before the values
            // are: undo only restores what still says what the import wrote, so a
            // row saved ahead of its write is harmless, and a write without one
            // would be the import's one unpickable change.
            await _db.SaveChangesAsync(ct);

            if (wantedCollections.Count > 0)
                await AddMembershipsAsync(batchRow, book.Id, wantedCollections, ct);
            if (ValuesChange(book, work, values))
                await ApplyValuesAsync(book, work, values, ct);

            if (writeNote)
            {
                // The note service saves on its own, so everything this row
                // changed is committed (with the batch row) first; the note then
                // joins it, and a failure can never leave values without undo.
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
    /// finish date goes on the edition and never moves backwards. Decided here,
    /// without writing, so the batch row can record both sides of every value
    /// before the library is changed.
    /// </summary>
    private static ImportedValues DecideValues(
        BookModel book,
        WorkModel work,
        ReadingHistorySourceBook row,
        DateTime? existingLastRead)
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
        var combined = BookValueRollUp.Combine([workSide, importSide]);

        // The edition's own value after the merge, read off the same rule
        // BookValueRollUp.Merge applies: a losing side keeps its value on this
        // edition unless the edition already says something of its own, and a
        // value that agrees with the book's stops being an exception.
        var editionRating = book.Progress.Rating;
        var editionReview = book.Progress.PersonalReview;
        foreach (var side in new[] { workSide, importSide })
        {
            if (combined.RatingLosers.Contains(side.Id) && editionRating == 0)
                editionRating = side.Rating;
            if (combined.ReviewLosers.Contains(side.Id) && string.IsNullOrEmpty(editionReview))
                editionReview = side.Review;
        }

        var finishedAt = book.Progress.FinishedAt;
        if (row.FinishedAt is { } finished && (finishedAt is null || finished > finishedAt))
            finishedAt = finished;

        return new ImportedValues(
            combined.Rating,
            combined.Review,
            BookValueRollUp.EditionRatingAfter(editionRating, combined.Rating),
            BookValueRollUp.EditionReviewAfter(editionReview, combined.Review),
            finishedAt);
    }

    private static bool ValuesChange(BookModel book, WorkModel work, ImportedValues values) =>
        values.Rating != work.Rating
        || !string.Equals(values.Review, work.PersonalReview, StringComparison.Ordinal)
        || values.EditionRating != book.Progress.Rating
        || !string.Equals(values.EditionReview, book.Progress.PersonalReview, StringComparison.Ordinal)
        || values.FinishedAt != book.Progress.FinishedAt;

    /// <summary>
    /// Writes the values through the library service, naming only the fields
    /// that change: the book's rating and review go to its work, the finish date
    /// to the edition. Nothing is written when nothing changes, so a row that
    /// says what the library already holds never advances the state version.
    /// </summary>
    private async Task ApplyValuesAsync(
        BookModel book,
        WorkModel work,
        ImportedValues values,
        CancellationToken ct)
    {
        var rating = values.Rating != work.Rating ? values.Rating : (int?)null;
        var review = string.Equals(values.Review, work.PersonalReview, StringComparison.Ordinal)
            ? null
            : values.Review ?? string.Empty;
        var editionRating = values.EditionRating != book.Progress.Rating ? values.EditionRating : (int?)null;
        var editionReview = string.Equals(values.EditionReview, book.Progress.PersonalReview, StringComparison.Ordinal)
            ? null
            : values.EditionReview ?? string.Empty;
        var finishedAt = values.FinishedAt != book.Progress.FinishedAt ? values.FinishedAt : null;
        // A finish date that goes away is cleared by IsFinished, never by a
        // null FinishedAt: null means "leave it as it is".
        var isFinished = values.FinishedAt is null && book.Progress.FinishedAt is not null
            ? false
            : (bool?)null;

        await _library.UpdateBookAsync(
            new LibraryUpdateBookRequest(
                ClientId,
                $"{ClientId}-{Guid.NewGuid():N}",
                book.Id,
                Rating: rating,
                PersonalReview: review,
                FinishedAt: finishedAt,
                IsFinished: isFinished,
                EditionRating: editionRating,
                EditionReview: editionReview),
            ct);
    }

    /// <summary>
    /// The collections a row's shelves name, created through the library when
    /// missing, and the memberships to add: decided before the write so the batch
    /// row records exactly what the import brings. A shelf the book is already
    /// filed under is not named again.
    /// </summary>
    private async Task<(IReadOnlyList<Guid> Wanted, IReadOnlyList<Guid> Created)> ResolveShelvesAsync(
        IReadOnlySet<Guid> memberIds,
        IReadOnlyList<string> shelves,
        ImportCollectionIndex collections,
        CancellationToken ct)
    {
        var wanted = new List<Guid>();
        var created = new List<Guid>();
        foreach (var shelf in shelves)
        {
            var name = shelf.Trim();
            if (name.Length == 0)
                continue;

            var (collection, wasCreated) = await collections.EnsureAsync(name, _library, ClientId, ct);
            if (wasCreated)
                created.Add(collection.Id);
            if (memberIds.Contains(collection.Id) || wanted.Contains(collection.Id))
                continue;

            wanted.Add(collection.Id);
        }

        return (wanted, created);
    }

    /// <summary>
    /// Adds the memberships the row brings and records on the batch row how many
    /// actually went in: a membership another writer added first is not this
    /// import's to take back.
    /// </summary>
    private async Task AddMembershipsAsync(
        BookImportBatchBook batchRow,
        Guid bookId,
        IReadOnlyList<Guid> wanted,
        CancellationToken ct)
    {
        var outcome = await _library.ApplyBookCollectionChangesAsync(bookId, wanted, [], ct);
        if (outcome.Data is not LibraryBookCollectionChange membership)
            throw new InvalidOperationException("The shelves could not be mapped onto the book.");

        if (membership.Added.Count == wanted.Count && wanted.All(membership.Added.Contains))
            return;

        batchRow.ChangesJson = JsonSerializer.Serialize(
            Deserialize(batchRow.ChangesJson) with { AddedCollectionIds = membership.Added },
            SnapshotJson);
        await _db.SaveChangesAsync(ct);
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
        var batches = await ImportBatchUndo.ListBatchesAsync(_db, source: null, take, ct);
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
                await RestoreAsync(bookId, changes, ct);

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

        var createdCollections = batch.Books
            .SelectMany(row => Deserialize(row.ChangesJson).CreatedCollectionIds ?? [])
            .Distinct()
            .ToList();
        var createdBooks = batch.Books
            .Where(row => row.CreatedBookId is not null)
            .Select(row => new ImportBatchUndo.CreatedBook(row.CreatedBookId!.Value))
            .ToList();

        _db.BookImportBatches.Remove(batch);
        await _db.SaveChangesAsync(ct);

        await ImportBatchUndo.RemoveCreatedCollectionsAsync(_db, _library, ClientId, createdCollections, ct);
        await ImportBatchUndo.RemoveCreatedBooksAsync(_db, _library, storage: null, createdBooks, ct);
        return batch.Books.Count;
    }

    /// <summary>
    /// Puts back only a value that still says what this import wrote, so a later
    /// edit by the owner is never overwritten; the edition keeps an exception
    /// that still differs from the book's, as the roll-up rule requires.
    /// </summary>
    private async Task RestoreAsync(Guid bookId, BookImportSnapshot changes, CancellationToken ct)
    {
        var book = await _db.Books
            .AsNoTracking()
            .Where(candidate => candidate.Id == bookId)
            .Select(candidate => new
            {
                WorkRating = candidate.Work!.Rating,
                WorkReview = candidate.Work.PersonalReview,
                EditionRating = candidate.Progress.Rating,
                EditionReview = candidate.Progress.PersonalReview,
                FinishedAt = candidate.Progress.FinishedAt,
            })
            .FirstOrDefaultAsync(ct);
        if (book is null)
            return;

        var rating = changes.RatingBefore != changes.RatingAfter && book.WorkRating == changes.RatingAfter
            ? changes.RatingBefore
            : (int?)null;
        var review = !string.Equals(changes.ReviewBefore, changes.ReviewAfter, StringComparison.Ordinal)
            && string.Equals(book.WorkReview, changes.ReviewAfter, StringComparison.Ordinal)
                ? changes.ReviewBefore ?? string.Empty
                : null;
        var editionRating = changes.EditionRatingBefore != changes.EditionRatingAfter
            && book.EditionRating == changes.EditionRatingAfter
                ? changes.EditionRatingBefore
                : (int?)null;
        var editionReview = !string.Equals(changes.EditionReviewBefore, changes.EditionReviewAfter, StringComparison.Ordinal)
            && string.Equals(book.EditionReview, changes.EditionReviewAfter, StringComparison.Ordinal)
                ? changes.EditionReviewBefore ?? string.Empty
                : null;

        // The finish date is never cleared by a null value (that means "leave
        // it"), so a date the import added goes with IsFinished.
        var finishedAt = changes.FinishedAtBefore != changes.FinishedAtAfter
            && book.FinishedAt == changes.FinishedAtAfter
                ? changes.FinishedAtBefore
                : null;
        var isFinished = changes.FinishedAtBefore != changes.FinishedAtAfter
            && book.FinishedAt == changes.FinishedAtAfter
            && changes.FinishedAtBefore is null
                ? false
                : (bool?)null;

        // The roll-up invariant again: an edition value that now says the same
        // as the work is not an exception.
        var workRatingAfter = rating ?? book.WorkRating;
        var workReviewAfter = review is null ? book.WorkReview : NullIfEmpty(review);

        var editionRatingAfter = BookValueRollUp.EditionRatingAfter(
            editionRating ?? book.EditionRating, workRatingAfter);
        editionRating = editionRatingAfter == book.EditionRating ? null : editionRatingAfter;

        var editionReviewAfter = BookValueRollUp.EditionReviewAfter(
            editionReview ?? book.EditionReview, workReviewAfter);
        editionReview = string.Equals(editionReviewAfter, book.EditionReview, StringComparison.Ordinal)
            ? null
            : editionReviewAfter ?? string.Empty;

        if (rating is not null || review is not null || editionRating is not null
            || editionReview is not null || finishedAt is not null || isFinished is not null)
        {
            await _library.UpdateBookAsync(
                new LibraryUpdateBookRequest(
                    ClientId,
                    $"{ClientId}-{Guid.NewGuid():N}",
                    bookId,
                    Rating: rating,
                    PersonalReview: review,
                    FinishedAt: finishedAt,
                    IsFinished: isFinished,
                    EditionRating: editionRating,
                    EditionReview: editionReview),
                ct);
        }

        if (changes.AddedCollectionIds is { Count: > 0 } addedIds)
            await _library.ApplyBookCollectionChangesAsync(bookId, [], addedIds, ct);
    }

    private static string? NullIfEmpty(string value) => value.Trim().Length == 0 ? null : value;

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

    /// <summary>The library values one row asks for, decided before any write.</summary>
    private sealed record ImportedValues(
        int Rating,
        string? Review,
        int EditionRating,
        string? EditionReview,
        DateTime? FinishedAt);

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
