using System.Text;
using System.Text.Json;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Deterministic, content-free measurements of the assistant capability surface.
/// These numbers are for prompt/catalogue engineering only; they never influence
/// trust enforcement or which tools are exposed at runtime.
/// </summary>
public sealed record AssistantCapabilityCatalogueMeasurement(
    int CapabilityCount,
    int SerializedToolSchemaUtf8Bytes,
    int ToolDescriptionUtf8Bytes,
    int LegacyDuplicateAbilitiesPromptUtf8Bytes,
    IReadOnlyDictionary<AssistantCapabilityCategory, int> CategoryCounts);

public static class AssistantCapabilityCatalogueMetrics
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AssistantCapabilityCatalogueMeasurement Measure(
        IEnumerable<AssistantCapability> capabilities)
    {
        var list = capabilities.ToList();

        var toolPayload = list.Select(capability => new
        {
            name = capability.Name,
            description = capability.Summary,
            parameters = JsonDocument.Parse(capability.ParametersJsonSchema).RootElement.Clone(),
        }).ToList();

        var categoryCounts = Enum.GetValues<AssistantCapabilityCategory>()
            .ToDictionary(
                category => category,
                category => list.Count(capability => capability.Category == category));

        return new AssistantCapabilityCatalogueMeasurement(
            CapabilityCount: list.Count,
            SerializedToolSchemaUtf8Bytes: JsonSerializer.SerializeToUtf8Bytes(toolPayload, JsonOptions).Length,
            ToolDescriptionUtf8Bytes: list.Sum(capability => Encoding.UTF8.GetByteCount(capability.Summary)),
            LegacyDuplicateAbilitiesPromptUtf8Bytes: Encoding.UTF8.GetByteCount(BuildLegacyDuplicateAbilitiesPrompt(list)),
            CategoryCounts: categoryCounts);
    }

    internal static string BuildLegacyDuplicateAbilitiesPrompt(
        IReadOnlyList<AssistantCapability> capabilities)
    {
        var abilities = capabilities.Select(capability =>
        {
            var mode = capability.Trust switch
            {
                AssistantTrustClass.Suggest => "read-only",
                AssistantTrustClass.Capture => "immediate capture",
                AssistantTrustClass.Act => "immediate action",
                AssistantTrustClass.PlanAndAct => "requires approval",
                _ => "unknown",
            };

            return $"- {capability.Name} [{mode}]: {capability.Summary}";
        });

        return "\n\nAvailable abilities in this Nostos installation:\n"
            + string.Join("\n", abilities);
    }
}
