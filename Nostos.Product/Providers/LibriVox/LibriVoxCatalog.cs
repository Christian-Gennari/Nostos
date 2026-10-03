using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.LibriVox;

/// <summary>
/// LibriVox's machine-readable catalogue, reduced to the shapes the provider
/// needs.
///
/// Everything LibriVox-shaped stops here: the JSON field names, the
/// stringly-typed numbers, the archive.org URLs and the API's query quirks are
/// all absorbed in this file, so a change to the feed is a change to one place
/// and nothing downstream can accidentally depend on it.
///
/// The API is deliberately tiny. It exposes:
/// <list type="bullet">
///   <item><c>?id=&lt;n&gt;</c> — one recording,</item>
///   <item><c>?title=^&lt;prefix&gt;</c> — titles STARTING WITH the text,</item>
///   <item><c>?author=^&lt;prefix&gt;</c> — author names starting with the text,</item>
///   <item><c>?genre=^&lt;prefix&gt;</c>, plus <c>limit</c>/<c>offset</c>.</item>
/// </list>
/// There is no free-text search and no substring matching, which is why the
/// provider searches titles and then authors rather than pretending it can do
/// more.
/// </summary>
internal static partial class LibriVoxCatalog
{
    public const string BaseUrl = "https://librivox.org";

    /// <summary>The JSON feed. <c>extended=1</c> is what includes the <c>sections</c> array.</summary>
    public const string ApiPath = "/api/feed/audiobooks/";

    public const string CoverHostUrl = "https://archive.org";

    /// <summary>
    /// LibriVox's own statement, quoted. Its recordings are released into the
    /// public domain; this is the source's position, not a Nostos legal claim
    /// about every jurisdiction.
    /// </summary>
    public const string RightsStatement =
        "LibriVox recordings are in the public domain.";

    public const string RightsUrl = "https://librivox.org/pages/public-domain/";

    /// <summary>
    /// The one asset a LibriVox item offers: every section, combined into a
    /// single chaptered M4B. There is deliberately no per-track asset — the
    /// multi-track form is an implementation detail of the source, not
    /// something a user should be able to import.
    /// </summary>
    public const string AudiobookAssetId = "m4b";

    /// <summary>
    /// LibriVox ids are numeric. Checked before the id is placed in a query, so
    /// a crafted id cannot alter the request beyond the value it should be.
    /// </summary>
    [GeneratedRegex(@"^\d{1,7}$")]
    private static partial Regex RecordingId();

    /// <summary>
    /// An archive.org item identifier, as used in a URL path segment. Constrained
    /// to the characters archive.org actually issues: these values come from a
    /// remote JSON document and are interpolated into URLs, so they are treated
    /// as untrusted input rather than assumed benign.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$")]
    private static partial Regex ArchiveIdentifier();

    internal sealed record Section(int Number, string Title, Uri ListenUrl, IReadOnlyList<string> Readers);

    internal sealed record Book(
        string Id,
        string Title,
        string? Description,
        string? Language,
        string? Author,
        string? Narrator,
        string? Duration,
        string? Categories,
        string? PublishedDate,
        ProviderCover? Cover,
        IReadOnlyList<Section> Sections,
        string Url);

    public static bool IsValidId(string? externalId) =>
        !string.IsNullOrWhiteSpace(externalId) && RecordingId().IsMatch(externalId.Trim());

    /// <summary>
    /// Reads one recording out of the feed. Returns null when the record has no
    /// usable id or title, which is how the catalogue's "no such recording"
    /// response and any malformed entry are handled alike.
    /// </summary>
    public static Book? ParseBook(JsonElement book)
    {
        var id = Text(book, "id");
        var title = Text(book, "title");

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || !IsValidId(id))
            return null;

        var identifier = ArchiveIdentifierFrom(book);
        var sections = ParseSections(book);

