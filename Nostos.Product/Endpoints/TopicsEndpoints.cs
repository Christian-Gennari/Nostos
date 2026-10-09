using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Mapping;
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
                        nc.Note.AnchorVerified
                    ))
                    .ToList();

                return Results.Ok(new TopicDetailDto(topic.Id, topic.Topic, notes));
            }
        );

        // GET topics that co-occur with this topic in notes
        group.MapGet(
            "/{id}/related",
            async (Guid id, ITopicRepository repo) => Results.Ok(await repo.GetRelatedAsync(id))
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

                var topic = await repo.RenameAsync(id, dto.Topic.Trim());
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
    }

    /// <summary>
    /// Deletes a topic and its note links. The topic will be re-created
    /// the next time a note containing [[Name]] is saved; that is expected.
    /// </summary>
    private static async Task<IResult> DeleteTopicAsync(Guid id, ITopicRepository repo)
    {
        return await repo.DeleteAsync(id) ? Results.NoContent() : Results.NotFound();
    }
}
