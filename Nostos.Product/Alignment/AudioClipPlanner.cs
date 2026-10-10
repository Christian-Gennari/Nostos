using Nostos.Backend.Services;

namespace Nostos.Product.Alignment;

/// <summary>
/// One portion of an audiobook clip, expressed in seconds relative to
/// the beginning of its source track.
/// </summary>
/// <param name="TrackNumber">The 1-based track number.</param>
/// <param name="StartSeconds">The offset into the track.</param>
/// <param name="DurationSeconds">The duration to extract.</param>
public sealed record AudioClipSegment(
    int TrackNumber,
    double StartSeconds,
    double DurationSeconds);

/// <summary>
/// Pure timeline planner for extracting the audio immediately preceding
/// a playback position.
///
/// All positions supplied by callers are on the audiobook's global timeline.
/// Track durations are stored in milliseconds; the resulting segments use
/// track-relative seconds suitable for ffmpeg.
///
/// No media is opened, decoded, or written by this planner.
/// </summary>
public static class AudioClipPlanner
{
    public static IReadOnlyList<AudioClipSegment> Plan(
        IReadOnlyList<BookTrack> tracks,
        double positionSeconds,
        double clipLengthSeconds)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(positionSeconds));

        if (!double.IsFinite(clipLengthSeconds) || clipLengthSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(clipLengthSeconds));

        if (tracks.Count == 0)
            throw new ArgumentException(
                "At least one audio track is required.",
                nameof(tracks));

        if (BookTrackList.Validate(tracks) is { } error)
            throw new ArgumentException(error, nameof(tracks));

        var totalDurationMs = BookTrackList.TotalDurationMs(tracks);
        var positionMs = positionSeconds * 1000.0;

        if (positionMs > totalDurationMs)
            throw new ArgumentOutOfRangeException(
                nameof(positionSeconds),
                "The playback position exceeds the audiobook duration.");

        if (positionMs == 0)
            return [];

        var startMs = Math.Max(0, positionMs - clipLengthSeconds * 1000.0);
        var segments = new List<AudioClipSegment>();

        double trackStartMs = 0;

        foreach (var track in tracks)
        {
            var trackEndMs = trackStartMs + track.DurationMs;

            // Intersect the requested global interval with this track.
            var segmentStartMs = Math.Max(startMs, trackStartMs);
            var segmentEndMs = Math.Min(positionMs, trackEndMs);

            if (segmentEndMs > segmentStartMs)
            {
                segments.Add(new AudioClipSegment(
                    TrackNumber: track.Number,
                    StartSeconds: (segmentStartMs - trackStartMs) / 1000.0,
                    DurationSeconds: (segmentEndMs - segmentStartMs) / 1000.0));
            }

            trackStartMs = trackEndMs;

            if (trackStartMs >= positionMs)
                break;
        }

        return segments;
    }
}
