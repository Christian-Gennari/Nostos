using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace Nostos.Product.BookText;

internal sealed record EpubSpineItem(
    int RawIndex,
    int? IndexedIndex,
    string IdRef,
    string? ItemId,
    string? ResourceHref);

internal readonly record struct EpubExtractedBlock<TNode>(
    TNode Node,
    string Text,
    int ResourceOffset);

internal sealed class EpubPackage
{
    private readonly IReadOnlyList<EpubSpineItem> _indexedSpineItems;

    private EpubPackage(
        string packagePath,
        string packageDirectory,
        int? spineNodeIndex,
        IReadOnlyDictionary<string, string> manifest,
        IReadOnlyList<EpubSpineItem> rawSpineItems,
        IReadOnlyList<EpubSpineItem> indexedSpineItems)
    {
        PackagePath = packagePath;
        PackageDirectory = packageDirectory;
        SpineNodeIndex = spineNodeIndex;
        Manifest = manifest;
        RawSpineItems = rawSpineItems;
        _indexedSpineItems = indexedSpineItems;
    }

    public string PackagePath { get; }
    public string PackageDirectory { get; }
    public int? SpineNodeIndex { get; }
    public IReadOnlyDictionary<string, string> Manifest { get; }
    public IReadOnlyList<EpubSpineItem> RawSpineItems { get; }
    public IReadOnlyList<EpubSpineItem> IndexedSpineItems => _indexedSpineItems;

    public EpubSpineItem? FindByIndexedSpineIndex(int indexedSpineIndex) =>
        indexedSpineIndex >= 0 && indexedSpineIndex < _indexedSpineItems.Count
            ? _indexedSpineItems[indexedSpineIndex]
            : null;

    public ZipArchiveEntry? FindResourceEntry(ZipArchive archive, string resourceHref) =>
        EpubContentHelper.FindEntry(
            archive,
            EpubContentHelper.ResolvePath(PackageDirectory, resourceHref));

    public static EpubPackage Read(ZipArchive archive)
    {
        var packagePath = EpubContentHelper.ReadPackagePath(archive);
        var package = XDocument.Parse(EpubContentHelper.ReadEntry(archive, packagePath));
        var packageDirectory = EpubContentHelper.ZipDirectory(packagePath);
        var root = package.Root;

        if (root is null)
        {
            return new EpubPackage(
                packagePath,
                packageDirectory,
                spineNodeIndex: null,
                new Dictionary<string, string>(StringComparer.Ordinal),
                [],
                []);
        }

        var manifest = root.Descendants()
            .Where(item => item.Name.LocalName == "item"
                && item.Attribute("id") is not null
                && item.Attribute("href") is not null)
            .GroupBy(item => item.Attribute("id")!.Value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().Attribute("href")!.Value,
                StringComparer.Ordinal);

        var spine = root.Elements().FirstOrDefault(element => element.Name.LocalName == "spine");
        var spineNodeIndex = spine is null
            ? (int?)null
            : root.Elements().ToList().IndexOf(spine);

        var rawSpineItems = new List<EpubSpineItem>();
        var indexedSpineItems = new List<EpubSpineItem>();
        if (spine is not null)
        {
            var rawIndex = 0;
            var indexedIndex = 0;
            foreach (var itemRef in spine.Descendants().Where(item => item.Name.LocalName == "itemref"))
            {
                var idRef = itemRef.Attribute("idref")?.Value ?? string.Empty;
                var itemId = itemRef.Attribute("id")?.Value;
                int? assignedIndexed = string.IsNullOrWhiteSpace(idRef) ? null : indexedIndex++;
                string? href = !string.IsNullOrWhiteSpace(idRef) && manifest.TryGetValue(idRef, out var resolved)
                    ? resolved
                    : null;

                var entry = new EpubSpineItem(rawIndex++, assignedIndexed, idRef, itemId, href);
                rawSpineItems.Add(entry);
                if (assignedIndexed is not null)
                    indexedSpineItems.Add(entry);
            }
        }

        return new EpubPackage(
            packagePath,
            packageDirectory,
            spineNodeIndex,
            manifest,
            rawSpineItems,
            indexedSpineItems);
    }
}

