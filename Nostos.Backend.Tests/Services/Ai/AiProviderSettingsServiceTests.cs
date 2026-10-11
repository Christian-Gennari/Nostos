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
/// Provider settings, encrypted persistence, effective resolution and outbound
/// probes. SQLite is real; HTTP is intercepted.
/// </summary>
public sealed class AiProviderSettingsServiceTests : IDisposable
{
    private const string LlmEnvVariable = "NOSTOS_AI_PROVIDER_SERVICE_TEST_LLM_TOKEN";
    private const string SttEnvVariable = "NOSTOS_AI_PROVIDER_SERVICE_TEST_STT_TOKEN";
    private const string EmbeddingEnvVariable = "NOSTOS_AI_PROVIDER_SERVICE_TEST_EMBEDDING_TOKEN";
    private const string AlignmentEnvVariable = "NOSTOS_ALIGNMENT_STT_TOKEN";
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
            new AiProviderModelsRequest("stt", null, null));

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
            new AiProviderModelsRequest("llm", null, null));

        result.Models.Should().Equal("only");
        handler.RecordedRequestPaths.Should().Equal("/v1/models");
    }

    [Fact]
    public async Task Models_returns_a_502_style_upstream_error_with_the_provider_message()
    {
        var handler = new StubHttpMessageHandler().SetFallback(
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(
                    """{"error":{"message":"gateway says no"}}"""),
            });

        var service = CreateService(handler);

        var act = () => service.ListModelsAsync(
            new AiProviderModelsRequest("llm", null, null));

        var exception =
            (await act.Should().ThrowAsync<AiProviderUpstreamException>()).Which;

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
                new AiProviderModelsRequest("llm", null, null));

            authorization!.Scheme.Should().Be("Bearer");
            authorization.Parameter.Should().Be(EnvKeyValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmEnvVariable, null);
        }
    }

    // ------------------------------------------------------------------
    // Existing voice, LLM and embedding behaviour
    // ------------------------------------------------------------------

    [Fact]
    public async Task Llm_test_sends_stream_false_and_a_tiny_max_tokens()
    {
        var handler = new StubHttpMessageHandler();
        string? body = null;

        handler.Register("/v1/chat/completions", request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(
                """{"choices":[{"message":{"content":"pong"},"finish_reason":"stop"}]}""");
        });

        Environment.SetEnvironmentVariable(LlmEnvVariable, EnvKeyValue);
        try
        {
            var service = CreateService(handler);
            var result = await service.TestAsync(
                new AiProviderTestRequest("llm", null, null, null));

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
            var result = await service.TestAsync(
                new AiProviderTestRequest("embedding", null, null, null));

            result.Ok.Should().BeTrue();
            result.Detail.Should()
                .Contain("alibaba/qwen3-embedding-0-6b")
                .And.Contain("4 dimensions");

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
        handler.Register(
            "/v1/embeddings",
            Json("""{"choices":[{"message":{"content":"pong"}}]}"""));

        var service = CreateService(handler);
        var result = await service.TestAsync(
            new AiProviderTestRequest("embedding", null, null, null));

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

        var updated = await service.UpdateAsync(
            new AiProviderSettingsUpdateRequest(
                Llm: null,
                Stt: null,
                Embedding: new AiProviderSectionUpdate(
                    true,
                    "http://ollama.lan:11434/v1",
                    "qwen3-embedding:0.6b",
                    key)));

        updated.Embedding!.BaseUrl.Should().Be("http://ollama.lan:11434/v1");
        updated.Embedding.Model.Should().Be("qwen3-embedding:0.6b");
        updated.Embedding.HasKey.Should().BeTrue();
        updated.Embedding.KeyFromServerEnv.Should().BeFalse();
        updated.Llm.Model.Should().Be("test-llm-model");

        var effective = await service.GetEffectiveEmbeddingAsync();
        effective.IsAvailable.Should().BeTrue();
        effective.ApiKey.Should().Be(key);

        await using (var db = _fixture.CreateContext(path))
        {
            var row = await db.AiProviderSettings.SingleAsync();
            row.EmbeddingApiKeyEncrypted.Should().NotBeNullOrWhiteSpace();
            row.EmbeddingApiKeyEncrypted.Should().NotContain(key);
            row.LlmApiKeyEncrypted.Should().BeNull();
        }

        var disabled = await service.UpdateAsync(
            new AiProviderSettingsUpdateRequest(
                Llm: null,
                Stt: null,
                Embedding: new AiProviderSectionUpdate(
                    false, null, null, null)));

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
            var result = await service.TestAsync(
                new AiProviderTestRequest("stt", null, null, null));

            result.Ok.Should().BeTrue();
            contentType.Should().StartWith("multipart/form-data");
            body.Should().Contain("silence.wav");
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
        var handler = new StubHttpMessageHandler().SetFallback(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"error":{"message":"bad key"}}"""),
            });

        Environment.SetEnvironmentVariable(LlmEnvVariable, EnvKeyValue);
        try
        {
            var service = CreateService(handler);
            var result = await service.TestAsync(
                new AiProviderTestRequest("llm", null, null, null));

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

        var result = await service.TestAsync(
            new AiProviderTestRequest("nonsense", null, null, null));

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("llm");
    }

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
                    LlmApiKeyEncrypted = "not-a-valid-protected-payload",
                });
                await db.SaveChangesAsync();
            }

            var service = CreateService(new StubHttpMessageHandler(), path);
            var effective = await service.GetEffectiveLlmAsync();

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
    // EF-backed alignment settings
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
        var voice = await service.GetEffectiveSttAsync();

        effective.UseVoiceTranscription.Should().BeTrue();
        effective.ApiStyle.Should().Be(
            AlignmentTranscriptionApiStyles.OpenAiMultipart);
        effective.Provider.Should().BeEquivalentTo(voice);
        effective.BaseUrl.Should().Be("http://voice.example.test");
        effective.Model.Should().Be("voice-test-model");

        await using var verification = _fixture.CreateContext(path);
        var row = await verification.AiProviderSettings.SingleAsync();

        row.AlignmentTranscriptionEnabled.Should().BeNull();
        row.AlignmentTranscriptionBaseUrl.Should().BeNull();
        row.AlignmentTranscriptionModel.Should().BeNull();
        row.AlignmentTranscriptionApiKeyEncrypted.Should().BeNull();
        row.AlignmentTranscriptionUseVoiceTranscription.Should().BeNull();
        row.AlignmentTranscriptionApiStyle.Should().BeNull();
    }

    [Fact]
    public async Task Missing_settings_row_also_uses_voice_defaults()
    {
        var service = CreateService(new StubHttpMessageHandler());

        var alignment = await service.GetEffectiveAlignmentSttAsync();
        var voice = await service.GetEffectiveSttAsync();

        alignment.UseVoiceTranscription.Should().BeTrue();
        alignment.ApiStyle.Should().Be(
            AlignmentTranscriptionApiStyles.OpenAiMultipart);
        alignment.Provider.Should().BeEquivalentTo(voice);
    }

    [Fact]
    public async Task Alignment_settings_round_trip_and_encrypt_the_key()
    {
        const string key = "alignment-test-secret";
        var path = _fixture.CreateDatabasePath();
        var service = CreateService(new StubHttpMessageHandler(), path);

        var updated = await service.UpdateAsync(
            new AiProviderSettingsUpdateRequest(
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
            var rows = await db.AiProviderSettings.ToListAsync();
            rows.Should().ContainSingle();

            var row = rows.Single();
            row.AlignmentTranscriptionEnabled.Should().BeTrue();
            row.AlignmentTranscriptionBaseUrl.Should()
                .Be("https://transcription.example.test");
            row.AlignmentTranscriptionModel.Should().Be("alignment-test-model");
            row.AlignmentTranscriptionUseVoiceTranscription.Should().BeFalse();
            row.AlignmentTranscriptionApiStyle.Should()
                .Be(AlignmentTranscriptionApiStyles.JsonBase64);

            row.AlignmentTranscriptionApiKeyEncrypted
                .Should().NotBeNullOrWhiteSpace();
            row.AlignmentTranscriptionApiKeyEncrypted.Should().NotContain(key);
            row.SttApiKeyEncrypted.Should().BeNull();
        }

        updated = await service.UpdateAsync(
            new AiProviderSettingsUpdateRequest(
                null,
                null,
                AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                    null, null, "replacement-model", null)));

        updated.AlignmentTranscription!.Model.Should().Be("replacement-model");
        (await service.GetEffectiveAlignmentSttAsync()).ApiKey.Should().Be(key);

        updated = await service.UpdateAsync(
            new AiProviderSettingsUpdateRequest(
                null,
                null,
                AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                    null, null, null, "")));

        updated.AlignmentTranscription!.HasKey.Should().BeFalse();

        await using var finalDb = _fixture.CreateContext(path);
        (await finalDb.AiProviderSettings.SingleAsync())
            .AlignmentTranscriptionApiKeyEncrypted.Should().BeNull();
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
    public async Task Alignment_settings_preserve_the_dedicated_section_while_using_voice()
    {
        const string key = "dedicated-alignment-secret";
        const string customUrl = "https://dedicated.example.test";
        const string customModel = "dedicated-test-model";

        var service = CreateService(new StubHttpMessageHandler());

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                Enabled: true,
                BaseUrl: customUrl,
                Model: customModel,
                ApiKey: key,
                UseVoiceTranscription: false,
                ApiStyle: AlignmentTranscriptionApiStyles.JsonBase64)));

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                null, null, null, null,
                UseVoiceTranscription: true)));

        var settings = await service.GetAsync();
        var alignment = settings.AlignmentTranscription!;

        alignment.UseVoiceTranscription.Should().BeTrue();
        alignment.Enabled.Should().BeTrue();
        alignment.BaseUrl.Should().Be(customUrl);
        alignment.Model.Should().Be(customModel);
        alignment.ApiStyle.Should().Be(
            AlignmentTranscriptionApiStyles.JsonBase64);
        alignment.HasKey.Should().BeTrue();
        alignment.KeyFromServerEnv.Should().BeFalse();

        var effective = await service.GetEffectiveAlignmentSttAsync();
        var voice = await service.GetEffectiveSttAsync();

        effective.UseVoiceTranscription.Should().BeTrue();
        effective.ApiStyle.Should().Be(
            AlignmentTranscriptionApiStyles.OpenAiMultipart);
        effective.Provider.Should().BeEquivalentTo(voice);

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                null, null, null, null,
                UseVoiceTranscription: false)));

        effective = await service.GetEffectiveAlignmentSttAsync();

        effective.UseVoiceTranscription.Should().BeFalse();
        effective.Enabled.Should().BeTrue();
        effective.IsAvailable.Should().BeTrue();
        effective.BaseUrl.Should().Be(customUrl);
        effective.Model.Should().Be(customModel);
        effective.ApiKey.Should().Be(key);
        effective.ApiStyle.Should().Be(
            AlignmentTranscriptionApiStyles.JsonBase64);
    }

    [Fact]
    public async Task Alignment_effective_availability_respects_enabled_and_key()
    {
        var service = CreateService(new StubHttpMessageHandler());

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                Enabled: true,
                BaseUrl: "https://alignment.example.test",
                Model: "test-model",
                ApiKey: null,
                UseVoiceTranscription: false,
                ApiStyle: AlignmentTranscriptionApiStyles.JsonBase64)));

        var effective = await service.GetEffectiveAlignmentSttAsync();

        effective.Enabled.Should().BeTrue();
        effective.HasKey.Should().BeFalse();
        effective.IsAvailable.Should().BeFalse();

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                null, null, null, "test-key")));

        effective = await service.GetEffectiveAlignmentSttAsync();

        effective.Enabled.Should().BeTrue();
        effective.HasKey.Should().BeTrue();
        effective.IsAvailable.Should().BeTrue();

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                false, null, null, null)));

        effective = await service.GetEffectiveAlignmentSttAsync();

        effective.Enabled.Should().BeFalse();
        effective.HasKey.Should().BeTrue();
        effective.IsAvailable.Should().BeFalse();
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
    public async Task Alignment_validation_failure_does_not_save_other_section_changes()
    {
        var path = _fixture.CreateDatabasePath();
        var service = CreateService(new StubHttpMessageHandler(), path);

        var act = () => service.UpdateAsync(
            new AiProviderSettingsUpdateRequest(
                Llm: new AiProviderSectionUpdate(
                    false, null, "modified-model", null),
                Stt: null,
                AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                    null, null, null, null,
                    ApiStyle: "unsupported")));

        await act.Should().ThrowAsync<AiProviderValidationException>();

        await using var db = _fixture.CreateContext(path);
        (await db.AiProviderSettings.CountAsync()).Should().Be(0);
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

        var result = await service.TestAsync(
            new AiProviderTestRequest(
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
    public async Task Alignment_connection_test_rejects_a_malformed_success_response()
    {
        var handler = new StubHttpMessageHandler();

        handler.Register(
            "/v4/ai/transcription-model",
            _ => Json("""{"unexpected":"not a transcript"}"""));

        var service = CreateService(handler);
        await ConfigureJsonAlignmentAsync(service);

        var result = await service.TestAsync(
            new AiProviderTestRequest(
                "alignment-transcription", null, null, null));

        result.Ok.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        handler.RecordedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Alignment_connection_test_enforces_the_real_response_limit()
    {
        var handler = new StubHttpMessageHandler();

        handler.Register(
            "/v4/ai/transcription-model",
            _ => Json("{\"text\":\"" + new string('x', 2 * 1024 * 1024) + "\"}"));

        var service = CreateService(handler);
        await ConfigureJsonAlignmentAsync(service);

        var result = await service.TestAsync(
            new AiProviderTestRequest(
                "alignment-transcription", null, null, null));

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("size limit");
        handler.RecordedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Alignment_connection_test_honours_the_unsaved_selection()
    {
        // Found live: the form selected a dedicated JSON provider, the user
        // pressed "Test connection" before saving, and the probe still went
        // to the saved (voice, multipart) transport.
        var handler = new StubHttpMessageHandler();

        handler.Register(
            "/v4/ai/transcription-model",
            _ => Json("""{"text":"","durationInSeconds":1}"""));

        var service = CreateService(handler);

        var result = await service.TestAsync(
            new AiProviderTestRequest(
                "alignment-transcription",
                "https://transcription.example.test",
                "alignment-model",
                "alignment-key",
                AlignmentTranscriptionApiStyles.JsonBase64,
                UseVoiceTranscription: false));

        result.Ok.Should().BeTrue(result.Error);
        handler.RecordedRequestPaths.Should()
            .Equal("/v4/ai/transcription-model");
    }

    [Fact]
    public async Task Alignment_using_voice_tests_voice_multipart_not_saved_json()
    {
        var handler = new StubHttpMessageHandler();

        handler.Register(
            "/v1/audio/transcriptions",
            _ => Json("""{"text":"Voice result","language":"en","duration":1}"""));

        var service = CreateService(handler);

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            Llm: null,
            Stt: new AiProviderSectionUpdate(
                true,
                "https://voice.example.test",
                "voice-model",
                "voice-key"),
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                true,
                "https://dedicated.example.test",
                "dedicated-model",
                "dedicated-key",
                true,
                AlignmentTranscriptionApiStyles.JsonBase64)));

        var result = await service.TestAsync(
            new AiProviderTestRequest(
                "alignment-transcription", null, null, null));

        result.Ok.Should().BeTrue();
        result.Detail.Should().Contain("voice-model");
        handler.RecordedRequestPaths.Should()
            .Equal("/v1/audio/transcriptions");
    }

    [Fact]
    public async Task Alignment_using_voice_refuses_missing_voice_key()
    {
        var handler = new StubHttpMessageHandler();
        var service = CreateService(handler);

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                true,
                "https://dedicated.example.test",
                "dedicated-model",
                "dedicated-key",
                true,
                AlignmentTranscriptionApiStyles.JsonBase64)));

        var result = await service.TestAsync(
            new AiProviderTestRequest(
                "alignment-transcription", null, null, null));

        result.Ok.Should().BeFalse();
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Alignment_model_listing_uses_the_effective_configuration()
    {
        var handler = new StubHttpMessageHandler();
        string? token = null;

        handler.Register("/v1/models", request =>
        {
            token = request.Headers.Authorization?.Parameter;
            return Json(
                """{"data":[{"id":"alignment-model-a"},{"id":"alignment-model-b"}]}""");
        });

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
        token.Should().Be("test-key");
    }

    [Fact]
    public async Task Alignment_model_listing_uses_voice_key_when_reusing_voice()
    {
        var handler = new StubHttpMessageHandler();
        string? token = null;

        handler.Register("/v1/models", request =>
        {
            token = request.Headers.Authorization?.Parameter;
            return Json("""{"data":[{"id":"voice-model"}]}""");
        });

        var service = CreateService(handler);

        await service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            Llm: null,
            Stt: new AiProviderSectionUpdate(
                true,
                "https://voice.example.test",
                "voice-model",
                "voice-key"),
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                true,
                "https://dedicated.example.test",
                "dedicated-model",
                "dedicated-key",
                true,
                AlignmentTranscriptionApiStyles.JsonBase64)));

        var result = await service.ListModelsAsync(
            new AiProviderModelsRequest(
                "alignment-transcription", null, null));

        result.Models.Should().Equal("voice-model");
        token.Should().Be("voice-key");
    }

    [Fact]
    public async Task Alignment_key_decrypt_failure_uses_alignment_environment_fallback()
    {
        var path = _fixture.CreateDatabasePath();

        await using (var db = _fixture.CreateContext(path))
        {
            db.AiProviderSettings.Add(new AiProviderSettingsModel
            {
                Id = AiProviderSettingsModel.SingletonId,
                AlignmentTranscriptionEnabled = true,
                AlignmentTranscriptionBaseUrl = "https://alignment.example.test",
                AlignmentTranscriptionModel = "alignment-model",
                AlignmentTranscriptionUseVoiceTranscription = false,
                AlignmentTranscriptionApiKeyEncrypted = "invalid-ciphertext",
            });
            await db.SaveChangesAsync();
        }

        Environment.SetEnvironmentVariable(AlignmentEnvVariable, EnvKeyValue);
        try
        {
            var service = CreateService(
                new StubHttpMessageHandler(), path);

            var effective = await service.GetEffectiveAlignmentSttAsync();

            effective.ApiKey.Should().Be(EnvKeyValue);
            effective.KeyFromServerEnv.Should().BeTrue();
            effective.IsAvailable.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AlignmentEnvVariable, null);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Task<AiProviderSettingsResponse> ConfigureJsonAlignmentAsync(
        AiProviderSettingsService service) =>
        service.UpdateAsync(new AiProviderSettingsUpdateRequest(
            null,
            null,
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                true,
                "https://transcription.example.test",
                "alignment-model",
                "alignment-key",
                false,
                AlignmentTranscriptionApiStyles.JsonBase64)));

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json, Encoding.UTF8, "application/json"),
        };

    private AiProviderSettingsService CreateService(
        StubHttpMessageHandler handler,
        string? databasePath = null)
    {
        var path = databasePath ?? _fixture.CreateDatabasePath();

        using (var db = _fixture.CreateContext(path))
        {
            // EnsureCreated is idempotent.
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

    private sealed class TestDbContextFactory(string path)
        : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options);

        public Task<NostosDbContext> CreateDbContextAsync(
            CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }
}
