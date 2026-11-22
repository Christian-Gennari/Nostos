using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// The topics that connect notes across books: listing and reading them, and
/// the kinds a connected app may use to classify them (issue #1771).
/// </summary>
[McpServerToolType]
public sealed class McpTopicTools(IHttpContextAccessor http) : NostosMcpTool(http)
{
    [McpServerTool(Name = "list_topics", Title = "Topics", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The topics that connect the user's notes across books, most used first, or those matching a search, each with its kind when it has one. Pass a topic's `id` to `fetch` to read its notes. Pass `withoutKind` to list only topics no kind has been given to yet.")]
    public async Task<McpTopicList> ListTopicsAsync(
        [Description("Words to find topics by, through the notes linked to them. Leave out to list topics by use.")] string? search = null,
        [Description("Only topics that have no kind yet, so an app can classify them.")] bool withoutKind = false,
        CancellationToken cancellationToken = default)
    {
        var citations = Citations;
        var repo = Repository();
        var kinds = await KindMapAsync(repo, cancellationToken);

        List<TopicDto> topics;
        if (string.IsNullOrWhiteSpace(search))
            topics = await RunAsync<List<TopicDto>>("topics_list", new { }, cancellationToken);
        else
            topics = await RunAsync<List<TopicDto>>("topics_search", new { term = search.Trim() }, cancellationToken);

        if (withoutKind)
            topics = topics.Where(t => t.KindId is null).ToList();

        return new McpTopicList(topics
            .Select(t => new McpTopic(
                McpEvidenceId.ForTopic(t.Id),
                t.Name,
                t.UsageCount,
                citations.TopicUrl(t.Id),
                KindOf(t.KindId, kinds),
                t.KindSource))
            .ToList());
    }

    [McpServerTool(Name = "get_topic", Title = "One topic", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One topic: its kind when it has one, the user's own words about it and the notes linked to it across books, newest first, each with a citation. Pass a topic id from `list_topics` or `search`.")]
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

        var repo = Repository();
        var row = await repo.GetByIdAsync(id, cancellationToken);
        var kinds = await KindMapAsync(repo, cancellationToken);

        return new McpTopicDetail(
            McpEvidenceId.ForTopic(id),
            topic.Name ?? "Topic",
            McpText.Blank(topic.Description),
            topic.NoteCount ?? notes.Count,
            citations.TopicUrl(id),
            notes,
            KindOf(row?.KindId, kinds),
            row?.KindSource);
    }

    [McpServerTool(Name = "list_topic_kinds", Title = "Topic kinds", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The kinds a topic can have, each with its palette colour key and how many topics use it, and how many of the 12 slots are still free. Use a kind's `id` with `set_topic_kinds`.")]
    public async Task<McpTopicKindList> ListTopicKindsAsync(CancellationToken cancellationToken = default)
    {
        var kinds = await Repository().GetAllKindsAsync(cancellationToken);
        return new McpTopicKindList(
            kinds.Select(k => new McpTopicKind(k.Id, k.Name, k.ColorKey, k.TopicCount)).ToList(),
            TopicKindModel.MaxKinds,
            Math.Max(0, TopicKindModel.MaxKinds - kinds.Count),
            McpJson.Link(Citations, "/brain/kinds"));
    }

    [McpServerTool(Name = "set_topic_kinds", Title = "Set topic kinds", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Gives each listed topic a kind, or clears it with a null kind. At most 100 assignments per call, applied in one transaction; calling again with the same assignments changes nothing. A kind the reader set by hand is skipped unless `overwriteReaderKinds` is true, and unknown topic ids are reported instead of failing the batch. Show the reader the plan before a large change: they can review and clear kinds set by a connected app in Nostos at Brain → Kinds → \"Set by a connected app\".")]
    public async Task<McpTopicKindAssignmentResult> SetTopicKindsAsync(
        [Description("The topics and their new kinds. `{ topicId, kindId }`; a null `kindId` clears the kind. Max 100.")] IReadOnlyList<McpTopicKindAssignment> assignments,
        [Description("Also replace a kind the reader set by hand. Defaults to false, which leaves those topics alone.")] bool overwriteReaderKinds = false,
        CancellationToken cancellationToken = default)
    {
        if (assignments is null || assignments.Count == 0)
            throw new McpException("Give at least one assignment: `{ topicId, kindId }`.");
        if (assignments.Count > 100)
            throw new McpException("At most 100 topic assignments can be set in one call. Split the list and call again.");

        var outcome = await Repository().SetTopicKindsAsync(
            assignments.Select(a => new TopicKindAssignment(a.TopicId, a.KindId)).ToList(),
            TopicKindSources.ConnectedApp,
            overwriteReaderKinds,
            ignoreUnknownTopicIds: true,
            cancellationToken);

        if (outcome.Status == SetTopicsKindStatus.KindNotFound)
            throw new McpException("That topic kind does not exist. Use `list_topic_kinds` for the kinds this library has.");
        if (outcome.Status != SetTopicsKindStatus.Success || outcome.Result is null)
            throw new McpException(outcome.ErrorMessage ?? "Nostos could not set those topic kinds.");

        var result = outcome.Result;
        var set = result.SetTopicIds ?? [];
        var cleared = result.ClearedTopicIds ?? [];
        var skipped = result.SkippedReaderKindTopicIds ?? [];
        var unknown = result.UnknownTopicIds ?? [];
        var parts = new List<string> { $"Set {set.Count} topic kind(s)" };
        if (cleared.Count > 0)
            parts.Add($"cleared {cleared.Count}");
        if (skipped.Count > 0)
            parts.Add($"skipped {skipped.Count} whose kind the reader set");
        if (unknown.Count > 0)
            parts.Add($"{unknown.Count} unknown topic id(s)");
        parts.Add("the reader can review or clear these in Nostos at Brain → Kinds → \"Set by a connected app\"");

        return new McpTopicKindAssignmentResult(set, cleared, skipped, unknown, string.Join("; ", parts) + ".");
    }

    [McpServerTool(Name = "create_topic_kind", Title = "Create a topic kind", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Creates a kind topics can be given, with its own palette colour, and returns its id for `set_topic_kinds`. Nostos allows at most 12 kinds; at the limit the call is refused with the kinds that already exist. Prefer `list_topic_kinds` and an existing kind before making one.")]
    public async Task<McpCreatedTopicKind> CreateTopicKindAsync(
        [Description("The kind's name, 1 to 100 characters, unique in this library (for example Person or Place).")] string name,
        [Description("A palette colour key from `list_topic_kinds`, kind-1 to kind-12. Leave out to take the next free colour.")] string? colorKey = null,
        CancellationToken cancellationToken = default)
    {
        var repo = Repository();
        var kinds = await repo.GetAllKindsAsync(cancellationToken);
        if (kinds.Count >= TopicKindModel.MaxKinds)
            throw new McpException(AtLimitMessage(kinds));

        var chosenColor = colorKey?.Trim();
        if (string.IsNullOrEmpty(chosenColor))
        {
            var used = kinds.Select(k => k.ColorKey).ToHashSet(StringComparer.Ordinal);
            chosenColor = TopicKindModel.PaletteColorKeys.FirstOrDefault(key => !used.Contains(key));
        }

        if (string.IsNullOrEmpty(chosenColor))
            throw new McpException(AtLimitMessage(kinds));

        var result = await repo.CreateKindAsync(name, chosenColor, cancellationToken);
        switch (result.Status)
        {
            case TopicKindMutationStatus.Success when result.Kind is { } kind:
                return new McpCreatedTopicKind(
                    kind.Id,
                    kind.Name,
                    kind.ColorKey,
                    McpJson.Link(Citations, "/brain/kinds"),
                    $"Created the kind \"{kind.Name}\" ({kind.ColorKey}). Use its id with `set_topic_kinds`.");
            case TopicKindMutationStatus.LimitReached:
                throw new McpException(AtLimitMessage(await repo.GetAllKindsAsync(cancellationToken)));
            default:
                throw new McpException(result.ErrorMessage ?? "Nostos could not create that topic kind.");
        }
    }

    private ITopicRepository Repository() => Services.GetRequiredService<ITopicRepository>();

    private static async Task<IReadOnlyDictionary<Guid, TopicKindDto>> KindMapAsync(
        ITopicRepository repo,
        CancellationToken cancellationToken) =>
        (await repo.GetAllKindsAsync(cancellationToken)).ToDictionary(k => k.Id);

    private static McpTopicKindRef? KindOf(
        Guid? kindId,
        IReadOnlyDictionary<Guid, TopicKindDto> kinds) =>
        kindId is { } id && kinds.TryGetValue(id, out var kind)
            ? new McpTopicKindRef(kind.Id, kind.Name)
            : null;

    private static string AtLimitMessage(IReadOnlyList<TopicKindDto> kinds) =>
        $"Nostos already has {TopicKindModel.MaxKinds} topic kinds, the most it allows: "
        + string.Join(", ", kinds.Select(k => $"\"{k.Name}\""))
        + ". Use one of them, or delete a kind in Nostos at Brain → Kinds.";
}
