using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// Single-responsibility tests for <see cref="AiProviderSettingsStore"/>:
/// unified section descriptors, EF persistence, Data Protection encryption,
/// environment-variable fallback, and URL/model validation.
/// </summary>
public sealed class AiProviderSettingsStoreTests : IDisposable
{
    private const string LlmEnvVar = "NOSTOS_STORE_TEST_LLM_KEY";
    private const string SttEnvVar = "NOSTOS_STORE_TEST_STT_KEY";
    private const string EmbeddingEnvVar = "NOSTOS_STORE_TEST_EMBEDDING_KEY";
    private const string TtsEnvVar = "NOSTOS_STORE_TEST_TTS_KEY";
    private const string AlignmentEnvVar = "NOSTOS_ALIGNMENT_STT_TOKEN";

    private readonly SqliteTestFixture _fixture = new();

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LlmEnvVar, null);
        Environment.SetEnvironmentVariable(SttEnvVar, null);
        Environment.SetEnvironmentVariable(EmbeddingEnvVar, null);
        Environment.SetEnvironmentVariable(TtsEnvVar, null);
        Environment.SetEnvironmentVariable(AlignmentEnvVar, null);
        _fixture.Dispose();
    }

    [Theory]
    [InlineData("llm", "http://192.168.1.40:11434/v1", "qwen3:8b", "sk-llm-store-secret")]
    [InlineData("stt", "http://10.0.0.15:8000/v1", "whisper-large-v3", "sk-stt-store-secret")]
    [InlineData("embedding", "http://172.16.0.20:11434/v1", "nomic-embed-text", "sk-emb-store-secret")]
    [InlineData("tts", "http://kokoro.lan:8880", "hexgrad/Kokoro-82M", "sk-tts-store-secret")]
    public async Task Standard_sections_share_identical_update_encryption_and_resolution_semantics(
        string section,
        string lanBaseUrl,
        string model,
        string apiKey)
    {
        var path = _fixture.CreateDatabasePath();
        var store = CreateStore(path);

        var update = new AiProviderSectionUpdate(
            Enabled: true,
            BaseUrl: $"  {lanBaseUrl}  ",
            Model: $"  {model}  ",
            ApiKey: $"  {apiKey}  ");

        var request = section switch
        {
            "llm" => new AiProviderSettingsUpdateRequest(update, null),
            "stt" => new AiProviderSettingsUpdateRequest(null, update),
            "embedding" => new AiProviderSettingsUpdateRequest(null, null, Embedding: update),
            _ => new AiProviderSettingsUpdateRequest(null, null, Tts: update),
        };

        var updated = await store.UpdateAsync(request);
        var dto = section switch
        {
            "llm" => updated.Llm,
            "stt" => updated.Stt,
            "embedding" => updated.Embedding!,
            _ => updated.Tts!,
        };

        dto.Enabled.Should().BeTrue();
        dto.BaseUrl.Should().Be(lanBaseUrl);
        dto.Model.Should().Be(model);
        dto.HasKey.Should().BeTrue();
        dto.KeyFromServerEnv.Should().BeFalse();

        IAiProviderConfigResolver resolver = store;
        var effective = section switch
        {
            "llm" => await resolver.GetEffectiveLlmAsync(),
            "stt" => await resolver.GetEffectiveSttAsync(),
            "embedding" => await resolver.GetEffectiveEmbeddingAsync(),
            _ => await resolver.GetEffectiveTtsAsync(),
        };

        effective.Enabled.Should().BeTrue();
        effective.BaseUrl.Should().Be(lanBaseUrl);
        effective.Model.Should().Be(model);
        effective.ApiKey.Should().Be(apiKey);
        effective.HasKey.Should().BeTrue();
        effective.KeyFromServerEnv.Should().BeFalse();

        string? ciphertextBefore;
        await using (var db = _fixture.CreateContext(path))
        {
            var row = await db.AiProviderSettings.SingleAsync();
            ciphertextBefore = section switch
            {
                "llm" => row.LlmApiKeyEncrypted,
                "stt" => row.SttApiKeyEncrypted,
                "embedding" => row.EmbeddingApiKeyEncrypted,
                _ => row.TtsApiKeyEncrypted,
            };
            ciphertextBefore.Should().NotBeNullOrWhiteSpace();
            ciphertextBefore.Should().NotContain(apiKey);
        }

        // Omitting ApiKey preserves the stored ciphertext.
        var preserveRequest = section switch
        {
            "llm" => new AiProviderSettingsUpdateRequest(new AiProviderSectionUpdate(false, null, null, null), null),
            "stt" => new AiProviderSettingsUpdateRequest(null, new AiProviderSectionUpdate(false, null, null, null)),
            "embedding" => new AiProviderSettingsUpdateRequest(null, null, Embedding: new AiProviderSectionUpdate(false, null, null, null)),
            _ => new AiProviderSettingsUpdateRequest(null, null, Tts: new AiProviderSectionUpdate(false, null, null, null)),
        };

        var preserved = await store.UpdateAsync(preserveRequest);
        var preservedDto = section switch
        {
            "llm" => preserved.Llm,
            "stt" => preserved.Stt,
            "embedding" => preserved.Embedding!,
            _ => preserved.Tts!,
        };
        preservedDto.Enabled.Should().BeFalse();
        preservedDto.HasKey.Should().BeTrue();

        await using (var db = _fixture.CreateContext(path))
        {
            var row = await db.AiProviderSettings.SingleAsync();
            var ciphertextAfter = section switch
            {
                "llm" => row.LlmApiKeyEncrypted,
                "stt" => row.SttApiKeyEncrypted,
                "embedding" => row.EmbeddingApiKeyEncrypted,
                _ => row.TtsApiKeyEncrypted,
            };
            ciphertextAfter.Should().Be(ciphertextBefore);
        }

        // Whitespace-only ApiKey clears the stored key.
        var clearRequest = section switch
        {
            "llm" => new AiProviderSettingsUpdateRequest(new AiProviderSectionUpdate(null, null, null, "   "), null),
            "stt" => new AiProviderSettingsUpdateRequest(null, new AiProviderSectionUpdate(null, null, null, "   ")),
            "embedding" => new AiProviderSettingsUpdateRequest(null, null, Embedding: new AiProviderSectionUpdate(null, null, null, "   ")),
            _ => new AiProviderSettingsUpdateRequest(null, null, Tts: new AiProviderSectionUpdate(null, null, null, "   ")),
        };

        var cleared = await store.UpdateAsync(clearRequest);
        var clearedDto = section switch
        {
            "llm" => cleared.Llm,
            "stt" => cleared.Stt,
            "embedding" => cleared.Embedding!,
            _ => cleared.Tts!,
        };
        clearedDto.HasKey.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://192.168.1.10:20128/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("https://gateway.example.test/v1")]
    public void ValidateBaseUrl_allows_trusted_lan_http_and_https_urls(string url)
    {
        var act = () => AiProviderSettingsStore.ValidateBaseUrl(url);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://gateway.lan/v1")]
    [InlineData("/relative/path")]
    public void ValidateBaseUrl_rejects_non_http_or_non_absolute_urls(string url)
    {
        var act = () => AiProviderSettingsStore.ValidateBaseUrl(url);

        act.Should().Throw<AiProviderValidationException>();
    }

    [Fact]
    public async Task ResolveAlignmentProbeConfigAsync_applies_unsaved_overrides_without_persisting()
    {
        var path = _fixture.CreateDatabasePath();
        var store = CreateStore(path);

        await store.UpdateAsync(new AiProviderSettingsUpdateRequest(
            Llm: null,
            Stt: new AiProviderSectionUpdate(true, "http://voice.lan:8000", "voice-model", "voice-key"),
            AlignmentTranscription: new AlignmentTranscriptionSectionUpdate(
                Enabled: true,
                BaseUrl: "http://align.lan:9000",
                Model: "align-model",
                ApiKey: "align-key",
                UseVoiceTranscription: true,
                ApiStyle: AlignmentTranscriptionApiStyles.JsonBase64)));

        var overriddenToDedicated = await store.ResolveAlignmentProbeConfigAsync(
            useVoiceTranscriptionOverride: false,
            apiStyleOverride: AlignmentTranscriptionApiStyles.JsonBase64);

        overriddenToDedicated.UseVoiceTranscription.Should().BeFalse();
        overriddenToDedicated.ApiStyle.Should().Be(AlignmentTranscriptionApiStyles.JsonBase64);
        overriddenToDedicated.BaseUrl.Should().Be("http://align.lan:9000");
        overriddenToDedicated.Model.Should().Be("align-model");
        overriddenToDedicated.ApiKey.Should().Be("align-key");

        // Saved setting remains UseVoiceTranscription = true.
        var persisted = await store.GetEffectiveAlignmentSttAsync();
        persisted.UseVoiceTranscription.Should().BeTrue();
        persisted.Model.Should().Be("voice-model");
    }

    [Fact]
    public async Task Decrypt_failure_logs_warning_without_exposing_ciphertext_and_falls_back_to_env()
    {
        Environment.SetEnvironmentVariable(EmbeddingEnvVar, "sk-env-embedding-fallback");
        var path = _fixture.CreateDatabasePath();

        await using (var db = _fixture.CreateContext(path))
        {
            db.AiProviderSettings.Add(new AiProviderSettingsModel
            {
                Id = AiProviderSettingsModel.SingletonId,
                EmbeddingEnabled = true,
                EmbeddingApiKeyEncrypted = "corrupted-ciphertext-payload",
            });
            await db.SaveChangesAsync();
        }

        var logger = new RecordingLogger();
        var store = CreateStore(path, logger);

        var effective = await store.GetEffectiveEmbeddingAsync();

        effective.ApiKey.Should().Be("sk-env-embedding-fallback");
        effective.KeyFromServerEnv.Should().BeTrue();
        logger.Warnings.Should().ContainSingle();
        logger.Warnings[0].Should().Contain("embedding");
        logger.Warnings[0].Should().NotContain("corrupted-ciphertext-payload");
        logger.Warnings[0].Should().NotContain("sk-env-embedding-fallback");
    }

    private AiProviderSettingsStore CreateStore(string databasePath, ILogger? logger = null)
    {
        using (var _ = _fixture.CreateContext(databasePath))
        {
        }

        return new AiProviderSettingsStore(
            new TestDbContextFactory(databasePath),
            new AssistantOptions
            {
                Enabled = false,
                BaseUrl = "http://llm.fallback:8000/v1",
                Model = "fallback-llm",
                ApiKeyEnvironmentVariable = LlmEnvVar,
            },
            new SpeechOptions
            {
                Enabled = false,
                BaseUrl = "http://stt.fallback:8000",
                Model = "fallback-stt",
                ApiKeyEnvironmentVariable = SttEnvVar,
            },
            new EmbeddingOptions
            {
                Enabled = false,
                BaseUrl = "http://emb.fallback:8000/v1",
                Model = "fallback-emb",
                ApiKeyEnvironmentVariable = EmbeddingEnvVar,
            },
            new EphemeralDataProtectionProvider(),
            logger ?? NullLogger.Instance,
            new ReadAloudOptions
            {
                Enabled = false,
                BaseUrl = "http://tts.fallback:8880",
                Model = "fallback-tts",
                ApiKeyEnvironmentVariable = TtsEnvVar,
            });
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

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
