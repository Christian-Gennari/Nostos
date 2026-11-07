using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Product.BookText;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// Where a result came from, in a form a connected app can show and the
/// Writing Studio can use. <see cref="Link"/> and <see cref="Markdown"/> use
/// the Studio's own citation format (<c>nostos:cite/&lt;book&gt;?p=…&amp;as=…&amp;src=…</c>,
/// see <c>WritingCitationIndex</c> and <c>citation.extension.ts</c>), so a quote
/// an app writes into a draft is a live citation and exports as a footnote.
/// </summary>
public sealed record McpCitation(
    string Book,
    string? Author,
    string? Year,
    string? Location,
    string Url,
    string Link,
    string? Markdown);

/// <summary>One book's citation details, looked up once per request.</summary>
internal sealed record McpBookInfo(Guid Id, string Title, string? Author, string? Year);

/// <summary>
/// Builds <see cref="McpCitation"/>s and app links. Scoped to one request: it
/// caches the books it looks up and reads the public origin from the request,
/// so every link points at the Nostos the app connected to.
/// </summary>
internal sealed partial class McpCitations(ILibraryService library, IHttpContextAccessor http)
{
    private const string Scheme = "nostos:cite/";
    private readonly Dictionary<Guid, McpBookInfo?> _books = [];

    [GeneratedRegex(@"\d{4}")]
    private static partial Regex YearPattern();

    /// <summary>The book's title, author and year, or null when it is not in the library.</summary>
    public async Task<McpBookInfo?> BookAsync(Guid bookId, string? knownTitle, CancellationToken ct)
    {
        if (_books.TryGetValue(bookId, out var cached))
            return cached;

        var result = await library.GetBookAsync(bookId, ct);
        McpBookInfo? info = result.Data is BookDto book
            ? new McpBookInfo(book.Id, book.Title, Blank(book.Author), YearOf(book.PublishedDate))
            : knownTitle is null ? null : new McpBookInfo(bookId, knownTitle, null, null);
        _books[bookId] = info;
        return info;
    }

    public static string? YearOf(string? publishedDate) =>
        publishedDate is null ? null : YearPattern().Match(publishedDate) is { Success: true } m ? m.Value : null;

    /// <summary>A kept note, highlight or passage.</summary>
    public McpCitation ForNote(
        Guid noteId,
        McpBookInfo book,
        string? content,
        string? selectedText,
        string? sourceAnchorKind,
        string? sourceAnchorValue,
        bool anchorVerified)
    {
        var (page, loc, label) = NoteLocator(sourceAnchorKind, sourceAnchorValue, anchorVerified);
        var quote = Blank(selectedText);
        var role = quote is null ? "note" : "quote";
        var link = CiteHref(book.Id, page, loc, role, noteId.ToString("D"));
        var words = quote ?? Blank(content);
        return new McpCitation(
            book.Title,
            book.Author,
            book.Year,
            label,
            AppUrl($"/brain/notes/{noteId:D}"),
            link,
            words is null ? null : $"[{EscapeText(words)}]({link})");
    }

    /// <summary>A passage from the text of an imported book.</summary>
    public McpCitation ForBookText(McpBookInfo book, KnowledgeBookEvidence passage)
    {
        int? page = null;
        string? loc = null;
        string? label = null;
        var query = new List<string>();
        switch (passage.SourceSegments.FirstOrDefault()?.Locator)
        {
            case PdfBookTextSourceLocator pdf:
                page = pdf.PageIndex + 1;
                label = $"p. {Blank(pdf.PageLabel) ?? page.Value.ToString(CultureInfo.InvariantCulture)}";
                query.Add($"sourcePage={page}");
                if (Blank(pdf.PageLabel) is { } pageLabel)
                    query.Add($"sourcePageLabel={Uri.EscapeDataString(pageLabel)}");
                break;
            case EpubBookTextSourceLocator epub:
                if (passage.HeadingPath.Select(Blank).FirstOrDefault(h => h is not null) is { } chapter)
                {
                    loc = CleanLoc($"c:{chapter}");
                    label = chapter;
                }

                if (Blank(epub.Cfi) is { } cfi) query.Add($"sourceCfi={Uri.EscapeDataString(cfi)}");
                query.Add($"sourceHref={Uri.EscapeDataString(epub.ResourceHref)}");
                query.Add($"sourceSpine={epub.SpineIndex}");
                if (epub.StartTextOffset is { } offset) query.Add($"sourceOffset={offset}");
                var excerpt = passage.Text.Length > 240 ? passage.Text[..240] : passage.Text;
                query.Add($"sourceExcerpt={Uri.EscapeDataString(excerpt)}");
                break;
            case AudioBookTextSourceLocator audio:
                var clock = Clock(audio.StartMs / 1000.0);
                loc = $"t:{clock}";
                label = clock;
                query.Add($"sourceTime={(audio.StartMs / 1000.0).ToString(CultureInfo.InvariantCulture)}");
                break;
        }

        var link = CiteHref(book.Id, page, loc, "quote", src: null);
        var url = query.Count > 0
            ? AppUrl($"/read/{book.Id:D}?{string.Join('&', query)}")
            : AppUrl($"/library/{book.Id:D}");
        return new McpCitation(book.Title, book.Author, book.Year, label, url, link, $"[{EscapeText(passage.Text)}]({link})");
    }

