using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Nostos.Backend.Configuration;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Transcription transport for a specific JSON/base64 wire protocol.
///
/// <para>
/// Sends POST {BaseUrl}/v4/ai/transcription-model, unless BaseUrl already
/// ends in that path, with Bearer authentication and these headers:
/// ai-gateway-protocol-version: 0.0.1,
/// ai-transcription-model-specification-version: 4,
/// ai-model-id: {Model}.
/// </para>
///
/// <para>
/// The application/json request is
/// {"audio":"&lt;base64 WAV bytes&gt;","mediaType":"audio/wav"}.
/// The application/json response must contain a string text property and may
/// contain string language and numeric durationInSeconds properties.
/// </para>
///
/// <para>
/// This is not a universal JSON transcription adapter. Providers implementing
/// a different endpoint, header set, request schema or response schema are
/// incompatible. Only WAV audio is accepted.
/// </para>
///
/// <para>
/// Sends one request per call. No retry, fallback provider, local receipt,
/// audio file, or credential logging is performed. Audio is bounded by
/// SpeechOptions.MaxUploadBytes and responses by MaxResponseBytes.
/// </para>
/// </summary>
public sealed class JsonBase64SttProvider(
    IHttpClientFactory httpClientFactory,
    SpeechOptions options,
    IAiProviderConfigResolver? config = null) : ISTtProvider
{
    public const string HttpClientName = "stt-json-base64";

    internal const int MaxResponseBytes = 2 * 1024 * 1024;

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
    /// alignment dispatcher. Request construction, size checks and response
    /// parsing are shared with connection testing.
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

        if (!string.Equals(contentType, "audio/wav", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(contentType, "audio/x-wav", StringComparison.OrdinalIgnoreCase))
        {
            throw SttException.UnsupportedFormat(contentType);
        }

        var limit = options.MaxUploadBytes;

        if (limit <= 0)
        {
            throw SttException.TooLarge(limit);
        }

        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);

        try
        {
            // Check known remaining length before buffering. The loop below
            // remains authoritative for streams whose length is unavailable.
            if (audio.CanSeek)
            {
                try
                {
                    if (audio.Length - audio.Position > limit)
                    {
                        throw SttException.TooLarge(limit);
                    }
                }
                catch (NotSupportedException)
                {
                    // Some seekable stream implementations do not expose Length.
                }
            }

            int count;
            while ((count = await audio.ReadAsync(chunk.AsMemory(), ct)) > 0)
            {
                if (buffer.Length > limit - count)
                {
                    throw SttException.TooLarge(limit);
                }

                await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        if (buffer.Length == 0)
        {
            throw new SttException(
                SttErrorCodes.InvalidRequest,
                "The audio clip is empty.");
        }

        // MemoryStream owns the buffered audio. Convert directly from its
        // backing array rather than allocating another full WAV via ToArray().
        if (!buffer.TryGetBuffer(out var segment) || segment.Array is null)
        {
            throw SttException.ProviderFailure(
                "The audio buffer could not be prepared.");
        }

        var encodedAudio = Convert.ToBase64String(
            segment.Array,
            segment.Offset,
            checked((int)buffer.Length));

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
            audio = encodedAudio,
            mediaType = "audio/wav",
        });

        var client = httpClientFactory.CreateClient(HttpClientName);

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
            // The provider may already have processed or billed this call.
            // Deliberately do not retry or expose transport exception details.
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
                // Error bodies are deliberately not read: the status is
                // sufficient and may not contain safe diagnostic text.
                throw SttException.ProviderFailure(
                    $"HTTP {(int)response.StatusCode}");
            }

            string body;
            try
            {
                body = await ReadBoundedResponseAsync(response.Content, ct);
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
            catch (IOException)
            {
                throw SttException.ProviderFailure(
                    "response could not be read");
            }

            return ParseResponse(body);
        }
    }

    /// <summary>
    /// Reads at most MaxResponseBytes plus one byte to distinguish an
    /// oversized response from a response exactly at the limit.
    /// </summary>
    internal static async Task<string> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw SttException.InvalidResponse(
                "transcription response exceeds the size limit");
        }

        await using var source = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);

        try
        {
            int count;
            while ((count = await source.ReadAsync(chunk.AsMemory(), ct)) > 0)
            {
                if (buffer.Length > MaxResponseBytes - count)
                {
                    throw SttException.InvalidResponse(
                        "transcription response exceeds the size limit");
                }

                buffer.Write(chunk, 0, count);
            }

            buffer.Position = 0;
            using var reader = new StreamReader(
                buffer,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);

            return await reader.ReadToEndAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
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
