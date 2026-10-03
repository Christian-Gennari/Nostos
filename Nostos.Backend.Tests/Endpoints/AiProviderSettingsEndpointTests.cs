using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// Full-host coverage for the AI provider settings surface
/// <c>/api/settings/ai-provider</c> (assistant-milestone plan). The host runs
/// against a real temporary SQLite database, so the encrypted-at-rest assertions
/// read the bytes that were actually written.
/// </summary>
public sealed class AiProviderSettingsEndpointTests
{
    // Variable names owned solely by this class: the process-wide environment is
    // shared, so a distinct name keeps another suite's token out of these tests.
    private const string LlmTokenVariable = "NOSTOS_AI_PROVIDER_TEST_LLM_TOKEN";
    private const string SttTokenVariable = "NOSTOS_AI_PROVIDER_TEST_STT_TOKEN";
    private const string LlmTokenValue = "sk-llm-sentinel-key-value";
    private const string SttTokenValue = "sk-stt-sentinel-key-value";
    private const string NewLlmKey = "sk-new-llm-key-1234567890";
    private const string LlmBaseUrl = "http://assistant.invalid/v1";
    private const string LlmModel = "test-llm-model";
    private const string SttBaseUrl = "http://stt.invalid";
    private const string SttModel = "test-stt-model";

    public AiProviderSettingsEndpointTests()
    {
        Environment.SetEnvironmentVariable(LlmTokenVariable, null);
        Environment.SetEnvironmentVariable(SttTokenVariable, null);
    }

    // ------------------------------------------------------------------
    // GET — effective values, never the key
    // ------------------------------------------------------------------

