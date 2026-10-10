using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// Coverage for the AI provider settings service's outbound probes and its
/// graceful secret handling (assistant-milestone plan). The transport is stubbed,
/// so nothing here talks to a real gateway; the DB is a real temporary SQLite
/// file so the encrypted-at-rest and degrade paths are exercised for real.
/// </summary>
public sealed class AiProviderSettingsServiceTests : IDisposable
{
    private const string LlmEnvVariable = "NOSTOS_AI_PROVIDER_SERVICE_TEST_LLM_TOKEN";
    private const string SttEnvVariable = "NOSTOS_AI_PROVIDER_SERVICE_TEST_STT_TOKEN";
    private const string EmbeddingEnvVariable = "NOSTOS_AI_PROVIDER_SERVICE_TEST_EMBEDDING_TOKEN";
    private const string EnvKeyValue = "sk-from-environment";

    private readonly SqliteTestFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // ------------------------------------------------------------------
    // Model listing
    // ------------------------------------------------------------------

    [Fact]
    public async Task Models_falls_back_to_the_v1_path_when_the_base_has_no_v1()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/models", Json("""{"data":[{"id":"alpha"},{"id":"beta"}]}"""));

        var service = CreateService(handler);

        var result = await service.ListModelsAsync(
            new AiProviderModelsRequest("stt", BaseUrl: null, ApiKey: null));

        result.Models.Should().Equal("alpha", "beta");
        handler.RecordedRequestPaths.Should().Contain("/models");
        handler.RecordedRequestPaths.Should().Contain("/v1/models");
    }

    [Fact]
    public async Task Models_tries_the_base_url_first_when_it_already_ends_in_v1()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/models", Json("""{"data":[{"id":"only"}]}"""));

        var service = CreateService(handler);

        var result = await service.ListModelsAsync(
            new AiProviderModelsRequest("llm", BaseUrl: null, ApiKey: null));

        result.Models.Should().Equal("only");
        // The base URL already ends in /v1, so the first attempt succeeds and the
        // fallback is never tried.
        handler.RecordedRequestPaths.Should().Equal("/v1/models");
    }

    [Fact]
    public async Task Models_returns_a_502_style_upstream_error_with_the_provider_message()
    {
        var handler = new StubHttpMessageHandler().SetFallback(_ => new HttpResponseMessage(
            HttpStatusCode.BadGateway)
        {
            Content = new StringContent("""{"error":{"message":"gateway says no"}}"""),
        });

        var service = CreateService(handler);

        var act = () => service.ListModelsAsync(
            new AiProviderModelsRequest("llm", BaseUrl: null, ApiKey: null));

        var exception = (await act.Should().ThrowAsync<AiProviderUpstreamException>()).Which;
        exception.Message.Should().Contain("gateway says no");
    }

    [Fact]
    public async Task Models_sends_the_effective_bearer_token()
    {
        var handler = new StubHttpMessageHandler();
        System.Net.Http.Headers.AuthenticationHeaderValue? authorization = null;
        handler.Register("/v1/models", request =>
        {
            authorization = request.Headers.Authorization;
            return Json("""{"data":[{"id":"x"}]}""");
        });

        Environment.SetEnvironmentVariable(LlmEnvVariable, EnvKeyValue);
        try
        {
            var service = CreateService(handler);
            await service.ListModelsAsync(
                new AiProviderModelsRequest("llm", BaseUrl: null, ApiKey: null));

            authorization!.Scheme.Should().Be("Bearer");
            authorization.Parameter.Should().Be(EnvKeyValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmEnvVariable, null);
        }
    }

    // ------------------------------------------------------------------
    // Connection tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Llm_test_sends_stream_false_and_a_tiny_max_tokens()
    {
        var handler = new StubHttpMessageHandler();
        string? body = null;
        handler.Register("/v1/chat/completions", request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"choices":[{"message":{"content":"pong"},"finish_reason":"stop"}]}""");
        });

        Environment.SetEnvironmentVariable(LlmEnvVariable, EnvKeyValue);
        try
        {
            var service = CreateService(handler);
            var result = await service.TestAsync(new AiProviderTestRequest("llm", null, null, null));

            result.Ok.Should().BeTrue();
            result.Error.Should().BeNull();
            body.Should().Contain("\"stream\":false");
            body.Should().Contain("\"max_tokens\":8");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmEnvVariable, null);
        }
    }

    [Fact]
    public async Task Embedding_test_posts_one_real_embedding_and_reports_its_dimensions()
    {
        var handler = new StubHttpMessageHandler();
        string? body = null;
        handler.Register("/v1/embeddings", request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""{"data":[{"index":0,"embedding":[0.1,0.2,0.3,0.4]}]}""");
        });

        Environment.SetEnvironmentVariable(EmbeddingEnvVariable, EnvKeyValue);
        try
        {
            var service = CreateService(handler);
            var result = await service.TestAsync(new AiProviderTestRequest("embedding", null, null, null));

            result.Ok.Should().BeTrue();
            result.Detail.Should().Contain("alibaba/qwen3-embedding-0-6b").And.Contain("4 dimensions");
            body.Should().Contain("\"model\":\"alibaba/qwen3-embedding-0-6b\"");
            body.Should().Contain("\"input\":[\"ping\"]");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EmbeddingEnvVariable, null);
        }
    }

    [Fact]
    public async Task Embedding_test_fails_when_a_200_carries_no_usable_vector()
    {
        var handler = new StubHttpMessageHandler();
        // A chat endpoint answering the embeddings route: 200, but not a vector.
        handler.Register("/v1/embeddings", Json("""{"choices":[{"message":{"content":"pong"}}]}"""));

        var service = CreateService(handler);
        var result = await service.TestAsync(new AiProviderTestRequest("embedding", null, null, null));

        result.Ok.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Embedding_settings_round_trip_with_the_key_encrypted_at_rest()
    {
        const string key = "sk-embedding-owner-key";
        var path = _fixture.CreateDatabasePath();
        var service = CreateService(new StubHttpMessageHandler(), path);

        var before = await service.GetAsync();
        before.Embedding!.Model.Should().Be("alibaba/qwen3-embedding-0-6b");
        before.Embedding.HasKey.Should().BeFalse();
        (await service.GetEffectiveEmbeddingAsync()).IsAvailable.Should().BeFalse();

        var updated = await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            Llm: null,
            Stt: null,
            Embedding: new AiProviderSectionUpdate(true, "http://ollama.lan:11434/v1", "qwen3-embedding:0.6b", key)));

        updated.Embedding!.BaseUrl.Should().Be("http://ollama.lan:11434/v1");
        updated.Embedding.Model.Should().Be("qwen3-embedding:0.6b");
        updated.Embedding.HasKey.Should().BeTrue();
        updated.Embedding.KeyFromServerEnv.Should().BeFalse();
        // The other surfaces are untouched.
        updated.Llm.Model.Should().Be("test-llm-model");

        var effective = await service.GetEffectiveEmbeddingAsync();
        effective.IsAvailable.Should().BeTrue();
        effective.ApiKey.Should().Be(key);

        using (var db = _fixture.CreateContext(path))
        {
            var row = await db.AiProviderSettings.SingleAsync();
            row.EmbeddingApiKeyEncrypted.Should().NotBeNullOrWhiteSpace();
            row.EmbeddingApiKeyEncrypted.Should().NotContain(key);
            row.LlmApiKeyEncrypted.Should().BeNull();
        }

        // The kill switch is a stored `false`, distinct from "never set".
        var disabled = await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            Llm: null,
            Stt: null,
            Embedding: new AiProviderSectionUpdate(false, null, null, null)));
        disabled.Embedding!.Enabled.Should().BeFalse();
        disabled.Embedding.HasKey.Should().BeTrue();
        (await service.GetEffectiveEmbeddingAsync()).IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Stt_test_uploads_a_generated_wav()
    {
        var handler = new StubHttpMessageHandler();
        string? body = null;
        string? contentType = null;
        handler.Register("/v1/audio/transcriptions", request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            contentType = request.Content.Headers.ContentType?.MediaType;
            return Json("""{"text":"","language":"en","duration":1.0}""");
        });

        Environment.SetEnvironmentVariable(SttEnvVariable, EnvKeyValue);
        try
        {
            var service = CreateService(handler);
            var result = await service.TestAsync(new AiProviderTestRequest("stt", null, null, null));

            result.Ok.Should().BeTrue();
            contentType.Should().StartWith("multipart/form-data");
            body.Should().Contain("silence.wav");
            // The bytes are a real RIFF/WAVE payload, not a placeholder string.
            body.Should().Contain("RIFF");
            body.Should().Contain("WAVE");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SttEnvVariable, null);
        }
    }

    [Fact]
    public async Task Test_reports_a_provider_failure_as_ok_false()
    {
        var handler = new StubHttpMessageHandler().SetFallback(_ => new HttpResponseMessage(
            HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"bad key"}}"""),
        });

        Environment.SetEnvironmentVariable(LlmEnvVariable, EnvKeyValue);
        try
        {
            var service = CreateService(handler);
            var result = await service.TestAsync(new AiProviderTestRequest("llm", null, null, null));

            result.Ok.Should().BeFalse();
            result.Detail.Should().BeNull();
            result.Error.Should().Contain("bad key");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmEnvVariable, null);
        }
    }

    [Fact]
    public async Task Test_reports_an_unknown_kind_as_ok_false_not_a_throw()
    {
        var service = CreateService(new StubHttpMessageHandler());

        var result = await service.TestAsync(new AiProviderTestRequest("nonsense", null, null, null));

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("llm");
    }

    // ------------------------------------------------------------------
    // Secret handling
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_decrypt_failure_degrades_to_the_environment_variable()
    {
        Environment.SetEnvironmentVariable(LlmEnvVariable, EnvKeyValue);
        try
        {
            var path = _fixture.CreateDatabasePath();
            await using (var db = _fixture.CreateContext(path))
            {
                db.AiProviderSettings.Add(new AiProviderSettingsModel
                {
                    Id = AiProviderSettingsModel.SingletonId,
                    // Not a valid Data Protection payload.
                    LlmApiKeyEncrypted = "not-a-valid-protected-payload",
                });
                await db.SaveChangesAsync();
            }

            var service = CreateService(new StubHttpMessageHandler(), path);

            var effective = await service.GetEffectiveLlmAsync();

            // Degraded, not thrown, and it used the env var.
            effective.HasKey.Should().BeTrue();
            effective.ApiKey.Should().Be(EnvKeyValue);
            effective.KeyFromServerEnv.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmEnvVariable, null);
        }
    }

    // ------------------------------------------------------------------
    // Alignment transcription settings
    // ------------------------------------------------------------------

    [Fact]
    public async Task Existing_settings_default_alignment_to_voice_transcription()
    {
        var path = _fixture.CreateDatabasePath();

        await using (var db = _fixture.CreateContext(path))
        {
            db.AiProviderSettings.Add(new AiProviderSettingsModel
            {
                Id = AiProviderSettingsModel.SingletonId,
                SttEnabled = true,
                SttBaseUrl = "http://voice.example.test",
                SttModel = "voice-test-model",
            });
            await db.SaveChangesAsync();
        }

        var service = CreateService(new StubHttpMessageHandler(), path);

        var settings = await service.GetAsync();

        settings.AlignmentTranscription.Should().NotBeNull();
        settings.AlignmentTranscription!.UseVoiceTranscription.Should().BeTrue();
        settings.AlignmentTranscription.ApiStyle.Should().Be(
            AlignmentTranscriptionApiStyles.OpenAiMultipart);

        var effective = await service.GetEffectiveAlignmentSttAsync();

        effective.UseVoiceTranscription.Should().BeTrue();
        effective.ApiStyle.Should().Be(
            AlignmentTranscriptionApiStyles.OpenAiMultipart);
        effective.BaseUrl.Should().Be("http://voice.example.test");
        effective.Model.Should().Be("voice-test-model");
    }

    [Fact]
    public async Task Alignment_settings_round_trip_and_encrypt_the_key()
    {
        const string key = "alignment-test-secret";
        var path = _fixture.CreateDatabasePath();
        var service = CreateService(new StubHttpMessageHandler(), path);

        var updated = await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            Llm: null,
            Stt: null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                Enabled: true,
                BaseUrl: "https://transcription.example.test",
                Model: "alignment-test-model",
                ApiKey: key,
                UseVoiceTranscription: false,
                ApiStyle: AlignmentTranscriptionApiStyles.JsonBase64)));

        updated.AlignmentTranscription!.UseVoiceTranscription.Should().BeFalse();
        updated.AlignmentTranscription.ApiStyle.Should().Be(
            AlignmentTranscriptionApiStyles.JsonBase64);
        updated.AlignmentTranscription.Model.Should().Be("alignment-test-model");
        updated.AlignmentTranscription.HasKey.Should().BeTrue();
        updated.AlignmentTranscription.KeyFromServerEnv.Should().BeFalse();

        var effective = await service.GetEffectiveAlignmentSttAsync();
        effective.Enabled.Should().BeTrue();
        effective.IsAvailable.Should().BeTrue();
        effective.ApiKey.Should().Be(key);
        effective.ApiStyle.Should().Be(AlignmentTranscriptionApiStyles.JsonBase64);

        await using (var db = _fixture.CreateContext(path))
        {
            var connection = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync();

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT ApiKeyEncrypted
                    FROM AlignmentTranscriptionSettings
                    WHERE Id = 1;
                    """;

                var encrypted = (string?)await command.ExecuteScalarAsync();
                encrypted.Should().NotBeNullOrWhiteSpace();
                encrypted.Should().NotContain(key);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        // Null key preserves the secret; changing the model does not erase it.
        updated = await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                null, null, "replacement-model", null)));

        updated.AlignmentTranscription!.Model.Should().Be("replacement-model");
        (await service.GetEffectiveAlignmentSttAsync()).ApiKey.Should().Be(key);

        // Explicit empty key clears the stored credential.
        updated = await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                null, null, null, "")));

        updated.AlignmentTranscription!.HasKey.Should().BeFalse();
    }

    [Fact]
    public async Task Alignment_resolution_switches_between_voice_and_custom_settings()
    {
        const string key = "alignment-secret";
        var service = CreateService(new StubHttpMessageHandler());

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                true,
                "https://custom.example.test",
                "custom-model",
                key,
                false,
                AlignmentTranscriptionApiStyles.JsonBase64)));

        var custom = await service.GetEffectiveAlignmentSttAsync();
        custom.UseVoiceTranscription.Should().BeFalse();
        custom.BaseUrl.Should().Be("https://custom.example.test");
        custom.ApiStyle.Should().Be(AlignmentTranscriptionApiStyles.JsonBase64);
        custom.IsAvailable.Should().BeTrue();

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                null, null, null, null,
                UseVoiceTranscription: true)));

        var voice = await service.GetEffectiveAlignmentSttAsync();
        var ordinaryStt = await service.GetEffectiveSttAsync();

        voice.UseVoiceTranscription.Should().BeTrue();
        voice.ApiStyle.Should().Be(
            AlignmentTranscriptionApiStyles.OpenAiMultipart);
        voice.Provider.Should().BeEquivalentTo(ordinaryStt);

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                null, null, null, null,
                UseVoiceTranscription: false)));

        var restored = await service.GetEffectiveAlignmentSttAsync();
        restored.Model.Should().Be("custom-model");
        restored.ApiKey.Should().Be(key);
        restored.ApiStyle.Should().Be(AlignmentTranscriptionApiStyles.JsonBase64);
    }

    [Fact]
    public async Task Alignment_rejects_unsupported_api_styles()
    {
        var service = CreateService(new StubHttpMessageHandler());

        var act = () => service.UpdateAsync(
            new AiProviderSettingsUpdateRequest(
                null,
                null,
                AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                    null, null, null, null,
                    ApiStyle: "unsupported")));

        await act.Should().ThrowAsync<AiProviderValidationException>();
    }

    [Fact]
    public async Task Alignment_json_connection_test_sends_the_expected_wire_format()
    {
        var handler = new StubHttpMessageHandler();
        string? body = null;
        string? model = null;
        string? version = null;

        handler.Register("/v4/ai/transcription-model", request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            model = request.Headers.GetValues("ai-model-id").Single();
            version = request.Headers.GetValues(
                "ai-transcription-model-specification-version").Single();

            return Json(
                """{"text":"","language":"en","durationInSeconds":1}""");
        });

        var service = CreateService(handler);
        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                true,
                "https://transcription.example.test",
                "test-transcription-model",
                "test-key",
                false,
                AlignmentTranscriptionApiStyles.JsonBase64)));

        var result = await service.TestAsync(new AiProviderTestRequest(
            "alignment-transcription", null, null, null));

        result.Ok.Should().BeTrue();
        model.Should().Be("test-transcription-model");
        version.Should().Be("4");

        using var document = JsonDocument.Parse(body!);
        document.RootElement.GetProperty("mediaType")
            .GetString().Should().Be("audio/wav");

        var bytes = Convert.FromBase64String(
            document.RootElement.GetProperty("audio").GetString()!);

        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("RIFF");
        handler.RecordedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Alignment_model_listing_uses_the_effective_configuration()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v1/models",
            Json("""{"data":[{"id":"alignment-model-a"},{"id":"alignment-model-b"}]}"""));

        var service = CreateService(handler);
        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                true,
                "https://transcription.example.test",
                "alignment-model-a",
                "test-key",
                false,
                AlignmentTranscriptionApiStyles.JsonBase64)));

        var result = await service.ListModelsAsync(
            new AiProviderModelsRequest(
                "alignment-transcription", null, null));

        result.Models.Should().Equal(
            "alignment-model-a", "alignment-model-b");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private AiProviderSettingsService CreateService(
        StubHttpMessageHandler handler,
        string? databasePath = null)
    {
        var path = databasePath ?? _fixture.CreateDatabasePath();
        using (var db = _fixture.CreateContext(path))
        {
            // EnsureCreated is idempotent; this guarantees the schema on a path
            // that was just handed out.
        }

        var assistant = new AssistantOptions
        {
            Enabled = true,
            BaseUrl = "http://omenhub:20128/v1",
            Model = "test-llm-model",
            ApiKeyEnvironmentVariable = LlmEnvVariable,
        };
        var speech = new SpeechOptions
        {
            Enabled = true,
            BaseUrl = "http://omenhub:20128",
            Model = "groq/whisper-large-v3-turbo",
            ApiKeyEnvironmentVariable = SttEnvVariable,
        };

        var embedding = new EmbeddingOptions
        {
            Enabled = true,
            BaseUrl = "https://ai-gateway.vercel.sh/v1",
            Model = "alibaba/qwen3-embedding-0-6b",
            ApiKeyEnvironmentVariable = EmbeddingEnvVariable,
        };

        return new AiProviderSettingsService(
            new TestDbContextFactory(path),
            assistant,
            speech,
            embedding,
            new EphemeralDataProtectionProvider(),
            new StubHttpClientFactory(handler),
            NullLogger<AiProviderSettingsService>.Instance);
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
}
