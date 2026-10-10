namespace Nostos.Backend.Providers.Acquisition.Media;

/// <summary>
/// Reads how long an audio file plays, from the file itself.
///
/// A seam rather than a direct call so the acquisition pipeline can be tested
/// without real audio, and so a host can substitute another measurement.
/// </summary>
public interface IAudioDurationProbe
{
    /// <summary>Null when the file is not readable audio.</summary>
    TimeSpan? ProbeDuration(string filePath);
}

/// <summary>
/// Measures duration in managed code, with the tag library the product already
/// uses for uploaded audiobooks. No external media tool is involved, so a
/// multi-track import has no server prerequisite beyond Nostos itself.
///
/// Checked against ffprobe on fifteen sections from six LibriVox recordings
/// (22.05 and 24 kHz, 64 kbps): the two agreed to within 27 ms on every file.
/// </summary>
public sealed class AtlAudioDurationProbe : IAudioDurationProbe
{
    public TimeSpan? ProbeDuration(string filePath)
    {
        try
        {
            var track = new ATL.Track(filePath);
            return track.DurationMs > 0 ? TimeSpan.FromMilliseconds(track.DurationMs) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
