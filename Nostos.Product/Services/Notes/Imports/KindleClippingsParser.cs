using System.Text;
using System.Text.RegularExpressions;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Reads a Kindle's <c>My Clippings.txt</c> (issue #1219). The file is an
/// append-only log, one entry per highlight, note or bookmark, each ended by a
/// line of ten <c>=</c>:
/// <code>
/// Title (Author)
/// - Your Highlight on page 12 | Location 180-181 | Added on Monday, …
///
/// The highlighted text
/// ==========
/// </code>
/// Editing a highlight on the device appends a second entry rather than
/// changing the first, and a note is a separate entry at the highlight's
/// last location; both are folded back into one annotation here. The second
/// line is in the device's UI language, so it is read by keyword, and an
/// entry that matches none is left out with a reason rather than guessed at.
/// </summary>
internal static class KindleClippingsParser
{
    public const string Separator = "==========";

    public const string LocationAnchor = "kindle_location";
    public const string PageAnchor = "kindle_page";
    public const string UnknownAnchor = "kindle_unknown";

    internal const string BookmarkReason = "Bookmarks mark a place and hold no text.";
    internal const string ClippingLimitReason = "The publisher's clipping limit kept the Kindle from saving this text.";
    internal const string EmptyReason = "The entry has no text.";
    internal const string UnrecognisedReason = "Nostos does not recognise the format of this entry.";

