using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Nostos.Backend.Configuration;
using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Read-aloud against a speech server the installation's owner runs, over the
/// OpenAI speech route (<c>POST …/v1/audio/speech</c>).
///
/// One call per sentence, no retry: the reader answers a failed call by
/// carrying on with the device voice. The token, when there is one, is attached
/// only to the outbound request and never logged or returned.
/// </summary>
public sealed class OpenAiCompatibleReadAloudProvider(
    IHttpClientFactory httpClientFactory,
    ReadAloudOptions options,
    ReadAloudAudioCache cache,
    ILogger<OpenAiCompatibleReadAloudProvider> logger) : IReadAloudProvider
{
    public const string HttpClientName = "read-aloud-openai-compatible";

    public IReadOnlyList<ReadAloudVoice> Voices => KokoroVoices.All;

    public Task<ReadAloudAvailability> GetAvailabilityAsync(CancellationToken ct = default) =>
        Task.FromResult(new ReadAloudAvailability(
            Enabled: options.Enabled,
            Configured: SpeechUri(options.BaseUrl) is not null && !string.IsNullOrWhiteSpace(options.Model),
            Managed: false,
            MaxCharacters: options.MaxCharacters));

    public async Task<ReadAloudAudio> SynthesizeAsync(
        ReadAloudSpeechRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = SpeechUri(options.BaseUrl);
        if (!options.Enabled)
        {
            throw new ReadAloudException(
                ReadAloudErrorCodes.Disabled,
                "The server voice for read aloud is switched off.");
        }

        if (uri is null || string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ReadAloudException(
                ReadAloudErrorCodes.NotConfigured,
                "ReadAloud:BaseUrl and ReadAloud:Model must both be set.");
        }

        var cacheKey = ReadAloudAudioCache.Key(uri.AbsoluteUri, options.Model, request.Voice, request.Text);
        var cached = await cache.TryGetAsync(cacheKey, ct);
        if (cached is not null)
            return cached;

        using var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            // Serialized up front so the request carries a Content-Length: small
            // self-hosted speech servers do not all read a chunked body.
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = options.Model,
                    input = request.Text,
                    voice = request.Voice,
                    response_format = "mp3",
                    speed = 1.0,
                }),
                Encoding.UTF8,
                "application/json"),
        };

        var token = Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(token))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        var client = httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is not a provider failure: the reader moved on.
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new ReadAloudException(
                ReadAloudErrorCodes.Timeout,
                "The speech server did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Read-aloud speech server is unreachable.");
            throw new ReadAloudException(
                ReadAloudErrorCodes.Provider,
                "The speech server could not be reached.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Read-aloud speech server answered {StatusCode}.",
                    (int)response.StatusCode);
                throw response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ReadAloudException(
                        ReadAloudErrorCodes.Permission,
                        "The speech server rejected the configured credential."),
                    HttpStatusCode.TooManyRequests => new ReadAloudException(
                        ReadAloudErrorCodes.RateLimited,
                        "The speech server is rate limiting."),
                    HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => new ReadAloudException(
                        ReadAloudErrorCodes.Timeout,
                        "The speech server did not answer in time."),
                    _ => new ReadAloudException(
                        ReadAloudErrorCodes.Provider,
                        $"The speech server answered {(int)response.StatusCode}."),
                };
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0
                || !(mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                    || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ReadAloudException(
                    ReadAloudErrorCodes.InvalidResponse,
                    "The speech server answered with something that is not audio.");
            }

            var audio = new ReadAloudAudio(
                bytes,
                mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? mediaType : "audio/mpeg");
            await cache.SetAsync(cacheKey, audio, ct);
            return audio;
        }
    }

    /// <summary>
    /// The speech route for a configured base URL, or null when the URL is not
    /// usable. A base whose path already has a version segment is trusted to be
    /// the API root (<c>https://host/v1</c>, <c>https://host/v1/openai</c>);
    /// a bare host gets <c>/v1</c>.
    /// </summary>
    public static Uri? SpeechUri(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var root)
            || (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var path = root.AbsolutePath.TrimEnd('/');
        var versioned = path.Split('/').Any(segment =>
            segment.Length >= 2
            && (segment[0] == 'v' || segment[0] == 'V')
            && segment[1..].All(char.IsAsciiDigit));
        var suffix = versioned ? "/audio/speech" : "/v1/audio/speech";
        return new Uri($"{root.GetLeftPart(UriPartial.Authority)}{path}{suffix}");
    }
}
