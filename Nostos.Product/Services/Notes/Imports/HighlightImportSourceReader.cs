using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Turns an uploaded e-reader file into the one shape the import works on.
/// The source is decided by content, not by file name: a SQLite header is a
/// Kobo database, anything else is read as a KOReader sidecar.
/// </summary>
internal static class HighlightImportSourceReader
{
    public const string Kobo = "kobo";
    public const string Koreader = "koreader";
    private const long MaxSidecarBytes = 2 * 1024 * 1024;

    public static (string Source, IReadOnlyList<ImportSourceBook> Books) Read(string path)
    {
        if (KoboDatabaseReader.HasSqliteHeader(path))
            return (Kobo, ReadKobo(path));

        if (new FileInfo(path).Length > MaxSidecarBytes)
            throw new FormatException("This file is not a Kobo database or a KOReader metadata file.");

        try
        {
            return (Koreader, [ReadKoreader(File.ReadAllText(path))]);
        }
        catch (FormatException)
        {
            // The sidecar parser's own message assumes the file is a sidecar.
            throw new FormatException("This file is not a Kobo database or a KOReader metadata file.");
        }
    }

    private static List<ImportSourceBook> ReadKobo(string path) =>
        KoboDatabaseReader.Read(path)
            .Select(volume => new ImportSourceBook(
                volume.VolumeId,
                volume.Title,
                volume.Author,
                volume.Isbn,
                volume.Bookmarks
                    .Where(bookmark => bookmark.Text is not null || bookmark.Annotation is not null)
                    .Select(bookmark => new ImportAnnotation(
                        "kobo_bookmark",
                        bookmark.BookmarkId,
                        bookmark.Text,
                        bookmark.Annotation ?? string.Empty))
                    .ToList()))
            .Where(book => book.Annotations.Count > 0)
            .ToList();

    private static ImportSourceBook ReadKoreader(string source)
    {
        var document = KoreaderMetadataParser.Parse(source);

        // The file checksum is KOReader's own identity for the book; older
        // sidecars lack it, and title + author is the next most stable thing.
        var key = document.PartialMd5 is not null
            ? $"md5:{document.PartialMd5}"
            : $"meta:{BookIdentityNormalizer.NormalizeTitle(document.Title)}|{BookIdentityNormalizer.NormalizeAuthor(document.Author)}|{BookIdentityNormalizer.NormalizeIsbn(document.Isbn)}";

        var annotations = new List<ImportAnnotation>();
        foreach (var annotation in document.Annotations)
        {
            var text = KoreaderNoteImportService.Clean(annotation.Text);
            var note = KoreaderNoteImportService.Clean(annotation.Note) ?? string.Empty;
            if (text is null && note.Length == 0)
                continue;

            var (kind, value) = KoreaderNoteImportService.Anchor(annotation);
            annotations.Add(new ImportAnnotation(kind, value, text, note));
        }

        return new ImportSourceBook(key, document.Title, document.Author, document.Isbn, annotations);
    }
}
