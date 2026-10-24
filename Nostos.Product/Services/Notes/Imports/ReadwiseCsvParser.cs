using System.Text;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Reads a Readwise CSV export (issue #1219): one row per highlight with
/// <c>Highlight, Book Title, Book Author, Amazon Book ID, Note, Color, Tags,
/// Location Type, Location, Highlighted at, Document tags</c>. Columns are
/// found by name, so an export with more, fewer or reordered columns still
/// reads. Readwise also collects web articles, tweets and podcasts; those are
/// not books in a library and are left out with a reason, or flagged where
/// the export leaves it uncertain.
/// </summary>
internal static class ReadwiseCsvParser
{
    public const string AnchorPrefix = "readwise_";
    public const string UnknownAnchor = "readwise_unknown";

    internal const string ArticleReason = "A web article. Only highlights from books are imported.";
    internal const string ArticleNotice = "Readwise keeps this like a web article.";
    internal const string TweetReason = "A tweet. Only highlights from books are imported.";
    internal const string PodcastReason = "A podcast. Only highlights from books are imported.";
    internal const string OtherReason = "Not a book. Only highlights from books are imported.";
    internal const string EmptyReason = "The row has no highlight or note.";
    internal const string UntitledReason = "The row has no book title.";

    private static readonly HashSet<string> AnchorTypes = new(StringComparer.Ordinal) { "location", "page", "order" };

    /// <summary>True when the first line is a Readwise header.</summary>
    public static bool Looks(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var header = (end < 0 ? text : text[..end]).TrimStart('﻿');
        var columns = Records(header).FirstOrDefault() ?? [];
        return columns.Any(column => Is(column, "Highlight")) && columns.Any(column => Is(column, "Book Title"));
    }

    public static (IReadOnlyList<ImportSourceBook> Books, IReadOnlyList<HighlightImportSkipped> Skipped) Parse(string text)
    {
        var records = Records(text.TrimStart('﻿')).ToList();
        if (records.Count == 0)
            throw new FormatException("This Readwise export is empty.");

        var header = records[0];
        int Column(params string[] names) => header.FindIndex(column => names.Any(name => Is(column, name)));
        var highlight = Column("Highlight");
        var title = Column("Book Title", "Title");
        if (highlight < 0 || title < 0)
            throw new FormatException("This file is not a Readwise CSV export.");
        var author = Column("Book Author", "Author");
        var asin = Column("Amazon Book ID");
        var note = Column("Note");
        var locationType = Column("Location Type");
        var location = Column("Location");
        var category = Column("Category", "Source Type");

        var books = new Dictionary<string, ReadwiseBook>(StringComparer.Ordinal);
        var order = new List<string>();
        var skipped = new SkipTally();

        foreach (var record in records.Skip(1))
        {
            if (record.All(string.IsNullOrWhiteSpace))
                continue;

            string? Cell(int index) => index >= 0 && index < record.Count ? KoreaderNoteImportService.Clean(record[index]) : null;
            var bookTitle = Cell(title);
            var bookAuthor = Cell(author);
            var bookAsin = Cell(asin);
            var type = Cell(locationType)?.ToLowerInvariant();

            if (NotABook(Cell(category)?.ToLowerInvariant(), type, bookTitle, bookAuthor, bookAsin) is { } reason)
            {
                skipped.Add(bookTitle, bookAuthor, reason);
                continue;
            }

            if (bookTitle is null)
            {
                skipped.Add(null, bookAuthor, UntitledReason);
                continue;
            }

            var selected = Cell(highlight);
            var content = Cell(note) ?? string.Empty;
            if (selected is null && content.Length == 0)
            {
                skipped.Add(bookTitle, bookAuthor, EmptyReason);
                continue;
            }

            var value = Cell(location);
            var (kind, anchor) = type is not null && AnchorTypes.Contains(type) && value is not null
                ? (AnchorPrefix + type, value)
                : (UnknownAnchor, (string?)null);

            var key = bookAsin is not null
                ? $"readwise:asin:{bookAsin.ToUpperInvariant()}"
                : $"readwise:meta:{BookIdentityNormalizer.NormalizeTitle(bookTitle)}|{BookIdentityNormalizer.NormalizeAuthor(bookAuthor)}";
            if (!books.TryGetValue(key, out var book))
            {
                book = new ReadwiseBook(bookTitle, bookAuthor);
                books[key] = book;
                order.Add(key);
            }

            book.Annotations.Add(new ImportAnnotation(kind, anchor, selected, content));
            // Positioned by order, with no Amazon id: how Readwise keeps a web
            // article, though some book sources look the same. Flagged for the
            // owner rather than left out on a guess.
            book.LooksLikeArticle &= bookAsin is null && type is "order" or "offset";
        }

        var result = order
            .Select(key => books[key])
            .Select((book, index) => new ImportSourceBook(
                order[index],
                book.Title,
                book.Author,
                null,
                book.Annotations,
                book.LooksLikeArticle ? ArticleNotice : null))
            .ToList();
        return (result, skipped.ToList());
    }

    /// <summary>
    /// Why a row is not from a book, or null when it is. An export that names
    /// each row's category says so outright; one that does not gives it away
    /// by its shape: a tweet thread is "Tweets From …" by an @handle, and a
    /// podcast is positioned by time.
    /// </summary>
    private static string? NotABook(string? category, string? locationType, string? title, string? author, string? asin)
    {
        if (category is not null)
        {
            return category switch
            {
                "books" or "book" or "supplementals" or "supplemental" or "pdfs" or "pdf" or "epubs" or "epub" => null,
                "articles" or "article" => ArticleReason,
                "tweets" or "tweet" => TweetReason,
                "podcasts" or "podcast" => PodcastReason,
                _ => OtherReason,
            };
        }

        if (asin is not null)
            return null;
        if ((title?.StartsWith("Tweets From ", StringComparison.OrdinalIgnoreCase) ?? false)
            || (author?.StartsWith('@') ?? false))
            return TweetReason;
        return locationType is "time_offset" or "time" or "timestamp" ? PodcastReason : null;
    }

    private sealed class ReadwiseBook(string title, string? author)
    {
        public string Title { get; } = title;
        public string? Author { get; } = author;
        public List<ImportAnnotation> Annotations { get; } = [];
        public bool LooksLikeArticle { get; set; } = true;
    }

    private static bool Is(string column, string name) =>
        string.Equals(column.Trim().TrimStart('﻿'), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// RFC 4180 records: a quoted field can hold commas, doubled quotes and
    /// line breaks. Line endings are LF or CRLF; a quote that never closes
    /// ends at the end of the file rather than failing the whole export.
    /// </summary>
    internal static IEnumerable<List<string>> Records(string text)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var any = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            any = true;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r' or '\n':
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    record.Add(field.ToString());
                    field.Clear();
                    yield return record;
                    record = [];
                    any = false;
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        if (any)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }
}
