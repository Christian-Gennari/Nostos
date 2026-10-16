using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Chat-with-tools against any OpenAI-compatible
/// <c>POST {BaseUrl}/chat/completions</c> route (OpenAI, Ollama, LM Studio,
/// vLLM, or a gateway in front of several providers).
///
/// SelfHosted keeps its existing BYOK configuration and error behavior. The
/// wire-format work is shared with other OpenAI-compatible transports so tool
/// serialization and usage parsing cannot drift between providers.
/// </summary>
public sealed class OpenAiCompatibleLlmProvider(
    IHttpClientFactory httpClientFactory,
    IAiProviderConfigResolver config,
    ILogger<OpenAiCompatibleLlmProvider> logger) : ILlmProvider
{
    public const string HttpClientName = "assistant-openai-compatible";

    private const int MaxTokensCeiling = 8192;

    public async Task<LlmCompletion> CompleteAsync(
        LlmCompletionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await config.GetEffectiveLlmAsync(ct);

        if (string.IsNullOrWhiteSpace(effective.ApiKey))
            throw LlmException.NotConfigured(effective.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(effective.BaseUrl))
            throw LlmException.NotConfiguredSetting("Assistant:BaseUrl");
        if (string.IsNullOrWhiteSpace(effective.Model))
            throw LlmException.NotConfiguredSetting("Assistant:Model");

        var body = OpenAiCompatibleChatCompletions.BuildRequestBody(
            request,
            effective.Model,
            MaxTokensCeiling);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            OpenAiCompatibleChatCompletions.BuildUri(effective.BaseUrl))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", effective.ApiKey.Trim());

        var client = httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw LlmException.ProviderFailure($"the gateway could not be reached ({ex.Message})");
        }
        catch (TaskCanceledException)
        {
            throw LlmException.TimedOut();
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw LlmException.PermissionDenied();
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw LlmException.RateLimited();
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = await ProviderErrorDetail.ReadAsync(response, ct);
                throw LlmException.ProviderFailure(
                    $"HTTP {(int)response.StatusCode}{(detail is null ? string.Empty : $": {detail}")}");
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var completion = OpenAiCompatibleChatCompletions.ParseResponse(responseBody);

            logger.LogDebug(
                "Assistant completion with model {Model}: finish {FinishReason}, {ToolCalls} tool call(s).",
                effective.Model,
                completion.FinishReason ?? "(none)",
                completion.ToolCalls.Count);

            return completion;
        }
    }
}
