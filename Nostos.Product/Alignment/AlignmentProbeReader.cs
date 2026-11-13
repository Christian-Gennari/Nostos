using Nostos.Product.BookText;

namespace Nostos.Product.Alignment;

/// <summary>What one probe's transcript says about where the clip is in the book.</summary>
internal sealed record ProbeReading(
    IReadOnlyList<AudioAlignmentSearch.Point> Knots,
    double Score,
    PassageLocation? OpeningStart,
    PassageLocation? ClosingEnd);

internal sealed class AlignmentProbeReader
{
    /// <summary>
    /// Places a probe's transcript in the book. The closing words are spoken
    /// at the clip's end and the opening words at its start; between them a
    /// word's time is its share of the transcript, because the transcript is
    /// the speech of exactly this clip. When one of the two cannot be placed,
    /// a window from the middle is tried, so a clip with a misheard ending is
    /// still read. Null when nothing in the transcript is found in the book.
    /// </summary>
    public ProbeReading? ReadProbe(
        PassageMatcher matcher,
        string transcript,
        double clipStart,
        double clipEnd,
        double probeSeconds)
    {
        var spoken = AlignmentTokens.Split(transcript);
        var settings = MatcherSettings.For(spoken);

        if (spoken.Count < settings.MinimumTokens)
            return null;

        var size = Math.Min(settings.MaximumTokens, spoken.Count);
        var first = spoken[0].Index;
        var last = spoken[^1].Index + spoken[^1].Length;

        double TimeAt(int token) =>
            token >= spoken.Count
                ? clipEnd
                : clipStart + (clipEnd - clipStart) *
                    (spoken[token].Index - first) / (double)(last - first);

        var windows = new List<(int From, int To, PassageMatch Match)>();

        void Place(int from)
        {
            if (windows.Any(window => window.From == from))
                return;

            var match = matcher.LocateTokens(
                spoken.Skip(from).Take(size),
                settings);

            if (!match.Accepted)
                return;

            if (match.Start?.Locator is not
                    (EpubBookTextSourceLocator or PdfBookTextSourceLocator) ||
                match.End?.Locator is not
                    (EpubBookTextSourceLocator or PdfBookTextSourceLocator))
            {
                throw new AlignmentException(
                    "alignment_locator_invalid",
                    422,
                    "A probe has no supported text source provenance.");
            }

            windows.Add((from, from + size, match));
        }

        var closing = spoken.Count - size;

        Place(closing);

        if (closing > 0)
            Place(0);

        if (closing > 1 && windows.Count < 2)
            Place(closing / 2);

        if (windows.Count == 0)
            return null;

        // The closing window is tried first, so it wins a tie.
        var best = windows.MaxBy(window => window.Match.Score);

        // The pairs are (second, spoken characters). A clip cannot cover more
        // text than fast speech allows, and what it covers runs forward. Windows that disagree did not all find the same
        // place; the best one then stands alone.
        var points = new List<AudioAlignmentSearch.Point>();
        var agree = true;

        foreach (var window in windows.OrderBy(window => window.From))
        {
            foreach (var point in new[]
            {
                new AudioAlignmentSearch.Point(
                    TimeAt(window.From),
                    window.Match.StartSpoken),
                new AudioAlignmentSearch.Point(
                    TimeAt(window.To),
                    window.Match.EndSpoken),
            })
            {
                if (points.Count == 0)
                {
                    points.Add(point);
                    continue;
                }

                var previous = points[^1];

                if (point.AudioSeconds > previous.AudioSeconds &&
                    point.TextOffset > previous.TextOffset)
                {
                    points.Add(point);
                }
                else if (point.AudioSeconds > previous.AudioSeconds + 1 &&
                         point.TextOffset <= previous.TextOffset)
                {
                    agree = false;
                }
            }
        }

        var plausible = Math.Ceiling(probeSeconds * 40);

        if (!agree ||
            points.Count < 2 ||
            points[^1].TextOffset - points[0].TextOffset > plausible)
        {
            windows = [best];
            points =
            [
                new AudioAlignmentSearch.Point(
                    TimeAt(best.From),
                    best.Match.StartSpoken),
                new AudioAlignmentSearch.Point(
                    TimeAt(best.To),
                    best.Match.EndSpoken),
            ];

            if (points[1].TextOffset <= points[0].TextOffset ||
                points[1].AudioSeconds <= points[0].AudioSeconds)
                return null;
        }

        var opening = windows.FirstOrDefault(window => window.From == 0);
        var ending = windows.FirstOrDefault(window => window.To == spoken.Count);

        return new ProbeReading(
            points,
            best.Match.Score,
            opening.Match?.Start,
            ending.Match?.End);
    }

    public static bool ConsistentWithAnchors(
        double positionSeconds,
        int targetOffset,
        AudioAlignmentSearch.Point? before,
        AudioAlignmentSearch.Point? after,
        double toleranceSeconds)
    {
        if (before is null || after is null ||
            before.TextOffset > targetOffset ||
            after.TextOffset < targetOffset)
            return true;

        if (!double.IsFinite(toleranceSeconds) ||
            toleranceSeconds <= 0)
            return false;

        if (before.AudioSeconds > after.AudioSeconds)
            return false;

        var low = before.AudioSeconds - toleranceSeconds;
        var high = after.AudioSeconds + toleranceSeconds;

        return double.IsFinite(positionSeconds) &&
            positionSeconds >= low &&
            positionSeconds <= high;
    }
}
