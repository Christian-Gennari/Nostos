using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Nostos.Backend.Services;
using Nostos.Product.Storage;

namespace Nostos.Product.BookText;

/// <summary>
/// The two ends of a passage, in the terms the passage viewer and the readers
/// use (issue #1014): <c>epub_cfi</c> point CFIs, or <c>pdf_page</c> pages.
/// </summary>
public sealed record BookPassageEnds(string AnchorKind, string Start, string End);

/// <summary>
/// Turns a quote found in a book's indexed text (a <c>quoteRef</c>) into a
/// passage's two ends, so a passage can be kept without the book open: by an
/// MCP client, which has no reader to mark the ends in.
/// </summary>
public interface IBookPassageAnchors
{
    /// <summary>The quote's ends in its own book, or null when they cannot be placed.</summary>
    Task<BookPassageEnds?> AnchorAsync(BookTextResolvedQuote quote, CancellationToken ct = default);
}

public sealed class BookPassageAnchors(
    IBookAssetStorage storage,
    IProductScratchStorage? scratchStorage = null) : IBookPassageAnchors
{
    private readonly IProductScratchStorage _scratch = scratchStorage ?? ProductScratchStorage.TempPath;

    public async Task<BookPassageEnds?> AnchorAsync(BookTextResolvedQuote quote, CancellationToken ct = default)
    {
        switch (quote.StartAt?.Locator, quote.EndAt?.Locator)
        {
            case (PdfBookTextSourceLocator first, PdfBookTextSourceLocator last):
                var from = first.PageIndex + 1;
                var to = Math.Max(from, last.PageIndex + 1);
                return new BookPassageEnds(
                    "pdf_page",
                    from.ToString(CultureInfo.InvariantCulture),
                    to.ToString(CultureInfo.InvariantCulture));

            case (EpubBookTextSourceLocator start, EpubBookTextSourceLocator end):
                return await EpubAsync(quote, start, quote.StartAt!.SourceOffset, end, quote.EndAt!.SourceOffset, ct);

            default:
                return null;
        }
    }

    private async Task<BookPassageEnds?> EpubAsync(
        BookTextResolvedQuote quote,
        EpubBookTextSourceLocator start,
        int startOffset,
        EpubBookTextSourceLocator end,
        int endOffset,
        CancellationToken ct)
    {
        await using var read = await storage.OpenBookFileAsync(quote.SourceBookId, range: null, ct);
        if (read is null)
            return null;

        // A ZIP needs to seek; the stored file may be a network stream. Staged
        // on the host's scratch root, not the process temp path (issue #1596).
        var temp = _scratch.CreateFilePath("nostos-passage", ".epub");
        try
        {
            await using (var staged = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                await read.Content.CopyToAsync(staged, ct);

            await using var file = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            var book = EpubCfiWriter.Open(archive);
            if (book is null)
                return null;

            var visible = VisibleChars(quote.Text);
            if (visible.Length == 0)
                return null;

            var first = book.Point(start, startOffset, visible, atEnd: false);
            var last = book.Point(end, endOffset, visible, atEnd: true);
            return first is null || last is null ? null : new BookPassageEnds("epub_cfi", first, last);
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or BookTextUnsupportedException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    internal static string VisibleChars(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
                builder.Append(ch);
        }
        return builder.ToString();
    }
}

/// <summary>
/// Writes epub.js point CFIs (<c>epubcfi(/6/14[id]!/4/2/1:23)</c>) for a place
/// in the book text index, the same form the reader stores for a passage it
/// marked. The index keeps, per block, its spine resource and its offset in
/// the resource's normalized text; normalization only collapses whitespace,
/// so the n-th visible character of a block is the n-th visible character of
/// its XHTML text nodes. The place found is checked against the quote's own
/// words; when the two disagree the quote's words are looked for instead.
/// </summary>
internal sealed class EpubCfiWriter
{
    /// <summary>How many visible characters of the quote must match at an end.</summary>
    private const int Signature = 40;

    /// <summary>The fewest visible characters a search for a cut-off signature may use.</summary>
    private const int MinSignature = 6;

    private readonly ZipArchive _archive;
    private readonly EpubPackage _package;
    private readonly int _spineNodeIndex;
    private readonly Dictionary<int, XDocument?> _documents = [];

    private EpubCfiWriter(ZipArchive archive, EpubPackage package, int spineNodeIndex)
    {
        _archive = archive;
        _package = package;
        _spineNodeIndex = spineNodeIndex;
    }

    public static EpubCfiWriter? Open(ZipArchive archive)
    {
        var package = EpubPackage.Read(archive);
        if (package.SpineNodeIndex is not { } spineNodeIndex)
            return null;

        return new EpubCfiWriter(archive, package, spineNodeIndex);
    }

    /// <summary>
    /// The point CFI before the visible character at <paramref name="offset"/>
    /// of the resource, or after it when <paramref name="atEnd"/>.
    /// </summary>
    public string? Point(EpubBookTextSourceLocator locator, int offset, string quoteVisible, bool atEnd)
    {
        var item = _package.FindByIndexedSpineIndex(locator.SpineIndex);
        if (item?.ResourceHref is null || !string.Equals(item.ResourceHref, locator.ResourceHref, StringComparison.Ordinal))
            return null;

        var document = Document(item.RawIndex, item.ResourceHref);
        var body = EpubContentHelper.FindBody(document);
        if (body is null)
            return null;

        var signature = atEnd
            ? quoteVisible[Math.Max(0, quoteVisible.Length - Signature)..]
            : quoteVisible[..Math.Min(Signature, quoteVisible.Length)];

        var place = ByOffset(body, offset);
        if (place is null || !Matches(body, place.Value, signature, atEnd))
            place = BySearch(body, signature, atEnd, place);
        if (place is null)
            return null;

        var (node, at) = place.Value;
        var spineStep = $"/{(_spineNodeIndex + 1) * 2}/{(item.RawIndex + 1) * 2}{Assertion(item.ItemId)}";
        return $"epubcfi({spineStep}!{Path(node)}:{(atEnd ? at + 1 : at).ToString(CultureInfo.InvariantCulture)})";
    }

    private XDocument? Document(int rawIndex, string href)
    {
        if (_documents.TryGetValue(rawIndex, out var cached))
            return cached;

        var entry = _package.FindResourceEntry(_archive, href);
        XDocument? document = null;
        if (entry is not null)
        {
            string text;
            using (var reader = new StreamReader(entry.Open(), detectEncodingFromByteOrderMarks: true))
                text = reader.ReadToEnd();
            document = ParseXhtml(text);
        }

        _documents[rawIndex] = document;
        return document;
    }

    internal static XDocument? ParseXhtml(string text) => EpubContentHelper.ParseXhtml(text);

    /// <summary>The text node and index of the visible character at a resource offset.</summary>
    private static (XText Node, int At)? ByOffset(XElement body, int offset)
    {
        foreach (var (block, text, start) in EpubContentHelper.EnumerateBlocks(body))
        {
            if (offset < start + text.Length)
            {
                if (offset < start)
                    return null;
                var within = offset - start;
                var before = 0;
                for (var i = 0; i < within; i++)
                {
                    if (!char.IsWhiteSpace(text[i]))
                        before++;
                }
                return Visible(TextNodes(block), before);
            }
        }

        return null;
    }

    /// <summary>The <paramref name="n"/>-th (0-based) visible character among <paramref name="nodes"/>.</summary>
    private static (XText Node, int At)? Visible(IEnumerable<XText> nodes, int n)
    {
        var seen = 0;
        foreach (var node in nodes)
        {
            var value = node.Value;
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsWhiteSpace(value[i]))
                    continue;
                if (seen++ == n)
                    return (node, i);
            }
        }
        return null;
    }

    private static IEnumerable<XText> TextNodes(XElement element) =>
        element.DescendantNodes().OfType<XText>();

    /// <summary>
    /// The visible characters of the body from a place: forward from it for a
    /// start, back to it (inclusive) for an end.
    /// </summary>
    private static bool Matches(XElement body, (XText Node, int At) place, string signature, bool atEnd)
    {
        var (chars, index) = Flatten(body, place);
        if (index < 0)
            return false;

        // A passage may run on into the next spine item (or begin in the one
        // before): only the part of the signature this resource holds is compared.
        if (atEnd)
        {
            var count = Math.Min(signature.Length, index + 1);
            for (var i = 0; i < count; i++)
            {
                if (chars[index - i].Ch != signature[^(i + 1)])
                    return false;
            }
        }
        else
        {
            var count = Math.Min(signature.Length, chars.Count - index);
            for (var i = 0; i < count; i++)
            {
                if (chars[index + i].Ch != signature[i])
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The quote's own words in the body: the occurrence nearest the place the
    /// index gave, or the first when it gave none.
    /// </summary>
    private static (XText Node, int At)? BySearch(XElement body, string signature, bool atEnd, (XText Node, int At)? near)
    {
        var (chars, nearIndex) = Flatten(body, near);
        var text = new string(chars.Select(c => c.Ch).ToArray());

        // The whole signature first; then, for a passage that runs on into the
        // next spine item (or begins in the one before), its start's opening
        // (its end's close) at the very end (start) of this resource.
        for (var length = signature.Length; length >= Math.Min(signature.Length, MinSignature); length--)
        {
            var words = atEnd ? signature[^length..] : signature[..length];
            var partial = length < signature.Length;
            int? best = null;
            for (var at = text.IndexOf(words, StringComparison.Ordinal); at >= 0; at = text.IndexOf(words, at + 1, StringComparison.Ordinal))
            {
                if (partial && (atEnd ? at != 0 : at + length != text.Length))
                    continue;
                var point = atEnd ? at + length - 1 : at;
                if (best is null || (nearIndex >= 0 && Math.Abs(point - nearIndex) < Math.Abs(best.Value - nearIndex)))
                    best = point;
            }

            if (best is { } found)
                return (chars[found].Node, chars[found].At);
        }

        return null;
    }

    private static (List<(char Ch, XText Node, int At)> Chars, int Index) Flatten(XElement body, (XText Node, int At)? place)
    {
        var chars = new List<(char, XText, int)>();
        var index = -1;
        foreach (var node in TextNodes(body))
        {
            if (node.Ancestors().Any(a => a.Name.LocalName is "script" or "style"))
                continue;
            var value = node.Value;
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsWhiteSpace(value[i]))
                    continue;
                if (place is { } p && ReferenceEquals(p.Node, node) && p.At == i)
                    index = chars.Count;
                chars.Add((value[i], node, i));
            }
        }
        return (chars, index);
    }

    /// <summary>
    /// The path epub.js writes for a text node: an element is its index among
    /// its parent's elements, (i + 1) * 2, with its id; a text node is its
    /// index among its parent's text nodes, 1 + 2i. The root element is left out.
    /// </summary>
    internal static string Path(XText node)
    {
        var steps = new List<string>();
        var parent = node.Parent!;
        var textIndex = parent.Nodes().Where(n => n is XText and not XCData).ToList().IndexOf(node);
        steps.Add((1 + 2 * Math.Max(0, textIndex)).ToString(CultureInfo.InvariantCulture));

        for (var element = parent; element.Parent is { } up; element = up)
        {
            var index = up.Elements().ToList().IndexOf(element);
            steps.Add(((index + 1) * 2).ToString(CultureInfo.InvariantCulture) + Assertion(element.Attribute("id")?.Value));
        }

        steps.Reverse();
        return "/" + string.Join('/', steps);
    }

    /// <summary>An id assertion, left out when the id would need escaping.</summary>
    private static string Assertion(string? id) =>
        string.IsNullOrEmpty(id) || id.IndexOfAny(['[', ']', '(', ')', ',', ';', '^', '=', ' ']) >= 0 ? string.Empty : $"[{id}]";
}
