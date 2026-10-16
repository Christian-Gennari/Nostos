using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// A scriptable <see cref="ISTtProvider"/> for every test on the transcription
/// path.
///
/// The real Groq provider is never called by a test: each real transcription is
/// paid, and the quota is rationed. This fake reads the audio the same way the
/// real provider does, so an endpoint test can still prove the upload actually
/// reached the provider instead of being buffered somewhere.
/// </summary>
public sealed class FakeSttProvider : ISTtProvider
{
    /// <summary>The canned success returned unless <see cref="Failure"/> is set.</summary>
    public SttResult Result { get; set; } = new("the magic mountain", "en", 6.9);

    /// <summary>When set, thrown instead of returning <see cref="Result"/>.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Hook to block or observe while the "call" is in flight.</summary>
    public Func<CancellationToken, Task>? BeforeRespond { get; set; }

    public int CallCount { get; private set; }
    public string? LastFileName { get; private set; }
    public string? LastContentType { get; private set; }
    public string? LastLanguageHint { get; private set; }
    public long LastByteCount { get; private set; }

    public async Task<SttResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string? contentType,
        string? languageHint,
        CancellationToken ct = default)
    {
        CallCount++;
        LastFileName = fileName;
        LastContentType = contentType;
        LastLanguageHint = languageHint;

        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, ct);
        LastByteCount = buffer.Length;

        if (BeforeRespond is not null)
        {
            await BeforeRespond(ct);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return Result;
    }
}

/// <summary>
/// The fake itself is load-bearing for every other test here, so its contract
/// is asserted rather than assumed.
/// </summary>
public sealed class FakeSttProviderTests
{
    [Fact]
    public async Task Records_the_call_and_returns_the_canned_result()
    {
        var provider = new FakeSttProvider
        {
            Result = new SttResult("a thought", "en", 1.5),
        };

        var audio = new MemoryStream(Encoding.UTF8.GetBytes("not really audio"));
        var result = await provider.TranscribeAsync(audio, "clip.webm", "audio/webm", "en");

        result.Text.Should().Be("a thought");
        provider.CallCount.Should().Be(1);
        provider.LastFileName.Should().Be("clip.webm");
        provider.LastContentType.Should().Be("audio/webm");
        provider.LastLanguageHint.Should().Be("en");
        provider.LastByteCount.Should().Be(audio.Length);
    }

    [Fact]
    public async Task Throws_the_configured_failure()
    {
        var expected = new SttException(SttErrorCodes.Provider, "provider exploded");
        var provider = new FakeSttProvider { Failure = expected };

        var act = () => provider.TranscribeAsync(
            new MemoryStream([1, 2, 3]), "clip.webm", "audio/webm", null);

        (await act.Should().ThrowAsync<SttException>()).Which.Should().BeSameAs(expected);
    }
}

/// <summary>
/// Unit coverage for <see cref="OpenAiCompatibleSttProvider"/> against a canned HTTP
/// response — the transport is stubbed, so no real API is called and no quota
/// is spent. This is where the request contract (exact model id, bearer token)
/// and the response/error mapping are pinned.
/// </summary>
public sealed class OpenAiCompatibleSttProviderTests
{
    private const string Model = "groq/whisper-large-v3-turbo";
    private const string TokenVariable = "NOSTOS_STT_TEST_TOKEN";
    private const string TokenValue = "test-token-not-a-real-secret";

