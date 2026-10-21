using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace Nostos.Product.BookText;

public sealed class PdfBookTextExtractor : IBookTextExtractor
{
    /// <summary>
    /// How many pages are read from one open document before it is opened
    /// again. PdfPig keeps every font it has parsed until the document is
    /// closed, and a page-level font re-parses an embedded program even when
    /// every page shares it: a scanned book with one OCR font per page kept a
    /// copy per page and ran the extraction child out of memory. Reopening
    /// bounds that to this many pages' worth.
    /// </summary>
    internal const int PagesPerOpen = 16;

    /// <summary>Test seam: called after each page, with its number, while its document is still open.</summary>
    internal Action<int>? AfterPage { get; init; }

    public BookTextSourceFormat Format => BookTextSourceFormat.Pdf;

    public Task<BookTextExtractedDocument> ExtractAsync(Stream source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!source.CanSeek)
        {
            var buffered = new MemoryStream();
            source.CopyTo(buffered);
            source = buffered;
        }

        var blocks = new List<BookTextArtifactBlock>();
        var order = 0;
        IReadOnlyDictionary<int, string> pageLabels;
        int pageCount;

        source.Position = 0;
        using (var first = PdfDocument.Open(source))
        {
            pageLabels = PdfPageLabelResolver.Read(first);
            pageCount = first.NumberOfPages;
        }

        for (var start = 1; start <= pageCount; start += PagesPerOpen)
        {
            source.Position = 0;
            using var document = PdfDocument.Open(source);
            var end = Math.Min(pageCount, start + PagesPerOpen - 1);
            for (var number = start; number <= end; number++)
            {
                ct.ThrowIfCancellationRequested();
                var page = document.GetPage(number);
                // Placed words, not `page.Text`: see PdfReadingOrder for why the raw
                // glyph stream is not usable text.
                var text = BookTextNormalization.Normalize(
                    PdfReadingOrder.Compose(
                        page.GetWords()
                            .Select(word => new PlacedWord(
                                word.Text,
                                word.BoundingBox.Left,
                                word.BoundingBox.Right,
                                word.BoundingBox.Bottom,
                                word.BoundingBox.Height))
                            .ToList(),
                        page.CropBox.Bounds.Left,
                        page.CropBox.Bounds.Right));
                AfterPage?.Invoke(number);
                if (text.Length == 0)
                    continue;

                blocks.Add(new BookTextArtifactBlock(
                    Order: order++,
                    Text: text,
                    HeadingPath: [],
                    SourceSegments:
                    [
                        new BookTextSourceSegment(
                            TextStart: 0,
                            TextLength: text.Length,
                            Locator: new PdfBookTextSourceLocator(
                                PageIndex: Math.Max(0, page.Number - 1),
                                PageLabel: pageLabels.GetValueOrDefault(
                                    Math.Max(0, page.Number - 1)),
                                StartTextOffset: 0,
                                EndTextOffset: text.Length)),
                    ]));
            }
        }

        return Task.FromResult(new BookTextExtractedDocument(Format, blocks));
    }
}


internal static class PdfPageLabelResolver
{
    private sealed record Range(
        int StartPageIndex,
        string? Prefix,
        string? Style,
        int StartNumber);

    public static IReadOnlyDictionary<int, string> Read(PdfDocument document)
    {
        try
        {
            var catalog = document.Structure.Catalog.CatalogDictionary;
            if (!catalog.TryGet(NameToken.Create("PageLabels"), out IToken pageLabelsToken))
                return new Dictionary<int, string>();

            var pageLabels = ResolveDictionary(document, pageLabelsToken);
            if (pageLabels is null)
                return new Dictionary<int, string>();

            var ranges = new List<Range>();
            ReadNumberTree(document, pageLabels, ranges);
            if (ranges.Count == 0)
                return new Dictionary<int, string>();

            ranges.Sort((left, right) => left.StartPageIndex.CompareTo(right.StartPageIndex));
            var result = new Dictionary<int, string>();
            Range? current = null;
            var rangeIndex = 0;

            for (var pageIndex = 0; pageIndex < document.NumberOfPages; pageIndex++)
            {
                while (rangeIndex < ranges.Count
                    && ranges[rangeIndex].StartPageIndex <= pageIndex)
                {
                    current = ranges[rangeIndex++];
                }

                if (current is null)
                    continue;

                var number = current.StartNumber + pageIndex - current.StartPageIndex;
                result[pageIndex] =
                    (current.Prefix ?? string.Empty)
                    + FormatNumber(number, current.Style);
            }

            return result;
        }
        catch
        {
            // Page labels are useful presentation metadata, not a reason to
            // reject an otherwise extractable source. Physical page index
            // remains the authoritative navigation locator.
            return new Dictionary<int, string>();
        }
    }

