using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// The To read list: the books the user wants to read, whether or not they
/// own them yet, and putting one on the list.
/// </summary>
[McpServerToolType]
public sealed class McpReadingListTools(IHttpContextAccessor http) : NostosMcpTool(http)
{
    [McpServerTool(Name = "reading_list", Title = "To read", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The books the user wants to read, whether or not they own them yet: Next up first, then the rest. An entry with `ownedBookId` is already in the library (that book's id); the user keeps it on To read until they take it off the list.")]
    public async Task<JsonElement> ReadingListAsync(
        [Description("`all`, `next` (Next up only) or `someday`. Defaults to all.")] string? shelf = null,
        [Description("How many, at most 200. Defaults to 50.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var list = await RunAsync<McpReadingList>(
            "reading_list_list",
            new Dictionary<string, object?> { ["shelf"] = shelf, ["limit"] = limit },
            cancellationToken);
        return McpJson.Element(list with { Link = McpJson.Link(Citations, list.Link) });
    }

    [McpServerTool(Name = "add_to_reading_list", Title = "Put a book on To read", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Puts a book the user wants to read on their To read list. A book already there is not added twice. A book they already own may go on the list too: the entry comes back with `ownedBookId` set.")]
    public async Task<JsonElement> AddToReadingListAsync(
        [Description("The book's title.")] string title,
        [Description("The author.")] string? author = null,
        [Description("The ISBN, if known.")] string? isbn = null,
        [Description("The year or date it was published.")] string? publishedDate = null,
        [Description("Why the user wants to read it, in their words.")] string? why = null,
        [Description("Who recommended it.")] string? recommendedBy = null,
        [Description("True to put it on Next up.")] bool readNext = false,
        CancellationToken cancellationToken = default)
    {
        var added = await RunAsync<McpReadingListAdded>(
            "reading_list_add",
            new Dictionary<string, object?>
            {
                ["title"] = title,
                ["author"] = author,
                ["isbn"] = isbn,
                ["publishedDate"] = publishedDate,
                ["why"] = why,
                ["recommendedBy"] = recommendedBy,
                ["readNext"] = readNext,
            },
            cancellationToken);
        return McpJson.Element(added with { Link = McpJson.Link(Citations, added.Link) });
    }
}
