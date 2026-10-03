using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// The one place the assistant's LLM, voice STT and passage-embedding
/// configuration is read and written (assistant-milestone plan, "AI provider
/// settings"; embeddings: issue #683).
///
/// <para>
/// Effective config = the stored override when set, else the existing
/// <c>appsettings.json</c>/environment fallback. That is why
/// <see cref="AssistantOptions"/>, <see cref="SpeechOptions"/> and
/// <see cref="EmbeddingOptions"/> are still injected here: they are the
/// fallback, and the class never rewrites them.
/// </para>
///
/// <para>
/// Secrets: an owner-supplied key is stored ENCRYPTED AT REST with ASP.NET
/// Data Protection. It is never returned to a client, never logged, and never
/// included in an exception — only <c>hasKey</c> / <c>keyFromServerEnv</c>
/// cross the wire. A decrypt failure degrades to the environment variable and
/// logs a warning without the value; it never throws at startup or per request.
/// </para>
///
/// <para>
/// SSRF is deliberately NOT mitigated: the endpoint is the owner's own gateway
/// on the trusted LAN (no auth), so http and private hosts must be allowed.
/// Validation is limited to "absolute http/https". This is a documented
/// decision, not an oversight.
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
    /// <summary>Registered <see cref="IHttpClientFactory"/> client for the settings probes.</summary>
    public const string HttpClientName = "ai-provider-settings";

    // A versioned purpose string: rotating it (v1 -> v2) is what would make old
    // ciphertext undecryptable, which is exactly the migration lever if the
    // stored shape ever changes.
    private const string ProtectorPurpose = "Nostos.AiProviderSettings.ApiKey.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private IDataProtector CreateProtector() => dataProtection.CreateProtector(ProtectorPurpose);

    // ------------------------------------------------------------------
    // Effective configuration (the provider-facing read)
    // ------------------------------------------------------------------

    public async Task<EffectiveAiProviderConfig> GetEffectiveLlmAsync(CancellationToken ct = default) =>
        ResolveLlm(await LoadRowAsync(ct));

    public async Task<EffectiveAiProviderConfig> GetEffectiveSttAsync(CancellationToken ct = default) =>
        ResolveStt(await LoadRowAsync(ct));

    public async Task<EffectiveAiProviderConfig> GetEffectiveEmbeddingAsync(CancellationToken ct = default) =>
        ResolveEmbedding(await LoadRowAsync(ct));

    // ------------------------------------------------------------------
    // GET / PUT
    // ------------------------------------------------------------------

    public async Task<AiProviderSettingsResponse> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await LoadRowAsync(db, ct);
        return new AiProviderSettingsResponse(
            ToDto(ResolveLlm(row)),
            ToDto(ResolveStt(row)),
            ToDto(ResolveEmbedding(row)));
    }

    public async Task<AiProviderSettingsResponse> UpdateAsync(
        AiProviderSettingsUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.AiProviderSettings
            .FirstOrDefaultAsync(s => s.Id == AiProviderSettingsModel.SingletonId, ct);

        if (row is null)
        {
            row = new AiProviderSettingsModel { Id = AiProviderSettingsModel.SingletonId };
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

        row.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Re-read so the response is the same effective shape as GET.
        return await GetAsync(ct);
    }

    private void ApplyLlmUpdate(AiProviderSettingsModel row, AiProviderSectionUpdate update)
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

        row.LlmApiKeyEncrypted = ApplyKey(update.ApiKey, row.LlmApiKeyEncrypted);
    }

    private void ApplySttUpdate(AiProviderSettingsModel row, AiProviderSectionUpdate update)
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

        row.SttApiKeyEncrypted = ApplyKey(update.ApiKey, row.SttApiKeyEncrypted);
    }

    private void ApplyEmbeddingUpdate(AiProviderSettingsModel row, AiProviderSectionUpdate update)
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

        row.EmbeddingApiKeyEncrypted = ApplyKey(update.ApiKey, row.EmbeddingApiKeyEncrypted);
    }

    /// <summary>
    /// The apiKey contract, exactly: omitted/<c>null</c> = unchanged,
    /// <c>""</c> = clear, non-empty = store encrypted. Getting this wrong either
    /// silently wipes a working key or silently ignores a new one.
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
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AiProviderValidationException(
                "baseUrl must be an absolute http:// or https:// URL.");
        }
    }

    private static void ValidateModel(string? model)
    {
        if (model is not null && string.IsNullOrWhiteSpace(model))
        {
            throw new AiProviderValidationException("model must not be empty.");
        }
    }

    // ------------------------------------------------------------------
    // Model listing and connection tests
    // ------------------------------------------------------------------

    public async Task<AiProviderModelsResponse> ListModelsAsync(
        AiProviderModelsRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kind = ParseKind(request.Kind);
        var effective = await EffectiveAsync(kind, ct);
        var baseUrl = ResolveBaseUrl(request.BaseUrl, effective);
        ValidateResolvedBaseUrl(baseUrl, kind);
        var apiKey = ResolveApiKey(request.ApiKey, effective);

        // Try {baseUrl}/models, then {baseUrl}/v1/models. The LLM and embedding
        // base URLs already end in /v1, STT's does not, so the first attempt
        // covers the former and the fallback the latter.
        string? lastError = null;
        foreach (var url in new[] { Combine(baseUrl, "models"), Combine(baseUrl, "v1/models") })
        {
            var (ok, body, error) = await SendAsync(HttpMethod.Get, url, apiKey, null, ct);
            if (!ok)
            {
                lastError = error;
                continue;
            }

            var models = ParseModels(body);
            if (models is null)
            {
                lastError = "the provider returned a body that did not look like a model list";
                continue;
            }

            return new AiProviderModelsResponse(models);
        }

        throw new AiProviderUpstreamException(lastError ?? "the provider did not return a model list");
    }

    /// <summary>
    /// Always succeeds at the HTTP layer: a refused provider is data
    /// (<c>{ok:false,error}</c>) so the UI can render a message instead of a
    /// broken panel.
    /// </summary>
    public async Task<AiProviderTestResult> TestAsync(
        AiProviderTestRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var kind = ParseKind(request.Kind);
            var effective = await EffectiveAsync(kind, ct);
            var baseUrl = ResolveBaseUrl(request.BaseUrl, effective);
            ValidateResolvedBaseUrl(baseUrl, kind);
            var model = string.IsNullOrWhiteSpace(request.Model) ? effective.Model : request.Model.Trim();
            var apiKey = ResolveApiKey(request.ApiKey, effective);

            if (string.IsNullOrWhiteSpace(model))
            {
                return new AiProviderTestResult(false, null, $"{KindLabel(kind)} model is not configured.");
            }

            return kind switch
            {
                AiProviderKind.Llm => await TestLlmAsync(baseUrl, model, apiKey, ct),
                AiProviderKind.Embedding => await TestEmbeddingAsync(baseUrl, model, apiKey, ct),
                _ => await TestSttAsync(baseUrl, model, apiKey, ct),
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

    private async Task<AiProviderTestResult> TestLlmAsync(
        string baseUrl,
        string model,
        string? apiKey,
        CancellationToken ct)
    {
        // One minimal REAL completion. stream:false is mandatory for this
        // gateway (omitting it yields SSE), and max_tokens is kept tiny.
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = false,
            ["max_tokens"] = 8,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = "ping" },
            },
        }, JsonOptions);

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
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
        // One minimal REAL embedding, parsed with the same reader ingestion
        // uses: an endpoint that answers 200 with something that is not a
        // usable vector must fail here, not later in the background worker.
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
            var batch = OpenAiCompatibleEmbeddings.ParseResponse(body, model, inputs.Length);
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
    /// A generated 1-second silent 16-bit mono WAV, so the STT probe costs one
    /// real (tiny) transcription without shipping a fixture blob.
    /// </summary>
    private static byte[] SilentWav()
    {
        const int sampleRate = 8000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int seconds = 1;
        var dataLength = sampleRate * channels * (bitsPerSample / 8) * seconds;

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
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

    private async Task<AiProviderSettingsModel?> LoadRowAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await LoadRowAsync(db, ct);
    }

    private static Task<AiProviderSettingsModel?> LoadRowAsync(NostosDbContext db, CancellationToken ct) =>
        db.AiProviderSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == AiProviderSettingsModel.SingletonId, ct);

    private async Task<EffectiveAiProviderConfig> EffectiveAsync(AiProviderKind kind, CancellationToken ct)
    {
        var row = await LoadRowAsync(ct);
        return kind switch
        {
            AiProviderKind.Llm => ResolveLlm(row),
            AiProviderKind.Embedding => ResolveEmbedding(row),
            _ => ResolveStt(row),
        };
    }

    private EffectiveAiProviderConfig ResolveLlm(AiProviderSettingsModel? row)
    {
        var (key, fromEnv) = ResolveKey(
            row?.LlmApiKeyEncrypted, assistantOptions.ApiKeyEnvironmentVariable, "LLM");

        return new EffectiveAiProviderConfig(
            row?.LlmEnabled ?? assistantOptions.Enabled,
            OverrideOrFallback(row?.LlmBaseUrl, assistantOptions.BaseUrl),
            OverrideOrFallback(row?.LlmModel, assistantOptions.Model),
            assistantOptions.ApiKeyEnvironmentVariable,
            key,
            fromEnv);
    }

    private EffectiveAiProviderConfig ResolveStt(AiProviderSettingsModel? row)
    {
        var (key, fromEnv) = ResolveKey(
            row?.SttApiKeyEncrypted, speechOptions.ApiKeyEnvironmentVariable, "STT");

        return new EffectiveAiProviderConfig(
            row?.SttEnabled ?? speechOptions.Enabled,
            OverrideOrFallback(row?.SttBaseUrl, speechOptions.BaseUrl),
            OverrideOrFallback(row?.SttModel, speechOptions.Model),
            speechOptions.ApiKeyEnvironmentVariable,
            key,
            fromEnv);
    }

    private EffectiveAiProviderConfig ResolveEmbedding(AiProviderSettingsModel? row)
    {
        var (key, fromEnv) = ResolveKey(
            row?.EmbeddingApiKeyEncrypted, embeddingOptions.ApiKeyEnvironmentVariable, "embedding");

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
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                // Degrade to the environment variable. Type name only — the
                // value must never reach a log line.
                logger.LogWarning(
                    "Stored {Surface} API key could not be decrypted ({ErrorType}); "
                        + "falling back to environment variable '{Variable}'.",
                    surface,
                    ex.GetType().Name,
                    environmentVariable);
            }
        }

        var env = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(env) ? (null, false) : (env.Trim(), true);
    }

    private static string OverrideOrFallback(string? stored, string fallback) =>
        string.IsNullOrWhiteSpace(stored) ? fallback : stored;

    private static AiProviderSectionDto ToDto(EffectiveAiProviderConfig config) =>
        new(config.Enabled, config.BaseUrl, config.Model, config.HasKey, config.KeyFromServerEnv);

    // ------------------------------------------------------------------
    // Wire helpers
    // ------------------------------------------------------------------

    private static string ResolveBaseUrl(string? requested, EffectiveAiProviderConfig effective) =>
        string.IsNullOrWhiteSpace(requested) ? effective.BaseUrl : requested.Trim();

    private static string? ResolveApiKey(string? requested, EffectiveAiProviderConfig effective) =>
        string.IsNullOrWhiteSpace(requested) ? effective.ApiKey : requested.Trim();

    private static AiProviderKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "llm" => AiProviderKind.Llm,
        "stt" => AiProviderKind.Stt,
        "embedding" => AiProviderKind.Embedding,
        _ => throw new AiProviderValidationException("kind must be 'llm', 'stt' or 'embedding'."),
    };

    private static string KindLabel(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Llm => "LLM",
        AiProviderKind.Embedding => "Embedding",
        _ => "STT",
    };

    private static void ValidateResolvedBaseUrl(string baseUrl, AiProviderKind kind)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new AiProviderValidationException($"{KindLabel(kind)} baseUrl is not configured.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new AiProviderValidationException(
                $"{KindLabel(kind)} baseUrl must be an absolute http:// or https:// URL.");
        }
    }

    private static string Combine(string baseUrl, string path) => $"{baseUrl.TrimEnd('/')}/{path}";

    private async Task<(bool Ok, string Body, string? Error)> SendAsync(
        HttpMethod method,
        string url,
        string? apiKey,
        HttpContent? content,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var client = httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return (false, string.Empty, $"the provider could not be reached ({ex.Message})");
        }
        catch (TaskCanceledException ex)
        {
            return (false, string.Empty, $"the provider timed out ({ex.Message})");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? (true, body, null)
                : (false, body, DescribeError(response.StatusCode, body));
        }
    }

    /// <summary>
    /// Best-effort provider message: the gateway reports
    /// <c>{"error":{"message":…}}</c>; anything else falls back to the status
    /// line so the UI always gets something readable.
    /// </summary>
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

    /// <summary>
    /// Parses an OpenAI-style model list (<c>{"data":[{"id":…}]}</c>), also
    /// accepting a bare <c>{"models":[…]}</c> and bare string arrays. Returns
    /// null when the body carries no recognizable list at all.
    /// </summary>
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
                        JsonValueKind.Object when item.TryGetProperty("id", out var idElement)
                            && idElement.ValueKind == JsonValueKind.String => idElement.GetString(),
                        JsonValueKind.Object when item.TryGetProperty("name", out var nameElement)
                            && nameElement.ValueKind == JsonValueKind.String => nameElement.GetString(),
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
