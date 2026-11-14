using System.Text.Json;
using System.Text.Json.Nodes;
using Nostos.Backend.Search;
using Nostos.Product.BookText;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Server-owned retrieval state for one Ask Nostos turn.
///
/// It has three jobs that should not live in the orchestration loop:
/// - preserve an explicit/current/recent canonical book scope without guessing;
/// - observe which canonical reads actually produced grounding;
/// - decide whether an empty retrieval is terminal for the user's request.
///
/// The object is turn-scoped and contains no durable conversation state. The
/// browser already carries historical book identity through
/// <see cref="AssistantHistoricalContextDto"/>.
///
/// Capability results are read through <see cref="AssistantCapabilityPayloads"/>
/// (issue #1598), so the ingestion status of a source is parsed once rather than
/// twice by two string-keyed probes.
/// </summary>
internal sealed class AssistantRetrievalTurnState(AssistantContextDto context)
{
    private const string BookTextCapability = AssistantCapabilityNames.BookTextSearch;
    private const string KnowledgeSearchCapability = AssistantCapabilityNames.KnowledgeSearch;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HashSet<Guid> _metadataBookIds = [];
    private readonly HashSet<Guid> _emptyRetrievalBookIds = [];
    private readonly Dictionary<Guid, string> _bookScopes = [];
    private readonly List<BookTextIngestionStatus> _states = [];

    public bool RetrievalAttempted { get; private set; }
    public bool ScopedRetrievalAttempted { get; private set; }
    public bool RetrievalEvidenceAvailable { get; private set; }

    public AssistantResolvedBookDto? ResolvedBook =>
        _bookScopes.Count == 1
            ? _bookScopes
                .Select(item => string.IsNullOrWhiteSpace(item.Value)
                    ? null
                    : new AssistantResolvedBookDto(item.Key, item.Value))
                .SingleOrDefault()
            : null;

    public void RecordResolvedBook(AssistantResolvedBookDto book) =>
        RecordBookScope(book.BookId.ToString(), book.BookTitle);

    /// <summary>
    /// Applies the strongest available implicit book scope only when the model
    /// omitted scope entirely. An explicit <c>bookIds: []</c> is therefore a
    /// meaningful whole-library broadening and is never overwritten.
    /// </summary>
    public JsonElement ApplyImplicitBookScope(
        JsonElement args,
        IReadOnlyList<AssistantHistoryMessageDto>? history,
        out bool implicitReaderScope)
    {
        implicitReaderScope = false;

        if (HasExplicitScopeArgument(args))
        {
            RecordExplicitKnownBookScope(args, history);
            return args;
        }

        Guid bookId;
        string? bookTitle;
        if (Guid.TryParse(context.BookId, out var currentBookId))
        {
            bookId = currentBookId;
            bookTitle = context.BookTitle;
            implicitReaderScope = string.Equals(
                context.Surface,
                "reader",
                StringComparison.OrdinalIgnoreCase);
        }
        else if (TryGetLatestHistoricalBookScope(history, out var historical))
        {
            bookId = historical.BookId;
            bookTitle = historical.BookTitle;
        }
        else
        {
            return args;
        }

        RecordBookScope(bookId.ToString(), bookTitle);

        var obj = JsonNode.Parse(args.GetRawText()) as JsonObject ?? new JsonObject();
        obj["bookIds"] = new JsonArray(bookId.ToString());
        return JsonSerializer.SerializeToElement(obj, JsonOptions);
    }

