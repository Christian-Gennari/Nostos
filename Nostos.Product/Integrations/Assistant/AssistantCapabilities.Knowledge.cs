using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Mapping;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Knowledge;

namespace Nostos.Backend.Integrations.Assistant;

public static partial class AssistantCapabilities
{
    private static IReadOnlyList<AssistantCapability> BuildKnowledgeCapabilities(
        INoteService notes,
        ITopicRepository topics,
        IKnowledgeRetrievalService knowledge) =>
    [
        new AssistantCapability(
            "knowledge_search",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Searches the user's own notes, topics-through-linked-notes, and indexed imported PDF/EPUB text through one bounded retrieval path. Prefer this for questions that may span the user's reading and thinking. Results include canonical evidence handles that can be re-read exactly.",
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "Words or a short natural-language formulation of what to find in the user's material. Required." },
                "bookIds": {
                  "type": "array",
                  "items": { "type": "string", "format": "uuid" },
                  "description": "Explicit book scope. When present and non-empty, note/topic/book-text evidence is restricted to these books. Omit to keep current/recent conversational book scope; pass an empty array only when the user explicitly broadens to the whole library."
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
            "Returns a compact structural overview of the user's notes and topics: totals, unlinked notes, top topics, and books with the most notes. Use this for whole-knowledge questions before making broad claims; it is structure, not an AI-generated insight.",
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
            "Re-reads one canonical evidence handle returned by knowledge_search. Use it when a later turn needs the exact note, topic, or imported-book chunk again instead of trusting an old excerpt.",
            """
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["note", "topic", "book_text"], "description": "Evidence kind from the handle. Required." },
                "noteId": { "type": "string", "format": "uuid", "description": "Required for kind=note." },
                "topicId": { "type": "string", "format": "uuid", "description": "Required for kind=topic." },
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
                    TopicId: Id(args, "topicId"),
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
            "Searches notes by note text, book titles/authors, and linked topic names. For questions that may span notes, topics, or imported-book text, prefer knowledge_search.",
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "The search words to match against note text, book titles/authors, and linked topic names. This is a search phrase, not a question for you to answer. Required." },
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
            "Lists notes that belong to no topic, for the review queue.",
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
            "Reads one note (text, book, linked topics) for the review flow.",
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
            "topics_list",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Lists topics ordered by usage, one page at a time, each with its kind and who set it. Use untyped to classify: it lists only topics with no kind yet as id and name, which is all classifying needs. nextPage says which page to ask for next; a large library is a few pages, not one huge answer.",
            """
            {
              "type": "object",
              "properties": {
                "page": { "type": "integer", "minimum": 1, "description": "The 1-based page of topics to return. Defaults to 1." },
                "pageSize": { "type": "integer", "minimum": 1, "maximum": 250, "description": "How many topics per page, up to 250. Defaults to 250 with untyped and 50 otherwise." },
                "untyped": { "type": "boolean", "description": "True to list only topics that have no kind yet, as {id, name} only." },
                "kindId": { "type": "string", "format": "uuid", "description": "Only topics with this kind. Omit it for every kind." }
              },
              "required": [],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var page = Num(args, "page") ?? 1;
                var untyped = Bool(args, "untyped") ?? false;
                // Classifying reads the whole unkinded set; a full page by
                // default means a 150-topic library is one call, not three.
                var pageSize = Num(args, "pageSize") ?? (untyped ? 250 : 50);
                if (page < 1)
                    return Invalid("'page' must be at least 1.");
                if (pageSize is < 1 or > 250)
                    return Invalid("'pageSize' must be between 1 and 250.");
                var result = await topics.GetPageWithUsageAsync(
                    page,
                    pageSize,
                    untyped,
                    Id(args, "kindId"),
                    ct);

                // Classifying a topic only needs its id and name. Measured on a
                // 149-topic library, the full row is 172 bytes against 71 for
                // this projection, so a 250-topic page spends about 59% less of
                // the turn's token budget.
                if (untyped)
                {
                    return AssistantToolResult.Ok(Element(new
                    {
                        items = result.Items.Select(topic => new { id = topic.Id, name = topic.Name }).ToList(),
                        result.Page,
                        result.PageSize,
                        result.Total,
                        result.TotalPages,
                        result.UnkindedTotal,
                        result.NextPage,
                    }));
                }

                return AssistantToolResult.Ok(Element(result));
            }),

        new AssistantCapability(
            "topics_list_kinds",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Lists the topic kinds the library has, each with its colour and how many topics use it. Use these ids or names with topics_set_kind.",
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
                var kinds = await topics.GetAllKindsAsync(ct);
                return AssistantToolResult.Ok(Element(kinds));
            }),

