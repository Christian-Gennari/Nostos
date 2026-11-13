using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Persists, encrypts, and resolves effective AI provider settings across
/// database overrides and <c>appsettings.json</c>/environment fallbacks.
///
/// <para>
/// Secrets are encrypted at rest with ASP.NET Data Protection. They are never
/// returned to a client or logged. A decrypt failure degrades to the
/// environment variable without exposing the encrypted value.
/// </para>
///
/// <para>
/// SSRF is deliberately not mitigated: the endpoint is the owner's own gateway
/// on the trusted LAN, so HTTP and private hosts must be allowed.
/// Validation is limited to absolute HTTP/HTTPS URLs.
/// </para>
/// </summary>
internal sealed class AiProviderSettingsStore : IAiProviderConfigResolver
{
    private const string ProtectorPurpose = "Nostos.AiProviderSettings.ApiKey.v1";
    private const string AlignmentKeyEnvironmentVariable = "NOSTOS_ALIGNMENT_STT_TOKEN";

    private readonly IDbContextFactory<NostosDbContext> _dbFactory;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ILogger _logger;

    private readonly AiProviderSectionDescriptor _llm;
    private readonly AiProviderSectionDescriptor _stt;
    private readonly AiProviderSectionDescriptor _embedding;
    private readonly AiProviderSectionDescriptor _tts;
    private readonly AiProviderSectionDescriptor _alignment;

    public AiProviderSettingsStore(
        IDbContextFactory<NostosDbContext> dbFactory,
        AssistantOptions assistantOptions,
        SpeechOptions speechOptions,
        EmbeddingOptions embeddingOptions,
        IDataProtectionProvider dataProtection,
        ILogger logger,
        ReadAloudOptions? readAloudOptions = null)
    {
        _dbFactory = dbFactory;
        _dataProtection = dataProtection;
        _logger = logger;

        var ttsOptions = readAloudOptions ?? new ReadAloudOptions();

        _llm = new AiProviderSectionDescriptor(
            AiProviderKind.Llm,
            "LLM",
            row => row.LlmEnabled,
            (row, value) => row.LlmEnabled = value,
            row => row.LlmBaseUrl,
            (row, value) => row.LlmBaseUrl = value,
            row => row.LlmModel,
            (row, value) => row.LlmModel = value,
            row => row.LlmApiKeyEncrypted,
            (row, value) => row.LlmApiKeyEncrypted = value,
            () => assistantOptions.Enabled,
            () => assistantOptions.BaseUrl,
            () => assistantOptions.Model,
            () => assistantOptions.ApiKeyEnvironmentVariable);

        _stt = new AiProviderSectionDescriptor(
            AiProviderKind.Stt,
            "STT",
            row => row.SttEnabled,
            (row, value) => row.SttEnabled = value,
            row => row.SttBaseUrl,
            (row, value) => row.SttBaseUrl = value,
            row => row.SttModel,
            (row, value) => row.SttModel = value,
            row => row.SttApiKeyEncrypted,
            (row, value) => row.SttApiKeyEncrypted = value,
            () => speechOptions.Enabled,
            () => speechOptions.BaseUrl,
            () => speechOptions.Model,
            () => speechOptions.ApiKeyEnvironmentVariable);

        _embedding = new AiProviderSectionDescriptor(
            AiProviderKind.Embedding,
            "embedding",
            row => row.EmbeddingEnabled,
            (row, value) => row.EmbeddingEnabled = value,
            row => row.EmbeddingBaseUrl,
            (row, value) => row.EmbeddingBaseUrl = value,
            row => row.EmbeddingModel,
            (row, value) => row.EmbeddingModel = value,
            row => row.EmbeddingApiKeyEncrypted,
            (row, value) => row.EmbeddingApiKeyEncrypted = value,
            () => embeddingOptions.Enabled,
            () => embeddingOptions.BaseUrl,
            () => embeddingOptions.Model,
            () => embeddingOptions.ApiKeyEnvironmentVariable);

        _tts = new AiProviderSectionDescriptor(
            AiProviderKind.Tts,
            "read aloud",
            row => row.TtsEnabled,
            (row, value) => row.TtsEnabled = value,
            row => row.TtsBaseUrl,
            (row, value) => row.TtsBaseUrl = value,
            row => row.TtsModel,
            (row, value) => row.TtsModel = value,
            row => row.TtsApiKeyEncrypted,
            (row, value) => row.TtsApiKeyEncrypted = value,
            () => ttsOptions.Enabled,
            () => ttsOptions.BaseUrl,
            () => ttsOptions.Model,
            () => ttsOptions.ApiKeyEnvironmentVariable);

        _alignment = new AiProviderSectionDescriptor(
            AiProviderKind.AlignmentTranscription,
            "alignment transcription",
            row => row.AlignmentTranscriptionEnabled,
            (row, value) => row.AlignmentTranscriptionEnabled = value,
            row => row.AlignmentTranscriptionBaseUrl,
            (row, value) => row.AlignmentTranscriptionBaseUrl = value,
            row => row.AlignmentTranscriptionModel,
            (row, value) => row.AlignmentTranscriptionModel = value,
            row => row.AlignmentTranscriptionApiKeyEncrypted,
            (row, value) => row.AlignmentTranscriptionApiKeyEncrypted = value,
            fallbackEnabled: static () => false,
            fallbackBaseUrl: static () => string.Empty,
            fallbackModel: static () => string.Empty,
            static () => AlignmentKeyEnvironmentVariable);
    }

