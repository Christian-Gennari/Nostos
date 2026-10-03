namespace Nostos.Backend.Services.Ai;

/// <summary>The provider surfaces the settings page owns.</summary>
public enum AiProviderKind
{
    Llm,
    Stt,
    Embedding,
}

/// <summary>
/// The effective configuration for one provider surface (LLM, STT or embeddings): the
/// stored override when set, otherwise the existing appsettings/env fallback.
///
/// <see cref="IsAvailable"/> is the ONE definition of "this surface can run",
/// mirroring the original <c>AssistantOptions.IsAvailable()</c> rule — the kill
/// switch is on, a base URL is configured, and a key is usable. The key is
/// carried here only so a provider can attach it to an outbound request; it is
/// never returned, logged, or hinted at.
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
    /// startup — it simply reports unavailable here.
    /// </summary>
    public bool IsAvailable =>
        Enabled
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && HasKey;
}

/// <summary>
/// Read-only access to the effective provider configuration. This is the small
/// surface the providers depend on; the full settings service (read/update,
/// model listing, connection tests) extends it. Split out so a provider never
/// has to see the update or secret-handling API.
/// </summary>
public interface IAiProviderConfigResolver
{
    Task<EffectiveAiProviderConfig> GetEffectiveLlmAsync(CancellationToken ct = default);

    Task<EffectiveAiProviderConfig> GetEffectiveSttAsync(CancellationToken ct = default);

    /// <summary>
    /// The embedding surface (issue #683). Defaults to "not configured" so a
    /// host that predates embeddings — or one that supplies them through a
    /// managed <c>IEmbeddingProvider</c> instead of BYOK settings — keeps
    /// compiling and simply reports the surface as unavailable.
    /// </summary>
    Task<EffectiveAiProviderConfig> GetEffectiveEmbeddingAsync(CancellationToken ct = default) =>
        Task.FromResult(new EffectiveAiProviderConfig(
            Enabled: false,
            BaseUrl: string.Empty,
            Model: string.Empty,
            ApiKeyEnvironmentVariable: string.Empty,
            ApiKey: null,
            KeyFromServerEnv: false));
}

/// <summary>
/// A request that fails validation (bad URL, empty model, unknown kind). The
/// endpoint turns it into a 400 with the message.
/// </summary>
public sealed class AiProviderValidationException(string message) : Exception(message);

/// <summary>
/// The upstream provider could not satisfy a models/test call. The endpoint
/// turns it into a 502 carrying the provider's own message.
/// </summary>
public sealed class AiProviderUpstreamException(string message) : Exception(message);
