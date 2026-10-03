namespace Nostos.Backend.Services.Ai;

/// <summary>
/// The one operation voice capture needs from speech-to-text: turn one uploaded
/// audio blob into text.
///
/// Deliberately this narrow — no streaming session object, no vendor capability
/// matrix, no audio framework. The gateway already returns per-segment
/// confidence (<c>avg_logprob</c>), but nothing on this path consumes it yet, so
/// it is not modelled. A provider that needs segments can be widened then,
/// against a real caller, instead of carrying a speculative shape now.
/// </summary>
public interface ISTtProvider
{
    /// <summary>
    /// Transcribes one audio stream.
    ///
    /// <paramref name="audio"/> is read to the end exactly once and is never
    /// written to disk by the implementation. Failure is reported as a typed
    /// <see cref="SttException"/> — permission, too-large, unsupported format,
    /// provider failure — so callers can map it to data and nothing retries
    /// automatically and spends quota.
    /// </summary>
    Task<SttResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string? contentType,
        string? languageHint,
        CancellationToken ct = default);
}

/// <summary>
/// A successful transcription.
///
/// <see cref="Text"/> is already trimmed: the provider returns a leading space
/// (measured, not theoretical). <see cref="Language"/> and
/// <see cref="DurationSeconds"/> are whatever the provider reported and may be
/// null when it does not say.
/// </summary>
public sealed record SttResult(string Text, string? Language, double? DurationSeconds);
