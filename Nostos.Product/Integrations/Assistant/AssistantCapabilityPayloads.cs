using System.Text.Json;
using System.Text.Json.Serialization;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Notes;
using Nostos.Product.BookText;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// The canonical typed reader for the capability result shapes the assistant
/// reads back (issue #1598).
///
/// A capability serializes its own result; this class is the typed view of that
/// shape, so no assistant code probes a <see cref="JsonElement"/> for
/// <c>value</c>, <c>status</c>, <c>states</c>, <c>bookPassages</c> or
/// <c>matchedBook</c> any more. Where a contract already describes the shape it
/// is reused instead of copied:
///
/// <list type="bullet">
/// <item><see cref="BookTextSearchResponse"/> and <see cref="KnowledgeSearchResponse"/>
/// for retrieval,</item>
/// <item><see cref="NoteCommandResult{T}"/> of <see cref="NoteDto"/> for
/// <c>notes_capture</c> and <c>notes_rewrite_capture</c>,</item>
/// <item><see cref="McpLibraryEnvelope{T}"/> and <see cref="McpResolvedBook"/>
/// from the MCP capability contracts (issue #1599) for
/// <c>library_get_book</c> and <c>library_resolve_book</c>.</item>
/// </list>
///
/// A payload that does not parse is "not this shape": every reader answers null
/// and the caller keeps its existing conservative behaviour. The reader accepts
/// both a numeric enum (what a capability writes) and its name, which is the
/// tolerance the string-probing code had before the split.
/// </summary>
internal static class AssistantCapabilityPayloads
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The typed view of one payload, or null when it is absent or not that shape.</summary>
    public static T? Read<T>(JsonElement? data)
        where T : class
    {
        if (data is not { } element
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            return element.Deserialize<T>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The note a successful <c>notes_capture</c> produced.</summary>
    public static NoteDto? ReadCapturedNote(JsonElement? data) =>
        Read<NoteCommandResult<NoteDto>>(data)?.Value;

    /// <summary>
    /// The id of the note a successful capture produced. Null when the shape is
    /// not what we expect: the surface then simply does not offer its
    /// raw-transcript affordance.
    /// </summary>
    public static string? ReadCapturedNoteId(JsonElement? data) =>
        ReadCapturedNote(data)?.Id.ToString();

    /// <summary>
    /// True when the saved note is a rewrite of the user's words, read from the
    /// stored note: a rewrite that fell back to the original reports verbatim.
    /// </summary>
    public static bool IsRewritten(JsonElement? data) =>
        ReadCapturedNote(data) is { } note
        && !ThoughtProcessingModes.IsVerbatim(note.ProcessingMode);

    /// <summary><c>book_text_find_quote</c>'s status word (<c>found</c>, <c>choose</c>, …).</summary>
    public static string? ReadQuoteLookupStatus(JsonElement? data) =>
        Read<AssistantQuoteLookup>(data)?.Status;

    /// <summary>The topics <c>topics_list</c> / <c>topics_search</c> answered with.</summary>
    public static IReadOnlyList<TopicDto>? ReadTopics(JsonElement? data) =>
        Read<List<TopicDto>>(data);

    /// <summary>The topic ids those rows name, skipping a row that named none.</summary>
    public static IEnumerable<Guid> ReadTopicIds(JsonElement? data) =>
        ReadTopics(data)?.Select(topic => topic.Id).Where(id => id != Guid.Empty) ?? [];

    /// <summary>The <c>noteId</c> a read capability was called with.</summary>
    public static Guid? ReadNoteId(JsonElement args) =>
        Read<AssistantNoteTarget>(args) is { NoteId: { } noteId }
        && Guid.TryParse(noteId, out var id)
            ? id
            : null;

    /// <summary>The book <c>library_get_book</c> answered with, from its envelope's <c>data</c>.</summary>
    public static BookDto? ReadLibraryBook(JsonElement? data) =>
        Read<McpLibraryEnvelope<BookDto>>(data)?.Data;

    /// <summary><c>library_resolve_book</c>'s answer: the resolution and the book or candidates.</summary>
    public static McpResolvedBook? ReadResolvedBook(JsonElement? data) =>
        Read<McpResolvedBook>(data);

    /// <summary><c>book_text_search</c>'s answer.</summary>
    public static BookTextSearchResponse? ReadBookTextSearch(JsonElement? data) =>
        Read<BookTextSearchResponse>(data);

    /// <summary><c>knowledge_search</c>'s answer.</summary>
    public static KnowledgeSearchResponse? ReadKnowledgeSearch(JsonElement? data) =>
        Read<KnowledgeSearchResponse>(data);
}

/// <summary>The status word a <c>book_text_find_quote</c> result leads with.</summary>
internal sealed record AssistantQuoteLookup(string? Status);

/// <summary>The typed view of a capability's <c>noteId</c> argument.</summary>
internal sealed record AssistantNoteTarget(string? NoteId);
