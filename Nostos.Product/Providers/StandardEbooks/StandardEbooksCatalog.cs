using System.Globalization;
using System.Xml.Linq;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.StandardEbooks;

internal sealed record StandardEbooksAsset(
    string Id,
    string Label,
    string SourceFormat,
    Uri Url,
    long? SizeBytes,
    bool IsPreferred = false);

internal sealed record StandardEbooksBook(
    string ExternalId,
    string Identifier,
    string Title,
    string? Author,
    string? Description,
    string? Language,
    string? Publisher,
    string? PublishedDate,
    string? Categories,
    string? Rights,
    Uri? ItemUrl,
    ProviderCover? Cover,
    IReadOnlyList<StandardEbooksAsset> Assets);

/// <summary>
/// Parses Standard Ebooks' official OPDS 1.x acquisition feed.
///
/// Standard Ebooks also supports OPDS 2.0. Nostos intentionally requests the
/// Atom representation because it includes the source's per-item <rights>
/// statement, which OPDS 2.0 currently omits. Both representations come from
/// the same official feed endpoint; no human-facing page is scraped.
/// </summary>
internal static class StandardEbooksCatalog
{
    public const string BaseUrl = "https://standardebooks.org";

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    private const string AcquisitionRel = "http://opds-spec.org/acquisition/open-access";
    private const string ImageRel = "http://opds-spec.org/image";
    private const string EbookPathPrefix = "/ebooks/";

    public static bool IsFeed(XDocument document) =>
        document.Root?.Name == Atom + "feed";

