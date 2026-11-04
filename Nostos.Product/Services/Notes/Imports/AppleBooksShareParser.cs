using System.Text.RegularExpressions;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Reads highlights shared from Apple Books on an iPhone or iPad (issue
/// #1357). Apple offers no file and no API there; what a reader has is the
/// text the share sheet produces when highlights are copied or mailed. Each
/// passage is in quotes, and the share ends with a footer naming the book:
/// <code>
/// “The highlighted passage.”
///
/// Excerpt From
/// Book Title
/// Author Name
/// This material may be protected by copyright.
/// </code>
/// Older versions put the footer on one line
/// (<c>Excerpt From: Author. “Title.” iBooks.</c>), and a Store book may add
/// a link. Apple documents none of this, so the parser holds on to as little
/// as it can: quoted paragraphs are passages, an unquoted paragraph after a
/// passage is the reader's note on it, and the footer only names the book.
/// When the footer is missing or reads otherwise, the passages still come in
/// as one untitled book and the preview's book picker places them.
/// </summary>
internal static class AppleBooksShareParser
{
    public const string PassageAnchor = "applebooks_passage";
    internal const string UntitledKey = "applebooks:untitled";

    // "Excerpt From", as the share sheet words it in the languages Books
    // ships most. Only the English one is confirmed from a real share; the
    // others are best effort, and a miss still reads the passages.
    private static readonly string[] ExcerptFromWords =
    [
        "excerpt from", "extract from", "utdrag ur", "uddrag fra", "utdrag fra", "auszug aus", "extrait de",
        "fragmento de", "extracto de", "estratto da", "trecho de", "excerto de", "fragment uit", "απόσπασμα από",
    ];

    private static readonly Regex OneLineFooter = new(
        @"^(?<author>[^“""]*?)\.?\s*[“""](?<title>.+?)\.?[”""]",
        RegexOptions.CultureInvariant);

    private static readonly Regex Link = new(@"^\s*(?:https?://|www\.)\S+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Copyright = new(@"copyright|upphovsrätt|urheberrecht|droit d'auteur|derechos de autor|diritto d'autore|auteursrecht|πνευματικ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly (char Open, char Close)[] QuotePairs =
    [
        ('“', '”'), ('"', '"'), ('„', '“'), ('„', '”'), ('«', '»'), ('»', '«'), ('”', '”'), ('「', '」'),
    ];

    /// <summary>
    /// True when the text is a Books share: it carries the "Excerpt From"
    /// footer, or (with the footer cut off) it opens on a quoted passage.
    /// </summary>
    public static bool Looks(string text)
    {
        var lines = Lines(text);
        if (lines.Any(line => FooterStart(line) is not null))
            return true;
        var first = lines.FirstOrDefault(line => line.Trim().Length > 0)?.Trim();
        return first is not null && QuotePairs.Any(pair => first[0] == pair.Open);
    }

    public static (IReadOnlyList<ImportSourceBook> Books, IReadOnlyList<HighlightImportSkipped> Skipped) Parse(string text)
    {
        var books = new Dictionary<string, ShareBook>(StringComparer.Ordinal);
        var order = new List<string>();
        var pending = new List<Passage>();

        void Flush(string? title, string? author)
        {
            if (pending.Count == 0)
                return;
            var key = title is null && author is null
                ? UntitledKey
                : $"applebooks:{BookIdentityNormalizer.NormalizeTitle(title)}|{BookIdentityNormalizer.NormalizeAuthor(author)}";
            if (!books.TryGetValue(key, out var book))
            {
                book = new ShareBook(title, author);
                books[key] = book;
                order.Add(key);
            }

            foreach (var passage in pending)
                book.Add(passage);
            pending.Clear();
        }

        var lines = Lines(text);
        var paragraph = new List<string>();

        void EndParagraph()
        {
            if (paragraph.Count == 0)
                return;
            AddParagraph(pending, string.Join('\n', paragraph).Trim());
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (FooterStart(line) is { } rest)
            {
                EndParagraph();
                var (title, author, next) = ReadFooter(lines, i, rest);
                Flush(title, author);
                i = next - 1;
                continue;
            }

            if (line.Trim().Length == 0)
            {
                EndParagraph();
                continue;
            }

            // A passage starts its own paragraph even when the share puts no
            // blank line before it, unless it is inside a quote still open.
            if (paragraph.Count > 0 && Unquote(line.Trim()) is not null && !InsideQuote(paragraph))
                EndParagraph();
            paragraph.Add(line);
        }

        EndParagraph();
        Flush(null, null);

        var result = order
            .Select(key => new ImportSourceBook(key, books[key].Title, books[key].Author, null, books[key].Annotations))
            .Where(book => book.Annotations.Count > 0)
            .ToList();
        return (result, []);
    }

    /// <summary>
    /// A quoted paragraph is a passage. An unquoted one is the note on the
    /// passage before it; with no passage before it, it is taken as a passage
    /// whose quotes were lost on the way.
    /// </summary>
    private static void AddParagraph(List<Passage> pending, string paragraph)
    {
        if (paragraph.Length == 0 || Link.IsMatch(paragraph) || IsCopyright(paragraph))
            return;

        if (Unquote(paragraph) is { } quoted)
        {
            var clean = KoreaderNoteImportService.Clean(quoted);
            if (clean is not null)
                pending.Add(new Passage(clean, string.Empty));
            return;
        }

        var text = KoreaderNoteImportService.Clean(paragraph);
        if (text is null)
            return;

        if (pending.Count > 0)
        {
            var last = pending[^1];
            pending[^1] = last with { Note = last.Note.Length == 0 ? text : $"{last.Note}\n\n{text}" };
            return;
        }

        pending.Add(new Passage(text, string.Empty));
    }

    /// <summary>The text after the "Excerpt From" words, or null when the line is not a footer.</summary>
    private static string? FooterStart(string line)
    {
        var value = line.Trim();
        foreach (var words in ExcerptFromWords)
        {
            if (!value.StartsWith(words, StringComparison.OrdinalIgnoreCase))
                continue;
            var rest = value[words.Length..];
            if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]) && rest[0] != ':')
                continue;
            return rest.TrimStart(':').Trim();
        }

        return null;
    }

    /// <summary>
    /// The book a footer names, and the index of the line after it. The
    /// footer is either one line (<c>Excerpt From: Author. “Title.”</c>) or
    /// the words alone, then the title and the author on their own lines.
    /// </summary>
    private static (string? Title, string? Author, int Next) ReadFooter(List<string> lines, int start, string rest)
    {
        var next = start + 1;
        string? title = null;
        string? author = null;

        if (rest.Length > 0)
        {
            var match = OneLineFooter.Match(rest);
            if (match.Success)
            {
                title = Clean(match.Groups["title"].Value);
                author = Clean(match.Groups["author"].Value);
            }
            else
            {
                title = Clean(rest);
            }
        }
        else
        {
            var named = new List<string>();
            while (next < lines.Count && named.Count < 2)
            {
                var value = lines[next].Trim();
                if (value.Length == 0)
                {
                    if (named.Count > 0)
                        break;
                    next++;
                    continue;
                }

                if (IsCopyright(value) || Link.IsMatch(value) || FooterStart(value) is not null || Unquote(value) is not null)
                    break;
                named.Add(value);
                next++;
            }

            title = named.Count > 0 ? Clean(named[0]) : null;
            author = named.Count > 1 ? Clean(named[1]) : null;
        }

        // The copyright line, a Store link and blank lines close the footer.
        while (next < lines.Count && (lines[next].Trim().Length == 0 || IsCopyright(lines[next]) || Link.IsMatch(lines[next])))
            next++;
        return (title, author, next);
    }

    private static string? Clean(string value)
    {
        var trimmed = value.Trim().TrimEnd('.').Trim();
        if (Unquote(trimmed) is { } inner)
            trimmed = inner.Trim().TrimEnd('.').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool IsCopyright(string line) =>
        line.Trim().Length < 160 && Copyright.IsMatch(line);

    private static bool InsideQuote(List<string> paragraph)
    {
        var value = string.Join('\n', paragraph).Trim();
        return value.Length > 0 && QuotePairs.Any(pair => value[0] == pair.Open) && Unquote(value) is null;
    }

    /// <summary>The text inside a paragraph's outer quotes, or null when it is not quoted.</summary>
    internal static string? Unquote(string paragraph)
    {
        var value = paragraph.Trim();
        if (value.Length < 2)
            return null;
        foreach (var (open, close) in QuotePairs)
        {
            if (value[0] == open && value[^1] == close)
                return value[1..^1].Trim();
        }

        return null;
    }

    private static List<string> Lines(string text) =>
        text.Replace("﻿", string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ').Split('\n').ToList();

    private sealed record Passage(string Text, string Note);

    private sealed class ShareBook(string? title, string? author)
    {
        private readonly HashSet<(string, string)> _seen = [];

        public string? Title { get; } = title;
        public string? Author { get; } = author;
        public List<ImportAnnotation> Annotations { get; } = [];

        // The same passage shared twice in one paste is one highlight.
        public void Add(Passage passage)
        {
            if (_seen.Add((passage.Text, passage.Note)))
                Annotations.Add(new ImportAnnotation(PassageAnchor, null, passage.Text, passage.Note));
        }
    }
}
