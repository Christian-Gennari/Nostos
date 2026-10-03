using System.Xml.Linq;
using System.Globalization;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>One downloadable representation, with the location the catalogue gave us.</summary>
internal sealed record GutenbergAsset(
    string Id,
    string Label,
    /// <summary>Normalized MIME/format family used by the UI and acquisition planner.</summary>
    string SourceFormat,
    Uri Url,
    string FileExtension,
    string OutputLabel,
    long? SizeBytes = null,
    bool IsPreferred = false);

/// <summary>A book as Gutenberg's own feed describes it.</summary>
internal sealed record GutenbergBook(
    string Id,
    string Title,
    string? Author,
    string? Description,
    string? Language,
    string? Categories,
    string? Rights,
    IReadOnlyList<GutenbergAsset> Assets);

/// <summary>
/// Reads Project Gutenberg's OPDS/Atom feeds.
///
/// Every Gutenberg-shaped detail lives here, so a change to the catalogue's
/// protocol is a change to this file and nothing else in Nostos. The parser is a
/// pure function of the feed text, which is what makes it testable against saved
/// fixtures instead of against the live site.
///
/// Two facts about the feeds shape the whole design:
///
/// 1. The search feed carries only a title, an author and a link — no language,
///    no subjects, no rights, no usable cover. Fetching the per-book feed for
///    each of 25 results would be 25 requests per search against a service that
///    asks to be treated politely, so detail is fetched lazily, when a result is
///    actually opened or imported.
///
/// 2. A per-book feed contains SEVERAL <c>entry</c> elements — one per format
///    profile ("urn:gutenberg:1342:2" is the no-images profile, ":3" the
///    illustrated one). They describe the same book, so their acquisition links
///    are merged into one record. A genuinely different edition has its own
///    Gutenberg id and therefore its own feed.
/// </summary>
internal static class GutenbergCatalog
{
    public const string BaseUrl = "https://www.gutenberg.org";

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    private const string OpdsAcquisition = "http://opds-spec.org/acquisition";
    private const string OpdsImage = "http://opds-spec.org/image";

    /// <summary>
    /// Asset ids (and the source's own label) by the href suffix Gutenberg uses.
    /// Matching on the suffix rather than the label means the ids survive PG
    /// rewording a tooltip.
    ///
    /// The order is the preference order: EPUB3 with images first, because it is
    /// Gutenberg's own recommendation for modern readers and it keeps the
    /// illustrations, which in an illustrated classic are part of the text. The
    /// smaller no-images variant stays selectable for a constrained connection.
    /// </summary>
    private static readonly string[] PreferredFormats =
    [
        "epub3-images",
        "epub-images",
        "epub-noimages",
    ];

    public static IReadOnlyList<GutenbergBook> ParseSearch(XDocument feed)
    {
        var books = new List<GutenbergBook>();
        var entries = feed.Root?.Elements(Atom + "entry") ?? [];

        foreach (var entry in entries)
        {
            // The "No records found." placeholder is itself an entry, whose id
            // points at the catalogue index rather than at a book. Requiring a
            // parseable book id drops it without relying on its wording.
            if (BookIdFromEntry(entry) is not { } id)
                continue;

            var title = Value(entry, Atom + "title");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            // The search feed puts the author in the entry's text content, and
            // writes it the right way round ("Jane Austen") more often than the
            // per-book feed does. Normalizing handles both forms.
            var author = GutenbergAuthorName.Normalize(Value(entry, Atom + "content"));

            books.Add(new GutenbergBook(
                Id: id,
                Title: title,
                Author: author,
                Description: null,
                Language: null,
                Categories: null,
                Rights: null,
                Assets: []));
        }

        return books;
    }

    /// <summary>How many results the catalogue promised per page, for paging.</summary>
    public static int ItemsPerPage(XDocument feed)
    {
        var value = feed.Root?.Element(XName.Get("itemsPerPage", "http://a9.com/-/spec/opensearch/1.1/"))?.Value;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 25;
    }

