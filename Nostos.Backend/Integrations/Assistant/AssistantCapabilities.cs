using System.Text.Json;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Knowledge;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Nostos.Product.BookText;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// The complete assistant action surface (issue #260 §5, §6). It is
/// deliberately small: every capability delegates to a canonical service
/// (<see cref="INoteService"/>, <see cref="ILibraryService"/>, or the existing
/// topic read repository) and nothing here reimplements library or note
/// logic.
///
/// Trust classes: read-only capabilities are <see cref="AssistantTrustClass.Suggest"/>
/// and never call a write method; capture is <see cref="AssistantTrustClass.Capture"/>;
/// ordinary user-requested mutations are <see cref="AssistantTrustClass.Act"/> and
/// run immediately inside the tool loop; destructive/high-impact operations stay
/// <see cref="AssistantTrustClass.PlanAndAct"/> behind explicit approval. Every
/// mutation still delegates to the canonical domain service.
/// </summary>
public static partial class AssistantCapabilities
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds the ordered capability list against the canonical services. The
    /// registry owns enforcement; this method only declares what can be done.
    /// </summary>
    public static IReadOnlyList<AssistantCapability> Build(
        INoteService notes,
        ILibraryService library,
        ITopicRepository topics) =>
        Build(
            notes,
            library,
            topics,
            NoOpKnowledgeRetrievalService.Instance,
            bookText: null);

    public static IReadOnlyList<AssistantCapability> Build(
        INoteService notes,
        ILibraryService library,
        ITopicRepository topics,
        IBookTextSearchService bookText) =>
        Build(
            notes,
            library,
            topics,
            NoOpKnowledgeRetrievalService.Instance,
            bookText);

    public static IReadOnlyList<AssistantCapability> Build(
        INoteService notes,
        ILibraryService library,
        ITopicRepository topics,
        IKnowledgeRetrievalService knowledge,
        IBookTextSearchService? bookText = null)
    {
        var capabilities = BuildLibraryBookCapabilities(library)
            .Concat(BuildKnowledgeCapabilities(notes, topics, knowledge))
            .Concat(BuildCollectionAndCaptureCapabilities(notes, library))
            .ToList();

        if (bookText is not null)
            capabilities.AddRange(BuildBookTextCapabilities(bookText));

        return capabilities;
    }

    // ------------------------------------------------------------------
    // Argument readers. The tool args are JSON objects keyed by the canonical
    // camelCase request field names; a missing or mistyped field reads null and
    // the capability turns that into a typed invalid-arguments failure.
    // ------------------------------------------------------------------

    private static JsonElement Property(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (args.TryGetProperty(name, out var value))
        {
            return value;
        }

        // Accept PascalCase too: the orchestrator is not the only possible
        // caller, and a wrong-cased key should not silently read as absent.
        var pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return args.TryGetProperty(pascal, out value) ? value : default;
    }

    private static string? Str(JsonElement args, string name)
    {
        var value = Property(args, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static Guid? Id(JsonElement args, string name)
    {
        var value = Property(args, name);
        return value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out var id)
                ? id
                : null;
    }

    private static int? Num(JsonElement args, string name)
    {
        var value = Property(args, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static bool? Bool(JsonElement args, string name) =>
        Property(args, name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    private static bool TryIds(JsonElement args, string name, out IReadOnlyList<Guid>? ids)
    {
        var value = Property(args, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            ids = null;
            return true;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            ids = null;
            return false;
        }

        var parsed = new List<Guid>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !Guid.TryParse(item.GetString(), out var id))
            {
                ids = null;
                return false;
            }

            parsed.Add(id);
        }

        ids = parsed;
        return true;
    }

    private static TEnum ValueEnum<TEnum>(JsonElement args, string name, TEnum fallback)
        where TEnum : struct, Enum
    {
        var value = Str(args, name);
        return !string.IsNullOrWhiteSpace(value)
            && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
                ? parsed
                : fallback;
    }

    // ------------------------------------------------------------------
    // Result shaping.
    // ------------------------------------------------------------------

    private static AssistantToolResult Invalid(string message) =>
        AssistantToolResult.Fail(AssistantErrorCodes.InvalidArguments, message);

    /// <summary>
    /// Serializes a value using its own runtime type: several canonical results
    /// carry their payload as <c>object</c>, and the declared type would drop
    /// it.
    /// </summary>
    private static JsonElement Element(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), Json);

    private static AssistantToolResult NoteResult<T>(NoteCommandResult<T> result) =>
        result.Success
            ? AssistantToolResult.Ok(Element(result))
            : AssistantToolResult.Fail(
                result.ErrorCode ?? AssistantErrorCodes.NotFound,
                result.ErrorMessage ?? "Note command failed.");

    /// <summary>
    /// Library mutations return an envelope whose failures are data, exactly as
    /// the REST/MCP callers receive them; the assistant does not re-map those
    /// codes.
    /// </summary>
    private static AssistantToolResult LibraryResult(LibraryCommandResultDto result)
    {
        var payload = Element(new
        {
            result.Reply,
            result.Data,
            result.StateVersion,
            result.Duplicate,
        });

        return result.Data switch
        {
            LibraryConfirmationErrorDto error =>
                AssistantToolResult.Fail(error.Code, result.Reply, payload),
            LibraryErrorDto error =>
                AssistantToolResult.Fail(error.Code, result.Reply, payload),
            _ => AssistantToolResult.Ok(payload),
        };
    }
}
