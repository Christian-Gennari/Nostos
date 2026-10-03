using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Nostos.Product.Services.Ai;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// SelfHosted BYOK embeddings against any OpenAI-compatible
/// <c>POST {BaseUrl}/embeddings</c> route (Vercel AI Gateway, OpenAI, 9Router,
/// Ollama, vLLM, …).
///
/// The endpoint, model and credential are read at call time from the EFFECTIVE
/// configuration — the override stored in Settings → AI when set, otherwise the
/// appsettings/environment fallback — and the key is attached only to the
/// outbound request: it is never logged or returned. A single send, no retry
/// loop; a failed call is a typed <see cref="EmbeddingException"/>.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProvider(
    IHttpClientFactory httpClientFactory,
    IAiProviderConfigResolver config,
    ILogger<OpenAiCompatibleEmbeddingProvider> logger) : IEmbeddingProvider
{
    /// <summary>Name of the registered <see cref="IHttpClientFactory"/> client.</summary>
    public const string HttpClientName = "embedding-openai-compatible";

    public async Task<string?> GetActiveModelAsync(CancellationToken ct = default)
    {
        var effective = await config.GetEffectiveEmbeddingAsync(ct);
        return effective.IsAvailable && !string.IsNullOrWhiteSpace(effective.Model)
            ? effective.Model
            : null;
    }

    public async Task<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> inputs,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            throw new ArgumentException("At least one input is required.", nameof(inputs));
        }

        var effective = await config.GetEffectiveEmbeddingAsync(ct);
        if (!effective.IsAvailable || string.IsNullOrWhiteSpace(effective.Model))
        {
            throw EmbeddingException.NotConfigured();
        }

        // The model id is sent verbatim: gateway prefixes such as `alibaba/`
        // are load-bearing.
        var body = OpenAiCompatibleEmbeddings.BuildRequestBody(effective.Model, inputs);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            OpenAiCompatibleEmbeddings.BuildUri(effective.BaseUrl))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", effective.ApiKey!.Trim());

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
        catch (HttpRequestException ex)
        {
            throw EmbeddingException.ProviderFailure($"the endpoint could not be reached ({ex.Message})");
        }
        catch (TaskCanceledException)
        {
            throw EmbeddingException.TimedOut();
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw EmbeddingException.PermissionDenied();
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw EmbeddingException.RateLimited();
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ReadErrorDetailAsync(response, ct);
                throw EmbeddingException.ProviderFailure(
                    $"HTTP {(int)response.StatusCode}{(detail is null ? string.Empty : $": {detail}")}");
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var batch = OpenAiCompatibleEmbeddings.ParseResponse(
                responseBody,
                effective.Model,
                inputs.Count);

            // Deliberately no input text and no credential in this line.
            logger.LogDebug(
                "Embedded {Count} input(s) with model {Model}: {Dimensions} dimensions, {Tokens} token(s).",
                batch.Vectors.Count,
                effective.Model,
                batch.Vectors[0].Length,
                batch.TotalTokens);

            // Stored vectors are keyed by the CONFIGURED id, not whatever alias
            // the provider echoes back, so "is this chunk embedded with the
            // active model" stays a stable comparison.
            return batch with { Model = effective.Model };
        }
    }

    private static async Task<string?> ReadErrorDetailAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return null;

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
