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
                "content": { "type": "string", "description": "The user's own words for the note, exactly as they said or wrote them, with only any instruction removed. Never paraphrase, shorten, translate, correct or add to them. A separate setting decides how the words are rendered." },
                "selectedText": { "type": "string", "description": "A passage quoted from the book itself. When the reader has a passage selected it is already in the current context — capture it here rather than asking for it. It is stored as the quotation and is never rewritten by any processing setting." },
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
        // Act: normal user-requested writes. They run inside the tool loop so
        // subsequent calls can depend on their real results.
        // ------------------------------------------------------------------

        new AssistantCapability(
            "notes_link_existing_concept",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.Organization,
            "Links a note to an existing concept. Never creates a concept.",
            """
            {
              "type": "object",
              "properties": {
                "noteId": { "type": "string", "format": "uuid", "description": "The id of the note to link. Required." },
                "conceptId": { "type": "string", "format": "uuid", "description": "The id of an existing concept to link the note to. Never invent one; find it with concepts_list or concepts_search. Required." }
              },
              "required": ["noteId", "conceptId"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "noteId") is not { } noteId || Id(args, "conceptId") is not { } conceptId)
                {
                    return Invalid("'noteId' and 'conceptId' are required.");
                }

                var result = await notes.LinkToExistingConceptAsync(noteId, conceptId, ct);
                return NoteResult(result);
            }),

        new AssistantCapability(
            "library_create_collection",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.Organization,
            "Creates a collection (or returns the existing sibling with the same name).",
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "The new collection's name. Required." },
                "parentId": { "type": "string", "format": "uuid", "description": "The id of the parent collection. Omit it to create the collection at the top level." }
              },
              "required": ["name"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var name = Str(args, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    return Invalid("'name' is required.");
                }

                var result = await library.CreateCollectionAsync(
                    new LibraryCreateCollectionRequest(
                        context.ClientId ?? string.Empty,
                        context.IdempotencyKey ?? string.Empty,
                        name,
                        Id(args, "parentId")),
                    ct);

                return LibraryResult(result);
            }),

        new AssistantCapability(
            "library_rename_collection",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.Organization,
            "Renames an existing collection.",
            """
            {
              "type": "object",
              "properties": {
                "collectionId": { "type": "string", "format": "uuid", "description": "The id of the collection to rename. Required." },
                "name": { "type": "string", "description": "The collection's new name. Required." }
              },
              "required": ["collectionId", "name"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "collectionId") is not { } collectionId || string.IsNullOrWhiteSpace(Str(args, "name")))
                {
                    return Invalid("'collectionId' and 'name' are required.");
                }

                var result = await library.RenameCollectionAsync(
                    new LibraryRenameCollectionRequest(
                        context.ClientId ?? string.Empty,
                        context.IdempotencyKey ?? string.Empty,
                        collectionId,
                        Str(args, "name")!),
                    ct);

                return LibraryResult(result);
            }),

        new AssistantCapability(
            "library_move_collection",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.Organization,
            "Moves a collection under a new parent (null moves it to the top level).",
            """
            {
              "type": "object",
              "properties": {
                "collectionId": { "type": "string", "format": "uuid", "description": "The id of the collection to move. Required." },
                "newParentId": { "type": "string", "format": "uuid", "description": "The id of the new parent collection. Omit it to move the collection to the top level." }
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

                var result = await library.MoveCollectionAsync(
                    new LibraryMoveCollectionRequest(
                        context.ClientId ?? string.Empty,
                        context.IdempotencyKey ?? string.Empty,
                        collectionId,
                        Id(args, "newParentId")),
                    ct);

                return LibraryResult(result);
            }),

        new AssistantCapability(
            "library_delete_empty_collection",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.Organization,
            "Deletes an empty collection immediately. Refuses when the collection still contains books, so cleanup after a reorganization does not need a second approval while membership-destructive deletion remains guarded.",
            """
            {
              "type": "object",
              "properties": {
                "collectionId": { "type": "string", "format": "uuid", "description": "The empty collection to delete. Required." }
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

                var members = await library.ListBooksAsync(
                    filter: BookFilter.All,
                    sort: BookSort.Title,
                    search: null,
                    page: 1,
                    pageSize: 1,
                    collectionId: collectionId,
                    ct: ct);

                if (members.Data is LibraryErrorDto readError)
                {
                    return AssistantToolResult.Fail(readError.Code, members.Reply, Element(members));
                }

                if (members.Data is PaginatedResponse<BookDto> page && page.TotalCount > 0)
                {
                    return AssistantToolResult.Fail(
                        "collection_not_empty_requires_approval",
                        "This collection still contains books. Use library_delete_collection only after the user explicitly approves unlinking those memberships.",
                        Element(new { collectionId, bookCount = page.TotalCount }));
                }

                var result = await library.DeleteCollectionAsync(
                    new LibraryDeleteCollectionRequest(
                        context.ClientId ?? string.Empty,
                        context.IdempotencyKey ?? string.Empty,
                        collectionId,
                        Confirm: true),
                    ct);

                return LibraryResult(result);
            }),

        // ------------------------------------------------------------------
        // PlanAndAct: deletion that can unlink books remains approval-gated.
        // ------------------------------------------------------------------

        new AssistantCapability(
            "library_delete_collection",
            AssistantTrustClass.PlanAndAct,
            AssistantCapabilityCategory.Organization,
            "Deletes a collection after explicit approval. Books are unlinked, never deleted; child collections must be handled first.",
            """
            {
              "type": "object",
              "properties": {
                "collectionId": { "type": "string", "format": "uuid", "description": "The collection to delete. Required." }
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

                var result = await library.DeleteCollectionAsync(
                    new LibraryDeleteCollectionRequest(
                        context.ClientId ?? string.Empty,
                        context.IdempotencyKey ?? string.Empty,
                        collectionId,
                        Confirm: true),
                    ct);

                return LibraryResult(result);
            }),
    ];
}
