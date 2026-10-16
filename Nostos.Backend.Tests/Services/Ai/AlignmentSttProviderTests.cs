using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// Verifies that alignment transcription resolves settings per call and
/// dispatches to exactly one transport without changing voice transcription.
/// All outbound requests are intercepted by a fake HTTP handler.
/// </summary>
public sealed class AlignmentSttProviderTests
{
    private const string VoiceKey = "voice-test-key";
    private const string AlignmentKey = "alignment-test-key";

    [Fact]
    public async Task Uses_voice_multipart_when_voice_transcription_is_selected()
    {
        var handler = CreateHandler();
        var resolver = new TestResolver
        {
            Alignment = new EffectiveAlignmentSttConfig(
                VoiceConfig(),
                AlignmentTranscriptionApiStyles.OpenAiMultipart,
                UseVoiceTranscription: true),
        };

        var provider = CreateProvider(handler, resolver);

        var result = await provider.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            "en");

        result.Text.Should().Be("A passage");
        handler.RecordedRequestPaths.Should()
            .Equal("/v1/audio/transcriptions");

        handler.RecordedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Uses_json_transport_when_dedicated_configuration_is_selected()
    {
        var handler = CreateHandler();
        var resolver = new TestResolver
        {
            Alignment = new EffectiveAlignmentSttConfig(
                AlignmentConfig(),
                AlignmentTranscriptionApiStyles.JsonBase64,
                UseVoiceTranscription: false),
        };

        var provider = CreateProvider(handler, resolver);

        var result = await provider.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            "en");

        result.Text.Should().Be("A passage");
        handler.RecordedRequestPaths.Should()
            .Equal("/v4/ai/transcription-model");

        handler.RecordedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Configuration_is_resolved_again_for_each_call()
    {
        var handler = CreateHandler();
        var resolver = new TestResolver
        {
            Alignment = new EffectiveAlignmentSttConfig(
                VoiceConfig(),
                AlignmentTranscriptionApiStyles.OpenAiMultipart,
                UseVoiceTranscription: true),
        };

        var provider = CreateProvider(handler, resolver);

        var first = await provider.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            "first.wav",
            "audio/wav",
            null);

        resolver.Alignment = new EffectiveAlignmentSttConfig(
            AlignmentConfig(),
            AlignmentTranscriptionApiStyles.JsonBase64,
            UseVoiceTranscription: false);

        var second = await provider.TranscribeAsync(
            new MemoryStream([4, 5, 6]),
            "second.wav",
            "audio/wav",
            null);

        first.Text.Should().Be("A passage");
        second.Text.Should().Be("A passage");

        resolver.AlignmentResolutionCount.Should().Be(2);

        // The stub handler does not record in call order.
        handler.RecordedRequestPaths.Should().BeEquivalentTo(
            "/v1/audio/transcriptions",
            "/v4/ai/transcription-model");

        handler.RecordedRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Disabled_alignment_does_not_call_either_transport()
    {
        var handler = CreateHandler();
        var resolver = new TestResolver
        {
            Alignment = new EffectiveAlignmentSttConfig(
                AlignmentConfig() with { Enabled = false },
                AlignmentTranscriptionApiStyles.JsonBase64,
                UseVoiceTranscription: false),
        };

        var provider = CreateProvider(handler, resolver);

        var act = () => provider.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            null);

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(SttErrorCodes.Disabled);
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_alignment_key_does_not_call_either_transport()
    {
        var handler = CreateHandler();
        var resolver = new TestResolver
        {
            Alignment = new EffectiveAlignmentSttConfig(
                AlignmentConfig() with { ApiKey = null },
                AlignmentTranscriptionApiStyles.JsonBase64,
                UseVoiceTranscription: false),
        };

        var provider = CreateProvider(handler, resolver);

        var act = () => provider.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            null);

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(SttErrorCodes.NotConfigured);
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Unsupported_api_style_fails_without_sending_audio()
    {
        var handler = CreateHandler();
        var resolver = new TestResolver
        {
            Alignment = new EffectiveAlignmentSttConfig(
                AlignmentConfig(),
                "unsupported",
                UseVoiceTranscription: false),
        };

        var provider = CreateProvider(handler, resolver);

        var act = () => provider.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            null);

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(SttErrorCodes.InvalidRequest);
        handler.RecordedRequests.Should().BeEmpty();
    }

    private static AlignmentSttProvider CreateProvider(
        StubHttpMessageHandler handler,
        TestResolver resolver)
    {
        var factory = new StubHttpClientFactory(
            handler,
            new Uri("https://transcription.example.test"));

        var options = new SpeechOptions
        {
            Enabled = true,
            MaxUploadBytes = 26_214_400,
        };

        var multipart = new OpenAiCompatibleSttProvider(
            factory,
            options,
            resolver,
            NullLogger<OpenAiCompatibleSttProvider>.Instance);

        var json = new JsonBase64SttProvider(
            factory,
            options,
            resolver);

        return new AlignmentSttProvider(
            resolver,
            multipart,
            json);
    }

    private static StubHttpMessageHandler CreateHandler()
    {
        var handler = new StubHttpMessageHandler();

        handler.Register("/v1/audio/transcriptions", _ =>
            Json("""{"text":" A passage","language":"en","duration":2}"""));

        handler.Register("/v4/ai/transcription-model", _ =>
            Json("""
                {
                    "text": " A passage ",
                    "language": "en",
                    "durationInSeconds": 2
                }
                """));

        return handler;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                body,
                Encoding.UTF8,
                "application/json"),
        };

    private static EffectiveAiProviderConfig VoiceConfig() => new(
        Enabled: true,
        BaseUrl: "https://voice.example.test",
        Model: "voice-test-model",
        ApiKeyEnvironmentVariable: "TEST_VOICE_TOKEN",
        ApiKey: VoiceKey,
        KeyFromServerEnv: false);

    private static EffectiveAiProviderConfig AlignmentConfig() => new(
        Enabled: true,
        BaseUrl: "https://transcription.example.test",
        Model: "alignment-test-model",
        ApiKeyEnvironmentVariable: "TEST_ALIGNMENT_TOKEN",
        ApiKey: AlignmentKey,
        KeyFromServerEnv: false);

    private sealed class TestResolver : IAiProviderConfigResolver
    {
        public EffectiveAlignmentSttConfig Alignment { get; set; } =
            new(
                VoiceConfig(),
                AlignmentTranscriptionApiStyles.OpenAiMultipart,
                UseVoiceTranscription: true);

        public int AlignmentResolutionCount { get; private set; }

        public Task<EffectiveAiProviderConfig> GetEffectiveLlmAsync(
            CancellationToken ct = default) =>
            Task.FromResult(VoiceConfig());

        public Task<EffectiveAiProviderConfig> GetEffectiveSttAsync(
            CancellationToken ct = default) =>
            Task.FromResult(VoiceConfig());

        public Task<EffectiveAiProviderConfig> GetEffectiveEmbeddingAsync(
            CancellationToken ct = default) =>
            Task.FromResult(VoiceConfig() with { Enabled = false });

        public Task<EffectiveAlignmentSttConfig> GetEffectiveAlignmentSttAsync(
            CancellationToken ct = default)
        {
            AlignmentResolutionCount++;
            return Task.FromResult(Alignment);
        }
    }
}
