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
/// </summary>
internal sealed class AssistantRetrievalTurnState(AssistantContextDto context)
{
    private const string BookTextCapability = "book_text_search";
    private const string KnowledgeSearchCapability = "knowledge_search";

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

        if (data is not { } retrievalData
            || (!string.Equals(capabilityName, KnowledgeSearchCapability, StringComparison.Ordinal)
                && !string.Equals(capabilityName, BookTextCapability, StringComparison.Ordinal)))
        {
            return;
        }

        RetrievalAttempted = true;
        ScopedRetrievalAttempted |= HasRetrievalScope(args);

        var callHasEvidence = RetrievalHasEvidence(retrievalData);
        RetrievalEvidenceAvailable |= callHasEvidence;
        if (!callHasEvidence)
            RecordScopedBookIds(args, _emptyRetrievalBookIds);

        ObserveIngestionStates(capabilityName, retrievalData);
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
    /// original scope is preserved; only the query is reformulated from the
    /// user's literal lexical terms.
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
        if (tokens.All(token =>
                currentQuery.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var obj = JsonNode.Parse(args.GetRawText()) as JsonObject ?? new JsonObject();
        obj["query"] = string.Join(" ", tokens);
        return JsonSerializer.SerializeToElement(obj, JsonOptions);
    }

    private static bool IsSparseKnowledgeResult(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return false;

        var evidenceCount =
            ArrayLength(data, "notes")
            + ArrayLength(data, "concepts")
            + ArrayLength(data, "bookPassages");

        return evidenceCount <= 1;
    }

    private static bool HasBlockingKnowledgeSourceState(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("bookTextStates", out var states)
            || states.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in states.EnumerateArray())
        {
            if (!item.TryGetProperty("status", out var status))
                continue;

            if (TryReadIngestionStatus(status, out var parsed)
                && parsed is BookTextIngestionStatus.Pending
                    or BookTextIngestionStatus.Processing
                    or BookTextIngestionStatus.Failed
                    or BookTextIngestionStatus.Unsupported)
            {
                return true;
            }
        }

        return false;
    }

    private static int ArrayLength(JsonElement data, string propertyName) =>
        data.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static bool TryReadIngestionStatus(
        JsonElement status,
        out BookTextIngestionStatus parsed)
    {
        parsed = default;
        if (status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var numeric)
            && Enum.IsDefined(typeof(BookTextIngestionStatus), numeric))
        {
            parsed = (BookTextIngestionStatus)numeric;
            return true;
        }

        return status.ValueKind == JsonValueKind.String
            && Enum.TryParse(
                status.GetString(),
                ignoreCase: true,
                out parsed);
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

    private void ObserveIngestionStates(string capabilityName, JsonElement data)
    {
        var statePropertyName = string.Equals(
            capabilityName,
            KnowledgeSearchCapability,
            StringComparison.Ordinal)
                ? "bookTextStates"
                : "states";

        if (!data.TryGetProperty(statePropertyName, out var stateArray)
            || stateArray.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in stateArray.EnumerateArray())
        {
            if (!item.TryGetProperty("status", out var status))
                continue;

            if (status.ValueKind == JsonValueKind.Number
                && status.TryGetInt32(out var numeric)
                && Enum.IsDefined(typeof(BookTextIngestionStatus), numeric))
            {
                _states.Add((BookTextIngestionStatus)numeric);
            }
            else if (status.ValueKind == JsonValueKind.String
                     && Enum.TryParse<BookTextIngestionStatus>(
                         status.GetString(),
                         ignoreCase: true,
                         out var parsed))
            {
                _states.Add(parsed);
            }
        }
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

    private static bool RetrievalHasEvidence(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty("evidenceAvailable", out var evidence)
        && evidence.ValueKind == JsonValueKind.True;

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

    private static bool TryReadCanonicalBook(
        string capabilityName,
        JsonElement? data,
        out AssistantResolvedBookDto book)
    {
        book = default!;
        if (data is not { ValueKind: JsonValueKind.Object } element)
            return false;

        JsonElement candidate = default;
        if (string.Equals(capabilityName, "library_get_book", StringComparison.Ordinal))
        {
            if (!element.TryGetProperty("data", out candidate)
                || candidate.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
        }
        else if (string.Equals(capabilityName, "library_resolve_book", StringComparison.Ordinal))
        {
            if (element.TryGetProperty("matchedBook", out var matched)
                && matched.ValueKind == JsonValueKind.Object)
            {
                candidate = matched;
            }
            else if (element.TryGetProperty("candidates", out var candidates)
                     && candidates.ValueKind == JsonValueKind.Array
                     && candidates.GetArrayLength() == 1)
            {
                candidate = candidates.EnumerateArray().Single();
            }
            else
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (!candidate.TryGetProperty("id", out var idProperty))
            candidate.TryGetProperty("bookId", out idProperty);

        if (idProperty.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idProperty.GetString(), out var id)
            || !candidate.TryGetProperty("title", out var titleProperty)
            || titleProperty.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(titleProperty.GetString()))
        {
            return false;
        }

        book = new AssistantResolvedBookDto(id, titleProperty.GetString()!);
        return true;
    }

    private static bool CanRetryBookTextSearch(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return false;

        if (data.TryGetProperty("evidenceAvailable", out var evidence)
            && evidence.ValueKind == JsonValueKind.True)
        {
            return false;
        }

        if (!data.TryGetProperty("states", out var states)
            || states.ValueKind != JsonValueKind.Array
            || states.GetArrayLength() == 0)
        {
            return false;
        }

        foreach (var item in states.EnumerateArray())
        {
            if (!item.TryGetProperty("status", out var status))
                return false;

            if (status.ValueKind == JsonValueKind.Number
                && status.TryGetInt32(out var numeric))
            {
                if ((BookTextIngestionStatus)numeric != BookTextIngestionStatus.Ready)
                    return false;
                continue;
            }

            if (status.ValueKind == JsonValueKind.String
                && Enum.TryParse<BookTextIngestionStatus>(
                    status.GetString(),
                    ignoreCase: true,
                    out var parsed))
            {
                if (parsed != BookTextIngestionStatus.Ready)
                    return false;
                continue;
            }

            return false;
        }

        return true;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