    private static readonly Regex SeparatorLine = new(@"^﻿?={10}\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // The kind of entry, as each UI language words it. Bookmarks and notes are
    // tested before highlights: no language's highlight wording contains theirs.
    private static readonly string[] BookmarkWords =
        ["bookmark", "lesezeichen", "signet", "marcador", "segnalibro", "bladwijzer", "ブックマーク", "书签", "書籤", "закладка", "북마크"];
    private static readonly string[] NoteWords =
        ["note", "notiz", "nota", "notitie", "メモ", "笔记", "筆記", "заметка", "메모"];
    private static readonly string[] HighlightWords =
        ["highlight", "markierung", "surlignement", "subrayado", "evidenziazione", "destaque", "markering", "ハイライト", "标注", "標註", "выделен", "하이라이트"];

    private const string Number = @"\d[\d.,]*";
    private const string Range = Number + @"(?:\s*[-–]\s*" + Number + ")?";
    private const string Roman = "[ivxlcdm]+";

    private static readonly Regex[] PagePatterns =
    [
        new($@"(?:page|seite|página|pagina|pág\.?|страниц\w*|페이지)\s*#?\s*(?<v>{Range}|{Roman}(?:\s*[-–]\s*{Roman})?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new($@"第\s*(?<v>{Range})\s*页", RegexOptions.CultureInvariant),
        new($@"(?<v>{Range})\s*(?:ページ|페이지)", RegexOptions.CultureInvariant),
    ];

    private static readonly Regex[] LocationPatterns =
    [
        new($@"(?:location|loc\.|position|emplacement|posición|posizione|posição|locatie|месте|위치)\s*#?\s*(?<v>{Range})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new($@"位置\s*(?:no\.?)?\s*#?\s*(?<v>{Range})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    /// <summary>True when the text is laid out as a clippings file.</summary>
    public static bool Looks(string text) => SeparatorLine.IsMatch(text);

    public static (IReadOnlyList<ImportSourceBook> Books, IReadOnlyList<HighlightImportSkipped> Skipped) Parse(string text)
    {
        var books = new Dictionary<string, BookEntries>(StringComparer.Ordinal);
        var order = new List<string>();
        var skipped = new SkipTally();

        foreach (var block in Blocks(text))
        {
            var entry = ReadEntry(block, out var title, out var author, out var reason);
            if (entry is null)
            {
                skipped.Add(title, author, reason!);
                continue;
            }

            var key = SourceKey(title, author);
            if (!books.TryGetValue(key, out var book))
            {
                book = new BookEntries(title, author);
                books[key] = book;
                order.Add(key);
            }

            if (entry.Kind == EntryKind.Highlight)
                book.AddHighlight(entry);
            else
                book.Notes.Add(entry);
        }

        var result = order
            .Select(key => new ImportSourceBook(key, books[key].Title, books[key].Author, null, books[key].Annotations()))
            .Where(book => book.Annotations.Count > 0)
            .ToList();
        return (result, skipped.ToList());
    }

    internal static string SourceKey(string? title, string? author) =>
        $"kindle:{BookIdentityNormalizer.NormalizeTitle(title)}|{BookIdentityNormalizer.NormalizeAuthor(author)}";

    /// <summary>"Title (Last, First)" → ("Title", "First Last").</summary>
    internal static (string? Title, string? Author) TitleAndAuthor(string line)
    {
        var value = line.Replace("﻿", string.Empty).Trim();
        if (value.Length == 0)
            return (null, null);
        if (!value.EndsWith(')'))
            return (value, null);

        // The author is the last bracketed group, which may itself hold
        // brackets; a title can carry its own "(Penguin Classics)" before it.
        var depth = 0;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (value[i] == ')')
                depth++;
            else if (value[i] == '(' && --depth == 0)
            {
                var title = value[..i].Trim();
                var author = value[(i + 1)..^1].Trim();
                if (title.Length == 0)
                    return (value, null);
                return (title, author.Length == 0 ? null : Author(author));
            }
        }

        return (value, null);
    }

    /// <summary>
    /// "Austen, Jane" → "Jane Austen"; several authors are separated by
    /// semicolons ("Austen, Jane; Brontë, Charlotte") and each is turned the
    /// same way. A comma that does not split one name in two is left alone.
    /// </summary>
    internal static string Author(string value)
    {
        var names = value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name =>
            {
                var parts = name.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                    return name;
                // "King, Jr." is not a surname and a first name.
                if (Regex.IsMatch(parts[1], @"^(jr|sr|ii|iii|iv|phd)\.?$", RegexOptions.IgnoreCase))
                    return name;
                return $"{parts[1]} {parts[0]}";
            })
            .ToList();
        return string.Join(", ", names);
    }

    internal static (int Start, int End)? ParseRange(string value)
    {
        var parts = value.Split(['-', '–'], 2, StringSplitOptions.TrimEntries);
        if (!TryNumber(parts[0], out var start, out var startDigits))
            return null;
        if (parts.Length == 1 || !TryNumber(parts[1], out var end, out var endDigits))
            return (start, start);

        // Older firmware abbreviates the end of a range: "1180-81" is 1180-1181.
        if (end < start && endDigits.Length < startDigits.Length
            && int.TryParse(startDigits[..(startDigits.Length - endDigits.Length)] + endDigits, out var expanded))
            end = expanded;
        return end < start ? (start, start) : (start, end);
    }

    private static bool TryNumber(string value, out int number, out string digits)
    {
        digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        number = 0;
        return digits.Length is > 0 and <= 9 && int.TryParse(digits, out number);
    }

    private static IEnumerable<List<string>> Blocks(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var block = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Replace("﻿", string.Empty);
            if (line.Trim() == Separator)
            {
                if (block.Any(candidate => candidate.Trim().Length > 0))
                    yield return block;
                block = [];
                continue;
            }

            block.Add(line);
        }

        // A file cut off mid-entry still holds that entry.
        if (block.Any(candidate => candidate.Trim().Length > 0))
            yield return block;
    }

    private static Entry? ReadEntry(List<string> block, out string? title, out string? author, out string? reason)
    {
        var start = block.FindIndex(line => line.Trim().Length > 0);
        (title, author) = TitleAndAuthor(block[start]);
        reason = UnrecognisedReason;

        if (start + 1 >= block.Count)
            return null;
        var meta = block[start + 1].Trim();
        if (!meta.StartsWith('-'))
            return null;

        var body = string.Join('\n', block.Skip(start + 2)).Trim();
        var segments = meta.TrimStart('-').Split('|', StringSplitOptions.TrimEntries);
        var kind = Kind(segments[0]);
        if (kind is null)
            return null;

        if (kind == EntryKind.Bookmark)
        {
            reason = BookmarkReason;
            return null;
        }

        if (body.Length == 0)
        {
            reason = EmptyReason;
            return null;
        }

        // "<You have reached the clipping limit for this item>", worded in the
        // device's language.
        if (kind == EntryKind.Highlight && body.StartsWith('<') && body.EndsWith('>') && !body.Contains('\n') && body.Length < 200)
        {
            reason = ClippingLimitReason;
            return null;
        }

        string? page = null;
        (int Start, int End)? location = null;
        foreach (var segment in segments)
        {
            page ??= Match(PagePatterns, segment);
            if (location is null && Match(LocationPatterns, segment) is { } value)
                location = ParseRange(value);
        }

        reason = null;
        return new Entry(kind.Value, body, page is null ? null : Page(page), location);
    }

    private static string? Match(Regex[] patterns, string segment)
    {
        foreach (var pattern in patterns)
        {
            var match = pattern.Match(segment);
            if (match.Success)
                return match.Groups["v"].Value.Trim();
        }

        return null;
    }

    // Arabic page numbers lose their separators ("1,204" → "1204"); roman
    // front-matter numbers stay as printed.
    private static string Page(string value)
    {
        if (!value.Any(char.IsAsciiDigit))
            return value.ToLowerInvariant().Replace(" ", string.Empty).Replace('–', '-');
        var range = ParseRange(value);
        return range is null ? value : range.Value.Start == range.Value.End ? $"{range.Value.Start}" : $"{range.Value.Start}-{range.Value.End}";
    }

    private static EntryKind? Kind(string segment)
    {
        var value = segment.ToLowerInvariant();
        if (BookmarkWords.Any(value.Contains))
            return EntryKind.Bookmark;
        if (NoteWords.Any(value.Contains))
            return EntryKind.Note;
        if (HighlightWords.Any(value.Contains))
            return EntryKind.Highlight;
        return null;
    }

    private static string Comparable(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
                builder.Append(' ');
            builder.Append(char.ToLowerInvariant(ch));
            space = false;
        }

        return builder.ToString();
    }

