using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.BookUpload;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Import a Calibre library in two steps (issue #1527): <see cref="PreviewAsync"/>
/// reads the library the host exposes and proposes, per Calibre book, the
/// library book it would join and the formats it would bring, writing nothing;
/// <see cref="CommitAsync"/> brings exactly the formats the owner decided on.
/// Each commit is one <see cref="BookImportBatch"/>, so <see cref="UndoAsync"/>
/// takes the whole import back.
///
/// Calibre's model is mapped where Nostos keeps the same thing: title, authors,
/// series and index, publisher, ISBN, rating, comments and tags are metadata,
/// the read date is the edition's finish date, and a tag becomes a collection
/// only when the owner asks. Nostos holds one file per book, so a Calibre book
/// with several formats offers each as a separate choice; choosing more than
/// one brings them in as editions of the same work. The library itself is never
/// written to: the source only ever opens files for reading.
/// </summary>
public sealed class CalibreImportService(
    NostosDbContext db,
    ILibraryService library,
    IBookAssetStorage storage,
    ICalibreLibrarySource source,
    BookFileCommit fileCommit,
    ILogger<CalibreImportService> logger)
{
    public const string ClientId = "calibre-import";
    public const string SourceName = "calibre";

    // What Nostos can read, best first. The first one is the format chosen on
    // the review screen; the rest are offered, never assumed.
    private static readonly string[] FormatPreference = ["EPUB", "PDF", "M4B", "M4A", "MP3"];

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------
    // Preview
    // ------------------------------------------------------------------

    public async Task<CalibreImportPreview> PreviewAsync(CancellationToken ct = default)
    {
        var library = await source.ReadAsync(ct);
        var editions = await LoadEditionsAsync(ct);
        var matcherLibrary = ToMatcherLibrary(editions);
        var byId = editions.ToDictionary(edition => edition.Id);
        var byWork = editions
            .GroupBy(edition => edition.WorkId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var books = new List<CalibrePreviewBook>(library.Books.Count);
        var skipped = new List<CalibreSkipped>(library.Skipped);

        foreach (var book in library.Books)
        {
            ct.ThrowIfCancellationRequested();

            var (match, candidates) = Match(book, matcherLibrary);
            var matched = match == HighlightImportMatcher.Exact && candidates.Count > 0
                ? byId.GetValueOrDefault(candidates[0].BookId)
                : null;
            var siblings = matched is null ? [] : byWork.GetValueOrDefault(matched.WorkId, []);
            var preferred = PreferredFormat(book);

            var formats = new List<CalibreFormatChoice>();
            var unsupported = new List<CalibreUnsupportedFormat>();
            foreach (var file in book.Formats)
            {
                if (!IsSupported(file))
                {
                    unsupported.Add(new CalibreUnsupportedFormat(
                        file.FileName, file.Format, "Nostos cannot read this format."));
                    continue;
                }

                if (!file.Readable)
                {
                    unsupported.Add(new CalibreUnsupportedFormat(
                        file.FileName, file.Format, file.Problem ?? "The file could not be read."));
                    continue;
                }

                var existing = siblings.FirstOrDefault(edition =>
                    edition.HasFile && HasFormat(edition.FileName, file.Format));
                formats.Add(new CalibreFormatChoice(
                    file.Key,
                    file.Format,
                    file.FileName,
                    file.Bytes,
                    file.Kind,
                    AlreadyHere: existing is not null,
                    ExistingBookId: existing?.Id,
                    SelectedByDefault: existing is null && file.Format == preferred));
            }

            if (formats.Count == 0)
            {
                skipped.Add(new CalibreSkipped(
                    book.Title,
                    book.Author,
                    1,
                    unsupported.Count == 0
                        ? "Calibre records no book file for this entry."
                        : UnsupportedReason(unsupported)));
                continue;
            }

            books.Add(new CalibrePreviewBook(
                BookKey(book.Id),
                book.Id,
                book.Title,
                book.Author,
                book.Isbn,
                book.Series,
                book.VolumeNumber,
                book.Publisher,
                book.PublishedDate,
                book.Rating,
                book.Description,
                book.CoverPath is not null,
                book.Tags,
                book.ReadAt,
                ReadNotice(book),
                match,
                matched?.Id,
                candidates,
                formats,
                unsupported,
                matched?.Title,
                book.Notices.Count == 0 ? null : string.Join(" ", book.Notices)));
        }

        var summary = new CalibrePreviewSummary(
            Books: books.Count,
            Ready: books.Count(book => book.Match != HighlightImportMatcher.Suggested
                && book.Formats.Any(format => !format.AlreadyHere)),
            InLibrary: books.Count(book => book.Match == HighlightImportMatcher.Exact
                && book.Formats.Any(format => !format.AlreadyHere)),
            AlreadyHere: books.Count(book => book.Formats.All(format => format.AlreadyHere)),
            NeedsReview: books.Count(book => book.Match == HighlightImportMatcher.Suggested
                && book.Formats.Any(format => !format.AlreadyHere)),
            Skipped: skipped.Sum(entry => entry.Count),
            Formats: books.Sum(book => book.Formats.Count),
            UnsupportedFormats: books.Sum(book => book.UnsupportedFormats.Count),
            Truncated: library.Truncated);

        return new CalibreImportPreview(
            SourceName,
            summary,
            books,
            skipped,
            library.CustomColumns,
            library.Warnings);
    }

    private static string UnsupportedReason(IReadOnlyList<CalibreUnsupportedFormat> unsupported)
    {
        // A file that exists but cannot be read says so specifically; only the
        // formats Nostos has no reader for get the generic sentence.
        if (unsupported.FirstOrDefault(file => !file.Reason.StartsWith("Nostos cannot read", StringComparison.Ordinal))
            is { } unreadable)
        {
            return unreadable.Reason;
        }

        var formats = unsupported
            .Select(file => file.Format)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(format => format, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return formats.Count == 0
            ? "Nostos cannot read the files Calibre records for this entry."
            : $"Nostos cannot read {string.Join(", ", formats)}.";
    }

    /// <summary>
    /// A read flag without a date cannot become a finished book: Nostos derives
    /// "finished" from the finish date, so the import says so rather than
    /// inventing one.
    /// </summary>
    private static string? ReadNotice(CalibreSourceBook book) =>
        book.Read == true && book.ReadAt is null
            ? "Calibre says this book was read, but has no read date. Nostos shows a book as finished by its read date, so it will not be marked finished."
            : null;

    // ------------------------------------------------------------------
    // Commit
    // ------------------------------------------------------------------

    public async Task<CalibreImportResult> CommitAsync(
        IReadOnlyList<CalibreImportDecision> decisions,
        Action<CalibreCommitProgress>? progress = null,
        CancellationToken ct = default)
    {
        var library = await source.ReadAsync(ct);
        var decisionsByKey = decisions
            .GroupBy(decision => decision.SourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var booksByKey = library.Books.ToDictionary(book => BookKey(book.Id), StringComparer.Ordinal);

        // The source order first, so a run is stable; a key the library no
        // longer has is still answered, never ignored.
        var work = library.Books
            .Where(book => decisionsByKey.ContainsKey(BookKey(book.Id)))
            .Select(book => (Book: (CalibreSourceBook?)book, Decision: decisionsByKey[BookKey(book.Id)]))
            .ToList();
        work.AddRange(decisionsByKey
            .Where(pair => !booksByKey.ContainsKey(pair.Key))
            .Select(pair => (Book: (CalibreSourceBook?)null, Decision: pair.Value)));

        var context = new CommitContext
        {
            Editions = await LoadEditionsAsync(ct),
            Collections = new ImportCollectionIndex(await db.Collections.ToListAsync(ct)),
            Batch = new BookImportBatch { Source = SourceName },
        };
        context.ById = context.Editions.ToDictionary(edition => edition.Id);
        context.ByWork = context.Editions
            .GroupBy(edition => edition.WorkId)
            .ToDictionary(group => group.Key, group => group.ToList());
        context.MatcherLibrary = ToMatcherLibrary(context.Editions);

        var results = new List<CalibreImportResultBook>(work.Count);
        var completed = 0;

        foreach (var (book, decision) in work)
        {
            ct.ThrowIfCancellationRequested();
            CalibreImportResultBook result;
            if (book is null)
            {
                result = MissingResult(decision.SourceKey);
            }
            else
            {
                try
                {
                    result = await ImportBookAsync(book, decision, context, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One damaged or vanished file must not stop the rest; the
                    // rows already written stay in the batch and remain undoable.
                    logger.LogWarning(
                        "Importing Calibre book {CalibreId} failed; exception type {ExceptionType}. Details suppressed.",
                        book.Id,
                        ex.GetType().Name);
                    result = new CalibreImportResultBook(
                        BookKey(book.Id), book.Id, book.Title, "failed", null, null, 0, false,
                        "This book could not be imported.");
                }
            }

            results.Add(result);
            completed++;
            progress?.Invoke(new CalibreCommitProgress(
                completed,
                work.Count,
                book?.Title,
                context.BatchAdded ? context.Batch.Id : null,
                result));
        }

        if (context.Batch.Books.Count == 0 && context.BatchAdded)
            db.BookImportBatches.Remove(context.Batch);
        await SaveWithRetryAsync(ct);

        return new CalibreImportResult(
            context.BatchAdded && context.Batch.Books.Count > 0 ? context.Batch.Id : null,
            SourceName,
            results);
    }

    private static CalibreImportResultBook MissingResult(string sourceKey) =>
        new(sourceKey, 0, string.Empty, "skipped", null, null, 0, false,
            "This book is no longer in the Calibre library. Scan again.");

    private async Task<CalibreImportResultBook> ImportBookAsync(
        CalibreSourceBook book,
        CalibreImportDecision decision,
        CommitContext context,
        CancellationToken ct)
    {
        var chosen = (decision.Formats ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var formats = book.Formats
            .Where(file => chosen.Contains(file.Key) && IsSupported(file) && file.Readable)
            .OrderBy(file => FormatOrder(file.Format))
            .ToList();
        if (formats.Count == 0)
        {
            var unsupported = book.Formats.Where(file => !IsSupported(file) || !file.Readable).ToList();
            return new CalibreImportResultBook(
                BookKey(book.Id), book.Id, book.Title, "skipped", null, null, 0, false,
                unsupported.Count > 0
                    ? UnsupportedReason(unsupported.Select(file => new CalibreUnsupportedFormat(
                        file.FileName, file.Format, file.Problem ?? "Nostos cannot read this format.")).ToList())
                    : "No format was chosen for this book.");
        }

        var (match, candidates) = Match(book, context.MatcherLibrary);
        LibraryEdition? target = null;
        if (decision.BookId is { } requestedId && context.ById.TryGetValue(requestedId, out var requested))
            target = requested;
        else if (match == HighlightImportMatcher.Exact && candidates.Count > 0
            && context.ById.TryGetValue(candidates[0].BookId, out var exact))
            target = exact;

        Guid? workId = target?.WorkId;
        var imported = 0;
        var alreadyHere = 0;
        var created = false;
        Guid? firstBookId = null;
        var messages = new List<string>();

        foreach (var file in formats)
        {
            ct.ThrowIfCancellationRequested();

            var kind = KindOf(file.Format);
            if (FindWithFormat(context, workId, file.Format) is not null)
            {
                alreadyHere++;
                continue;
            }

            // A write can meet another writer in this process (the book-text
            // indexer, a backup). SQLite reports that as a transient lock; a
            // short retry with a fresh read is enough, and never loses a book.
            for (var attempt = 0; ; attempt++)
            {
                var attachTo = FindAwaitingFile(context, workId, kind);
                Guid bookId;
                var wasCreated = attachTo is null;
                LibraryEdition edition;
                if (attachTo is not null)
                {
                    bookId = attachTo.Id;
                    edition = attachTo;
                }
                else
                {
                    var (createdId, createdEdition) = await CreateEditionAsync(book, file, context, ct);
                    bookId = createdId;
                    edition = createdEdition;
                }

                // The undo row exists before the file does: a crash between the
                // two leaves a no-op detach, never an orphaned file no batch can
                // take back.
                var row = AddBatchRow(context, book, file, bookId, wasCreated, new CalibreBatchChange(
                    AttachedToExisting: !wasCreated,
                    StoredFileName: "book" + Path.GetExtension(file.FileName).ToLowerInvariant()));
                try
                {
                    var attached = await AttachWithRetryAsync(bookId, book, file, ct);
                    var changes = Deserialize(row.ChangesJson) with
                    {
                        StoredFileName = attached.StoredFileName,
                        EntityTag = attached.EntityTag,
                        CoverAdded = attached.CoverAdded,
                        CoverName = attached.CoverName,
                        CoverEntityTag = attached.CoverEntityTag,
                    };
                    row.ChangesJson = JsonSerializer.Serialize(changes, SnapshotJson);
                    await SaveWithRetryAsync(ct);

                    var (added, createdCollections) = await AddCollectionsAsync(
                        bookId, book.Tags, decision.CreateCollections, context, ct);
                    changes = changes with
                    {
                        AddedCollectionIds = added,
                        CreatedCollectionIds = createdCollections,
                    };
                    row.ChangesJson = JsonSerializer.Serialize(changes, SnapshotJson);
                    await SaveWithRetryAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        "Writing Calibre file {Format} for book {CalibreId} failed; exception type {ExceptionType}. Details suppressed.",
                        file.Format,
                        book.Id,
                        ex.GetType().Name);

                    // A failed format must leave no half-created book behind.
                    await RecoverFailedFormatAsync(context, row, bookId, wasCreated, ct);

                    if (IsTransientLock(ex) && attempt < 2)
                    {
                        await Task.Delay(300 * (attempt + 1), ct);
                        await ReloadContextAsync(context, ct);
                        continue;
                    }

                    messages.Add($"{file.Format} could not be imported.");
                    break;
                }

                edition.HasFile = true;
                edition.FileName = "book" + Path.GetExtension(file.FileName).ToLowerInvariant();
                if (wasCreated) created = true;
                firstBookId ??= bookId;
                imported++;
                break;
            }
        }

        var totalFiles = imported + alreadyHere;
        if (imported == 0)
        {
            if (alreadyHere > 0)
            {
                return new CalibreImportResultBook(
                    BookKey(book.Id), book.Id, book.Title, "unchanged",
                    target?.Id, target?.Title, 0, false, "Already in Nostos.");
            }

            return new CalibreImportResultBook(
                BookKey(book.Id), book.Id, book.Title, "skipped", target?.Id, target?.Title, 0, false,
                messages.Count > 0 ? string.Join(" ", messages) : "Nothing was imported for this book.");
        }

        return new CalibreImportResultBook(
            BookKey(book.Id),
            book.Id,
            book.Title,
            "imported",
            firstBookId,
            target?.Title ?? book.Title,
            imported,
            created,
            messages.Count > 0 ? string.Join(" ", messages) : null);
    }

    private async Task<(Guid BookId, LibraryEdition Edition)> CreateEditionAsync(
        CalibreSourceBook book,
        CalibreSourceFormat file,
        CommitContext context,
        CancellationToken ct)
    {
        var kind = KindOf(file.Format);
        var normalizedTitle = BookIdentityNormalizer.NormalizeTitle(book.Title);
        var normalizedAuthor = BookIdentityNormalizer.NormalizeAuthor(book.Author);

        // A new edition joins an existing work by title and author. Its rating
        // and read date are only imported when nothing already says them: the
        // library's values are not displaced by a second copy arriving.
        var workExists = await db.Works.AsNoTracking().AnyAsync(work =>
                work.NormalizedTitle == normalizedTitle
                && work.NormalizedAuthor == (string.IsNullOrEmpty(normalizedAuthor) ? null : normalizedAuthor), ct);
        var workFinished = workExists && await db.Books.AsNoTracking().AnyAsync(candidate =>
            candidate.Work!.NormalizedTitle == normalizedTitle
            && candidate.Work.NormalizedAuthor == (string.IsNullOrEmpty(normalizedAuthor) ? null : normalizedAuthor)
            && candidate.Progress.FinishedAt != null, ct);

        var normalizedIsbn = BookIdentityNormalizer.NormalizeIsbn(book.Isbn);
        var isbn = kind == "audiobook" ? null : book.Isbn;
        if (normalizedIsbn is not null && await db.Books.AsNoTracking()
                .AnyAsync(candidate => candidate.NormalizedIsbn == normalizedIsbn, ct))
        {
            // The identifier belongs to the edition already here; a second copy
            // cannot carry it without violating the identity index.
            isbn = null;
        }

        var outcome = await library.CreateOrMatchBookAsync(
            new LibraryCreateBookRequest(
                ClientId,
                $"{ClientId}-{Guid.NewGuid():N}",
                kind,
                book.Title,
                Author: book.Author,
                Description: book.Description,
                Isbn: isbn,
                Publisher: book.Publisher,
                PublishedDate: book.PublishedDate,
                Series: book.Series,
                VolumeNumber: book.VolumeNumber,
                Categories: book.Tags.Count > 0 ? string.Join(", ", book.Tags) : null,
                Rating: workExists ? 0 : book.Rating,
                FinishedAt: workFinished ? null : book.ReadAt,
                ForceCreate: true,
                FileUploadExpected: true),
            strictConfirmation: false,
            ct);
        if (outcome.Data is not LibraryCreateOrMatchResultDto { BookId: { } bookId })
            throw new CalibreLibraryException("This book could not be added to the library.");

        var created = await db.Books.SingleAsync(candidate => candidate.Id == bookId, ct);
        var edition = new LibraryEdition(
            created.Id,
            created.WorkId,
            created.Title,
            created.Author,
            created.NormalizedIsbn,
            kind,
            HighlightImportBookFormat.Of(kind == "audiobook", false, "book." + file.Format.ToLowerInvariant()),
            hasFile: false,
            fileName: null,
            created.Status,
            finishedAt: null);

        context.Editions.Add(edition);
        context.ById[edition.Id] = edition;
        if (!context.ByWork.TryGetValue(edition.WorkId, out var siblings))
        {
            siblings = [];
            context.ByWork[edition.WorkId] = siblings;
        }

        siblings.Add(edition);
        context.MatcherLibrary.Add(new ImportLibraryBook(
            edition.Id, edition.Title, edition.Author, edition.NormalizedIsbn, edition.Format));
        return (bookId, edition);
    }

    /// <summary>
    /// Writes the file, then its cover when the book has none. The bytes are
    /// copied: unlike external media, a Calibre import brings the book into
    /// Nostos, so removing it later never reaches into the library folder.
    /// </summary>
    private async Task<AttachedFile> AttachAsync(
        Guid bookId,
        CalibreSourceBook sourceBook,
        CalibreSourceFormat file,
        CancellationToken ct)
    {
        // The preview may be minutes old; check the bytes that will be copied.
        await using (var check = source.OpenRead(file)
            ?? throw new CalibreLibraryException("The file is no longer in the Calibre library."))
        {
            if (BookFileValidator.Validate(check, file.FileName) is { } problem)
                throw new CalibreLibraryException(problem);
        }

        var storedName = "book" + Path.GetExtension(file.FileName).ToLowerInvariant();
        var book = await db.Books.SingleAsync(candidate => candidate.Id == bookId, ct);
        await fileCommit.CommitStreamAsync(
            book,
            () => source.OpenRead(file)
                ?? throw new CalibreLibraryException("The file is no longer in the Calibre library."),
            file.FileName,
            ct);

        var coverAdded = false;
        string? coverName = null;
        string? coverEntityTag = null;
        if (string.IsNullOrEmpty(book.FileDetails.CoverFileName) && sourceBook.CoverPath is not null)
        {
            var extension = source.CoverExtension ?? ".jpg";
            coverName = "cover" + extension.ToLowerInvariant();
            await using var cover = source.OpenCover(sourceBook);
            if (cover is not null)
            {
                await storage.SaveBookCoverAsync(bookId, cover, coverName, ct);
                book.FileDetails.SetCover(coverName);
                await SaveWithRetryAsync(ct);
                coverAdded = true;
                coverEntityTag = (await storage.GetBookCoverInfoAsync(bookId, ct))?.EntityTag;
            }
            else
            {
                coverName = null;
            }
        }

        var info = await storage.GetBookFileInfoAsync(bookId, ct);
        return new AttachedFile(
            book.FileDetails.FileName ?? storedName,
            info?.EntityTag,
            coverAdded,
            coverName,
            coverEntityTag);
    }

    /// <summary>
    /// A lock from another writer in this process (the book-text indexer, a
    /// backup) is transient. The book's bytes are written again over the same
    /// name, so a retry cannot duplicate anything.
    /// </summary>
    private async Task<AttachedFile> AttachWithRetryAsync(
        Guid bookId,
        CalibreSourceBook sourceBook,
        CalibreSourceFormat file,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await AttachAsync(bookId, sourceBook, file, ct);
            }
            catch (DbUpdateException ex) when (IsTransientLock(ex) && attempt < 2)
            {
                await Task.Delay(300 * (attempt + 1), ct);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task SaveWithRetryAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (IsTransientLock(ex) && attempt < 3)
            {
                await Task.Delay(250 * (attempt + 1), ct);
            }
        }
    }

    /// <summary>
    /// Undoes a format that failed part-way: a book this import created is
    /// removed again, and its batch row goes with it. A book that was already
    /// here keeps everything it had.
    /// </summary>
    private async Task RecoverFailedFormatAsync(
        CommitContext context,
        BookImportBatchBook row,
        Guid bookId,
        bool created,
        CancellationToken ct)
    {
        context.Batch.Books.Remove(row);
        context.Batch.ChangeCount = Math.Max(0, context.Batch.ChangeCount - 1);

        if (created)
        {
            var deleted = await library.DeleteBookAsync(bookId, CancellationToken.None);
            if (deleted.Data is not null)
                await storage.DeleteBookFilesAsync(bookId, CancellationToken.None);
            context.Editions.RemoveAll(edition => edition.Id == bookId);
            context.ById.Remove(bookId);
            foreach (var siblings in context.ByWork.Values)
                siblings.RemoveAll(edition => edition.Id == bookId);
            context.MatcherLibrary.RemoveAll(entry => entry.Id == bookId);
            db.ChangeTracker.Clear();
            return;
        }

        await storage.DeleteBookFileAsync(bookId, CancellationToken.None);
        db.ChangeTracker.Clear();
        var book = await db.Books.FirstOrDefaultAsync(candidate => candidate.Id == bookId, ct);
        if (book is { FileDetails.HasFile: true })
        {
            book.FileDetails.HasFile = false;
            book.FileDetails.FileName = null;
            book.FileDetails.LocationsJson = null;
            book.FileDetails.ChaptersJson = null;
            await SaveWithRetryAsync(ct);
        }
    }

    /// <summary>Fresh reads after a failed save, so a retry never works from a tainted tracker.</summary>
    private async Task ReloadContextAsync(CommitContext context, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        context.Editions = await LoadEditionsAsync(ct);
        context.ById = context.Editions.ToDictionary(edition => edition.Id);
        context.ByWork = context.Editions
            .GroupBy(edition => edition.WorkId)
            .ToDictionary(group => group.Key, group => group.ToList());
        context.MatcherLibrary = ToMatcherLibrary(context.Editions);
        context.Collections = new ImportCollectionIndex(await db.Collections.ToListAsync(ct));
    }

    private static bool IsTransientLock(Exception ex) =>
        ex is SqliteException { SqliteErrorCode: 5 }
        || ex is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 } };

    private BookImportBatchBook AddBatchRow(
        CommitContext context,
        CalibreSourceBook book,
        CalibreSourceFormat file,
        Guid bookId,
        bool created,
        CalibreBatchChange changes)
    {
        if (!context.BatchAdded)
        {
            db.BookImportBatches.Add(context.Batch);
            context.BatchAdded = true;
        }

        var row = new BookImportBatchBook
        {
            BatchId = context.Batch.Id,
            BookId = bookId,
            CreatedBookId = created ? bookId : null,
            SourceKey = file.Key.Length <= 1024 ? file.Key : file.Key[..1024],
            ChangesJson = JsonSerializer.Serialize(changes, SnapshotJson),
        };
        context.Batch.Books.Add(row);
        // Explicit Add: once the batch itself is saved, a child with a generated
        // key added through the navigation would be inferred as an existing row.
        db.BookImportBatchBooks.Add(row);
        context.Batch.ChangeCount += 1;
        return row;
    }

    private static LibraryEdition? FindWithFormat(CommitContext context, Guid? workId, string format) =>
        workId is null || !context.ByWork.TryGetValue(workId.Value, out var siblings)
            ? null
            : siblings.FirstOrDefault(edition => edition.HasFile && HasFormat(edition.FileName, format));

    private static LibraryEdition? FindAwaitingFile(CommitContext context, Guid? workId, string kind) =>
        workId is null || !context.ByWork.TryGetValue(workId.Value, out var siblings)
            ? null
            : siblings.FirstOrDefault(edition =>
                edition.Kind == kind
                && !edition.HasFile
                && edition.Status is not (BookStatus.Downloading or BookStatus.Transcoding or BookStatus.UploadPending));

    /// <summary>
    /// The book's tags become collections only when the owner asked. A
    /// collection already here is reused by its normalized name, a missing one
    /// is created through <see cref="ILibraryService"/>, and the membership is
    /// added the same way: the batch records the rows that actually changed, so
    /// undo takes back exactly what the import brought.
    /// </summary>
    private async Task<(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Created)> AddCollectionsAsync(
        Guid bookId,
        IReadOnlyList<string> tags,
        bool enabled,
        CommitContext context,
        CancellationToken ct)
    {
        if (!enabled || tags.Count == 0)
            return ([], []);

        var memberIds = await db.BookCollections
            .AsNoTracking()
            .Where(membership => membership.BookId == bookId)
            .Select(membership => membership.CollectionId)
            .ToListAsync(ct);
        var memberNames = memberIds
            .Select(id => context.Collections.FindById(id)?.Name)
            .Where(name => name is not null)
            .Select(name => BookIdentityNormalizer.NormalizeTitle(name!))
            .ToHashSet(StringComparer.Ordinal);

        var wanted = new List<Guid>();
        var created = new List<Guid>();
        foreach (var tag in tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0))
        {
            var normalized = BookIdentityNormalizer.NormalizeTitle(tag);
            if (!memberNames.Add(normalized))
                continue;

            var (collection, wasCreated) = await context.Collections.EnsureAsync(tag, library, ClientId, ct);
            if (wasCreated)
                created.Add(collection.Id);
            wanted.Add(collection.Id);
        }

        var change = await library.ApplyBookCollectionChangesAsync(bookId, wanted, [], ct);
        if (change.Data is not LibraryBookCollectionChange membership)
            throw new CalibreLibraryException("The tags could not be added to the book.");

        return (membership.Added, created);
    }

    // ------------------------------------------------------------------
    // Batches and undo
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<CalibreImportBatchSummary>> ListBatchesAsync(
        int take,
        CancellationToken ct = default)
    {
        var batches = await ImportBatchUndo.ListBatchesAsync(db, SourceName, take, ct);
        return batches
            .Select(batch => new CalibreImportBatchSummary(
                batch.Id, batch.Source, batch.FileName, batch.CreatedAtUtc, batch.BookCount, batch.ChangeCount))
            .ToList();
    }

    /// <summary>
    /// Takes a whole Calibre import back: the files it copied, the covers it
    /// added, the memberships and collections it created, and the books it
    /// added when nothing else has come to them. Values the owner changed since
    /// are never touched.
    /// </summary>
    public async Task<CalibreUndoResult?> UndoAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await db.BookImportBatches
            .Include(candidate => candidate.Books)
            .FirstOrDefaultAsync(candidate => candidate.Id == batchId && candidate.Source == SourceName, ct);
        if (batch is null)
            return null;

        var createdChanges = new Dictionary<Guid, CalibreBatchChange>();
        var createdCollections = new HashSet<Guid>();

        foreach (var row in batch.Books)
        {
            var changes = Deserialize(row.ChangesJson);
            if (changes.CreatedCollectionIds is { Count: > 0 } ids)
                createdCollections.UnionWith(ids);

            if (row.CreatedBookId is { } createdId)
            {
                createdChanges[createdId] = changes;
                continue;
            }

            if (changes.AttachedToExisting && row.BookId is { } bookId)
                await DetachAsync(bookId, changes, ct);
        }

        var removed = batch.Books.Count;
        db.BookImportBatches.Remove(batch);
        await db.SaveChangesAsync(ct);

        await ImportBatchUndo.RemoveCreatedCollectionsAsync(db, library, ClientId, createdCollections, ct);
        var kept = await ImportBatchUndo.RemoveCreatedBooksAsync(
            db,
            library,
            storage,
            createdChanges
                .Select(created => new ImportBatchUndo.CreatedBook(created.Key, created.Value.EntityTag))
                .ToList(),
            ct);
        return new CalibreUndoResult(removed, kept);
    }

    private async Task DetachAsync(Guid bookId, CalibreBatchChange changes, CancellationToken ct)
    {
        var book = await db.Books
            .FirstOrDefaultAsync(candidate => candidate.Id == bookId, ct);
        if (book is null)
            return;

        // Only the file this import wrote is removed. A replacement since then
        // is the owner's, and the tag no longer matches the one recorded.
        var info = await storage.GetBookFileInfoAsync(bookId, ct);
        var fileStillOurs = changes.StoredFileName is not null
            && book.FileDetails.HasFile
            && string.Equals(book.FileDetails.FileName, changes.StoredFileName, StringComparison.OrdinalIgnoreCase)
            && (changes.EntityTag is null || string.Equals(info?.EntityTag, changes.EntityTag, StringComparison.Ordinal));
        if (fileStillOurs)
        {
            await storage.DeleteBookFileAsync(bookId, ct);
            book.FileDetails.HasFile = false;
            book.FileDetails.FileName = null;
            book.FileDetails.LocationsJson = null;
            book.FileDetails.ChaptersJson = null;
        }

        var coverInfo = changes.CoverAdded
            ? await storage.GetBookCoverInfoAsync(bookId, ct)
            : null;
        if (changes.CoverAdded
            && changes.CoverName is not null
            && string.Equals(book.FileDetails.CoverFileName, changes.CoverName, StringComparison.OrdinalIgnoreCase)
            && (changes.CoverEntityTag is null
                || string.Equals(coverInfo?.EntityTag, changes.CoverEntityTag, StringComparison.Ordinal)))
        {
            await storage.DeleteCoverAsync(bookId, ct);
            book.FileDetails.SetCover(null);
        }

        if (changes.AddedCollectionIds is { Count: > 0 } addedIds)
        {
            // Best effort: undo never fails on a book that is no longer there.
            await library.ApplyBookCollectionChangesAsync(bookId, [], addedIds, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    private static CalibreBatchChange Deserialize(string json) =>
        JsonSerializer.Deserialize<CalibreBatchChange>(json, SnapshotJson) ?? new CalibreBatchChange();

    // ------------------------------------------------------------------
    // Matching and library reading
    // ------------------------------------------------------------------

    private async Task<List<LibraryEdition>> LoadEditionsAsync(CancellationToken ct)
    {
        var rows = await db.Books
            .AsNoTracking()
            .Select(book => new
            {
                book.Id,
                book.WorkId,
                book.Title,
                book.Author,
                book.NormalizedIsbn,
                IsAudiobook = book is AudioBookModel,
                IsPhysical = book is PhysicalBookModel,
                book.FileDetails.HasFile,
                book.FileDetails.FileName,
                book.Status,
                book.Progress.FinishedAt,
            })
            .ToListAsync(ct);

        return rows
            .Select(row => new LibraryEdition(
                row.Id,
                row.WorkId,
                row.Title,
                row.Author,
                row.NormalizedIsbn,
                row.IsAudiobook ? "audiobook" : row.IsPhysical ? "physical" : "ebook",
                HighlightImportBookFormat.Of(row.IsAudiobook, row.IsPhysical, row.FileName),
                row.HasFile,
                row.FileName,
                row.Status,
                row.FinishedAt))
            .ToList();
    }

    private static List<ImportLibraryBook> ToMatcherLibrary(IReadOnlyList<LibraryEdition> editions) =>
        editions
            .Select(edition => new ImportLibraryBook(
                edition.Id,
                edition.Title,
                edition.Author,
                edition.NormalizedIsbn,
                edition.Format))
            .ToList();

    private static (string Match, IReadOnlyList<HighlightImportCandidate> Candidates) Match(
        CalibreSourceBook book,
        IReadOnlyList<ImportLibraryBook> library) =>
        HighlightImportMatcher.Match(
            new ImportSourceBook(BookKey(book.Id), book.Title, book.Author, book.Isbn, []),
            library);

    private static string BookKey(int calibreId) => $"{SourceName}:{calibreId}";

    private static string? PreferredFormat(CalibreSourceBook book) =>
        book.Formats
            .Where(file => IsSupported(file) && file.Readable)
            .OrderBy(file => FormatOrder(file.Format))
            .Select(file => file.Format)
            .FirstOrDefault();

    private static bool IsSupported(CalibreSourceFormat file) =>
        FormatPreference.Contains(file.Format, StringComparer.OrdinalIgnoreCase);

    private static int FormatOrder(string format) =>
        Array.FindIndex(FormatPreference, candidate => string.Equals(candidate, format, StringComparison.OrdinalIgnoreCase)) switch
        {
            -1 => FormatPreference.Length,
            var index => index,
        };

    private static string KindOf(string format) =>
        format.ToUpperInvariant() is "MP3" or "M4A" or "M4B" ? "audiobook" : "ebook";

    private static bool HasFormat(string? fileName, string format) =>
        fileName is not null
        && string.Equals(Path.GetExtension(fileName), "." + format.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

    private sealed class CommitContext
    {
        public required List<LibraryEdition> Editions { get; set; }
        public Dictionary<Guid, LibraryEdition> ById { get; set; } = [];
        public Dictionary<Guid, List<LibraryEdition>> ByWork { get; set; } = [];
        public List<ImportLibraryBook> MatcherLibrary { get; set; } = [];
        public required ImportCollectionIndex Collections { get; set; }
        public required BookImportBatch Batch { get; init; }
        public bool BatchAdded { get; set; }
    }

    private sealed class LibraryEdition(
        Guid id,
        Guid workId,
        string title,
        string? author,
        string? normalizedIsbn,
        string kind,
        string format,
        bool hasFile,
        string? fileName,
        BookStatus status,
        DateTime? finishedAt)
    {
        public Guid Id { get; } = id;
        public Guid WorkId { get; } = workId;
        public string Title { get; } = title;
        public string? Author { get; } = author;
        public string? NormalizedIsbn { get; } = normalizedIsbn;
        public string Kind { get; } = kind;
        public string Format { get; } = format;
        public BookStatus Status { get; } = status;
        public DateTime? FinishedAt { get; } = finishedAt;
        public bool HasFile { get; set; } = hasFile;
        public string? FileName { get; set; } = fileName;
    }

    private sealed record AttachedFile(
        string StoredFileName,
        string? EntityTag,
        bool CoverAdded,
        string? CoverName,
        string? CoverEntityTag);

    private sealed record CalibreBatchChange(
        bool AttachedToExisting = false,
        string? StoredFileName = null,
        string? EntityTag = null,
        bool CoverAdded = false,
        string? CoverName = null,
        string? CoverEntityTag = null,
        IReadOnlyList<Guid>? AddedCollectionIds = null,
        IReadOnlyList<Guid>? CreatedCollectionIds = null);
}
