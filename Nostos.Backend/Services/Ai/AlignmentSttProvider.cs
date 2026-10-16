namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Alignment-specific transcription entry point. Resolves configuration on
/// every call, then dispatches to exactly one transport.
///
/// This type is registered separately and never replaces the default
/// ISTtProvider used for voice capture.
/// </summary>
public sealed class AlignmentSttProvider(
    IAiProviderConfigResolver config,
    OpenAiCompatibleSttProvider multipartProvider,
    JsonBase64SttProvider jsonProvider) : ISTtProvider
{
    public async Task<SttResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string? contentType,
        string? languageHint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        var effective = await config.GetEffectiveAlignmentSttAsync(ct);

        if (!effective.Enabled)
        {
            throw SttException.Disabled();
        }

        if (!effective.IsAvailable)
        {
            throw SttException.NotConfigured(
                effective.ApiKeyEnvironmentVariable);
        }

        return effective.ApiStyle switch
        {
            AlignmentTranscriptionApiStyles.OpenAiMultipart =>
                await multipartProvider.TranscribeWithConfigAsync(
                    audio,
                    fileName,
                    contentType,
                    languageHint,
                    effective.Provider,
                    ct),

            AlignmentTranscriptionApiStyles.JsonBase64 =>
                await jsonProvider.TranscribeWithConfigAsync(
                    audio,
                    fileName,
                    contentType,
                    languageHint,
                    effective.Provider,
                    ct),

            _ => throw new SttException(
                SttErrorCodes.InvalidRequest,
                "The alignment transcription API style is unsupported."),
        };
    }
}
