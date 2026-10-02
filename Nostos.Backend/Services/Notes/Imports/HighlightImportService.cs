using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// E-reader highlight import in two steps (issue #656): <see cref="PreviewAsync"/>
/// reads a file and proposes where each book goes without writing anything;
/// <see cref="CommitAsync"/> imports exactly the books the owner decided on.
/// Each commit is a batch that <see cref="UndoAsync"/> can take back.
/// </summary>
public sealed class HighlightImportService
{
    public const string Remembered = "remembered";
    private const string KoboClientId = "kobo-import";
    private readonly NostosDbContext _db;
    private readonly INoteService _notes;
    private readonly ILibraryService _library;

    public HighlightImportService(NostosDbContext db, INoteService notes, ILibraryService library)
    {
        _db = db;
        _notes = notes;
        _library = library;
    }

    public async Task<HighlightImportPreview> PreviewAsync(string path, CancellationToken ct = default)
    {
        var (source, sourceBooks) = HighlightImportSourceReader.Read(path);
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
                    new HighlightImportCandidate(linked.Id, linked.Title, linked.Author, "Confirmed on an earlier import"),
                    .. candidates.Where(candidate => candidate.BookId != linked.Id),
                ];
            }

            var target = bookId ?? candidates.FirstOrDefault()?.BookId;
            var newCount = target is null
                ? sourceBook.Annotations.Count
                : (await PlanAsync(source, sourceBook, target.Value, ct)).Count(entry => entry.IsNew);

            books.Add(new HighlightImportPreviewBook(
                sourceBook.SourceKey,
                sourceBook.Title,
                sourceBook.Author,
                sourceBook.Annotations.Count,
                newCount,
                match,
                bookId,
                candidates));
        }

        return new HighlightImportPreview(source, books);
    }

    public async Task<HighlightImportResult> CommitAsync(
        string path,
        string? fileName,
        IReadOnlyList<HighlightImportDecision> decisions,
        CancellationToken ct = default)
    {
        var (source, sourceBooks) = HighlightImportSourceReader.Read(path);
        var bySourceKey = decisions
            .GroupBy(decision => decision.SourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        var batch = new NoteImportBatch { Source = source, FileName = Truncate(fileName, 260) };
        var results = new List<HighlightImportResultBook>(sourceBooks.Count);

        foreach (var sourceBook in sourceBooks)
        {
            bySourceKey.TryGetValue(sourceBook.SourceKey, out var decision);
            if (decision is null || (decision.BookId is null && !decision.Create))
            {
                results.Add(Skipped(sourceBook, null));
                continue;
            }

            var created = false;
            var bookId = decision.BookId;
            if (bookId is null)
            {
                var title = KoreaderNoteImportService.Clean(sourceBook.Title);
                if (title is null)
                {
                    results.Add(Skipped(sourceBook, "The device has no title for this book."));
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
                continue;
            }

            await RememberAsync(source, sourceBook.SourceKey, book.Id, ct);

            var imported = 0;
            var duplicates = 0;
            foreach (var entry in await PlanAsync(source, sourceBook, book.Id, ct))
            {
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
                    ClientId: entry.ClientId,
                    IdempotencyKey: entry.Key),
                    ct);

                if (!result.Success)
                    throw new InvalidOperationException(result.ErrorMessage ?? "Highlight import failed.");

                batch.Notes.Add(new NoteImportBatchNote
                {
                    NoteId = result.Value!.Id,
                    ClientId = entry.ClientId,
                    IdempotencyKey = entry.Key,
                });
                imported++;
            }

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
                null));
        }

        // A batch exists to be undone; one that added nothing has nothing to undo.
        if (batch.Notes.Count > 0)
            _db.NoteImportBatches.Add(batch);
        await _db.SaveChangesAsync(ct);

        return new HighlightImportResult(batch.Notes.Count > 0 ? batch.Id : null, source, results);
    }

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
                NoteCount = batch.Notes.Count,
                BookCount = batch.Notes.Select(note => note.Note.BookId).Distinct().Count(),
            })
            .ToListAsync(ct);

        return batches
            .Select(batch => new HighlightImportBatchSummary(
                batch.Id, batch.Source, batch.FileName, batch.CreatedAtUtc, batch.NoteCount, batch.BookCount))
            .ToList();
    }

    /// <summary>
    /// Removes the notes an import created. Books it added and the remembered
    /// book links stay: both are the owner's decisions, not the import's notes.
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
        return entries.Count;
    }

    /// <summary>
    /// Which of a book's annotations are new for the given library book. A
    /// Kobo bookmark id is globally unique, so it is looked up across the whole
    /// library (a note moved to another book is still the same highlight). A
    /// KOReader position is only meaningful inside one book and is compared
    /// together with its text, exactly as the original sidecar import does.
    /// </summary>
    private async Task<List<PlannedNote>> PlanAsync(
        string source,
        ImportSourceBook sourceBook,
        Guid bookId,
        CancellationToken ct)
    {
        var isKobo = source == HighlightImportSourceReader.Kobo;
        var clientId = isKobo ? KoboClientId : KoreaderNoteImportService.ClientId;

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

        var existing = isKobo
            ? (await _db.Notes
                    .AsNoTracking()
                    .Where(note => note.SourceAnchorKind == "kobo_bookmark")
                    .Select(note => note.SourceAnchorValue)
                    .ToListAsync(ct))
                .Select(value => value ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal)
            : (await _db.Notes
                    .AsNoTracking()
                    .Where(note => note.BookId == bookId && note.CaptureSource == "import")
                    .Select(note => new { note.SourceAnchorKind, note.SourceAnchorValue, note.SelectedText, note.Content })
                    .ToListAsync(ct))
                .Select(note => Identity(note.SourceAnchorKind, note.SourceAnchorValue, note.SelectedText, note.Content))
                .ToHashSet(StringComparer.Ordinal);

        // A receipt without a note is a note the owner deleted: it stays deleted.
        var keys = planned.Select(entry => entry.Key).ToList();
        var receipted = (await _db.NoteCommandReceipts
                .AsNoTracking()
                .Where(receipt => receipt.ClientId == clientId && keys.Contains(receipt.IdempotencyKey))
                .Select(receipt => receipt.IdempotencyKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        for (var i = 0; i < planned.Count; i++)
        {
            var entry = planned[i];
            var identity = isKobo
                ? entry.AnchorValue
                : Identity(entry.Annotation.AnchorKind, entry.AnchorValue, entry.Annotation.Text, entry.Annotation.Note);

            // `Add` also catches the same annotation twice in one file.
            if (!existing.Add(identity) || receipted.Contains(entry.Key))
                planned[i] = entry with { IsNew = false };
        }

        return planned;
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

    private async Task<List<ImportLibraryBook>> LoadLibraryAsync(CancellationToken ct) =>
        await _db.Books
            .AsNoTracking()
            .Select(book => new ImportLibraryBook(book.Id, book.Title, book.Author, book.NormalizedIsbn))
            .ToListAsync(ct);

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

    private static string Identity(string kind, string? value, string? text, string? content) =>
        string.Join('\n', kind, value ?? string.Empty, text ?? string.Empty, content ?? string.Empty);

    private static string Hash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    private sealed record PlannedNote(
        ImportAnnotation Annotation,
        string AnchorValue,
        string ClientId,
        string Key,
        bool IsNew);
}
