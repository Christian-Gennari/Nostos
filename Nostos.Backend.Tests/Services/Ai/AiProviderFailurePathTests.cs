using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// The same transport failures, over a real socket, for the embedding, speech-
/// to-text (voice and alignment) and read-aloud providers (issue #1466). Whatever the endpoint does
/// — nothing listening, never answering, stalling after the headers, dropping
/// the connection, rate limiting — the provider ends with its own typed
/// exception within its client timeout. A raw transport exception would reach
/// the endpoint as a 500; a hang would hold a request or a background job.
/// </summary>
public sealed class AiProviderFailurePathTests : IDisposable
{
    private const string Key = "sk-provider-sentinel-not-a-secret";
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(2);

    private readonly string _cacheDirectory =
        Path.Combine(Path.GetTempPath(), $"nostos-failure-path-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
            Directory.Delete(_cacheDirectory, recursive: true);
    }

    public static TheoryData<string> Providers => new() { "embedding", "stt", "alignment-stt", "read-aloud" };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Nothing_listening_is_a_provider_failure(string provider)
    {
        var code = await FailureCodeAsync(provider, $"http://127.0.0.1:{LoopbackHttpServer.ClosedPort()}/v1");

        code.Should().Be(Codes(provider).Provider);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_server_that_never_answers_times_out(string provider)
    {
        await using var server = LoopbackHttpServer.Start((_, _, ct) => Task.Delay(Timeout.Infinite, ct));

        var code = await FailureCodeAsync(provider, server.BaseUrl);

        code.Should().Be(Codes(provider).Timeout);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_body_that_stalls_after_the_headers_times_out(string provider)
    {
        await using var server = LoopbackHttpServer.Start(async (_, stream, ct) =>
        {
            await LoopbackHttpServer.WritePartialAsync(stream, 500, "{\"da", ct);
            await Task.Delay(Timeout.Infinite, ct);
        });

        var code = await FailureCodeAsync(provider, server.BaseUrl);

        code.Should().Be(Codes(provider).Timeout);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task A_connection_dropped_mid_body_is_a_provider_failure(string provider)
    {
        await using var server = LoopbackHttpServer.Start(
            (_, stream, ct) => LoopbackHttpServer.WritePartialAsync(stream, 500, "{\"da", ct));

        var code = await FailureCodeAsync(provider, server.BaseUrl);

        code.Should().Be(Codes(provider).Provider);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Rate_limiting_is_reported_as_rate_limiting(string provider)
    {
        await using var server = LoopbackHttpServer.Responding(429, """{"error":{"message":"slow down"}}""");

        var code = await FailureCodeAsync(provider, server.BaseUrl);

        code.Should().Be(Codes(provider).RateLimited);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task The_callers_cancellation_stays_a_cancellation(string provider)
    {
        await using var server = LoopbackHttpServer.Start((_, _, ct) => Task.Delay(Timeout.Infinite, ct));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = () => CallAsync(provider, server.BaseUrl, TimeSpan.FromSeconds(30), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed record ProviderCodes(string Provider, string Timeout, string RateLimited);

    private static ProviderCodes Codes(string provider) => provider switch
    {
        "embedding" => new(EmbeddingErrorCodes.Provider, EmbeddingErrorCodes.Timeout, EmbeddingErrorCodes.RateLimited),
        "stt" or "alignment-stt" => new(SttErrorCodes.Provider, SttErrorCodes.Timeout, SttErrorCodes.RateLimited),
        _ => new(ReadAloudErrorCodes.Provider, ReadAloudErrorCodes.Timeout, ReadAloudErrorCodes.RateLimited),
    };

    /// <summary>The typed code the call failed with; a hang or an untyped exception fails the test.</summary>
    private async Task<string> FailureCodeAsync(string provider, string baseUrl)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var call = CallAsync(provider, baseUrl, ClientTimeout, CancellationToken.None);
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(20)));
        finished.Should().BeSameAs(call, $"the {provider} provider must fail, not hang");
        started.Elapsed.Should().BeLessThan(ClientTimeout * 4);

        try
        {
            await call;
        }
        catch (EmbeddingException ex)
        {
            return ex.Code;
        }
        catch (SttException ex)
        {
            return ex.Code;
        }
        catch (ReadAloudException ex)
        {
            return ex.Code;
        }

        throw new Xunit.Sdk.XunitException($"the {provider} provider succeeded against a failing endpoint");
    }

    private Task CallAsync(string provider, string baseUrl, TimeSpan timeout, CancellationToken ct)
    {
        var factory = new TimeoutHttpClientFactory(timeout);
        var config = new EffectiveAiProviderConfig(
            Enabled: true,
            BaseUrl: baseUrl,
            Model: "test-model",
            ApiKeyEnvironmentVariable: "NOSTOS_TEST_TOKEN",
            ApiKey: Key,
            KeyFromServerEnv: false);
        switch (provider)
        {
            case "embedding":
                return new OpenAiCompatibleEmbeddingProvider(
                        factory,
                        new StubAiProviderConfigResolver { Embedding = config },
                        NullLogger<OpenAiCompatibleEmbeddingProvider>.Instance)
                    .EmbedAsync(["text"], ct);
            case "stt":
                return new OpenAiCompatibleSttProvider(
                        factory,
                        new SpeechOptions(),
                        new StubAiProviderConfigResolver { Stt = config },
                        NullLogger<OpenAiCompatibleSttProvider>.Instance)
                    .TranscribeAsync(new MemoryStream([1, 2, 3]), "a.webm", "audio/webm", null, ct);
            case "alignment-stt":
                return new JsonBase64SttProvider(factory, new SpeechOptions())
                    .TranscribeWithConfigAsync(
                        new MemoryStream([1, 2, 3]), "a.wav", "audio/wav", null, config, ct);
            default:
                var options = new ReadAloudOptions { CacheDirectory = _cacheDirectory };
                return new OpenAiCompatibleReadAloudProvider(
                        factory,
                        options,
                        new StubAiProviderConfigResolver { Tts = config },
                        new ReadAloudAudioCache(options, NullLogger<ReadAloudAudioCache>.Instance),
                        NullLogger<OpenAiCompatibleReadAloudProvider>.Instance)
                    .SynthesizeAsync(new ReadAloudSpeechRequest("A sentence.", "af_heart"), ct);
        }
    }
}