    private static void ReadNumberTree(
        PdfDocument document,
        DictionaryToken node,
        List<Range> ranges)
    {
        if (node.TryGet(NameToken.Create("Nums"), out IToken numsToken)
            && Resolve(document, numsToken) is ArrayToken nums)
        {
            for (var i = 0; i + 1 < nums.Data.Count; i += 2)
            {
                var startToken = Resolve(document, nums.Data[i]) as NumericToken;
                var label = ResolveDictionary(document, nums.Data[i + 1]);
                if (startToken is null || label is null)
                    continue;

                ranges.Add(new Range(
                    StartPageIndex: startToken.Int,
                    Prefix: ReadText(document, label, "P"),
                    Style: ReadName(document, label, "S"),
                    StartNumber: ReadNumber(document, label, "St") ?? 1));
            }
        }

        if (node.TryGet(NameToken.Create("Kids"), out IToken kidsToken)
            && Resolve(document, kidsToken) is ArrayToken kids)
        {
            foreach (var child in kids.Data)
            {
                var childDictionary = ResolveDictionary(document, child);
                if (childDictionary is not null)
                    ReadNumberTree(document, childDictionary, ranges);
            }
        }
    }

    private static DictionaryToken? ResolveDictionary(PdfDocument document, IToken token) =>
        Resolve(document, token) as DictionaryToken;

    private static IToken? Resolve(PdfDocument document, IToken token)
    {
        if (token is not IndirectReferenceToken reference)
            return token;

        return document.Structure.GetObject(reference.Data).Data;
    }

    private static string? ReadText(
        PdfDocument document,
        DictionaryToken dictionary,
        string key)
    {
        if (!dictionary.TryGet(NameToken.Create(key), out IToken token))
            return null;

        return Resolve(document, token) switch
        {
            StringToken value => value.Data,
            HexToken value => value.Data,
            _ => null,
        };
    }

    private static string? ReadName(
        PdfDocument document,
        DictionaryToken dictionary,
        string key)
    {
        if (!dictionary.TryGet(NameToken.Create(key), out IToken token))
            return null;

        return Resolve(document, token) is NameToken value ? value.Data : null;
    }

    private static int? ReadNumber(
        PdfDocument document,
        DictionaryToken dictionary,
        string key)
    {
        if (!dictionary.TryGet(NameToken.Create(key), out IToken token))
            return null;

        return Resolve(document, token) is NumericToken value ? value.Int : null;
    }

    private static string FormatNumber(int number, string? style)
    {
        if (number <= 0)
            return string.Empty;

        return style switch
        {
            "D" => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "R" => Roman(number).ToUpperInvariant(),
            "r" => Roman(number),
            "A" => Alpha(number).ToUpperInvariant(),
            "a" => Alpha(number),
            _ => string.Empty,
        };
    }

    private static string Roman(int number)
    {
        var values = new (int Value, string Symbol)[]
        {
            (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"),
            (100, "c"), (90, "xc"), (50, "l"), (40, "xl"),
            (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i"),
        };

        var remaining = number;
        var builder = new System.Text.StringBuilder();
        foreach (var (value, symbol) in values)
        {
            while (remaining >= value)
            {
                builder.Append(symbol);
                remaining -= value;
            }
        }
        return builder.ToString();
    }

    private static string Alpha(int number)
    {
        var builder = new System.Text.StringBuilder();
        var current = number;
        while (current > 0)
        {
            current--;
            builder.Insert(0, (char)('a' + current % 26));
            current /= 26;
        }
        return builder.ToString();
    }
}
