namespace Nostos.Product.Alignment;

/// <summary>
/// Pure, bounded search over correspondences between audiobook seconds
/// and EPUB global character offsets. Does not perform I/O.
/// </summary>
public sealed class AudioAlignmentSearch
{
    public sealed record Point(
        double AudioSeconds,
        double TextOffset);

    /// <summary>
    /// What one transcribed clip established: the text it covers. When
    /// <paramref name="Knots"/> is given, it lists the (time, text) pairs the
    /// transcript supports, in order; the text span then runs from the first
    /// pair to the last. Without it the span is spread evenly over the clip.
    /// </summary>
    public sealed record Observation(
        double ClipStartSeconds,
        double ClipEndSeconds,
        int TextStartOffset,
        int TextEndOffset,
        double Score,
        IReadOnlyList<Point>? Knots = null);

    public sealed record Decision(
        bool Located,
        bool Inconsistent,
        double? PositionSeconds,
        double? NextEstimateSeconds);

    private readonly int targetOffset;
    private readonly double durationSeconds;
    private readonly double averageRate;
    private readonly AlignmentOptions options;

    private readonly List<Point> locatedCenters = [];
    /// <summary>The last pair of each located probe: normally the clip's end and the end of its matched words.</summary>
    private readonly List<Point> exactEnds = [];
    private readonly List<double> probedClipCenters = [];

    /// <summary>Characters per second inside the clip of each entry of <see cref="exactEnds"/>.</summary>
    private readonly List<double> exactEndRates = [];

    /// <summary>
    /// How much of the last located clip its transcript placed, and how far
    /// before the clip's end that stretch stopped. A clean transcript places
    /// the whole clip; a noisy one may place only its closing words.
    /// </summary>
    private double readableSeconds;
    private double readableGapSeconds;

    private Point? lower;
    private Point? upper;

    public double EstimateSeconds { get; private set; }

    public bool HasNextProbe { get; private set; } = true;

    /// <summary>
    /// Where the target is, counted from the nearest probe's end pair (clip
    /// end, end of its matched words), when that pair lies within about one
    /// clip of the target. Null when no probe came that close.
    ///
    /// The pace between the pair and the target is not known, only the
    /// book's average and what recent probes measured. So the count is biased
    /// to land EARLY: forward from the pair it assumes fast narration (fewer
    /// seconds), backward it assumes slow narration (more seconds). A reader
    /// who starts a little before the sentence loses nothing; one who starts
    /// after it has lost the sentence.
    /// </summary>
    public double? NearTargetPositionSeconds
    {
        get
        {
            if (exactEnds.Count == 0 || !ValidRate(averageRate))
                return null;

            var nearest = exactEnds.MinBy(
                point => Math.Abs(point.TextOffset - targetOffset))!;

            var characters = targetOffset - nearest.TextOffset;
            if (Math.Abs(characters) > averageRate * options.ProbeSeconds)
                return null;

            // The pace inside the nearest clip is the best evidence of the pace
            // just outside it, and narrows the allowance for being wrong.
            var inClip = exactEndRates[exactEnds.IndexOf(nearest)];
            double fast;
            double slow;

            if (ValidRate(inClip))
            {
                fast = inClip * 1.1;
                slow = inClip * 0.9;
            }
            else
            {
                var measured = LocalRate();
                fast = Math.Max(averageRate, ValidRate(measured) ? measured : averageRate) * 1.25;
                slow = Math.Min(averageRate, ValidRate(measured) ? measured : averageRate) * 0.8;
            }

            var seconds = nearest.AudioSeconds +
                characters / (characters >= 0 ? fast : slow);

            return Clamp(seconds - options.LeadInSeconds);
        }
    }

    public double? ApproximatePositionSeconds
    {
        get
        {
            if (lower is not null && upper is not null)
                return Clamp(
                    lower.AudioSeconds +
                    (upper.AudioSeconds - lower.AudioSeconds) / 2);

            if (lower is not null)
                return Clamp(
                    lower.AudioSeconds +
                    SafeSeconds(
                        targetOffset - lower.TextOffset,
                        averageRate));

            if (upper is not null)
                return Clamp(
                    upper.AudioSeconds -
                    SafeSeconds(
                        upper.TextOffset - targetOffset,
                        averageRate));

            return Clamp(EstimateSeconds);
        }
    }

