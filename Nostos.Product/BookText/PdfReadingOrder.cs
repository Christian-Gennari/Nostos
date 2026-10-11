namespace Nostos.Product.BookText;

/// <summary>A word and where it sits on the page, in PDF points (origin bottom-left).</summary>
internal readonly record struct PlacedWord(
    string Text,
    double Left,
    double Right,
    double Bottom,
    double Height);

/// <summary>
/// Turns a page's positioned words into text a person would read.
///
/// A PDF stores glyphs in drawing order. Taking that stream as text fails two
/// ways that matter for search and for matching speech: a line that ends
/// without a space glyph runs into the next ("indeed.There"), and an OCR layer
/// is often not drawn in reading order at all ("him bestow implored to a
/// trifle"); many scanned PDFs come out as one unbroken string.
///
/// The words are placed instead: split into two columns when the page has a
/// clear vertical gutter, then into lines by baseline, then left to right.
/// It is deliberately simple. It reads single- and two-column pages; a page
/// with three or more columns, or text set at an angle, is read line by line
/// across the page.
/// </summary>
internal static class PdfReadingOrder
{
    private const int Buckets = 200;

    public static string Compose(
        IReadOnlyList<PlacedWord> words,
        double pageLeft,
        double pageRight)
    {
        var placed = words
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .ToList();

        if (placed.Count == 0)
            return string.Empty;

        return string.Join(
            '\n',
            SplitColumns(placed, pageLeft, pageRight).Select(Lines));
    }

    /// <summary>
    /// One group per column, left to right. A gutter is a vertical band near
    /// the middle of the page that almost no word crosses, with a real share
    /// of the words on each side. A heading set across both columns crosses
    /// it with a few words, which the threshold allows.
    /// </summary>
    private static IEnumerable<List<PlacedWord>> SplitColumns(
        List<PlacedWord> words,
        double pageLeft,
        double pageRight)
    {
        var width = pageRight - pageLeft;
        if (!(width > 0) || words.Count < 40)
            return [words];

        var crossing = new int[Buckets];
        foreach (var word in words)
        {
            var from = Bucket(word.Left, pageLeft, width);
            var to = Bucket(word.Right, pageLeft, width);
            for (var bucket = from; bucket <= to; bucket++)
                crossing[bucket]++;
        }

        var allowed = Math.Max(1, words.Count / 100);
        var bestStart = -1;
        var bestLength = 0;
        var runStart = -1;

        for (var bucket = Buckets / 4; bucket <= Buckets * 3 / 4; bucket++)
        {
            if (crossing[bucket] <= allowed)
            {
                if (runStart < 0) runStart = bucket;
                var length = bucket - runStart + 1;
                if (length > bestLength)
                {
                    bestLength = length;
                    bestStart = runStart;
                }
            }
            else
            {
                runStart = -1;
            }
        }

        // At least 1.5% of the page width, or it is just a space between words.
        if (bestLength < 3)
            return [words];

        var gutter = pageLeft + width * (bestStart + bestLength / 2.0) / Buckets;
        var left = words.Where(word => (word.Left + word.Right) / 2 < gutter).ToList();
        var right = words.Where(word => (word.Left + word.Right) / 2 >= gutter).ToList();

        var smallest = words.Count * 0.15;
        return left.Count < smallest || right.Count < smallest
            ? [words]
            : [left, right];
    }

    private static int Bucket(double x, double pageLeft, double width) =>
        Math.Clamp((int)((x - pageLeft) / width * Buckets), 0, Buckets - 1);

    /// <summary>
    /// Top line first. A word joins the current line when its baseline is
    /// within half a word height of the line's.
    /// </summary>
    private static string Lines(List<PlacedWord> words)
    {
        var lines = new List<List<PlacedWord>>();
        double baseline = 0;

        foreach (var word in words.OrderByDescending(word => word.Bottom))
        {
            var height = Math.Max(1, word.Height);
            if (lines.Count == 0 || Math.Abs(word.Bottom - baseline) > height * 0.5)
            {
                lines.Add([]);
                baseline = word.Bottom;
            }

            lines[^1].Add(word);
        }

        return string.Join(
            '\n',
            lines.Select(line => string.Join(
                ' ',
                line.OrderBy(word => word.Left).Select(word => word.Text))));
    }
}
