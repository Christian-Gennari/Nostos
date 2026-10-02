using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Notes.Imports;

public sealed class KoboNoteImportService
{
    private const string ClientId = "kobo-import";
    private const string AnchorKind = "kobo_bookmark";
    private readonly NostosDbContext _db;
    private readonly INoteService _notes;

    public KoboNoteImportService(NostosDbContext db, INoteService notes)
    {
        _db = db;
        _notes = notes;
    }

    public async Task<KoboImportReport> ImportAsync(
        string databasePath,
        CancellationToken ct = default)
    {
        var volumes = KoboDatabaseReader.Read(databasePath);

        var library = await _db.Books
            .AsNoTracking()
            .Select(book => new LibraryBook(
                book.Id,
                book.Title,
                book.NormalizedIsbn,
                book.Work != null ? book.Work.NormalizedTitle : null,
                book.Work != null ? book.Work.NormalizedAuthor : null))
            .ToListAsync(ct);

        var books = new List<KoboImportBookReport>(volumes.Count);
        foreach (var volume in volumes)
        {
            // Dog-ears and other text-less bookmarks are not annotations; a
            // volume holding only those has nothing to report.
            var annotations = volume.Bookmarks
                .Where(bookmark => bookmark.Text is not null || bookmark.Annotation is not null)
                .ToList();
            if (annotations.Count == 0)
                continue;

            books.Add(await ImportVolumeAsync(volume, annotations, library, ct));
        }

        return new KoboImportReport(
            books.Count,
            books.Count(book => book.Status == "matched"),
            books.Sum(book => book.AnnotationCount),
            books.Sum(book => book.ImportedCount),
            books.Sum(book => book.DuplicateCount),
            books.Sum(book => book.SkippedCount),
            books);
    }

    private async Task<KoboImportBookReport> ImportVolumeAsync(
        KoboVolume volume,
        IReadOnlyList<KoboBookmark> annotations,
        IReadOnlyList<LibraryBook> library,
        CancellationToken ct)
    {
        var candidates = Match(volume, library);
        if (candidates.Count != 1)
        {
            return new KoboImportBookReport(
                candidates.Count == 0 ? "unmatched" : "ambiguous",
                null,
                null,
                volume.Title,
                volume.Author,
                annotations.Count,
                0,
                0,
                annotations.Count,
                candidates.Count == 0
                    ? "Not in your library."
                    : $"Matches {candidates.Count} books in your library.");
        }

        var book = candidates[0];

        // The Kobo bookmark id is the identity of an annotation. Matching on it
        // alone (not on the text) keeps a note that was edited in Nostos, or
        // re-worded on the device, from being imported a second time.
        var existing = (await _db.Notes
                .AsNoTracking()
                .Where(note => note.BookId == book.Id && note.SourceAnchorKind == AnchorKind)
                .Select(note => note.SourceAnchorValue)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        // A receipt without a note means the note was deleted in Nostos after an
        // earlier import; it stays deleted.
        var keys = annotations.ToDictionary(
            annotation => annotation.BookmarkId,
            annotation => Fingerprint(book.Id, annotation.BookmarkId),
            StringComparer.Ordinal);
        var keyList = keys.Values.ToList();
        var receipted = (await _db.NoteCommandReceipts
                .AsNoTracking()
                .Where(receipt => receipt.ClientId == ClientId && keyList.Contains(receipt.IdempotencyKey))
                .Select(receipt => receipt.IdempotencyKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var imported = 0;
        var duplicates = 0;

        foreach (var annotation in annotations)
        {
            var key = keys[annotation.BookmarkId];
            if (!existing.Add(annotation.BookmarkId) || receipted.Contains(key))
            {
                duplicates++;
                continue;
            }

            var result = await _notes.CaptureAsync(new CaptureNoteRequest(
                BookId: book.Id,
                Content: annotation.Annotation ?? string.Empty,
                SelectedText: annotation.Text,
                CaptureSource: "import",
                ProcessingMode: "verbatim",
                SourceAnchorKind: AnchorKind,
                SourceAnchorValue: annotation.BookmarkId,
                AnchorVerified: false,
                ClientId: ClientId,
                IdempotencyKey: key),
                ct);

            if (!result.Success)
                throw new InvalidOperationException(result.ErrorMessage ?? "Kobo note import failed.");

            imported++;
        }

        return new KoboImportBookReport(
            "matched",
            book.Id,
            book.Title,
            volume.Title,
            volume.Author,
            annotations.Count,
            imported,
            duplicates,
            0,
            null);
    }

    /// <summary>
    /// ISBN, else title and author together. A title on its own is a
    /// resemblance, not an identity, so it never attaches notes: two different
    /// books share titles far too often.
    /// </summary>
    private static List<LibraryBook> Match(KoboVolume volume, IReadOnlyList<LibraryBook> library)
    {
        var isbn = BookIdentityNormalizer.NormalizeIsbn(volume.Isbn);
        if (isbn is not null)
        {
            var byIsbn = library.Where(book => book.NormalizedIsbn == isbn).ToList();
            if (byIsbn.Count > 0)
                return byIsbn;
        }

        var title = BookIdentityNormalizer.NormalizeTitle(volume.Title);
        var author = BookIdentityNormalizer.NormalizeAuthor(volume.Author);
        if (title.Length == 0 || author.Length == 0)
            return [];

        return library
            .Where(book => book.NormalizedTitle == title && book.NormalizedAuthor == author)
            .ToList();
    }

    private static string Fingerprint(Guid bookId, string bookmarkId) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{bookId:D}\n{AnchorKind}\n{bookmarkId}")))
            .ToLowerInvariant();

    private sealed record LibraryBook(
        Guid Id,
        string Title,
        string? NormalizedIsbn,
        string? NormalizedTitle,
        string? NormalizedAuthor);
}