    private enum EntryKind
    {
        Highlight,
        Note,
        Bookmark,
    }

    private sealed record Entry(EntryKind Kind, string Text, string? Page, (int Start, int End)? Location)
    {
        public bool Overlaps(Entry other) =>
            Location is { } a && other.Location is { } b
                ? a.Start <= b.End && b.Start <= a.End
                : Location is null && other.Location is null && Page is not null && Page == other.Page;

        public bool SameRange(Entry other) =>
            Location is { } a && other.Location is { } b
                ? a == b
                : Location is null && other.Location is null && Page == other.Page;

        // A note sits at its highlight's last location.
        public bool Holds(Entry note) =>
            Location is { } range && note.Location is { } at
                ? range.Start <= at.Start && at.Start <= range.End
                : Location is null && note.Location is null && Page is not null && Page == note.Page;

        public (string Kind, string? Value) Anchor() =>
            Location is { } range
                ? (LocationAnchor, range.Start == range.End ? $"{range.Start}" : $"{range.Start}-{range.End}")
                : Page is not null
                    ? (PageAnchor, Page)
                    : (UnknownAnchor, null);
    }

    private sealed class BookEntries(string? title, string? author)
    {
        public string? Title { get; } = title;
        public string? Author { get; } = author;
        public List<Entry> Highlights { get; } = [];
        public List<Entry> Notes { get; } = [];

        /// <summary>
        /// An edited highlight arrives as a new entry over the same passage.
        /// The same range is always the later version; an overlapping one
        /// that contains the other's text (or is contained by it) is the same
        /// highlight extended or trimmed, and the longer text is kept.
        /// </summary>
        public void AddHighlight(Entry entry)
        {
            var text = Comparable(entry.Text);
            for (var i = 0; i < Highlights.Count; i++)
            {
                var kept = Highlights[i];
                if (!entry.Overlaps(kept) && !entry.SameRange(kept))
                    continue;

                var keptText = Comparable(kept.Text);
                if (entry.SameRange(kept) && (entry.Location is not null || entry.Page is not null))
                {
                    Highlights[i] = entry;
                    return;
                }

                if (text.Contains(keptText, StringComparison.Ordinal) || keptText.Contains(text, StringComparison.Ordinal))
                {
                    Highlights[i] = text.Length >= keptText.Length ? entry : kept;
                    return;
                }
            }

            Highlights.Add(entry);
        }

        public List<ImportAnnotation> Annotations()
        {
            var notes = new Dictionary<int, string>();
            var loose = new List<Entry>();
            foreach (var note in Notes)
            {
                // The highlight that ends at the note's location first, then any
                // that spans it; the latest of those, as later entries are newer.
                var index = Highlights.FindLastIndex(highlight => highlight.Location is { } range && note.Location is { } at && range.End == at.Start);
                if (index < 0)
                    index = Highlights.FindLastIndex(highlight => highlight.Holds(note));

                if (index >= 0)
                {
                    // An edited note is a new entry at the same place.
                    notes[index] = note.Text;
                    continue;
                }

                var same = loose.FindIndex(other => other.SameRange(note) && (note.Location is not null || note.Page is not null));
                if (same >= 0)
                    loose[same] = note;
                else
                    loose.Add(note);
            }

            var annotations = new List<ImportAnnotation>(Highlights.Count + loose.Count);
            for (var i = 0; i < Highlights.Count; i++)
            {
                var (kind, value) = Highlights[i].Anchor();
                annotations.Add(new ImportAnnotation(kind, value, Highlights[i].Text, notes.GetValueOrDefault(i, string.Empty)));
            }

            foreach (var note in loose)
            {
                var (kind, value) = note.Anchor();
                annotations.Add(new ImportAnnotation(kind, value, null, note.Text));
            }

            return annotations;
        }
    }
}

/// <summary>Counts what a file holds that cannot be imported, by book and reason.</summary>
internal sealed class SkipTally
{
    private readonly Dictionary<(string? Title, string? Author, string Reason), int> _counts = [];
    private readonly List<(string? Title, string? Author, string Reason)> _order = [];

    public void Add(string? title, string? author, string reason)
    {
        var key = (title, author, reason);
        if (_counts.TryGetValue(key, out var count))
        {
            _counts[key] = count + 1;
            return;
        }

        _counts[key] = 1;
        _order.Add(key);
    }

    public List<HighlightImportSkipped> ToList() =>
        _order.Select(key => new HighlightImportSkipped(key.Title, key.Author, _counts[key], key.Reason)).ToList();
}
