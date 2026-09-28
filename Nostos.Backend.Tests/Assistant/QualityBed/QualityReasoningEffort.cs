// Production-posture reasoning control for the #566 live quality bed.
//
// The managed production deployment runs with a thinking level of `none`,
// sent as the OpenAI-compatible `reasoning_effort` request parameter. The
// self-hosted product provider (NineRouterLlmProvider) never sends that
// parameter, so the live bed can opt into a production-equivalent posture
// via NOSTOS_QG_REASONING_EFFORT. Unset/empty keeps the default: the request
// is byte-for-byte what the product sends.
//
// The control lives at the transport boundary: this DelegatingHandler is
// registered additively on the product's named HttpClient
// (NineRouterLlmProvider.HttpClientName) inside QualityBedHost.CreateLive,
// only when the env value is set. Product code is untouched; the
// deterministic path never registers the handler.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Text;
using System.Text.Json.Nodes;

internal sealed class QualityReasoningEffortHandler(string effort) : DelegatingHandler
{
    public const string EnvironmentVariable = "NOSTOS_QG_REASONING_EFFORT";

    private readonly string _effort = effort?.Trim() ?? string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_effort)
            && request.Method == HttpMethod.Post
            && request.Content is not null
            && request.RequestUri is not null
            && request.RequestUri.AbsolutePath.EndsWith(
                "/chat/completions", StringComparison.Ordinal))
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            if (TryAddEffort(body, _effort, out var rewritten))
            {
                var mediaType = request.Content.Headers.ContentType?.MediaType ?? "application/json";
                request.Content.Dispose();
                request.Content = new StringContent(rewritten, Encoding.UTF8, mediaType);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    internal static bool TryAddEffort(string body, string effort, out string rewritten)
    {
        rewritten = body;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (node is not JsonObject root)
            return false;

        // Assignment (not append) guarantees exactly one entry even when the
        // product body already carries the parameter.
        root["reasoning_effort"] = effort;
        rewritten = root.ToJsonString();
        return true;
    }
}