        new AssistantCapability(
            "topics_search",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Searches topics through their linked note evidence. For questions that may span notes, topics, or imported-book text, prefer knowledge_search.",
            """
            {
              "type": "object",
              "properties": {
                "term": { "type": "string", "description": "The search term to match against the text of notes linked to topics. This is a search term, not a question for you to answer. Required." }
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

                var result = await topics.SearchByNoteTextAsync(term);
                return AssistantToolResult.Ok(Element(result));
            }),

        new AssistantCapability(
            "topics_get",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.KnowledgeRetrieval,
            "Reads one topic: the user's own description of it and the notes linked to it, newest first, each with its book. Find the topic's id with topics_list or topics_search first.",
            """
            {
              "type": "object",
              "properties": {
                "topicId": { "type": "string", "format": "uuid", "description": "The id of the topic to read, from topics_list or topics_search. Required." },
                "limit": { "type": "integer", "minimum": 1, "maximum": 100, "description": "The maximum number of linked notes to return, newest first. Defaults to 30." }
              },
              "required": ["topicId"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "topicId") is not { } topicId)
                    return Invalid("'topicId' is required.");

                var limit = Num(args, "limit");
                if (limit is < 1 or > 100)
                    return Invalid("'limit' must be between 1 and 100.");

                // The same read the topic page uses (GET /api/topics/{id}).
                var topic = await topics.GetByIdWithNotesAsync(topicId);
                if (topic is null)
                    return AssistantToolResult.Fail(
                        AssistantErrorCodes.NotFound,
                        $"Topic {topicId} not found.");

                var linked = topic.NoteTopics
                    .Select(link => link.Note)
                    .Where(note => note is not null)
                    .OrderByDescending(note => note.CreatedAt)
                    .ThenBy(note => note.Id)
                    .ToList();

                var kind = topic.KindId is { } kindId
                    ? (await topics.GetAllKindsAsync(ct)).FirstOrDefault(candidate => candidate.Id == kindId)
                    : null;

                return AssistantToolResult.Ok(Element(new
                {
                    id = topic.Id,
                    name = topic.Topic,
                    description = topic.Description,
                    kindId = topic.KindId,
                    kindName = kind?.Name,
                    kindSource = topic.KindSource,
                    noteCount = linked.Count,
                    notes = linked.Take(limit ?? 30).Select(note => new
                    {
                        noteId = note.Id,
                        bookId = note.BookId,
                        bookTitle = note.Book?.Title,
                        bookAuthor = note.Book?.Author,
                        kind = note.Kind,
                        content = note.Content,
                        // A passage's text is long; its opening is enough to
                        // recognise it, and knowledge_read_evidence reads it whole.
                        selectedText = TextLigatures.Expand(note.IsPassage()
                            ? PassageText.Opening(note.SelectedText)
                            : note.SelectedText),
                        location = note.SourceAnchorValue,
                        locationKind = note.SourceAnchorKind,
                        createdAt = note.CreatedAt,
                    }),
                }));
            }),

        new AssistantCapability(
            "topics_propose_links",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.Organization,
            "Explicitly proposes up to three additional existing topics not already linked to the note, each with a concrete evidence-based reason. Use only after reading the note and relevant topic material. Ordinary topic listing/search never creates suggestions. This tool does not link or create anything.",
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
                      "topicId": { "type": "string", "format": "uuid", "description": "Existing topic ID obtained from Nostos. Required." },
                      "reason": { "type": "string", "description": "One brief, concrete relationship between the note and this topic's evidence. Required." }
                    },
                    "required": ["topicId", "reason"],
                    "additionalProperties": false
                  },
                  "description": "Zero to three relevant existing topics; an empty list means no useful match."
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

                var review = await notes.GetForReviewAsync(noteId, ct);
                if (review is null)
                    return AssistantToolResult.Fail(
                        AssistantErrorCodes.NotFound,
                        "The note no longer exists.");

                var candidates = Property(args, "candidates");
                if (candidates.ValueKind != System.Text.Json.JsonValueKind.Array
                    || candidates.GetArrayLength() > 3)
                    return Invalid("'candidates' must be an array of at most three proposals.");

                var available = (await topics.GetAllWithUsageCountAsync())
                    .ToDictionary(topic => topic.Id);
                var seen = new HashSet<Guid>();
                var linkedTopicIds = review.TopicIds.ToHashSet();
                var proposals = new List<object>();
                foreach (var item in candidates.EnumerateArray())
                {
                    if (Id(item, "topicId") is not { } topicId
                        || !seen.Add(topicId)
                        || !available.TryGetValue(topicId, out var topic))
                        return Invalid("Every proposed topic must be a distinct existing topic ID.");

                    // Server-owned note associations, not the model's opinion, decide novelty.
                    // Already-linked candidates are simply omitted so mixed batches still help.
                    if (linkedTopicIds.Contains(topicId)) continue;

                    var reason = Str(item, "reason")?.Trim();
                    if (string.IsNullOrWhiteSpace(reason) || reason.Length > 240)
                        return Invalid("Every proposal needs a brief evidence-based reason.");

                    proposals.Add(new { id = topic.Id, name = topic.Name, reason });
                }

                return AssistantToolResult.Ok(Element(new { noteId, candidates = proposals }));
            }),

    ];
}
