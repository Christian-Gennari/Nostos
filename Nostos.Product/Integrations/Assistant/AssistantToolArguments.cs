using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Reads the arguments JSON a model attached to a tool call. The model writes
/// that text, so anything that is not a JSON object is read as an empty one and
/// the capability's own validation reports what is missing.
/// </summary>
internal static class AssistantToolArguments
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    public static JsonElement ParseElement(string? json) =>
        JsonSerializer.SerializeToElement(ParseObject(json), JsonOptions);
}