        return new Book(
            Id: id.Trim(),
            Title: title.Trim(),
            Description: CleanDescription(Text(book, "description")),
            Language: Text(book, "language"),
            Author: AuthorName(book),
            // One reader is a name worth showing; a full cast is better
            // summarised than listed.
            Narrator: SummarizeReaders(sections),
            Duration: Text(book, "totaltime"),
            Categories: GenreNames(book),
            PublishedDate: Text(book, "copyright_year"),
            Cover: CoverFor(identifier),
            Sections: sections,
            Url: ItemUrl(book, id.Trim()));
    }

    /// <summary>
    /// The cover archive.org serves for an item, derived from its identifier.
    /// Deterministic, so a search result carries a cover without a second
    /// request per result — and <c>services/img</c> is archive.org's own
    /// thumbnail service rather than a file name guessed from a convention.
    /// </summary>
    public static ProviderCover? CoverFor(string? archiveIdentifier) =>
        string.IsNullOrWhiteSpace(archiveIdentifier)
            ? null
            : new ProviderCover(
                new Uri($"{CoverHostUrl}/services/img/{archiveIdentifier}"),
                "image/jpeg",
                ".jpg");

    /// <summary>
    /// The archive.org item behind a recording, taken from its details page and
    /// falling back to the download URL. Null when neither yields something that
    /// looks like a real identifier.
    /// </summary>
    public static string? ArchiveIdentifierFrom(JsonElement book)
    {
        var details = Text(book, "url_iarchive");
        if (!string.IsNullOrWhiteSpace(details))
        {
            var marker = details.IndexOf("/details/", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                var candidate = details[(marker + "/details/".Length)..].Trim('/');
                var end = candidate.IndexOfAny(['/', '?', '#']);
                if (end >= 0)
                    candidate = candidate[..end];

                if (ArchiveIdentifier().IsMatch(candidate))
                    return candidate;
            }
        }

        // https://www.archive.org/download/<identifier>/<file>.mp3
        foreach (var section in Sections(book))
        {
            var segments = section.ListenUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var download = Array.FindIndex(segments, s => s.Equals("download", StringComparison.OrdinalIgnoreCase));
            if (download >= 0 && download + 1 < segments.Length && ArchiveIdentifier().IsMatch(segments[download + 1]))
                return segments[download + 1];
        }

        return null;
    }

    /// <summary>
    /// The sections, in the order the recording plays.
    ///
    /// Ordering is explicit rather than trusting the array order: the tracks are
    /// concatenated in this sequence, so getting it wrong would scramble the
    /// audiobook. <c>section_number</c> is the source's own statement of order.
    /// </summary>
    public static IReadOnlyList<Section> ParseSections(JsonElement book)
    {
        if (!book.TryGetProperty("sections", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var sections = new List<Section>();

        foreach (var element in array.EnumerateArray())
        {
            var raw = Text(element, "listen_url");
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            // Only an absolute https URL is accepted; a relative or non-https
            // value is dropped rather than resolved against anything.
            if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                continue;

            var number = Number(element, "section_number");
            var title = Text(element, "title");

            sections.Add(new Section(
                Number: number,
                Title: string.IsNullOrWhiteSpace(title) ? $"Section {number}" : title.Trim(),
                ListenUrl: uri,
                Readers: ReaderNames(element)));
        }

        return sections
            .OrderBy(s => s.Number)
            .ToList();
    }

    private static IEnumerable<Section> Sections(JsonElement book) => ParseSections(book);

    private static IReadOnlyList<string> ReaderNames(JsonElement section)
    {
        if (!section.TryGetProperty("readers", out var readers) || readers.ValueKind != JsonValueKind.Array)
            return [];

        var names = new List<string>();
        foreach (var reader in readers.EnumerateArray())
        {
            var name = Text(reader, "display_name");
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name.Trim());
        }

        return names;
    }

    /// <summary>
    /// Deduplicated reader names across every section, summarised when a
    /// recording is a full cast. A solo reading produces one name, which is what
    /// the user actually wants to see; a nine-voice dramatic reading would
    /// otherwise fill the field with a cast list.
    /// </summary>
    public static string? SummarizeReaders(IReadOnlyList<Section> sections)
    {
        var names = sections
            .SelectMany(section => section.Readers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count switch
        {
            0 => null,
            <= 4 => string.Join(", ", names),
            _ => $"{names[0]} and {names.Count - 1} others",
        };
    }

    /// <summary>
    /// The author, built from the structured name fields the feed provides.
    /// </summary>
    private static string? AuthorName(JsonElement book)
    {
        if (!book.TryGetProperty("authors", out var authors) || authors.ValueKind != JsonValueKind.Array)
            return null;

        var names = new List<string>();

        foreach (var author in authors.EnumerateArray())
        {
            var first = Text(author, "first_name");
            var last = Text(author, "last_name");
            var name = string.Join(' ', new[] { first, last }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names.Count == 0 ? null : string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string? GenreNames(JsonElement book)
    {
        if (!book.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
            return null;

        var names = new List<string>();
        foreach (var genre in genres.EnumerateArray())
        {
            var name = Text(genre, "name");
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name.Trim());
        }

        return names.Count == 0 ? null : string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string ItemUrl(JsonElement book, string id)
    {
        var url = Text(book, "url_librivox");
        return string.IsNullOrWhiteSpace(url) ? $"{BaseUrl}/?id={id}" : url.Trim();
    }

    /// <summary>
    /// LibriVox descriptions are hand-written HTML from a web form. The reader
    /// renders text, so tags are stripped and entities decoded here rather than
    /// leaving markup to surface in the UI.
    /// </summary>
    public static string? CleanDescription(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var withoutTags = Regex.Replace(html, "<[^>]*>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
        var collapsed = string.Join(' ', decoded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (collapsed.Length == 0)
            return null;

        return collapsed.Length <= 1200 ? collapsed : collapsed[..1200].TrimEnd() + "…";
    }

    /// <summary>
    /// Reads a field that the feed may send as a string or as a number —
    /// <c>num_sections</c> and <c>playtime</c> arrive as strings, while other
    /// fields are sometimes bare numbers, and a missing field is simply absent.
    /// </summary>
    private static string? Text(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int Number(JsonElement element, string property)
    {
        var text = Text(element, property);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