    public AudioAlignmentSearch(
        int targetOffset,
        int totalCharacters,
        double durationSeconds,
        AlignmentOptions options,
        Point? before = null,
        Point? after = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (targetOffset < 0 ||
            totalCharacters <= 0 ||
            !double.IsFinite(durationSeconds) ||
            durationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "A finite duration and nonempty document are required.");
        }

        this.targetOffset = targetOffset;
        this.durationSeconds = durationSeconds;
        this.options = options;

        averageRate = totalCharacters / durationSeconds;

        if (!ValidRate(averageRate))
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                "The document-to-duration rate must be finite and positive.");
        }

        if (before is not null)
            CheckPoint(before);

        if (after is not null)
            CheckPoint(after);

        if (before is not null && after is not null &&
            before.AudioSeconds < after.AudioSeconds &&
            before.TextOffset <= after.TextOffset)
        {
            lower = before;
            upper = after;
        }
        else
        {
            if (before is not null &&
                before.TextOffset <= targetOffset)
                lower = before;

            if (after is not null &&
                after.TextOffset >= targetOffset)
                upper = after;
        }

        double estimate;

        if (lower is not null && upper is not null)
        {
            var span = upper.TextOffset - lower.TextOffset;

            estimate = span > 0
                ? lower.AudioSeconds +
                  SafeSeconds(
                      targetOffset - lower.TextOffset,
                      span / (upper.AudioSeconds - lower.AudioSeconds))
                : lower.AudioSeconds +
                  (upper.AudioSeconds - lower.AudioSeconds) / 2;
        }
        else if (lower is not null)
        {
            estimate = lower.AudioSeconds +
                SafeSeconds(
                    targetOffset - lower.TextOffset,
                    averageRate);
        }
        else if (upper is not null)
        {
            estimate = upper.AudioSeconds -
                SafeSeconds(
                    upper.TextOffset - targetOffset,
                    averageRate);
        }
        else
        {
            estimate = targetOffset / (double)totalCharacters *
                durationSeconds;
        }

        EstimateSeconds = Clamp(estimate);
    }

    /// <summary>
    /// The requested cutter endpoint puts the current estimate halfway
    /// through the part of a clip a transcript can be expected to place:
    /// the whole clip at first, and after a clip that was placed only by its
    /// closing words, those words. Clamped at the audiobook's boundaries.
    /// </summary>
    public double ClipEndSeconds =>
        Math.Clamp(
            EstimateSeconds + AfterEstimateSeconds,
            Math.Min(durationSeconds, 0.001),
            durationSeconds);

    private double AfterEstimateSeconds =>
        readableSeconds > 0
            ? Math.Clamp(
                readableSeconds / 2 + readableGapSeconds,
                Math.Min(2, options.ProbeSeconds / 2),
                options.ProbeSeconds / 2)
            : options.ProbeSeconds / 2;

    public double ClipStartSeconds =>
        Math.Max(0, ClipEndSeconds - options.ProbeSeconds);

    public Decision Observe(Observation probe)
    {
        if (!double.IsFinite(probe.ClipStartSeconds) ||
            !double.IsFinite(probe.ClipEndSeconds) ||
            probe.ClipStartSeconds < 0 ||
            probe.ClipEndSeconds <= probe.ClipStartSeconds ||
            probe.ClipEndSeconds > durationSeconds ||
            probe.TextStartOffset < 0 ||
            probe.TextEndOffset <= probe.TextStartOffset)
        {
            throw new ArgumentException(
                "Invalid located probe.", nameof(probe));
        }

        RecordProbe(
            probe.ClipStartSeconds,
            probe.ClipEndSeconds);

        var knots = Knots(probe);

        // Use the measured passage midpoint for search progression.
        var center = new Point(
            knots[0].AudioSeconds +
                (knots[^1].AudioSeconds - knots[0].AudioSeconds) / 2,
            probe.TextStartOffset +
                ((double)probe.TextEndOffset -
                    probe.TextStartOffset) / 2);

        foreach (var previous in locatedCenters)
        {
            var audioDifference =
                center.AudioSeconds - previous.AudioSeconds;

            var textDifference =
                center.TextOffset - previous.TextOffset;

            // Same or overlapping audio intervals are not independent
            // evidence of ordering.
            if (Math.Abs(audioDifference) <
                options.ProbeSeconds * 0.75)
                continue;

            if (audioDifference * textDifference <
                -options.ConsistencyToleranceCharacters *
                Math.Abs(audioDifference))
            {
                HasNextProbe = false;
                return new Decision(false, true, null, null);
            }
        }

        locatedCenters.Add(center);
        exactEnds.Add(knots[^1]);

        var placedSeconds = knots[^1].AudioSeconds - knots[0].AudioSeconds;
        var placedRate = (knots[^1].TextOffset - knots[0].TextOffset) / placedSeconds;

        exactEndRates.Add(
            ValidRate(placedRate) &&
            placedRate >= averageRate / options.MaximumLocalRateRatio &&
            placedRate <= averageRate * options.MaximumLocalRateRatio
                ? placedRate
                : double.NaN);

        readableSeconds = placedSeconds;
        readableGapSeconds = probe.ClipEndSeconds - knots[^1].AudioSeconds;

        // Tolerance helps guide the search, but cannot turn a passage
        // that does not contain the target into a successful location.
        if (targetOffset >= probe.TextStartOffset &&
            targetOffset <= probe.TextEndOffset)
        {
            var position = knots[^1].AudioSeconds;

            for (var i = 1; i < knots.Count; i++)
            {
                if (targetOffset > knots[i].TextOffset)
                    continue;

                var from = knots[i - 1];
                var to = knots[i];

                position = from.AudioSeconds +
                    (targetOffset - from.TextOffset) /
                    (to.TextOffset - from.TextOffset) *
                    (to.AudioSeconds - from.AudioSeconds);
                break;
            }

            HasNextProbe = false;

            return new Decision(
                true,
                false,
                Clamp(position - options.LeadInSeconds),
                null);
        }

        if (center.TextOffset < targetOffset)
        {
            if (lower is null ||
                center.AudioSeconds > lower.AudioSeconds)
                lower = center;
        }
        else
        {
            if (upper is null ||
                center.AudioSeconds < upper.AudioSeconds)
                upper = center;
        }

        if (lower is not null && upper is not null &&
            (lower.AudioSeconds >= upper.AudioSeconds ||
             lower.TextOffset >
             upper.TextOffset + options.ConsistencyToleranceCharacters))
        {
            HasNextProbe = false;
            return new Decision(false, true, null, null);
        }

        var rate = LocalRate();

        var proposed = center.AudioSeconds +
            SafeSeconds(
                targetOffset - center.TextOffset,
                rate);

        // A near miss may guide the next estimate, but is not located.
        // Deduplication operates on actual clip centres.
        Advance(proposed);

        return new Decision(
            false,
            false,
            null,
            HasNextProbe ? EstimateSeconds : null);
    }

    /// <summary>
    /// The (time, text) pairs a probe supports, with strictly increasing times
    /// and text offsets. Speech is taken as even between two pairs. Pairs that
    /// do not fit the clip or the span fall back to the clip's two ends.
    /// </summary>
    private static IReadOnlyList<Point> Knots(Observation probe)
    {
        var given = probe.Knots;

        if (given is { Count: >= 2 } &&
            given[0].TextOffset == probe.TextStartOffset &&
            given[^1].TextOffset == probe.TextEndOffset)
        {
            var ordered = true;

            for (var i = 0; i < given.Count && ordered; i++)
            {
                ordered =
                    double.IsFinite(given[i].AudioSeconds) &&
                    given[i].AudioSeconds >= probe.ClipStartSeconds &&
                    given[i].AudioSeconds <= probe.ClipEndSeconds &&
                    (i == 0 ||
                     (given[i].AudioSeconds > given[i - 1].AudioSeconds &&
                      given[i].TextOffset > given[i - 1].TextOffset));
            }

            if (ordered)
                return given;
        }

        return
        [
            new Point(probe.ClipStartSeconds, probe.TextStartOffset),
            new Point(probe.ClipEndSeconds, probe.TextEndOffset),
        ];
    }

    public double Decline()
    {
        RecordProbe(ClipStartSeconds, ClipEndSeconds);

        var proposed = EstimateSeconds +
            options.ProbeSeconds * 1.5;

        Advance(proposed);
        return EstimateSeconds;
    }

    private double LocalRate()
    {
        if (locatedCenters.Count < 2)
            return averageRate;

        var previous = locatedCenters[^2];
        var current = locatedCenters[^1];

        var audioDelta =
            current.AudioSeconds - previous.AudioSeconds;

        var textDelta =
            current.TextOffset - previous.TextOffset;

        if (Math.Abs(audioDelta) <
            options.ProbeSeconds * 0.75)
            return averageRate;

        var rate = textDelta / audioDelta;

        if (!ValidRate(rate) ||
            rate < averageRate / options.MaximumLocalRateRatio ||
            rate > averageRate * options.MaximumLocalRateRatio)
            return averageRate;

        return rate;
    }

    private void RecordProbe(double start, double end)
    {
        var center = start + (end - start) / 2;

        if (double.IsFinite(center))
            probedClipCenters.Add(center);
    }

    private void Advance(double proposed)
    {
        EstimateSeconds = InsideBracket(proposed);

        var center = ClipCenter(EstimateSeconds);

        if (!WasProbed(center))
        {
            HasNextProbe = true;
            return;
        }

        // Outside an established bracket, preserve the usual
        // progression. Repeated boundary clips cannot make progress.
        if (lower is null || upper is null)
        {
            HasNextProbe = false;
            return;
        }

        var low = lower.AudioSeconds;
        var high = upper.AudioSeconds;

        if (high <= low)
        {
            HasNextProbe = false;
            return;
        }

        var halfProbe = options.ProbeSeconds / 2;

        // Prefer the side containing more unexplored space.
        // If necessary, try the opposite side before giving up.
        var rightRoom = high - EstimateSeconds;
        var leftRoom = EstimateSeconds - low;

        var directions = rightRoom >= leftRoom
            ? new[] { 1d, -1d }
            : new[] { -1d, 1d };

        foreach (var direction in directions)
        {
            var candidate = EstimateSeconds +
                direction * halfProbe;

            if (candidate <= low || candidate >= high)
                continue;

            var candidateCenter = ClipCenter(candidate);

            if (WasProbed(candidateCenter))
                continue;

            EstimateSeconds = Clamp(candidate);
            HasNextProbe = true;
            return;
        }

        // Neither half-clip step fits in the remaining bracket.
        // Do not request another paid transcription of the same area.
        HasNextProbe = false;
    }

    private bool WasProbed(double center) =>
        probedClipCenters.Any(
            previous => Math.Abs(previous - center) <= 2);

    private double ClipCenter(double estimate)
    {
        var end = Math.Clamp(
            estimate + AfterEstimateSeconds,
            Math.Min(durationSeconds, 0.001),
            durationSeconds);

        var start = Math.Max(
            0,
            end - options.ProbeSeconds);

        return start + (end - start) / 2;
    }

    private double InsideBracket(double proposed)
    {
        proposed = Clamp(proposed);

        if (lower is null || upper is null)
            return proposed;

        var low = lower.AudioSeconds;
        var high = upper.AudioSeconds;

        if (high <= low)
            return Clamp(low + (high - low) / 2);

        if (proposed <= low || proposed >= high)
            proposed = low + (high - low) / 2;

        return Clamp(proposed);
    }

    private double SafeSeconds(double textDifference, double rate)
    {
        if (!double.IsFinite(textDifference))
            return 0;

        if (!ValidRate(rate))
            rate = averageRate;

        var seconds = textDifference / rate;

        if (!double.IsFinite(seconds))
        {
            seconds = textDifference > 0
                ? durationSeconds
                : textDifference < 0
                    ? -durationSeconds
                    : 0;
        }

        return Math.Clamp(
            seconds,
            -durationSeconds,
            durationSeconds);
    }

    private static bool ValidRate(double rate) =>
        double.IsFinite(rate) && rate > 0;

    private double Clamp(double position)
    {
        if (double.IsNaN(position))
            return 0;

        if (double.IsPositiveInfinity(position))
            return durationSeconds;

        if (double.IsNegativeInfinity(position))
            return 0;

        return Math.Clamp(position, 0, durationSeconds);
    }

    private void CheckPoint(Point point)
    {
        if (!double.IsFinite(point.AudioSeconds) ||
            !double.IsFinite(point.TextOffset) ||
            point.AudioSeconds < 0 ||
            point.AudioSeconds > durationSeconds ||
            point.TextOffset < 0)
        {
            throw new ArgumentException(
                "Invalid alignment anchor.");
        }
    }
}
