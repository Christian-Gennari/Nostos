using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// Failure paths of the Ask Nostos chat provider against a real socket
/// (issue #1466). Every way an OpenAI-compatible endpoint can fail, including
/// a SelfHosted base URL that points at the wrong thing, must leave the
/// provider as a typed <see cref="LlmException"/>, never a raw transport
/// exception the endpoint would turn into a 500, and never a hang. Status
/// mapping over a stubbed handler lives in FakeLlmProviderTests.cs.
/// </summary>
public sealed class OpenAiCompatibleLlmProviderFailurePathTests
{
    private const string Key = "sk-llm-sentinel-not-a-secret";
    private const string Ok =
        """{"choices":[{"message":{"role":"assistant","content":"Hello."},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":1}}""";

    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Posts_to_chat_completions_with_the_bearer_key_and_reads_the_answer()
    {
        await using var server = LoopbackHttpServer.Responding(200, Ok);

        var completion = await Provider(server.BaseUrl).CompleteAsync(Request());

        completion.Content.Should().Be("Hello.");
        completion.PromptTokens.Should().Be(5);
        server.LastRequest!.RequestLine.Should().StartWith("POST /v1/chat/completions ");
        server.LastRequest.Authorization.Should().Be($"Bearer {Key}");
        server.LastRequest.Body.Should().Contain("\"model\":\"test-model\"");
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:1/v1", "test-model")]
    [InlineData(Key, "", "test-model")]
    [InlineData(Key, "http://127.0.0.1:1/v1", " ")]
    public async Task Missing_key_address_or_model_is_not_configured_and_sends_nothing(
        string? key, string baseUrl, string model)
    {
        await using var server = LoopbackHttpServer.Responding(200, Ok);
        var provider = Provider(
            string.IsNullOrEmpty(baseUrl) ? baseUrl : server.BaseUrl, key: key, model: model);

        var failure = await FailureOf(provider);

        failure.Code.Should().Be(LlmErrorCodes.NotConfigured);
        server.Requests.Should().Be(0);
    }

    [Fact]
    public async Task A_base_url_without_the_api_path_is_a_provider_failure_that_names_the_status()
    {
        // SelfHosted misconfiguration: the owner pasted the server's root, so
        // /chat/completions lands on the server's own 404 page.
        await using var server = LoopbackHttpServer.Responding(
            404, "<html><body><h1>404</h1>" + new string('x', 2000) + "</body></html>", "text/html");

        var failure = await FailureOf(Provider(server.BaseUrl));

        failure.Code.Should().Be(LlmErrorCodes.Provider);
        failure.Message.Should().Contain("HTTP 404");
        // A web page body is cut short, never pasted whole into an error.
        failure.Message.Length.Should().BeLessThan(400);
    }

    [Theory]
    [InlineData("<!doctype html><html><body>Open WebUI</body></html>")]
    [InlineData("""{"object":"list","data":[]}""")]
    [InlineData("""{"choices":[]}""")]
    public async Task A_success_status_with_a_body_that_is_not_a_completion_is_an_invalid_response(string body)
    {
        // SelfHosted misconfiguration: the base URL points at a web UI or at
        // another API that answers 200 with something else.
        await using var server = LoopbackHttpServer.Responding(200, body);

        var failure = await FailureOf(Provider(server.BaseUrl));

        failure.Code.Should().Be(LlmErrorCodes.InvalidResponse);
    }

    [Fact]
    public async Task Nothing_listening_at_the_address_is_a_provider_failure()
    {
        var provider = Provider($"http://127.0.0.1:{LoopbackHttpServer.ClosedPort()}/v1");

        var failure = await FailureOf(provider);

        failure.Code.Should().Be(LlmErrorCodes.Provider);
        failure.Message.Should().Contain("could not be reached");
    }

    [Fact]
    public async Task A_server_that_never_answers_times_out_within_the_client_timeout()
    {
        await using var server = LoopbackHttpServer.Start(
            (_, _, ct) => Task.Delay(Timeout.Infinite, ct));
        var started = System.Diagnostics.Stopwatch.StartNew();

        var failure = await FailureOf(Provider(server.BaseUrl));

        failure.Code.Should().Be(LlmErrorCodes.Timeout);
        started.Elapsed.Should().BeLessThan(ClientTimeout * 4);
    }

    [Fact]
    public async Task A_body_that_stalls_after_the_headers_times_out_instead_of_hanging()
    {
        // Headers arrive at once, then the gateway stops sending. The client
        // timeout must cover the body too: callers such as the note processor
        // pass no deadline of their own.
        await using var server = LoopbackHttpServer.Start(async (_, stream, ct) =>
        {
            await LoopbackHttpServer.WritePartialAsync(stream, Ok.Length, Ok[..10], ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        var started = System.Diagnostics.Stopwatch.StartNew();

        var failure = await FailureOf(Provider(server.BaseUrl), within: TimeSpan.FromSeconds(30));

        failure.Code.Should().Be(LlmErrorCodes.Timeout);
        started.Elapsed.Should().BeLessThan(ClientTimeout * 4);
    }

    [Fact]
    public async Task A_connection_dropped_mid_body_is_a_provider_failure()
    {
        await using var server = LoopbackHttpServer.Start(
            (_, stream, ct) => LoopbackHttpServer.WritePartialAsync(stream, Ok.Length, Ok[..10], ct));

        var failure = await FailureOf(Provider(server.BaseUrl));

        failure.Code.Should().Be(LlmErrorCodes.Provider);
    }

    [Fact]
    public async Task The_callers_cancellation_stays_a_cancellation()
    {
        await using var server = LoopbackHttpServer.Start(
            (_, _, ct) => Task.Delay(Timeout.Infinite, ct));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = () => Provider(server.BaseUrl, timeout: TimeSpan.FromSeconds(30))
            .CompleteAsync(Request(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static LlmCompletionRequest Request() =>
        new([LlmMessage.User("Hi")], [], 64);

    private static OpenAiCompatibleLlmProvider Provider(
        string baseUrl, string? key = Key, string model = "test-model", TimeSpan? timeout = null) =>
        new(
            new TimeoutHttpClientFactory(timeout ?? ClientTimeout),
            new StubAiProviderConfigResolver
            {
                Llm = new EffectiveAiProviderConfig(
                    Enabled: true,
                    BaseUrl: baseUrl,
                    Model: model,
                    ApiKeyEnvironmentVariable: "NOSTOS_TEST_TOKEN",
                    ApiKey: key,
                    KeyFromServerEnv: false),
            },
            NullLogger<OpenAiCompatibleLlmProvider>.Instance);

    /// <summary>
    /// Runs one completion that must fail typed. A hang fails the test after
    /// <paramref name="within"/> instead of blocking the run.
    /// </summary>
    private static async Task<LlmException> FailureOf(
        OpenAiCompatibleLlmProvider provider, TimeSpan? within = null)
    {
        var call = provider.CompleteAsync(Request());
        var finished = await Task.WhenAny(call, Task.Delay(within ?? TimeSpan.FromSeconds(20)));
        finished.Should().BeSameAs(call, "the provider must fail, not hang");
        var act = () => call;
        return (await act.Should().ThrowAsync<LlmException>()).Which;
    }
}
