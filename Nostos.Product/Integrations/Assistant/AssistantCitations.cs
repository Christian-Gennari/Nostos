using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Numbered citations inside an Ask Nostos reply (issue #1002).
///
/// Every piece of evidence a turn retrieves has a place in the turn's evidence
/// list, which the reply returns as <c>Evidence</c>. Before the model reads a
/// retrieval result, each item in it is given its number there as
/// <c>"cite": n</c>; the model writes <c>[^n]</c> where it uses that item.
/// After the turn, a marker that points at nothing is removed, so a client can
/// trust that <c>[^n]</c> means <c>Evidence[n - 1]</c>.
/// </summary>
internal static partial class AssistantCitations
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The result as the model should read it: each evidence item carries the
    /// number it has in <paramref name="turnEvidence"/>. Null, meaning "send it
    /// as it is", when nothing was numbered or its shape does not match the
    /// extracted evidence one for one, so a number never lands on the wrong item.
    /// </summary>
    public static JsonElement? Annotate(
        string capability,
        JsonElement? data,
        IReadOnlyList<AssistantEvidenceReferenceDto> extracted,
        IReadOnlyList<AssistantEvidenceReferenceDto> turnEvidence)
    {
        if (data is not { } element || extracted.Count == 0)
            return null;

        var root = JsonNode.Parse(element.GetRawText());
        if (root is null)
            return null;

        var items = ItemsInExtractionOrder(capability, root);
        if (items is null || items.Count != extracted.Count)
            return null;

        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < turnEvidence.Count; i++)
            numbers.TryAdd(AssistantEvidenceProjection.EvidenceKey(turnEvidence[i].Handle), i + 1);

        var annotated = false;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is JsonObject item
                && numbers.TryGetValue(AssistantEvidenceProjection.EvidenceKey(extracted[i].Handle), out var number))
            {
                item["cite"] = number;
                annotated = true;
            }
        }

        return annotated ? JsonSerializer.SerializeToElement(root, JsonOptions) : null;
    }

    /// <summary>The JSON items each <c>Extract…Evidence</c> reads, in the order it yields them.</summary>
    private static List<JsonNode?>? ItemsInExtractionOrder(string capability, JsonNode root)
    {
        static IEnumerable<JsonNode?> Array(JsonNode root, string name) =>
            root is JsonObject obj && obj[name] is JsonArray array ? array : [];

        return capability switch
        {
            "knowledge_search" => [.. Array(root, "notes"), .. Array(root, "topics"), .. Array(root, "bookPassages")],
            "book_text_search" => [.. Array(root, "passages")],
            "notes_list_for_book" or "notes_search" => root is JsonArray array ? [.. array] : null,
            "knowledge_read_evidence" => root is JsonObject ? [root] : null,
            _ => null,
        };
    }

    [GeneratedRegex(@"^[ \t]*\[\^\d+\]:.*(?:\r?\n|$)", RegexOptions.Multiline)]
    private static partial Regex FootnoteDefinition();

    [GeneratedRegex(@"([ \t]?)\[\^(\d+)\]")]
    private static partial Regex Marker();

    [GeneratedRegex(@"(\[\^\d+\])(?:\1)+")]
    private static partial Regex RepeatedMarker();

    /// <summary>
    /// Keeps only markers that point at this turn's evidence. Footnote
    /// definitions the model wrote are dropped: the sources row is the list.
    /// </summary>
    public static string Normalize(string reply, int evidenceCount)
    {
        if (string.IsNullOrEmpty(reply) || !reply.Contains("[^", StringComparison.Ordinal))
            return reply;

        var cleaned = FootnoteDefinition().Replace(reply, string.Empty);
        cleaned = Marker().Replace(cleaned, match =>
            int.TryParse(match.Groups[2].Value, out var number) && number >= 1 && number <= evidenceCount
                ? $"{match.Groups[1].Value}[^{number}]"
                : string.Empty);
        cleaned = RepeatedMarker().Replace(cleaned, "$1");
        return cleaned.TrimEnd();
    }
}
