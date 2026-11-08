using Nostos.Backend.Services.Notes;
using Nostos.Product.BookText;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

public static partial class AssistantCapabilities
{
    /// <summary>
    /// Keeps a passage (issue #1014) the user names by its first and last
    /// words, without the book open: the words and both ends come from the
    /// book's own indexed text through a <c>quoteRef</c>, never from the model.
    /// MCP only: in Ask Nostos the reader marks a passage's ends itself.
    /// </summary>
    private static AssistantCapability BuildSavePassageCapability(
        INoteService notes,
        IBookTextQuoteFinder quotes,
        IBookPassageAnchors anchors) =>
        new(
            "notes_save_passage",
            AssistantTrustClass.Capture,
            AssistantCapabilityCategory.Capture,
            "Keeps a passage of a book: a longer stretch of its text with a title, read in Nostos's passage viewer rather than as a highlight. Find the passage with book_text_find_quote first and pass its quoteRef; Nostos stores the book's exact words and both ends. Use it only when the user asks to keep a passage.",
            """
            {
              "type": "object",
              "properties": {
                "bookId": { "type": "string", "format": "uuid", "description": "The book the passage belongs to, from a Nostos tool result. Required." },
                "quoteRef": { "type": "string", "description": "The quoteRef book_text_find_quote returned for the passage. Required." },
                "title": { "type": "string", "description": "The passage's title, 1 to 300 characters, as the user named it. Required." },
                "content": { "type": "string", "description": "The user's own note on the passage, exactly as they gave it. Optional." },
                "topics": { "type": "array", "items": { "type": "string" }, "description": "Up to five topic names the passage is plainly about. Prefer the exact names of topics that already exist." }
              },
              "required": ["bookId", "quoteRef", "title"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "bookId") is not { } bookId)
                    return Invalid("'bookId' is required.");
                if (Str(args, "quoteRef")?.Trim() is not { Length: > 0 } quoteRef)
                    return Invalid("'quoteRef' is required: find the passage with book_text_find_quote first.");
                if (Str(args, "title")?.Trim() is not { Length: > 0 } title)
                    return Invalid("'title' is required: a passage is kept under a title.");

                var quote = await quotes.ResolveAsync(quoteRef, ct);
                if (quote is null)
                    return Invalid("That quoteRef no longer resolves. Look the passage up again with book_text_find_quote.");

                // A passage is read where it stands; a paper book's quote is
                // found in another edition, which is not where it stands.
                if (quote.SourceBookId != bookId)
                    return Invalid("That passage was found in another edition of the book, so it cannot be kept as a passage of this one. Keep it as a passage of the edition it was found in (the quote's source book), or as a quote with notes_capture.");

                var ends = await anchors.AnchorAsync(quote, ct);
                if (ends is null)
                    return Invalid("Nostos found the words but could not place both ends of the passage in the book, so nothing was saved. Keep it as a quote with notes_capture instead.");

                var dto = new CreateNoteDto(
                    Content: Str(args, "content")?.Trim() ?? string.Empty,
                    SelectedText: quote.Text,
                    CaptureSource: Str(args, "captureSource") ?? "text",
                    SourceAnchorKind: ends.AnchorKind,
                    SourceAnchorValue: ends.Start,
                    AnchorVerified: true,
                    Kind: NoteKinds.Passage,
                    PassageTitle: title,
                    PassageEnd: ends.End,
                    Topics: StrList(args, "topics") is { Count: > 0 } topics ? topics : null);

                return NoteResult(await notes.CreateAsync(bookId, dto, context.ClientId, context.IdempotencyKey, ct));
            })
        {
            Surfaces = AssistantSurface.Mcp,
        };
}