    /// <summary>
    /// Records a successful Suggest/read result. Empty search results are kept
    /// separate from successful metadata reads so terminal truth can distinguish
    /// "no passage" from "the whole turn had no canonical evidence".
    /// </summary>
    public void ObserveResult(
        string capabilityName,
        JsonElement args,
        JsonElement? data)
    {
        if (TryReadCanonicalBook(capabilityName, data, out var readBook))
        {
            _metadataBookIds.Add(readBook.BookId);
            RecordBookScope(readBook.BookId.ToString(), readBook.BookTitle);
        }

        IReadOnlyList<BookTextIngestionStatus> states;
        bool callHasEvidence;
        if (string.Equals(capabilityName, KnowledgeSearchCapability, StringComparison.Ordinal))
        {
            if (AssistantCapabilityPayloads.ReadKnowledgeSearch(data) is not { } knowledge)
                return;

            callHasEvidence = knowledge.EvidenceAvailable;
            states = knowledge.BookTextStates?.Select(state => state.Status).ToList() ?? [];
        }
        else if (string.Equals(capabilityName, BookTextCapability, StringComparison.Ordinal))
        {
            if (AssistantCapabilityPayloads.ReadBookTextSearch(data) is not { } bookText)
                return;

            callHasEvidence = bookText.EvidenceAvailable;
            states = bookText.States?.Select(state => state.Status).ToList() ?? [];
        }
        else
        {
            return;
        }

        RetrievalAttempted = true;
        ScopedRetrievalAttempted |= HasRetrievalScope(args);

        RetrievalEvidenceAvailable |= callHasEvidence;
        if (!callHasEvidence)
            RecordScopedBookIds(args, _emptyRetrievalBookIds);

        if (states.Count > 0)
            _states.AddRange(states);
    }

    public void RecordSources(IEnumerable<AssistantSourceReferenceDto> sources)
    {
        foreach (var source in sources)
            RecordBookScope(source.BookId.ToString(), source.BookTitle);
    }

    public void MarkExactEvidenceAvailable() =>
        RetrievalEvidenceAvailable = true;

    /// <summary>
    /// Returns a terminal retrieval error only when the turn genuinely ended
    /// without enough Nostos evidence for the user's request.
    /// </summary>
    public AssistantTurnErrorDto? TerminalFailure(string userMessage)
    {
        if (!RetrievalAttempted || RetrievalEvidenceAvailable)
            return null;

        var sourceStateFailure = SourceStateFailure();
        if (sourceStateFailure is not null)
            return sourceStateFailure;

        var requiresNostosEvidence =
            ScopedRetrievalAttempted
            || AssistantRetrievalIntentPolicy.IsExplicitMaterialLookup(userMessage);
        if (!requiresNostosEvidence)
            return null;

        // Canonical book metadata can ground a clearly bibliographic question,
        // but never a source-content question. It must also be metadata for the
        // same scoped book whose text lookup was empty.
        var metadataGroundedSameBook =
            AssistantRetrievalIntentPolicy.IsClearlyBookMetadataRequest(userMessage)
            && _emptyRetrievalBookIds.Overlaps(_metadataBookIds);

        return metadataGroundedSameBook
            ? null
            : new AssistantTurnErrorDto(
                AssistantErrorCodes.NoEvidence,
                "I could not find usable evidence for that in your Nostos material.");
    }

    /// <summary>
    /// Reader context starts narrow for latency and relevance, but a single
    /// weak same-book hit is not enough to prove the answer is in that book.
    /// For implicitly scoped knowledge lookups only, widen once to the whole
    /// library when the result is empty/sparse. Explicit scope is never widened.
    /// </summary>
    public JsonElement? BuildKnowledgeScopeFallbackArguments(
        JsonElement args,
        JsonElement resultData,
        bool implicitReaderScope)
    {
        if (!implicitReaderScope
            || !IsSparseKnowledgeResult(resultData)
            || HasBlockingKnowledgeSourceState(resultData))
        {
            return null;
        }

        var obj = JsonNode.Parse(args.GetRawText()) as JsonObject ?? new JsonObject();
        obj["bookIds"] = new JsonArray();
        return JsonSerializer.SerializeToElement(obj, JsonOptions);
    }

