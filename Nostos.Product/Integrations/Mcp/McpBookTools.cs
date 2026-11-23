using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Services;
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
    [Description("A compact picture of the whole library and the Brain: every book with its collections, note totals, the most used topics, how many topic kinds exist and how many topics have none, and the books with the most notes. Use it for questions about the library as a whole.")]
    public async Task<McpOverview> OverviewAsync(CancellationToken cancellationToken = default)
    {
        var library = await RunAsync<JsonElement>("library_overview", new { }, cancellationToken);
        var brain = await RunAsync<KnowledgeOverview>("knowledge_overview", new { }, cancellationToken);
        var topics = await Services.GetRequiredService<ITopicRepository>().GetAllWithUsageCountAsync(cancellationToken);
        var kinds = await Services.GetRequiredService<ITopicRepository>().GetAllKindsAsync(cancellationToken);
        return new McpOverview(
            library,
            new McpBrainOverview(
                brain.TotalNotes,
                brain.UnlinkedNotes,
                brain.TotalTopics,
                brain.TopTopics.Select(t => new McpNamedCount(t.Name, t.UsageCount, McpEvidenceId.ForTopic(t.Id))).ToList(),
                brain.TopBooksByNoteCount.Select(b => new McpNamedCount(b.BookTitle, b.NoteCount, b.BookId.ToString("D"))).ToList(),
                kinds.Count,
                topics.Count(t => t.KindId is null)));
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

    [McpServerTool(Name = "add_book", Title = "Add a book to the library", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Adds a book to the user's library from the details you give: a paper book by default, or an ebook or audiobook entry without its file. Use it only when the user asks to add a book. Nostos looks nothing else up, so pass the details you have. An `https` `coverUrl` is fetched as the book's cover; when it cannot be used the book is still added and the result says why. A book already in the library (same ISBN, or same title and author) comes back with `duplicate: true` and no second copy is made. When Nostos cannot tell, it returns `candidates` and adds nothing: ask the user, then call again with `confirmedBookId` or `forceCreate: true`. A To read entry for a book this call adds moves to the library, and its reason stays as a note on the book. An entry that was already in the library before the call stays on To read and comes back in `onToRead`.")]
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
        [Description("An https URL of the cover image. Nostos downloads it; the book is added without a cover if it cannot be used.")] string? coverUrl = null,
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

        // Which To read entries were already in the library before this call:
        // those stay on the list whatever the call does (see below).
        var readingList = Services.GetRequiredService<IReadingListService>();
        var ownedBefore = (await readingList.ListAsync(cancellationToken))
            .Where(e => e.OwnedBookId is not null)
            .Select(e => e.Id)
            .ToHashSet();

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

        // To read holds the books the user wants to read, owned or not. An
        // entry this call brought into the library moves, exactly as the app's
        // "Got it" does: the reason the user wanted it stays on the book as a
        // note. An entry that was already in the library before the call is
        // the user's own choice to keep it there ("It's in your library"), so
        // it stays, and is reported for the model to ask about.
        var moved = new List<McpMovedFromToRead>();
        var stillOnToRead = new List<McpOnToRead>();
        var editions = (book.OtherEditions ?? []).Select(e => e.Id).Append(book.Id).ToHashSet();
        foreach (var entry in await readingList.ListAsync(cancellationToken))
        {
            if (entry.OwnedBookId is not { } owned || !editions.Contains(owned))
                continue;
            if (ownedBefore.Contains(entry.Id))
            {
                stillOnToRead.Add(new McpOnToRead(entry.Id, entry.Title, entry.NextPosition is null ? "someday" : "next"));
                continue;
            }
            var done = await readingList.MarkAcquiredAsync(entry.Id, book.Id, cancellationToken);
            if (done.Success)
                moved.Add(new McpMovedFromToRead(entry.Id, entry.Title, !string.IsNullOrWhiteSpace(entry.Why) || !string.IsNullOrWhiteSpace(entry.RecommendedBy)));
        }

        var message = matched ? "This book was already in the library, so nothing new was added." : null;
        if (stillOnToRead.Count > 0)
            message = (message is null ? "" : message + " ")
                + "It is also on To read and stays there: the user takes a book they own off the list themselves, in the app.";

        string? warning = null;
        if (!string.IsNullOrWhiteSpace(coverUrl))
        {
            var cover = await ApplyCoverAsync(book.Id, coverUrl, replace: false, cancellationToken);
            warning = cover.Outcome switch
            {
                CoverOutcome.Failed when matched => "the cover was not changed: " + cover.Failure,
                CoverOutcome.Failed => "added without cover: " + cover.Failure,
                CoverOutcome.Kept => "the book already has a cover, so it was kept",
                _ => null,
            };
        }

        return new McpAddedBook(
            matched ? "matched" : "created",
            matched,
            Summary(book, citations),
            moved.Count > 0 ? moved : null,
            null,
            message,
            stillOnToRead.Count > 0 ? stillOnToRead : null,
            warning);
    }

    [McpServerTool(Name = "update_book", Title = "Fill in or correct a book's details", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Fills in or corrects one book's details: title, subtitle, author, description, year, ISBN, publisher, language, series, and the cover from an `https` `coverUrl`. Use it only when the user asks. Pass only the details to set. A detail the book already has is kept unless you pass `replace: true`, which only the user's request to change it allows. An existing cover is kept unless `replace: true` is passed when `coverUrl` is; the previous cover cannot be restored by this tool. The result lists every changed detail with its old value, and `undo` holds the arguments that put them back.")]
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
        [Description("An https URL of a new cover image. Sets a cover the book has none of; replaces an existing cover only with `replace: true`.")] string? coverUrl = null,
        [Description("True to change details the book already has. With it, an empty string clears a detail, and a `coverUrl` replaces an existing cover.")] bool replace = false,
        CancellationToken cancellationToken = default)
    {
        var detailsGiven = title is not null || subtitle is not null || author is not null
            || description is not null || publishedDate is not null || isbn is not null
            || publisher is not null || language is not null || series is not null
            || volumeNumber is not null;

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
            ["coverUrl"] = McpText.Blank(coverUrl),
            ["replace"] = replace,
        };

        // A cover-only call has no detail for the capability to edit, and it
        // refuses an empty edit, so it is asked only when a detail is given.
        // When neither is given the capability's refusal is the answer.
        BookDetailsEdit? edit = null;
        if (detailsGiven || string.IsNullOrWhiteSpace(coverUrl))
        {
            edit = await RunAsync<BookDetailsEdit>(
                AssistantCapabilities.EditBookDetailsName,
                args,
                cancellationToken,
                new AssistantToolContext(NostosMcp.ClientId, McpToolKeys.RequestKey("update_book", args)));
        }

        var cover = await ApplyCoverAsync(bookId, coverUrl, replace, cancellationToken);
        var coverChanged = cover.Outcome is CoverOutcome.Set or CoverOutcome.Replaced;

        var book = edit?.Book;
        if (book is null)
        {
            var current = await Services.GetRequiredService<ILibraryService>().GetBookAsync(bookId, cancellationToken);
            book = current.Data as BookDto ?? throw new McpException("No book in the library has that id.");
        }

        var detailChanged = edit?.Changed ?? [];
        var changed = detailChanged.ToList();
        if (coverChanged)
            changed.Add(new BookFieldChange("cover", cover.PreviousUrl, "a new cover image"));

        var kept = (edit?.Kept ?? []).ToList();
        if (cover.Outcome == CoverOutcome.Kept)
            kept.Add(new BookFieldKept("cover", cover.PreviousUrl ?? "a cover", coverUrl!.Trim()));

        // The old values as update_book arguments: an empty one was empty. A
        // cover's bytes are gone once replaced, so undo says it cannot restore
        // them instead of naming an argument that would.
        Dictionary<string, object?>? undo = null;
        if (detailChanged.Count > 0 || coverChanged)
        {
            undo = new Dictionary<string, object?> { ["bookId"] = bookId, ["replace"] = true };
            foreach (var change in detailChanged)
                undo[change.Field] = change.From ?? string.Empty;
            if (coverChanged)
                undo["cover"] = cover.Outcome == CoverOutcome.Replaced
                    ? "The previous cover was replaced and cannot be restored by this tool."
                    : "The cover was added and cannot be removed by this tool.";
        }

        var words = new List<string>();
        if (detailChanged.Count > 0)
            words.Add($"Changed {string.Join(", ", detailChanged.Select(c => c.Field))}. `undo` puts the old values back.");
        else if (!coverChanged)
            words.Add("Nothing changed.");
        if (coverChanged)
        {
            words.Add(cover.Outcome == CoverOutcome.Replaced ? "Cover replaced." : "Cover set.");
            words.Add((string)undo!["cover"]!);
        }
        if (kept.Count > 0)
            words.Add($"Kept what the book already has for {string.Join(", ", kept.Select(k => k.Field))}. Pass `replace: true` only if the user asked to change it.");

        return new McpUpdatedBook(
            book.Id,
            book.Title,
            Citations.BookUrl(book.Id),
            changed,
            kept.Count > 0 ? kept : null,
            edit is { Unchanged.Count: > 0 } ? edit.Unchanged : null,
            undo,
            string.Join(' ', words),
            cover.Outcome == CoverOutcome.Failed ? "the cover was not changed: " + cover.Failure : null);
    }

    // ------------------------------------------------------------------
    // Covers
    // ------------------------------------------------------------------

    private enum CoverOutcome
    {
        None,
        Set,
        Replaced,
        Kept,
        Failed,
    }

    private sealed record CoverApply(CoverOutcome Outcome, string? Failure = null, string? PreviousUrl = null);

    /// <summary>
    /// Sets a book's cover from a URL, the way <c>POST /api/books/{id}/cover</c>
    /// does: the bytes are downloaded through the acquisition path, stored by
    /// <see cref="IBookAssetStorage"/>, then recorded with <c>SetCover</c>.
    ///
    /// A cover the book already has is kept unless <paramref name="replace"/>
    /// is true. Anything that goes wrong is a reason in the result, never an
    /// error: the book itself is already added or edited by the time this runs.
    /// </summary>
    private async Task<CoverApply> ApplyCoverAsync(
        Guid bookId,
        string? coverUrl,
        bool replace,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
            return new CoverApply(CoverOutcome.None);

        var wanted = coverUrl.Trim();
        if (!Uri.TryCreate(wanted, UriKind.Absolute, out var url)
            || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new CoverApply(CoverOutcome.Failed, "the cover URL must be an absolute https URL");
        }

        var repo = Services.GetRequiredService<IBookRepository>();
        var book = await repo.GetByIdAsync(bookId, ct);
        if (book is null)
            return new CoverApply(CoverOutcome.Failed, "the book is no longer in the library");

        var hadCover = !string.IsNullOrWhiteSpace(book.FileDetails.CoverFileName);
        var previous = hadCover ? $"/api/books/{bookId:D}/cover" : null;
        if (hadCover && !replace)
            return new CoverApply(CoverOutcome.Kept, PreviousUrl: previous);

        var logger = Services.GetRequiredService<ILogger<McpBookTools>>();
        try
        {
            var downloader = Services.GetRequiredService<IProviderContentDownloader>();
            var fetched = await CoverDownload.TryAsync(
                downloader,
                url,
                new CoverUrlDownloadPolicy(url.Host),
                logger,
                ct);
            if (!fetched.Succeeded)
                return new CoverApply(CoverOutcome.Failed, fetched.Failure, previous);

            var storage = Services.GetRequiredService<IBookAssetStorage>();
            var fileName = $"cover{fetched.FileExtension}";
            await using (var stream = new MemoryStream(fetched.Bytes!))
                await storage.SaveBookCoverAsync(bookId, stream, fileName, ct);

            book.FileDetails.SetCover(fileName);
            await repo.UpdateAsync(book, ct);
            return new CoverApply(hadCover ? CoverOutcome.Replaced : CoverOutcome.Set, PreviousUrl: previous);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Storing cover art for book {BookId} failed; the book itself is unaffected. Exception type {ExceptionType}; details suppressed.",
                bookId,
                ex.GetType().Name);
            return new CoverApply(CoverOutcome.Failed, "the cover could not be saved", previous);
        }
    }
}