    [Fact]
    public async Task Sends_the_configured_model_verbatim_and_the_bearer_token_from_the_environment()
    {
        var handler = new StubHttpMessageHandler();
        HttpMethod? method = null;
        Uri? uri = null;
        AuthenticationHeaderValue? authorization = null;
        string? body = null;

        handler.Register("/v1/audio/transcriptions", request =>
        {
            // Captured here, at send time: the provider disposes the request
            // once the call returns, so reading its content afterwards throws.
            method = request.Method;
            uri = request.RequestUri;
            authorization = request.Headers.Authorization;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"text\":\" the magic mountain\",\"language\":\"en\",\"duration\":6.9}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        var provider = CreateProvider(handler);

        Environment.SetEnvironmentVariable(TokenVariable, TokenValue);
        try
        {
            var result = await provider.TranscribeAsync(
                new MemoryStream(Encoding.UTF8.GetBytes("fake audio bytes")),
                "clip.mp3",
                "audio/mpeg",
                "en");

            method.Should().Be(HttpMethod.Post);
            uri!.AbsolutePath.Should().Be("/v1/audio/transcriptions");
            authorization!.Scheme.Should().Be("Bearer");
            authorization.Parameter.Should().Be(TokenValue);

            // The groq/ prefix is mandatory; a bare whisper-1 routes elsewhere.
            body.Should().Contain(Model);
            body.Should().Contain("response_format");
            body.Should().Contain("verbose_json");
            body.Should().Contain("clip.mp3");
            body.Should().Contain("fake audio bytes");

            result.Text.Should().Be("the magic mountain");
            result.Language.Should().Be("en");
            result.DurationSeconds.Should().Be(6.9);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    [Fact]
    public async Task Accepts_browser_content_type_with_codec_parameter()
    {
        var handler = new StubHttpMessageHandler();
        string? body = null;
        handler.Register("/v1/audio/transcriptions", request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("{\"text\":\" browser audio\"}");
        });

        var provider = CreateProvider(handler);

        var result = await provider.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            "voice-note.webm",
            "audio/webm;codecs=opus",
            null);

        result.Text.Should().Be("browser audio");
        body.Should().Contain("Content-Type: audio/webm; codecs=opus");
        handler.RecordedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Invalid_browser_content_type_is_a_typed_format_error()
    {
        var handler = new StubHttpMessageHandler();
        var provider = CreateProvider(handler);

        var act = () => provider.TranscribeAsync(
            new MemoryStream([1, 2, 3]),
            "voice-note.webm",
            "not a media type",
            null);

        var exception = (await act.Should().ThrowAsync<SttException>()).Which;
        exception.Code.Should().Be(SttErrorCodes.UnsupportedFormat);
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Trims_the_leading_space_the_provider_returns()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/audio/transcriptions", _ => Json("{\"text\":\" the magic mountain\"}"));

        var provider = CreateProvider(handler);

        Environment.SetEnvironmentVariable(TokenVariable, TokenValue);
        try
        {
            var result = await provider.TranscribeAsync(
                new MemoryStream([1, 2, 3]), "clip.webm", "audio/webm", null);

            // Measured provider behaviour: the text arrives with a leading space.
            result.Text.Should().Be("the magic mountain");
            result.Text.Should().NotStartWith(" ");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    [Fact]
    public async Task Fails_fast_with_a_typed_error_when_the_key_is_absent()
    {
        var handler = new StubHttpMessageHandler();
        var provider = CreateProvider(handler, apiKey: null);

        Environment.SetEnvironmentVariable(TokenVariable, null);
        var act = () => provider.TranscribeAsync(
            new MemoryStream([1, 2, 3]), "clip.webm", "audio/webm", null);

        var exception = (await act.Should().ThrowAsync<SttException>()).Which;
        exception.Code.Should().Be(SttErrorCodes.NotConfigured);

        // No credential means no outbound request at all.
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SttErrorCodes.Permission)]
    [InlineData(HttpStatusCode.Forbidden, SttErrorCodes.Permission)]
    [InlineData(HttpStatusCode.TooManyRequests, SttErrorCodes.Permission)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, SttErrorCodes.TooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, SttErrorCodes.UnsupportedFormat)]
    [InlineData(HttpStatusCode.BadGateway, SttErrorCodes.Provider)]
    [InlineData(HttpStatusCode.InternalServerError, SttErrorCodes.Provider)]
    public async Task Maps_provider_status_codes_to_data(HttpStatusCode status, string expectedCode)
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/audio/transcriptions", _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"nope\"}}", Encoding.UTF8, "application/json"),
        });

        var provider = CreateProvider(handler);

        Environment.SetEnvironmentVariable(TokenVariable, TokenValue);
        try
        {
            var act = () => provider.TranscribeAsync(
                new MemoryStream([1, 2, 3]), "clip.webm", "audio/webm", null);

            var exception = (await act.Should().ThrowAsync<SttException>()).Which;
            exception.Code.Should().Be(expectedCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    [Fact]
    public async Task A_provider_failure_is_attempted_exactly_once_never_retried()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/audio/transcriptions", _ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":{\"message\":\"boom\"}}"),
        });

        var provider = CreateProvider(handler);

        Environment.SetEnvironmentVariable(TokenVariable, TokenValue);
        try
        {
            var act = () => provider.TranscribeAsync(
                new MemoryStream([1, 2, 3]), "clip.webm", "audio/webm", null);

            await act.Should().ThrowAsync<SttException>();

            // Every retry would be another paid call against a rationed quota.
            handler.RecordedRequests.Should().HaveCount(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    [Fact]
    public async Task Cancellation_mid_call_surfaces_as_a_cancellation_not_a_hang()
    {
        var handler = new BlockingHandler();
        var provider = new OpenAiCompatibleSttProvider(
            new SingleHandlerHttpClientFactory(handler),
            TestOptions(),
            new StubAiProviderConfigResolver { Stt = TestConfig(TokenValue) },
            NullLogger<OpenAiCompatibleSttProvider>.Instance);

        Environment.SetEnvironmentVariable(TokenVariable, TokenValue);
        try
        {
            using var cts = new CancellationTokenSource();
            var call = provider.TranscribeAsync(
                new MemoryStream([1, 2, 3]), "clip.webm", "audio/webm", null, cts.Token);

            await handler.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static SpeechOptions TestOptions() => new()
    {
        Enabled = true,
        BaseUrl = "http://omenhub:20128",
        Model = Model,
        ApiKeyEnvironmentVariable = TokenVariable,
    };

    private static EffectiveAiProviderConfig TestConfig(string? apiKey) => new(
        Enabled: true,
        BaseUrl: "http://omenhub:20128",
        Model: Model,
        ApiKeyEnvironmentVariable: TokenVariable,
        ApiKey: apiKey,
        KeyFromServerEnv: apiKey is not null);

    private static OpenAiCompatibleSttProvider CreateProvider(
        StubHttpMessageHandler handler,
        string? apiKey = TokenValue) =>
        new(
            new StubHttpClientFactory(handler, new Uri("http://omenhub:20128")),
            TestOptions(),
            new StubAiProviderConfigResolver { Stt = TestConfig(apiKey) },
            NullLogger<OpenAiCompatibleSttProvider>.Instance);

    /// <summary>A handler that parks until the caller's token is cancelled.</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class SingleHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
