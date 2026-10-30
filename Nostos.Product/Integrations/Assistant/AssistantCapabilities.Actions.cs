using Nostos.Product.BookText;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Integrations.Assistant;

public static partial class AssistantCapabilities
{
    private static IReadOnlyList<AssistantCapability> BuildCollectionAndCaptureCapabilities(
        INoteService notes,
        ILibraryService library,
        IBookTextQuoteFinder? quotes = null) =>
    [
        new AssistantCapability(
            "library_list_collections",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.Organization,
            "Lists all collections as a flat (id, name, parentId) list.",
            """
            {
              "type": "object",
              "properties": {},
              "required": [],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var result = await library.ListCollectionsAsync(ct);
                return LibraryResult(result);
            }),

        new AssistantCapability(
            "library_get_collection",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.Organization,
            "Gets one collection and its membership.",
            """
            {
              "type": "object",
              "properties": {
                "collectionId": { "type": "string", "format": "uuid", "description": "The id of the collection to read. Required." }
              },
              "required": ["collectionId"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "collectionId") is not { } collectionId)
                {
                    return Invalid("'collectionId' is required.");
                }

                var result = await library.GetCollectionAsync(collectionId, ct);
                return LibraryResult(result);
            }),

        // ------------------------------------------------------------------
        // Capture: low risk, runs immediately. The context's client/key pair
        // flows straight into CanonicalCapture, so a retry is exactly-once.
        // ------------------------------------------------------------------

        new AssistantCapability(
            "notes_capture",
            AssistantTrustClass.Capture,
            AssistantCapabilityCategory.Capture,
            "Saves one of the user's own thoughts, observations or quotes as a note against the book that is open. Use it for genuine thoughts the user gives you to keep even when they do not literally say 'save' or 'note'. Never use it for corrections, source/book clarifications, answers to a previous question, explicit instructions not to save, or a request to rephrase a note already saved in this conversation (that is notes_rewrite_capture). The book is supplied by the app; never pass one.",
            """
            {
              "type": "object",
              "properties": {
                "content": { "type": "string", "description": "The user's own words for the note, exactly as they said or wrote them, with every instruction to you removed (\"can you add a note on…\", \"rephrase this and post it for me\"). Never paraphrase, shorten, translate, correct or add to them yourself: when the user asks for a rewrite, set rewrite and Nostos does it." },
                "rewrite": { "type": "boolean", "description": "True only when the user asked you to rephrase, clean up or make the thought more coherent. Nostos then saves a tidied version and keeps their exact words as the original. Never set it on your own judgement." },
                "topics": { "type": "array", "items": { "type": "string" }, "description": "With rewrite, or when the user asks for topics: up to five topic names the thought is plainly about (a character, a theme, an idea it names). Prefer the exact names of topics that already exist. Ignored otherwise." },
                "wholeBook": { "type": "boolean", "description": "True when the thought is about the book as a whole (a character across the book, a theme, an overall reaction) rather than the moment the user is at. The note is then saved without a page or timestamp, and the user is not asked for one." },
                "selectedText": { "type": "string", "description": "A passage quoted from the book itself. When the reader has a passage selected it is already in the current context — capture it here rather than asking for it. It is stored as the quotation exactly as given." },
                "quoteRef": { "type": "string", "description": "The quoteRef book_text_find_quote returned for the passage the user named by its first and last words. Nostos stores that passage exactly as the book has it; do not pass selectedText with it." },
                "captureSource": { "type": "string", "description": "Where the words came from. Defaults to 'text'." }
              },
              "required": [],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "bookId") is not { } bookId)
                {
                    return Invalid("'bookId' is required.");
                }

                var content = Str(args, "content");
                var selectedText = Str(args, "selectedText");
                if (Str(args, "quoteRef") is { Length: > 0 } quoteRef)
                {
                    // The words are the book's, never the model's copy of them.
                    var quote = quotes is null ? null : await quotes.ResolveAsync(quoteRef, ct);
                    if (quote is null)
                        return Invalid("That quoteRef no longer resolves. Look the passage up again with book_text_find_quote.");
                    selectedText = quote.Text;
                }

                if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(selectedText))
                {
                    return Invalid("'content' or 'selectedText' is required.");
                }

                var request = new CaptureNoteRequest(
                    BookId: bookId,
                    Content: content ?? string.Empty,
                    CfiRange: Str(args, "cfiRange"),
                    SelectedText: selectedText,
                    RawContent: Str(args, "rawContent"),
                    CaptureSource: Str(args, "captureSource") ?? "text",
                    ProcessingMode: Str(args, "processingMode") ?? "verbatim",
                    SourceAnchorKind: Str(args, "sourceAnchorKind") ?? "unknown",
                    SourceAnchorValue: Str(args, "sourceAnchorValue"),
                    AnchorVerified: Bool(args, "anchorVerified") ?? false,
                    ClientId: context.ClientId,
                    IdempotencyKey: context.IdempotencyKey);

                var result = await notes.CaptureAsync(request, ct);
                if (result.Success && StrList(args, "topics") is { Count: > 0 } topics)
                {
                    // After the rewrite, never before it: the references are
                    // appended to the stored text, so the processor cannot drop them.
                    await notes.LinkTopicsAsync(topics, [result.Value!.Id], ct);
                }

                return NoteResult(result);
            }),

        new AssistantCapability(
            "notes_rewrite_capture",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.Capture,
            "Rewrites a note saved earlier in this conversation into a tidier, more coherent version when the user asks for that afterwards (\"clean that up\", \"rephrase it\"). Their exact words stay with the note as the original. Only for a note this conversation captured.",
            """
            {
              "type": "object",
              "properties": {
                "noteId": { "type": "string", "format": "uuid", "description": "The id of the note this conversation captured. Required." },
                "topics": { "type": "array", "items": { "type": "string" }, "description": "Up to five topic names the thought is plainly about. Prefer the exact names of topics that already exist." }
              },
              "required": ["noteId"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "noteId") is not { } noteId)
                {
                    return Invalid("'noteId' is required.");
                }

                var result = await notes.RewriteCaptureAsync(noteId, context.ClientId ?? string.Empty, ct);
                if (result.Success && StrList(args, "topics") is { Count: > 0 } topics)
                {
                    await notes.LinkTopicsAsync(topics, [noteId], ct);
                }

                return NoteResult(result);
            }),

        // ------------------------------------------------------------------
        // Act: a normal user-requested write that runs inside the tool loop.
        // Library organisation is staged instead (AssistantCapabilities.Changes).
        // ------------------------------------------------------------------

        new AssistantCapability(
            "notes_link_existing_topic",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.Organization,
            "Links a note to an existing topic. Never creates a topic.",
            """
            {
              "type": "object",
              "properties": {
                "noteId": { "type": "string", "format": "uuid", "description": "The id of the note to link. Required." },
                "topicId": { "type": "string", "format": "uuid", "description": "The id of an existing topic to link the note to. Never invent one; find it with topics_list or topics_search. Required." }
              },
              "required": ["noteId", "topicId"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "noteId") is not { } noteId || Id(args, "topicId") is not { } topicId)
                {
                    return Invalid("'noteId' and 'topicId' are required.");
                }

                var result = await notes.LinkToExistingTopicAsync(noteId, topicId, ct);
                return NoteResult(result);
            }),
    ];
}
