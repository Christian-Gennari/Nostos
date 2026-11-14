using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Library.Imports;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Highlight import in two steps (issue #656; Kindle and Readwise, #1219): <see cref="PreviewAsync"/>
/// reads a file and proposes where each book goes without writing anything;
/// <see cref="CommitAsync"/> imports exactly the books the owner decided on.
/// Each commit is a batch that <see cref="UndoAsync"/> can take back.
/// </summary>
public sealed class HighlightImportService
{
    public const string Remembered = "remembered";
    // How much of a highlight the review step carries; the import keeps all of it.
    private const int SnippetLength = 400;
    private const string KoboClientId = "kobo-import";
    private const string KindleClientId = "kindle-import";
    private const string ReadwiseClientId = "readwise-import";
    private const string AppleBooksClientId = "applebooks-import";
    private readonly NostosDbContext _db;
    private readonly INoteService _notes;
    private readonly ILibraryService _library;
    private readonly ILogger<HighlightImportService> _logger;

    public HighlightImportService(
        NostosDbContext db,
        INoteService notes,
        ILibraryService library,
        ILogger<HighlightImportService> logger)
    {
        _db = db;
        _notes = notes;
        _library = library;
        _logger = logger;
    }

    public Task<HighlightImportPreview> PreviewAsync(string path, CancellationToken ct = default) =>
        PreviewAsync(path, [], ct);

    /// <param name="companions">Files chosen together with the main one (Apple Books' library database).</param>
    public async Task<HighlightImportPreview> PreviewAsync(string path, IReadOnlyList<string> companions, CancellationToken ct = default)
    {
        var file = ReadSource(path, companions);
        LogShape(file, path);
        return await PreviewSourceAsync(file, ct);
    }

    /// <summary>
    /// The same preview for a source that did not arrive as an uploaded file,
    /// such as one page set fetched from Readwise's export API (issue #1525).
    /// </summary>
    internal async Task<HighlightImportPreview> PreviewSourceAsync(ImportSourceFile file, CancellationToken ct = default)
    {
        var (source, sourceBooks, skipped) = file;

        var library = await LoadLibraryAsync(ct);
        var links = await LoadLinksAsync(source, sourceBooks, ct);

        var books = new List<HighlightImportPreviewBook>(sourceBooks.Count);
        foreach (var sourceBook in sourceBooks)
        {
            var (match, candidates) = HighlightImportMatcher.Match(sourceBook, library);
            Guid? bookId = match == HighlightImportMatcher.Exact ? candidates[0].BookId : null;

            // An earlier confirmation outranks whatever the names suggest now.
            if (links.TryGetValue(sourceBook.SourceKey, out var linkedId)
                && library.FirstOrDefault(book => book.Id == linkedId) is { } linked)
            {
                match = Remembered;
                bookId = linked.Id;
                candidates =
                [
                    new HighlightImportCandidate(linked.Id, linked.Title, linked.Author, "Confirmed on an earlier import", linked.Format),
                    .. candidates.Where(candidate => candidate.BookId != linked.Id),
                ];
            }

            var target = bookId ?? candidates.FirstOrDefault()?.BookId;
            var plan = target is null ? null : await PlanAsync(source, sourceBook, target.Value, ct);
            var newCount = plan is null ? sourceBook.Annotations.Count : plan.Count(entry => entry.IsNew);
            var highlights = sourceBook.Annotations
                .Select((annotation, index) => new HighlightImportPreviewHighlight(
                    index,
                    Truncate(annotation.Text, SnippetLength),
                    Truncate(annotation.Note, SnippetLength),
                    plan is null || plan[index].IsNew))
                .ToList();

            books.Add(new HighlightImportPreviewBook(
                sourceBook.SourceKey,
                sourceBook.Title,
                sourceBook.Author,
                sourceBook.Annotations.Count,
                newCount,
                match,
                bookId,
                candidates,
                sourceBook.Notice,
                highlights));
        }

        return new HighlightImportPreview(source, books, skipped);
    }

    public Task<HighlightImportResult> CommitAsync(
        string path,
        string? fileName,
        IReadOnlyList<HighlightImportDecision> decisions,
        Guid? batchId = null,
        CancellationToken ct = default) =>
        CommitAsync(path, [], fileName, decisions, batchId, ct);

    public async Task<HighlightImportResult> CommitAsync(
        string path,
        IReadOnlyList<string> companions,
        string? fileName,
        IReadOnlyList<HighlightImportDecision> decisions,
        Guid? batchId = null,
        CancellationToken ct = default)
    {
        var file = ReadSource(path, companions);
        return await CommitSourceAsync(file, fileName, decisions, batchId, ct);
    }

    /// <summary>
    /// The same commit for a source that did not arrive as an uploaded file.
    /// A Readwise API import (#1525) joins the same batch, dedupe and undo path.
    /// </summary>
    internal async Task<HighlightImportResult> CommitSourceAsync(
        ImportSourceFile file,
        string? fileName,
        IReadOnlyList<HighlightImportDecision> decisions,
        Guid? batchId = null,
        CancellationToken ct = default)
    {
        // The preview of this source already logged any missing columns; a
        // commit logging them again would count every import twice.
        var (source, sourceBooks, skipped) = file;

        // Every entry the source holds is found once: the annotations of its
        // books, plus what the reader reports as unimportable (a bookmark, a
        // web article, an entry past the clipping limit). The report below
        // must always account for all of them, whichever way the source arrived
        // (#1550 for files, #1525 for the Readwise export API).
        var found = sourceBooks.Sum(book => book.Annotations.Count) + skipped.Sum(entry => entry.Count);
        var importedTotal = 0;
        var duplicateTotal = 0;
        var leftOutTotal = 0;
        var booksMatched = 0;
        var booksCreated = 0;
        var booksLeftOut = 0;

        var bySourceKey = decisions
            .GroupBy(decision => decision.SourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        // Several files chosen together are one import to the owner, so later
        // files join the batch the first one opened.
        var batch = batchId is null
            ? null
            : await _db.NoteImportBatches.FirstOrDefaultAsync(candidate => candidate.Id == batchId, ct);
        var isNewBatch = batch is null;
        batch ??= new NoteImportBatch { Source = source, FileName = Truncate(fileName, 260) };
        // Held back until every note exists: the capture path saves and commits
        // on its own, and must not carry half a batch with it.
        var added = new List<NoteImportBatchNote>();
        var results = new List<HighlightImportResultBook>(sourceBooks.Count);

        foreach (var fullBook in sourceBooks)
        {
            bySourceKey.TryGetValue(fullBook.SourceKey, out var decision);
            if (decision is null || (decision.BookId is null && !decision.Create))
            {
                results.Add(Skipped(fullBook, null));
                leftOutTotal += fullBook.Annotations.Count;
                booksLeftOut++;
                continue;
            }

            // Highlights the owner unticked in the review are never read again.
            var excluded = decision.ExcludedHighlights?.ToHashSet() ?? [];
            var sourceBook = excluded.Count == 0
                ? fullBook
                : fullBook with { Annotations = fullBook.Annotations.Where((_, index) => !excluded.Contains(index)).ToList() };
            leftOutTotal += fullBook.Annotations.Count - sourceBook.Annotations.Count;
            if (sourceBook.Annotations.Count == 0)
            {
                results.Add(Skipped(sourceBook, "Every highlight was left out."));
                booksLeftOut++;
                continue;
            }

            var created = false;
            var bookId = decision.BookId;
            if (bookId is null)
            {
                var title = KoreaderNoteImportService.Clean(sourceBook.Title);
                if (title is null)
                {
                    results.Add(Skipped(sourceBook, "The file has no title for this book."));
                    leftOutTotal += sourceBook.Annotations.Count;
                    booksLeftOut++;
                    continue;
                }

                var outcome = await _library.CreateOrMatchBookAsync(
                    new LibraryCreateBookRequest(
                        "highlight-import",
                        $"highlight-import-{Guid.NewGuid():N}",
                        "physical",
                        title,
                        Author: sourceBook.Author,
                        Isbn: BookIdentityNormalizer.NormalizeIsbn(sourceBook.Isbn)),
                    strictConfirmation: false,
                    ct);
                if (outcome.Data is not LibraryCreateOrMatchResultDto { BookId: not null } createdBook)
                {
                    results.Add(Skipped(sourceBook, "The book could not be added to the library."));
                    leftOutTotal += sourceBook.Annotations.Count;
                    booksLeftOut++;
                    continue;
                }

                bookId = createdBook.BookId;
                created = createdBook.Outcome == "created";
            }

            var book = await _db.Books
                .AsNoTracking()
                .Where(candidate => candidate.Id == bookId.Value)
                .Select(candidate => new { candidate.Id, candidate.Title })
                .FirstOrDefaultAsync(ct);
            if (book is null)
            {
                results.Add(Skipped(sourceBook, "That book is no longer in the library."));
                leftOutTotal += sourceBook.Annotations.Count;
                booksLeftOut++;
                continue;
            }

            await RememberAsync(source, sourceBook.SourceKey, book.Id, ct);

            var imported = 0;
            var duplicates = 0;
            var repaired = 0;
            foreach (var entry in await PlanAsync(source, sourceBook, book.Id, ct))
            {
                if (entry.Repairs is { } earlierNoteId)
                {
                    await KoreaderNoteImportService.RepairAsync(
                        _db,
                        _notes,
                        earlierNoteId,
                        entry.Annotation.AnchorKind,
                        entry.AnchorValue,
                        entry.Annotation.Text,
                        entry.Annotation.Note,
                        ct);
                    repaired++;
                }

                if (!entry.IsNew)
                {
                    duplicates++;
                    continue;
                }

                var result = await _notes.CaptureAsync(new CaptureNoteRequest(
                    BookId: book.Id,
                    Content: entry.Annotation.Note,
                    SelectedText: entry.Annotation.Text,
                    CaptureSource: "import",
                    ProcessingMode: "verbatim",
                    SourceAnchorKind: entry.Annotation.AnchorKind,
                    SourceAnchorValue: entry.AnchorValue,
                    AnchorVerified: false,
                    // The e-reader's own colour names do not map to the four pens, so an
                    // import arrives in the default one and is recoloured by hand (#861).
                    HighlightColour: NoteHighlightColours.Default,
                    ClientId: entry.ClientId,
                    IdempotencyKey: entry.Key,
                    CreatedAt: entry.Annotation.CreatedAt),
                    ct);

                if (!result.Success)
                    throw new InvalidOperationException(result.ErrorMessage ?? "Highlight import failed.");

                added.Add(new NoteImportBatchNote
                {
                    BatchId = batch.Id,
                    NoteId = result.Value!.Id,
                    ClientId = entry.ClientId,
                    IdempotencyKey = entry.Key,
                    CreatedBookId = created ? book.Id : null,
                });
                imported++;
            }

            importedTotal += imported;
            duplicateTotal += duplicates;
            if (created)
                booksCreated++;
            else
                booksMatched++;

            results.Add(new HighlightImportResultBook(
                sourceBook.SourceKey,
                "imported",
                book.Id,
                book.Title,
                sourceBook.Title,
                sourceBook.Author,
                sourceBook.Annotations.Count,
                imported,
                duplicates,
                created,
                repaired == 0
                    ? null
                    : repaired == 1
                        ? "1 note from an earlier import was corrected."
                        : $"{repaired} notes from an earlier import were corrected."));
        }

        var report = new HighlightImportReport(
            found,
            importedTotal,
            duplicateTotal,
            skipped.Sum(entry => entry.Count),
            leftOutTotal,
            skipped,
            booksMatched,
            booksCreated,
            booksLeftOut);
        if (report.Found != report.Imported + report.Duplicates + report.Skipped + report.LeftOut)
        {
            // The stored report is only useful when it adds up. The tests assert
            // this for every source; logging keeps a production surprise visible.
            _logger.LogError(
                "Highlight import report does not add up: source {Source}, found {Found}, imported {Imported}, duplicates {Duplicates}, skipped {Skipped}, left out {LeftOut}.",
                source, report.Found, report.Imported, report.Duplicates, report.Skipped, report.LeftOut);
        }

        // A batch exists to be undone; one that added nothing has nothing to undo.
        if (isNewBatch && added.Count > 0)
        {
            batch.ReportJson = SerializeReport(report);
            _db.NoteImportBatches.Add(batch);
        }
        else if (!isNewBatch && batch.ReportJson is not null)
        {
            // Files chosen together are one batch, so their reports add up too.
            batch.ReportJson = SerializeReport(AddReports(ReadReport(batch.ReportJson), report));
        }

        _db.NoteImportBatchNotes.AddRange(added);
        await _db.SaveChangesAsync(ct);

        return new HighlightImportResult(!isNewBatch || added.Count > 0 ? batch.Id : null, source, results);
    }

    /// <summary>
    /// The passages a starter draft opens with (issue #1220), as the notes the
    /// Studio quotes: from the given imports, or, with none given, from the
    /// most recently imported highlights in the library.
    /// </summary>
    public async Task<IReadOnlyList<NoteSearchHitDto>> StarterPassagesAsync(
        IReadOnlyCollection<Guid> batchIds,
        CancellationToken ct = default)
    {
        var notes = batchIds.Count > 0
            ? _db.NoteImportBatchNotes
                .AsNoTracking()
                .Where(entry => batchIds.Contains(entry.BatchId))
                .Select(entry => entry.Note)
            : _db.Notes.AsNoTracking().Where(note => note.CaptureSource == "import");

        // Bounded: a whole Kindle history is thousands of highlights, and the
        // most recent ones are the ones the reader just brought in.
        var candidates = await notes
            .Where(note => note.SelectedText != null && note.SelectedText != "")
            .OrderByDescending(note => note.CreatedAt)
            .Take(StarterCandidateLimit)
            .Select(note => new StarterPassageCandidate(
                note.Id, note.BookId, note.SelectedText, note.Content, note.CreatedAt))
            .ToListAsync(ct);

        var picked = StarterPassageSelector.Pick(candidates);
        return picked.Count == 0
            ? []
            : await _notes.GetManyAsync(picked.Select(candidate => candidate.NoteId).ToList(), ct);
    }

    private const int StarterCandidateLimit = 2000;

    public async Task<IReadOnlyList<HighlightImportBatchSummary>> ListBatchesAsync(
        int take,
        CancellationToken ct = default)
    {
        var batches = await _db.NoteImportBatches
            .AsNoTracking()
            .Where(batch => batch.Notes.Count > 0)
            .OrderByDescending(batch => batch.CreatedAtUtc)
            .Take(take)
            .Select(batch => new
            {
                batch.Id,
                batch.Source,
                batch.FileName,
                batch.CreatedAtUtc,
                batch.ReportJson,
                NoteCount = batch.Notes.Count,
                BookCount = batch.Notes.Select(note => note.Note.BookId).Distinct().Count(),
            })
            .ToListAsync(ct);

        return batches
            .Select(batch => new HighlightImportBatchSummary(
                batch.Id,
                batch.Source,
                batch.FileName,
                batch.CreatedAtUtc,
                batch.NoteCount,
                batch.BookCount,
                ReadReport(batch.ReportJson)))
            .ToList();
    }

    /// <summary>
    /// Removes the notes an import created, and the books it added when nothing
    /// else has come to them since: no other note, no file. The remembered book
    /// links of a removed book go with it; the others stay.
    /// </summary>
    public async Task<int?> UndoAsync(Guid batchId, CancellationToken ct = default)
    {
        var entries = await _db.NoteImportBatchNotes
            .AsNoTracking()
            .Where(entry => entry.BatchId == batchId)
            .ToListAsync(ct);
        var batch = await _db.NoteImportBatches.FirstOrDefaultAsync(candidate => candidate.Id == batchId, ct);
        if (batch is null)
            return null;

        foreach (var entry in entries)
        {
            await _notes.DeleteAsync(entry.NoteId, ct);

            // Without this the receipt would replay on the next import and the
            // note could never come back.
            await _db.NoteCommandReceipts
                .Where(receipt => receipt.ClientId == entry.ClientId && receipt.IdempotencyKey == entry.IdempotencyKey)
                .ExecuteDeleteAsync(ct);
        }

        _db.NoteImportBatches.Remove(batch);
        await _db.SaveChangesAsync(ct);

        await RemoveCreatedBooksAsync(entries, ct);
        return entries.Count;
    }

    private async Task RemoveCreatedBooksAsync(IReadOnlyList<NoteImportBatchNote> entries, CancellationToken ct)
    {
        // The notes are gone by now; each entry remembers the book its import
        // added. One shared rule decides whether anything has come to it since.
        await ImportBatchUndo.RemoveCreatedBooksAsync(
            _db,
            _library,
            storage: null,
            entries
                .Where(entry => entry.CreatedBookId is not null)
                .Select(entry => new ImportBatchUndo.CreatedBook(entry.CreatedBookId!.Value))
                .ToList(),
            ct,
            beforeDelete: async (db, bookId, token) =>
            {
                // The remembered book links of a removed book go with it; the
                // others stay, and the foreign key needs them gone first.
                db.NoteImportBookLinks.RemoveRange(
                    await db.NoteImportBookLinks.Where(link => link.BookId == bookId).ToListAsync(token));
                await db.SaveChangesAsync(token);
            });
    }

    /// <summary>
    /// Which of a book's annotations are new for the given library book. A
    /// Kobo bookmark id is globally unique, so it is looked up across the whole
    /// library (a note moved to another book is still the same highlight). A
    /// KOReader position, a Kindle location and a Readwise row are only
    /// meaningful inside one book and are compared together with their text,
    /// exactly as the original sidecar import does.
    /// </summary>
    private async Task<List<PlannedNote>> PlanAsync(
        string source,
        ImportSourceBook sourceBook,
        Guid bookId,
        CancellationToken ct)
    {
        var isKobo = source == HighlightImportSourceReader.Kobo;
        var clientId = source switch
        {
            HighlightImportSourceReader.Kobo => KoboClientId,
            HighlightImportSourceReader.Kindle => KindleClientId,
            HighlightImportSourceReader.Readwise => ReadwiseClientId,
            HighlightImportSourceReader.AppleBooks => AppleBooksClientId,
            _ => KoreaderNoteImportService.ClientId,
        };

        var planned = sourceBook.Annotations
            .Select(annotation =>
            {
                var key = isKobo
                    ? Hash($"kobo_bookmark\n{annotation.AnchorValue}")
                    : KoreaderNoteImportService.Fingerprint(
                        bookId, annotation.AnchorKind, annotation.AnchorValue, annotation.Text, annotation.Note);
                return new PlannedNote(annotation, annotation.AnchorValue ?? $"annotation:{key}", clientId, key, true);
            })
            .ToList();

        var koboExisting = isKobo
            ? (await _db.Notes
                    .AsNoTracking()
                    .Where(note => note.SourceAnchorKind == "kobo_bookmark")
                    .Select(note => note.SourceAnchorValue)
                    .ToListAsync(ct))
                .Select(value => value ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal)
            : [];
        var existing = isKobo
            ? []
            : await KoreaderNoteImportService.ImportedNotesAsync(_db, bookId, ct);

        // A receipt without a note is a note the owner deleted: it stays
        // deleted, including one deleted in the shape an earlier import gave it.
        var earlierKeys = planned.ToDictionary(
            entry => entry.Key,
            entry => (entry.Annotation.EarlierShapes ?? [])
                .Select(shape => KoreaderNoteImportService.Fingerprint(
                    bookId, shape.AnchorKind, shape.AnchorValue, shape.Text, shape.Content))
                .ToList(),
            StringComparer.Ordinal);
        var keys = planned
            .Select(entry => entry.Key)
            .Concat(earlierKeys.Values.SelectMany(shapeKeys => shapeKeys))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var receipted = (await _db.NoteCommandReceipts
                .AsNoTracking()
                .Where(receipt => receipt.ClientId == clientId && keys.Contains(receipt.IdempotencyKey))
                .Select(receipt => receipt.IdempotencyKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        for (var i = 0; i < planned.Count; i++)
        {
            var entry = planned[i];
            if (isKobo)
            {
                // `Add` also catches the same annotation twice in one file.
                if (!koboExisting.Add(entry.AnchorValue) || receipted.Contains(entry.Key))
                    planned[i] = entry with { IsNew = false };
                continue;
            }

            var identity = KoreaderNoteImportService.Identity(
                entry.Annotation.AnchorKind, entry.AnchorValue, entry.Annotation.Text, entry.Annotation.Note);
            if (existing.ContainsKey(identity))
            {
                planned[i] = entry with { IsNew = false };
                continue;
            }

            var earlier = (entry.Annotation.EarlierShapes ?? [])
                .Select(shape => KoreaderNoteImportService.Identity(
                    shape.AnchorKind, shape.AnchorValue, shape.Text, shape.Content))
                .FirstOrDefault(existing.ContainsKey);
            if (earlier is not null)
            {
                planned[i] = entry with { IsNew = false, Repairs = existing[earlier] };
                existing.Remove(earlier);
            }
            else if (receipted.Contains(entry.Key) || earlierKeys[entry.Key].Any(receipted.Contains))
            {
                planned[i] = entry with { IsNew = false };
            }

            // Claims the identity so the same annotation twice in one file is
            // imported once.
            existing[identity] = Guid.Empty;
        }

        // The same sentence arriving from another e-reader or app is not new
        // either (#1369). Only notes already in the book count, so two equal
        // passages inside one file stay as the file has them.
        if (planned.Any(entry => entry.IsNew && !string.IsNullOrWhiteSpace(entry.Annotation.Text)))
        {
            var quoted = (await _db.Notes
                    .AsNoTracking()
                    .Where(note => note.BookId == bookId && note.SelectedText != null && note.SelectedText != "")
                    .Select(note => note.SelectedText!)
                    .ToListAsync(ct))
                .Select(NormalizeQuote)
                .ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < planned.Count; i++)
            {
                var entry = planned[i];
                if (entry.IsNew
                    && !string.IsNullOrWhiteSpace(entry.Annotation.Text)
                    && quoted.Contains(NormalizeQuote(entry.Annotation.Text)))
                {
                    planned[i] = entry with { IsNew = false };
                }
            }
        }

        return planned;
    }

    /// <summary>
    /// A quoted passage reduced to what the author wrote: compatibility forms
    /// (the fi and fl ligatures) unfolded, curly quotes and dashes made plain,
    /// whitespace collapsed and case ignored, so one highlight from Kindle,
    /// Readwise and KOReader compares equal.
    /// </summary>
    internal static string NormalizeQuote(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var raw in text.Normalize(NormalizationForm.FormKC))
        {
            var c = raw switch
            {
                '\u2018' or '\u2019' or '\u201A' or '\u201B' or '\u2032' => '\'',
                '\u201C' or '\u201D' or '\u201E' or '\u201F' or '\u2033' => '"',
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
                _ => raw,
            };
            if (c == '\u00AD' || c == '\u200B' || c == '\uFEFF')
                continue;
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private async Task RememberAsync(string source, string sourceKey, Guid bookId, CancellationToken ct)
    {
        if (sourceKey.Length > 1024)
            return;

        var link = await _db.NoteImportBookLinks
            .FirstOrDefaultAsync(candidate => candidate.Source == source && candidate.SourceKey == sourceKey, ct);
        if (link is null)
            _db.NoteImportBookLinks.Add(new NoteImportBookLink { Source = source, SourceKey = sourceKey, BookId = bookId });
        else
            link.BookId = bookId;

        await _db.SaveChangesAsync(ct);
    }

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

    private async Task<Dictionary<string, Guid>> LoadLinksAsync(
        string source,
        IReadOnlyList<ImportSourceBook> sourceBooks,
        CancellationToken ct)
    {
        var keys = sourceBooks.Select(book => book.SourceKey).ToList();
        return await _db.NoteImportBookLinks
            .AsNoTracking()
            .Where(link => link.Source == source && keys.Contains(link.SourceKey))
            .ToDictionaryAsync(link => link.SourceKey, link => link.BookId, StringComparer.Ordinal, ct);
    }

    /// <summary>
    /// The structural description for a file the reader just parsed, once per
    /// import (issue #1523). A source fetched from an API has no file shape to
    /// log.
    /// </summary>
    private void LogShape(ImportSourceFile file, string path)
    {
        if (file.MissingOptionalColumns.Count > 0)
        {
            HighlightImportShapeLog.Write(
                _logger, path, HighlightImportShapeLog.MissingOptionalColumns, file.MissingOptionalColumns);
        }
        else if (file.Skipped.Any(entry => entry.Reason == KindleClippingsParser.UnrecognisedReason))
        {
            HighlightImportShapeLog.Write(_logger, path, HighlightImportShapeLog.UnrecognisedEntries);
        }
    }

    /// <summary>
    /// Reads the file, recording a structural description once when it is not
    /// a source the import knows (issue #1523). The endpoint still turns the
    /// same exception into the same 400; only the log gains the shape.
    /// </summary>
    private ImportSourceFile ReadSource(string path, IReadOnlyList<string> companions)
    {
        try
        {
            return HighlightImportSourceReader.Read(path, companions);
        }
        catch (FormatException)
        {
            HighlightImportShapeLog.Write(_logger, path, HighlightImportShapeLog.ParseFailed);
            throw;
        }
    }

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web);

    private static string SerializeReport(HighlightImportReport report) =>
        JsonSerializer.Serialize(report, ReportJson);

    // NULL or unreadable means "no report", never a report of zeroes: an image
    // written before this field existed must keep showing as it always did.
    private static HighlightImportReport? ReadReport(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<HighlightImportReport>(json, ReportJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HighlightImportReport AddReports(HighlightImportReport? previous, HighlightImportReport next) =>
        previous is null
            ? next
            : new HighlightImportReport(
                previous.Found + next.Found,
                previous.Imported + next.Imported,
                previous.Duplicates + next.Duplicates,
                previous.Skipped + next.Skipped,
                previous.LeftOut + next.LeftOut,
                MergeSkipped(previous.SkippedReasons, next.SkippedReasons),
                previous.BooksMatched + next.BooksMatched,
                previous.BooksCreated + next.BooksCreated,
                previous.BooksLeftOut + next.BooksLeftOut);

    /// <summary>
    /// One reason line per title, author and reason, with the counts added, in
    /// the order the files first reported them.
    /// </summary>
    private static List<HighlightImportSkipped> MergeSkipped(
        IEnumerable<HighlightImportSkipped> previous,
        IEnumerable<HighlightImportSkipped> next)
    {
        var merged = new List<HighlightImportSkipped>();
        var positions = new Dictionary<(string? Title, string? Author, string Reason), int>();
        foreach (var entry in previous.Concat(next))
        {
            var key = (entry.Title, entry.Author, entry.Reason);
            if (positions.TryGetValue(key, out var at))
                merged[at] = merged[at] with { Count = merged[at].Count + entry.Count };
            else
            {
                positions[key] = merged.Count;
                merged.Add(entry);
            }
        }

        return merged;
    }

    private static HighlightImportResultBook Skipped(ImportSourceBook sourceBook, string? message) =>
        new(
            sourceBook.SourceKey,
            "skipped",
            null,
            null,
            sourceBook.Title,
            sourceBook.Author,
            sourceBook.Annotations.Count,
            0,
            0,
            false,
            message);

    private static string Hash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    private sealed record PlannedNote(
        ImportAnnotation Annotation,
        string AnchorValue,
        string ClientId,
        string Key,
        bool IsNew,
        // The note an earlier import stored for this annotation in another shape.
        Guid? Repairs = null);
}
