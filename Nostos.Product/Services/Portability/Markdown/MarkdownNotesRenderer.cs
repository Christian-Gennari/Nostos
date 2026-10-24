using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nostos.Backend.Services.Notes;

namespace Nostos.Backend.Services.Portability.Markdown;

/// <summary>A book as the Markdown export describes it.</summary>
public sealed record MarkdownBook(
    Guid Id,
    string Title,
    string? Subtitle,
    string? Author,
    string? Translator,
    string? Editor,
    string? Narrator,
    string? PublishedDate,
    string? Publisher,
    string? Isbn,
    string? Asin,
    string Type,
    string? FileName,
    bool HasTracks,
    string? ChaptersJson);

/// <summary>A note, highlight or passage as the Markdown export writes it.</summary>
public sealed record MarkdownNote(
    Guid Id,
    string Content,
    string? CfiRange,
    string? SelectedText,
    DateTime CreatedAt,
    string Kind,
    string SourceAnchorKind,
    string? SourceAnchorValue,
    bool AnchorVerified,
    string? PassageTitle,
    string? PassageEnd,
    IReadOnlyList<Guid> TopicIds);

public sealed record MarkdownTopic(Guid Id, string Name, string? Description);

/// <summary>Where a note sits, in words, and the raw position kept beside them.</summary>
public sealed record MarkdownLocation(string? Words, string? Position);

/// <summary>One line of a topic's file: a note that carries the topic.</summary>
public sealed record MarkdownTopicEntry(
    string BookFile,
    string BookTitle,
    string? Location,
    string? Quote,
    string? Thought,
    DateTime CreatedAt);

/// <summary>
/// The Markdown of the notes export (issue #1221): one file per book with its
/// metadata as YAML front matter and its notes in reading order, one file per
/// topic, and a README. CommonMark with Obsidian wikilinks. A note's own words
/// are the reader's Markdown and stay exactly as written, <c>[[Topic]]</c>
/// links included; text taken from a book is escaped the way the Studio's
/// Markdown export escapes text, so a quoted "*" stays a star.
/// </summary>
public static partial class MarkdownNotesRenderer
{
    public const string BooksFolder = "Books";
    public const string TopicsFolder = "Topics";
    public const string WritingsFolder = "Writings";

    private const int ExcerptLength = 240;

    [GeneratedRegex(@"\[\[(.*?)\]\]")]
    private static partial Regex ReferenceRegex();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex CfiAssertionRegex();

    [GeneratedRegex(@"[/:](\d+)")]
    private static partial Regex CfiStepRegex();

    [GeneratedRegex(@"^(\d+|[ivxlcdm]+)(-(\d+|[ivxlcdm]+))?$")]
    private static partial Regex ImportedPositionRegex();

    [GeneratedRegex(@"/DocFragment\[(\d+)\]")]
    private static partial Regex DocFragmentRegex();

