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
            .Where(word =>
                !string.IsNullOrWhiteSpace(word.Text) &&
                double.IsFinite(word.Left) &&
                double.IsFinite(word.Right) &&
                double.IsFinite(word.Bottom))
            .ToList();

        if (placed.Count == 0)
            return string.Empty;

        var lines = Lines(placed);
        var gutter = FindGutter(placed, pageLeft, pageRight);

        if (gutter is not { } band)
            return string.Join('\n', lines.Select(Text));

        // Read down the page in bands. Lines that cross the gutter (a title,
        // an abstract set across both columns) are read whole, where they
        // stand. Each run of column lines between them is read left column
        // first, then right.
        var output = new List<string>();
        var leftRun = new List<string>();
        var rightRun = new List<string>();

        void FlushColumns()
        {
            output.AddRange(leftRun);
            output.AddRange(rightRun);
            leftRun.Clear();
            rightRun.Clear();
        }

        var middle = (band.From + band.To) / 2;

        foreach (var line in lines)
        {
            if (line.Any(word => word.Left < band.To && word.Right > band.From))
            {
                FlushColumns();
                output.Add(Text(line));
                continue;
            }

            var left = line.Where(word => (word.Left + word.Right) / 2 < middle).ToList();
            var right = line.Where(word => (word.Left + word.Right) / 2 >= middle).ToList();
            if (left.Count > 0) leftRun.Add(Text(left));
            if (right.Count > 0) rightRun.Add(Text(right));
        }

        FlushColumns();
        return string.Join('\n', output);
    }

    /// <summary>
    /// The vertical band near the middle of the page that almost no word
    /// crosses, when the page has one with a real share of the words on each
    /// side. A heading set across both columns crosses it with a few words,
    /// which the threshold allows.
    /// </summary>
    private static (double From, double To)? FindGutter(
        List<PlacedWord> words,
        double pageLeft,
        double pageRight)
    {
        var width = pageRight - pageLeft;
        if (!double.IsFinite(width) || !(width > 0) || words.Count < 40)
            return null;

        var crossing = new int[Buckets];
        foreach (var word in words)
        {
            var from = Bucket(word.Left, pageLeft, width);
            var to = Bucket(word.Right, pageLeft, width);
            for (var bucket = Math.Min(from, to); bucket <= Math.Max(from, to); bucket++)
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
            return null;

        var from0 = pageLeft + width * bestStart / Buckets;
        var to0 = pageLeft + width * (bestStart + bestLength) / Buckets;
        var middle = (from0 + to0) / 2;

        var leftCount = words.Count(word => (word.Left + word.Right) / 2 < middle);
        var smallest = words.Count * 0.15;

        return leftCount < smallest || words.Count - leftCount < smallest
            ? null
            : (from0, to0);
    }

    private static int Bucket(double x, double pageLeft, double width) =>
        Math.Clamp((int)((x - pageLeft) / width * Buckets), 0, Buckets - 1);

    /// <summary>
    /// Top line first. A word joins the current line when its baseline is
    /// within half a typical word height of the line's. The page's median
    /// height is used, not each word's own: a dash, a comma or an OCR
    /// fragment can have a box a point tall and would otherwise start a line
    /// of its own in the middle of a sentence.
    /// </summary>
    private static List<List<PlacedWord>> Lines(List<PlacedWord> words)
    {
        var heights = words
            .Select(word => word.Height)
            .Where(height => double.IsFinite(height) && height > 0)
            .OrderBy(height => height)
            .ToList();

        var tolerance = Math.Max(1, (heights.Count > 0 ? heights[heights.Count / 2] : 10) * 0.5);

        var lines = new List<List<PlacedWord>>();
        double baseline = 0;

        foreach (var word in words.OrderByDescending(word => word.Bottom))
        {
            if (lines.Count == 0 || Math.Abs(word.Bottom - baseline) > tolerance)
            {
                lines.Add([]);
                baseline = word.Bottom;
            }

            lines[^1].Add(word);
        }

        return lines;
    }

    private static string Text(IEnumerable<PlacedWord> line) =>
        string.Join(' ', line.OrderBy(word => word.Left).Select(word => word.Text));
}
