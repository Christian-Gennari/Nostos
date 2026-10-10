namespace Nostos.Backend.Services.Ai;

/// <summary>The provider surfaces the settings page owns.</summary>
public enum AiProviderKind
{
    Llm,
    Stt,
    Embedding,
    AlignmentTranscription,
}

/// <summary>The supported alignment transcription wire formats.</summary>
public static class AlignmentTranscriptionApiStyles
{
    public const string OpenAiMultipart = "openai-multipart";
    public const string JsonBase64 = "json-base64";

    public static bool IsSupported(string? value) =>
        value is OpenAiMultipart or JsonBase64;
}

/// <summary>
/// The effective configuration for one provider surface: the stored override
/// when set, otherwise the host's existing configuration fallback.
/// </summary>
public sealed record EffectiveAiProviderConfig(
    bool Enabled,
    string BaseUrl,
    string Model,
    string ApiKeyEnvironmentVariable,
    string? ApiKey,
    bool KeyFromServerEnv)
{
    /// <summary>A usable credential is present (stored or from the env var).</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// The single derived answer to "can this surface run": enabled, addressed,
    /// and credentialed. Enabling a surface without a key must never throw at
    /// startup.
    /// </summary>
    public bool IsAvailable =>
        Enabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && HasKey;
}

/// <summary>
/// Effective alignment settings, including the wire format and whether the
/// ordinary voice transcription configuration was selected.
/// </summary>
public sealed record EffectiveAlignmentSttConfig(
    EffectiveAiProviderConfig Provider,
    string ApiStyle,
    bool UseVoiceTranscription)
{
    public bool Enabled => Provider.Enabled;
    public bool IsAvailable => Provider.IsAvailable;
    public bool HasKey => Provider.HasKey;
    public string BaseUrl => Provider.BaseUrl;
    public string Model => Provider.Model;
    public string? ApiKey => Provider.ApiKey;
    public string ApiKeyEnvironmentVariable => Provider.ApiKeyEnvironmentVariable;
    public bool KeyFromServerEnv => Provider.KeyFromServerEnv;
}

/// <summary>
/// Read-only access to effective provider configurations.
/// </summary>
public interface IAiProviderConfigResolver
{
    Task<EffectiveAiProviderConfig> GetEffectiveLlmAsync(CancellationToken ct = default);

    Task<EffectiveAiProviderConfig> GetEffectiveSttAsync(CancellationToken ct = default);

    /// <summary>
    /// A host that predates embeddings keeps compiling and reports the surface
    /// as unavailable.
    /// </summary>
    Task<EffectiveAiProviderConfig> GetEffectiveEmbeddingAsync(CancellationToken ct = default) =>
        Task.FromResult(new EffectiveAiProviderConfig(
            Enabled: false,
            BaseUrl: string.Empty,
            Model: string.Empty,
            ApiKeyEnvironmentVariable: string.Empty,
            ApiKey: null,
            KeyFromServerEnv: false));

    /// <summary>
    /// Older hosts transparently use the voice transcription configuration.
    /// </summary>
    async Task<EffectiveAlignmentSttConfig> GetEffectiveAlignmentSttAsync(
        CancellationToken ct = default) =>
        new(
            await GetEffectiveSttAsync(ct),
            AlignmentTranscriptionApiStyles.OpenAiMultipart,
            UseVoiceTranscription: true);
}

/// <summary>
/// A request that fails validation (bad URL, empty model, unknown kind).
/// </summary>
public sealed class AiProviderValidationException(string message) : Exception(message);

/// <summary>
/// The upstream provider could not satisfy a models/test call.
/// </summary>
public sealed class AiProviderUpstreamException(string message) : Exception(message);
