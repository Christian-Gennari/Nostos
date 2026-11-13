using System.Net;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// Single-responsibility tests for <see cref="AiProviderModelDiscovery"/>:
/// URL probing, JSON model extraction, client-name selection, and error mapping.
/// </summary>
public sealed class AiProviderModelDiscoveryTests
{
    [Fact]
    public async Task Parses_models_property_names_and_deduplicates_entries()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/models", Json("""{"models":[{"name":"llama3"},{"id":"qwen2.5"},"llama3",""]}"""));

        var discovery = CreateDiscovery(handler);
        var response = await discovery.ListModelsAsync(
            new AiProviderModelsRequest("llm", "http://192.168.1.50:11434", null));

        response.Models.Should().Equal("llama3", "qwen2.5");
    }

    [Fact]
    public async Task Strips_json_base64_transcription_endpoint_before_probing_models()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/models", Json("""{"data":[{"id":"align-1"}]}"""));

        var resolver = new AlignmentResolver(
            new EffectiveAlignmentSttConfig(
                new EffectiveAiProviderConfig(
                    Enabled: true,
                    BaseUrl: "https://transcription.example.test/v4/ai/transcription-model",
                    Model: "align-1",
                    ApiKeyEnvironmentVariable: "NOSTOS_ALIGNMENT_STT_TOKEN",
                    ApiKey: "align-secret",
                    KeyFromServerEnv: false),
                AlignmentTranscriptionApiStyles.JsonBase64,
                UseVoiceTranscription: false));

        var clientFactory = new StubHttpClientFactory(handler);
        var discovery = new AiProviderModelDiscovery(resolver, new AiProviderProbeHttpClient(clientFactory));

        var response = await discovery.ListModelsAsync(
            new AiProviderModelsRequest("alignment-transcription", null, null));

        response.Models.Should().Equal("align-1");
        handler.RecordedRequestPaths.Should().Equal("/models");
        clientFactory.RequestedClientNames.Should().Equal(JsonBase64SttProvider.HttpClientName);
    }

    [Fact]
    public async Task Uses_multipart_stt_client_when_alignment_uses_openai_multipart()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/models", Json("""{"data":[{"id":"whisper-1"}]}"""));

        var resolver = new AlignmentResolver(
            new EffectiveAlignmentSttConfig(
                new EffectiveAiProviderConfig(
                    Enabled: true,
                    BaseUrl: "http://10.0.0.8:8000/v1",
                    Model: "whisper-1",
                    ApiKeyEnvironmentVariable: "NOSTOS_TEST_TOKEN",
                    ApiKey: "voice-secret",
                    KeyFromServerEnv: false),
                AlignmentTranscriptionApiStyles.OpenAiMultipart,
                UseVoiceTranscription: true));

        var clientFactory = new StubHttpClientFactory(handler);
        var discovery = new AiProviderModelDiscovery(resolver, new AiProviderProbeHttpClient(clientFactory));

        var response = await discovery.ListModelsAsync(
            new AiProviderModelsRequest("alignment-transcription", null, null));

        response.Models.Should().Equal("whisper-1");
        clientFactory.RequestedClientNames.Should().Equal(OpenAiCompatibleSttProvider.HttpClientName);
    }

    [Theory]
    [InlineData("unknown-kind", "http://localhost:8000")]
    [InlineData("llm", "ftp://gateway.lan/v1")]
    [InlineData("embedding", "")]
    public async Task Rejects_invalid_kind_or_base_url_with_validation_exception(
        string kind,
        string baseUrl)
    {
        var resolver = new StubAiProviderConfigResolver
        {
            Embedding = new EffectiveAiProviderConfig(true, string.Empty, string.Empty, "ENV", null, false),
        };
        var discovery = CreateDiscovery(new StubHttpMessageHandler(), resolver);

        var act = () => discovery.ListModelsAsync(new AiProviderModelsRequest(kind, baseUrl, null));

        await act.Should().ThrowAsync<AiProviderValidationException>();
    }

    [Fact]
    public async Task Throws_upstream_exception_when_provider_returns_non_model_json()
    {
        var handler = new StubHttpMessageHandler().SetFallback(_ => Json("""{"status":"ok"}"""));
        var discovery = CreateDiscovery(handler);

        var act = () => discovery.ListModelsAsync(
            new AiProviderModelsRequest("llm", "http://127.0.0.1:8080/v1", null));

        var ex = (await act.Should().ThrowAsync<AiProviderUpstreamException>()).Which;
        ex.Message.Should().Contain("did not look like a model list");
    }

    private static AiProviderModelDiscovery CreateDiscovery(
        StubHttpMessageHandler handler,
        IAiProviderConfigResolver? resolver = null) =>
        new(
            resolver ?? new StubAiProviderConfigResolver(),
            new AiProviderProbeHttpClient(new StubHttpClientFactory(handler)));

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class AlignmentResolver(EffectiveAlignmentSttConfig alignment) : IAiProviderConfigResolver
    {
        public Task<EffectiveAiProviderConfig> GetEffectiveLlmAsync(CancellationToken ct = default) =>
            Task.FromResult(alignment.Provider);

        public Task<EffectiveAiProviderConfig> GetEffectiveSttAsync(CancellationToken ct = default) =>
            Task.FromResult(alignment.Provider);

        public Task<EffectiveAlignmentSttConfig> GetEffectiveAlignmentSttAsync(CancellationToken ct = default) =>
            Task.FromResult(alignment);
    }
}
