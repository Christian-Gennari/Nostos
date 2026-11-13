using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// Single-responsibility tests for <see cref="AiProviderConnectionTester"/>:
/// live probes across all five provider surfaces, shared HTTP helper usage
/// including <see cref="HttpCompletionOption.ResponseHeadersRead"/> for TTS,
/// and injected alignment STT transports.
/// </summary>
public sealed class AiProviderConnectionTesterTests : IDisposable
{
    private readonly SqliteTestFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Tts_probe_completes_on_headers_without_reading_body_and_accepts_audio()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/audio/speech", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UnreadableBodyStream()),
            };
            response.Content.Headers.ContentType = new("audio/mpeg");
            return response;
        });

        var clientFactory = new StubHttpClientFactory(handler);
        var tester = CreateTester(clientFactory);

        var result = await tester.TestAsync(
            new AiProviderTestRequest("tts", "http://192.168.1.30:8880", "kokoro", null));

        result.Ok.Should().BeTrue();
        result.Detail.Should().Be("kokoro answered with audio.");
        clientFactory.RequestedClientNames.Should().Equal(OpenAiCompatibleReadAloudProvider.HttpClientName);
    }

    [Fact]
    public async Task Tts_probe_reports_status_code_without_exposing_raw_exception()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/audio/speech", _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var tester = CreateTester(new StubHttpClientFactory(handler));

        var result = await tester.TestAsync(
            new AiProviderTestRequest("tts", "http://kokoro.lan:8880", "kokoro", null));

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("The speech server answered 502.");
    }

    [Fact]
    public async Task Tts_probe_reports_unreachable_when_transport_throws()
    {
        var tester = CreateTester(new ThrowingHttpClientFactory(new HttpRequestException("refused")));

        var result = await tester.TestAsync(
            new AiProviderTestRequest("tts", "http://kokoro.lan:8880", "kokoro", null));

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("The speech server could not be reached.");
    }

    [Fact]
    public async Task Missing_model_returns_ok_false_without_sending_http_request()
    {
        var handler = new StubHttpMessageHandler();
        var tester = CreateTester(new StubHttpClientFactory(handler));

        var result = await tester.TestAsync(
            new AiProviderTestRequest("llm", "http://127.0.0.1:11434/v1", "", "sk-key"));

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("LLM model is not configured.");
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Alignment_probe_rejects_unsupported_api_style()
    {
        var handler = new StubHttpMessageHandler();
        var tester = CreateTester(new StubHttpClientFactory(handler));

        var result = await tester.TestAsync(
            new AiProviderTestRequest(
                "alignment-transcription",
                "http://align.lan:9000",
                "align-model",
                "align-key",
                ApiStyle: "custom-unknown",
                UseVoiceTranscription: false));

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("Unsupported alignment transcription API style.");
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown_instead_of_masked_as_ok_false()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var tester = CreateTester(new ThrowingHttpClientFactory(new OperationCanceledException(cts.Token)));

        var act = () => tester.TestAsync(
            new AiProviderTestRequest("llm", "http://127.0.0.1:11434/v1", "llama3", "sk-key"),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Llm_probe_accepts_trusted_lan_http_endpoint_and_reports_truncated_non_json_error()
    {
        var longBody = new string('e', 350);
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/chat/completions", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(longBody, Encoding.UTF8, "text/plain"),
        });

        var tester = CreateTester(new StubHttpClientFactory(handler));

        var result = await tester.TestAsync(
            new AiProviderTestRequest("llm", "http://192.168.1.99:20128/v1", "local-model", null));

        result.Ok.Should().BeFalse();
        result.Error.Should().Be($"HTTP 500: {longBody[..300]}");
    }

    private AiProviderConnectionTester CreateTester(IHttpClientFactory httpClientFactory)
    {
        var dbPath = _fixture.CreateDatabasePath();
        using (var _ = _fixture.CreateContext(dbPath))
        {
        }

        var speechOptions = new SpeechOptions();
        var store = new AiProviderSettingsStore(
            new TestDbContextFactory(dbPath),
            new AssistantOptions(),
            speechOptions,
            new EmbeddingOptions(),
            new EphemeralDataProtectionProvider(),
            NullLogger.Instance,
            new ReadAloudOptions());

        var probeHttpClient = new AiProviderProbeHttpClient(httpClientFactory);
        var multipartStt = new OpenAiCompatibleSttProvider(
            httpClientFactory,
            speechOptions,
            store,
            NullLogger<OpenAiCompatibleSttProvider>.Instance);
        var jsonStt = new JsonBase64SttProvider(httpClientFactory, speechOptions, store);

        return new AiProviderConnectionTester(store, probeHttpClient, multipartStt, jsonStt);
    }

    private sealed class TestDbContextFactory(string path) : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options);

        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class UnreadableBodyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("TTS probe must not read the response body.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("TTS probe must not read the response body.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingHttpClientFactory(Exception exception) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new ThrowingHandler(exception), disposeHandler: false);

        private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromException<HttpResponseMessage>(ex);
        }
    }
}
