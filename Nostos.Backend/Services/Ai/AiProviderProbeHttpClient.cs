using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Nostos.Backend.Services.Ai;

internal sealed record AiProviderProbeResponse(
    bool Ok,
    HttpStatusCode? StatusCode,
    string? MediaType,
    string Body,
    string? Error);

/// <summary>
/// Shared HTTP transport and OpenAI-compatible error-body extraction for
/// model discovery and live provider connection probes. Supports both buffered
/// reads and <see cref="HttpCompletionOption.ResponseHeadersRead"/> (for the
/// TTS audio-header probe).
/// </summary>
internal sealed class AiProviderProbeHttpClient(IHttpClientFactory httpClientFactory)
{
    public async Task<AiProviderProbeResponse> SendAsync(
        HttpMethod method,
        string url,
        string? apiKey,
        HttpContent? content,
        CancellationToken ct,
        string clientName = AiProviderSettingsService.HttpClientName,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
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
            // Buffered by default so the probe's timeout covers the body too:
            // a "Test" against a server that stalls after the headers, or drops
            // the connection mid-body, reports it instead of hanging or failing
            // with an untyped error (#1466). Callers that only inspect headers
            // (such as the TTS audio probe) pass ResponseHeadersRead.
            response = await client.SendAsync(request, completionOption, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return new AiProviderProbeResponse(
                false,
                null,
                null,
                string.Empty,
                $"the provider could not be reached ({ex.Message})");
        }
        catch (OperationCanceledException ex)
        {
            return new AiProviderProbeResponse(
                false,
                null,
                null,
                string.Empty,
                $"the provider timed out ({ex.Message})");
        }

        using (response)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;

            if (completionOption == HttpCompletionOption.ResponseHeadersRead)
            {
                return response.IsSuccessStatusCode
                    ? new AiProviderProbeResponse(true, response.StatusCode, mediaType, string.Empty, null)
                    : new AiProviderProbeResponse(false, response.StatusCode, mediaType, string.Empty, $"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(ct);

            return response.IsSuccessStatusCode
                ? new AiProviderProbeResponse(true, response.StatusCode, mediaType, body, null)
                : new AiProviderProbeResponse(false, response.StatusCode, mediaType, body, DescribeError(response.StatusCode, body));
        }
    }

    internal static string DescribeError(HttpStatusCode status, string body)
    {
        var detail = TryReadErrorMessage(body);

        return detail is null
            ? $"HTTP {(int)status}"
            : $"HTTP {(int)status}: {detail}";
    }

    internal static string? TryReadErrorMessage(string body)
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
}

/// <summary>
/// Shared wire parsing and validation helpers for model discovery and live probes.
/// </summary>
internal static class AiProviderProbeHelpers
{
    public static string ResolveBaseUrl(
        string? requested,
        EffectiveAiProviderConfig effective) =>
        string.IsNullOrWhiteSpace(requested)
            ? effective.BaseUrl
            : requested.Trim();

    public static string? ResolveApiKey(
        string? requested,
        EffectiveAiProviderConfig effective) =>
        string.IsNullOrWhiteSpace(requested)
            ? effective.ApiKey
            : requested.Trim();

    public static AiProviderKind ParseKind(string? kind) =>
        kind?.Trim().ToLowerInvariant() switch
        {
            "llm" => AiProviderKind.Llm,
            "stt" => AiProviderKind.Stt,
            "embedding" => AiProviderKind.Embedding,
            "alignment-transcription" => AiProviderKind.AlignmentTranscription,
            "tts" => AiProviderKind.Tts,
            _ => throw new AiProviderValidationException(
                "kind must be 'llm', 'stt', 'embedding', 'alignment-transcription' or 'tts'."),
        };

    public static string KindLabel(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Llm => "LLM",
        AiProviderKind.Embedding => "Embedding",
        AiProviderKind.AlignmentTranscription => "Alignment transcription",
        AiProviderKind.Tts => "Read aloud",
        _ => "STT",
    };

    /// <summary>
    /// Validates the effective base URL for discovery or testing.
    /// Preserves the trusted-LAN HTTP/HTTPS allowance.
    /// </summary>
    public static void ValidateResolvedBaseUrl(
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

    public static string Combine(string baseUrl, string path) =>
        $"{baseUrl.TrimEnd('/')}/{path}";

    public static Task<EffectiveAiProviderConfig> GetEffectiveAsync(
        IAiProviderConfigResolver resolver,
        AiProviderKind kind,
        CancellationToken ct) =>
        kind switch
        {
            AiProviderKind.Llm => resolver.GetEffectiveLlmAsync(ct),
            AiProviderKind.Embedding => resolver.GetEffectiveEmbeddingAsync(ct),
            AiProviderKind.Tts => resolver.GetEffectiveTtsAsync(ct),
            AiProviderKind.Stt => resolver.GetEffectiveSttAsync(ct),
            AiProviderKind.AlignmentTranscription => ResolveAlignmentProviderAsync(resolver, ct),
            _ => resolver.GetEffectiveSttAsync(ct),
        };

    private static async Task<EffectiveAiProviderConfig> ResolveAlignmentProviderAsync(
        IAiProviderConfigResolver resolver,
        CancellationToken ct) =>
        (await resolver.GetEffectiveAlignmentSttAsync(ct)).Provider;
}