    private IDataProtector CreateProtector() =>
        _dataProtection.CreateProtector(ProtectorPurpose);

    // ------------------------------------------------------------------
    // Effective configuration
    // ------------------------------------------------------------------

    public async Task<EffectiveAiProviderConfig> GetEffectiveLlmAsync(
        CancellationToken ct = default) =>
        ResolveSection(await LoadRowAsync(ct), _llm);

    public async Task<EffectiveAiProviderConfig> GetEffectiveSttAsync(
        CancellationToken ct = default) =>
        ResolveSection(await LoadRowAsync(ct), _stt);

    public async Task<EffectiveAiProviderConfig> GetEffectiveEmbeddingAsync(
        CancellationToken ct = default) =>
        ResolveSection(await LoadRowAsync(ct), _embedding);

    public async Task<EffectiveAlignmentSttConfig> GetEffectiveAlignmentSttAsync(
        CancellationToken ct = default) =>
        ResolveAlignmentStt(await LoadRowAsync(ct));

    public async Task<EffectiveAiProviderConfig> GetEffectiveTtsAsync(
        CancellationToken ct = default) =>
        ResolveSection(await LoadRowAsync(ct), _tts);

    internal async Task<EffectiveAlignmentSttConfig> ResolveAlignmentProbeConfigAsync(
        bool? useVoiceTranscriptionOverride,
        string? apiStyleOverride,
        CancellationToken ct = default)
    {
        var row = await LoadRowAsync(ct);
        var saved = ResolveAlignmentStt(row);
        var useVoice = useVoiceTranscriptionOverride ?? saved.UseVoiceTranscription;
        var effective = useVoice
            ? ResolveSection(row, _stt)
            : ResolveSection(row, _alignment);
        var apiStyle = useVoice
            ? AlignmentTranscriptionApiStyles.OpenAiMultipart
            : string.IsNullOrWhiteSpace(apiStyleOverride)
                ? row?.AlignmentTranscriptionApiStyle
                    ?? AlignmentTranscriptionApiStyles.OpenAiMultipart
                : apiStyleOverride.Trim();

        return new EffectiveAlignmentSttConfig(effective, apiStyle, useVoice);
    }

    // ------------------------------------------------------------------
    // GET / PUT
    // ------------------------------------------------------------------

    public async Task<AiProviderSettingsResponse> GetAsync(
        CancellationToken ct = default)
    {
        var row = await LoadRowAsync(ct);
        var effectiveAlignment = ResolveAlignmentStt(row);

        // The saved dedicated section remains visible while voice reuse is on.
        // For a legacy row with no alignment overrides, show the voice values
        // without accidentally persisting those values as dedicated settings.
        var displayedAlignment = HasDedicatedAlignmentSettings(row)
            ? ResolveSection(row, _alignment)
            : effectiveAlignment.Provider;

        return new AiProviderSettingsResponse(
            ToDto(ResolveSection(row, _llm)),
            ToDto(ResolveSection(row, _stt)),
            ToDto(ResolveSection(row, _embedding)),
            new AlignmentTranscriptionSectionDto(
                displayedAlignment.Enabled,
                displayedAlignment.BaseUrl,
                displayedAlignment.Model,
                displayedAlignment.HasKey,
                displayedAlignment.KeyFromServerEnv,
                effectiveAlignment.UseVoiceTranscription,
                row?.AlignmentTranscriptionApiStyle
                    ?? AlignmentTranscriptionApiStyles.OpenAiMultipart),
            ToDto(ResolveSection(row, _tts)));
    }

