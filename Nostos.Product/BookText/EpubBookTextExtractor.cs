using System.IO.Compression;
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
        var package = EpubPackage.Read(archive);

        var blocks = new List<BookTextArtifactBlock>();
        var order = 0;
        foreach (var item in package.IndexedSpineItems)
        {
            ct.ThrowIfCancellationRequested();
            if (item.ResourceHref is null || item.IndexedIndex is not { } spineIndex)
                continue;

            var entry = package.FindResourceEntry(archive, item.ResourceHref);
            if (entry is null) continue;

            var html = EpubContentHelper.CreateHtmlDocument();
            using (var reader = new StreamReader(entry.Open(), detectEncodingFromByteOrderMarks: true))
                html.Load(reader);

            var body = EpubContentHelper.FindBody(html);
            var headingPath = new string?[6];

            foreach (var (node, text, resourceOffset) in EpubContentHelper.EnumerateBlocks(body, ct))
            {
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
                                ResourceHref: item.ResourceHref,
                                Cfi: null,
                                StartTextOffset: resourceOffset,
                                EndTextOffset: resourceOffset + text.Length)),
                    ]));
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

    private static bool TryHeadingLevel(string name, out int level)
    {
        level = 0;
        return name.Length == 2
            && (name[0] == 'h' || name[0] == 'H')
            && int.TryParse(name[1].ToString(), out level)
            && level is >= 1 and <= 6;
    }
}
