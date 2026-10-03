using System.Globalization;
using System.Xml.Linq;
using Nostos.Backend.Providers.Contracts;

namespace Nostos.Backend.Providers.Wikisource;

/// <summary>One export-ready work as WS Export describes it.</summary>
internal sealed record WikisourceBook(
    string Page,
    string Title,
    string? Author,
    string? Description,
    string? Language,
    string? Publisher,
    string? PublishedDate,
    string? Categories,
    string? Rights,
    Uri? SourceUrl,
    IReadOnlyList<WikisourceAsset> Assets,
    ProviderCover? Cover);

/// <summary>One WS Export representation Nostos can acquire.</summary>
internal sealed record WikisourceAsset(
    string Id,
    string Label,
    string SourceFormat,
    Uri Url,
    string FileExtension,
    bool IsPreferred = false);

/// <summary>
/// Parses WS Export's OPDS/Atom representation for English Wikisource.
///
/// All WS Export details stop here. Downstream code sees only Nostos-owned
/// provider contracts.
/// </summary>
internal static class WikisourceCatalog
{
    public const string BaseUrl = "https://ws-export.wmcloud.org";
    public const string CatalogPath = "/opds/en/Ready_for_export.xml";
    public const string LanguageCode = "en";

    private const string AtomNamespace = "http://www.w3.org/2005/Atom";
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";
    private const string DcTermsNamespace = "http://purl.org/dc/terms/";
    private const string OpdsAcquisition = "http://opds-spec.org/acquisition";
    private const string OpdsImage = "http://opds-spec.org/image";

    private static readonly XNamespace Atom = AtomNamespace;
    private static readonly XNamespace Dc = DcNamespace;
    private static readonly XNamespace DcTerms = DcTermsNamespace;
    private static readonly XNamespace Xml = XNamespace.Xml;

    public static bool IsAtomDocument(XDocument document)
    {
        var root = document.Root?.Name;
        return root == Atom + "feed" || root == Atom + "entry";
    }

