using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// The Writing Studio: the documents and folders in it, reading one as
/// Markdown, and making a new one.
/// </summary>
[McpServerToolType]
public sealed class McpWritingTools(IHttpContextAccessor http) : NostosMcpTool(http)
{
    [McpServerTool(Name = "list_drafts", Title = "Studio drafts", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The user's documents and folders in the Writing Studio, with their paths. Finds them by name or folder, not by what they say.")]
    public async Task<JsonElement> ListDraftsAsync(
        [Description("Words in a document's name or folder path.")] string? query = null,
        [Description("`Document` or `Folder`. Leave out for both.")] string? type = null,
        [Description("How many, 1 to 200. Defaults to 50.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var listed = await RunAsync<McpWritingList>(
            "writings_list",
            new Dictionary<string, object?> { ["query"] = query, ["type"] = type, ["limit"] = limit },
            cancellationToken);
        var citations = Citations;
        return McpJson.Element(listed with
            {
                Items = listed.Items
                    .Select(item => item with { Link = McpJson.OptionalLink(citations, item.Link) })
                    .ToList(),
            });
    }

    [McpServerTool(Name = "read_draft", Title = "Read a draft", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One Studio document as Markdown. Its `nostos:cite/` links are the Studio's live citations: keep them when you quote from the draft.")]
    public async Task<JsonElement> ReadDraftAsync(
        [Description("The document's id, from `list_drafts`.")] Guid writingId,
        CancellationToken cancellationToken = default)
    {
        var document = await RunAsync<McpWritingDocument>("writings_read", new { writingId }, cancellationToken);
        return McpJson.Element(document with { Link = McpJson.Link(Citations, document.Link) });
    }

    [McpServerTool(Name = "create_draft", Title = "Write a new Studio draft", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Makes a new document in the Writing Studio from Markdown. It never changes an existing document. Put quotations in as their `citation.markdown` links so the Studio shows them as live citations. Sending the same draft again keeps one document.")]
    public async Task<JsonElement> CreateDraftAsync(
        [Description("The document's name.")] string name,
        [Description("The document in Markdown.")] string? content = null,
        [Description("A folder to put it in (a folder id from `list_drafts`). Leave out for the top level.")] Guid? folderId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new McpException("'name' is required.");

        // Writings keep no receipts. A client that resends a call it never saw
        // answered gets the document it already made: same name, same folder,
        // same words, changed in the last ten minutes.
        var same = await RunAsync<McpWritingList>(
            "writings_list",
            new Dictionary<string, object?> { ["query"] = name.Trim(), ["type"] = "Document", ["limit"] = 20 },
            cancellationToken);
        foreach (var item in same.Items)
        {
            if (item.Name != name.Trim()
                || item.ParentId != folderId
                || item.UpdatedAt.ToUniversalTime() < DateTime.UtcNow.AddMinutes(-10))
            {
                continue;
            }

            var existing = await RunAsync<McpWritingDocument>("writings_read", new { writingId = item.Id }, cancellationToken);
            if (existing.Content?.Trim() == (content ?? string.Empty).Trim())
                return McpJson.Element(existing with { Link = McpJson.Link(Citations, existing.Link), Duplicate = true });
        }

        var created = await RunAsync<McpWritingCreated>(
            "writings_create",
            new Dictionary<string, object?> { ["name"] = name.Trim(), ["content"] = content, ["folderId"] = folderId },
            cancellationToken);
        return McpJson.Element(created with { Link = McpJson.Link(Citations, created.Link) });
    }
}
