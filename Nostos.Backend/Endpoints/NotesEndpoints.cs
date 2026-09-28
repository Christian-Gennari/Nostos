using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

public static class NotesEndpoints
{
    public static IEndpointRouteBuilder MapNotesEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api");

        // GET notes by book
        group.MapGet(
            "/books/{bookId}/notes",
            async (Guid bookId, INoteService notes) =>
                Results.Ok(await notes.GetByBookAsync(bookId))
        );

        // GET one canonical note for exact note/evidence deep-links.
        group.MapGet(
            "/notes/{id:guid}",
            async (Guid id, INoteService notes) =>
            {
                var note = await notes.GetAsync(id);
                return note is null ? Results.NotFound() : Results.Ok(note);
            }
        );

        // SEARCH notes by text (issue #158). The index could only ever match concept
        // NAMES, so a word living only in a note's body or quote was unreachable —
        // and 44 of this library's 63 notes belong to no concept at all, which no
        // concept row can ever lead to.
        group.MapGet(
            "/notes/search",
            async (string? query, INoteService notes, int? limit) =>
                Results.Ok(await notes.SearchAsync(query ?? string.Empty, limit ?? 50))
        );

        // A paged, searchable home for every saved note, whether or not it has a concept.
        group.MapGet(
            "/notes",
            async (INoteService notes, string? query, Guid? bookId, bool? withoutConcepts,
                bool? oldestFirst, int? limit, int? offset) =>
                Results.Ok(await notes.BrowseAsync(query, bookId, withoutConcepts ?? false,
                    oldestFirst ?? false, limit ?? 25, offset ?? 0))
        );

        // Notes linked to no concept, so they can be read at all.
        //
        // Paged on purpose. This used to answer with a bare list capped at 50,
        // which the Brain's permanent sidebar section then rendered as if it were
        // every unlinked note in the library (issue #256). The review mode that
        // replaces that section walks the whole set, so the reply carries the
        // total alongside a bounded page.
        group.MapGet(
            "/notes/unlinked",
            async (INoteService notes, int? limit, int? offset) =>
                Results.Ok(await notes.GetUnlinkedAsync(limit ?? 50, offset ?? 0))
        );

        // CREATE note. Optional `Idempotency-Key` + `X-Client-Id` headers make
        // the capture exactly-once (issue #260 §3); callers that send neither
        // keep the original non-idempotent behaviour.
        group.MapPost(
            "/books/{bookId}/notes",
            async (Guid bookId, CreateNoteDto dto, INoteService notes, HttpRequest request) =>
            {
                var clientId = HeaderValue(request, "X-Client-Id");
                var idempotencyKey = HeaderValue(request, "Idempotency-Key");
                var result = await notes.CreateAsync(bookId, dto, clientId, idempotencyKey);
                if (result.Success)
                    return Results.Created($"/api/notes/{result.Value!.Id}", result.Value);

                return result.ErrorCode switch
                {
                    NoteErrorCodes.BookNotFound => Results.NotFound(new { error = result.ErrorMessage }),
                    _ => Results.BadRequest(new { error = result.ErrorMessage }),
                };
            }
        );

        // IMPORT KOReader sidecar metadata. This first slice accepts one
        // text metadata.lua file, matches its book, and imports annotations
        // through the canonical note service so provenance/idempotency stay
        // identical to every other capture path.
        group.MapPost(
            "/notes/import/koreader",
            async (HttpRequest request, KoreaderNoteImportService importer, CancellationToken ct) =>
            {
                const long maxMetadataBytes = 2 * 1024 * 1024;
                if (!request.HasFormContentType)
                    return Results.BadRequest(new { error = "Expected multipart/form-data with a KOReader metadata.lua file." });

                var form = await request.ReadFormAsync(ct);
                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                if (file is null)
                    return Results.BadRequest(new { error = "A KOReader metadata.lua file is required." });
                if (file.Length <= 0 || file.Length > maxMetadataBytes)
                    return Results.BadRequest(new { error = "KOReader metadata.lua must be between 1 byte and 2 MB." });

                try
                {
                    await using var stream = file.OpenReadStream();
                    using var reader = new StreamReader(stream);
                    var source = await reader.ReadToEndAsync(ct);
                    return Results.Ok(await importer.ImportAsync(source, ct));
                }
                catch (FormatException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            }
        );

        // UPDATE note
        group.MapPut(
            "/notes/{id}",
            async (Guid id, UpdateNoteDto dto, INoteService notes) =>
            {
                var result = await notes.UpdateAsync(id, dto);
                return result.Success ? Results.Ok(result.Value) : Results.NotFound();
            }
        );

        // DELETE note
        group.MapDelete(
            "/notes/{id}",
            async (Guid id, INoteService notes) =>
            {
                var result = await notes.DeleteAsync(id);
                return result.Success ? Results.NoContent() : Results.NotFound();
            }
        );

        return routes;
    }

    private static string? HeaderValue(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : null;
}
