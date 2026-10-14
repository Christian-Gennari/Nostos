using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Integrations.Assistant;

public static partial class AssistantCapabilities
{
    private static IReadOnlyList<AssistantCapability> BuildCollectionAndCaptureCapabilities(INoteService notes, ILibraryService library) =>
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
            "Saves one of the user's own thoughts, observations or quotes as a note against the book that is open. Use it for genuine thoughts the user gives you to keep even when they do not literally say 'save' or 'note'. Never use it for corrections, source/book clarifications, answers to a previous question, or explicit instructions not to save. The book is supplied by the app; never pass one.",
            """
            {
              "type": "object",
              "properties": {
                "content": { "type": "string", "description": "The user's own words for the note, exactly as they said or wrote them, with only any instruction removed. Never paraphrase, shorten, translate, correct or add to them." },
                "selectedText": { "type": "string", "description": "A passage quoted from the book itself. When the reader has a passage selected it is already in the current context — capture it here rather than asking for it. It is stored as the quotation exactly as given." },
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
