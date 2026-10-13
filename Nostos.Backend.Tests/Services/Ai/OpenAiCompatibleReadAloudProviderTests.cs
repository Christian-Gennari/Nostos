using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// The SelfHosted server voice against a stubbed speech server. No real server
/// is called; the route, the request body, the failure mapping and the cache
/// are what a self-hoster's setup depends on.
/// </summary>
public sealed class OpenAiCompatibleReadAloudProviderTests : IDisposable
{
    private readonly string cacheDirectory =
        Path.Combine(Path.GetTempPath(), $"nostos-read-aloud-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(cacheDirectory))
            Directory.Delete(cacheDirectory, recursive: true);
    }

    [Theory]
    [InlineData("http://localhost:8880", "http://localhost:8880/v1/audio/speech")]
    [InlineData("http://localhost:8880/", "http://localhost:8880/v1/audio/speech")]
    [InlineData("http://localhost:8880/v1", "http://localhost:8880/v1/audio/speech")]
    [InlineData("https://api.example.com/v1/openai", "https://api.example.com/v1/openai/audio/speech")]
    [InlineData("https://api.example.com/openai", "https://api.example.com/openai/v1/audio/speech")]
    public void The_speech_route_follows_the_base_url(string baseUrl, string expected)
    {
        OpenAiCompatibleReadAloudProvider.SpeechUri(baseUrl)!.AbsoluteUri.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:8880")]
    [InlineData("ftp://example.com")]
    public void An_unusable_base_url_has_no_route(string baseUrl)
    {
        OpenAiCompatibleReadAloudProvider.SpeechUri(baseUrl).Should().BeNull();
    }

    [Fact]
    public async Task Availability_needs_the_switch_and_an_address_but_no_key()
    {
        (await Create(new StubHandler(), enabled: false).GetAvailabilityAsync()).Available.Should().BeFalse();
        (await Create(new StubHandler(), baseUrl: "").GetAvailabilityAsync()).Available.Should().BeFalse();

        var ready = await Create(new StubHandler()).GetAvailabilityAsync();
        ready.Available.Should().BeTrue();
        ready.Managed.Should().BeFalse();
    }

    [Fact]
    public async Task Sends_one_openai_speech_request_at_normal_speed_as_mp3()
    {
        var handler = new StubHandler();
        var provider = Create(handler);

        var audio = await provider.SynthesizeAsync(new ReadAloudSpeechRequest("Mr. Bennet made no answer.", "bm_george"));

        audio.ContentType.Should().Be("audio/mpeg");
        audio.Audio.Should().Equal(StubHandler.Mp3);
        handler.Calls.Should().Be(1);
        handler.LastUri.Should().Be("http://speech.invalid/v1/audio/speech");
        handler.LastAuthorization.Should().BeNull("a local speech server needs no key");

        var body = JsonDocument.Parse(handler.LastBody!).RootElement;
        body.GetProperty("model").GetString().Should().Be("kokoro");
        body.GetProperty("input").GetString().Should().Be("Mr. Bennet made no answer.");
        body.GetProperty("voice").GetString().Should().Be("bm_george");
        body.GetProperty("response_format").GetString().Should().Be("mp3");
        body.GetProperty("speed").GetDouble().Should().Be(1.0);
    }

    [Fact]
    public async Task The_same_sentence_in_the_same_voice_is_rendered_once()
    {
        var handler = new StubHandler();
        var provider = Create(handler);
        var request = new ReadAloudSpeechRequest("Mr. Bennet made no answer.", "bm_george");

        await provider.SynthesizeAsync(request);
        var again = await provider.SynthesizeAsync(request);
        await provider.SynthesizeAsync(request with { Voice = "bf_emma" });

        again.Audio.Should().Equal(StubHandler.Mp3);
        handler.Calls.Should().Be(2, "only a different voice is a different rendering");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ReadAloudErrorCodes.Permission)]
    [InlineData(HttpStatusCode.TooManyRequests, ReadAloudErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.GatewayTimeout, ReadAloudErrorCodes.Timeout)]
    [InlineData(HttpStatusCode.InternalServerError, ReadAloudErrorCodes.Provider)]
    public async Task A_failed_call_is_a_typed_failure_and_is_not_cached(HttpStatusCode status, string code)
    {
        var handler = new StubHandler { Status = status };
        var provider = Create(handler);
        var request = new ReadAloudSpeechRequest("Mr. Bennet made no answer.", "bm_george");

        var failure = await provider.Invoking(p => p.SynthesizeAsync(request))
            .Should().ThrowAsync<ReadAloudException>();
        failure.Which.Code.Should().Be(code);

        handler.Status = HttpStatusCode.OK;
        (await provider.SynthesizeAsync(request)).Audio.Should().Equal(StubHandler.Mp3);
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_body_that_is_not_audio_is_refused()
    {
        var handler = new StubHandler { ContentType = "application/json" };
        var provider = Create(handler);

        var failure = await provider
            .Invoking(p => p.SynthesizeAsync(new ReadAloudSpeechRequest("One.", "af_heart")))
            .Should().ThrowAsync<ReadAloudException>();
        failure.Which.Code.Should().Be(ReadAloudErrorCodes.InvalidResponse);
    }

    [Fact]
    public async Task An_unreachable_server_is_a_provider_failure()
    {
        var handler = new StubHandler { Throw = new HttpRequestException("connection refused") };
        var provider = Create(handler);

        var failure = await provider
            .Invoking(p => p.SynthesizeAsync(new ReadAloudSpeechRequest("One.", "af_heart")))
            .Should().ThrowAsync<ReadAloudException>();
        failure.Which.Code.Should().Be(ReadAloudErrorCodes.Provider);
    }

    [Fact]
    public async Task The_cache_drops_what_was_read_longest_ago_when_it_is_over_its_limit()
    {
        var options = Options(cacheMaxBytes: 250);
        var cache = new ReadAloudAudioCache(options, NullLogger<ReadAloudAudioCache>.Instance);
        var audio = new ReadAloudAudio(new byte[100], "audio/mpeg");

        await cache.SetAsync("oldest", audio);
        await cache.SetAsync("middle", audio);
        await cache.SetAsync("newest", audio);
        File.SetLastWriteTimeUtc(Path.Combine(cacheDirectory, "oldest.audio"), DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(Path.Combine(cacheDirectory, "middle.audio"), DateTime.UtcNow.AddHours(-1));

        await cache.TrimAsync();

        (await cache.TryGetAsync("oldest")).Should().BeNull();
        (await cache.TryGetAsync("middle")).Should().NotBeNull();
        (await cache.TryGetAsync("newest")).Should().NotBeNull();
    }

    private ReadAloudOptions Options(
        bool enabled = true,
        string baseUrl = "http://speech.invalid",
        long cacheMaxBytes = 1_000_000) =>
        new()
        {
            Enabled = enabled,
            BaseUrl = baseUrl,
            Model = "kokoro",
            ApiKeyEnvironmentVariable = "NOSTOS_READ_ALOUD_PROVIDER_TEST_TOKEN_UNSET",
            CacheDirectory = cacheDirectory,
            CacheMaxBytes = cacheMaxBytes,
        };

    private OpenAiCompatibleReadAloudProvider Create(
        StubHandler handler,
        bool enabled = true,
        string baseUrl = "http://speech.invalid")
    {
        var options = Options(enabled, baseUrl);
        return new OpenAiCompatibleReadAloudProvider(
            new StubHttpClientFactory(handler),
            options,
            new ReadAloudAudioCache(options, NullLogger<ReadAloudAudioCache>.Instance),
            NullLogger<OpenAiCompatibleReadAloudProvider>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public static readonly byte[] Mp3 = [0x49, 0x44, 0x33, 0x04, 0x00, 0x00];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ContentType { get; set; } = "audio/mpeg";
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri!.AbsoluteUri;
            LastAuthorization = request.Headers.Authorization;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Throw is not null)
                throw Throw;

            var response = new HttpResponseMessage(Status) { Content = new ByteArrayContent(Mp3) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
            return response;
        }
    }
}
