using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Nostos.Backend.Configuration;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Vendor-neutral transcription transport using a base64-encoded audio payload.
///
/// Sends one request per call. No retry, fallback provider, local receipt,
/// audio file, or credential logging is performed.
/// </summary>
public sealed class JsonBase64SttProvider(
    IHttpClientFactory httpClientFactory,
    SpeechOptions options,
    IAiProviderConfigResolver? config = null) : ISTtProvider
{
    public const string HttpClientName = "stt-json-base64";

    private const string TranscriptionsPath = "v4/ai/transcription-model";

    /// <summary>
    /// Resolves the alignment configuration per call. The optional resolver
    /// preserves explicit-config construction in transport tests; normal host
    /// dependency injection supplies it automatically.
    /// </summary>
    public async Task<SttResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string? contentType,
        string? languageHint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        if (config is null)
        {
            throw SttException.NotConfigured(
                "Alignment transcription provider configuration");
        }

        var effective = await config.GetEffectiveAlignmentSttAsync(ct);

        if (effective.ApiStyle != AlignmentTranscriptionApiStyles.JsonBase64)
        {
            throw new SttException(
                SttErrorCodes.InvalidRequest,
                "The selected alignment transcription configuration does not use the JSON transport.");
        }

        return await TranscribeWithConfigAsync(
            audio,
            fileName,
            contentType,
            languageHint,
            effective.Provider,
            ct);
    }

    /// <summary>
    /// Transcribes with the explicit effective settings selected by the
    /// alignment dispatcher.
    /// </summary>
    public async Task<SttResult> TranscribeWithConfigAsync(
        Stream audio,
        string fileName,
        string? contentType,
        string? languageHint,
        EffectiveAiProviderConfig effective,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(effective);

        if (!effective.Enabled)
        {
            throw SttException.Disabled();
        }

        if (string.IsNullOrWhiteSpace(effective.ApiKey))
        {
            throw SttException.NotConfigured(effective.ApiKeyEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(effective.BaseUrl)
            || string.IsNullOrWhiteSpace(effective.Model))
        {
            throw SttException.NotConfigured(
                "Alignment transcription base URL and model");
        }

        // The supported JSON wire contract carries WAV audio. The transport
        // does not pretend that other codecs have been converted to WAV.
        if (!string.Equals(contentType, "audio/wav", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contentType, "audio/x-wav", StringComparison.OrdinalIgnoreCase))
        {
            throw SttException.UnsupportedFormat(contentType);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var limit = options.MaxUploadBytes;

        if (limit <= 0)
        {
            throw SttException.TooLarge(limit);
        }

        int count;
        while ((count = await audio.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length > limit - count)
            {
                throw SttException.TooLarge(limit);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }

        if (buffer.Length == 0)
        {
            throw new SttException(
                SttErrorCodes.InvalidRequest,
                "The audio clip is empty.");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            BuildUri(effective.BaseUrl));

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", effective.ApiKey.Trim());

        request.Headers.TryAddWithoutValidation(
            "ai-gateway-protocol-version", "0.0.1");
        request.Headers.TryAddWithoutValidation(
            "ai-transcription-model-specification-version", "4");
        request.Headers.TryAddWithoutValidation(
            "ai-model-id", effective.Model);

        request.Content = JsonContent.Create(new
        {
            audio = Convert.ToBase64String(buffer.ToArray()),
            mediaType = "audio/wav",
        });

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
        catch (OperationCanceledException)
        {
            throw SttException.TimedOut();
        }
        catch (HttpRequestException)
        {
            // The outcome of a network failure may already have been billed.
            // Deliberately do not retry or expose request/credential details.
            throw SttException.ProviderFailure(
                "network failure; the request outcome is unknown");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden)
            {
                throw SttException.PermissionDenied();
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw SttException.RateLimited();
            }

            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                throw SttException.TooLarge(limit);
            }

            if (response.StatusCode == HttpStatusCode.UnsupportedMediaType)
            {
                throw SttException.UnsupportedFormat(contentType);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw SttException.ProviderFailure(
                    $"HTTP {(int)response.StatusCode}");
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw SttException.TimedOut();
            }
            catch (HttpRequestException)
            {
                throw SttException.ProviderFailure(
                    "response could not be read");
            }

            return ParseResponse(body);
        }
    }

    internal static Uri BuildUri(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');

        if (trimmed.EndsWith(
            "/" + TranscriptionsPath,
            StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed, UriKind.Absolute);
        }

        return new Uri(
            $"{trimmed}/{TranscriptionsPath}",
            UriKind.Absolute);
    }

    internal static SttResult ParseResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String)
            {
                throw SttException.InvalidResponse(
                    "missing transcript text");
            }

            string? language = null;
            double? duration = null;

            if (root.TryGetProperty("language", out var languageElement)
                && languageElement.ValueKind == JsonValueKind.String)
            {
                language = languageElement.GetString();
            }

            if (root.TryGetProperty("durationInSeconds", out var durationElement)
                && durationElement.ValueKind == JsonValueKind.Number
                && durationElement.TryGetDouble(out var seconds)
                && double.IsFinite(seconds)
                && seconds >= 0)
            {
                duration = seconds;
            }

            return new SttResult(
                text.GetString()!.Trim(),
                language,
                duration);
        }
        catch (JsonException)
        {
            throw SttException.InvalidResponse("malformed JSON");
        }
    }
}
