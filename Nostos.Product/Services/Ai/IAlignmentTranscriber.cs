using Nostos.Backend.Services.Ai;

namespace Nostos.Product.Services.Ai;

/// <summary>
/// Transcription boundary reserved for audiobook-to-publication alignment.
/// A host may provide a different model or API transport from voice capture.
/// </summary>
public interface IAlignmentTranscriber
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task<SttResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string? contentType,
        string? languageHint,
        CancellationToken ct = default);
}

/// <summary>
/// Default alignment transcription uses the existing effective voice STT
/// configuration. Hosts can replace this registration without changing
/// the voice-capture endpoint.
/// </summary>
public sealed class VoiceAlignmentTranscriber(
    ISTtProvider provider,
    IAiProviderConfigResolver configuration) : IAlignmentTranscriber
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        (await configuration.GetEffectiveSttAsync(ct)).IsAvailable;

    public Task<SttResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string? contentType,
        string? languageHint,
        CancellationToken ct = default) =>
        provider.TranscribeAsync(audio, fileName, contentType, languageHint, ct);
}
