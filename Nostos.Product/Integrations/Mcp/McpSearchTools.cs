using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// Finding things: the library search, reading one result in full, and finding
/// a passage in a book by its first and last words.
/// </summary>
[McpServerToolType]
public sealed class McpSearchTools(IHttpContextAccessor http) : NostosMcpTool(http)
{
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
                    McpText.Clip(note.Snippet),
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
                    McpText.Clip(topic.MatchSnippet ?? string.Join(" · ", topic.SupportingNotes.Select(n => n.Snippet))),
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
                    McpText.Clip(passage.Text),
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
                new McpNoteDocument(
                    note.BookId,
                    string.IsNullOrWhiteSpace(note.SelectedText) ? null : note.SelectedText,
                    string.IsNullOrWhiteSpace(note.Content) ? null : note.Content,
                    note.TopicNames,
                    note.CreatedAt));
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
                new McpTopicDocument(topic.UsageCount));
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
                new McpBookTextDocument(passage.BookId, passage.HeadingPath));
        }

        throw new McpException("Nothing in the library has that id any more.");
    }

    [McpServerTool(Name = "find_quote", Title = "Find a passage in a book", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Finds a passage in one book by its first and last words, and returns the book's exact words. For a paper book it looks in the digital editions of the same work. Use it before quoting a passage the user names, or before `save_note` or `save_passage` with a `quoteRef`. When it asks the user to choose, call it again with `choice`.")]
    public async Task<JsonElement> FindQuoteAsync(
        [Description("The book (id from `list_books`).")] Guid bookId,
        [Description("The words the passage starts with.")] string startsWith,
        [Description("The words the passage ends with.")] string endsWith,
        [Description("The page, if the user named one. Only narrows the search.")] int? page = null,
        [Description("The chapter, if the user named one. Only narrows the search.")] string? chapter = null,
        [Description("After the tool asked to choose: the number picked (1 confirms a single passage). Send the same words again.")] int? choice = null,
        CancellationToken cancellationToken = default)
    {
        var found = await RunAsync<McpQuoteFindResult>(
            "book_text_find_quote",
            new Dictionary<string, object?>
            {
                ["bookId"] = bookId,
                ["startsWith"] = startsWith,
                ["endsWith"] = endsWith,
                ["page"] = page,
                ["chapter"] = chapter,
                ["choice"] = choice,
            },
            cancellationToken);

        // Every passage it shows is citable before anything is saved: the book
        // the user named (a paper book is cited as itself), at its chapter.
        if (found.Candidates.Count > 0 && await Citations.BookAsync(bookId, null, cancellationToken) is { } book)
        {
            found = found with
            {
                Candidates = found.Candidates
                    .Select(candidate => candidate with
                    {
                        Citation = Citations.ForQuote(book, candidate.Chapter, candidate.Text),
                    })
                    .ToList(),
            };
        }

        // The guidance is written for Ask Nostos; name the tools this client has.
        return McpJson.Element(found with { Message = McpToolNames.Rename(found.Message) });
    }
}
