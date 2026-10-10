using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// Contract tests for the alignment JSON transcription transport.
/// All HTTP traffic is intercepted; no real provider is contacted.
/// </summary>
public sealed class JsonBase64SttProviderTests
{
    private const string Key = "test-alignment-key";
    private const string Model = "test-alignment-model";

    [Fact]
    public async Task Success_sends_base64_wav_and_reads_transcript_metadata()
    {
        var handler = new StubHttpMessageHandler();

        Uri? uri = null;
        string? authorization = null;
        string? model = null;
        string? protocolVersion = null;
        string? specificationVersion = null;
        string? body = null;

        handler.Register("/v4/ai/transcription-model", request =>
        {
            uri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            model = request.Headers.GetValues("ai-model-id").Single();
            protocolVersion = request.Headers.GetValues(
                "ai-gateway-protocol-version").Single();
            specificationVersion = request.Headers.GetValues(
                "ai-transcription-model-specification-version").Single();
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();

            return Json("""
                {
                    "text": " The Magic Mountain ",
                    "language": "en",
                    "durationInSeconds": 19.5
                }
                """);
        });

        var provider = CreateProvider(handler);
        byte[] audio = [82, 73, 70, 70, 1, 2, 3, 4];

        var result = await provider.TranscribeWithConfigAsync(
            new MemoryStream(audio),
            "clip.wav",
            "audio/wav",
            "en",
            Config());

        result.Text.Should().Be("The Magic Mountain");
        result.Language.Should().Be("en");
        result.DurationSeconds.Should().Be(19.5);

        uri!.AbsolutePath.Should().Be("/v4/ai/transcription-model");
        authorization.Should().Be($"Bearer {Key}");
        model.Should().Be(Model);
        protocolVersion.Should().Be("0.0.1");
        specificationVersion.Should().Be("4");

        using var document = JsonDocument.Parse(body!);

        document.RootElement.GetProperty("mediaType")
            .GetString().Should().Be("audio/wav");

        Convert.FromBase64String(
            document.RootElement.GetProperty("audio").GetString()!)
            .Should().Equal(audio);

        handler.RecordedRequests.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SttErrorCodes.Permission)]
    [InlineData(HttpStatusCode.Forbidden, SttErrorCodes.Permission)]
    [InlineData(HttpStatusCode.TooManyRequests, SttErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, SttErrorCodes.TooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, SttErrorCodes.UnsupportedFormat)]
    [InlineData(HttpStatusCode.BadGateway, SttErrorCodes.Provider)]
    [InlineData(HttpStatusCode.InternalServerError, SttErrorCodes.Provider)]
    public async Task Maps_provider_failures_to_typed_errors(
        HttpStatusCode status,
        string expectedCode)
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v4/ai/transcription-model",
            _ => new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    """{"error":{"message":"upstream failure"}}"""),
            });

        var provider = CreateProvider(handler);

        var act = () => provider.TranscribeWithConfigAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            null,
            Config());

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(expectedCode);
        exception.Message.Should().NotContain(Key);
        handler.RecordedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Provider_failure_is_never_retried()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v4/ai/transcription-model",
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var provider = CreateProvider(handler);

        var act = () => provider.TranscribeWithConfigAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            null,
            Config());

        await act.Should().ThrowAsync<SttException>();

        handler.RecordedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Missing_key_prevents_outbound_request()
    {
        var handler = new StubHttpMessageHandler();
        var provider = CreateProvider(handler);

        var act = () => provider.TranscribeWithConfigAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            null,
            Config() with { ApiKey = null });

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(SttErrorCodes.NotConfigured);
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Unsupported_format_prevents_outbound_request()
    {
        var handler = new StubHttpMessageHandler();
        var provider = CreateProvider(handler);

        var act = () => provider.TranscribeWithConfigAsync(
            new MemoryStream([1, 2, 3]),
            "clip.mp3",
            "audio/mpeg",
            null,
            Config());

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(SttErrorCodes.UnsupportedFormat);
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Oversized_audio_is_rejected_before_send()
    {
        var handler = new StubHttpMessageHandler();
        var provider = CreateProvider(handler, maxBytes: 4);

        var act = () => provider.TranscribeWithConfigAsync(
            new MemoryStream([1, 2, 3, 4, 5]),
            "clip.wav",
            "audio/wav",
            null,
            Config());

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(SttErrorCodes.TooLarge);
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"text":null}""")]
    [InlineData("""{"text":123}""")]
    [InlineData("not-json")]
    public async Task Invalid_response_is_a_typed_error(string responseBody)
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v4/ai/transcription-model",
            _ => Json(responseBody));

        var provider = CreateProvider(handler);

        var act = () => provider.TranscribeWithConfigAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            null,
            Config());

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(SttErrorCodes.InvalidResponse);
        handler.RecordedRequests.Should().HaveCount(1);
    }

    private static EffectiveAiProviderConfig Config() => new(
        Enabled: true,
        BaseUrl: "https://transcription.example.test",
        Model: Model,
        ApiKeyEnvironmentVariable: "TEST_ALIGNMENT_TOKEN",
        ApiKey: Key,
        KeyFromServerEnv: false);

    private static JsonBase64SttProvider CreateProvider(
        StubHttpMessageHandler handler,
        long maxBytes = 26_214_400) =>
        new(
            new StubHttpClientFactory(
                handler,
                new Uri("https://transcription.example.test")),
            new SpeechOptions { MaxUploadBytes = maxBytes });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                body, Encoding.UTF8, "application/json"),
        };
}