    /// <summary>
    /// One bounded deterministic recovery for a ready indexed source. The
    /// original scope is preserved; whenever the model query is not already
    /// in recovery form, it is reformulated from the user's literal lexical
    /// terms, including over-specified supersets.
    /// </summary>
    public JsonElement? BuildBookTextFallbackArguments(
        JsonElement args,
        string userMessage,
        JsonElement resultData)
    {
        if (!CanRetryBookTextSearch(resultData))
            return null;

        var tokens = LexicalQueryPlanner.Build(userMessage)
            .Where(variant => variant.Kind == LexicalQueryVariantKind.Token)
            .Select(variant => variant.Text)
            .Where(token => !AssistantRetrievalIntentPolicy.IsBookTextRecoveryFiller(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (tokens.Count == 0)
            return null;

        var currentQuery = ReadString(args, "query") ?? string.Empty;
        var recoveryQuery = string.Join(" ", tokens);
        if (string.Equals(
                CollapseWhitespace(currentQuery),
                recoveryQuery,
                StringComparison.OrdinalIgnoreCase))
        {
            // Repeating the identical query cannot change the outcome.
            return null;
        }

        var obj = JsonNode.Parse(args.GetRawText()) as JsonObject ?? new JsonObject();
        obj["query"] = recoveryQuery;
        return JsonSerializer.SerializeToElement(obj, JsonOptions);
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool IsSparseKnowledgeResult(JsonElement data)
    {
        if (AssistantCapabilityPayloads.ReadKnowledgeSearch(data) is not { } knowledge)
            return false;

        // A topic hit can be a useful orientation clue without containing
        // the continuity evidence itself. Treat the reader lookup as weak when
        // it has at most one concrete note/passage, so a topic-only or
        // one-note distractor cannot prevent the bounded library fallback.
        var concreteEvidenceCount =
            (knowledge.Notes?.Count ?? 0)
            + (knowledge.BookPassages?.Count ?? 0);

        return concreteEvidenceCount <= 1;
    }

    private static bool HasBlockingKnowledgeSourceState(JsonElement data)
    {
        if (AssistantCapabilityPayloads.ReadKnowledgeSearch(data) is not { BookTextStates: { } states })
            return false;

        return states.Any(state => state.Status is
            BookTextIngestionStatus.Pending
            or BookTextIngestionStatus.Processing
            or BookTextIngestionStatus.Failed
            or BookTextIngestionStatus.Unsupported);
    }

    private AssistantTurnErrorDto? SourceStateFailure()
    {
        if (_states.Any(status =>
                status is BookTextIngestionStatus.Pending or BookTextIngestionStatus.Processing))
        {
            return new AssistantTurnErrorDto(
                AssistantErrorCodes.SourceIndexingPending,
                "This source is still being indexed. Try again when it is ready.");
        }

        if (_states.Contains(BookTextIngestionStatus.Failed))
        {
            return new AssistantTurnErrorDto(
                AssistantErrorCodes.SourceIndexingFailed,
                "Text indexing failed for this source, so Ask Nostos cannot search it yet.");
        }

        if (_states.Contains(BookTextIngestionStatus.Unsupported))
        {
            return new AssistantTurnErrorDto(
                AssistantErrorCodes.SourceIndexingUnsupported,
                "This source cannot be searched as text in its current format.");
        }

        return null;
    }

    private static bool HasRetrievalScope(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return false;

        if (args.TryGetProperty("bookIds", out var bookIds)
            && bookIds.ValueKind == JsonValueKind.Array
            && bookIds.GetArrayLength() > 0)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(ReadString(args, "collectionId"));
    }

    private static bool HasExplicitScopeArgument(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return false;

        if (args.TryGetProperty("bookIds", out var bookIds)
            && bookIds.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            // An empty array is intentional whole-library scope. A malformed
            // non-null value is also explicit: do not silently overwrite it;
            // the capability should return its normal invalid-arguments error.
            return true;
        }

        return args.TryGetProperty("collectionId", out var collectionId)
            && collectionId.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }

    private void RecordExplicitKnownBookScope(
        JsonElement args,
        IReadOnlyList<AssistantHistoryMessageDto>? history)
    {
        if (!TryReadSingleBookId(args, out var explicitBookId))
            return;

        if (Guid.TryParse(context.BookId, out var currentBookId)
            && currentBookId == explicitBookId
            && !string.IsNullOrWhiteSpace(context.BookTitle))
        {
            RecordBookScope(explicitBookId.ToString(), context.BookTitle);
            return;
        }

        if (TryGetLatestHistoricalBookScope(history, out var historical)
            && historical.BookId == explicitBookId)
        {
            RecordBookScope(explicitBookId.ToString(), historical.BookTitle);
        }
    }

    private static bool TryReadSingleBookId(JsonElement args, out Guid bookId)
    {
        bookId = default;
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("bookIds", out var bookIds)
            || bookIds.ValueKind != JsonValueKind.Array
            || bookIds.GetArrayLength() != 1)
        {
            return false;
        }

        var item = bookIds.EnumerateArray().Single();
        return item.ValueKind == JsonValueKind.String
            && Guid.TryParse(item.GetString(), out bookId);
    }

    private static bool TryGetLatestHistoricalBookScope(
        IReadOnlyList<AssistantHistoryMessageDto>? history,
        out AssistantResolvedBookDto scope)
    {
        scope = default!;
        var latestUser = history?
            .LastOrDefault(entry =>
                string.Equals(entry.Role, "user", StringComparison.OrdinalIgnoreCase));

        if (latestUser?.Context is not { } historicalContext
            || !Guid.TryParse(historicalContext.BookId, out var bookId))
        {
            return false;
        }

        scope = new AssistantResolvedBookDto(
            bookId,
            historicalContext.BookTitle?.Trim() ?? string.Empty);
        return true;
    }

    private static void RecordScopedBookIds(
        JsonElement args,
        HashSet<Guid> target)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("bookIds", out var bookIds)
            || bookIds.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in bookIds.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && Guid.TryParse(item.GetString(), out var id))
            {
                target.Add(id);
            }
        }
    }

    private void RecordBookScope(string? bookId, string? bookTitle)
    {
        if (!Guid.TryParse(bookId, out var id))
            return;

        if (!_bookScopes.TryGetValue(id, out var existing)
            || string.IsNullOrWhiteSpace(existing))
        {
            _bookScopes[id] = bookTitle?.Trim() ?? string.Empty;
        }
    }

    /// <summary>
    /// The canonical book a library read answered with, from the typed library
    /// envelope or the typed resolution (issue #1599 contracts).
    /// </summary>
    private static bool TryReadCanonicalBook(
        string capabilityName,
        JsonElement? data,
        out AssistantResolvedBookDto book)
    {
        book = default!;
        if (string.Equals(capabilityName, "library_get_book", StringComparison.Ordinal))
        {
            return AssistantCapabilityPayloads.ReadLibraryBook(data) is { } read
                && TryAccept(read.Id, read.Title, out book);
        }

        if (string.Equals(capabilityName, "library_resolve_book", StringComparison.Ordinal))
        {
            if (AssistantCapabilityPayloads.ReadResolvedBook(data) is not { } resolved)
                return false;

            if (resolved.MatchedBook is { } matched)
                return TryAccept(matched.Id, matched.Title, out book);

            if (resolved.Candidates is { Count: 1 } candidates)
                return TryAccept(candidates[0].BookId, candidates[0].Title, out book);

            return false;
        }

        return false;
    }

    private static bool TryAccept(Guid id, string? title, out AssistantResolvedBookDto book)
    {
        book = default!;
        if (string.IsNullOrWhiteSpace(title))
            return false;

        book = new AssistantResolvedBookDto(id, title);
        return true;
    }

    private static bool CanRetryBookTextSearch(JsonElement data)
    {
        if (AssistantCapabilityPayloads.ReadBookTextSearch(data) is not { } bookText)
            return false;

        if (bookText.EvidenceAvailable)
            return false;

        if (bookText.States is not { Count: > 0 } states)
            return false;

        return states.All(state => state.Status == BookTextIngestionStatus.Ready);
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
