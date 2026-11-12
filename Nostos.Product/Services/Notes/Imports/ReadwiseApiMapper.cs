using System.Globalization;
using System.Text.Json;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Maps one page of Readwise's export API (<c>GET /api/v2/export/</c>) into
/// the same <see cref="ImportSourceBook"/> shape the CSV reader produces, so
/// the preview, matching, dedupe, skip rules and undo batch are the existing
/// ones (issue #1525). Book identity is the CSV reader's key — the Amazon id
/// when there is one, otherwise title and author — so a library that imported
/// a CSV earlier remembers the same books when it switches to the API.
/// </summary>
internal static class ReadwiseApiMapper
{
    public static ImportSourceFile Map(JsonElement root)
    {
        var issues = ReadwiseExportSchema.Check(root);
        if (issues.Count > 0)
            throw new ReadwiseFormatException(issues);

        var books = new Dictionary<string, ReadwiseApiBook>(StringComparer.Ordinal);
        var order = new List<string>();
        var skipped = new SkipTally();

        foreach (var result in root.GetProperty("results").EnumerateArray())
        {
            // Deleted books are only returned when includeDeleted is asked for;
            // we never ask, and an unchanged export may still carry the flag.
            if (Flag(result, "is_deleted"))
                continue;

            var title = Clean(result, "title");
            var author = Clean(result, "author");
            var asin = Clean(result, "asin");
            var category = Clean(result, "category")?.ToLowerInvariant();
            var highlights = result.GetProperty("highlights")
                .EnumerateArray()
                .Where(highlight => !Flag(highlight, "is_deleted") && !Flag(highlight, "is_discard"))
                .ToList();

            var firstType = highlights.Count > 0
                ? Clean(highlights[0], "location_type")?.ToLowerInvariant()
                : null;
            var notABook = ReadwiseBookRules.NotABook(category, firstType, title, author, asin);
            if (notABook is not null)
            {
                foreach (var _ in highlights)
                    skipped.Add(title, author, notABook);
                continue;
            }

            if (title is null)
            {
                foreach (var _ in highlights)
                    skipped.Add(null, author, ReadwiseBookRules.UntitledReason);
                continue;
            }

            var key = BookKey(title, author, asin);
            if (!books.TryGetValue(key, out var book))
            {
                book = new ReadwiseApiBook(title, author, category is null);
                books[key] = book;
                order.Add(key);
            }

            foreach (var highlight in highlights)
            {
                var text = Clean(highlight, "text");
                var note = Clean(highlight, "note") ?? string.Empty;
                if (text is null && note.Length == 0)
                {
                    skipped.Add(title, author, ReadwiseBookRules.EmptyReason);
                    continue;
                }

                var type = Clean(highlight, "location_type");
                var location = Number(highlight, "location");
                var (kind, anchor) = ReadwiseBookRules.Anchor(type, location);
                book.Annotations.Add(new ImportAnnotation(
                    kind,
                    anchor,
                    text,
                    note,
                    CreatedAt: ImportDates.Iso(Clean(highlight, "highlighted_at"))));

                // Positioned by order, with no Amazon id, and no category to
                // say otherwise: how Readwise keeps a web article, though some
                // book sources look the same. Flagged rather than guessed.
                book.LooksLikeArticle &= asin is null && type?.ToLowerInvariant() is "order" or "offset";
            }
        }

        var mapped = order
            .Select(key => (Key: key, Book: books[key]))
            .Select(entry => new ImportSourceBook(
                entry.Key,
                entry.Book.Title,
                entry.Book.Author,
                null,
                entry.Book.Annotations,
                entry.Book.LooksLikeArticle ? ReadwiseBookRules.ArticleNotice : null))
            .ToList();
        return new ImportSourceFile(HighlightImportSourceReader.Readwise, mapped, skipped.ToList());
    }

    private static string BookKey(string title, string? author, string? asin) =>
        asin is not null
            ? $"readwise:asin:{asin.ToUpperInvariant()}"
            : $"readwise:meta:{BookIdentityNormalizer.NormalizeTitle(title)}|{BookIdentityNormalizer.NormalizeAuthor(author)}";

    private static string? Clean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? KoreaderNoteImportService.Clean(value.GetString())
            : null;

    private static string? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;

    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private sealed class ReadwiseApiBook(string title, string? author, bool checkArticleShape)
    {
        public string Title { get; } = title;
        public string? Author { get; } = author;
        public List<ImportAnnotation> Annotations { get; } = [];
        public bool LooksLikeArticle { get; set; } = checkArticleShape;
    }
}