    public async Task<AiProviderSettingsResponse> UpdateAsync(
        AiProviderSettingsUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AiProviderSettings
            .FirstOrDefaultAsync(
                s => s.Id == AiProviderSettingsModel.SingletonId, ct);

        if (row is null)
        {
            row = new AiProviderSettingsModel
            {
                Id = AiProviderSettingsModel.SingletonId,
            };
            db.AiProviderSettings.Add(row);
        }

        if (request.Llm is not null)
        {
            ApplySectionUpdate(row, _llm, request.Llm);
        }

        if (request.Stt is not null)
        {
            ApplySectionUpdate(row, _stt, request.Stt);
        }

        if (request.Embedding is not null)
        {
            ApplySectionUpdate(row, _embedding, request.Embedding);
        }

        if (request.AlignmentTranscription is not null)
        {
            ApplyAlignmentUpdate(row, request.AlignmentTranscription);
        }

        if (request.Tts is not null)
        {
            ApplySectionUpdate(row, _tts, request.Tts);
        }

        row.UpdatedAtUtc = DateTime.UtcNow;

        // All provider fields and encrypted credentials are written in the
        // same EF unit of work. No separate connection, table or SQL upsert.
        await db.SaveChangesAsync(ct);

        return await GetAsync(ct);
    }

    private void ApplySectionUpdate(
        AiProviderSettingsModel row,
        AiProviderSectionDescriptor descriptor,
        AiProviderSectionUpdate update) =>
        ApplySectionFields(
            row,
            descriptor,
            update.Enabled,
            update.BaseUrl,
            update.Model,
            update.ApiKey);

    private void ApplyAlignmentUpdate(
        AiProviderSettingsModel row,
        AlignmentTranscriptionSectionUpdate update)
    {
        if (update.ApiStyle is not null
            && !AlignmentTranscriptionApiStyles.IsSupported(update.ApiStyle))
        {
            throw new AiProviderValidationException(
                "apiStyle must be 'openai-multipart' or 'json-base64'.");
        }

        ApplySectionFields(
            row,
            _alignment,
            update.Enabled,
            update.BaseUrl,
            update.Model,
            update.ApiKey);

        if (update.UseVoiceTranscription is { } useVoice)
        {
            row.AlignmentTranscriptionUseVoiceTranscription = useVoice;
        }

        if (update.ApiStyle is not null)
        {
            row.AlignmentTranscriptionApiStyle = update.ApiStyle;
        }
    }

    private void ApplySectionFields(
        AiProviderSettingsModel row,
        AiProviderSectionDescriptor descriptor,
        bool? enabled,
        string? baseUrl,
        string? model,
        string? apiKey)
    {
        ValidateBaseUrl(baseUrl);
        ValidateModel(model);

        if (enabled is { } enabledValue)
        {
            descriptor.SetEnabled(row, enabledValue);
        }

        if (baseUrl is not null)
        {
            descriptor.SetBaseUrl(row, baseUrl.Trim());
        }

        if (model is not null)
        {
            descriptor.SetModel(row, model.Trim());
        }

        descriptor.SetApiKeyEncrypted(
            row,
            ApplyKey(apiKey, descriptor.GetApiKeyEncrypted(row)));
    }

    /// <summary>
    /// API key contract: omitted/null = unchanged, empty = clear,
    /// non-empty = store encrypted.
    /// </summary>
    private string? ApplyKey(string? apiKey, string? existingEncrypted)
    {
        if (apiKey is null)
        {
            return existingEncrypted;
        }

        var trimmed = apiKey.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return CreateProtector().Protect(trimmed);
    }

