using System.Text.Json;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Backend.Services.Writings;
using Nostos.Product.Http;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

public static class WritingsEndpoints
{
    private static bool SameInstantToMillisecond(DateTime a, DateTime b) =>
        a.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond
        == b.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond;

    public static IEndpointRouteBuilder MapWritingsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/writings");

        // GET: Fetch the entire file list (FLAT)
        group.MapGet(
            "/",
            async (IWritingRepository repo, CancellationToken ct) =>
            {
                return Results.Ok(await repo.GetListAsync(ct));
            }
        );

        // GET: Fetch a single document (with Content)
        group.MapGet(
            "/{id}",
            async (Guid id, IWritingRepository repo, CancellationToken ct) =>
            {
                var item = await repo.GetByIdAsync(id, ct);
                if (item is null)
                    return Results.NotFound();

                return Results.Ok(item.ToContentDto());
            }
        );

        // POST: Create new Folder or Document
        group.MapPost(
            "/",
            async (CreateWritingDto dto, IWritingRepository repo, CancellationToken ct) =>
            {
                if (!Enum.TryParse<WritingType>(dto.Type, true, out var type))
                {
                    return ApiErrors.Message("Invalid type. Must be 'Folder' or 'Document'.");
                }

                var model = new WritingModel
                {
                    Name = dto.Name,
                    Type = type,
                    ParentId = dto.ParentId,
                };

                await repo.AddAsync(model, ct);

                return Results.Created($"/api/writings/{model.Id}", model.ToDto());
            }
        );

        // PUT: Update Name or Content (Auto-save)
        group.MapPut(
            "/{id}",
            async (
                Guid id,
                JsonElement body,
                IWritingRepository repo,
                IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
                CancellationToken ct) =>
            {
                UpdateWritingDto? dto;
                try
                {
                    dto = body.Deserialize<UpdateWritingDto>(
                        jsonOptions.Value.SerializerOptions);
                }
                catch (JsonException)
                {
                    return ApiErrors.EmptyBadRequest();
                }

                if (dto is null || body.ValueKind != JsonValueKind.Object)
                    return ApiErrors.EmptyBadRequest();

                var item = await repo.GetByIdAsync(id, ct);
                if (item is null)
                    return Results.NotFound();

                // Optimistic concurrency (issue #1363): a save made from a copy
                // older than the stored one is refused, not written over it.
                // Compared to the millisecond: PostgreSQL keeps microseconds,
                // SQLite 100 ns, and the client echoes back what it was given.
                if (dto.BaseUpdatedAt is { } baseUpdatedAt
                    && !SameInstantToMillisecond(baseUpdatedAt, item.UpdatedAt))
                {
                    return Results.Json(item.ToContentDto(), statusCode: StatusCodes.Status409Conflict);
                }

                item.Name = dto.Name;
                if (HasPropertyIgnoreCase(body, "content"))
                    item.Content = dto.Content;
                item.UpdatedAt = DateTime.UtcNow;

                await repo.SaveContentAsync(item, ct);

                return Results.Ok(item.ToContentDto());
            }
        )
        .Accepts<UpdateWritingDto>("application/json");

        // PUT: Move (Drag & Drop)
        group.MapPut(
            "/{id}/move",
            async (
                Guid id,
                MoveWritingDto dto,
                IWritingRepository repo,
                WritingTreeService tree,
                CancellationToken ct) =>
            {
                var item = await repo.GetByIdAsync(id, ct);
                if (item is null)
                    return Results.NotFound();

                // The tree check is one read for the whole move (WritingTreeService,
                // issue #1715); the messages below are the bodies clients already see.
                var outcome = await tree.CheckMoveAsync(id, dto.NewParentId, ct);
                if (outcome != WritingMoveOutcome.Allowed)
                {
                    return outcome switch
                    {
                        WritingMoveOutcome.IntoItself =>
                            ApiErrors.Message("Cannot move a folder into itself."),
                        WritingMoveOutcome.IntoDescendant =>
                            ApiErrors.Message("Cannot move a folder into its own descendant."),
                        WritingMoveOutcome.CircularHierarchy =>
                            ApiErrors.Message("Circular reference detected in folder hierarchy."),
                        _ => Results.NotFound(),
                    };
                }

                item.ParentId = dto.NewParentId;
                item.UpdatedAt = DateTime.UtcNow;

                await repo.UpdateAsync(item, ct);

                return Results.Ok(item.ToDto());
            }
        );

        // DELETE
        group.MapDelete(
            "/{id}",
            async (Guid id, IWritingRepository repo, CancellationToken ct) =>
            {
                var item = await repo.GetByIdAsync(id, ct);
                if (item is null)
                    return Results.NotFound();

                await repo.DeleteAsync(item, ct);

                return Results.NoContent();
            }
        );

        // GET: The writing's export page setup (issue #1164); 204 until one is saved.
        group.MapGet(
            "/{id}/page-setup",
            async (Guid id, IWritingRepository repo, CancellationToken ct) =>
            {
                var item = await repo.GetByIdAsync(id, ct);
                if (item is null)
                    return Results.NotFound();

                var setup = WritingPageSetup.Read(item.PageSetup);
                return setup is null ? Results.NoContent() : Results.Ok(setup);
            }
        );

        // PUT: Save the writing's export page setup. Not a content change, so
        // UpdatedAt stays as it is.
        group.MapPut(
            "/{id}/page-setup",
            async (Guid id, WritingPageSetupDto dto, IWritingRepository repo, CancellationToken ct) =>
            {
                var error = WritingPageSetup.Validate(dto);
                if (error is not null)
                    return ApiErrors.Message(error);

                var item = await repo.GetByIdAsync(id, ct);
                if (item is null)
                    return Results.NotFound();

                var normalized = WritingPageSetup.Normalize(dto);
                item.PageSetup = WritingPageSetup.Write(normalized);
                await repo.UpdateAsync(item, ct);

                return Results.Ok(normalized);
            }
        );

        // GET: Fetch kept source notes for a writing document
        group.MapGet(
            "/{writingId}/notes",
            async (Guid writingId, IWritingRepository repo, CancellationToken ct) =>
            {
                var notes = await repo.GetKeptNotesAsync(writingId, ct);
                if (notes is null)
                    return Results.NotFound();

                return Results.Ok(notes.Select(wn => wn.ToDto()));
            }
        );

        // POST: Keep a source note available for a writing document
        group.MapPost(
            "/{writingId}/notes",
            async (Guid writingId, AddWritingSourceDto dto, IWritingRepository repo, CancellationToken ct) =>
            {
                var result = await repo.AddKeptNoteAsync(writingId, dto.NoteId, ct);
                return result.Status switch
                {
                    AddKeptNoteStatus.WritingNotFound => Results.NotFound(),
                    AddKeptNoteStatus.NoteNotFound => Results.NotFound(),
                    AddKeptNoteStatus.WritingIsFolder => ApiErrors.Message("Cannot keep notes on a folder."),
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
            async (Guid writingId, Guid noteId, IWritingRepository repo, CancellationToken ct) =>
            {
                var writingExists = await repo.RemoveKeptNoteAsync(writingId, noteId, ct);
                if (!writingExists)
                    return Results.NotFound();

                return Results.NoContent();
            }
        );

        return routes;
    }

    private static bool HasPropertyIgnoreCase(JsonElement element, string propertyName)
    {
        return element.EnumerateObject().Any(
            property => string.Equals(
                property.Name,
                propertyName,
                StringComparison.OrdinalIgnoreCase));
    }
}
