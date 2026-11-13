using System.Text.Json;

namespace Nostos.Backend.Services.Ai;

/// <summary>
/// Queries OpenAI-compatible <c>/models</c> and <c>/v1/models</c> endpoints
/// for an AI provider surface using its effective configuration and request overrides.
/// </summary>
internal sealed class AiProviderModelDiscovery(
    IAiProviderConfigResolver configResolver,
    AiProviderProbeHttpClient httpClient)
{
    public async Task<AiProviderModelsResponse> ListModelsAsync(
        AiProviderModelsRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var kind = AiProviderProbeHelpers.ParseKind(request.Kind);

        // Resolve once, including API style, so a setting change between
        // queries cannot mix one transport with another configuration.
        var alignment = kind == AiProviderKind.AlignmentTranscription
            ? await configResolver.GetEffectiveAlignmentSttAsync(ct)
            : null;

        var effective = alignment?.Provider
            ?? await AiProviderProbeHelpers.GetEffectiveAsync(configResolver, kind, ct);

        var baseUrl = AiProviderProbeHelpers.ResolveBaseUrl(request.BaseUrl, effective);
        AiProviderProbeHelpers.ValidateResolvedBaseUrl(baseUrl, kind);
        var apiKey = AiProviderProbeHelpers.ResolveApiKey(request.ApiKey, effective);

        // Model discovery is a separate API, not part of either transcription
        // wire contract. It is supported only when the upstream exposes a
        // compatible models endpoint. However, use the SAME named HTTP client
        // as real calls, including redirect and timeout policy.
        var clientName = alignment?.ApiStyle switch
        {
            AlignmentTranscriptionApiStyles.JsonBase64 =>
                JsonBase64SttProvider.HttpClientName,
            AlignmentTranscriptionApiStyles.OpenAiMultipart =>
                OpenAiCompatibleSttProvider.HttpClientName,
            _ => AiProviderSettingsService.HttpClientName,
        };

        string? lastError = null;

        foreach (var url in ModelDiscoveryUrls(baseUrl))
        {
            var probe = await httpClient.SendAsync(
                HttpMethod.Get,
                url,
                apiKey,
                null,
                ct,
                clientName);

            if (!probe.Ok)
            {
                lastError = probe.Error;
                continue;
            }

            var models = ParseModels(probe.Body);
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

    internal static IEnumerable<string> ModelDiscoveryUrls(string baseUrl)
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
        var first = AiProviderProbeHelpers.Combine(trimmed, "models");
        var second = AiProviderProbeHelpers.Combine(trimmed, "v1/models");

        yield return first;

        if (!string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            yield return second;
        }
    }

    internal static IReadOnlyList<string>? ParseModels(string body)
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