    [Fact]
    public async Task Get_reports_the_fallback_when_nothing_is_stored()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory);
        using var client = host.CreateClient();

        var response = await client.GetAsync(AiProviderSettingsEndpoints.Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AiProviderSettingsResponse>();

        body!.Llm.Enabled.Should().BeTrue();
        body.Llm.BaseUrl.Should().Be(LlmBaseUrl);
        body.Llm.Model.Should().Be(LlmModel);
        body.Llm.HasKey.Should().BeFalse();
        body.Llm.KeyFromServerEnv.Should().BeFalse();

        body.Stt.Enabled.Should().BeTrue();
        body.Stt.BaseUrl.Should().Be(SttBaseUrl);
        body.Stt.Model.Should().Be(SttModel);
        body.Stt.HasKey.Should().BeFalse();

        // Embeddings ship unconfigured: a model default, no endpoint, no key.
        body.Embedding.Should().NotBeNull();
        body.Embedding!.BaseUrl.Should().BeEmpty();
        body.Embedding.Model.Should().Be("alibaba/qwen3-embedding-0-6b");
        body.Embedding.HasKey.Should().BeFalse();
    }

    [Fact]
    public async Task Get_never_returns_the_environment_key_or_its_variable_name()
    {
        Environment.SetEnvironmentVariable(LlmTokenVariable, LlmTokenValue);
        try
        {
            using var factory = new LibraryEndpointFactory();
            using var host = CreateHost(factory);
            using var client = host.CreateClient();

            var response = await client.GetAsync(AiProviderSettingsEndpoints.Route);
            var raw = await response.Content.ReadAsStringAsync();
            var body = await response.Content.ReadFromJsonAsync<AiProviderSettingsResponse>();

            body!.Llm.HasKey.Should().BeTrue();
            body.Llm.KeyFromServerEnv.Should().BeTrue();

            raw.Should().NotContain(LlmTokenValue);
            raw.Should().NotContain(LlmTokenVariable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmTokenVariable, null);
        }
    }

    // ------------------------------------------------------------------
    // PUT — round trip and tri-state key semantics
    // ------------------------------------------------------------------

    [Fact]
    public async Task Put_round_trips_enabled_baseUrl_and_model()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory);
        using var client = host.CreateClient();

        var put = await client.PutAsJsonAsync(
            AiProviderSettingsEndpoints.Route,
            new AiProviderSettingsUpdateRequest(
                new AiProviderSectionUpdate(
                    Enabled: false,
                    BaseUrl: "http://lan-gateway:9000/v1",
                    Model: "my-chosen-model",
                    ApiKey: null),
                Stt: null));

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await put.Content.ReadFromJsonAsync<AiProviderSettingsResponse>();
        updated!.Llm.Enabled.Should().BeFalse();
        updated.Llm.BaseUrl.Should().Be("http://lan-gateway:9000/v1");
        updated.Llm.Model.Should().Be("my-chosen-model");
        // The untouched section is still the fallback.
        updated.Stt.Model.Should().Be(SttModel);

        // And the change is durable, not just echoed.
        var get = await client.GetFromJsonAsync<AiProviderSettingsResponse>(
            AiProviderSettingsEndpoints.Route);
        get!.Llm.Enabled.Should().BeFalse();
        get.Llm.BaseUrl.Should().Be("http://lan-gateway:9000/v1");
        get.Llm.Model.Should().Be("my-chosen-model");
    }

    [Fact]
    public async Task Put_stores_the_key_encrypted_at_rest_and_never_returns_it()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory);
        using var client = host.CreateClient();

        var put = await client.PutAsJsonAsync(
            AiProviderSettingsEndpoints.Route,
            new AiProviderSettingsUpdateRequest(
                new AiProviderSectionUpdate(null, null, null, NewLlmKey),
                Stt: null));

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await put.Content.ReadAsStringAsync();
        var body = await put.Content.ReadFromJsonAsync<AiProviderSettingsResponse>();

        body!.Llm.HasKey.Should().BeTrue();
        body.Llm.KeyFromServerEnv.Should().BeFalse();
        raw.Should().NotContain(NewLlmKey);

        using var db = OpenDb(factory.DatabasePath);
        var stored = db.AiProviderSettings.AsNoTracking().Single();
        stored.LlmApiKeyEncrypted.Should().NotBeNullOrWhiteSpace();
        stored.LlmApiKeyEncrypted.Should().NotBe(NewLlmKey);
        stored.LlmApiKeyEncrypted.Should().NotContain(NewLlmKey);

        // It decrypts back to the same usable key, through the effective config.
        var effective = await host.Services
            .GetRequiredService<IAiProviderConfigResolver>()
            .GetEffectiveLlmAsync();
        effective.ApiKey.Should().Be(NewLlmKey);
    }

    [Fact]
    public async Task Put_omitting_the_key_leaves_the_stored_one_unchanged()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory);
        using var client = host.CreateClient();

        await client.PutAsJsonAsync(
            AiProviderSettingsEndpoints.Route,
            new AiProviderSettingsUpdateRequest(
                new AiProviderSectionUpdate(null, null, null, NewLlmKey),
                Stt: null));

        string? ciphertextBefore;
        using (var db = OpenDb(factory.DatabasePath))
        {
            ciphertextBefore = db.AiProviderSettings.AsNoTracking().Single().LlmApiKeyEncrypted;
        }

        // Only the model changes; apiKey is omitted entirely.
        var put = await client.PutAsJsonAsync(
            AiProviderSettingsEndpoints.Route,
            new AiProviderSettingsUpdateRequest(
                new AiProviderSectionUpdate(null, null, "another-model", null),
                Stt: null));

        var body = await put.Content.ReadFromJsonAsync<AiProviderSettingsResponse>();
        body!.Llm.HasKey.Should().BeTrue();
        body.Llm.KeyFromServerEnv.Should().BeFalse();
        body.Llm.Model.Should().Be("another-model");

        using (var db = OpenDb(factory.DatabasePath))
        {
            var stored = db.AiProviderSettings.AsNoTracking().Single();
            stored.LlmApiKeyEncrypted.Should().Be(ciphertextBefore);
        }
    }

    [Fact]
    public async Task Put_with_an_empty_key_clears_the_stored_one()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory);
        using var client = host.CreateClient();

        await client.PutAsJsonAsync(
            AiProviderSettingsEndpoints.Route,
            new AiProviderSettingsUpdateRequest(
                new AiProviderSectionUpdate(null, null, null, NewLlmKey),
                Stt: null));

        // No env fallback in this test: clearing must leave no usable key.
        var put = await client.PutAsJsonAsync(
            AiProviderSettingsEndpoints.Route,
            new AiProviderSettingsUpdateRequest(
                new AiProviderSectionUpdate(null, null, null, string.Empty),
                Stt: null));

        var body = await put.Content.ReadFromJsonAsync<AiProviderSettingsResponse>();
        body!.Llm.HasKey.Should().BeFalse();
        body.Llm.KeyFromServerEnv.Should().BeFalse();

        using var db = OpenDb(factory.DatabasePath);
        db.AiProviderSettings.AsNoTracking().Single().LlmApiKeyEncrypted.Should().BeNull();
    }

    [Fact]
    public async Task Put_with_an_empty_key_falls_back_to_the_environment_variable()
    {
        Environment.SetEnvironmentVariable(LlmTokenVariable, LlmTokenValue);
        try
        {
            using var factory = new LibraryEndpointFactory();
            using var host = CreateHost(factory);
            using var client = host.CreateClient();

            await client.PutAsJsonAsync(
                AiProviderSettingsEndpoints.Route,
                new AiProviderSettingsUpdateRequest(
                    new AiProviderSectionUpdate(null, null, null, NewLlmKey),
                    Stt: null));

            var put = await client.PutAsJsonAsync(
                AiProviderSettingsEndpoints.Route,
                new AiProviderSettingsUpdateRequest(
                    new AiProviderSectionUpdate(null, null, null, string.Empty),
                    Stt: null));

            var body = await put.Content.ReadFromJsonAsync<AiProviderSettingsResponse>();
            body!.Llm.HasKey.Should().BeTrue();
            body.Llm.KeyFromServerEnv.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmTokenVariable, null);
        }
    }

    [Fact]
    public async Task A_private_http_host_is_accepted_because_the_gateway_is_on_the_lan()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory);
        using var client = host.CreateClient();

        var put = await client.PutAsJsonAsync(
            AiProviderSettingsEndpoints.Route,
            new AiProviderSettingsUpdateRequest(
                new AiProviderSectionUpdate(null, "http://192.168.1.10:20128/v1", null, null),
                Stt: null));

        put.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------
    // Validation -> 400 with a message
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://gateway.local/v1")]
    [InlineData("")]
    public async Task Put_rejects_an_invalid_base_url(string baseUrl)
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory);
        using var client = host.CreateClient();

        var put = await client.PutAsJsonAsync(
            AiProviderSettingsEndpoints.Route,
            new AiProviderSettingsUpdateRequest(
                new AiProviderSectionUpdate(null, baseUrl, null, null),
                Stt: null));

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(put)).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Put_rejects_an_empty_model()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory);
        using var client = host.CreateClient();

        var put = await client.PutAsJsonAsync(
            AiProviderSettingsEndpoints.Route,
            new AiProviderSettingsUpdateRequest(
                new AiProviderSectionUpdate(null, null, "   ", null),
                Stt: null));

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(put)).Should().NotBeNullOrWhiteSpace();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static NostosDbContext OpenDb(string path) =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options);

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        return document.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private static WebApplicationFactory<Program> CreateHost(LibraryEndpointFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Assistant:Enabled", "true");
            builder.UseSetting("Assistant:BaseUrl", LlmBaseUrl);
            builder.UseSetting("Assistant:Model", LlmModel);
            builder.UseSetting("Assistant:ApiKeyEnvironmentVariable", LlmTokenVariable);

            builder.UseSetting("Speech:Enabled", "true");
            builder.UseSetting("Speech:BaseUrl", SttBaseUrl);
            builder.UseSetting("Speech:Model", SttModel);
            builder.UseSetting("Speech:ApiKeyEnvironmentVariable", SttTokenVariable);
        });
}