    /// <summary>
    /// SSRF is deliberately not mitigated: the endpoint is the owner's own gateway
    /// on the trusted LAN, so HTTP and private hosts must be allowed.
    /// Validation is limited to absolute HTTP/HTTPS URLs.
    /// </summary>
    internal static void ValidateBaseUrl(string? baseUrl)
    {
        if (baseUrl is null)
        {
            return;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AiProviderValidationException(
                "baseUrl must be an absolute http:// or https:// URL.");
        }
    }

    internal static void ValidateModel(string? model)
    {
        if (model is not null && string.IsNullOrWhiteSpace(model))
        {
            throw new AiProviderValidationException(
                "model must not be empty.");
        }
    }

    private async Task<AiProviderSettingsModel?> LoadRowAsync(
        CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AiProviderSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Id == AiProviderSettingsModel.SingletonId, ct);
    }

    private EffectiveAiProviderConfig ResolveSection(
        AiProviderSettingsModel? row,
        AiProviderSectionDescriptor descriptor)
    {
        var encrypted = row is null ? null : descriptor.GetApiKeyEncrypted(row);
        var (key, fromEnv) = ResolveKey(
            encrypted,
            descriptor.ApiKeyEnvironmentVariable,
            descriptor.Surface);

        return new EffectiveAiProviderConfig(
            (row is null ? null : descriptor.GetEnabled(row)) ?? descriptor.FallbackEnabled,
            OverrideOrFallback(
                row is null ? null : descriptor.GetBaseUrl(row),
                descriptor.FallbackBaseUrl),
            OverrideOrFallback(
                row is null ? null : descriptor.GetModel(row),
                descriptor.FallbackModel),
            descriptor.ApiKeyEnvironmentVariable,
            key,
            fromEnv);
    }

    private EffectiveAlignmentSttConfig ResolveAlignmentStt(
        AiProviderSettingsModel? row)
    {
        if (row?.AlignmentTranscriptionUseVoiceTranscription ?? true)
        {
            return new EffectiveAlignmentSttConfig(
                ResolveSection(row, _stt),
                AlignmentTranscriptionApiStyles.OpenAiMultipart,
                UseVoiceTranscription: true);
        }

        return new EffectiveAlignmentSttConfig(
            ResolveSection(row, _alignment),
            row?.AlignmentTranscriptionApiStyle
                ?? AlignmentTranscriptionApiStyles.OpenAiMultipart,
            UseVoiceTranscription: false);
    }

    private static bool HasDedicatedAlignmentSettings(
        AiProviderSettingsModel? row) =>
        row is not null
        && (row.AlignmentTranscriptionEnabled.HasValue
            || row.AlignmentTranscriptionBaseUrl is not null
            || row.AlignmentTranscriptionModel is not null
            || row.AlignmentTranscriptionApiKeyEncrypted is not null
            || row.AlignmentTranscriptionUseVoiceTranscription == false
            || row.AlignmentTranscriptionApiStyle is not null);

    private (string? Key, bool FromEnv) ResolveKey(
        string? encrypted,
        string environmentVariable,
        string surface)
    {
        if (!string.IsNullOrWhiteSpace(encrypted))
        {
            try
            {
                var plaintext = CreateProtector().Unprotect(encrypted);
                if (!string.IsNullOrWhiteSpace(plaintext))
                {
                    return (plaintext, false);
                }
            }
            catch (Exception ex) when (
                ex is CryptographicException
                or FormatException
                or ArgumentException)
            {
                _logger.LogWarning(
                    "Stored {Surface} API key could not be decrypted ({ErrorType}); "
                        + "falling back to environment variable '{Variable}'. "
                        + "Re-enter and save this key in Settings if the original Data Protection key ring is unavailable.",
                    surface,
                    ex.GetType().Name,
                    environmentVariable);
            }
        }

        var env = Environment.GetEnvironmentVariable(environmentVariable);

        return string.IsNullOrWhiteSpace(env)
            ? (null, false)
            : (env.Trim(), true);
    }

    private static string OverrideOrFallback(
        string? stored,
        string fallback) =>
        string.IsNullOrWhiteSpace(stored) ? fallback : stored;

    private static AiProviderSectionDto ToDto(
        EffectiveAiProviderConfig config) =>
        new(
            config.Enabled,
            config.BaseUrl,
            config.Model,
            config.HasKey,
            config.KeyFromServerEnv);
}
