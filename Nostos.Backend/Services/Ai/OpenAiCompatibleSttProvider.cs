using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nostos.Backend.Configuration;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Speech-to-text against any OpenAI-compatible transcription
/// route (<c>POST {BaseUrl}/v1/audio/transcriptions</c>).
///
/// One provider, one call. The credential and endpoint are read at call time
/// from the EFFECTIVE configuration — the stored override when set, otherwise
/// the appsettings/environment fallback (see <see cref="IAiProviderConfigResolver"/>)
/// — and the key is attached only to the outbound request: it is never logged,
/// returned, or handed to the client. The model id is sent exactly as
/// configured: the <c>groq/</c> prefix is load-bearing, and a bare
/// <c>whisper-1</c> would route to an uncredentialed provider instead.
///
/// Failures are translated into <see cref="SttException"/> with a stable code.
/// There is no retry loop anywhere here; a failed call is a failed call.
/// </summary>
public sealed class OpenAiCompatibleSttProvider(
    IHttpClientFactory httpClientFactory,
    SpeechOptions options,
    IAiProviderConfigResolver config,
    ILogger<OpenAiCompatibleSttProvider> logger) : ISTtProvider
{
    /// <summary>Name of the registered <see cref="IHttpClientFactory"/> client.</summary>
    public const string HttpClientName = "stt-openai-compatible";

    private const string TranscriptionsPath = "v1/audio/transcriptions";

    // verbose_json is what carries language + duration; plain json carries only
    // text. The gateway's text field arrives with a leading space, so it is
    // trimmed before it leaves this class.
    private const string ResponseFormat = "verbose_json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SttResult> TranscribeAsync(
        Stream audio,
        string fileName,
        string? contentType,
        string? languageHint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        var effective = await config.GetEffectiveSttAsync(ct);
        return await TranscribeWithConfigAsync(
            audio, fileName, contentType, languageHint, effective, ct);
    }

    /// <summary>
    /// Sends one multipart transcription using a previously resolved configuration.
    /// Alignment uses this without changing the voice provider's default settings.
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

        if (string.IsNullOrWhiteSpace(effective.BaseUrl) || string.IsNullOrWhiteSpace(effective.Model))
        {
            throw SttException.NotConfigured(
                $"Speech:BaseUrl and Speech:Model must both be set (missing "
                + $"{(string.IsNullOrWhiteSpace(effective.BaseUrl) ? nameof(SpeechOptions.BaseUrl) : nameof(SpeechOptions.Model))}).");
        }

        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(audio);
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            // MediaRecorder commonly returns a parameterized browser MIME type,
            // e.g. "audio/webm;codecs=opus". The MediaTypeHeaderValue(string)
            // constructor accepts only a bare media type and throws FormatException
            // for that real browser value. Parse the full header instead so codec
            // parameters survive the proxy hop to the STT provider.
            if (!MediaTypeHeaderValue.TryParse(contentType, out var parsedContentType))
            {
                throw SttException.UnsupportedFormat(contentType);
            }

            fileContent.Headers.ContentType = parsedContentType;
        }

        form.Add(fileContent, "file", string.IsNullOrWhiteSpace(fileName) ? "audio" : fileName);
        // Sent verbatim. Do not "simplify" the groq/ prefix away.
        form.Add(new StringContent(effective.Model), "model");
        form.Add(new StringContent(ResponseFormat), "response_format");
        if (!string.IsNullOrWhiteSpace(languageHint))
        {
            form.Add(new StringContent(languageHint), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(effective.BaseUrl))
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", effective.ApiKey.Trim());

        var client = httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            // Read the body yourself, after the headers, so a large error body is
            // never an implicit download. A single send: no retry handler, no
            // loop.
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is not a provider failure. Let it stay a cancellation.
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw SttException.ProviderFailure(
                $"the gateway could not be reached ({ex.Message})");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw SttException.PermissionDenied();
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw SttException.PermissionDenied();
            }

            if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                throw SttException.TooLarge(options.MaxUploadBytes);
            }

            if (response.StatusCode == HttpStatusCode.UnsupportedMediaType)
            {
                throw SttException.UnsupportedFormat(contentType);
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ProviderErrorDetail.ReadAsync(response, ct);
                throw SttException.ProviderFailure(
                    $"HTTP {(int)response.StatusCode}{(detail is null ? string.Empty : $": {detail}")}");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var result = ParseResponse(body);

            // Deliberately no audio content and no credential in this line.
            logger.LogDebug(
                "Transcribed audio with model {Model}: language {Language}, duration {Duration}s.",
                effective.Model,
                result.Language,
                result.DurationSeconds);

            return result;
        }
    }

    private static Uri BuildUri(string baseUrl) => new(TranscriptionUrl(baseUrl));

    /// <summary>
    /// The transcription route for an endpoint written with or without its API
    /// version. Settings asks for the base URL "including /v1", as it does for
    /// the reading assistant, so <c>https://api.openai.com/v1</c> and
    /// <c>https://api.openai.com</c> must both reach
    /// <c>/v1/audio/transcriptions</c> rather than <c>/v1/v1/…</c>.
    /// </summary>
    public static string TranscriptionUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        var versioned = lastSegment.Length >= 2
            && (lastSegment[0] == 'v' || lastSegment[0] == 'V')
            && lastSegment[1..].All(char.IsAsciiDigit);
        return versioned
            ? $"{trimmed}/audio/transcriptions"
            : $"{trimmed}/{TranscriptionsPath}";
    }

    private static SttResult ParseResponse(string body)
    {
        VerboseTranscript? transcript;
        try
        {
            transcript = JsonSerializer.Deserialize<VerboseTranscript>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw SttException.InvalidResponse($"the body was not JSON ({ex.Message})");
        }

        if (transcript is null)
        {
            throw SttException.InvalidResponse("the body was empty");
        }

        // The gateway returns a leading space on the text. Trim it here so no
        // caller has to know that quirk.
        return new SttResult(
            (transcript.Text ?? string.Empty).Trim(),
            transcript.Language,
            transcript.Duration);
    }

    /// <summary>The subset of the verbose_json response this provider reads.</summary>
    private sealed record VerboseTranscript(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("duration")] double? Duration);
}
