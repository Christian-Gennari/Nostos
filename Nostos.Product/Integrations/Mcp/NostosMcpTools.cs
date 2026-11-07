using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// The MCP v1 tool set (epic #1465). Every tool runs one or two Ask Nostos
/// capabilities through <see cref="AssistantCapabilityRegistry"/>, so
/// validation, limits and writes are exactly Ask Nostos's, and only shapes the
/// result for an outside model: one result list, ids it can pass back, and a
/// <see cref="McpCitation"/> on everything it may quote.
///
/// Services come from the HTTP request's own scope, never the MCP server's:
/// on Cloud that scope is the one bound to the signed-in account's library.
/// </summary>
[McpServerToolType]
public sealed class NostosMcpTools(IHttpContextAccessor http)
{
    private const int MaxSearchResults = 24;
    private const int MaxNotesPage = 100;
    private const int MaxSnippet = 600;

    // ------------------------------------------------------------------
    // Read
    // ------------------------------------------------------------------

    [McpServerTool(Name = "search", Title = "Search the library", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Searches the user's own reading in one go: notes, highlights and passages they kept, topics, and the text of their imported EPUB and PDF books. Returns results with an id you can pass to `fetch`, a short excerpt, a link, and a citation. Use it before saying what a book or the user's notes say.")]
    public async Task<McpSearchResult> SearchAsync(
        [Description("What to find, in a few words or a short question.")] string query,
        [Description("Only look in these books (ids from `list_books`). Leave out to search the whole library.")] Guid[]? bookIds = null,
        [Description("Only one kind of result: `notes`, `topics` or `book_text`. Leave out for all.")] string? kind = null,
        [Description("How many results of each kind, 1 to 8. Defaults to 6.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new McpException("'query' is required.");
        if (kind is not null and not ("notes" or "topics" or "book_text"))
            throw new McpException("'kind' must be notes, topics or book_text.");

        var args = new Dictionary<string, object?> { ["query"] = query.Trim() };
        if (bookIds is { Length: > 0 }) args["bookIds"] = bookIds;
        if (limit is not null) args["maxPerSource"] = Math.Clamp(limit.Value, 1, 8);

        var response = await RunAsync<KnowledgeSearchResponse>("knowledge_search", args, cancellationToken);
        var citations = Citations;
        var results = new List<McpSearchHit>();

        if (kind is null or "notes")
        {
            // The evidence carries a snippet only. The notes themselves say
            // whether each one quotes the book, which decides the citation.
            var full = response.Notes.Count == 0
                ? new Dictionary<Guid, NoteSearchHitDto>()
                : (await Services.GetRequiredService<INoteService>()
                    .GetManyAsync(response.Notes.Select(n => n.NoteId).Distinct().ToList(), cancellationToken))
                    .ToDictionary(n => n.Id);
            foreach (var note in response.Notes)
            {
                var book = await citations.BookAsync(note.BookId, note.BookTitle, cancellationToken);
                full.TryGetValue(note.NoteId, out var kept);
                results.Add(new McpSearchHit(
                    McpEvidenceId.ForNote(note.NoteId),
                    "note",
                    book?.Title ?? note.BookTitle ?? "Note",
                    Clip(note.Snippet),
                    citations.NoteUrl(note.NoteId),
                    book is null ? null : citations.ForNote(
                        note.NoteId,
                        book,
                        kept?.Content ?? note.Snippet,
                        kept?.SelectedText,
                        note.SourceAnchorKind,
                        note.SourceAnchorValue,
                        note.AnchorVerified),
                    note.TopicNames.Count > 0 ? note.TopicNames : null));
            }
        }

        if (kind is null or "topics")
        {
            foreach (var topic in response.Topics)
            {
                results.Add(new McpSearchHit(
                    McpEvidenceId.ForTopic(topic.TopicId),
                    "topic",
                    topic.Name,
                    Clip(topic.MatchSnippet ?? string.Join(" · ", topic.SupportingNotes.Select(n => n.Snippet))),
                    citations.TopicUrl(topic.TopicId),
                    null,
                    null));
            }
        }

        if (kind is null or "book_text")
        {
            foreach (var passage in response.BookPassages)
            {
                var book = await citations.BookAsync(passage.BookId, passage.BookTitle, cancellationToken)
                    ?? new McpBookInfo(passage.BookId, passage.BookTitle, passage.BookAuthor, null);
                var citation = citations.ForBookText(book, passage);
                results.Add(new McpSearchHit(
                    McpEvidenceId.ForBookText(passage),
                    "book_text",
                    citation.Location is null ? book.Title : $"{book.Title}, {citation.Location}",
                    Clip(passage.Text),
                    citation.Url,
                    citation,
                    null));
            }
        }

        var pending = response.BookTextStates
            .Where(s => s.Status is not Nostos.Product.BookText.BookTextIngestionStatus.Ready)
            .Select(s => s.SourceFileName ?? s.BookId.ToString("D"))
            .ToList();

        return new McpSearchResult(
            results.Take(MaxSearchResults).ToList(),
            results.Count == 0 ? "Nothing in the library matches. Say so rather than answering from memory." : null,
            pending.Count > 0 ? pending : null);
    }

    [McpServerTool(Name = "fetch", Title = "Read a result in full", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Reads one result from `search` in full: the whole note or highlight, a topic with its notes, or a passage of a book. Pass the result's `id`.")]
    public async Task<McpDocument> FetchAsync(
        [Description("The `id` of a result from `search`.")] string id,
        CancellationToken cancellationToken = default)
    {
        var handle = McpEvidenceId.Parse(id)
            ?? throw new McpException("That id is not a Nostos result id. Use an id that `search` returned.");
        var read = await RunAsync<KnowledgeReadResponse>("knowledge_read_evidence", handle, cancellationToken);
        var citations = Citations;

        if (read.Note is { } note)
        {
            var book = await citations.BookAsync(note.BookId, note.BookTitle, cancellationToken);
            var text = string.IsNullOrWhiteSpace(note.SelectedText)
                ? note.Content
                : string.IsNullOrWhiteSpace(note.Content) ? $"“{note.SelectedText}”" : $"“{note.SelectedText}”\n\n{note.Content}";
            return new McpDocument(
                id,
                "note",
                book?.Title ?? note.BookTitle ?? "Note",
                text,
                citations.NoteUrl(note.NoteId),
                book is null ? null : citations.ForNote(note.NoteId, book, note.Content, note.SelectedText, note.SourceAnchorKind, note.SourceAnchorValue, note.AnchorVerified),
                new Dictionary<string, object?>
                {
                    ["bookId"] = note.BookId,
                    ["quote"] = string.IsNullOrWhiteSpace(note.SelectedText) ? null : note.SelectedText,
                    ["note"] = string.IsNullOrWhiteSpace(note.Content) ? null : note.Content,
                    ["topics"] = note.TopicNames,
                    ["createdAt"] = note.CreatedAt,
                });
        }

        if (read.Topic is { } topic)
        {
            var lines = topic.Notes.Select(n => $"- {n.Snippet} ({n.BookTitle ?? "a book"}; id {McpEvidenceId.ForNote(n.NoteId)})");
            return new McpDocument(
                id,
                "topic",
                topic.Name,
                string.Join('\n', lines),
                citations.TopicUrl(topic.TopicId),
                null,
                new Dictionary<string, object?> { ["notes"] = topic.UsageCount });
        }

        if (read.BookPassage is { } passage)
        {
            var book = await citations.BookAsync(passage.BookId, passage.BookTitle, cancellationToken)
                ?? new McpBookInfo(passage.BookId, passage.BookTitle, passage.BookAuthor, null);
            var citation = citations.ForBookText(book, passage);
            return new McpDocument(
                id,
                "book_text",
                citation.Location is null ? book.Title : $"{book.Title}, {citation.Location}",
                passage.Text,
                citation.Url,
                citation,
                new Dictionary<string, object?>
                {
                    ["bookId"] = passage.BookId,
                    ["headings"] = passage.HeadingPath,
                });
        }

        throw new McpException("Nothing in the library has that id any more.");
    }

    [McpServerTool(Name = "find_quote", Title = "Find a passage in a book", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds a passage in one book by its first and last words, and returns the book's exact words. For a paper book it looks in the digital editions of the same work. Use it before quoting a passage the user names, or before `save_note` with a `quoteRef`. When it asks the user to choose, call it again with `choice`.")]
    public async Task<JsonElement> FindQuoteAsync(
        [Description("The book (id from `list_books`).")] Guid bookId,
        [Description("The words the passage starts with.")] string startsWith,
        [Description("The words the passage ends with.")] string endsWith,
        [Description("The page, if the user named one. Only narrows the search.")] int? page = null,
        [Description("The chapter, if the user named one. Only narrows the search.")] string? chapter = null,
        [Description("After the tool asked to choose: the number picked (1 confirms a single passage). Send the same words again.")] int? choice = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["bookId"] = bookId,
            ["startsWith"] = startsWith,
            ["endsWith"] = endsWith,
            ["page"] = page,
            ["chapter"] = chapter,
            ["choice"] = choice,
        };
        var found = JsonNode.Parse((await RunAsync<JsonElement>("book_text_find_quote", args, cancellationToken)).GetRawText())!.AsObject();

        // The guidance is written for Ask Nostos; name the tools this client has.
        if (found["message"] is JsonValue message && message.TryGetValue<string>(out var words))
            found["message"] = McpToolNames.Rename(words);

        // Every passage it shows is citable before anything is saved: the book
        // the user named (a paper book is cited as itself), at its chapter.
        if (found["candidates"] is JsonArray candidates && candidates.Count > 0
            && await Citations.BookAsync(bookId, null, cancellationToken) is { } book)
        {
            foreach (var candidate in candidates.OfType<JsonObject>())
            {
                var citation = Citations.ForQuote(
                    book,
                    candidate["chapter"]?.GetValue<string>(),
                    candidate["text"]?.GetValue<string>());
                candidate["citation"] = JsonSerializer.SerializeToNode(citation, NostosMcp.Json);
            }
        }

        return JsonSerializer.SerializeToElement(found, NostosMcp.Json);
    }

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
            var resolved = await RunAsync<JsonElement>(
                "library_resolve_book",
                new { isbn = isbn.Trim(), includeExternalMetadata = false },
                cancellationToken);
            var found = new List<McpBookSummary>();
            if (resolved.TryGetProperty("matchedBook", out var matched) && matched.ValueKind == JsonValueKind.Object)
                found.Add(Summary(matched.Deserialize<BookDto>(NostosMcp.Json)!, citations));
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
        var result = await RunAsync<JsonElement>("library_list_books", args, cancellationToken);
        var data = result.TryGetProperty("data", out var d) ? d : result;
        var items = data.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(b => Summary(b.Deserialize<BookDto>(NostosMcp.Json)!, citations)).ToList()
            : [];
        var total = data.TryGetProperty("totalCount", out var t) && t.TryGetInt32(out var count) ? count : items.Count;
        return new McpBookList(items, total, Math.Max(1, page), items.Count == 0 ? "No books match." : null);
    }

    [McpServerTool(Name = "get_book", Title = "One book", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One book's details: title, author, year, publisher, ISBN, description, progress, rating, the user's review, and a citation for the book as a whole.")]
    public async Task<McpBook> GetBookAsync(
        [Description("The book's id.")] Guid bookId,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync<JsonElement>("library_get_book", new { bookId }, cancellationToken);
        var data = result.TryGetProperty("data", out var d) ? d : result;
        var book = data.Deserialize<BookDto>(NostosMcp.Json)
            ?? throw new McpException("No book in the library has that id.");
        var citations = Citations;
        var info = new McpBookInfo(book.Id, book.Title, Blank(book.Author), McpCitations.YearOf(book.PublishedDate));
        return new McpBook(
            book.Id,
            book.Title,
            Blank(book.Subtitle),
            info.Author,
            info.Year,
            book.Type,
            Blank(book.Publisher),
            Blank(book.Isbn),
            Blank(book.Description),
            book.ProgressPercent,
            book.Rating > 0 ? book.Rating : null,
            Blank(book.PersonalReview),
            citations.BookUrl(book.Id),
            citations.ForBook(info));
    }

    [McpServerTool(Name = "list_notes", Title = "A book's notes", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Every highlight, note and passage the user kept from one book, in page order where Nostos knows the page, each with a citation.")]
    public async Task<McpNoteList> ListNotesAsync(
        [Description("The book's id.")] Guid bookId,
        [Description("Skip this many notes, for the next page.")] int offset = 0,
        [Description("Notes per page, 1 to 100. Defaults to 50.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var notes = await RunAsync<List<NoteDto>>("notes_list_for_book", new { bookId }, cancellationToken);
        var citations = Citations;
        var book = await citations.BookAsync(bookId, notes.FirstOrDefault()?.BookTitle, cancellationToken)
            ?? throw new McpException("No book in the library has that id.");

        var ordered = notes
            .OrderBy(n => McpCitations.NoteLocator(n.SourceAnchorKind, n.SourceAnchorValue, n.AnchorVerified).Page ?? int.MaxValue)
            .ThenBy(n => n.CreatedAt)
            .ToList();
        var take = Math.Clamp(limit, 1, MaxNotesPage);
        var skip = Math.Max(0, offset);
        var page = ordered.Skip(skip).Take(take).Select(n => new McpNote(
            McpEvidenceId.ForNote(n.Id),
            n.Kind,
            Blank(n.SelectedText),
            Blank(n.Content),
            Blank(n.PassageTitle),
            n.CreatedAt,
            citations.ForNote(n.Id, book, n.Content, n.SelectedText, n.SourceAnchorKind, n.SourceAnchorValue, n.AnchorVerified))).ToList();

        return new McpNoteList(book.Title, page, ordered.Count, skip + page.Count < ordered.Count ? skip + page.Count : null);
    }

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

        var topic = await RunAsync<JsonElement>(
            "topics_get",
            new Dictionary<string, object?> { ["topicId"] = id, ["limit"] = limit is null ? null : Math.Clamp(limit.Value, 1, 100) },
            cancellationToken);
        var noteIds = topic.TryGetProperty("notes", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(n => n.GetProperty("noteId").GetGuid()).ToList()
            : [];
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
                    Blank(n.SelectedText),
                    Blank(n.Content),
                    null,
                    n.CreatedAt,
                    citations.ForNote(n.Id, book, n.Content, n.SelectedText, n.SourceAnchorKind, n.SourceAnchorValue, n.AnchorVerified)));
            }
        }

        return new McpTopicDetail(
            McpEvidenceId.ForTopic(id),
            topic.TryGetProperty("name", out var name) ? name.GetString() ?? "Topic" : "Topic",
            topic.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? Blank(d.GetString()) : null,
            topic.TryGetProperty("noteCount", out var c) && c.TryGetInt32(out var count) ? count : notes.Count,
            citations.TopicUrl(id),
            notes);
    }

    [McpServerTool(Name = "list_drafts", Title = "Studio drafts", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The user's documents and folders in the Writing Studio, with their paths. Finds them by name or folder, not by what they say.")]
    public async Task<JsonElement> ListDraftsAsync(
        [Description("Words in a document's name or folder path.")] string? query = null,
        [Description("`Document` or `Folder`. Leave out for both.")] string? type = null,
        [Description("How many, 1 to 200. Defaults to 50.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        AbsoluteLinks(await RunAsync<JsonElement>(
            "writings_list",
            new Dictionary<string, object?> { ["query"] = query, ["type"] = type, ["limit"] = limit },
            cancellationToken));

    [McpServerTool(Name = "read_draft", Title = "Read a draft", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("One Studio document as Markdown. Its `nostos:cite/` links are the Studio's live citations: keep them when you quote from the draft.")]
    public async Task<JsonElement> ReadDraftAsync(
        [Description("The document's id, from `list_drafts`.")] Guid writingId,
        CancellationToken cancellationToken = default) =>
        AbsoluteLinks(await RunAsync<JsonElement>("writings_read", new { writingId }, cancellationToken));

    [McpServerTool(Name = "reading_list", Title = "To read", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The books the user wants to read but does not have yet: Next up first, then the rest.")]
    public async Task<JsonElement> ReadingListAsync(
        [Description("`all`, `next` (Next up only) or `someday`. Defaults to all.")] string? shelf = null,
        [Description("How many, at most 200. Defaults to 50.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        AbsoluteLinks(await RunAsync<JsonElement>(
            "reading_list_list",
            new Dictionary<string, object?> { ["shelf"] = shelf, ["limit"] = limit },
            cancellationToken));

    // ------------------------------------------------------------------
    // Write
    // ------------------------------------------------------------------

    [McpServerTool(Name = "save_note", Title = "Keep a note on a book", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Keeps a thought or a quote on one book, the way the user would in Nostos. Use it only when the user asks to keep something. Pass their own words as `thought`. For a quote, pass a `quoteRef` from `find_quote` (Nostos then stores the book's exact words) or the exact `quote`. Saving the same thing twice keeps one note.")]
    public async Task<McpSavedNote> SaveNoteAsync(
        [Description("The book the note belongs to (id from `list_books`).")] Guid bookId,
        [Description("The user's own words, as they gave them.")] string? thought = null,
        [Description("Words quoted from the book, exactly as the book has them.")] string? quote = null,
        [Description("A `quoteRef` from `find_quote`, instead of `quote`.")] string? quoteRef = null,
        [Description("Topic names to link the note to, at most five. Existing names are reused.")] string[]? topics = null,
        [Description("True when the thought is about the whole book rather than one place in it.")] bool wholeBook = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(thought) && string.IsNullOrWhiteSpace(quote) && string.IsNullOrWhiteSpace(quoteRef))
            throw new McpException("Give a `thought`, a `quote` or a `quoteRef`.");
        if (!string.IsNullOrWhiteSpace(quote) && !string.IsNullOrWhiteSpace(quoteRef))
            throw new McpException("Give a `quote` or a `quoteRef`, not both.");

        var topicNames = topics?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        var args = new Dictionary<string, object?>
        {
            ["bookId"] = bookId,
            ["content"] = thought?.Trim(),
            ["selectedText"] = string.IsNullOrWhiteSpace(quote) ? null : quote.Trim(),
            ["quoteRef"] = string.IsNullOrWhiteSpace(quoteRef) ? null : quoteRef.Trim(),
            ["topics"] = topicNames is { Length: > 0 } ? topicNames : null,
            ["wholeBook"] = wholeBook,
            ["captureSource"] = NostosMcp.CaptureSource,
        };

        // A client may resend a call it never saw an answer to. The same note
        // on the same book is one note: the key is the request itself.
        var key = IdempotencyKey(bookId, thought, quote, quoteRef, topicNames);
        var saved = await RunAsync<NoteCommandResult<NoteDto>>("notes_capture", args, cancellationToken, new AssistantToolContext(NostosMcp.ClientId, key));
        var note = saved.Value ?? throw new McpException("Nostos did not save the note.");
        var citations = Citations;
        var book = await citations.BookAsync(note.BookId, note.BookTitle, cancellationToken)
            ?? new McpBookInfo(note.BookId, note.BookTitle ?? "Book", note.BookAuthor, null);
        return new McpSavedNote(
            McpEvidenceId.ForNote(note.Id),
            book.Title,
            citations.NoteUrl(note.Id),
            citations.ForNote(note.Id, book, note.Content, note.SelectedText, note.SourceAnchorKind, note.SourceAnchorValue, note.AnchorVerified));
    }

    [McpServerTool(Name = "create_draft", Title = "Write a new Studio draft", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Makes a new document in the Writing Studio from Markdown. It never changes an existing document. Put quotations in as their `citation.markdown` links so the Studio shows them as live citations. Sending the same draft again keeps one document.")]
    public async Task<JsonElement> CreateDraftAsync(
        [Description("The document's name.")] string name,
        [Description("The document in Markdown.")] string? content = null,
        [Description("A folder to put it in (a folder id from `list_drafts`). Leave out for the top level.")] Guid? folderId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new McpException("'name' is required.");

        // Writings keep no receipts. A client that resends a call it never saw
        // answered gets the document it already made: same name, same folder,
        // same words, changed in the last ten minutes.
        var same = await RunAsync<JsonElement>(
            "writings_list",
            new Dictionary<string, object?> { ["query"] = name.Trim(), ["type"] = "Document", ["limit"] = 20 },
            cancellationToken);
        if (same.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var parent = item.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : (Guid?)null;
                if (item.GetProperty("name").GetString() != name.Trim()
                    || parent != folderId
                    || !item.TryGetProperty("updatedAt", out var updated)
                    || updated.GetDateTime().ToUniversalTime() < DateTime.UtcNow.AddMinutes(-10))
                {
                    continue;
                }

                var existing = await RunAsync<JsonElement>("writings_read", new { writingId = item.GetProperty("id").GetGuid() }, cancellationToken);
                if (existing.GetProperty("content").GetString()?.Trim() == (content ?? string.Empty).Trim())
                    return AbsoluteLinks(existing, duplicate: true);
            }
        }

        return AbsoluteLinks(await RunAsync<JsonElement>(
            "writings_create",
            new Dictionary<string, object?> { ["name"] = name.Trim(), ["content"] = content, ["folderId"] = folderId },
            cancellationToken));
    }

    [McpServerTool(Name = "add_to_reading_list", Title = "Put a book on To read", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Puts a book the user wants to read on their To read list. A book already there is not added twice. Check `list_books` first if they may own it.")]
    public async Task<JsonElement> AddToReadingListAsync(
        [Description("The book's title.")] string title,
        [Description("The author.")] string? author = null,
        [Description("The ISBN, if known.")] string? isbn = null,
        [Description("The year or date it was published.")] string? publishedDate = null,
        [Description("Why the user wants to read it, in their words.")] string? why = null,
        [Description("Who recommended it.")] string? recommendedBy = null,
        [Description("True to put it on Next up.")] bool readNext = false,
        CancellationToken cancellationToken = default) =>
        AbsoluteLinks(await RunAsync<JsonElement>(
            "reading_list_add",
            new Dictionary<string, object?>
            {
                ["title"] = title,
                ["author"] = author,
                ["isbn"] = isbn,
                ["publishedDate"] = publishedDate,
                ["why"] = why,
                ["recommendedBy"] = recommendedBy,
                ["readNext"] = readNext,
            },
            cancellationToken));

    // ------------------------------------------------------------------
    // Plumbing
    // ------------------------------------------------------------------

    private IServiceProvider Services =>
        http.HttpContext?.RequestServices
        ?? throw new McpException("Nostos can only answer over HTTP.");

    private McpCitations Citations => Services.GetRequiredService<McpCitations>();

    private async Task<T> RunAsync<T>(string capability, object args, CancellationToken ct, AssistantToolContext? context = null)
    {
        var registry = Services.GetRequiredService<AssistantCapabilityRegistry>();
        var element = JsonSerializer.SerializeToElement(args, NostosMcp.Json);
        var result = await registry.InvokeAsync(AssistantSurface.Mcp, capability, element, context ?? new AssistantToolContext(), ct);
        if (!result.Success)
            throw new McpException(McpToolNames.Rename(result.ErrorMessage ?? result.ErrorCode ?? "Nostos could not do that."));

        if (typeof(T) == typeof(JsonElement))
            return (T)(object)(result.Data ?? JsonSerializer.SerializeToElement(new { }));

        if (result.Data is not { } data)
            throw new McpException("Nostos returned nothing.");

        return data.Deserialize<T>(NostosMcp.Json)
            ?? throw new McpException("Nostos returned nothing.");
    }

    /// <summary>The capability's app links (<c>/studio?…</c>) as absolute URLs into this Nostos.</summary>
    private JsonElement AbsoluteLinks(JsonElement element, bool? duplicate = null)
    {
        var node = JsonNode.Parse(element.GetRawText());
        var citations = Citations;
        Rewrite(node);
        if (duplicate is not null && node is JsonObject root)
            root["duplicate"] = duplicate.Value;
        return JsonSerializer.SerializeToElement(node, NostosMcp.Json);

        void Rewrite(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject obj:
                    foreach (var key in obj.Select(pair => pair.Key).ToList())
                    {
                        if (key == "link" && obj[key] is JsonValue value
                            && value.TryGetValue<string>(out var link) && link.StartsWith('/'))
                            obj[key] = citations.AppUrl(link);
                        else
                            Rewrite(obj[key]);
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array)
                        Rewrite(item);
                    break;
            }
        }
    }

    internal static string IdempotencyKey(Guid bookId, string? thought, string? quote, string? quoteRef, string[]? topics)
    {
        var text = string.Join('\u001f',
            bookId.ToString("D"),
            Normalize(thought),
            Normalize(quote),
            quoteRef?.Trim() ?? string.Empty,
            string.Join('\u001e', (topics ?? []).Select(t => t.ToLowerInvariant()).Order(StringComparer.Ordinal)));
        return "mcp-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..40];

        static string Normalize(string? value) =>
            string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static McpBookSummary Summary(BookDto book, McpCitations citations) =>
        new(
            book.Id,
            book.Title,
            Blank(book.Author),
            McpCitations.YearOf(book.PublishedDate),
            book.Type,
            book.ProgressPercent,
            citations.BookUrl(book.Id));

    private static string Clip(string text)
    {
        var one = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= MaxSnippet ? one : one[..MaxSnippet].TrimEnd() + "…";
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The MCP tool a client calls for each Ask Nostos capability. Capability
/// messages (refusals, find-quote guidance) are written for Ask Nostos and name
/// its capabilities; a connected app knows only the MCP tools.
/// </summary>
internal static partial class McpToolNames
{
    private static readonly Dictionary<string, string> ToolFor = new(StringComparer.Ordinal)
    {
        ["knowledge_search"] = "search",
        ["notes_search"] = "search",
        ["knowledge_read_evidence"] = "fetch",
        ["book_text_find_quote"] = "find_quote",
        ["library_overview"] = "overview",
        ["knowledge_overview"] = "overview",
        ["library_list_books"] = "list_books",
        ["library_resolve_book"] = "list_books",
        ["library_get_book"] = "get_book",
        ["notes_list_for_book"] = "list_notes",
        ["notes_capture"] = "save_note",
        ["topics_list"] = "list_topics",
        ["topics_search"] = "list_topics",
        ["topics_get"] = "get_topic",
        ["writings_list"] = "list_drafts",
        ["writings_read"] = "read_draft",
        ["writings_create"] = "create_draft",
        ["reading_list_list"] = "reading_list",
        ["reading_list_add"] = "add_to_reading_list",
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:knowledge|library|notes|topics|writings|reading_list|book_text)_[a-z_]+\b")]
    private static partial System.Text.RegularExpressions.Regex CapabilityName();

    /// <summary>Ask Nostos capability names in <paramref name="text"/> as MCP tool names.</summary>
    public static string Rename(string text) =>
        CapabilityName().Replace(text, m => ToolFor.TryGetValue(m.Value, out var tool) ? tool : m.Value);
}

/// <summary>
/// Result ids an app can pass back to <c>fetch</c>: <c>note:&lt;id&gt;</c>,
/// <c>topic:&lt;id&gt;</c>, and <c>text:&lt;book&gt;:&lt;ordinal&gt;:&lt;sha256&gt;:&lt;extractor&gt;</c>
/// for a passage of a book, pinned to the exact indexed revision.
/// </summary>
internal static class McpEvidenceId
{
    public static string ForNote(Guid id) => $"note:{id:D}";

    public static string ForTopic(Guid id) => $"topic:{id:D}";

    public static string ForBookText(KnowledgeBookEvidence passage) =>
        $"text:{passage.BookId:D}:{passage.Ordinal.ToString(CultureInfo.InvariantCulture)}:{passage.SourceSha256}:{passage.ExtractorVersion}";

    /// <summary>The <c>knowledge_read_evidence</c> arguments, or null for an id that is not one of ours.</summary>
    public static object? Parse(string? id)
    {
        var parts = (id ?? string.Empty).Trim().Split(':', 5);
        return parts switch
        {
            ["note", var note] when Guid.TryParse(note, out var noteId) => new { kind = "note", noteId },
            ["topic", var topic] when Guid.TryParse(topic, out var topicId) => new { kind = "topic", topicId },
            ["text", var book, var ordinal, var sha, var extractor]
                when Guid.TryParse(book, out var bookId)
                     && int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                     && sha.Length > 0 && extractor.Length > 0
                => new { kind = "book_text", bookId, ordinal = n, sourceSha256 = sha, extractorVersion = extractor },
            _ => null,
        };
    }
}

public sealed record McpSearchHit(string Id, string Kind, string Title, string Text, string Url, McpCitation? Citation, IReadOnlyList<string>? Topics);

public sealed record McpSearchResult(IReadOnlyList<McpSearchHit> Results, string? Note, IReadOnlyList<string>? StillIndexing);

public sealed record McpDocument(string Id, string Kind, string Title, string Text, string Url, McpCitation? Citation, IReadOnlyDictionary<string, object?> Metadata);

public sealed record McpNamedCount(string Name, int Count, string Id);

public sealed record McpBrainOverview(int Notes, int UnsortedNotes, int Topics, IReadOnlyList<McpNamedCount> TopTopics, IReadOnlyList<McpNamedCount> BooksWithMostNotes);

public sealed record McpOverview(JsonElement Library, McpBrainOverview Brain);

public sealed record McpBookSummary(Guid Id, string Title, string? Author, string? Year, string Type, int ProgressPercent, string Url);

public sealed record McpBookList(IReadOnlyList<McpBookSummary> Books, int Total, int Page, string? Note);

public sealed record McpBook(
    Guid Id,
    string Title,
    string? Subtitle,
    string? Author,
    string? Year,
    string Type,
    string? Publisher,
    string? Isbn,
    string? Description,
    int ProgressPercent,
    int? Rating,
    string? Review,
    string Url,
    McpCitation Citation);

public sealed record McpNote(string Id, string Kind, string? Quote, string? Note, string? PassageTitle, DateTime CreatedAt, McpCitation Citation);

public sealed record McpNoteList(string Book, IReadOnlyList<McpNote> Notes, int Total, int? NextOffset);

public sealed record McpTopic(string Id, string Name, int Notes, string Url);

public sealed record McpTopicList(IReadOnlyList<McpTopic> Topics);

public sealed record McpTopicDetail(string Id, string Name, string? Description, int Notes, string Url, IReadOnlyList<McpNote> LinkedNotes);

public sealed record McpSavedNote(string Id, string Book, string Url, McpCitation Citation);
