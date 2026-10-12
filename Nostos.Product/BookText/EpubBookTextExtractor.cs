using System.IO.Compression;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace Nostos.Product.BookText;

/// <summary>A run of characters inside one extracted block's text.</summary>
public sealed record BookTextRange(int Start, int Length);

/// <summary>
/// An extracted document and, per block order, the parts of the block's text
/// that are reading aids printed beside the words rather than the words
/// themselves: the pronunciation glosses (ruby) of Japanese and Chinese books.
/// The document is exactly what <see cref="IBookTextExtractor.ExtractAsync"/>
/// returns; the ranges only say which characters nobody reads aloud.
/// </summary>
public sealed record BookTextAnnotatedDocument(
    BookTextExtractedDocument Document,
    IReadOnlyDictionary<int, IReadOnlyList<BookTextRange>> ReadingAids);

/// <summary>An extractor that can also report reading aids.</summary>
public interface IBookTextReadingAidSource
{
    Task<BookTextAnnotatedDocument> ExtractAnnotatedAsync(
        Stream source,
        CancellationToken ct = default);
}

public sealed class EpubBookTextExtractor : IBookTextExtractor, IBookTextReadingAidSource
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

    public Task<BookTextExtractedDocument> ExtractAsync(Stream source, CancellationToken ct = default) =>
        Task.FromResult(Extract(source, null, ct));

    public Task<BookTextAnnotatedDocument> ExtractAnnotatedAsync(
        Stream source,
        CancellationToken ct = default)
    {
        var readingAids = new Dictionary<int, IReadOnlyList<BookTextRange>>();
        var document = Extract(source, readingAids, ct);
        return Task.FromResult(new BookTextAnnotatedDocument(document, readingAids));
    }

    private BookTextExtractedDocument Extract(
        Stream source,
        Dictionary<int, IReadOnlyList<BookTextRange>>? readingAids,
        CancellationToken ct)
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

                if (readingAids is not null)
                {
                    var aids = ReadingAidRanges(node, text);
                    if (aids.Count > 0) readingAids[order] = aids;
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

        return new BookTextExtractedDocument(Format, blocks);
    }

    /// <summary>
    /// Where a block's ruby glosses sit in its extracted text. Each ruby is
    /// found in order by its own text, then its gloss and fallback brackets
    /// (rt, rp) inside it. A ruby that cannot be found is left unmarked: the
    /// text is then matched with the gloss in it, as before.
    /// </summary>
    private static List<BookTextRange> ReadingAidRanges(HtmlNode block, string text)
    {
        var ranges = new List<BookTextRange>();
        var cursor = 0;

        foreach (var ruby in block.Descendants("ruby"))
        {
            // A nested ruby is covered by the one that contains it.
            if (ruby.Ancestors("ruby").Any())
                continue;

            var whole = BookTextNormalization.Normalize(HtmlEntity.DeEntitize(ruby.InnerText));
            if (whole.Length == 0) continue;

            var at = text.IndexOf(whole, cursor, StringComparison.Ordinal);
            if (at < 0) continue;

            var inner = at;
            foreach (var aid in ruby.Descendants()
                .Where(node => node.Name is "rt" or "rp" or "rtc"))
            {
                if (aid.Ancestors().TakeWhile(node => !ReferenceEquals(node, ruby))
                    .Any(node => node.Name is "rt" or "rp" or "rtc"))
                    continue;

                var gloss = BookTextNormalization.Normalize(HtmlEntity.DeEntitize(aid.InnerText));
                if (gloss.Length == 0) continue;

                var found = text.IndexOf(gloss, inner, StringComparison.Ordinal);
                if (found < 0 || found + gloss.Length > at + whole.Length) continue;

                ranges.Add(new BookTextRange(found, gloss.Length));
                inner = found + gloss.Length;
            }

            cursor = at + whole.Length;
        }

        return ranges;
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
