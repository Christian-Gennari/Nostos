using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// What the user kept from a book: the notes of one book, and keeping a new
/// note, highlight or passage.
/// </summary>
[McpServerToolType]
public sealed class McpNoteTools(IHttpContextAccessor http) : NostosMcpTool(http)
{
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
            McpText.Blank(n.SelectedText),
            McpText.Blank(n.Content),
            McpText.Blank(n.PassageTitle),
            n.CreatedAt,
            citations.ForNote(n.Id, book, n.Content, n.SelectedText, n.SourceAnchorKind, n.SourceAnchorValue, n.AnchorVerified))).ToList();

        return new McpNoteList(book.Title, page, ordered.Count, skip + page.Count < ordered.Count ? skip + page.Count : null);
    }

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
        var key = McpToolKeys.IdempotencyKey(bookId, thought, quote, quoteRef, topicNames);
        var saved = await RunAsync<NoteCommandResult<NoteDto>>("notes_capture", args, cancellationToken, new AssistantToolContext(NostosMcp.ClientId, key));
        return await Saved(saved.Value ?? throw new McpException("Nostos did not save the note."), cancellationToken);
    }

    [McpServerTool(Name = "save_passage", Title = "Keep a passage of a book", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Keeps a passage: a longer stretch of a book with a title (such as a whole argument or a thought experiment), read in Nostos's passage viewer rather than shown as a highlight. Use it only when the user asks to keep a passage. Find the passage with `find_quote` first and pass its `quoteRef`; Nostos stores the book's exact words and where the passage starts and ends. Works for EPUB and PDF books; for a paper book, use `save_note` with the `quoteRef` instead. Saving the same passage twice keeps one.")]
    public async Task<McpSavedNote> SavePassageAsync(
        [Description("The book the passage belongs to (id from `list_books`).")] Guid bookId,
        [Description("A `quoteRef` from `find_quote` for the whole passage.")] string quoteRef,
        [Description("The passage's title, as the user named it (1 to 300 characters).")] string title,
        [Description("The user's own note on the passage, as they gave it.")] string? thought = null,
        [Description("Topic names to link the passage to, at most five. Existing names are reused.")] string[]? topics = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(quoteRef))
            throw new McpException("Give the `quoteRef` `find_quote` returned for the passage.");
        if (string.IsNullOrWhiteSpace(title))
            throw new McpException("Give the passage a `title`.");

        var topicNames = topics?.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        var args = new Dictionary<string, object?>
        {
            ["bookId"] = bookId,
            ["quoteRef"] = quoteRef.Trim(),
            ["title"] = title.Trim(),
            ["content"] = thought?.Trim(),
            ["topics"] = topicNames is { Length: > 0 } ? topicNames : null,
            ["captureSource"] = NostosMcp.CaptureSource,
        };

        // The same passage under the same title on the same book is one passage.
        var key = McpToolKeys.IdempotencyKey(bookId, thought, "passage\u001f" + title.Trim(), quoteRef, topicNames);
        var saved = await RunAsync<NoteCommandResult<NoteDto>>("notes_save_passage", args, cancellationToken, new AssistantToolContext(NostosMcp.ClientId, key));
        return await Saved(saved.Value ?? throw new McpException("Nostos did not save the passage."), cancellationToken);
    }

    /// <summary>A saved note or passage, cited where it now is in its book.</summary>
    private async Task<McpSavedNote> Saved(NoteDto note, CancellationToken ct)
    {
        var citations = Citations;
        var book = await citations.BookAsync(note.BookId, note.BookTitle, ct)
            ?? new McpBookInfo(note.BookId, note.BookTitle ?? "Book", note.BookAuthor, null);
        return new McpSavedNote(
            McpEvidenceId.ForNote(note.Id),
            book.Title,
            citations.NoteUrl(note.Id),
            citations.ForNote(note.Id, book, note.Content, note.SelectedText, note.SourceAnchorKind, note.SourceAnchorValue, note.AnchorVerified));
    }
}
