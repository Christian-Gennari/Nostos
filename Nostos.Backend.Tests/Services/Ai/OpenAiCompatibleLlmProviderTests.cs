using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// Unit coverage for <see cref="OpenAiCompatibleLlmProvider"/> against a canned HTTP
/// response — the transport is stubbed, so no real API is called and no quota is
/// spent. This is where the measured gateway quirks (explicit
/// <c>stream:false</c>, the verbatim free-pool id, empty-content/<c>length</c> as
/// data) are pinned.
/// </summary>
public sealed class OpenAiCompatibleLlmProviderTests
{
    private const string Model =
        "free_frontier_pooled_gemini_gemini_3.8_flash__kr_claude_sonnet_4.5__openrouter_nex_agi_nex_n2.5_pro_free__kgw_nex_agi_nex_n2.5_pro_free__openrouter_inclusionai_ling_3.0_flash_vl_free";
    private const string TokenVariable = "NOSTOS_ASSISTANT_TEST_TOKEN";
    private const string TokenValue = "test-token-not-a-real-secret";

    [Fact]
    public async Task Sends_stream_false_explicitly_and_the_frozen_model_id_verbatim()
    {
        var handler = new StubHttpMessageHandler();
        HttpMethod? method = null;
        Uri? uri = null;
        System.Net.Http.Headers.AuthenticationHeaderValue? authorization = null;
        string? body = null;

        handler.Register("/v1/chat/completions", request =>
        {
            // Captured at send time: the provider disposes the request after the
            // call, so reading it afterwards throws.
            method = request.Method;
            uri = request.RequestUri;
            authorization = request.Headers.Authorization;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"choices":[{"message":{"content":"FREE_POOL_OK"},"finish_reason":"stop"}]}""");
        });

        var provider = CreateProvider(handler);

        Environment.SetEnvironmentVariable(TokenVariable, TokenValue);
        try
        {
            var completion = await provider.CompleteAsync(MinimalRequest());

            method.Should().Be(HttpMethod.Post);
            uri!.AbsolutePath.Should().Be("/v1/chat/completions");
            authorization!.Scheme.Should().Be("Bearer");
            authorization.Parameter.Should().Be(TokenValue);

            // The measured gateway quirk: an omitted stream field returns SSE.
            body.Should().Contain("\"stream\":false");
            // The free-pool id, byte for byte. No paid fallback exists.
            body.Should().Contain(Model);

            completion.Content.Should().Be("FREE_POOL_OK");
            completion.FinishReason.Should().Be("stop");
            completion.ToolCalls.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    [Fact]
    public async Task An_empty_content_with_a_length_finish_is_handled_as_data()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register(
            "/v1/chat/completions",
            _ => Json("""{"choices":[{"message":{"content":""},"finish_reason":"length"}]}"""));

        var provider = CreateProvider(handler);

        Environment.SetEnvironmentVariable(TokenVariable, TokenValue);
        try
        {
            var completion = await provider.CompleteAsync(MinimalRequest());

            // Measured pool behaviour: the budget went to reasoning. Not a 500.
            completion.Content.Should().BeEmpty();
            completion.FinishReason.Should().Be("length");
            completion.IsLengthTruncated.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    [Fact]
    public async Task Parses_tool_calls_and_usage()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register(
            "/v1/chat/completions",
            _ => Json(
                """
                {"choices":[{"message":{"content":null,"tool_calls":[
                  {"id":"call_1","type":"function","function":{"name":"notes_search","arguments":"{\"query\":\"x\"}"}}
                ]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":2010,"completion_tokens":62,
                  "completion_tokens_details":{"reasoning_tokens":41}}}
                """));

        var provider = CreateProvider(handler);

        Environment.SetEnvironmentVariable(TokenVariable, TokenValue);
        try
        {
            var completion = await provider.CompleteAsync(MinimalRequest());

            completion.ToolCalls.Should().ContainSingle();
            completion.ToolCalls[0].Name.Should().Be("notes_search");
            completion.ToolCalls[0].ArgumentsJson.Should().Be("""{"query":"x"}""");
            completion.PromptTokens.Should().Be(2010);
            completion.CompletionTokens.Should().Be(62);
            completion.ThinkingTokens.Should().Be(41);
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
        var act = () => provider.CompleteAsync(MinimalRequest());

        var exception = (await act.Should().ThrowAsync<LlmException>()).Which;
        exception.Code.Should().Be(LlmErrorCodes.NotConfigured);

        // No credential means no outbound request at all.
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmErrorCodes.Permission)]
    [InlineData(HttpStatusCode.Forbidden, LlmErrorCodes.Permission)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, LlmErrorCodes.Provider)]
    [InlineData(HttpStatusCode.InternalServerError, LlmErrorCodes.Provider)]
    public async Task Maps_provider_status_codes_to_data(HttpStatusCode status, string expectedCode)
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/chat/completions", _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(
                """{"error":{"message":"nope"}}""", Encoding.UTF8, "application/json"),
        });

        var provider = CreateProvider(handler);

        Environment.SetEnvironmentVariable(TokenVariable, TokenValue);
        try
        {
            var act = () => provider.CompleteAsync(MinimalRequest());

            var exception = (await act.Should().ThrowAsync<LlmException>()).Which;
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
        handler.Register("/v1/chat/completions", _ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"error":{"message":"boom"}}"""),
        });

        var provider = CreateProvider(handler);

        Environment.SetEnvironmentVariable(TokenVariable, TokenValue);
        try
        {
            var act = () => provider.CompleteAsync(MinimalRequest());

            await act.Should().ThrowAsync<LlmException>();

            // No retry loop: one call, one outcome.
            handler.RecordedRequests.Should().HaveCount(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    private static LlmCompletionRequest MinimalRequest() =>
        new([LlmMessage.User("Say only: FREE_POOL_OK")], [], 2048);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static EffectiveAiProviderConfig TestConfig(string? apiKey) => new(
        Enabled: true,
        BaseUrl: "http://omenhub:20128/v1",
        Model: Model,
        ApiKeyEnvironmentVariable: TokenVariable,
        ApiKey: apiKey,
        KeyFromServerEnv: apiKey is not null);

    private static OpenAiCompatibleLlmProvider CreateProvider(
        StubHttpMessageHandler handler,
        string? apiKey = TokenValue) =>
        new(
            new StubHttpClientFactory(handler, new Uri("http://omenhub:20128")),
            new StubAiProviderConfigResolver { Llm = TestConfig(apiKey) },
            NullLogger<OpenAiCompatibleLlmProvider>.Instance);
}
