using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// The topics that connect notes across books: listing them and reading one
/// with its notes.
/// </summary>
[McpServerToolType]
public sealed class McpTopicTools(IHttpContextAccessor http) : NostosMcpTool(http)
{
    [McpServerTool(Name = "list_topics", Title = "Topics", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The topics that connect the user's notes across books, most used first, or those matching a search. Pass a topic's `id` to `fetch` to read its notes.")]
    public async Task<McpTopicList> ListTopicsAsync(
        [Description("Words to find topics by, through the notes linked to them. Leave out to list topics by use.")] string? search = null,
        CancellationToken cancellationToken = default)
    {
        var citations = Citations;
        if (string.IsNullOrWhiteSpace(search))
        {
            var topics = await RunAsync<List<TopicDto>>("topics_list", new { }, cancellationToken);
            return new McpTopicList(topics.Select(t => new McpTopic(McpEvidenceId.ForTopic(t.Id), t.Name, t.UsageCount, citations.TopicUrl(t.Id))).ToList());
        }

        var found = await RunAsync<List<TopicDto>>("topics_search", new { term = search.Trim() }, cancellationToken);
        var list = found.Select(t => new McpTopic(McpEvidenceId.ForTopic(t.Id), t.Name, t.UsageCount, citations.TopicUrl(t.Id))).ToList();
        return new McpTopicList(list);
    }

    [McpServerTool(Name = "get_topic", Title = "One topic", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One topic: the user's own words about it and the notes linked to it across books, newest first, each with a citation. Pass a topic id from `list_topics` or `search`.")]
    public async Task<McpTopicDetail> GetTopicAsync(
        [Description("The topic's id (`topic:<id>` or the bare id).")] string topicId,
        [Description("How many notes, 1 to 100. Defaults to 30.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var raw = topicId.StartsWith("topic:", StringComparison.Ordinal) ? topicId[6..] : topicId;
        if (!Guid.TryParse(raw, out var id))
            throw new McpException("That is not a topic id. Use an id from `list_topics`.");

        var topic = await RunAsync<McpTopicRead>(
            "topics_get",
            new Dictionary<string, object?> { ["topicId"] = id, ["limit"] = limit is null ? null : Math.Clamp(limit.Value, 1, 100) },
            cancellationToken);
        var noteIds = (topic.Notes ?? []).Select(n => n.NoteId).ToList();
        var citations = Citations;
        var notes = new List<McpNote>();
        if (noteIds.Count > 0)
        {
            var full = (await Services.GetRequiredService<INoteService>().GetManyAsync(noteIds, cancellationToken))
                .ToDictionary(n => n.Id);
            foreach (var noteId in noteIds)
            {
                if (!full.TryGetValue(noteId, out var n))
                    continue;
                var book = await citations.BookAsync(n.BookId, n.BookTitle, cancellationToken);
                if (book is null)
                    continue;
                notes.Add(new McpNote(
                    McpEvidenceId.ForNote(n.Id),
                    "note",
                    McpText.Blank(n.SelectedText),
                    McpText.Blank(n.Content),
                    null,
                    n.CreatedAt,
                    citations.ForNote(n.Id, book, n.Content, n.SelectedText, n.SourceAnchorKind, n.SourceAnchorValue, n.AnchorVerified)));
            }
        }

        return new McpTopicDetail(
            McpEvidenceId.ForTopic(id),
            topic.Name ?? "Topic",
            McpText.Blank(topic.Description),
            topic.NoteCount ?? notes.Count,
            citations.TopicUrl(id),
            notes);
    }
}