    public static IReadOnlyList<StandardEbooksBook> Parse(XDocument feed)
    {
        if (!IsFeed(feed))
            return [];

        var books = new List<StandardEbooksBook>();

        foreach (var entry in feed.Root!.Elements(Atom + "entry"))
        {
            var identifier = Value(entry, Dc + "identifier") ?? Value(entry, Atom + "id");
            var externalId = ExternalIdFromIdentifier(identifier);
            var title = Value(entry, Atom + "title");

            if (externalId is null || string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(title))
                continue;

            var authors = entry.Elements(Atom + "author")
                .Select(author => Value(author, Atom + "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var categories = entry.Elements(Atom + "category")
                .Select(category => ((string?)category.Attribute("term"))?.Trim())
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var assets = ParseAssets(entry)
                .GroupBy(asset => asset.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(asset => AssetPreference(asset.Id))
                .ThenBy(asset => asset.Label, StringComparer.OrdinalIgnoreCase)
                .Select((asset, index) => asset with { IsPreferred = index == 0 })
                .ToList();

            var alternate = entry.Elements(Atom + "link")
                .FirstOrDefault(link => string.Equals(
                    ((string?)link.Attribute("rel"))?.Trim(),
                    "alternate",
                    StringComparison.OrdinalIgnoreCase));

            var itemUrl = AbsoluteHttpsUrl((string?)alternate?.Attribute("href"))
                ?? AbsoluteHttpsUrl(identifier);

            var coverLink = entry.Elements(Atom + "link")
                .FirstOrDefault(link => string.Equals(
                    ((string?)link.Attribute("rel"))?.Trim(),
                    ImageRel,
                    StringComparison.OrdinalIgnoreCase));

            ProviderCover? cover = null;
            if (coverLink is not null
                && AbsoluteHttpsUrl((string?)coverLink.Attribute("href")) is { } coverUrl)
            {
                cover = new ProviderCover(
                    coverUrl,
                    NormalizeContentType((string?)coverLink.Attribute("type")) ?? "image/jpeg",
                    ".jpg");
            }

            books.Add(new StandardEbooksBook(
                ExternalId: externalId,
                Identifier: identifier,
                Title: title,
                Author: authors.Count == 0 ? null : string.Join(", ", authors),
                Description: Value(entry, Atom + "summary"),
                Language: NormalizeLanguage(Value(entry, Dc + "language")),
                Publisher: Value(entry, Dc + "publisher"),
                PublishedDate: Value(entry, Atom + "published") ?? Value(entry, Dc + "issued"),
                Categories: categories.Count == 0 ? null : string.Join(", ", categories),
                Rights: Value(entry, Atom + "rights"),
                ItemUrl: itemUrl,
                Cover: cover,
                Assets: assets));
        }

        return books;
    }

    public static StandardEbooksBook? FindByExternalId(XDocument feed, string externalId) =>
        Parse(feed).FirstOrDefault(book =>
            string.Equals(book.ExternalId, externalId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Converts Standard Ebooks' canonical identifier path into a route-safe,
    /// durable provider id:
    ///
    /// /ebooks/jane-austen/pride-and-prejudice
    ///     -> jane-austen~pride-and-prejudice
    ///
    /// '~' preserves path-segment boundaries while remaining safe as one API
    /// route segment. It also lets item re-resolution search by author + title
    /// without depending on a human page or on a second identity system.
    /// </summary>
    public static string? ExternalIdFromIdentifier(string? identifier)
    {
        if (!Uri.TryCreate(identifier, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !IsStandardEbooksHost(uri.Host)
            || !uri.AbsolutePath.StartsWith(EbookPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = Uri.UnescapeDataString(uri.AbsolutePath[EbookPathPrefix.Length..]).Trim('/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length < 2 || segments.Any(segment => !IsSlugSegment(segment)))
            return null;

        return string.Join('~', segments);
    }

    public static bool IsValidExternalId(string? externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 320)
            return false;

        var segments = externalId.Split('~', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length >= 2 && segments.All(IsSlugSegment);
    }

    /// <summary>
    /// The feed has no per-publication OPDS endpoint. Re-resolution therefore
    /// uses the feed's official search template and then exact-matches the
    /// canonical identifier. Author + title are enough to narrow the result set;
    /// translator/editor path suffixes are deliberately not required to match
    /// the source's full-text search index.
    /// </summary>
    public static string? SearchTermsForExternalId(string externalId)
    {
        if (!IsValidExternalId(externalId))
            return null;

        var segments = externalId.Split('~');
        return string.Join(' ', segments
            .Take(2)
            .Select(segment => segment.Replace('-', ' ').Replace('_', ' ')));
    }

    private static IEnumerable<StandardEbooksAsset> ParseAssets(XElement entry)
    {
        foreach (var link in entry.Elements(Atom + "link"))
        {
            var rel = ((string?)link.Attribute("rel"))?.Trim();
            var type = NormalizeContentType((string?)link.Attribute("type"));

            if (!string.Equals(rel, AcquisitionRel, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(type, "application/epub+zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (AbsoluteHttpsUrl((string?)link.Attribute("href")) is not { } url)
                continue;

            var title = ((string?)link.Attribute("title"))?.Trim();
            var id = AssetId(title);
            var label = string.IsNullOrWhiteSpace(title) ? "EPUB" : title;

            long? size = long.TryParse(
                (string?)link.Attribute("length"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : null;

            yield return new StandardEbooksAsset(
                Id: id,
                Label: label,
                SourceFormat: "application/epub+zip",
                Url: url,
                SizeBytes: size);
        }
    }

    private static string AssetId(string? title)
    {
        var value = title?.Trim() ?? string.Empty;

        if (value.Contains("advanced", StringComparison.OrdinalIgnoreCase))
            return "epub-advanced";

        if (value.Contains("recommended", StringComparison.OrdinalIgnoreCase)
            || value.Contains("compatible", StringComparison.OrdinalIgnoreCase))
        {
            return "epub-compatible";
        }

        return "epub";
    }

    private static int AssetPreference(string id) => id switch
    {
        "epub-compatible" => 0,
        "epub-advanced" => 1,
        _ => 2,
    };

    private static Uri? AbsoluteHttpsUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !IsStandardEbooksHost(uri.Host))
        {
            return null;
        }

        return uri;
    }

    private static bool IsStandardEbooksHost(string host) =>
        string.Equals(host, "standardebooks.org", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".standardebooks.org", StringComparison.OrdinalIgnoreCase);

    private static bool IsSlugSegment(string segment) =>
        segment.Length > 0
        && segment.All(ch =>
            char.IsAsciiLetterOrDigit(ch)
            || ch is '-' or '_');

    private static string? NormalizeContentType(string? value)
    {
        var type = value?.Split(';', 2, StringSplitOptions.TrimEntries)[0].ToLowerInvariant();
        return string.IsNullOrWhiteSpace(type) ? null : type;
    }

    private static string? NormalizeLanguage(string? language)
    {
        var value = language?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return CultureInfo.GetCultureInfo(value).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return value;
        }
    }

    private static string? Value(XElement element, XName name) =>
        element.Element(name)?.Value.Trim() is { Length: > 0 } value ? value : null;
}