/// <summary>
/// Canonical EPUB package navigation and XHTML block-filtering rules shared by
/// <see cref="EpubBookTextExtractor"/> and <see cref="EpubCfiWriter"/> so block
/// ordering and resource offsets stay byte-identical.
/// </summary>
internal static class EpubContentHelper
{
    internal static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "h1", "h2", "h3", "h4", "h5", "h6",
        "p", "li", "blockquote", "pre", "figcaption", "dt", "dd", "aside",
    };

    internal static readonly HashSet<string> IgnoredTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "nav", "svg", "math",
    };

    internal static bool IsBlockTag(string tagName) => BlockTags.Contains(tagName);

    internal static bool IsIgnoredTag(string tagName) => IgnoredTags.Contains(tagName);

    internal static bool IsExtractableBlock<TNode>(
        TNode node,
        TNode body,
        Func<TNode, string> getTagName,
        Func<TNode, IEnumerable<TNode>> getAncestors)
        where TNode : class
    {
        var tagName = getTagName(node);
        if (!IsBlockTag(tagName) || IsIgnoredTag(tagName))
            return false;

        var withinBody = true;
        foreach (var ancestor in getAncestors(node))
        {
            var ancestorTag = getTagName(ancestor);
            if (IsIgnoredTag(ancestorTag))
                return false;

            if (ReferenceEquals(ancestor, body))
            {
                withinBody = false;
                continue;
            }

            if (withinBody && IsBlockTag(ancestorTag))
                return false;
        }

        return true;
    }

    internal static IEnumerable<EpubExtractedBlock<TNode>> EnumerateBlocks<TNode>(
        TNode body,
        Func<TNode, IEnumerable<TNode>> getDescendants,
        Func<TNode, IEnumerable<TNode>> getAncestors,
        Func<TNode, string> getTagName,
        Func<TNode, string> getRawText,
        CancellationToken ct = default)
        where TNode : class
    {
        var resourceOffset = 0;
        foreach (var node in getDescendants(body))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsExtractableBlock(node, body, getTagName, getAncestors))
                continue;

            var text = BookTextNormalization.Normalize(getRawText(node));
            if (text.Length == 0)
                continue;

            yield return new EpubExtractedBlock<TNode>(node, text, resourceOffset);
            resourceOffset += text.Length + 1;
        }
    }

    internal static IEnumerable<EpubExtractedBlock<HtmlNode>> EnumerateBlocks(
        HtmlNode body,
        CancellationToken ct = default) =>
        EnumerateBlocks(
            body,
            static node => node.Descendants(),
            static node => node.Ancestors(),
            static node => node.Name,
            static node => HtmlEntity.DeEntitize(node.InnerText),
            ct);

    internal static IEnumerable<EpubExtractedBlock<XElement>> EnumerateBlocks(
        XElement body,
        CancellationToken ct = default) =>
        EnumerateBlocks(
            body,
            static element => element.Descendants(),
            static element => element.Ancestors(),
            static element => element.Name.LocalName,
            static element => element.Value,
            ct);

    internal static HtmlNode FindBody(HtmlDocument html) =>
        html.DocumentNode.Descendants()
            .FirstOrDefault(node => string.Equals(node.Name, "body", StringComparison.OrdinalIgnoreCase))
        ?? html.DocumentNode;

    internal static XElement? FindBody(XDocument? document) =>
        document?.Root?.DescendantsAndSelf()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "body", StringComparison.OrdinalIgnoreCase))
        ?? document?.Root;

    internal static HtmlDocument CreateHtmlDocument(bool outputAsXml = false) =>
        new()
        {
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true,
            OptionOutputAsXml = outputAsXml,
        };

    /// <summary>
    /// Parses an EPUB content resource as XML with whitespace nodes preserved,
    /// falling back to the canonical HTML cleanup parser when markup is not
    /// well-formed XML.
    /// </summary>
    internal static XDocument? ParseXhtml(string text)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            var html = CreateHtmlDocument(outputAsXml: true);
            html.LoadHtml(text);
            using var writer = new StringWriter();
            html.Save(writer);
            try
            {
                using var reader = XmlReader.Create(new StringReader(writer.ToString()), settings);
                return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return null;
            }
        }
    }

    internal static string ReadPackagePath(ZipArchive archive)
    {
        var container = XDocument.Parse(ReadEntry(archive, "META-INF/container.xml"));
        var rootfile = container.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile");
        var path = rootfile?.Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(path))
            throw new BookTextUnsupportedException(
                "book_text_epub_package_missing",
                "The EPUB package document could not be located.");
        return NormalizeZipPath(path);
    }

    internal static string ReadEntry(ZipArchive archive, string path)
    {
        var entry = FindEntry(archive, NormalizeZipPath(path))
            ?? throw new BookTextUnsupportedException(
                "book_text_epub_entry_missing",
                "The EPUB is missing a required package resource.");
        using var reader = new StreamReader(entry.Open(), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static ZipArchiveEntry? FindEntry(ZipArchive archive, string path) =>
        archive.Entries.FirstOrDefault(entry =>
            string.Equals(NormalizeZipPath(entry.FullName), NormalizeZipPath(path), StringComparison.OrdinalIgnoreCase));

    internal static string ResolvePath(string directory, string href)
    {
        var noFragment = href.Split('#')[0].Split('?')[0];
        var decoded = Uri.UnescapeDataString(noFragment);
        return NormalizeZipPath(string.IsNullOrEmpty(directory)
            ? decoded
            : $"{directory}/{decoded}");
    }

    internal static string ZipDirectory(string path)
    {
        var normalized = NormalizeZipPath(path);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    internal static string NormalizeZipPath(string path)
    {
        var parts = new List<string>();
        foreach (var raw in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".") continue;
            if (raw == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(raw);
        }
        return string.Join('/', parts);
    }
}
