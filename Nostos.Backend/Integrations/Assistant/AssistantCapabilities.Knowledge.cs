using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Knowledge;

namespace Nostos.Backend.Integrations.Assistant;

public static partial class AssistantCapabilities
{
    private static IReadOnlyList<AssistantCapability> BuildKnowledgeCapabilities(
        INoteService notes,
        IConceptRepository concepts,
        IKnowledgeRetrievalService knowledge) =>
    [
        new AssistantCapability(
            "knowledge_search",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Searches the user's own notes, concepts-through-linked-notes, and indexed imported PDF/EPUB text through one bounded retrieval path. Prefer this for questions that may span the user's reading and thinking. Results include canonical evidence handles that can be re-read exactly.",
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "Words or a short natural-language formulation of what to find in the user's material. Required." },
                "bookIds": {
                  "type": "array",
                  "items": { "type": "string", "format": "uuid" },
                  "description": "Explicit book scope. When present and non-empty, note/concept/book-text evidence is restricted to these books. Omit to keep current/recent conversational book scope; pass an empty array only when the user explicitly broadens to the whole library."
                },
                "collectionId": { "type": "string", "format": "uuid", "description": "Optional collection scope when bookIds are omitted." },
                "maxPerSource": { "type": "integer", "minimum": 1, "maximum": 8, "description": "Maximum evidence items per source type. Defaults to 6." }
              },
              "required": ["query"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var query = Str(args, "query");
                if (string.IsNullOrWhiteSpace(query))
                    return Invalid("'query' is required.");

                if (!TryIds(args, "bookIds", out var bookIds))
                    return Invalid("'bookIds' must be an array of UUID strings.");

                var max = Num(args, "maxPerSource");
                if (max is < 1 or > 8)
                    return Invalid("'maxPerSource' must be between 1 and 8.");

                var result = await knowledge.SearchAsync(
                    new KnowledgeSearchRequest(
                        query.Trim(),
                        bookIds,
                        Id(args, "collectionId"),
                        max),
                    ct);

                return AssistantToolResult.Ok(Element(result));
            }),

        new AssistantCapability(
            "knowledge_overview",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Returns a compact structural overview of the user's notes and concepts: totals, unlinked notes, top concepts, and books with the most notes. Use this for whole-knowledge questions before making broad claims; it is structure, not an AI-generated insight.",
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
                var result = await knowledge.OverviewAsync(ct);
                return AssistantToolResult.Ok(Element(result));
            }),

        new AssistantCapability(
            "knowledge_read_evidence",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.SourceNavigation,
            "Re-reads one canonical evidence handle returned by knowledge_search. Use it when a later turn needs the exact note, concept, or imported-book chunk again instead of trusting an old excerpt.",
            """
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["note", "concept", "book_text"], "description": "Evidence kind from the handle. Required." },
                "noteId": { "type": "string", "format": "uuid", "description": "Required for kind=note." },
                "conceptId": { "type": "string", "format": "uuid", "description": "Required for kind=concept." },
                "bookId": { "type": "string", "format": "uuid", "description": "Required for kind=book_text." },
                "sourceSha256": { "type": "string", "description": "Exact source revision hash for kind=book_text." },
                "extractorVersion": { "type": "string", "description": "Exact extractor version for kind=book_text." },
                "ordinal": { "type": "integer", "minimum": 0, "description": "Exact indexed chunk ordinal for kind=book_text." }
              },
              "required": ["kind"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var kind = Str(args, "kind")?.Trim();
                if (string.IsNullOrWhiteSpace(kind))
                    return Invalid("'kind' is required.");

                var handle = new KnowledgeEvidenceHandle(
                    kind,
                    NoteId: Id(args, "noteId"),
                    ConceptId: Id(args, "conceptId"),
                    BookId: Id(args, "bookId"),
                    SourceSha256: Str(args, "sourceSha256"),
                    ExtractorVersion: Str(args, "extractorVersion"),
                    Ordinal: Num(args, "ordinal"));

                var result = await knowledge.ReadAsync(handle, ct);
                return result is null
                    ? AssistantToolResult.Fail(
                        AssistantErrorCodes.NotFound,
                        "That evidence handle is missing, stale, or no longer resolves to canonical Nostos data.")
                    : AssistantToolResult.Ok(Element(result));
            }),

        new AssistantCapability(
            "notes_list_for_book",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Lists the notes captured against one book.",
            """
            {
              "type": "object",
              "properties": {
                "bookId": { "type": "string", "format": "uuid", "description": "The id of the book whose notes you want. Required." }
              },
              "required": ["bookId"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "bookId") is not { } bookId)
                {
                    return Invalid("'bookId' is required.");
                }

                var result = await notes.GetByBookAsync(bookId, ct);
                return AssistantToolResult.Ok(Element(result));
            }),

        new AssistantCapability(
            "notes_search",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Searches only note text and book titles. For questions that may span notes, concepts, or imported-book text, prefer knowledge_search.",
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "The search words to match against note text and book titles. This is a search phrase, not a question for you to answer. Required." },
                "limit": { "type": "integer", "description": "The maximum number of matching notes to return. Defaults to 20." }
              },
              "required": ["query"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var query = Str(args, "query");
                if (string.IsNullOrWhiteSpace(query))
                {
                    return Invalid("'query' is required.");
                }

                var result = await notes.SearchAsync(query, Num(args, "limit") ?? 20, ct);
                return AssistantToolResult.Ok(Element(result));
            }),

        new AssistantCapability(
            "notes_list_unlinked",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Lists notes that belong to no concept, for the review queue.",
            """
            {
              "type": "object",
              "properties": {
                "limit": { "type": "integer", "description": "The maximum number of unlinked notes to return. Defaults to 20." },
                "offset": { "type": "integer", "description": "How many unlinked notes to skip before returning results. Defaults to 0." }
              },
              "required": [],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var result = await notes.GetUnlinkedAsync(
                    Num(args, "limit") ?? 20,
                    Num(args, "offset") ?? 0,
                    ct);

                return AssistantToolResult.Ok(Element(result));
            }),

        new AssistantCapability(
            "notes_read_for_review",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Reads one note (text, book, linked concepts) for the review flow.",
            """
            {
              "type": "object",
              "properties": {
                "noteId": { "type": "string", "format": "uuid", "description": "The id of the single note to read for review. Required." }
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

                var review = await notes.GetForReviewAsync(noteId, ct);
                return review is null
                    ? AssistantToolResult.Fail(
                        AssistantErrorCodes.NotFound,
                        $"Note {noteId} not found.")
                    : AssistantToolResult.Ok(Element(review));
            }),

        new AssistantCapability(
            "concepts_list",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Lists concepts ordered by usage.",
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
                var result = await concepts.GetAllWithUsageCountAsync();
                return AssistantToolResult.Ok(Element(result));
            }),

        new AssistantCapability(
            "concepts_search",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Searches concepts through their linked note evidence. For questions that may span notes, concepts, or imported-book text, prefer knowledge_search.",
            """
            {
              "type": "object",
              "properties": {
                "term": { "type": "string", "description": "The search term to match against the text of notes linked to concepts. This is a search term, not a question for you to answer. Required." }
              },
              "required": ["term"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var term = Str(args, "term");
                if (string.IsNullOrWhiteSpace(term))
                {
                    return Invalid("'term' is required.");
                }

                var result = await concepts.SearchByNoteTextAsync(term);
                return AssistantToolResult.Ok(Element(result));
            }),

        new AssistantCapability(
            "concepts_propose_links",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.Organization,
            "Explicitly proposes up to three existing concepts for one real note, each with a concrete evidence-based reason. Use only after reading the note and relevant concept material. Ordinary concept listing/search never creates suggestions. This tool does not link or create anything.",
            """
            {
              "type": "object",
              "properties": {
                "noteId": { "type": "string", "format": "uuid", "description": "The note whose actual material was read. Required." },
                "candidates": {
                  "type": "array", "maxItems": 3,
                  "items": {
                    "type": "object",
                    "properties": {
                      "conceptId": { "type": "string", "format": "uuid", "description": "Existing concept ID obtained from Nostos. Required." },
                      "reason": { "type": "string", "description": "One brief, concrete relationship between the note and this concept's evidence. Required." }
                    },
                    "required": ["conceptId", "reason"],
                    "additionalProperties": false
                  },
                  "description": "Zero to three relevant existing concepts; an empty list means no useful match."
                }
              },
              "required": ["noteId", "candidates"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "noteId") is not { } noteId)
                    return Invalid("'noteId' is required.");

                if (await notes.GetForReviewAsync(noteId, ct) is null)
                    return AssistantToolResult.Fail(
                        AssistantErrorCodes.NotFound,
                        "The note no longer exists.");

                var candidates = Property(args, "candidates");
                if (candidates.ValueKind != System.Text.Json.JsonValueKind.Array
                    || candidates.GetArrayLength() > 3)
                    return Invalid("'candidates' must be an array of at most three proposals.");

                var available = (await concepts.GetAllWithUsageCountAsync())
                    .ToDictionary(concept => concept.Id);
                var seen = new HashSet<Guid>();
                var proposals = new List<object>();
                foreach (var item in candidates.EnumerateArray())
                {
                    if (Id(item, "conceptId") is not { } conceptId
                        || !seen.Add(conceptId)
                        || !available.TryGetValue(conceptId, out var concept))
                        return Invalid("Every proposed concept must be a distinct existing concept ID.");

                    var reason = Str(item, "reason")?.Trim();
                    if (string.IsNullOrWhiteSpace(reason) || reason.Length > 240)
                        return Invalid("Every proposal needs a brief evidence-based reason.");

                    proposals.Add(new { id = concept.Id, name = concept.Name, reason });
                }

                return AssistantToolResult.Ok(Element(new { noteId, candidates = proposals }));
            }),

    ];
}