    [GeneratedRegex(@"\b(\d{4})\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\s*(?:;|&|\band\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorSeparatorRegex();

    [GeneratedRegex(@"^(multiple authors|various( authors)?|unknown( author)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderNameRegex();

    // ---------------------------------------------------------------- books

    /// <summary>
    /// The people named in an author field, the way the Studio's citations read
    /// it (<c>parseNames</c>): split at ";", "&amp;" and "and"; a single name
    /// with a comma is one person ("Aurelius, Marcus"). Placeholders name nobody.
    /// </summary>
    public static IReadOnlyList<string> Authors(string? author)
    {
        var text = Regex.Replace(author ?? string.Empty, @"\s+", " ").Trim();
        if (text.Length == 0 || PlaceholderNameRegex().IsMatch(text)) return [];
        var parts = AuthorSeparatorRegex().Split(text).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 1 && parts[0].Contains(',')) return parts;
        return parts.Count == 1
            ? parts[0].Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList()
            : parts;
    }

    public static int? Year(string? publishedDate)
    {
        var match = YearRegex().Match(publishedDate ?? string.Empty);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The book's format in words: EPUB, PDF, Audiobook, Paper book…</summary>
    public static string Format(MarkdownBook book)
    {
        if (book.Type == "audiobook") return "Audiobook";
        if (book.Type == "physical") return "Paper book";
        var extension = Path.GetExtension(book.FileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "epub" => "EPUB",
            "pdf" => "PDF",
            "" => "Ebook",
            _ => extension.ToUpperInvariant(),
        };
    }

    public static string Book(MarkdownBook book, IReadOnlyList<MarkdownNote> notesInOrder)
    {
        var authors = Authors(book.Author);
        var yaml = new StringBuilder("---\n");
        YamlLine(yaml, "title", book.Title);
        YamlLine(yaml, "subtitle", book.Subtitle);
        if (authors.Count > 0)
            yaml.Append("authors: [").Append(string.Join(", ", authors.Select(Yaml))).Append("]\n");
        YamlLine(yaml, "translator", book.Translator);
        YamlLine(yaml, "editor", book.Editor);
        YamlLine(yaml, "narrator", book.Narrator);
        if (Year(book.PublishedDate) is { } year) yaml.Append("year: ").Append(year).Append('\n');
        YamlLine(yaml, "publisher", book.Publisher);
        YamlLine(yaml, "isbn", book.Isbn);
        YamlLine(yaml, "asin", book.Asin);
        YamlLine(yaml, "format", Format(book));
        yaml.Append("notes: ").Append(notesInOrder.Count).Append('\n');
        YamlLine(yaml, "nostos_id", book.Id.ToString());
        yaml.Append("---\n");

        var lines = new List<string> { yaml.ToString().TrimEnd('\n') };
        var title = string.IsNullOrWhiteSpace(book.Subtitle) ? book.Title : $"{book.Title}: {book.Subtitle}";
        lines.Add($"# {EscapeLineStart(EscapeText(title.Trim()))}");
        if (authors.Count > 0) lines.Add($"by {EscapeLineStart(EscapeText(string.Join(", ", authors)))}");

        if (notesInOrder.Count == 0)
            lines.Add("*No notes in this book yet.*");

        var chapters = AudioChapters(book.ChaptersJson);
        foreach (var note in notesInOrder)
            lines.Add(Entry(book, note, chapters));

        return string.Join("\n\n", lines) + "\n";
    }

    private static string Entry(MarkdownBook book, MarkdownNote note, IReadOnlyList<AudioChapter> chapters)
    {
        var location = Locate(note, chapters);
        var passage = IsPassage(note);
        var kind = passage ? "Passage" : string.IsNullOrWhiteSpace(note.SelectedText) ? "Note" : "Highlight";

        var heading = passage
            ? $"Passage: {(string.IsNullOrWhiteSpace(note.PassageTitle) ? "Untitled" : note.PassageTitle.Trim())}"
            : location.Words ?? kind;
        var parts = new List<string> { $"## {EscapeLineStart(EscapeText(heading))}" };

        if (!string.IsNullOrWhiteSpace(note.SelectedText))
            parts.Add(Quote(note.SelectedText));

        var thought = note.Content?.Trim();
        if (!string.IsNullOrEmpty(thought)) parts.Add(thought);

        var meta = new List<string> { kind };
        if (passage && location.Words is not null) meta.Add(EscapeText(location.Words));
        meta.Add(note.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (location.Position is { } position) meta.Add(Code(position));
        parts.Add($"*{string.Join(" · ", meta)}*");

        return string.Join("\n\n", parts);
    }

    private static bool IsPassage(MarkdownNote note) =>
        string.Equals(note.Kind, NoteKinds.Passage, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------- location

    /// <summary>
    /// Where a note sits, in the words a reader uses. A page or a time is said
    /// only when the reader verified it (the rule the app's own "Return to
    /// passage" follows); a page or time the reader typed for a paper book or
    /// another app is said as theirs. An EPUB position has no page, so it is
    /// named by its section of the book and kept as its CFI beside the words.
    /// </summary>
    public static MarkdownLocation Locate(MarkdownNote note, IReadOnlyList<AudioChapter>? chapters = null)
    {
        var kind = note.SourceAnchorKind?.Trim().ToLowerInvariant() ?? "unknown";
        var value = note.SourceAnchorValue?.Trim() ?? string.Empty;
        var end = note.PassageEnd?.Trim();
        var passage = IsPassage(note);

        if (note.AnchorVerified && kind == "pdf_page" && PositivePage(value) is { } page)
        {
            var last = passage ? PositivePage(end) : null;
            return new(last is { } to && to > page ? $"Pages {page}–{to}" : $"Page {page}", null);
        }

        if (note.AnchorVerified && kind == "audio_timestamp" && Seconds(value) is { } seconds)
        {
            var words = $"At {Time(seconds)}";
            if (passage && Seconds(end) is { } until && until > seconds)
                words = $"From {Time(seconds)} to {Time(until)}";
            if (ChapterAt(chapters, seconds) is { } chapter)
                words += $", in “{chapter}”";
            return new(words, null);
        }

        var cfi = note.AnchorVerified && kind == "epub_cfi" && value.StartsWith("epubcfi(", StringComparison.Ordinal)
            ? value
            : note.CfiRange?.Trim() is { } range && range.StartsWith("epubcfi(", StringComparison.Ordinal) ? range : null;
        if (cfi is not null)
        {
            var section = EpubSection(cfi);
            return new(section is { } n ? $"Section {n} of the book" : "In the book", cfi);
        }

        switch (kind)
        {
            case "physical_page" when PositivePage(value) is { } paperPage:
                return new($"Page {paperPage} (as noted)", null);
            case "external_audio_timestamp" when Seconds(value) is { } elsewhere:
                return new($"At {Time(elsewhere)} (as noted)", null);
            case "koreader_page" when PositivePage(value) is { } koreaderPage:
                return new($"Page {koreaderPage} in KOReader", null);
            case "koreader_xpointer":
                var fragment = DocFragmentRegex().Match(value);
                return new(fragment.Success ? $"Section {fragment.Groups[1].Value} of the book (from KOReader)" : "From KOReader", null);
        }
        if (kind.StartsWith("kobo_", StringComparison.Ordinal)) return new("From Kobo", null);
        // Kindle and Readwise positions are theirs, never a verified page (#1219).
        if (kind.StartsWith("kindle_", StringComparison.Ordinal))
            return new(ImportedPosition(kind["kindle_".Length..], value) is { } onKindle ? $"{onKindle} on Kindle" : "From Kindle", null);
        if (kind.StartsWith("readwise_", StringComparison.Ordinal))
            return new(ImportedPosition(kind["readwise_".Length..], value) is { } inReadwise ? $"{inReadwise} in Readwise" : "From Readwise", null);
        return new(null, null);
    }

    // "location" + "5-7" → "Location 5–7"; "page" + "12" → "Page 12".
    private static string? ImportedPosition(string type, string value)
    {
        if (!ImportedPositionRegex().IsMatch(value))
            return null;
        var range = value.Replace('-', '–');
        return type switch
        {
            "location" => $"Location {range}",
            "page" => $"Page {range}",
            _ => null,
        };
    }

    /// <summary>
    /// Sorts notes in reading order: by their place in the book, EPUB positions
    /// step by step, pages and times by number; a note with no place after
    /// those, by when it was written.
    /// </summary>
    public static IReadOnlyList<MarkdownNote> InReadingOrder(IEnumerable<MarkdownNote> notes) =>
        notes.Select(note => (note, key: OrderKey(note)))
            .OrderBy(x => x.key.Rank)
            .ThenBy(x => x.key.Steps, StepComparer.Instance)
            .ThenBy(x => x.note.CreatedAt)
            .ThenBy(x => x.note.Id)
            .Select(x => x.note)
            .ToList();

    private static (int Rank, IReadOnlyList<double> Steps) OrderKey(MarkdownNote note)
    {
        var kind = note.SourceAnchorKind?.Trim().ToLowerInvariant() ?? "unknown";
        var value = note.SourceAnchorValue?.Trim() ?? string.Empty;
        var cfi = kind == "epub_cfi" && value.StartsWith("epubcfi(", StringComparison.Ordinal)
            ? value
            : note.CfiRange?.Trim() is { } range && range.StartsWith("epubcfi(", StringComparison.Ordinal) ? range : null;
        if (cfi is not null) return (0, CfiSteps(cfi));
        if (kind is "pdf_page" or "physical_page" or "koreader_page" && PositivePage(value) is { } page) return (1, [page]);
        if (kind is "audio_timestamp" or "external_audio_timestamp" && Seconds(value) is { } seconds) return (2, [seconds]);
        if (kind == "koreader_xpointer" && DocFragmentRegex().Match(value) is { Success: true } fragment)
            return (0, [0, int.Parse(fragment.Groups[1].Value, CultureInfo.InvariantCulture) * 2]);
        return (9, []);
    }

    /// <summary>
    /// A CFI's steps as numbers, start of a range included: <c>epubcfi(/6/8!/4/2,/1:0,/1:5)</c>
    /// reads as 6, 8, 4, 2, 1, 0, so comparing step by step is comparing places.
    /// </summary>
    public static IReadOnlyList<double> CfiSteps(string cfi)
    {
        var inner = cfi.Trim();
        if (inner.StartsWith("epubcfi(", StringComparison.Ordinal)) inner = inner[8..];
        if (inner.EndsWith(')')) inner = inner[..^1];
        var parts = CfiAssertionRegex().Replace(inner, string.Empty).Split(',');
        var path = parts.Length >= 2 ? parts[0] + parts[1] : parts[0];
        return CfiStepRegex().Matches(path)
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
    }

    /// <summary>The spine item a CFI is in, counted from 1 (its second step is 2, 4, 6…).</summary>
    public static int? EpubSection(string cfi)
    {
        var steps = CfiSteps(cfi);
        return steps.Count >= 2 && steps[1] >= 2 ? (int)(steps[1] / 2) : null;
    }

    private sealed class StepComparer : IComparer<IReadOnlyList<double>>
    {
        public static readonly StepComparer Instance = new();

        public int Compare(IReadOnlyList<double>? x, IReadOnlyList<double>? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
            {
                var c = x[i].CompareTo(y[i]);
                if (c != 0) return c;
            }
            return x.Count.CompareTo(y.Count);
        }
    }

    public sealed record AudioChapter(string Title, double StartTime);

    public static IReadOnlyList<AudioChapter> AudioChapters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<AudioChapter>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Title))
                .OrderBy(c => c.StartTime)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ChapterAt(IReadOnlyList<AudioChapter>? chapters, double seconds) =>
        chapters?.LastOrDefault(c => c.StartTime <= seconds + 0.001)?.Title.Trim();

    private static int? PositivePage(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var page) && page > 0 ? page : null;

    private static double? Seconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && double.IsFinite(seconds) && seconds >= 0 ? seconds : null;

    /// <summary>h:mm:ss, or m:ss under an hour: the player's own clock.</summary>
    public static string Time(double seconds)
    {
        var whole = (long)Math.Floor(Math.Max(0, seconds));
        var h = whole / 3600;
        var m = whole % 3600 / 60;
        var s = whole % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    // --------------------------------------------------------------- topics

    /// <summary>
    /// What a topic file lists for one note: where it is, a little of the book's
    /// words and of the reader's, so the list reads without opening each book.
    /// </summary>
    public static MarkdownTopicEntry TopicEntry(string bookFile, MarkdownBook book, MarkdownNote note, IReadOnlyList<AudioChapter>? chapters)
    {
        var location = Locate(note, chapters).Words;
        if (IsPassage(note) && !string.IsNullOrWhiteSpace(note.PassageTitle))
            location = location is null ? $"Passage: {note.PassageTitle.Trim()}" : $"Passage: {note.PassageTitle.Trim()}, {location}";
        return new MarkdownTopicEntry(
            bookFile,
            book.Title,
            location,
            Excerpt(note.SelectedText),
            Excerpt(Thought(note.Content)),
            note.CreatedAt);
    }

    public static string Topic(MarkdownTopic topic, string fileName, IReadOnlyList<MarkdownTopicEntry> entries)
    {
        var yaml = new StringBuilder("---\n");
        // A name a file cannot hold ("C#", "Either/or") is still what the notes
        // link to, so it travels as an alias.
        if (!string.Equals(fileName, topic.Name.Trim(), StringComparison.Ordinal))
            yaml.Append("aliases: [").Append(Yaml(topic.Name.Trim())).Append("]\n");
        yaml.Append("notes: ").Append(entries.Count).Append('\n');
        YamlLine(yaml, "nostos_id", topic.Id.ToString());
        yaml.Append("---\n");

        var lines = new List<string> { yaml.ToString().TrimEnd('\n'), $"# {EscapeLineStart(EscapeText(topic.Name.Trim()))}" };
        if (!string.IsNullOrWhiteSpace(topic.Description))
            lines.Add(topic.Description.Trim());

        if (entries.Count == 0)
        {
            lines.Add("*No notes carry this topic yet.*");
            return string.Join("\n\n", lines) + "\n";
        }

        lines.Add("## Notes");
        foreach (var book in entries.GroupBy(e => e.BookFile))
        {
            var first = book.First();
            lines.Add($"### {WikiLink($"{BooksFolder}/{first.BookFile}", first.BookTitle)}");
            lines.Add(string.Join("\n", book.Select(TopicLine)));
        }
        return string.Join("\n\n", lines) + "\n";
    }

    private static string TopicLine(MarkdownTopicEntry entry)
    {
        var parts = new List<string>();
        if (entry.Location is not null) parts.Add(EscapeText(entry.Location));
        if (entry.Quote is not null) parts.Add($"“{EscapeText(entry.Quote)}”");
        if (entry.Thought is not null) parts.Add(entry.Thought);
        if (parts.Count == 0) parts.Add(entry.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return $"- {string.Join(" — ", parts)}";
    }

    /// <summary>A note's own words without its lines of bare <c>[[Topic]]</c> references.</summary>
    public static string? Thought(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var kept = content.Split('\n').Where(line => !IsReferenceLine(line));
        var text = string.Join(' ', kept.Select(line => line.Trim()).Where(line => line.Length > 0));
        return text.Length == 0 ? null : text;
    }

    private static bool IsReferenceLine(string line) =>
        ReferenceRegex().IsMatch(line)
        && ReferenceRegex().Replace(line, string.Empty).All(c => char.IsWhiteSpace(c) || c is ',' or ';' or '·' or '|');

    private static string? Excerpt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var single = Regex.Replace(text, @"\s+", " ").Trim();
        if (single.Length <= ExcerptLength) return single;
        var cut = MarkdownExportNames.CutToBytes(single, ExcerptLength);
        // Never end inside a [[link]]: cut before an unclosed one.
        var open = cut.LastIndexOf("[[", StringComparison.Ordinal);
        if (open >= 0 && cut.IndexOf("]]", open, StringComparison.Ordinal) < 0) cut = cut[..open];
        return cut.TrimEnd() + "…";
    }

    /// <summary><c>[[Books/File|Shown words]]</c>, with the words made safe inside a link.</summary>
    public static string WikiLink(string target, string? shown)
    {
        var words = Regex.Replace((shown ?? string.Empty).Replace('|', '/').Replace("[[", "[").Replace("]]", "]"), @"\s+", " ").Trim();
        return words.Length == 0 || words == target ? $"[[{target}]]" : $"[[{target}|{words}]]";
    }

    // -------------------------------------------------------------- writing

    public static string Writing(string name, DateTime createdAt, DateTime updatedAt, string? content)
    {
        var yaml = new StringBuilder("---\n");
        YamlLine(yaml, "title", name);
        YamlLine(yaml, "created", createdAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        YamlLine(yaml, "updated", updatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        yaml.Append("---\n");
        var body = Writings.WritingCitationIndex.CitationsAsWords(content).Trim();
        return body.Length == 0 ? yaml.ToString() : yaml + "\n" + body + "\n";
    }

    // --------------------------------------------------------------- readme

    public static string Readme(DateTime exportedAt, int books, int notes, int topics, int? writings)
    {
        var lines = new List<string>
        {
            "# Your Nostos notes",
            $"Exported from Nostos on {exportedAt.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}: {Count(notes, "note")} from {Count(books, "book")}, and {Count(topics, "topic")}.",
            "Every file is plain Markdown. Open this folder as an Obsidian vault, or read the files in any text editor.",
            "## What is where",
            string.Join("\n",
            [
                $"- `{BooksFolder}/` holds one file for each book you have notes in. Its details (title, authors, year, ISBN, format) are at the top, between the `---` lines. Below them are your highlights, passages and notes in reading order, each with where it is in the book and the date you saved it.",
                $"- `{TopicsFolder}/` holds one file for each topic: your own words about it, if you wrote any, and a list of the notes that carry it, with a link to each book's file.",
                .. writings is null ? Array.Empty<string>() : [$"- `{WritingsFolder}/` holds your Writing Studio documents, in their folders. A citation is kept as the words it reads as."],
            ]),
            "## Links",
            "A `[[Topic]]` in a note is a link to that topic's file, as Nostos shows it. Obsidian and other wiki-style editors follow these links. Where a topic's name has a character a file name cannot hold, the file has a slightly different name and lists the original as an alias.",
            "## Locations",
            "A page or a time is given when Nostos knows it: pages for PDFs, times for audiobooks (with the chapter when the recording has chapters). An EPUB has no fixed pages, so a note there names its section of the book and keeps its exact position (an EPUB CFI) beside it. A page you typed for a paper book is marked “as noted”.",
            "## What is not here",
            "Books without notes, and the book files and covers themselves. To move your whole library to another Nostos, use the portable .nostos archive in Settings.",
        };
        return string.Join("\n\n", lines) + "\n";
    }

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? string.Empty : "s")}");

    // ------------------------------------------------------------- escaping

    private static string Quote(string text) =>
        string.Join("\n", EscapeLineStart(EscapeText(text.Trim().Replace("\r\n", "\n")))
            .Split('\n')
            .Select(line => line.Trim().Length == 0 ? ">" : $"> {line.TrimEnd()}"));

    private static string Code(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var ticks = new string('`', longest + 1);
        var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : string.Empty;
        return $"{ticks}{pad}{text}{pad}{ticks}";
    }

    /// <summary>Every character CommonMark could read as syntax, escaped (the Studio's <c>escapeText</c>).</summary>
    public static string EscapeText(string text)
    {
        var escaped = Regex.Replace(text, @"[\\`*_\[\]<>|~]", @"\$&");
        escaped = Regex.Replace(escaped, @"&(?=#?[A-Za-z0-9]+;)", @"\&");
        return Regex.Replace(escaped, @"!(?=\\\[)", @"\!");
    }

    /// <summary>What would start a block at the start of a line: a heading, quote, list, rule or fence.</summary>
    public static string EscapeLineStart(string text) =>
        string.Join("\n", text.Split('\n').Select(line =>
            Regex.Replace(Regex.Replace(line, @"^(\s*)([#>+=-])", @"$1\$2"), @"^(\s*\d+)([.)])", @"$1\$2")));

    /// <summary>A YAML double-quoted scalar: every string reads back as itself.</summary>
    public static string Yaml(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append(@"\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append(@"\n"); break;
                case '\r': builder.Append(@"\r"); break;
                case '\t': builder.Append(@"\t"); break;
                default:
                    if (char.IsControl(c)) builder.Append(@"\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    private static void YamlLine(StringBuilder yaml, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        yaml.Append(key).Append(": ").Append(Yaml(value.Trim())).Append('\n');
    }
}
