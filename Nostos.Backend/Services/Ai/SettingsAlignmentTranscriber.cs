using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// SelfHosted alignment transcription: uses the "alignment transcription"
/// section from Settings, which falls back to the voice transcription
/// configuration unless the user gave alignment its own provider.
/// </summary>
public sealed class SettingsAlignmentTranscriber(
    AlignmentSttProvider provider,
    IAiProviderConfigResolver configuration) : IAlignmentTranscriber
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        (await configuration.GetEffectiveAlignmentSttAsync(ct)).IsAvailable;

    public Task<SttResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string? contentType,
        string? languageHint,
        CancellationToken ct = default) =>
        provider.TranscribeAsync(audio, fileName, contentType, languageHint, ct);
}
