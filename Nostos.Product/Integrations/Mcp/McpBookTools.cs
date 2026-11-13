using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.ReadingList;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// The library itself: the whole-library overview, finding and reading a book,
/// adding one, and filling in or correcting its details.
/// </summary>
[McpServerToolType]
public sealed class McpBookTools(IHttpContextAccessor http) : NostosMcpTool(http)
{
    [McpServerTool(Name = "overview", Title = "Library and Brain at a glance", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("A compact picture of the whole library and the Brain: every book with its collections, note totals, the most used topics, and the books with the most notes. Use it for questions about the library as a whole.")]
    public async Task<McpOverview> OverviewAsync(CancellationToken cancellationToken = default)
    {
        var library = await RunAsync<JsonElement>("library_overview", new { }, cancellationToken);
        var brain = await RunAsync<KnowledgeOverview>("knowledge_overview", new { }, cancellationToken);
        return new McpOverview(
            library,
            new McpBrainOverview(
                brain.TotalNotes,
                brain.UnlinkedNotes,
                brain.TotalTopics,
                brain.TopTopics.Select(t => new McpNamedCount(t.Name, t.UsageCount, McpEvidenceId.ForTopic(t.Id))).ToList(),
                brain.TopBooksByNoteCount.Select(b => new McpNamedCount(b.BookTitle, b.NoteCount, b.BookId.ToString("D"))).ToList()));
    }

    [McpServerTool(Name = "list_books", Title = "Find books", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists or finds books in the library by title, author, ISBN, collection or shelf. Returns each book's id for the other tools.")]
    public async Task<McpBookList> ListBooksAsync(
        [Description("Words in the title or author.")] string? search = null,
        [Description("An ISBN. Finds that exact book, ignoring the other filters.")] string? isbn = null,
        [Description("A shelf: All, Reading, Finished, NotStarted, Favorites or Unsorted. Defaults to All.")] string? shelf = null,
        [Description("Order: Recent, Title, LastRead, RecentlyFinished or Rating. Defaults to Recent.")] string? sort = null,
        [Description("Only books in this collection (id from `overview`).")] Guid? collectionId = null,
        [Description("Page, from 1.")] int page = 1,
        [Description("Books per page, 1 to 50. Defaults to 20.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var citations = Citations;
        if (!string.IsNullOrWhiteSpace(isbn))
        {
            var resolved = await RunAsync<McpResolvedBook>(
                "library_resolve_book",
                new { isbn = isbn.Trim(), includeExternalMetadata = false },
                cancellationToken);
            var found = new List<McpBookSummary>();
            if (resolved.MatchedBook is { } matched)
                found.Add(Summary(matched, citations));
            return new McpBookList(found, found.Count, 1, found.Count == 0 ? "No book in the library has that ISBN." : null);
        }

        var args = new Dictionary<string, object?>
        {
            ["search"] = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            ["filter"] = shelf,
            ["sort"] = sort,
            ["collectionId"] = collectionId,
            ["page"] = Math.Max(1, page),
            ["pageSize"] = Math.Clamp(pageSize, 1, 50),
        };
        var listed = await RunAsync<McpLibraryEnvelope<McpBookPage>>("library_list_books", args, cancellationToken);
        var items = listed.Data is { } books
            ? books.Items.Select(b => Summary(b, citations)).ToList()
            : [];
        var total = listed.Data?.TotalCount ?? items.Count;
        return new McpBookList(items, total, Math.Max(1, page), items.Count == 0 ? "No books match." : null);
    }

    [McpServerTool(Name = "get_book", Title = "One book", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One book's details: title, author, year, publisher, ISBN, description, progress, rating, the user's review, and a citation for the book as a whole.")]
    public async Task<McpBook> GetBookAsync(
        [Description("The book's id.")] Guid bookId,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync<McpLibraryEnvelope<BookDto>>("library_get_book", new { bookId }, cancellationToken);
        var book = result.Data ?? throw new McpException("No book in the library has that id.");
        var citations = Citations;
        var info = new McpBookInfo(book.Id, book.Title, McpText.Blank(book.Author), McpCitations.YearOf(book.PublishedDate));
        return new McpBook(
            book.Id,
            book.Title,
            McpText.Blank(book.Subtitle),
            info.Author,
            info.Year,
            book.Type,
            McpText.Blank(book.Publisher),
            McpText.Blank(book.Isbn),
            McpText.Blank(book.Description),
            book.ProgressPercent,
            book.Rating > 0 ? book.Rating : null,
            McpText.Blank(book.PersonalReview),
            citations.BookUrl(book.Id),
            citations.ForBook(info));
    }

    [McpServerTool(Name = "add_book", Title = "Add a book to the library", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Adds a book to the user's library from the details you give: a paper book by default, or an ebook or audiobook entry without its file. Use it only when the user asks to add a book. Nostos looks nothing up elsewhere, so pass the details you have. A book already in the library (same ISBN, or same title and author) comes back with `duplicate: true` and no second copy is made. When Nostos cannot tell, it returns `candidates` and adds nothing: ask the user, then call again with `confirmedBookId` or `forceCreate: true`. A matching entry on To read moves to the library, and its reason stays as a note on the book.")]
    public async Task<McpAddedBook> AddBookAsync(
        [Description("The title.")] string title,
        [Description("The author or authors.")] string? author = null,
        [Description("`physical` (a paper book, the default), `ebook` or `audiobook`. An ebook or audiobook is added without its file.")] string? type = null,
        [Description("The ISBN.")] string? isbn = null,
        [Description("The ASIN, for an audiobook.")] string? asin = null,
        [Description("The subtitle.")] string? subtitle = null,
        [Description("A description of the book.")] string? description = null,
        [Description("The publisher.")] string? publisher = null,
        [Description("The year or date it was published.")] string? publishedDate = null,
        [Description("The language.")] string? language = null,
        [Description("The number of pages.")] int? pageCount = null,
        [Description("The series it belongs to.")] string? series = null,
        [Description("Its number in the series.")] string? volumeNumber = null,
        [Description("After `candidates`: the id of the book the user says it is. Nothing is added.")] Guid? confirmedBookId = null,
        [Description("After `candidates`: true when the user wants it added as a separate book.")] bool forceCreate = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new McpException("'title' is required.");
        var kind = string.IsNullOrWhiteSpace(type) ? "physical" : type.Trim().ToLowerInvariant();
        if (kind == "paper")
            kind = "physical";
        if (kind is not ("physical" or "ebook" or "audiobook"))
            throw new McpException("'type' must be physical, ebook or audiobook.");

        var args = new Dictionary<string, object?>
        {
            ["type"] = kind,
            ["title"] = title.Trim(),
            ["author"] = McpText.Blank(author),
            ["isbn"] = McpText.Blank(isbn),
            ["asin"] = McpText.Blank(asin),
            ["subtitle"] = McpText.Blank(subtitle),
            ["description"] = McpText.Blank(description),
            ["publisher"] = McpText.Blank(publisher),
            ["publishedDate"] = McpText.Blank(publishedDate),
            ["language"] = McpText.Blank(language),
            ["pageCount"] = pageCount is > 0 ? pageCount : null,
            ["series"] = McpText.Blank(series),
            ["volumeNumber"] = McpText.Blank(volumeNumber),
            ["confirmedBookId"] = confirmedBookId,
            ["forceCreate"] = forceCreate,
        };

        // A client may resend a call it never saw an answer to: the same
        // request is one book. The receipt lives for days, so a replay that
        // points at a book deleted since then is asked again with a new key.
        var key = McpToolKeys.RequestKey("add_book", args);
        var added = await InvokeAsync("library_create_or_match_book", args, new AssistantToolContext(NostosMcp.ClientId, key), cancellationToken);
        var outcome = Data<McpLibraryEnvelope<McpBookCreateOutcome>>(added);
        if (added.Success && outcome is { Duplicate: true, Data.Book: { } replayed }
            && await Citations.BookAsync(replayed.Id, null, cancellationToken) is null)
        {
            added = await InvokeAsync("library_create_or_match_book", args, new AssistantToolContext(NostosMcp.ClientId, key + "-" + Guid.NewGuid().ToString("N")[..8]), cancellationToken);
            outcome = Data<McpLibraryEnvelope<McpBookCreateOutcome>>(added);
        }

        var citations = Citations;
        if (!added.Success)
        {
            if (added.ErrorCode != "confirmation_required")
                throw new McpException(McpToolNames.Rename(added.ErrorMessage ?? added.ErrorCode ?? "Nostos could not add the book."));

            var candidates = (outcome?.Data?.Candidates ?? [])
                .Select(candidate => new McpBookCandidate(
                    candidate.BookId,
                    candidate.Title,
                    McpText.Blank(candidate.Author),
                    McpText.Blank(candidate.Isbn),
                    citations.BookUrl(candidate.BookId)))
                .ToList();

            return new McpAddedBook(
                "choose",
                false,
                null,
                null,
                candidates.Count > 0 ? candidates : null,
                candidates.Count > 0
                    ? "Nothing was added: these books in the library may be the same one. Ask the user, then call add_book again with `confirmedBookId` for one of them, or `forceCreate: true` to add a separate book."
                    : "Nothing was added: a title alone cannot tell this book from others. Give the author or the ISBN, or call again with `forceCreate: true` to add it as it is.");
        }

        var addedBook = outcome?.Data ?? throw new McpException("Nostos did not add the book.");
        var book = addedBook.Book ?? throw new McpException("Nostos did not add the book.");
        var matched = addedBook.Outcome == "matched";

        // To read lists books the user does not have yet. Once the book is in
        // the library its entry moves, exactly as the app's "Got it" does: the
        // reason the user wanted it stays on the book as a note.
        var moved = new List<McpMovedFromToRead>();
        var readingList = Services.GetRequiredService<IReadingListService>();
        var editions = (book.OtherEditions ?? []).Select(e => e.Id).Append(book.Id).ToHashSet();
        foreach (var entry in await readingList.ListAsync(cancellationToken))
        {
            if (entry.OwnedBookId is not { } owned || !editions.Contains(owned))
                continue;
            var done = await readingList.MarkAcquiredAsync(entry.Id, book.Id, cancellationToken);
            if (done.Success)
                moved.Add(new McpMovedFromToRead(entry.Id, entry.Title, !string.IsNullOrWhiteSpace(entry.Why) || !string.IsNullOrWhiteSpace(entry.RecommendedBy)));
        }

        return new McpAddedBook(
            matched ? "matched" : "created",
            matched,
            Summary(book, citations),
            moved.Count > 0 ? moved : null,
            null,
            matched ? "This book was already in the library, so nothing new was added." : null);
    }

    [McpServerTool(Name = "update_book", Title = "Fill in or correct a book's details", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Fills in or corrects one book's details: title, subtitle, author, description, year, ISBN, publisher, language and series. Use it only when the user asks. Pass only the details to set. A detail the book already has is kept unless you pass `replace: true`, which only the user's request to change it allows. The result lists every changed detail with its old value, and `undo` holds the arguments that put them back.")]
    public async Task<McpUpdatedBook> UpdateBookAsync(
        [Description("The book (id from `list_books` or `add_book`).")] Guid bookId,
        [Description("The title.")] string? title = null,
        [Description("The subtitle.")] string? subtitle = null,
        [Description("The author or authors.")] string? author = null,
        [Description("The description.")] string? description = null,
        [Description("The year or date it was published.")] string? publishedDate = null,
        [Description("The ISBN.")] string? isbn = null,
        [Description("The publisher.")] string? publisher = null,
        [Description("The language.")] string? language = null,
        [Description("The series it belongs to.")] string? series = null,
        [Description("Its number in the series.")] string? volumeNumber = null,
        [Description("True to change details the book already has. With it, an empty string clears a detail.")] bool replace = false,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["bookId"] = bookId,
            ["title"] = title,
            ["subtitle"] = subtitle,
            ["author"] = author,
            ["description"] = description,
            ["publishedDate"] = publishedDate,
            ["isbn"] = isbn,
            ["publisher"] = publisher,
            ["language"] = language,
            ["series"] = series,
            ["volumeNumber"] = volumeNumber,
            ["replace"] = replace,
        };

        var edit = await RunAsync<BookDetailsEdit>(
            AssistantCapabilities.EditBookDetailsName,
            args,
            cancellationToken,
            new AssistantToolContext(NostosMcp.ClientId, McpToolKeys.RequestKey("update_book", args)));

        // The old values as update_book arguments: an empty one was empty.
        Dictionary<string, object?>? undo = null;
        if (edit.Changed.Count > 0)
        {
            undo = new Dictionary<string, object?> { ["bookId"] = bookId, ["replace"] = true };
            foreach (var change in edit.Changed)
                undo[change.Field] = change.From ?? string.Empty;
        }

        var words = new List<string>
        {
            edit.Changed.Count > 0
                ? $"Changed {string.Join(", ", edit.Changed.Select(c => c.Field))}. `undo` puts the old values back."
                : "Nothing changed.",
        };
        if (edit.Kept.Count > 0)
            words.Add($"Kept what the book already has for {string.Join(", ", edit.Kept.Select(k => k.Field))}. Pass `replace: true` only if the user asked to change it.");

        return new McpUpdatedBook(
            edit.Book.Id,
            edit.Book.Title,
            Citations.BookUrl(edit.Book.Id),
            edit.Changed,
            edit.Kept.Count > 0 ? edit.Kept : null,
            edit.Unchanged.Count > 0 ? edit.Unchanged : null,
            undo,
            string.Join(' ', words));
    }
}
