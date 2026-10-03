using System.Text.Json;

namespace Nostos.Product.Services.Ai;

/// <summary>
/// The OpenAI-compatible <c>POST {BaseUrl}/embeddings</c> wire format, shared by
/// every transport that speaks it (SelfHosted BYOK, a managed gateway) so the
/// request shape and response parsing cannot drift between hosts.
///
/// The base URL carries its own <c>/v1</c> suffix, the same convention as the
/// chat-completions base URL: <c>https://ai-gateway.vercel.sh/v1</c> →
/// <c>https://ai-gateway.vercel.sh/v1/embeddings</c>.
/// </summary>
public static class OpenAiCompatibleEmbeddings
{
    private const string EmbeddingsPath = "embeddings";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Uri BuildUri(string baseUrl) =>
        new($"{baseUrl.TrimEnd('/')}/{EmbeddingsPath}");

    /// <summary>
    /// <c>encoding_format</c> is pinned to <c>float</c>: some gateways default
    /// to base64, which <see cref="ParseResponse"/> deliberately does not read.
    /// </summary>
    public static string BuildRequestBody(string model, IReadOnlyList<string> inputs) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = inputs,
            ["encoding_format"] = "float",
        }, JsonOptions);

    /// <summary>
    /// Reads <c>{"data":[{"index":0,"embedding":[…]}],"usage":{…}}</c> into one
    /// vector per input, ordered by <c>index</c>. Anything that would leave an
    /// input without a usable vector — a missing or duplicated index, an empty
    /// or ragged vector, a non-finite component — is an invalid response, never
    /// a partially filled batch.
    /// </summary>
    public static EmbeddingBatch ParseResponse(string body, string requestedModel, int expectedCount)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw EmbeddingException.InvalidResponse($"the body was not JSON ({ex.Message})");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                throw EmbeddingException.InvalidResponse("the body carried no 'data' array");
            }

            if (data.GetArrayLength() != expectedCount)
            {
                throw EmbeddingException.InvalidResponse(
                    $"expected {expectedCount} embedding(s) but received {data.GetArrayLength()}");
            }

            var vectors = new float[expectedCount][];
            var position = 0;
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("embedding", out var embedding)
                    || embedding.ValueKind != JsonValueKind.Array)
                {
                    throw EmbeddingException.InvalidResponse("an item carried no float 'embedding' array");
                }

                // `index` is optional in practice; fall back to array position.
                var index = item.TryGetProperty("index", out var indexElement)
                    && indexElement.ValueKind == JsonValueKind.Number
                    && indexElement.TryGetInt32(out var parsedIndex)
                        ? parsedIndex
                        : position;

                if (index < 0 || index >= expectedCount || vectors[index] is not null)
                {
                    throw EmbeddingException.InvalidResponse("the embedding indices did not match the inputs");
                }

                var vector = new float[embedding.GetArrayLength()];
                var component = 0;
                foreach (var value in embedding.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number
                        || !value.TryGetSingle(out var parsed)
                        || !float.IsFinite(parsed))
                    {
                        throw EmbeddingException.InvalidResponse("an embedding held a non-numeric component");
                    }

                    vector[component++] = parsed;
                }

                vectors[index] = vector;
                position++;
            }

            var dimensions = vectors.Length == 0 ? 0 : vectors[0].Length;
            if (vectors.Any(vector => vector.Length == 0 || vector.Length != dimensions))
            {
                throw EmbeddingException.InvalidResponse("the embeddings were empty or of differing lengths");
            }

            var model = root.TryGetProperty("model", out var modelElement)
                && modelElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(modelElement.GetString())
                    ? modelElement.GetString()!
                    : requestedModel;

            int? totalTokens = root.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("total_tokens", out var tokens)
                && tokens.ValueKind == JsonValueKind.Number
                && tokens.TryGetInt32(out var parsedTokens)
                    ? parsedTokens
                    : null;

            return new EmbeddingBatch(model, vectors, totalTokens);
        }
    }
}
