using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

public static class WritingsEndpoints
{
    public static IEndpointRouteBuilder MapWritingsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/writings");

        // GET: Fetch the entire file list (FLAT)
        group.MapGet(
            "/",
            async (IWritingRepository repo) =>
            {
                var items = await repo.GetAllAsync();
                var dtos = items.Select(w => new WritingDto(
                    w.Id,
                    w.Name,
                    w.Type.ToString(),
                    w.ParentId,
                    w.UpdatedAt
                ));
                return Results.Ok(dtos);
            }
        );

        // GET: Fetch a single document (with Content)
        group.MapGet(
            "/{id}",
            async (Guid id, IWritingRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                if (item is null)
                    return Results.NotFound();

                return Results.Ok(item.ToContentDto());
            }
        );

        // POST: Create new Folder or Document
        group.MapPost(
            "/",
            async (CreateWritingDto dto, IWritingRepository repo) =>
            {
                if (!Enum.TryParse<WritingType>(dto.Type, true, out var type))
                {
                    return Results.BadRequest("Invalid type. Must be 'Folder' or 'Document'.");
                }

                var model = new WritingModel
                {
                    Name = dto.Name,
                    Type = type,
                    ParentId = dto.ParentId,
                };

                await repo.AddAsync(model);

                return Results.Created($"/api/writings/{model.Id}", model.ToDto());
            }
        );

        // PUT: Update Name or Content (Auto-save)
        group.MapPut(
            "/{id}",
            async (Guid id, UpdateWritingDto dto, IWritingRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                if (item is null)
                    return Results.NotFound();

                item.Name = dto.Name;
                item.Content = dto.Content;
                item.UpdatedAt = DateTime.UtcNow;

                await repo.UpdateAsync(item);

                return Results.Ok(item.ToContentDto());
            }
        );

        // PUT: Move (Drag & Drop)
        group.MapPut(
            "/{id}/move",
            async (Guid id, MoveWritingDto dto, IWritingRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                if (item is null)
                    return Results.NotFound();

                // --- CYCLE DETECTION START ---
                if (dto.NewParentId.HasValue)
                {
                    if (dto.NewParentId == id)
                        return Results.BadRequest("Cannot move a folder into itself.");

                    var currentAncestorId = dto.NewParentId;
                    var visited = new HashSet<Guid>();
                    while (currentAncestorId != null)
                    {
                        if (!visited.Add(currentAncestorId.Value))
                            return Results.BadRequest(
                                "Circular reference detected in folder hierarchy."
                            );

                        if (currentAncestorId == id)
                            return Results.BadRequest(
                                "Cannot move a folder into its own descendant."
                            );

                        currentAncestorId = await repo.GetParentIdAsync(currentAncestorId.Value);
                    }
                }
                // --- CYCLE DETECTION END ---

                item.ParentId = dto.NewParentId;
                item.UpdatedAt = DateTime.UtcNow;

                await repo.UpdateAsync(item);

                return Results.Ok(item.ToDto());
            }
        );

        // DELETE
        group.MapDelete(
            "/{id}",
            async (Guid id, IWritingRepository repo) =>
            {
                var item = await repo.GetByIdAsync(id);
                if (item is null)
                    return Results.NotFound();

                await repo.DeleteAsync(item);

                return Results.NoContent();
            }
        );

        // GET: Fetch kept source notes for a writing document
        group.MapGet(
            "/{writingId}/notes",
            async (Guid writingId, IWritingRepository repo) =>
            {
                var notes = await repo.GetKeptNotesAsync(writingId);
                if (notes is null)
                    return Results.NotFound();

                return Results.Ok(notes.Select(wn => wn.ToDto()));
            }
        );

        // POST: Keep a source note available for a writing document
        group.MapPost(
            "/{writingId}/notes",
            async (Guid writingId, AddWritingSourceDto dto, IWritingRepository repo) =>
            {
                var result = await repo.AddKeptNoteAsync(writingId, dto.NoteId);
                return result.Status switch
                {
                    AddKeptNoteStatus.WritingNotFound => Results.NotFound(),
                    AddKeptNoteStatus.NoteNotFound => Results.NotFound(),
                    AddKeptNoteStatus.WritingIsFolder => Results.BadRequest("Cannot keep notes on a folder."),
                    AddKeptNoteStatus.AlreadyExists => Results.Ok(result.WritingNote!.ToDto()),
                    AddKeptNoteStatus.Success => Results.Created(
                        $"/api/writings/{writingId}/notes/{dto.NoteId}",
                        result.WritingNote!.ToDto()),
                    _ => Results.StatusCode(500)
                };
            }
        );

        // DELETE: Remove a kept source note from a writing document
        group.MapDelete(
            "/{writingId}/notes/{noteId}",
            async (Guid writingId, Guid noteId, IWritingRepository repo) =>
            {
                var writingExists = await repo.RemoveKeptNoteAsync(writingId, noteId);
                if (!writingExists)
                    return Results.NotFound();

                return Results.NoContent();
            }
        );

        return routes;
    }
}