    /// <summary>A bare reference to a whole book, for a book with no location.</summary>
    public McpCitation ForBook(McpBookInfo book)
    {
        var link = CiteHref(book.Id, null, null, "reference", src: null);
        return new McpCitation(book.Title, book.Author, book.Year, null, AppUrl($"/library/{book.Id:D}"), link, null);
    }

    public string BookUrl(Guid bookId) => AppUrl($"/library/{bookId:D}");

    public string TopicUrl(Guid topicId) => AppUrl($"/brain/topics/{topicId:D}");

    public string NoteUrl(Guid noteId) => AppUrl($"/brain/notes/{noteId:D}");

    /// <summary>An absolute link into this Nostos, from the request's own origin.</summary>
    public string AppUrl(string path)
    {
        var request = http.HttpContext?.Request;
        if (request is null || !request.Host.HasValue)
            return path;
        return $"{request.Scheme}://{request.Host}{request.PathBase}{path}";
    }

    /// <summary>
    /// Where a note is, when Nostos knows it for certain (the Studio's
    /// <c>citationLocator</c>): a PDF page only when verified, a paper book's
    /// page as the reader typed it, a time in a recording when verified.
    /// </summary>
    internal static (int? Page, string? Loc, string? Label) NoteLocator(string? kind, string? value, bool verified)
    {
        var anchor = (kind ?? string.Empty).Trim().ToLowerInvariant();
        var text = (value ?? string.Empty).Trim();
        if (anchor is "pdf_page" or "physical_page")
        {
            if (anchor == "pdf_page" && !verified)
                return (null, null, null);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && page > 0
                ? (page, null, $"p. {page}")
                : (null, null, null);
        }

        if (anchor == "audio_timestamp" && verified
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0)
        {
            var clock = Clock(seconds);
            return (null, $"t:{clock}", clock);
        }

        return (null, null, null);
    }

    /// <summary>h:mm:ss, or m:ss under an hour: the player's own clock.</summary>
    internal static string Clock(double seconds)
    {
        var whole = (int)Math.Floor(seconds);
        var h = whole / 3600;
        var m = whole % 3600 / 60;
        var s = whole % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>The Studio's <c>citationHref</c>, byte for byte.</summary>
    internal static string CiteHref(Guid bookId, int? page, string? loc, string? role, string? src)
    {
        var parameters = new List<string>();
        if (page is > 0) parameters.Add($"p={page.Value.ToString(CultureInfo.InvariantCulture)}");
        if (loc is not null) parameters.Add($"loc={Uri.EscapeDataString(loc)}");
        if (role is not null) parameters.Add($"as={role}");
        if (src is not null) parameters.Add($"src={src}");
        var path = $"{Scheme}{bookId:D}";
        return parameters.Count > 0 ? $"{path}?{string.Join('&', parameters)}" : path;
    }

    /// <summary>The Studio's <c>cleanLoc</c>: <c>c:Title</c> or <c>t:12:34</c>, at most 300 characters.</summary>
    internal static string? CleanLoc(string value)
    {
        var text = Regex.Replace(value, @"\s+", " ").Trim();
        return text.Length is > 2 and <= 300 && (text.StartsWith("c:", StringComparison.Ordinal) || text.StartsWith("t:", StringComparison.Ordinal))
            ? text
            : null;
    }

    /// <summary>Link text the Studio reads back as the same words: no Markdown inside, one line.</summary>
    internal static string EscapeText(string words)
    {
        var one = Regex.Replace(words, @"\s+", " ").Trim();
        var builder = new StringBuilder(one.Length + 8);
        foreach (var c in one)
        {
            if (c is '\\' or '[' or ']' or '*' or '_' or '`')
                builder.Append('\\');
            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
