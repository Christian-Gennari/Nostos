using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

public static class TopicsEndpoints
{
    public static IEndpointRouteBuilder MapTopicsEndpoints(this IEndpointRouteBuilder routes)
    {
        MapTopicRoutes(routes.MapGroup("/api/topics"));

        // Transition alias: clients built before the Concepts -> Topics rename
        // keep reaching the same handlers. Request/response bodies use the new
        // topic field names on both prefixes.
        MapTopicRoutes(routes.MapGroup("/api/concepts"));

        return routes;
    }

    private static void MapTopicRoutes(RouteGroupBuilder group)
    {

        // GET all topics (The Index)
        group.MapGet(
            "/",
            async (string? search, ITopicRepository repo) =>
            {
                if (string.IsNullOrWhiteSpace(search))
                {
                    var dtos = await repo.GetAllWithUsageCountAsync();
                    return Results.Ok(dtos);
                }

                var results = await repo.SearchByNoteTextAsync(search);
                return Results.Ok(results);
            }
        );

        // GET aggregate topic statistics (The Index header)
        group.MapGet(
            "/stats",
            async (ITopicRepository repo) => Results.Ok(await repo.GetStatsAsync())
        );

        // GET single topic details (The Context)
        group.MapGet(
            "/{id}",
            async (Guid id, ITopicRepository repo) =>
            {
                var topic = await repo.GetByIdWithNotesAsync(id);
                if (topic is null)
                    return Results.NotFound();

                var notes = topic
                    .NoteTopics.Select(nc => new NoteContextDto(
                        nc.NoteId,
                        nc.Note.Content,
                        nc.Note.SelectedText,
                        nc.Note.CfiRange,
                        nc.Note.BookId,
                        nc.Note.Book?.Title ?? "Unknown Book",
                        nc.Note.CreatedAt,
                        nc.Note.SourceAnchorKind,
                        nc.Note.SourceAnchorValue,
                        nc.Note.AnchorVerified,
                        nc.Note.HighlightColour,
                        nc.Note.Kind,
                        nc.Note.PassageTitle,
                        nc.Note.PassageEnd,
                        nc.Note.IsPassage() ? PassageText.WordCount(nc.Note.SelectedText) : null,
                        nc.Note.Book?.Author
                    ) with
                    {
                        SelectedText = nc.Note.IsPassage()
                            ? PassageText.Opening(nc.Note.SelectedText)
                            : nc.Note.SelectedText,
                    })
                    .ToList();

                return Results.Ok(new TopicDetailDto(topic.Id, topic.Topic, notes));
            }
        );

        // GET topics that co-occur with this topic in notes
        group.MapGet(
            "/{id}/related",
            async (Guid id, ITopicRepository repo) => Results.Ok(await repo.GetRelatedAsync(id))
        );

        // GET where a topic is used in writing (issue #1162): read-only, joined
        // at read time from what writings cite. Nothing adds a topic to a writing.
        group.MapGet(
            "/{id}/writings",
            async (Guid id, int? limit, ITopicRepository repo, CancellationToken ct) =>
            {
                var usage = await repo.GetWritingUsageAsync(id, limit ?? 5, ct);
                return usage is null ? Results.NotFound() : Results.Ok(usage);
            }
        );

        // GET whole-brain topic co-occurrence graph
        group.MapGet(
            "/graph",
            async (ITopicRepository repo) => Results.Ok(await repo.GetGraphAsync())
        );

        // UPDATE name, merging into an existing topic with the same name
        group.MapPut(
            "/{id}",
            async (Guid id, UpdateTopicDto dto, ITopicRepository repo) =>
            {
                if (string.IsNullOrWhiteSpace(dto.Topic))
                    return Results.BadRequest(new { error = "Topic name cannot be empty." });
                // The new name is written into the notes' [[ ]] references.
                if (!TopicReferences.IsValidName(dto.Topic))
                    return Results.BadRequest(new { error = TopicReferences.InvalidNameMessage });

                var topic = await repo.RenameAsync(id, dto.Topic.Trim());
                return topic is null
                    ? Results.NotFound()
                    : Results.Ok(topic.ToDto());
            }
        );

        // The reader's own words about a topic; an empty value clears them.
        group.MapPut(
            "/{id}/description",
            async (Guid id, UpdateTopicDescriptionDto dto, ITopicRepository repo) =>
            {
                // The length that counts is what is kept: the trimmed text.
                if (TopicModel.NormalizeDescription(dto.Description) is { Length: > TopicModel.MaxDescriptionLength })
                    return Results.BadRequest(new { error = $"A topic's description is at most {TopicModel.MaxDescriptionLength} characters." });

                var topic = await repo.SetDescriptionAsync(id, dto.Description);
                return topic is null
                    ? Results.NotFound()
                    : Results.Ok(topic.ToDto());
            }
        );

        // MERGE one topic into another
        group.MapPost(
            "/{id}/merge",
            async (Guid id, MergeTopicDto dto, ITopicRepository repo) =>
            {
                if (id == dto.TargetId)
                    return Results.BadRequest(new { error = "A topic cannot be merged into itself." });

                var topic = await repo.MergeAsync(id, dto.TargetId);
                return topic is null
                    ? Results.NotFound()
                    : Results.Ok(topic.ToDto());
            }
        );

        group.MapDelete(
            "/{id}",
            async (Guid id, ITopicRepository repo) =>
                await DeleteTopicAsync(id, repo)
        );

        // LINK named topics to many notes at once (the Brain's selection bar
        // and its one-by-one sorter); topics are created when new.
        group.MapPost(
            "/link",
            async (TopicLinkRequestDto dto, INoteService notes, CancellationToken ct) =>
                LinkResult(await notes.LinkTopicsAsync(dto.Topics, dto.NoteIds, ct))
        );

        // UNLINK named topics from many notes at once; also how a link is undone.
        group.MapPost(
            "/unlink",
            async (TopicLinkRequestDto dto, INoteService notes, CancellationToken ct) =>
                LinkResult(await notes.UnlinkTopicsAsync(dto.Topics, dto.NoteIds, ct))
        );
    }

    private static IResult LinkResult(NoteCommandResult<TopicLinkResultDto> result) =>
        result.Success
            ? Results.Ok(result.Value)
            : result.ErrorCode == NoteErrorCodes.NoteNotFound
                ? Results.NotFound(new { error = result.ErrorMessage })
                : Results.BadRequest(new { error = result.ErrorMessage });

    /// <summary>
    /// Deletes a topic and its note links, and turns its references in the
    /// notes' text into plain words, so it is not re-created on their next save.
    /// </summary>
    private static async Task<IResult> DeleteTopicAsync(Guid id, ITopicRepository repo)
    {
        return await repo.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();
    }
}
