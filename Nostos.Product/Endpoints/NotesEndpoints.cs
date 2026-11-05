using System.Text.Json;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Services;
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

        // GET the chapter titles of an EPUB in reading order (issue #1382), so
        // the Studio can name the chapter a highlight is in. Index 0 is the
        // first section; a book with no contents, or another format, has none.
        group.MapGet(
            "/books/{bookId:guid}/sections",
            async (Guid bookId, IBookAssetStorage storage, CancellationToken ct) =>
                Results.Ok(new { titles = await EpubSections.ReadAsync(storage, bookId, ct) })
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

        // GET several notes by id, with their topics (issue #1162): the Studio
        // shows the topics of the sources a writing cites, read from the
        // editor's current document. `ids` is comma-separated; ids that are
        // not GUIDs, or name no note in this library, are left out.
        group.MapGet(
            "/notes/by-ids",
            async (string? ids, INoteService notes, CancellationToken ct) =>
            {
                var parsed = (ids ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();
                if (parsed.Count > NoteService.MaxNotesByIds)
                    return Results.BadRequest(new { error = $"At most {NoteService.MaxNotesByIds} ids." });
                return Results.Ok(await notes.GetManyAsync(parsed, ct));
            }
        );

        // GET a passage in full (issue #1014). The note lists carry only its
        // opening, so the passage viewer asks for the rest when it opens.
        group.MapGet(
            "/notes/{id:guid}/passage",
            async (Guid id, INoteService notes, CancellationToken ct) =>
            {
                var passage = await notes.GetPassageAsync(id, ct);
                return passage is null ? Results.NotFound() : Results.Ok(passage);
            }
        );

        // SEARCH notes by text (issue #158). The index could only ever match topic
        // NAMES, so a word living only in a note's body or quote was unreachable —
        // and 44 of this library's 63 notes belong to no topic at all, which no
        // topic row can ever lead to.
        group.MapGet(
            "/notes/search",
            async (string? query, INoteService notes, int? limit) =>
                Results.Ok(await notes.SearchAsync(query ?? string.Empty, limit ?? 50))
        );

        // A paged, searchable home for every saved note, whether or not it has a
        // topic: each place of the Brain is one combination of these filters.
        // `order` is newest | oldest | book; `oldestFirst` is the older spelling.
        group.MapGet(
            "/notes",
            async (INoteService notes, string? query, Guid? bookId, bool? withoutTopics,
                bool? oldestFirst, int? limit, int? offset, string? kind, Guid? topicId,
                Guid? withTopicId, string? order, Guid? workId) =>
            {
                if (!TryParseOrder(order, oldestFirst ?? false, out var parsed))
                    return Results.BadRequest(new { error = "order is newest, oldest or book." });

                // workId: the notes of every edition of one book; bookId: one edition.
                var browse = new NoteBrowseQuery(query, bookId, withoutTopics ?? false, kind,
                    topicId, withTopicId, parsed, workId);
                return Results.Ok(await notes.BrowseAsync(browse, limit ?? 25, offset ?? 0));
            }
        );

        // How many notes the Brain's fixed places hold: All notes, Passages, Unsorted.
        group.MapGet(
            "/notes/summary",
            async (INoteService notes) => Results.Ok(await notes.GetSummaryAsync())
        );

        // The books that have notes in one place (the same filters as /notes,
        // without bookId), for the Brain's Book filter.
        group.MapGet(
            "/notes/books",
            async (INoteService notes, string? query, bool? withoutTopics, string? kind,
                Guid? topicId, Guid? withTopicId) =>
                Results.Ok(await notes.GetBookFacetsAsync(new NoteBrowseQuery(
                    query, null, withoutTopics ?? false, kind, topicId, withTopicId)))
        );

        // Notes linked to no topic, so they can be read at all.
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

        // E-READER HIGHLIGHT IMPORT (issue #656), in two steps so nothing is
        // written on a guess: `preview` reads the file and proposes a library
        // book per device book; `commit` takes the same file again with the
        // owner's decisions. Stateless on purpose — the server keeps no upload
        // between the two calls. The file is a Kobo database, a KOReader
        // sidecar, a Kindle's My Clippings.txt, a Readwise CSV export (#1219)
        // or Apple Books' annotation database (#1357), told apart by content.
        // Apple Books' library database, and either database's -wal file,
        // come along as `companion` parts of the same request.
        group.MapPost(
            "/notes/imports/preview",
            (HttpRequest request, HighlightImportService importer, CancellationToken ct) =>
                WithImportFileAsync(request, ct, (path, companions, _, _) => importer.PreviewAsync(path, companions, ct))
        );

        group.MapPost(
            "/notes/imports/commit",
            (HttpRequest request, HighlightImportService importer, CancellationToken ct) =>
                WithImportFileAsync(request, ct, (path, companions, fileName, form) =>
                {
                    var decisions = JsonSerializer.Deserialize<List<HighlightImportDecision>>(
                        form["decisions"].ToString() is { Length: > 0 } json ? json : "[]",
                        ImportJson) ?? [];
                    Guid? batchId = Guid.TryParse(form["batchId"].ToString(), out var parsed) ? parsed : null;
                    return importer.CommitAsync(path, companions, fileName, decisions, batchId, ct);
                })
        );

        group.MapGet(
            "/notes/imports/batches",
            async (HighlightImportService importer, CancellationToken ct) =>
                Results.Ok(await importer.ListBatchesAsync(5, ct))
        );

        // STARTER DRAFT (issue #1220): the imported highlights a first draft
        // opens with. `batchIds` narrows it to an import just made; without
        // it, the library's most recently imported highlights.
        group.MapGet(
            "/notes/imports/starter",
            async (string? batchIds, HighlightImportService importer, CancellationToken ct) =>
            {
                var ids = (batchIds ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .Take(50)
                    .ToList();
                return Results.Ok(await importer.StarterPassagesAsync(ids, ct));
            }
        );

        // UNDO one import: removes the notes it created, nothing else.
        group.MapDelete(
            "/notes/imports/batches/{id}",
            async (Guid id, HighlightImportService importer, CancellationToken ct) =>
                await importer.UndoAsync(id, ct) is { } removed
                    ? Results.Ok(new { removed })
                    : Results.NotFound()
        );

        // UPDATE note
        group.MapPut(
            "/notes/{id}",
            async (Guid id, UpdateNoteDto dto, INoteService notes) =>
            {
                var result = await notes.UpdateAsync(id, dto);
                if (result.Success)
                    return Results.Ok(result.Value);
                return result.ErrorCode == NoteErrorCodes.NoteNotFound
                    ? Results.NotFound()
                    : Results.BadRequest(new { error = result.ErrorMessage });
            }
        );

        // LOCATE a note's text: the reader reports where it found an imported
        // highlight or a quote Ask Nostos saved. See SetFoundLocationAsync.
        group.MapPut(
            "/notes/{id}/location",
            async (Guid id, NoteLocationDto dto, INoteService notes, CancellationToken ct) =>
            {
                var result = await notes.SetFoundLocationAsync(id, dto.CfiRange, ct);
                if (result.Success)
                    return Results.Ok(result.Value);
                return result.ErrorCode == NoteErrorCodes.NoteNotFound
                    ? Results.NotFound()
                    : Results.BadRequest(new { error = result.ErrorMessage });
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

    private static readonly JsonSerializerOptions ImportJson = new(JsonSerializerDefaults.Web);

    // SQLite needs a real file and may create -wal/-shm beside it, so the
    // upload is spooled to a private temp directory that is removed whatever
    // the outcome. Files keep their own names there, so a database's -wal
    // file sits beside it under the name SQLite looks for.
    private static async Task<IResult> WithImportFileAsync<T>(
        HttpRequest request,
        CancellationToken ct,
        Func<string, IReadOnlyList<string>, string, IFormCollection, Task<T>> run)
    {
        const long maxBytes = 256L * 1024 * 1024;
        const int maxCompanions = 4;
        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "Expected multipart/form-data with a highlights file." });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null)
            return Results.BadRequest(new { error = "Choose a highlights file to import." });
        if (file.Length <= 0 || file.Length > maxBytes)
            return Results.BadRequest(new { error = "The file must be between 1 byte and 256 MB." });

        var companions = form.Files.GetFiles("companion");
        if (companions.Count > maxCompanions)
            return Results.BadRequest(new { error = "Choose at most five files for one import." });
        if (companions.Any(companion => companion.Length <= 0 || companion.Length > maxBytes))
            return Results.BadRequest(new { error = "Each file must be between 1 byte and 256 MB." });

        var directory = Directory.CreateTempSubdirectory("nostos-highlight-import-");
        try
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            async Task<string> SaveAsync(IFormFile upload, string fallback)
            {
                var name = SafeUploadName(upload.FileName) ?? fallback;
                if (!used.Add(name))
                    name = $"{fallback}-{used.Count}";
                used.Add(name);
                var target = Path.Combine(directory.FullName, name);
                await using var stream = File.Create(target);
                await using var source = upload.OpenReadStream();
                await source.CopyToAsync(stream, ct);
                return target;
            }

            var path = await SaveAsync(file, "upload");
            var companionPaths = new List<string>(companions.Count);
            for (var i = 0; i < companions.Count; i++)
                companionPaths.Add(await SaveAsync(companions[i], $"companion-{i}"));

            // A -wal file is read through its database, never on its own.
            companionPaths.RemoveAll(candidate => candidate.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                || candidate.EndsWith("-shm", StringComparison.OrdinalIgnoreCase));

            return Results.Ok(await run(path, companionPaths, Path.GetFileName(file.FileName), form));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        finally
        {
            try { directory.Delete(recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>A plain file name to spool an upload under, or null when it has none worth keeping.</summary>
    private static string? SafeUploadName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (name.Length is 0 or > 200 || name.StartsWith('.'))
            return null;
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? null : name;
    }

    private static string? HeaderValue(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : null;
    private static bool TryParseOrder(string? value, bool oldestFirst, out NoteBrowseOrder order)
    {
        order = oldestFirst ? NoteBrowseOrder.Oldest : NoteBrowseOrder.Newest;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "":
                return true;
            case "newest":
                order = NoteBrowseOrder.Newest;
                return true;
            case "oldest":
                order = NoteBrowseOrder.Oldest;
                return true;
            case "book":
                order = NoteBrowseOrder.Book;
                return true;
            default:
                return false;
        }
    }
}