    public static GutenbergBook? ParseDetail(XDocument feed, string id)
    {
        var entries = (feed.Root?.Elements(Atom + "entry") ?? []).ToList();
        if (entries.Count == 0)
            return null;

        var title = entries.Select(e => Value(e, Atom + "title")).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        if (string.IsNullOrWhiteSpace(title))
            return null;

        // Metadata is duplicated across the profile entries, so it is read from
        // the first and never appended to — appending would multiply every
        // subject and language by the number of profiles.
        var primary = entries[0];

        var assets = entries
            .SelectMany(ParseAssets)
            .GroupBy(asset => asset.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(asset => PreferenceIndex(asset.Id))
            .Select((asset, index) => asset with { IsPreferred = index == 0 })
            .ToList();

        var languages = entries
            .SelectMany(entry => entry.Elements(DcTerms + "language"))
            .Select(element => LanguageName(element.Value))
            .Where(value => value is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var subjects = entries
            .SelectMany(entry => entry.Elements(Atom + "category"))
            .Where(element => (string?)element.Attribute("scheme") is { } scheme
                && scheme.Contains("LCSH", StringComparison.OrdinalIgnoreCase))
            .Select(element => (string?)element.Attribute("term"))
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rights = entries
            .Select(entry => Value(entry, Atom + "rights"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        var author = GutenbergAuthorName.NormalizeAll(
            entries.SelectMany(entry => entry.Elements(Atom + "author")).Select(a => Value(a, Atom + "name")));

        return new GutenbergBook(
            Id: id,
            Title: title,
            Author: author,
            Description: Summary(primary),
            Language: languages.Count == 0 ? null : string.Join(", ", languages),
            Categories: subjects.Count == 0 ? null : string.Join(", ", subjects),
            Rights: rights,
            Assets: assets);
    }

    /// <summary>
    /// Gutenberg's cover images follow a deterministic pattern per ebook id, so a
    /// search result can show real artwork on the first render instead of the
    /// 22x22 base64 icon the search feed itself carries.
    /// </summary>
    public static ProviderCover CoverFor(string id) =>
        new(new Uri($"{BaseUrl}/cache/epub/{id}/pg{id}.cover.medium.jpg"), "image/jpeg", ".jpg");

    private static IEnumerable<GutenbergAsset> ParseAssets(XElement entry)
    {
        var links = entry.Elements(Atom + "link")
            .Where(link => (string?)link.Attribute("rel") == OpdsAcquisition);

        foreach (var link in links)
        {
            var href = (string?)link.Attribute("href");
            var contentType = ((string?)link.Attribute("type"))?.Trim().ToLowerInvariant();

            // Only representations Nostos can actually open. PDF is accepted
            // only when Gutenberg's machine-readable OPDS entry advertises it;
            // no PDF URL is synthesized from the human-facing site.
            if (string.IsNullOrWhiteSpace(href)
                || contentType is not ("application/epub+zip" or "application/pdf"))
            {
                continue;
            }

            if (!Uri.TryCreate(new Uri(BaseUrl), href, out var url) || url.Scheme != Uri.UriSchemeHttps)
                continue;

            var assetId = AssetIdFromUrl(url, contentType);
            if (assetId is null)
                continue;

            long? size = long.TryParse(
                (string?)link.Attribute("length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

            var isPdf = contentType == "application/pdf";
            yield return new GutenbergAsset(
                Id: assetId,
                Label: (string?)link.Attribute("title") ?? (isPdf ? "PDF" : "EPUB"),
                SourceFormat: contentType,
                Url: url,
                FileExtension: isPdf ? ".pdf" : ".epub",
                OutputLabel: isPdf ? "PDF" : "EPUB",
                SizeBytes: size);
        }
    }

    /// <summary>
    /// Derives a stable provider asset id from Gutenberg's acquisition href.
    /// Existing EPUB ids are preserved (e.g. epub3-images). PDF variants use
    /// the same suffix normalization (pdf, pdf-images, ...), so multiple
    /// genuinely advertised PDF representations remain independently selectable.
    /// </summary>
    private static string? AssetIdFromUrl(Uri url, string contentType)
    {
        var file = Path.GetFileName(url.AbsolutePath);
        var separator = file.IndexOf('.');
        if (separator < 0 || separator == file.Length - 1)
            return null;

        var suffix = file[(separator + 1)..];
        var slug = suffix.Replace('.', '-').ToLowerInvariant();

        if (contentType == "application/epub+zip")
            return PreferredFormats.Contains(slug, StringComparer.Ordinal) ? slug : null;

        return slug == "pdf" || slug.StartsWith("pdf-", StringComparison.Ordinal)
            ? slug
            : null;
    }

    private static int PreferenceIndex(string assetId)
    {
        var index = Array.IndexOf(PreferredFormats, assetId);
        return index < 0 ? PreferredFormats.Length : index;
    }

    private static string? BookIdFromEntry(XElement entry)
    {
        var id = Value(entry, Atom + "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        // "https://www.gutenberg.org/ebooks/1342.opds" -> "1342".
        var marker = "/ebooks/";
        var start = id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        var rest = id[(start + marker.Length)..];
        var end = rest.IndexOf('.');
        var candidate = end < 0 ? rest : rest[..end];

        return candidate.Length is > 0 and <= 7 && candidate.All(char.IsAsciiDigit) ? candidate : null;
    }

    private static string? Value(XElement element, XName name) =>
        element.Element(name)?.Value.Trim() is { Length: > 0 } value ? value : null;

    /// <summary>
    /// The per-book feed's XHTML content carries a "Summary:" paragraph. It is
    /// picked out by its prefix and any failure to find one simply yields no
    /// description — the feed's prose is a presentation concern we do not want to
    /// depend on, so this never throws and never blocks an import.
    /// </summary>
    private static string? Summary(XElement entry)
    {
        var content = entry.Element(Atom + "content");
        if (content is null)
            return null;

        const string marker = "Summary:";
        var paragraphs = content.Descendants(Xhtml + "p").Select(p => p.Value.Trim());

        var summary = paragraphs.FirstOrDefault(text => text.StartsWith(marker, StringComparison.OrdinalIgnoreCase));
        if (summary is null)
            return null;

        var trimmed = summary[marker.Length..].Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// OPDS gives a language code; every other part of Nostos shows the full
    /// language name, so it is converted here rather than leaving a two-letter
    /// code in one corner of the library.
    /// </summary>
    private static string? LanguageName(string? code)
    {
        var value = code?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        if (value.Length > 3)
            return value;

        try
        {
            return CultureInfo.GetCultureInfo(value).EnglishName is { Length: > 0 } name ? name : value;
        }
        catch (CultureNotFoundException)
        {
            return value;
        }
    }
}
