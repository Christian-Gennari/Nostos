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

    public sealed record Observation(
        double ClipStartSeconds,
        double ClipEndSeconds,
        int TextStartOffset,
        int TextEndOffset,
        double Score);

    public sealed record Decision(
        bool Located,
        bool Inconsistent,
        double? PositionSeconds,
        double? NextEstimateSeconds);

    private readonly int targetOffset;
    private readonly double durationSeconds;
    private readonly double averageRate;
    private readonly AlignmentOptions options;

    private readonly List<Point> observations = [];
    private readonly List<Point> locatedCenters = [];

    private Point? lower;
    private Point? upper;

    public double EstimateSeconds { get; private set; }

    public double? ApproximatePositionSeconds
    {
        get
        {
            if (lower is not null && upper is not null)
                return (lower.AudioSeconds + upper.AudioSeconds) / 2;

            if (lower is not null)
                return Clamp(
                    lower.AudioSeconds +
                    (targetOffset - lower.TextOffset) / averageRate);

            if (upper is not null)
                return Clamp(
                    upper.AudioSeconds -
                    (upper.TextOffset - targetOffset) / averageRate);

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
                  (targetOffset - lower.TextOffset) *
                  (upper.AudioSeconds - lower.AudioSeconds) / span
                : (lower.AudioSeconds + upper.AudioSeconds) / 2;
        }
        else if (lower is not null)
        {
            estimate = lower.AudioSeconds +
                (targetOffset - lower.TextOffset) / averageRate;
        }
        else if (upper is not null)
        {
            estimate = upper.AudioSeconds -
                (upper.TextOffset - targetOffset) / averageRate;
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
    /// through a full clip, except near audiobook boundaries.
    /// </summary>
    public double ClipEndSeconds =>
        Math.Clamp(
            EstimateSeconds + options.ProbeSeconds / 2,
            Math.Min(durationSeconds, 0.001),
            durationSeconds);

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

        // The start/end mappings are approximations. Use the
        // measured passage midpoint for search progression.
        var center = new Point(
            (probe.ClipStartSeconds + probe.ClipEndSeconds) / 2,
            (probe.TextStartOffset + (double)probe.TextEndOffset) / 2);

        var measuredEnd = new Point(
            probe.ClipEndSeconds, probe.TextEndOffset);

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
                return new Decision(false, true, null, null);
            }
        }

        locatedCenters.Add(center);
        observations.Add(measuredEnd);

        if (targetOffset >=
                probe.TextStartOffset - options.MatchToleranceCharacters &&
            targetOffset <=
                probe.TextEndOffset + options.MatchToleranceCharacters)
        {
            var proportion = Math.Clamp(
                (targetOffset - probe.TextStartOffset) /
                (double)(probe.TextEndOffset - probe.TextStartOffset),
                0,
                1);

            var position = probe.ClipStartSeconds +
                proportion *
                (probe.ClipEndSeconds - probe.ClipStartSeconds);

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
            return new Decision(false, true, null, null);
        }

        var rate = LocalRate();
        var proposed = center.AudioSeconds +
            (targetOffset - center.TextOffset) / rate;

        EstimateSeconds = InsideBracket(proposed);

        return new Decision(
            false, false, null, EstimateSeconds);
    }

    public double Decline()
    {
        var proposed = EstimateSeconds +
            options.ProbeSeconds * 1.5;

        EstimateSeconds = InsideBracket(proposed);
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

        if (!double.IsFinite(rate) ||
            rate <= 0 ||
            rate < averageRate / options.MaximumLocalRateRatio ||
            rate > averageRate * options.MaximumLocalRateRatio)
            return averageRate;

        return rate;
    }

    private double InsideBracket(double proposed)
    {
        proposed = Clamp(proposed);

        if (lower is null || upper is null)
            return proposed;

        var low = lower.AudioSeconds;
        var high = upper.AudioSeconds;

        if (high <= low)
            return Clamp((low + high) / 2);

        if (proposed <= low || proposed >= high)
            proposed = (low + high) / 2;

        return Clamp(proposed);
    }

    private double Clamp(double position) =>
        Math.Clamp(position, 0, durationSeconds);

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