    public static IReadOnlyList<WikisourceBook> Parse(XDocument document)
    {
        var books = new List<WikisourceBook>();

        foreach (var entry in Entries(document))
        {
            var title = Text(entry.Element(Atom + "title"));
            if (title is null)
                continue;

            var page = PageFromEntry(entry) ?? title;
            if (string.IsNullOrWhiteSpace(page))
                continue;

            var authorNames = entry
                .Elements(Atom + "author")
                .Select(author => Text(author.Element(Atom + "name")))
                .Where(name => name is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var categories = entry
                .Elements(Atom + "category")
                .Select(category => ((string?)category.Attribute("term"))?.Trim())
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var epub = Link(entry, OpdsAcquisition, "application/epub+zip");
            var pdf = Link(entry, OpdsAcquisition, "application/pdf")
                // WS Export's documented export route supports format=pdf
                // (an alias for its PDF generator). This is a machine export
                // endpoint, not Wikisource HTML scraping, and resolves PDF
                // independently from the EPUB acquisition URL.
                ?? ExportUri(page, "pdf");
            var image = Link(entry, OpdsImage, requiredType: null);
            var source = TryAbsoluteUri(Text(entry.Element(Dc + "source")))
                ?? Link(entry, "alternate", "text/html")
                ?? Link(entry, "alternate", requiredType: null);

            var language = NormalizeLanguage(
                    Text(entry.Element(Dc + "language"))
                    ?? Text(entry.Element(DcTerms + "language"))
                    ?? (string?)entry.Attribute(Xml + "lang"))
                ?? "English";

            // Preserve the source value as-is. Rights on Wikisource can be a
            // licence URL, a licence label or a public-domain statement; Nostos
            // must not turn any of those into a broader legal claim.
            var rightsElement = entry.Element(Atom + "rights");
            var rights = rightsElement is null || string.IsNullOrWhiteSpace(rightsElement.Value)
                ? null
                : rightsElement.Value;

            books.Add(new WikisourceBook(
                Page: page,
                Title: title,
                Author: authorNames.Count == 0 ? null : string.Join(", ", authorNames),
                Description: Text(entry.Element(Atom + "summary")) ?? Text(entry.Element(Atom + "content")),
                Language: language,
                Publisher: Text(entry.Element(Dc + "publisher")),
                PublishedDate: Text(entry.Element(DcTerms + "issued")),
                Categories: categories.Count == 0 ? null : string.Join(", ", categories),
                Rights: rights,
                SourceUrl: source,
                Assets: BuildAssets(epub, pdf),
                Cover: image is null ? null : CoverFor(entry, image)));
        }

        return books;
    }

    public static WikisourceBook? ParseItem(XDocument document, string page)
    {
        var books = Parse(document);
        return books.FirstOrDefault(book =>
                   string.Equals(book.Page, page, StringComparison.OrdinalIgnoreCase))
               ?? books.FirstOrDefault();
    }

    private static IReadOnlyList<WikisourceAsset> BuildAssets(Uri? epub, Uri pdf)
    {
        var assets = new List<WikisourceAsset>();

        if (epub is not null)
        {
            assets.Add(new WikisourceAsset(
                Id: "epub",
                Label: "EPUB",
                SourceFormat: "application/epub+zip",
                Url: epub,
                FileExtension: ".epub",
                IsPreferred: true));
        }

        assets.Add(new WikisourceAsset(
            Id: "pdf",
            Label: "PDF",
            SourceFormat: "application/pdf",
            Url: pdf,
            FileExtension: ".pdf",
            IsPreferred: epub is null));

        return assets;
    }

    private static Uri ExportUri(string page, string format) =>
        new($"{BaseUrl}/?lang={LanguageCode}&format={format}&page={Uri.EscapeDataString(page)}");

    private static IEnumerable<XElement> Entries(XDocument document)
    {
        if (document.Root?.Name == Atom + "entry")
            return [document.Root];

        return document.Root?.Elements(Atom + "entry") ?? [];
    }

    private static string? PageFromEntry(XElement entry)
    {
        foreach (var link in entry.Elements(Atom + "link"))
        {
            var href = (string?)link.Attribute("href");
            if (TryAbsoluteUri(href) is not { } uri)
                continue;

            var page = QueryValue(uri, "page");
            if (!string.IsNullOrWhiteSpace(page))
                return page;
        }

        var id = Text(entry.Element(Atom + "id"));
        if (TryAbsoluteUri(id) is { } idUri
            && idUri.Host.EndsWith("wikisource.org", StringComparison.OrdinalIgnoreCase)
            && idUri.AbsolutePath.StartsWith("/wiki/", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(idUri.AbsolutePath["/wiki/".Length..]).Replace('_', ' ');
        }

        return null;
    }

    private static Uri? Link(XElement entry, string rel, string? requiredType)
    {
        var link = entry
            .Elements(Atom + "link")
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute("rel"), rel, StringComparison.OrdinalIgnoreCase)
                && (requiredType is null
                    || string.Equals((string?)element.Attribute("type"), requiredType, StringComparison.OrdinalIgnoreCase)));

        return TryAbsoluteUri((string?)link?.Attribute("href"));
    }

    private static ProviderCover CoverFor(XElement entry, Uri image)
    {
        var imageLink = entry
            .Elements(Atom + "link")
            .FirstOrDefault(element =>
                string.Equals((string?)element.Attribute("rel"), OpdsImage, StringComparison.OrdinalIgnoreCase));

        var contentType = ((string?)imageLink?.Attribute("type"))?.Trim();
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = ImageContentType(image);

        var extension = Path.GetExtension(image.AbsolutePath).ToLowerInvariant();
        if (extension is not ".jpg" and not ".jpeg" and not ".png" and not ".webp")
        {
            extension = contentType switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => ".jpg",
            };
        }

        return new ProviderCover(image, contentType, extension);
    }

    private static string ImageContentType(Uri image) =>
        Path.GetExtension(image.AbsolutePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg",
        };

    private static Uri? TryAbsoluteUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var absolute))
            return absolute;

        return Uri.TryCreate(new Uri(BaseUrl), value.Trim(), out var relative)
            ? relative
            : null;
    }

    private static string? QueryValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
            return null;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
                continue;

            var name = Uri.UnescapeDataString(parts[0].Replace("+", " ", StringComparison.Ordinal));
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                continue;

            return Uri.UnescapeDataString(parts[1].Replace("+", " ", StringComparison.Ordinal));
        }

        return null;
    }

    private static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var language = value.Trim();
        try
        {
            return CultureInfo.GetCultureInfo(language).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }

    private static string? Text(XElement? element)
    {
        if (element is null || string.IsNullOrWhiteSpace(element.Value))
            return null;

        return element.Value.Trim();
    }
}
