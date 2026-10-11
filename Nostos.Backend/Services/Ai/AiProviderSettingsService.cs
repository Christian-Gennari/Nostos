using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// The one place the assistant's LLM, voice STT, alignment transcription and
/// passage-embedding configuration is read and written.
///
/// <para>
/// Effective config = the stored override when set, else the existing
/// appsettings.json/environment fallback. Alignment transcription can reuse
/// voice STT, or select its own saved configuration and wire format.
/// </para>
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
public sealed class AiProviderSettingsService(
    IDbContextFactory<NostosDbContext> dbFactory,
    AssistantOptions assistantOptions,
    SpeechOptions speechOptions,
    EmbeddingOptions embeddingOptions,
    IDataProtectionProvider dataProtection,
    IHttpClientFactory httpClientFactory,
    ILogger<AiProviderSettingsService> logger) : IAiProviderSettingsService
{
    /// <summary>Registered HTTP client for ordinary settings probes.</summary>
    public const string HttpClientName = "ai-provider-settings";

    private const string ProtectorPurpose = "Nostos.AiProviderSettings.ApiKey.v1";
    private const string AlignmentKeyEnvironmentVariable = "NOSTOS_ALIGNMENT_STT_TOKEN";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private IDataProtector CreateProtector() =>
        dataProtection.CreateProtector(ProtectorPurpose);

    // ------------------------------------------------------------------
    // Effective configuration
    // ------------------------------------------------------------------

    public async Task<EffectiveAiProviderConfig> GetEffectiveLlmAsync(
        CancellationToken ct = default) =>
        ResolveLlm(await LoadRowAsync(ct));

    public async Task<EffectiveAiProviderConfig> GetEffectiveSttAsync(
        CancellationToken ct = default) =>
        ResolveStt(await LoadRowAsync(ct));

    public async Task<EffectiveAiProviderConfig> GetEffectiveEmbeddingAsync(
        CancellationToken ct = default) =>
        ResolveEmbedding(await LoadRowAsync(ct));

    public async Task<EffectiveAlignmentSttConfig> GetEffectiveAlignmentSttAsync(
        CancellationToken ct = default) =>
        ResolveAlignmentStt(await LoadRowAsync(ct));

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
            ? ResolveStoredAlignment(row)
            : effectiveAlignment.Provider;

        return new AiProviderSettingsResponse(
            ToDto(ResolveLlm(row)),
            ToDto(ResolveStt(row)),
            ToDto(ResolveEmbedding(row)),
            new AlignmentTranscriptionSectionDto(
                displayedAlignment.Enabled,
                displayedAlignment.BaseUrl,
                displayedAlignment.Model,
                displayedAlignment.HasKey,
                displayedAlignment.KeyFromServerEnv,
                effectiveAlignment.UseVoiceTranscription,
                row?.AlignmentTranscriptionApiStyle
                    ?? AlignmentTranscriptionApiStyles.OpenAiMultipart));
    }

    public async Task<AiProviderSettingsResponse> UpdateAsync(
        AiProviderSettingsUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
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
            ApplyLlmUpdate(row, request.Llm);
        }

        if (request.Stt is not null)
        {
            ApplySttUpdate(row, request.Stt);
        }

        if (request.Embedding is not null)
        {
            ApplyEmbeddingUpdate(row, request.Embedding);
        }

        if (request.AlignmentTranscription is not null)
        {
            ApplyAlignmentUpdate(row, request.AlignmentTranscription);
        }

        row.UpdatedAtUtc = DateTime.UtcNow;

        // All provider fields and encrypted credentials are written in the
        // same EF unit of work. No separate connection, table or SQL upsert.
        await db.SaveChangesAsync(ct);

        return await GetAsync(ct);
    }

    private void ApplyLlmUpdate(
        AiProviderSettingsModel row,
        AiProviderSectionUpdate update)
    {
        ValidateBaseUrl(update.BaseUrl);
        ValidateModel(update.Model);

        if (update.Enabled is { } enabled)
        {
            row.LlmEnabled = enabled;
        }

        if (update.BaseUrl is not null)
        {
            row.LlmBaseUrl = update.BaseUrl.Trim();
        }

        if (update.Model is not null)
        {
            row.LlmModel = update.Model.Trim();
        }

        row.LlmApiKeyEncrypted = ApplyKey(
            update.ApiKey, row.LlmApiKeyEncrypted);
    }

    private void ApplySttUpdate(
        AiProviderSettingsModel row,
        AiProviderSectionUpdate update)
    {
        ValidateBaseUrl(update.BaseUrl);
        ValidateModel(update.Model);

        if (update.Enabled is { } enabled)
        {
            row.SttEnabled = enabled;
        }

        if (update.BaseUrl is not null)
        {
            row.SttBaseUrl = update.BaseUrl.Trim();
        }

        if (update.Model is not null)
        {
            row.SttModel = update.Model.Trim();
        }

        row.SttApiKeyEncrypted = ApplyKey(
            update.ApiKey, row.SttApiKeyEncrypted);
    }

    private void ApplyEmbeddingUpdate(
        AiProviderSettingsModel row,
        AiProviderSectionUpdate update)
    {
        ValidateBaseUrl(update.BaseUrl);
        ValidateModel(update.Model);

        if (update.Enabled is { } enabled)
        {
            row.EmbeddingEnabled = enabled;
        }

        if (update.BaseUrl is not null)
        {
            row.EmbeddingBaseUrl = update.BaseUrl.Trim();
        }

        if (update.Model is not null)
        {
            row.EmbeddingModel = update.Model.Trim();
        }

        row.EmbeddingApiKeyEncrypted = ApplyKey(
            update.ApiKey, row.EmbeddingApiKeyEncrypted);
    }

    private void ApplyAlignmentUpdate(
        AiProviderSettingsModel row,
        AlignmentTranscriptionSectionUpdate update)
    {
        ValidateBaseUrl(update.BaseUrl);
        ValidateModel(update.Model);

        if (update.ApiStyle is not null
            && !AlignmentTranscriptionApiStyles.IsSupported(update.ApiStyle))
        {
            throw new AiProviderValidationException(
                "apiStyle must be 'openai-multipart' or 'json-base64'.");
        }

        if (update.Enabled is { } enabled)
        {
            row.AlignmentTranscriptionEnabled = enabled;
        }

        if (update.BaseUrl is not null)
        {
            row.AlignmentTranscriptionBaseUrl = update.BaseUrl.Trim();
        }

        if (update.Model is not null)
        {
            row.AlignmentTranscriptionModel = update.Model.Trim();
        }

        row.AlignmentTranscriptionApiKeyEncrypted = ApplyKey(
            update.ApiKey,
            row.AlignmentTranscriptionApiKeyEncrypted);

        if (update.UseVoiceTranscription is { } useVoice)
        {
            row.AlignmentTranscriptionUseVoiceTranscription = useVoice;
        }

        if (update.ApiStyle is not null)
        {
            row.AlignmentTranscriptionApiStyle = update.ApiStyle;
        }
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

    private static void ValidateBaseUrl(string? baseUrl)
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

    private static void ValidateModel(string? model)
    {
        if (model is not null && string.IsNullOrWhiteSpace(model))
        {
            throw new AiProviderValidationException(
                "model must not be empty.");
        }
    }

    // ------------------------------------------------------------------
    // Model listing
    // ------------------------------------------------------------------

    public async Task<AiProviderModelsResponse> ListModelsAsync(
        AiProviderModelsRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kind = ParseKind(request.Kind);

        // Resolve once, including API style, so a setting change between
        // queries cannot mix one transport with another configuration.
        var alignment = kind == AiProviderKind.AlignmentTranscription
            ? await GetEffectiveAlignmentSttAsync(ct)
            : null;

        var effective = alignment?.Provider
            ?? await EffectiveAsync(kind, ct);

        var baseUrl = ResolveBaseUrl(request.BaseUrl, effective);
        ValidateResolvedBaseUrl(baseUrl, kind);
        var apiKey = ResolveApiKey(request.ApiKey, effective);

        // Model discovery is a separate API, not part of either transcription
        // wire contract. It is supported only when the upstream exposes a
        // compatible models endpoint. However, use the SAME named HTTP client
        // as real calls, including redirect and timeout policy.
        var clientName = alignment?.ApiStyle switch
        {
            AlignmentTranscriptionApiStyles.JsonBase64 =>
                JsonBase64SttProvider.HttpClientName,
            AlignmentTranscriptionApiStyles.OpenAiMultipart =>
                NineRouterSttProvider.HttpClientName,
            _ => HttpClientName,
        };

        string? lastError = null;

        foreach (var url in ModelDiscoveryUrls(baseUrl))
        {
            var (ok, body, error) = await SendAsync(
                HttpMethod.Get,
                url,
                apiKey,
                null,
                ct,
                clientName);

            if (!ok)
            {
                lastError = error;
                continue;
            }

            var models = ParseModels(body);
            if (models is null)
            {
                lastError =
                    "the provider returned a body that did not look like a model list";
                continue;
            }

            return new AiProviderModelsResponse(models);
        }

        throw new AiProviderUpstreamException(
            lastError ?? "the provider did not return a model list");
    }

    private static IEnumerable<string> ModelDiscoveryUrls(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');

        // When the configured URL is a concrete transcription endpoint,
        // discover relative to its API origin instead.
        const string jsonPath = "/v4/ai/transcription-model";

        if (trimmed.EndsWith(jsonPath, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^jsonPath.Length];
        }

        // Preserve ordinary /v1 handling and avoid trying the same URL twice.
        var first = Combine(trimmed, "models");
        var second = Combine(trimmed, "v1/models");

        yield return first;

        if (!string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            yield return second;
        }
    }

    // ------------------------------------------------------------------
    // Connection tests
    // ------------------------------------------------------------------

    /// <summary>
    /// Always succeeds at the HTTP layer: a refused provider is represented
    /// by {ok:false,error} so the UI can display the failure.
    /// </summary>
    public async Task<AiProviderTestResult> TestAsync(
        AiProviderTestRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var kind = ParseKind(request.Kind);

            if (kind == AiProviderKind.AlignmentTranscription)
            {
                return await TestAlignmentAsync(request, ct);
            }

            var effective = await EffectiveAsync(kind, ct);
            var baseUrl = ResolveBaseUrl(request.BaseUrl, effective);
            ValidateResolvedBaseUrl(baseUrl, kind);

            var model = string.IsNullOrWhiteSpace(request.Model)
                ? effective.Model
                : request.Model.Trim();
            var apiKey = ResolveApiKey(request.ApiKey, effective);

            if (string.IsNullOrWhiteSpace(model))
            {
                return new AiProviderTestResult(
                    false, null, $"{KindLabel(kind)} model is not configured.");
            }

            return kind switch
            {
                AiProviderKind.Llm =>
                    await TestLlmAsync(baseUrl, model, apiKey, ct),
                AiProviderKind.Embedding =>
                    await TestEmbeddingAsync(baseUrl, model, apiKey, ct),
                _ =>
                    await TestSttAsync(baseUrl, model, apiKey, ct),
            };
        }
        catch (AiProviderValidationException ex)
        {
            return new AiProviderTestResult(false, null, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AiProviderTestResult(false, null, ex.Message);
        }
    }

    private async Task<AiProviderTestResult> TestAlignmentAsync(
        AiProviderTestRequest request,
        CancellationToken ct)
    {
        // Resolve once. The form may test a selection it has not saved yet:
        // an explicit voice-reuse or API-style choice in the request wins over
        // the saved one, so "test" probes what "save" would then use.
        var row = await LoadRowAsync(ct);
        var saved = ResolveAlignmentStt(row);
        var useVoice = request.UseVoiceTranscription ?? saved.UseVoiceTranscription;
        var effective = useVoice ? ResolveStt(row) : ResolveStoredAlignment(row);
        var apiStyle = useVoice
            ? AlignmentTranscriptionApiStyles.OpenAiMultipart
            : string.IsNullOrWhiteSpace(request.ApiStyle)
                ? row?.AlignmentTranscriptionApiStyle
                    ?? AlignmentTranscriptionApiStyles.OpenAiMultipart
                : request.ApiStyle.Trim();

        var baseUrl = ResolveBaseUrl(request.BaseUrl, effective);
        ValidateResolvedBaseUrl(baseUrl, AiProviderKind.AlignmentTranscription);

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? effective.Model
            : request.Model.Trim();
        var apiKey = ResolveApiKey(request.ApiKey, effective);

        if (string.IsNullOrWhiteSpace(model))
        {
            return new AiProviderTestResult(
                false, null, "Alignment transcription model is not configured.");
        }

        // Explicit probe overrides are used only for this call. The selected
        // mode still controls the transport, client and validation.
        var probeConfig = effective with
        {
            // A dedicated setup that is not saved (or not enabled) yet can
            // still be probed; the test answers "would this connect".
            Enabled = useVoice ? effective.Enabled : true,
            BaseUrl = baseUrl,
            Model = model,
            ApiKey = apiKey,
        };

        using var audio = new MemoryStream(SilentWav(), writable: false);

        try
        {
            SttResult result;

            switch (apiStyle)
            {
                case AlignmentTranscriptionApiStyles.OpenAiMultipart:
                    // This is the same provider implementation used by the
                    // alignment dispatcher for multipart calls.
                    var multipart = new NineRouterSttProvider(
                        httpClientFactory,
                        speechOptions,
                        this,
                        NullLogger<NineRouterSttProvider>.Instance);

                    result = await multipart.TranscribeWithConfigAsync(
                        audio,
                        "silence.wav",
                        "audio/wav",
                        null,
                        probeConfig,
                        ct);
                    break;

                case AlignmentTranscriptionApiStyles.JsonBase64:
                    // Reuse production serialization, size caps, response
                    // validation and the production named HTTP client.
                    var json = new JsonBase64SttProvider(
                        httpClientFactory,
                        speechOptions);

                    result = await json.TranscribeWithConfigAsync(
                        audio,
                        "silence.wav",
                        "audio/wav",
                        null,
                        probeConfig,
                        ct);
                    break;

                default:
                    return new AiProviderTestResult(
                        false, null, "Unsupported alignment transcription API style.");
            }

            return new AiProviderTestResult(
                true, $"Reached {model}.", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SttException ex)
        {
            return new AiProviderTestResult(false, null, ex.Message);
        }
        catch (HttpRequestException)
        {
            return new AiProviderTestResult(
                false, null, "Alignment transcription provider could not be reached.");
        }
        catch (OperationCanceledException)
        {
            return new AiProviderTestResult(
                false, null, "Alignment transcription timed out.");
        }
    }

    private async Task<AiProviderTestResult> TestLlmAsync(
        string baseUrl,
        string model,
        string? apiKey,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = false,
            ["max_tokens"] = 8,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = "ping",
                },
            },
        }, JsonOptions);

        using var content = new StringContent(
            payload, Encoding.UTF8, "application/json");

        var (ok, _, error) = await SendAsync(
            HttpMethod.Post,
            Combine(baseUrl, "chat/completions"),
            apiKey,
            content,
            ct);

        return ok
            ? new AiProviderTestResult(true, $"Reached {model}.", null)
            : new AiProviderTestResult(false, null, error);
    }

    private async Task<AiProviderTestResult> TestSttAsync(
        string baseUrl,
        string model,
        string? apiKey,
        CancellationToken ct)
    {
        // Preserve the existing voice STT test contract unchanged.
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(SilentWav());
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "silence.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("json"), "response_format");

        var (ok, _, error) = await SendAsync(
            HttpMethod.Post,
            Combine(baseUrl, "v1/audio/transcriptions"),
            apiKey,
            form,
            ct);

        return ok
            ? new AiProviderTestResult(true, $"Reached {model}.", null)
            : new AiProviderTestResult(false, null, error);
    }

    private async Task<AiProviderTestResult> TestEmbeddingAsync(
        string baseUrl,
        string model,
        string? apiKey,
        CancellationToken ct)
    {
        var inputs = new[] { "ping" };
        using var content = new StringContent(
            OpenAiCompatibleEmbeddings.BuildRequestBody(model, inputs),
            Encoding.UTF8,
            "application/json");

        var (ok, body, error) = await SendAsync(
            HttpMethod.Post,
            OpenAiCompatibleEmbeddings.BuildUri(baseUrl).ToString(),
            apiKey,
            content,
            ct);

        if (!ok)
        {
            return new AiProviderTestResult(false, null, error);
        }

        try
        {
            var batch = OpenAiCompatibleEmbeddings.ParseResponse(
                body, model, inputs.Length);

            return new AiProviderTestResult(
                true,
                $"Reached {model} ({batch.Vectors[0].Length} dimensions).",
                null);
        }
        catch (EmbeddingException ex)
        {
            return new AiProviderTestResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Generated one-second silent 16-bit mono WAV.
    /// </summary>
    private static byte[] SilentWav()
    {
        const int sampleRate = 8000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int seconds = 1;

        var dataLength =
            sampleRate * channels * (bitsPerSample / 8) * seconds;

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(
            stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        writer.Flush();

        return stream.ToArray();
    }

    // ------------------------------------------------------------------
    // Effective resolution
    // ------------------------------------------------------------------

    private async Task<AiProviderSettingsModel?> LoadRowAsync(
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await LoadRowAsync(db, ct);
    }

    private static Task<AiProviderSettingsModel?> LoadRowAsync(
        NostosDbContext db,
        CancellationToken ct) =>
        db.AiProviderSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Id == AiProviderSettingsModel.SingletonId, ct);

    private async Task<EffectiveAiProviderConfig> EffectiveAsync(
        AiProviderKind kind,
        CancellationToken ct)
    {
        var row = await LoadRowAsync(ct);

        return kind switch
        {
            AiProviderKind.Llm => ResolveLlm(row),
            AiProviderKind.Embedding => ResolveEmbedding(row),
            AiProviderKind.AlignmentTranscription =>
                ResolveAlignmentStt(row).Provider,
            _ => ResolveStt(row),
        };
    }

    private EffectiveAiProviderConfig ResolveLlm(
        AiProviderSettingsModel? row)
    {
        var (key, fromEnv) = ResolveKey(
            row?.LlmApiKeyEncrypted,
            assistantOptions.ApiKeyEnvironmentVariable,
            "LLM");

        return new EffectiveAiProviderConfig(
            row?.LlmEnabled ?? assistantOptions.Enabled,
            OverrideOrFallback(row?.LlmBaseUrl, assistantOptions.BaseUrl),
            OverrideOrFallback(row?.LlmModel, assistantOptions.Model),
            assistantOptions.ApiKeyEnvironmentVariable,
            key,
            fromEnv);
    }

    private EffectiveAiProviderConfig ResolveStt(
        AiProviderSettingsModel? row)
    {
        var (key, fromEnv) = ResolveKey(
            row?.SttApiKeyEncrypted,
            speechOptions.ApiKeyEnvironmentVariable,
            "STT");

        return new EffectiveAiProviderConfig(
            row?.SttEnabled ?? speechOptions.Enabled,
            OverrideOrFallback(row?.SttBaseUrl, speechOptions.BaseUrl),
            OverrideOrFallback(row?.SttModel, speechOptions.Model),
            speechOptions.ApiKeyEnvironmentVariable,
            key,
            fromEnv);
    }

    private EffectiveAlignmentSttConfig ResolveAlignmentStt(
        AiProviderSettingsModel? row)
    {
        if (row?.AlignmentTranscriptionUseVoiceTranscription ?? true)
        {
            return new EffectiveAlignmentSttConfig(
                ResolveStt(row),
                AlignmentTranscriptionApiStyles.OpenAiMultipart,
                UseVoiceTranscription: true);
        }

        return new EffectiveAlignmentSttConfig(
            ResolveStoredAlignment(row),
            row?.AlignmentTranscriptionApiStyle
                ?? AlignmentTranscriptionApiStyles.OpenAiMultipart,
            UseVoiceTranscription: false);
    }

    private EffectiveAiProviderConfig ResolveStoredAlignment(
        AiProviderSettingsModel? row)
    {
        var (key, fromEnv) = ResolveKey(
            row?.AlignmentTranscriptionApiKeyEncrypted,
            AlignmentKeyEnvironmentVariable,
            "alignment transcription");

        return new EffectiveAiProviderConfig(
            row?.AlignmentTranscriptionEnabled ?? false,
            row?.AlignmentTranscriptionBaseUrl ?? string.Empty,
            row?.AlignmentTranscriptionModel ?? string.Empty,
            AlignmentKeyEnvironmentVariable,
            key,
            fromEnv);
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

    private EffectiveAiProviderConfig ResolveEmbedding(
        AiProviderSettingsModel? row)
    {
        var (key, fromEnv) = ResolveKey(
            row?.EmbeddingApiKeyEncrypted,
            embeddingOptions.ApiKeyEnvironmentVariable,
            "embedding");

        return new EffectiveAiProviderConfig(
            row?.EmbeddingEnabled ?? embeddingOptions.Enabled,
            OverrideOrFallback(row?.EmbeddingBaseUrl, embeddingOptions.BaseUrl),
            OverrideOrFallback(row?.EmbeddingModel, embeddingOptions.Model),
            embeddingOptions.ApiKeyEnvironmentVariable,
            key,
            fromEnv);
    }

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
                logger.LogWarning(
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

    // ------------------------------------------------------------------
    // Wire helpers
    // ------------------------------------------------------------------

    private static string ResolveBaseUrl(
        string? requested,
        EffectiveAiProviderConfig effective) =>
        string.IsNullOrWhiteSpace(requested)
            ? effective.BaseUrl
            : requested.Trim();

    private static string? ResolveApiKey(
        string? requested,
        EffectiveAiProviderConfig effective) =>
        string.IsNullOrWhiteSpace(requested)
            ? effective.ApiKey
            : requested.Trim();

    private static AiProviderKind ParseKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "llm" => AiProviderKind.Llm,
            "stt" => AiProviderKind.Stt,
            "embedding" => AiProviderKind.Embedding,
            "alignment-transcription" => AiProviderKind.AlignmentTranscription,
            _ => throw new AiProviderValidationException(
                "kind must be 'llm', 'stt', 'embedding' or 'alignment-transcription'."),
        };

    private static string KindLabel(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Llm => "LLM",
        AiProviderKind.Embedding => "Embedding",
        AiProviderKind.AlignmentTranscription => "Alignment transcription",
        _ => "STT",
    };

    private static void ValidateResolvedBaseUrl(
        string baseUrl,
        AiProviderKind kind)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new AiProviderValidationException(
                $"{KindLabel(kind)} baseUrl is not configured.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AiProviderValidationException(
                $"{KindLabel(kind)} baseUrl must be an absolute http:// or https:// URL.");
        }
    }

    private static string Combine(string baseUrl, string path) =>
        $"{baseUrl.TrimEnd('/')}/{path}";

    private async Task<(bool Ok, string Body, string? Error)> SendAsync(
        HttpMethod method,
        string url,
        string? apiKey,
        HttpContent? content,
        CancellationToken ct,
        string clientName = HttpClientName)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = content,
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var client = httpClientFactory.CreateClient(clientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return (
                false,
                string.Empty,
                $"the provider could not be reached ({ex.Message})");
        }
        catch (TaskCanceledException ex)
        {
            return (
                false,
                string.Empty,
                $"the provider timed out ({ex.Message})");
        }

        using (response)
        {
            // Existing non-alignment settings probes retain their response
            // behaviour. Alignment transcription itself always uses the
            // bounded concrete transport through TestAlignmentAsync.
            var body = await response.Content.ReadAsStringAsync(ct);

            return response.IsSuccessStatusCode
                ? (true, body, null)
                : (false, body, DescribeError(response.StatusCode, body));
        }
    }

    private static string DescribeError(HttpStatusCode status, string body)
    {
        var detail = TryReadErrorMessage(body);

        return detail is null
            ? $"HTTP {(int)status}"
            : $"HTTP {(int)status}: {detail}";
    }

    private static string? TryReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            return body.Length <= 300 ? body : body[..300];
        }
        catch (JsonException)
        {
            return body.Length <= 300 ? body : body[..300];
        }
    }

    private static IReadOnlyList<string>? ParseModels(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in new[] { "data", "models" })
            {
                if (!root.TryGetProperty(property, out var array)
                    || array.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var ids = new List<string>();

                foreach (var item in array.EnumerateArray())
                {
                    var id = item.ValueKind switch
                    {
                        JsonValueKind.String => item.GetString(),
                        JsonValueKind.Object
                            when item.TryGetProperty("id", out var idElement)
                                 && idElement.ValueKind == JsonValueKind.String =>
                            idElement.GetString(),
                        JsonValueKind.Object
                            when item.TryGetProperty("name", out var nameElement)
                                 && nameElement.ValueKind == JsonValueKind.String =>
                            nameElement.GetString(),
                        _ => null,
                    };

                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id);
                    }
                }

                return ids.Distinct(StringComparer.Ordinal).ToList();
            }

            return null;
        }
    }
}
