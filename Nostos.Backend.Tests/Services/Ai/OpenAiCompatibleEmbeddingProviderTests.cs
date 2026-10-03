using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// Transport coverage for the SelfHosted BYOK embedding provider (issue #683).
/// The handler is stubbed, so nothing here talks to a real gateway.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProviderTests
{
    private const string Key = "sk-embedding-sentinel";

    [Fact]
    public async Task Embed_posts_the_openai_shape_to_the_embeddings_route_with_the_bearer_key()
    {
        var handler = new StubHttpMessageHandler();
        string? body = null;
        System.Net.Http.Headers.AuthenticationHeaderValue? authorization = null;
        handler.Register("/v1/embeddings", request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            authorization = request.Headers.Authorization;
            return Json("""
                {"object":"list","model":"provider-alias",
                 "data":[{"index":1,"embedding":[0,1,0]},{"index":0,"embedding":[1,0,0]}],
                 "usage":{"prompt_tokens":7,"total_tokens":7}}
                """);
        });

        var provider = CreateProvider(handler, Configured());

        var batch = await provider.EmbedAsync(["first", "second"]);

        body.Should().Contain("\"model\":\"alibaba/qwen3-embedding-0-6b\"");
        body.Should().Contain("\"input\":[\"first\",\"second\"]");
        body.Should().Contain("\"encoding_format\":\"float\"");
        authorization!.Scheme.Should().Be("Bearer");
        authorization.Parameter.Should().Be(Key);

        // Ordered by `index`, not by array position.
        batch.Vectors.Should().HaveCount(2);
        batch.Vectors[0].Should().Equal(1f, 0f, 0f);
        batch.Vectors[1].Should().Equal(0f, 1f, 0f);
        batch.TotalTokens.Should().Be(7);
        // The configured id, not the alias the provider echoed back.
        batch.Model.Should().Be("alibaba/qwen3-embedding-0-6b");
    }

    [Fact]
    public async Task Active_model_is_null_until_the_surface_is_enabled_addressed_and_credentialed()
    {
        var handler = new StubHttpMessageHandler();

        (await CreateProvider(handler, Configured()).GetActiveModelAsync())
            .Should().Be("alibaba/qwen3-embedding-0-6b");
        (await CreateProvider(handler, Configured() with { Enabled = false }).GetActiveModelAsync())
            .Should().BeNull();
        (await CreateProvider(handler, Configured() with { BaseUrl = "" }).GetActiveModelAsync())
            .Should().BeNull();
        (await CreateProvider(handler, Configured() with { ApiKey = null }).GetActiveModelAsync())
            .Should().BeNull();
        (await CreateProvider(handler, Configured() with { Model = " " }).GetActiveModelAsync())
            .Should().BeNull();
    }

    [Fact]
    public async Task Embed_without_configuration_fails_typed_and_never_calls_the_endpoint()
    {
        var handler = new StubHttpMessageHandler();
        var provider = CreateProvider(handler, Configured() with { ApiKey = null });

        var act = () => provider.EmbedAsync(["text"]);

        (await act.Should().ThrowAsync<EmbeddingException>())
            .Which.Code.Should().Be(EmbeddingErrorCodes.NotConfigured);
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, EmbeddingErrorCodes.Permission)]
    [InlineData(HttpStatusCode.Forbidden, EmbeddingErrorCodes.Permission)]
    [InlineData(HttpStatusCode.TooManyRequests, EmbeddingErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, EmbeddingErrorCodes.Provider)]
    public async Task Embed_maps_upstream_statuses_to_stable_codes(HttpStatusCode status, string expectedCode)
    {
        var handler = new StubHttpMessageHandler().SetFallback(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"error":{"message":"upstream says no"}}"""),
        });
        var provider = CreateProvider(handler, Configured());

        var act = () => provider.EmbedAsync(["text"]);

        var exception = (await act.Should().ThrowAsync<EmbeddingException>()).Which;
        exception.Code.Should().Be(expectedCode);
        exception.Message.Should().NotContain(Key);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":[]}]}""")]
    [InlineData("""{"data":[{"index":0,"embedding":"AAAA"}]}""")]
    [InlineData("""{"data":[{"index":3,"embedding":[1,2]}]}""")]
    public async Task Embed_rejects_a_body_that_does_not_carry_one_usable_vector_per_input(string responseBody)
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/embeddings", _ => Json(responseBody));
        var provider = CreateProvider(handler, Configured());

        var act = () => provider.EmbedAsync(["text"]);

        (await act.Should().ThrowAsync<EmbeddingException>())
            .Which.Code.Should().Be(EmbeddingErrorCodes.InvalidResponse);
    }

    [Fact]
    public async Task Embed_rejects_vectors_of_differing_lengths()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/embeddings", _ => Json(
            """{"data":[{"index":0,"embedding":[1,2,3]},{"index":1,"embedding":[1,2]}]}"""));
        var provider = CreateProvider(handler, Configured());

        var act = () => provider.EmbedAsync(["a", "b"]);

        (await act.Should().ThrowAsync<EmbeddingException>())
            .Which.Code.Should().Be(EmbeddingErrorCodes.InvalidResponse);
    }

    private static EffectiveAiProviderConfig Configured() => new(
        Enabled: true,
        BaseUrl: "https://ai-gateway.vercel.sh/v1",
        Model: "alibaba/qwen3-embedding-0-6b",
        ApiKeyEnvironmentVariable: "NOSTOS_TEST_TOKEN",
        ApiKey: Key,
        KeyFromServerEnv: false);

    private static OpenAiCompatibleEmbeddingProvider CreateProvider(
        StubHttpMessageHandler handler,
        EffectiveAiProviderConfig embedding) =>
        new(
            new StubHttpClientFactory(handler),
            new StubAiProviderConfigResolver { Embedding = embedding },
            NullLogger<OpenAiCompatibleEmbeddingProvider>.Instance);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
