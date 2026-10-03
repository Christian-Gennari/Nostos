using Nostos.Backend.Services.Ai;

namespace Nostos.Backend.Tests.Support;

/// <summary>
/// A fixed <see cref="IAiProviderConfigResolver"/> for provider unit tests: the
/// real resolver reads the database and the environment, which a transport test
/// does not need. The defaults are "enabled, addressed, no key" so a test opts
/// into a credential explicitly.
/// </summary>
internal sealed class StubAiProviderConfigResolver : IAiProviderConfigResolver
{
    public EffectiveAiProviderConfig Llm { get; set; } = new(
        Enabled: true,
        BaseUrl: "http://omenhub:20128/v1",
        Model: "test-model",
        ApiKeyEnvironmentVariable: "NOSTOS_TEST_TOKEN",
        ApiKey: null,
        KeyFromServerEnv: false);

    public EffectiveAiProviderConfig Stt { get; set; } = new(
        Enabled: true,
        BaseUrl: "http://omenhub:20128",
        Model: "groq/whisper-large-v3-turbo",
        ApiKeyEnvironmentVariable: "NOSTOS_TEST_TOKEN",
        ApiKey: null,
        KeyFromServerEnv: false);

    public EffectiveAiProviderConfig Embedding { get; set; } = new(
        Enabled: true,
        BaseUrl: "https://ai-gateway.vercel.sh/v1",
        Model: "alibaba/qwen3-embedding-0-6b",
        ApiKeyEnvironmentVariable: "NOSTOS_TEST_TOKEN",
        ApiKey: null,
        KeyFromServerEnv: false);

    public Task<EffectiveAiProviderConfig> GetEffectiveLlmAsync(CancellationToken ct = default) =>
        Task.FromResult(Llm);

    public Task<EffectiveAiProviderConfig> GetEffectiveSttAsync(CancellationToken ct = default) =>
        Task.FromResult(Stt);

    public Task<EffectiveAiProviderConfig> GetEffectiveEmbeddingAsync(CancellationToken ct = default) =>
        Task.FromResult(Embedding);
}
