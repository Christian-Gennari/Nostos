using System.Text;
using System.Text.RegularExpressions;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Turns an uploaded file into the one shape the import works on. The source
/// is decided by content, not by file name: a SQLite header is a Kobo
/// database, a Readwise header row is a Readwise CSV export, a Lua table is a
/// KOReader sidecar, and lines of ten <c>=</c> are a Kindle's My Clippings.txt.
/// </summary>
internal static class HighlightImportSourceReader
{
    public const string Kobo = "kobo";
    public const string Koreader = "koreader";
    public const string Kindle = "kindle";
    public const string Readwise = "readwise";
    private const long MaxSidecarBytes = 2 * 1024 * 1024;
    // Years of Kindle clippings or a full Readwise export are a few MB.
    private const long MaxTextBytes = 64 * 1024 * 1024;

    internal const string Unrecognised =
        "This file is not one Nostos can import highlights from. Choose KoboReader.sqlite, a KOReader metadata.lua, a Kindle's My Clippings.txt or a Readwise CSV export.";

    private static readonly Regex LuaTable = new(@"\A\s*(?:--[^\n]*\n\s*)*return\s*\{", RegexOptions.CultureInvariant);

    public static ImportSourceFile Read(string path)
    {
        if (KoboDatabaseReader.HasSqliteHeader(path))
            return new ImportSourceFile(Kobo, ReadKobo(path), []);

        var length = new FileInfo(path).Length;
        if (length > MaxTextBytes)
            throw new FormatException(Unrecognised);

        var text = DecodeText(File.ReadAllBytes(path));

        if (ReadwiseCsvParser.Looks(text))
        {
            var (books, skipped) = ReadwiseCsvParser.Parse(text);
            return new ImportSourceFile(Readwise, books, skipped);
        }

        if (!LuaTable.IsMatch(text) && KindleClippingsParser.Looks(text))
        {
            var (books, skipped) = KindleClippingsParser.Parse(text);
            return new ImportSourceFile(Kindle, books, skipped);
        }

        if (length > MaxSidecarBytes)
            throw new FormatException(Unrecognised);

        try
        {
            return new ImportSourceFile(Koreader, [ReadKoreader(text)], []);
        }
        catch (FormatException)
        {
            // The sidecar parser's own message assumes the file is a sidecar.
            throw new FormatException(Unrecognised);
        }
    }

    /// <summary>
    /// A byte-order mark names its own encoding (UTF-8, UTF-16, UTF-32). Without
    /// one the bytes are UTF-8 when they decode strictly, and Windows-1252
    /// otherwise: that is what older Kindles and Windows exports write, and
    /// decoding it as UTF-8 would turn every accent and dash into U+FFFD.
    /// </summary>
    internal static string DecodeText(byte[] bytes)
    {
        using (var reader = new StreamReader(
            new MemoryStream(bytes),
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: true))
        {
            var hasBom = bytes.Length >= 2
                && ((bytes[0] == 0xFF && bytes[1] == 0xFE)
                    || (bytes[0] == 0xFE && bytes[1] == 0xFF)
                    || (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
            if (hasBom)
                return reader.ReadToEnd();
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
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
            annotations.Add(new ImportAnnotation(
                kind,
                value,
                text,
                note,
                KoreaderNoteImportService.EarlierShapes(annotation, text, note)));
        }

        return new ImportSourceBook(key, document.Title, document.Author, document.Isbn, annotations);
    }
}
