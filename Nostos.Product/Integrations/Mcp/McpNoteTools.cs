using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Mcp;

/// <summary>
/// What the user kept from a book: the notes of one book, keeping a new note,
/// highlight or passage, and changing or removing one they already kept.
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

    // ------------------------------------------------------------------
    // Changing and removing what the user kept (issue #1785)
    // ------------------------------------------------------------------

    [McpServerTool(Name = "update_note", Title = "Change a note or highlight", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Changes what the user kept from a book: a note's or highlight's own words and/or its topics. Use it only when the user asks to change one. Pass only what changes: the quote and where it sits in the book stay as they are. The result carries `undo`, the arguments that put the previous words and topics back. A passage has its own tool, `update_passage`.")]
    public Task<McpUpdatedNote> UpdateNoteAsync(
        [Description("The note's id (`note:<id>` or the bare id) from `list_notes`, `search` or `save_note`.")] string noteId,
        [Description("The user's own words to replace the note's text with. Leave out to keep them.")] string? thought = null,
        [Description("The topic names the note keeps, replacing the ones it has. Existing names are reused; an empty list removes them all. Leave out to keep them.")] string[]? topics = null,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(noteId, NoteKinds.Note, "update_note", thought, topics, cancellationToken);

    [McpServerTool(Name = "update_passage", Title = "Change a passage", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Changes a passage's own words (the user's note on it) and/or its topics. Use it only when the user asks to change one. Pass only what changes: the passage's title, quoted words and place in the book stay as they are. The result carries `undo`, the arguments that put the previous words and topics back. A note or highlight has its own tool, `update_note`.")]
    public Task<McpUpdatedNote> UpdatePassageAsync(
        [Description("The passage's id (`note:<id>` or the bare id) from `list_notes`, `search` or `save_passage`.")] string passageId,
        [Description("The user's own words to replace the passage's note with. Leave out to keep them.")] string? thought = null,
        [Description("The topic names the passage keeps, replacing the ones it has. Existing names are reused; an empty list removes them all. Leave out to keep them.")] string[]? topics = null,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(passageId, NoteKinds.Passage, "update_passage", thought, topics, cancellationToken);

    [McpServerTool(Name = "delete_note", Title = "Remove a note or highlight", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Removes a note or highlight from the user's library. Use it only when the user asks, and confirm with them first: Nostos keeps no copy. The result carries what was removed (its quote, words and topics) so it can be kept again with `save_note`. A passage has its own tool, `delete_passage`.")]
    public Task<McpDeletedNote> DeleteNoteAsync(
        [Description("The note's id (`note:<id>` or the bare id) from `list_notes`, `search` or `save_note`.")] string noteId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(noteId, NoteKinds.Note, "delete_note", cancellationToken);

    [McpServerTool(Name = "delete_passage", Title = "Remove a passage", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Removes a passage from the user's library. Use it only when the user asks, and confirm with them first: Nostos keeps no copy. The result carries the passage's title, opening and the user's words so it can be found again with `find_quote` and kept with `save_passage`. A note or highlight has its own tool, `delete_note`.")]
    public Task<McpDeletedNote> DeletePassageAsync(
        [Description("The passage's id (`note:<id>` or the bare id) from `list_notes`, `search` or `save_passage`.")] string passageId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(passageId, NoteKinds.Passage, "delete_passage", cancellationToken);

    /// <summary>
    /// One edit path for a note and a passage (issue #1785): the same canonical
    /// <see cref="INoteService.UpdateAsync"/> the REST endpoint calls, so an
    /// edit made here means exactly what it means in the app.
    /// </summary>
    private async Task<McpUpdatedNote> UpdateAsync(
        string rawId,
        string kind,
        string tool,
        string? thought,
        string[]? topics,
        CancellationToken ct)
    {
        if (thought is null && topics is null)
            throw new McpException("Pass `thought`, `topics`, or both: there is nothing to change.");

        var id = ParseId(rawId, kind, tool);
        var notes = Services.GetRequiredService<INoteService>();
        var existing = await notes.GetAsync(id, ct)
            ?? throw new McpException(NotFound(kind));
        RequireKind(existing.Kind, kind, counterpart: kind == NoteKinds.Passage ? "update_note" : "update_passage");

        var previousWords = TopicReferences.Words(existing.Content);
        var previousTopics = existing.TopicNames.ToList();
        var words = thought is null ? previousWords : thought.Trim();
        var keptTopics = topics is null ? previousTopics : TopicNames(topics);

        if (words.Length == 0 && string.IsNullOrWhiteSpace(existing.SelectedText))
            throw new McpException("A note keeps some words or a quote, and this would leave neither. Pass `thought` with the user's words.");

        // Topics are sent even when only the words changed: with them the
        // service knows the list is every topic the note keeps, so one left out
        // of a new list is removed rather than kept as a hidden link.
        var result = await notes.UpdateAsync(id, new UpdateNoteDto(Content: words, Topics: keptTopics), ct);
        if (!result.Success)
            throw new McpException(result.ErrorMessage ?? "Nostos could not change that note.");

        var updated = result.Value ?? throw new McpException("Nostos did not return the changed note.");
        var newWords = TopicReferences.Words(updated.Content);
        var newTopics = TopicReferences.Names(updated.Content);
        var changed = !string.Equals(previousWords, newWords, StringComparison.Ordinal)
            || !SameTopics(previousTopics, newTopics);

        var citations = Citations;
        var book = await citations.BookAsync(updated.BookId, updated.BookTitle, ct)
            ?? new McpBookInfo(updated.BookId, updated.BookTitle ?? "Book", updated.BookAuthor, null);

        return new McpUpdatedNote(
            McpEvidenceId.ForNote(id),
            updated.Kind,
            book.Title,
            citations.NoteUrl(id),
            McpText.Blank(updated.SelectedText),
            McpText.Blank(newWords),
            newTopics,
            changed
                ? new Dictionary<string, object?>
                {
                    [kind == NoteKinds.Passage ? "passageId" : "noteId"] = McpEvidenceId.ForNote(id),
                    ["thought"] = previousWords,
                    ["topics"] = previousTopics,
                }
                : null,
            changed
                ? "Changed. `undo` puts the previous words and topics back."
                : "Nothing changed.",
            citations.ForNote(id, book, newWords, updated.SelectedText, updated.SourceAnchorKind, updated.SourceAnchorValue, updated.AnchorVerified),
            McpText.Blank(updated.PassageTitle));
    }

    /// <summary>
    /// Removing a note or a passage through the same service the app's delete
    /// button calls. What it carried is answered before it goes, so the user
    /// can keep it again.
    /// </summary>
    private async Task<McpDeletedNote> DeleteAsync(
        string rawId,
        string kind,
        string tool,
        CancellationToken ct)
    {
        var id = ParseId(rawId, kind, tool);
        var notes = Services.GetRequiredService<INoteService>();
        var existing = await notes.GetAsync(id, ct)
            ?? throw new McpException(NotFound(kind));
        RequireKind(existing.Kind, kind, counterpart: kind == NoteKinds.Passage ? "delete_note" : "delete_passage");

        var result = await notes.DeleteAsync(id, ct);
        if (!result.Success)
            throw new McpException(result.ErrorMessage ?? "Nostos could not remove that note.");

        var citations = Citations;
        var book = await citations.BookAsync(existing.BookId, existing.BookTitle, ct)
            ?? new McpBookInfo(existing.BookId, existing.BookTitle ?? "Book", existing.BookAuthor, null);

        return new McpDeletedNote(
            McpEvidenceId.ForNote(id),
            kind,
            book.Title,
            McpText.Blank(existing.SelectedText),
            McpText.Blank(TopicReferences.Words(existing.Content)),
            existing.TopicNames.ToList(),
            McpText.Blank(existing.PassageTitle),
            kind == NoteKinds.Passage
                ? "Removed the passage. Its title, opening and the user's words are in this result; `find_quote` finds the words again and `save_passage` keeps them."
                : "Removed the note. Its quote, words and topics are in this result; `save_note` keeps them again.");
    }

    /// <summary>A note id from a tool result: <c>note:&lt;id&gt;</c> or the bare id.</summary>
    private static Guid ParseId(string? value, string kind, string tool)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.StartsWith("note:", StringComparison.Ordinal))
            text = text["note:".Length..];
        if (Guid.TryParse(text, out var id))
            return id;

        throw new McpException(
            $"That is not a {(kind == NoteKinds.Passage ? "passage" : "note")} id. Use the id `list_notes`, `search` or `save_{(kind == NoteKinds.Passage ? "passage" : "note")}` returned, or pass it to `{tool}` as `note:<id>`.");
    }

    private static string NotFound(string kind) => kind == NoteKinds.Passage
        ? "No passage in this library has that id. Use `list_notes`, `search` or `save_passage` to find the one the user means."
        : "No note in this library has that id. Use `list_notes`, `search` or `save_note` to find the one the user means.";

    /// <summary>An id of one kind never edits or removes the other by mistake.</summary>
    private static void RequireKind(string? actual, string expected, string counterpart)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
            return;

        throw new McpException(expected == NoteKinds.Passage
            ? $"That id is a note or highlight, not a passage. Use `{counterpart}` instead."
            : $"That id is a passage. Use `{counterpart}` instead.");
    }

    /// <summary>The topic names as the note service takes them, in order and once.</summary>
    private static List<string> TopicNames(IReadOnlyList<string> topics) =>
        topics
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool SameTopics(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count
        && a.Select(name => name.ToLowerInvariant()).Order(StringComparer.Ordinal)
            .SequenceEqual(b.Select(name => name.ToLowerInvariant()).Order(StringComparer.Ordinal));
}
