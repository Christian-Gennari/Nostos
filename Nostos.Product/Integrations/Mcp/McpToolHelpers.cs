using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Knowledge;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// The MCP tool a client calls for each Ask Nostos capability. Capability
/// messages (refusals, find-quote guidance) are written for Ask Nostos and name
/// its capabilities; a connected app knows only the MCP tools.
/// </summary>
internal static partial class McpToolNames
{
    private static readonly Dictionary<string, string> ToolFor = new(StringComparer.Ordinal)
    {
        ["knowledge_search"] = "search",
        ["notes_search"] = "search",
        ["knowledge_read_evidence"] = "fetch",
        ["book_text_find_quote"] = "find_quote",
        ["library_overview"] = "overview",
        ["knowledge_overview"] = "overview",
        ["library_list_books"] = "list_books",
        ["library_resolve_book"] = "list_books",
        ["library_get_book"] = "get_book",
        ["notes_list_for_book"] = "list_notes",
        ["notes_capture"] = "save_note",
        ["notes_save_passage"] = "save_passage",
        ["topics_list"] = "list_topics",
        ["topics_search"] = "list_topics",
        ["topics_get"] = "get_topic",
        ["writings_list"] = "list_drafts",
        ["writings_read"] = "read_draft",
        ["writings_create"] = "create_draft",
        ["reading_list_list"] = "reading_list",
        ["reading_list_add"] = "add_to_reading_list",
        ["library_create_or_match_book"] = "add_book",
        [AssistantCapabilities.EditBookDetailsName] = "update_book",
    };

    [GeneratedRegex(@"\b(?:knowledge|library|notes|topics|writings|reading_list|book_text)_[a-z_]+\b")]
    private static partial Regex CapabilityName();

    /// <summary>Ask Nostos capability names in <paramref name="text"/> as MCP tool names.</summary>
    public static string Rename(string text) =>
        CapabilityName().Replace(text, m => ToolFor.TryGetValue(m.Value, out var tool) ? tool : m.Value);
}

/// <summary>
/// Result ids an app can pass back to <c>fetch</c>: <c>note:&lt;id&gt;</c>,
/// <c>topic:&lt;id&gt;</c>, and <c>text:&lt;book&gt;:&lt;ordinal&gt;:&lt;sha256&gt;:&lt;extractor&gt;</c>
/// for a passage of a book, pinned to the exact indexed revision.
/// </summary>
internal static class McpEvidenceId
{
    public static string ForNote(Guid id) => $"note:{id:D}";

    public static string ForTopic(Guid id) => $"topic:{id:D}";

    public static string ForBookText(KnowledgeBookEvidence passage) =>
        $"text:{passage.BookId:D}:{passage.Ordinal.ToString(CultureInfo.InvariantCulture)}:{passage.SourceSha256}:{passage.ExtractorVersion}";

    /// <summary>The <c>knowledge_read_evidence</c> arguments, or null for an id that is not one of ours.</summary>
    public static McpEvidenceAddress? Parse(string? id)
    {
        var parts = (id ?? string.Empty).Trim().Split(':', 5);
        return parts switch
        {
            ["note", var note] when Guid.TryParse(note, out var noteId) =>
                new McpEvidenceAddress("note", NoteId: noteId),
            ["topic", var topic] when Guid.TryParse(topic, out var topicId) =>
                new McpEvidenceAddress("topic", TopicId: topicId),
            ["text", var book, var ordinal, var sha, var extractor]
                when Guid.TryParse(book, out var bookId)
                     && int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                     && sha.Length > 0 && extractor.Length > 0
                => new McpEvidenceAddress(
                    "book_text",
                    BookId: bookId,
                    Ordinal: n,
                    SourceSha256: sha,
                    ExtractorVersion: extractor),
            _ => null,
        };
    }
}

/// <summary>
/// The idempotency keys capture tools send with a write, so a call a client
/// never saw answered changes nothing the second time.
/// </summary>
internal static class McpToolKeys
{
    /// <summary>An idempotency key for a whole request: the tool and its arguments, words with their spacing evened out.</summary>
    public static string RequestKey(string tool, IReadOnlyDictionary<string, object?> args)
    {
        var text = string.Join('\u001f', args
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value switch
            {
                null => string.Empty,
                string words => string.Join(' ', words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
                IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
                var value => value.ToString(),
            }));
        return "mcp-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(tool + '\u001e' + text)))[..40];
    }

    /// <summary>The key for one captured note or passage: the book, the words, the quote and its topics.</summary>
    public static string IdempotencyKey(Guid bookId, string? thought, string? quote, string? quoteRef, string[]? topics)
    {
        var text = string.Join('\u001f',
            bookId.ToString("D"),
            Normalize(thought),
            Normalize(quote),
            quoteRef?.Trim() ?? string.Empty,
            string.Join('\u001e', (topics ?? []).Select(t => t.ToLowerInvariant()).Order(StringComparer.Ordinal)));
        return "mcp-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..40];

        static string Normalize(string? value) =>
            string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>One-line text, the way every MCP result presents it.</summary>
internal static class McpText
{
    private const int MaxSnippet = 600;

    /// <summary>A snippet on one line, cut at <see cref="MaxSnippet"/> characters.</summary>
    public static string Clip(string text)
    {
        var one = OneLine(text);
        return one.Length <= MaxSnippet ? one : one[..MaxSnippet].TrimEnd() + "…";
    }

    public static string OneLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Serialization the tools whose wire shape carries explicit <c>null</c>s.
/// <see cref="NostosMcp.Json"/> drops nulls, which is right for the result
/// records that predate the typed capability contracts, but the capability
/// payloads below have always carried keys like <c>"parentId": null</c>, and a
/// client reading one must keep seeing the same key.
/// </summary>
internal static class McpJson
{
    public static readonly JsonSerializerOptions Verbatim = new(JsonSerializerDefaults.Web);

    /// <summary>A typed result as the JSON a connected app reads.</summary>
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Verbatim);

    /// <summary>An app path as the absolute URL a connected app can open.</summary>
    public static string Link(McpCitations citations, string path) =>
        path.StartsWith('/') ? citations.AppUrl(path) : path;

    /// <summary>An app path that may be absent (a folder has no link to open).</summary>
    public static string? OptionalLink(McpCitations citations, string? path) =>
        path is null ? null : Link(citations, path);

    /// <summary>
    /// The capability's JSON read as <typeparamref name="T"/>, or null when it
    /// sent no payload at all.
    /// </summary>
    public static T? Data<T>(System.Text.Json.JsonElement? data) where T : class =>
        data is { } element ? element.Deserialize<T>(NostosMcp.Json) : null;
}
