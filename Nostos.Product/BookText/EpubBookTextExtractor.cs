using System.IO.Compression;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace Nostos.Product.BookText;

public sealed class EpubBookTextExtractor : IBookTextExtractor
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "h1", "h2", "h3", "h4", "h5", "h6",
        "p", "li", "blockquote", "pre", "figcaption", "dt", "dd", "aside",
    };

    private static readonly HashSet<string> IgnoredTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "nav", "svg", "math",
    };

    public BookTextSourceFormat Format => BookTextSourceFormat.Epub;

    public Task<BookTextExtractedDocument> ExtractAsync(Stream source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (source.CanSeek) source.Position = 0;

        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var packagePath = ReadPackagePath(archive);
        var package = XDocument.Parse(ReadEntry(archive, packagePath));
        var ns = package.Root?.Name.Namespace ?? XNamespace.None;
        var packageDir = ZipDirectory(packagePath);

        var manifest = package
            .Descendants(ns + "item")
            .Where(item => item.Attribute("id") is not null && item.Attribute("href") is not null)
            .ToDictionary(
                item => item.Attribute("id")!.Value,
                item => item.Attribute("href")!.Value,
                StringComparer.Ordinal);

        var spineIds = package
            .Descendants(ns + "itemref")
            .Select(item => item.Attribute("idref")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();

        var blocks = new List<BookTextArtifactBlock>();
        var order = 0;
        for (var spineIndex = 0; spineIndex < spineIds.Count; spineIndex++)
        {
            ct.ThrowIfCancellationRequested();
            if (!manifest.TryGetValue(spineIds[spineIndex], out var href))
                continue;

            var resourcePath = ResolvePath(packageDir, href);
            var entry = FindEntry(archive, resourcePath);
            if (entry is null) continue;

            var html = new HtmlDocument
            {
                OptionFixNestedTags = true,
                OptionAutoCloseOnEnd = true,
            };
            using (var reader = new StreamReader(entry.Open(), detectEncodingFromByteOrderMarks: true))
                html.Load(reader);

            var body = html.DocumentNode.SelectSingleNode("//body") ?? html.DocumentNode;
            var headingPath = new string?[6];
            var resourceOffset = 0;

            foreach (var node in body.Descendants())
            {
                ct.ThrowIfCancellationRequested();
                if (!BlockTags.Contains(node.Name) || HasIgnoredAncestor(node))
                    continue;

                if (node.Ancestors().Any(ancestor =>
                    !ReferenceEquals(ancestor, body) && BlockTags.Contains(ancestor.Name)))
                    continue;

                var text = BookTextNormalization.Normalize(HtmlEntity.DeEntitize(node.InnerText));
                if (text.Length == 0) continue;

                if (TryHeadingLevel(node.Name, out var level))
                {
                    headingPath[level - 1] = text;
                    for (var i = level; i < headingPath.Length; i++) headingPath[i] = null;
                }

                var headings = headingPath.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
                blocks.Add(new BookTextArtifactBlock(
                    Order: order++,
                    Text: text,
                    HeadingPath: headings,
                    SourceSegments:
                    [
                        new BookTextSourceSegment(
                            TextStart: 0,
                            TextLength: text.Length,
                            Locator: new EpubBookTextSourceLocator(
                                SpineIndex: spineIndex,
                                ResourceHref: href,
                                Cfi: null,
                                StartTextOffset: resourceOffset,
                                EndTextOffset: resourceOffset + text.Length)),
                    ]));

                resourceOffset += text.Length + 1;
            }
        }

        return Task.FromResult(new BookTextExtractedDocument(Format, blocks));
    }

    private static bool HasIgnoredAncestor(HtmlNode node) =>
        node.AncestorsAndSelf().Any(ancestor => IgnoredTags.Contains(ancestor.Name));

    private static bool TryHeadingLevel(string name, out int level)
    {
        level = 0;
        return name.Length == 2
            && (name[0] == 'h' || name[0] == 'H')
            && int.TryParse(name[1].ToString(), out level)
            && level is >= 1 and <= 6;
    }

    private static string ReadPackagePath(ZipArchive archive)
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

    private static string ReadEntry(ZipArchive archive, string path)
    {
        var entry = FindEntry(archive, NormalizeZipPath(path))
            ?? throw new BookTextUnsupportedException(
                "book_text_epub_entry_missing",
                "The EPUB is missing a required package resource.");
        using var reader = new StreamReader(entry.Open(), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path) =>
        archive.Entries.FirstOrDefault(entry =>
            string.Equals(NormalizeZipPath(entry.FullName), NormalizeZipPath(path), StringComparison.OrdinalIgnoreCase));

    private static string ResolvePath(string directory, string href)
    {
        var noFragment = href.Split('#')[0].Split('?')[0];
        var decoded = Uri.UnescapeDataString(noFragment);
        return NormalizeZipPath(string.IsNullOrEmpty(directory)
            ? decoded
            : $"{directory}/{decoded}");
    }

    private static string ZipDirectory(string path)
    {
        var normalized = NormalizeZipPath(path);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    private static string NormalizeZipPath(string path)
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
