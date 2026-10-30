using System.Text.Json;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Knowledge;
using Nostos.Shared.Dtos;
using Nostos.Product.Services.Ai;
using Nostos.Product.BookText;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// The backend assistant bridge (issue #261 §3, §4, §7): it gives the assistant
/// an LLM and an in-process tool loop over <see cref="AssistantCapabilityRegistry"/>.
///
/// Design rules, in one place:
/// <list type="bullet">
/// <item>Tools are generated from the registry, one per capability, using its
/// <c>Summary</c>. The assistant reaches its own Nostos instance
/// directly, through the same canonical services the REST surface uses.</item>
/// <item>Trust classes are enforced here as well as in the registry.
/// <see cref="AssistantTrustClass.Capture"/> and normal
/// <see cref="AssistantTrustClass.Act"/> work execute inside the tool loop,
/// <see cref="AssistantTrustClass.Suggest"/> never mutates, and library
/// organisation (<see cref="AssistantTrustClass.Stage"/>) only stages into the
/// turn's changeset. When the model is done, the change policy judges the whole
/// changeset by what it would do: it applies atomically with undo, or waits
/// for the user's review. One request is one changeset, whatever its size.</item>
/// <item>The source-location follow-up is deterministic: when a capture has no
/// nearby anchor and the format cannot supply one, the bridge asks (physical
/// book → a page; externally played audiobook → a timestamp) instead of
/// guessing. An explicitly skipped anchor still captures as <c>unknown</c>.</item>
/// </list>
/// </summary>
public sealed class AssistantOrchestrator(
    AssistantCapabilityRegistry registry,
    ILlmProvider llm,
    AssistantChangesetCoordinator changesets,
    ILibraryChangesetService libraryChanges,
    AssistantContinuationStore continuations,
    ILibraryService library,
    AssistantOptions options,
    ILogger<AssistantOrchestrator> logger,
    IAiUsageAccountingService? usageAccounting = null,
    TimeProvider? timeProvider = null,
    IAssistantOwner? owner = null)
{
    private readonly IAiUsageAccountingService _usageAccounting =
        usageAccounting ?? NoOpAiUsageAccountingService.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IAssistantOwner _owner = owner ?? SingleAssistantOwner.Instance;
    private readonly AssistantConversationBuilder _conversation =
        new(registry, new AssistantContextPacker(options));
    private readonly AssistantCapturePolicy _capturePolicy = new(library);
    private readonly AssistantCaptureIntentGuard _captureIntentGuard = new(library);

    /// <summary>
    /// Generous on purpose: a tool-calling turn can spend reasoning tokens even
    /// on a two-word reply, and a tighter budget truncates real answers.
    /// </summary>
    public const int MaxResponseTokens = 4096;

    /// <summary>
    /// The review flow's "small set": at most three candidate topics are shown
    /// for one unlinked note. A longer list is a search result, not a suggestion
    /// (issue #261 §5).
    /// </summary>
    public const int MaxTopicSuggestions = 3;

    /// <summary>
    /// A turn keeps only a small bounded set of evidence artifacts. Stable
    /// handles carry identity; excerpts are display hints, not a second archive.
    /// </summary>
    public const int MaxEvidenceArtifacts = 12;

    /// <summary>
    /// Appended to a quote typed/transcribed by hand rather than read from the
    /// digital source, so the difference is never inferred later.
    /// </summary>
    public const string QuoteFidelityNote =
        "Quoted by hand; punctuation and wording may differ from the source.";

    /// <summary>
    /// The one-line reply when a turn ends with no assistant content at all —
    /// the measured "exhausted the tool loop and returned nothing" case. It is
    /// deliberately not an apology and never claims the request succeeded. It is
    /// "that", not "what you asked": roughly half of these turns are captures,
    /// where the user asked nothing and simply gave the assistant something.
    /// </summary>
    public const string IncompleteTurnReply =
        "I reached this turn's execution limit before I could finish. Send another message to continue.";

    /// <summary>
    /// The reply when the execution ceiling fires after the final round was
    /// nothing but successful canonical writes: the requested change is done
    /// and only the model's closing prose is missing. The server-owned action
    /// artifacts name what changed, so no extra model call is spent to say so.
    /// </summary>
    public const string CompletedBeforeCeilingReply = "Done.";

    /// <summary>
    /// The reply when the execution ceiling fires after some canonical writes
    /// committed but work was still in progress. It never implies a rollback:
    /// the committed changes stay applied and are listed as action artifacts.
    /// </summary>
    public const string PartiallyCompletedReply =
        "I made the changes listed below, but reached this turn's execution limit before I could finish the rest. Send another message to continue.";


    /// <summary>
    /// The one question a capture asks when the app cannot know the book: no book
    /// is open and the user has not named one. Nothing is saved until it is
    /// answered, because a guess files the thought in the wrong place.
    /// </summary>
    public const string WhichBookQuestion = "Which book is this for?";

    /// <summary>
    /// Follow-up after a title produced no local candidate. A continuation may
    /// ask this again only within the bounded book-resolution attempt budget.
    /// </summary>
    public const string BookNotFoundQuestion = "I could not find that book. Which book is this for?";

    /// <summary>The prompt kind the client answers with a title or numbered candidate.</summary>
    public const string BookPromptKind = "book";

    private const int MaxBookContinuationAttempts = 3;
    private const string CaptureCapability = "notes_capture";
    private const string RewriteCaptureCapability = "notes_rewrite_capture";
    private const string BookTextCapability = "book_text_search";
    private const string FindQuoteCapability = "book_text_find_quote";
    private const string KnowledgeSearchCapability = "knowledge_search";
    private const string KnowledgeReadCapability = "knowledge_read_evidence";
    private const string NotesListForBookCapability = "notes_list_for_book";
    private const string NotesSearchCapability = "notes_search";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string TopicProposalCapability = "topics_propose_links";

    /// <summary>
    /// Reads that return text the user did not write in this conversation:
    /// imported book text, notes (which include imported highlights), and book
    /// descriptions from metadata providers. Any of them can carry instructions.
    /// </summary>
    private static readonly HashSet<string> UntrustedContentCapabilities = new(StringComparer.Ordinal)
    {
        BookTextCapability,
        FindQuoteCapability,
        KnowledgeSearchCapability,
        KnowledgeReadCapability,
        "knowledge_overview",
        NotesListForBookCapability,
        NotesSearchCapability,
        "notes_list_unlinked",
        "notes_read_for_review",
        "library_get_book",
    };

    // ------------------------------------------------------------------
    // A turn
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs one assistant turn: build the conversation and tools, loop over the
    /// model's tool calls, and return prose plus any suggestions / follow-up
    /// question / library changeset.
    /// </summary>
    public Task<AssistantTurnResponse> HandleTurnAsync(
        AssistantTurnRequest request,
        CancellationToken ct = default) =>
        HandleTurnAsync(request, activity: null, execution: null, ct);

    internal Task<AssistantTurnResponse> HandleTurnAsync(
        AssistantTurnRequest request,
        Func<AssistantTurnActivityDto, ValueTask>? activity,
        CancellationToken ct = default) =>
        HandleTurnAsync(request, activity, execution: null, ct);

    internal async Task<AssistantTurnResponse> HandleTurnAsync(
        AssistantTurnRequest request,
        Func<AssistantTurnActivityDto, ValueTask>? activity,
        AssistantTurnExecution? execution,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conversationKey = AssistantTurnIdentity.ConversationKey(
            string.IsNullOrWhiteSpace(request.ConversationId)
                ? request.ClientId
                : request.ConversationId);
        var turnId = AssistantTurnIdentity.TurnKey(request);

        if (ct.IsCancellationRequested)
        {
            return new AssistantTurnResponse(
                Reply: string.Empty,
                Acknowledgement: null,
                AnchorPrompt: null,
                Suggestions: [],
                Changeset: null,
                CapturedNoteId: null,
                ExecutedCapabilities: [],
                Sources: [],
                Error: new AssistantTurnErrorDto(
                    AssistantErrorCodes.TurnCancelled,
                    "Stopped."));
        }

        if (!string.IsNullOrWhiteSpace(request.ContinuationId))
        {
            return await ResumeCaptureContinuationAsync(
                request,
                conversationKey,
                turnId,
                activity,
                execution,
                ct);
        }

        var executionMeter = new AssistantExecutionMeter(_time);
        AiUsageLease? usageLease = null;
        var toolLoopDetector = new AssistantToolLoopDetector();
        var stopReason = AssistantTurnStopReason.SafetyCeiling;

        var capabilityByName = registry.All.ToDictionary(c => c.Name, StringComparer.Ordinal);

        var captureIntent = await _captureIntentGuard.EvaluateAsync(
            request.Message,
            ct);
        var captureSuppressed = captureIntent.SuppressCapture;
        var turnContext = captureIntent.ResolvedBook is { } clarifiedBook
            ? request.Context with
            {
                BookId = clarifiedBook.BookId.ToString(),
                BookTitle = clarifiedBook.BookTitle,
            }
            : request.Context;

        // The model sees the same canonical scope the server will enforce on
        // read tools. A source clarification can therefore become useful
        // immediately without being misclassified as a note.
        var messages = _conversation.BuildConversation(request with { Context = turnContext });
        var tools = _conversation.BuildTools()
            .Where(tool => !captureSuppressed
                || !string.Equals(tool.Name, CaptureCapability, StringComparison.Ordinal))
            .ToList();

        var suggestions = new List<AssistantSuggestionDto>();
        var reviewedNoteIds = new HashSet<Guid>();
        var inspectedTopicIds = new HashSet<Guid>();
        var topicsInspected = false;
        var executedCapabilities = new List<string>();
        var successfulCapabilities = new HashSet<string>(StringComparer.Ordinal);
        var sourceReferences = new List<AssistantSourceReferenceDto>();
        var evidenceReferences = new List<AssistantEvidenceReferenceDto>();
        var draft = new AssistantChangesetDraft(libraryChanges, library);
        // Set once this turn reads text the user did not write into it: book
        // text, notes, or a book's description. Changes worked out from such
        // text always wait for the user's review (prompt-injection guard).
        var readUntrustedContent = false;
        AssistantAnchorPromptDto? anchorPrompt = null;
        string? acknowledgement = null;
        string? finalContent = null;
        string? lastAssistantContent = null;
        string? capturedNoteId = null;
        AssistantTurnErrorDto? terminalError = null;
        var retrieval = new AssistantRetrievalTurnState(turnContext);
        // A quote looked up by its first and last words that did not resolve
        // to one passage leaves nothing to save this turn: the dictated words
        // are not the quote, and saving them would pass them off as one.
        var quoteLookupUnresolved = false;
        var earlierUserMessages = (request.History ?? [])
            .Where(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(message => message.Text)
            .Reverse()
            .ToList();
        if (captureIntent.ResolvedBook is { } clarifiedScope)
            retrieval.RecordResolvedBook(clarifiedScope);
        var mutationCompleted = false;
        var mutationFailed = false;
        // True when the most recent tool round consisted only of successful
        // canonical writes, i.e. the model had nothing left to do but reply.
        var lastRoundOnlySucceededMutations = false;

        var iterations = Math.Max(1, options.MaxToolIterations);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            if (ct.IsCancellationRequested)
            {
                stopReason = AssistantTurnStopReason.Cancelled;
                break;
            }

            // Per-turn execution ceilings (#406). Token/cost are evaluated before
            // spending another upstream call. Wall clock also supplies the provider
            // call with the remaining turn deadline, so one slow in-flight call
            // cannot run past the turn budget and fall through to the 90 s transport
            // timeout.
            var usageBeforeCall = executionMeter.Snapshot();
            var exceededCeiling = AssistantExecutionBudget.ExceededCeiling(usageBeforeCall, options);
            if (exceededCeiling is not null)
            {
                logger.LogDebug(
                    "Assistant turn stopped at the {Ceiling} execution ceiling.",
                    exceededCeiling);
                stopReason = AssistantTurnStopReason.SafetyCeiling;
                break;
            }

            CancellationTokenSource? deadlineTimer = null;
            CancellationTokenSource? turnDeadline = null;
            var upstreamCancellation = ct;
            if (options.MaxTurnElapsedMilliseconds > 0)
            {
                var remainingMilliseconds =
                    options.MaxTurnElapsedMilliseconds - usageBeforeCall.ElapsedMilliseconds;

                if (remainingMilliseconds <= 0)
                {
                    stopReason = AssistantTurnStopReason.SafetyCeiling;
                    break;
                }

                deadlineTimer = new CancellationTokenSource(
                    TimeSpan.FromMilliseconds(remainingMilliseconds),
                    _time);
                turnDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineTimer.Token);
                upstreamCancellation = turnDeadline.Token;
            }

            // A host can reserve usage before the first provider call. SelfHosted
            // uses a no-op accounting service.
            usageLease ??= await _usageAccounting.BeginLlmTurnAsync(ct);
            executionMeter.RecordUpstreamRequest();

            LlmCompletion completion;
            try
            {
                completion = await llm.CompleteAsync(
                    new LlmCompletionRequest(messages, tools, MaxResponseTokens),
                    upstreamCancellation);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stopReason = AssistantTurnStopReason.Cancelled;
                break;
            }
            catch (OperationCanceledException) when (turnDeadline?.IsCancellationRequested == true)
            {
                logger.LogDebug(
                    "Assistant turn stopped at the wall-clock execution ceiling during an upstream call.");
                stopReason = AssistantTurnStopReason.SafetyCeiling;
                break;
            }
            catch (LlmException)
            {
                var metrics = executionMeter.Finish(AssistantTurnStopReason.ProviderError);
                LogExecutionMetrics(metrics);
                await CompleteUsageAsync(usageLease, metrics);
                throw;
            }
            finally
            {
                turnDeadline?.Dispose();
                deadlineTimer?.Dispose();
            }

            executionMeter.RecordCompletion(completion);

            if (ct.IsCancellationRequested)
            {
                stopReason = AssistantTurnStopReason.Cancelled;
                break;
            }

            finalContent = completion.Content;
            if (!string.IsNullOrWhiteSpace(completion.Content))
            {
                lastAssistantContent = completion.Content;
            }

            if (completion.ToolCalls.Count == 0)
            {
                // A plain answer (including the pool's empty-content/"length"
                // outcome, which is data: it is returned as-is, never a 500).
                stopReason = AssistantTurnStopReason.Completed;
                break;
            }

            // The in-process Nostos tools are deterministic for an unchanged
            // request. If the model asks for the same tool batch with equivalent
            // arguments on the immediately following round, executing it again
            // adds no information and can duplicate writes because each round
            // intentionally has a distinct receipt key.
            if (toolLoopDetector.IsImmediateRepeat(completion.ToolCalls))
            {
                stopReason = AssistantTurnStopReason.RepeatedToolLoop;
                break;
            }

            messages.Add(LlmMessage.Assistant(
                completion.Content,
                completion.ToolCalls,
                completion.ProviderState));

            var roundSucceededMutations = 0;
            foreach (var call in completion.ToolCalls)
            {
                if (ct.IsCancellationRequested)
                {
                    stopReason = AssistantTurnStopReason.Cancelled;
                    break;
                }

                if (!capabilityByName.TryGetValue(call.Name, out var capability))
                {
                    messages.Add(LlmMessage.Tool(call.Id, ToolJson(new
                    {
                        status = "unknown_capability",
                        message = $"No assistant capability named '{call.Name}' exists.",
                    })));
                    continue;
                }

                JsonElement args;
                var implicitReaderScope = false;
                AssistantCapturePreparation? capture = null;

                if (capability.Trust == AssistantTrustClass.Capture
                    && string.Equals(capability.Name, CaptureCapability, StringComparison.Ordinal))
                {
                    if (captureSuppressed)
                    {
                        // Durable writes must not depend on the model correctly
                        // interpreting an explicit "do not save" instruction or
                        // a short source clarification. The tool is also omitted
                        // from the advertised catalogue for these turns; this is
                        // the server-side backstop if a provider still emits it.
                        lastAssistantContent = null;
                        messages.Add(LlmMessage.Tool(call.Id, ToolJson(new
                        {
                            status = "capture_rejected",
                            message = "This turn is conversational control, not a capture. Nothing was saved. Do not call notes_capture again for this turn.",
                        })));
                        continue;
                    }

                    if (quoteLookupUnresolved
                        && string.IsNullOrWhiteSpace(ReadString(
                            AssistantToolArguments.ParseElement(call.ArgumentsJson), "quoteRef")))
                    {
                        lastAssistantContent = null;
                        messages.Add(LlmMessage.Tool(call.Id, ToolJson(new
                        {
                            status = "capture_rejected",
                            message = "book_text_find_quote did not find one passage, so there is nothing to save. Nothing was saved. Tell the user what book_text_find_quote said.",
                        })));
                        continue;
                    }

                    capture = await _capturePolicy.PrepareAsync(
                        call.ArgumentsJson,
                        turnContext,
                        request.Message,
                        fallbackContent: null,
                        ct,
                        earlierUserMessages);

                    if (capture.Prompt is { } prompt)
                    {
                        var storedContinuation = continuations.Create(
                            _owner.Key,
                            conversationKey,
                            turnId,
                            prompt.Kind,
                            call.ArgumentsJson,
                            request.Message,
                            turnContext);

                        anchorPrompt = prompt with
                        {
                            ContinuationId = storedContinuation.ContinuationId,
                        };
                        messages.Add(LlmMessage.Tool(call.Id, ToolJson(new
                        {
                            status = capture.PromptStatus,
                            kind = prompt.Kind,
                            question = prompt.Question,
                            continuationId = storedContinuation.ContinuationId,
                            message = capture.PromptMessage,
                        })));
                        continue;
                    }

                    args = capture.Arguments!.Value;
                }
                else
                {
                    args = AssistantToolArguments.ParseElement(call.ArgumentsJson);
                    // A rewrite is the user's to ask for, never the model's to
                    // offer: without a request in their own words, nothing changes.
                    // Which notes it may touch is the note service's check.
                    if (string.Equals(capability.Name, RewriteCaptureCapability, StringComparison.Ordinal)
                        && !AssistantRewriteIntent.Requested(request.Message))
                    {
                        messages.Add(LlmMessage.Tool(call.Id, ToolJson(new
                        {
                            status = "rewrite_rejected",
                            message = "The user did not ask for a rewrite in this message. Nothing was changed.",
                        })));
                        continue;
                    }

                    // Like a capture, the open book is the app's: the quote is
                    // looked up in the book the user is reading, never one the
                    // model went and found.
                    if (string.Equals(capability.Name, FindQuoteCapability, StringComparison.Ordinal)
                        && Guid.TryParse(turnContext.BookId, out var openBookId))
                    {
                        var findArgs = AssistantToolArguments.ParseObject(call.ArgumentsJson);
                        findArgs["bookId"] = openBookId.ToString();
                        args = JsonSerializer.SerializeToElement(findArgs);
                    }

                    if (string.Equals(capability.Name, BookTextCapability, StringComparison.Ordinal)
                        || string.Equals(capability.Name, KnowledgeSearchCapability, StringComparison.Ordinal))
                    {
                        args = retrieval.ApplyImplicitBookScope(
                            args,
                            request.History,
                            out implicitReaderScope);
                    }
                }

                // The receipt key identifies the logical mutation, not its
                // position in the model's tool sequence. A retried delivery is
                // free to reorder, add, or drop calls, so a positional key can
                // miss an earlier write (a duplicate) or land on a different
                // command's receipt (a wrong replay). One logical operation —
                // same TurnId, capability and effective arguments — reuses one
                // identity and replays instead of writing again; genuinely
                // different mutations stay distinct. Capture fingerprints the
                // server-prepared arguments, so model-chosen book or anchor
                // noise cannot change the identity.
                var toolContext = new AssistantToolContext(
                    ClientId: conversationKey,
                    IdempotencyKey: AssistantMutationIdentity.Key(turnId, capability.Name, args),
                    Changeset: draft);

                if (AssistantTurnActivities.ForCapability(capability) is { } turnActivity
                    && activity is not null)
                {
                    await activity(turnActivity);
                }

                var mutation = capability.Trust is
                    AssistantTrustClass.Capture or AssistantTrustClass.Act;

                // Stop and canonical write-start share one lifecycle gate.
                // If Stop wins, no write begins. If the mutation lease wins,
                // that write is already considered in-flight and is allowed to
                // reach its truthful terminal state before the turn stops.
                AssistantTurnMutationLease? mutationLease = null;
                if (mutation)
                {
                    mutationLease = execution?.TryBeginMutation();
                    if ((execution is not null && mutationLease is null)
                        || (execution is null && ct.IsCancellationRequested))
                    {
                        stopReason = AssistantTurnStopReason.Cancelled;
                        break;
                    }
                }

                AssistantToolResult result;
                try
                {
                    result = string.Equals(capability.Name, TopicProposalCapability, StringComparison.Ordinal)
                        && !ProposalUsesReadEvidence(
                            args, turnContext.BrainReviewNoteId,
                            reviewedNoteIds, inspectedTopicIds, topicsInspected)
                        ? AssistantToolResult.Fail(
                            AssistantErrorCodes.InvalidArguments,
                            "Read the target note and existing topic evidence in this turn before proposing those IDs.")
                        : await registry.InvokeAsync(
                            capability.Name,
                            args,
                            toolContext,
                            mutation ? CancellationToken.None : ct);

                    // One bounded server-owned recovery protects retrieval from a
                    // weak query rewrite. Keep the model's exact scope, but when
                    // a ready source returned no passage, retry once with the
                    // distinctive lexical terms the user actually typed.
                    if (result.Success
                        && string.Equals(capability.Name, BookTextCapability, StringComparison.Ordinal)
                        && result.Data is { } bookTextData
                        && retrieval.BuildBookTextFallbackArguments(
                            args,
                            request.Message,
                            bookTextData) is { } fallbackArgs)
                    {
                        result = await registry.InvokeAsync(
                            capability.Name,
                            fallbackArgs,
                            toolContext,
                            ct);
                        args = fallbackArgs;
                    }
                    else if (result.Success
                        && string.Equals(capability.Name, KnowledgeSearchCapability, StringComparison.Ordinal)
                        && result.Data is { } knowledgeData
                        && retrieval.BuildKnowledgeScopeFallbackArguments(
                            args,
                            knowledgeData,
                            implicitReaderScope) is { } widenedArgs)
                    {
                        // Keep the original scoped args for turn-state truth:
                        // this turn did attempt a reader-scoped lookup before
                        // the bounded whole-library fallback.
                        result = await registry.InvokeAsync(
                            capability.Name,
                            widenedArgs,
                            toolContext,
                            ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    stopReason = AssistantTurnStopReason.Cancelled;
                    break;
                }
                finally
                {
                    mutationLease?.Dispose();
                }

                // Evidence joins the turn's list before the model reads it, so
                // each item can carry its citation number (issue #1002).
                var evidenceBeforeCall = evidenceReferences.Count;
                var modelResult = result;
                if (result.Success && capability.Trust == AssistantTrustClass.Suggest)
                {
                    var callEvidence = AssistantEvidenceProjection
                        .ExtractEvidence(capability.Name, result.Data)
                        .ToList();
                    AssistantEvidenceProjection.MergeEvidenceReferences(
                        evidenceReferences,
                        callEvidence,
                        MaxEvidenceArtifacts);
                    if (AssistantCitations.Annotate(
                            capability.Name, result.Data, callEvidence, evidenceReferences) is { } annotated)
                        modelResult = result with { Data = annotated };
                }

                messages.Add(LlmMessage.Tool(call.Id, ToolJson(modelResult)));

                if (result.Success)
                {
                    successfulCapabilities.Add(capability.Name);
                }

                if (result.Success && mutation)
                {
                    mutationCompleted = true;
                    roundSucceededMutations++;
                    terminalError = null;
                }
                else if (!result.Success && mutation)
                {
                    mutationFailed = true;
                    terminalError = new AssistantTurnErrorDto(
                        result.ErrorCode ?? AssistantErrorCodes.NotFound,
                        result.ErrorMessage ?? "The requested change could not be completed.");
                }

                if (result.Success && capability.Trust == AssistantTrustClass.Act)
                {
                    executedCapabilities.Add(capability.Name);
                }

                if (result.Success
                    && capability.Trust == AssistantTrustClass.Suggest)
                {
                    retrieval.ObserveResult(capability.Name, args, result.Data);
                    readUntrustedContent |= UntrustedContentCapabilities.Contains(capability.Name);
                }

                if (result.Success && capability.Trust == AssistantTrustClass.Suggest)
                {
                    if (string.Equals(capability.Name, "notes_read_for_review", StringComparison.Ordinal)
                        && Guid.TryParse(ReadString(args, "noteId"), out var readNoteId))
                        reviewedNoteIds.Add(readNoteId);

                    if (string.Equals(capability.Name, "topics_list", StringComparison.Ordinal)
                        || string.Equals(capability.Name, "topics_search", StringComparison.Ordinal))
                    {
                        topicsInspected = true;
                        if (result.Data is { } topicRows && topicRows.ValueKind == JsonValueKind.Array)
                            foreach (var row in topicRows.EnumerateArray())
                                if (row.ValueKind == JsonValueKind.Object
                                    && Guid.TryParse(ReadString(row, "id"), out var readTopicId))
                                    inspectedTopicIds.Add(readTopicId);
                    }

                    MergeSuggestions(suggestions, ExtractSuggestions(capability.Name, result.Data));

                    if (string.Equals(capability.Name, BookTextCapability, StringComparison.Ordinal))
                    {
                        var sources = AssistantEvidenceProjection
                            .ExtractBookTextSources(result.Data)
                            .ToList();
                        sourceReferences.AddRange(sources);
                        retrieval.RecordSources(sources);
                    }
                    else if (string.Equals(capability.Name, KnowledgeSearchCapability, StringComparison.Ordinal))
                    {
                        var sources = AssistantEvidenceProjection
                            .ExtractKnowledgeSearchSources(result.Data)
                            .ToList();
                        sourceReferences.AddRange(sources);
                        retrieval.RecordSources(sources);
                    }
                    else if (string.Equals(capability.Name, KnowledgeReadCapability, StringComparison.Ordinal))
                    {
                        var sources = AssistantEvidenceProjection
                            .ExtractKnowledgeReadSources(result.Data)
                            .ToList();
                        sourceReferences.AddRange(sources);
                        retrieval.RecordSources(sources);
                        if (evidenceReferences.Count > evidenceBeforeCall)
                            retrieval.MarkExactEvidenceAvailable();
                    }
                }

                if (string.Equals(capability.Name, FindQuoteCapability, StringComparison.Ordinal))
                {
                    quoteLookupUnresolved = !(result.Success
                        && result.Data is { ValueKind: JsonValueKind.Object } found
                        && string.Equals(ReadString(found, "status"), "found", StringComparison.Ordinal));
                }

                if (result.Success
                    && capability.Trust == AssistantTrustClass.Capture
                    && string.Equals(capability.Name, CaptureCapability, StringComparison.Ordinal))
                {
                    acknowledgement = AssistantCapturePolicy.BuildAcknowledgement(
                        capture!.BookTitle, capture.QuoteFidelity, Rewritten(result.Data));
                    capturedNoteId = ReadNoteId(result.Data);
                }

                if (ct.IsCancellationRequested)
                {
                    stopReason = AssistantTurnStopReason.Cancelled;
                    break;
                }
            }

            if (stopReason == AssistantTurnStopReason.Cancelled)
                break;

            lastRoundOnlySucceededMutations = roundSucceededMutations == completion.ToolCalls.Count;

            // Asking for deterministic capture input ends this turn. The user's
            // actual answer arrives as the next Message plus continuation id; it
            // is never hidden in context or replayed as the original message.
            if (anchorPrompt is not null)
            {
                stopReason = AssistantTurnStopReason.UserInputRequired;
                break;
            }

        }

        // The turn's library changes, judged as one changeset. A stopped turn
        // applies nothing it staged; a turn cut short by a guard can only ever
        // offer its changes for review, never apply them unseen.
        // A retried delivery of a turn whose changes already applied reports
        // them again, whether or not the model repeated its tool calls.
        var changeOutcome = AssistantChangesetOutcome.None;
        if (stopReason != AssistantTurnStopReason.Cancelled)
        {
            // The server cannot tell whether a model stopped by a guard had
            // finished staging, so its changes are only ever offered for review.
            var incomplete = stopReason is
                AssistantTurnStopReason.SafetyCeiling or
                AssistantTurnStopReason.RepeatedToolLoop;
            using var applyLease = draft.IsEmpty ? null : execution?.TryBeginMutation();
            if (!draft.IsEmpty && execution is not null && applyLease is null)
            {
                stopReason = AssistantTurnStopReason.Cancelled;
            }
            else
            {
                changeOutcome = await changesets.FinishTurnAsync(
                    conversationKey, turnId, draft, readUntrustedContent, incomplete, ct);
                if (changeOutcome.Applied)
                    mutationCompleted = true;
            }
        }

        var ceilingOutcome = CeilingOutcome.None;
        if (stopReason == AssistantTurnStopReason.Cancelled)
        {
            terminalError = new AssistantTurnErrorDto(
                AssistantErrorCodes.TurnCancelled,
                mutationCompleted
                    ? "Stopped. Changes that already completed remain applied."
                    : "Stopped.");
        }
        else if (stopReason == AssistantTurnStopReason.SafetyCeiling)
        {
            // The ceiling is a runaway guard, not a rollback: a committed write
            // must never be reported as a failed turn (#647).
            ceilingOutcome = !mutationCompleted
                ? CeilingOutcome.NothingCommitted
                : !mutationFailed && lastRoundOnlySucceededMutations
                    ? CeilingOutcome.Completed
                    : CeilingOutcome.PartiallyCompleted;

            terminalError = ceilingOutcome switch
            {
                CeilingOutcome.Completed => null,
                CeilingOutcome.PartiallyCompleted => new AssistantTurnErrorDto(
                    AssistantErrorCodes.PartiallyCompleted,
                    PartiallyCompletedReply),
                _ => new AssistantTurnErrorDto(
                    AssistantErrorCodes.ExecutionBudgetExhausted,
                    "This turn reached its execution limit before it could finish."),
            };
        }
        else if (terminalError is null)
        {
            terminalError = retrieval.TerminalFailure(request.Message);
        }

        // A review card states that the turn stopped early, so the stop is not
        // also reported as a failure beside it.
        if (changeOutcome.AwaitingReview)
            terminalError = null;

        // Server-known boundaries outrank model narration. A provider can attach
        // prose such as "Saved" or "Done" to a tool call even when Nostos knows
        // it still needs input/approval or stopped at an execution guard.
        var stoppedByExecutionGuard = stopReason is
            AssistantTurnStopReason.SafetyCeiling or
            AssistantTurnStopReason.RepeatedToolLoop;

        string reply;
        if (stopReason == AssistantTurnStopReason.Cancelled)
        {
            reply = string.Empty;
        }
        else if (anchorPrompt is not null)
        {
            reply = anchorPrompt.Question;
        }
        else if (changeOutcome.AwaitingReview)
        {
            reply = AssistantChangesetCoordinator.ReviewReply;
        }
        else if (changeOutcome.Error is { } changeError)
        {
            terminalError = changeError;
            reply = changeError.Message;
        }
        else if (stoppedByExecutionGuard && acknowledgement is null)
        {
            reply = ceilingOutcome switch
            {
                CeilingOutcome.Completed => CompletedBeforeCeilingReply,
                CeilingOutcome.PartiallyCompleted => PartiallyCompletedReply,
                _ => IncompleteTurnReply,
            };
        }
        else
        {
            reply = lastAssistantContent?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reply) && acknowledgement is null)
            {
                reply = IncompleteTurnReply;
            }
        }

        // A model may narrate a write or empty search result without ever
        // executing the capability. Only server-observed successful calls can
        // support those completion claims; otherwise fail closed with an honest
        // typed turn result instead of presenting invented state as fact.
        if (AssistantCompletionClaimGuard.Evaluate(reply, successfulCapabilities) is { } unverifiedClaim)
        {
            logger.LogWarning(
                "Assistant completion-claim guard replaced an unverified {ClaimKind} claim; reply content is not logged.",
                unverifiedClaim.Kind);
            reply = unverifiedClaim.ReplacementReply;
            terminalError ??= new AssistantTurnErrorDto(
                AssistantErrorCodes.UnverifiedCompletionClaim,
                unverifiedClaim.ReplacementReply);
        }

        // The conversational reply is the only text the guard may rewrite. Note
        // content, quotes, processing results and plan summaries are the user's
        // own words and must never be touched, so this runs here and nowhere else.
        var selfAssertedVendor = AssistantIdentityGuard.MatchVendorSelfAssertion(reply);
        if (selfAssertedVendor is not null)
        {
            // The matched term only: never the reply, the user's message, or the
            // content that was removed.
            logger.LogWarning(
                "Assistant identity guard replaced a self-asserted vendor/model mention; reply content is not logged.");
            reply = AssistantIdentityGuard.Apply(reply);
        }

        // A citation marker only survives when it points at this turn's evidence.
        reply = AssistantCitations.Normalize(reply, evidenceReferences.Count);

        var finalMetrics = executionMeter.Finish(stopReason);
        LogExecutionMetrics(finalMetrics);
        await CompleteUsageAsync(usageLease, finalMetrics);

        logger.LogDebug(
            "Assistant turn handled: {Suggestions} suggestion(s), changeset {Changeset}, anchor prompt {HasPrompt}.",
            suggestions.Count,
            changeOutcome.Changeset?.Status ?? "none",
            anchorPrompt is not null);

        var resolvedBook = retrieval.ResolvedBook;

        return new AssistantTurnResponse(
            reply,
            acknowledgement,
            anchorPrompt,
            suggestions,
            changeOutcome.Changeset,
            capturedNoteId,
            executedCapabilities,
            sourceReferences,
            terminalError,
            evidenceReferences,
            resolvedBook);
    }

    private enum CeilingOutcome
    {
        None,
        NothingCommitted,
        Completed,
        PartiallyCompleted,
    }

    private Task CompleteUsageAsync(
        AiUsageLease? lease,
        AssistantExecutionMetrics metrics) =>
        _usageAccounting.CompleteLlmTurnAsync(
            lease,
            new LlmProviderUsage(
                metrics.UpstreamCallCount,
                metrics.PromptTokens,
                metrics.OutputTokens,
                metrics.ThinkingTokens,
                metrics.ReportedTotalTokens,
                metrics.ToolLoopIterations,
                metrics.StopReason.ToString(),
                metrics.ProviderFinishReason),
            CancellationToken.None);

    private void LogExecutionMetrics(AssistantExecutionMetrics metrics)
    {
        // Content-free by design: this is safe to promote into Cloud usage
        // accounting later without storing prompts, replies, tool arguments or
        // private library results.
        logger.LogDebug(
            "Assistant turn execution: {UpstreamCalls} upstream call(s), {ToolCalls} tool call(s), " +
            "{ToolLoopIterations} tool-loop iteration(s), prompt tokens {PromptTokens}, " +
            "output tokens {OutputTokens}, thinking tokens {ThinkingTokens}, " +
            "reported total tokens {ReportedTotalTokens}, elapsed {ElapsedMilliseconds} ms, " +
            "stop {StopReason}, provider finish {ProviderFinishReason}.",
            metrics.UpstreamCallCount,
            metrics.ToolCallCount,
            metrics.ToolLoopIterations,
            metrics.PromptTokens,
            metrics.OutputTokens,
            metrics.ThinkingTokens,
            metrics.ReportedTotalTokens,
            metrics.ElapsedMilliseconds,
            metrics.StopReason,
            metrics.ProviderFinishReason ?? "(none)");
    }

    // ------------------------------------------------------------------
    // Deterministic capture continuation — no model authority
    // ------------------------------------------------------------------

    private async Task<AssistantTurnResponse> ResumeCaptureContinuationAsync(
        AssistantTurnRequest request,
        string conversationKey,
        string turnId,
        Func<AssistantTurnActivityDto, ValueTask>? activity,
        AssistantTurnExecution? execution,
        CancellationToken ct)
    {
        var continuationId = request.ContinuationId!.Trim();

        // A lost HTTP response is replayed by the same logical TurnId before we
        // inspect active state. Terminal captures may already have removed that
        // state, but their receipt remains bounded for a short retry window.
        if (continuations.TryGetReceipt(
                _owner.Key,
                continuationId,
                conversationKey,
                turnId,
                out var replay))
        {
            return replay;
        }

        var lookup = continuations.Find(_owner.Key, continuationId, conversationKey);
        if (lookup.Status == AssistantContinuationLookupStatus.ConversationMismatch)
        {
            return ContinuationFailure(
                AssistantErrorCodes.ContinuationMismatch,
                "That follow-up belongs to a different Ask Nostos conversation. Nothing was changed.");
        }

        if (lookup.Status != AssistantContinuationLookupStatus.Found
            || lookup.Continuation is null)
        {
            return ContinuationFailure(
                AssistantErrorCodes.ContinuationNotFound,
                "That follow-up has expired, was superseded, or no longer exists. Nothing was changed.");
        }

        var stored = lookup.Continuation;
        if (request.ContinuationSkipped
            && string.Equals(stored.Kind, BookPromptKind, StringComparison.Ordinal))
        {
            return ContinuationFailure(
                AssistantErrorCodes.ContinuationAnswerRequired,
                "A book is required before this capture can be saved. Nothing was changed.");
        }

        if (!request.ContinuationSkipped && string.IsNullOrWhiteSpace(request.Message))
        {
            return ContinuationFailure(
                AssistantErrorCodes.ContinuationAnswerRequired,
                "This follow-up needs an answer before the capture can continue. Nothing was changed.");
        }

        var resumedContext = string.Equals(
                stored.Kind,
                BookPromptKind,
                StringComparison.Ordinal)
            ? await _capturePolicy.ApplyBookContinuationAnswerAsync(
                stored.Context,
                request.Message,
                ct)
            : _capturePolicy.ApplyContinuationAnswer(
                stored.Context,
                stored.Kind,
                request.Message,
                request.ContinuationSkipped);

        var capture = await _capturePolicy.PrepareAsync(
            stored.ArgumentsJson,
            resumedContext,
            stored.OriginalMessage,
            stored.OriginalMessage,
            ct);

        if (capture.Prompt is { } prompt)
        {
            var bookResolutionAttempts = stored.BookResolutionAttempts;
            if (string.Equals(stored.Kind, BookPromptKind, StringComparison.Ordinal)
                && string.Equals(prompt.Kind, BookPromptKind, StringComparison.Ordinal))
            {
                bookResolutionAttempts++;
                if (bookResolutionAttempts >= MaxBookContinuationAttempts)
                {
                    var failure = ContinuationFailure(
                        AssistantErrorCodes.NotFound,
                        "I still could not match that to a book in your library. Nothing was saved.");
                    continuations.Complete(stored, turnId, failure);
                    return failure;
                }
            }

            continuations.Update(
                stored,
                prompt.Kind,
                resumedContext,
                bookResolutionAttempts);
            var nextPrompt = prompt with { ContinuationId = continuationId };
            var response = new AssistantTurnResponse(
                Reply: prompt.Question,
                Acknowledgement: null,
                AnchorPrompt: nextPrompt,
                Suggestions: [],
                Changeset: null,
                CapturedNoteId: null,
                ExecutedCapabilities: [],
                Sources: [],
                Error: null);

            continuations.RecordReceipt(
                _owner.Key,
                continuationId,
                conversationKey,
                turnId,
                response);
            return response;
        }

        // The mutation belongs to the original capture turn, not to whichever
        // follow-up turn happened to supply the last missing field. This keeps
        // one canonical mutation identity across multi-stage continuations while
        // each follow-up still has its own TurnId for conversation/retry truth.
        var toolContext = new AssistantToolContext(
            ClientId: conversationKey,
            IdempotencyKey: AssistantMutationIdentity.Key(
                stored.OriginalTurnId,
                CaptureCapability,
                capture.Arguments!.Value));

        if (ct.IsCancellationRequested)
        {
            return ContinuationFailure(
                AssistantErrorCodes.TurnCancelled,
                "Stopped.");
        }

        if (activity is not null)
            await activity(new AssistantTurnActivityDto("saving_note", "Saving your note…"));

        AssistantTurnMutationLease? mutationLease = execution?.TryBeginMutation();
        if ((execution is not null && mutationLease is null)
            || (execution is null && ct.IsCancellationRequested))
        {
            return ContinuationFailure(
                AssistantErrorCodes.TurnCancelled,
                "Stopped.");
        }

        // Same atomic write boundary as the ordinary tool path. From this
        // lifecycle transition onward, a Stop request may end subsequent work
        // but cannot make this canonical write ambiguous.
        AssistantToolResult result;
        try
        {
            result = await registry.InvokeAsync(
                CaptureCapability,
                capture.Arguments!.Value,
                toolContext,
                CancellationToken.None);
        }
        finally
        {
            mutationLease?.Dispose();
        }

        if (!result.Success)
        {
            var failure = ContinuationFailure(
                result.ErrorCode ?? AssistantErrorCodes.NotFound,
                result.ErrorMessage ?? "The capture could not be completed.");
            continuations.RecordReceipt(
                _owner.Key,
                continuationId,
                conversationKey,
                turnId,
                failure);
            return failure;
        }

        var completed = new AssistantTurnResponse(
            Reply: string.Empty,
            Acknowledgement: AssistantCapturePolicy.BuildAcknowledgement(
                capture.BookTitle,
                capture.QuoteFidelity,
                Rewritten(result.Data)),
            AnchorPrompt: null,
            Suggestions: [],
            Changeset: null,
            CapturedNoteId: ReadNoteId(result.Data),
            ExecutedCapabilities: [],
            Sources: [],
            Error: ct.IsCancellationRequested
                ? new AssistantTurnErrorDto(
                    AssistantErrorCodes.TurnCancelled,
                    "Stopped. The saved note remains saved.")
                : null);

        continuations.Complete(stored, turnId, completed);
        return completed;
    }

    private static AssistantTurnResponse ContinuationFailure(string code, string message) =>
        new(
            Reply: message,
            Acknowledgement: null,
            AnchorPrompt: null,
            Suggestions: [],
            Changeset: null,
            CapturedNoteId: null,
            ExecutedCapabilities: [],
            Sources: [],
            Error: new AssistantTurnErrorDto(code, message));

    // ------------------------------------------------------------------
    // Suggestions (non-mutating)
    // ------------------------------------------------------------------

    private static bool ProposalUsesReadEvidence(
        JsonElement args,
        string? currentReviewNoteId,
        HashSet<Guid> reviewedNoteIds,
        HashSet<Guid> inspectedTopicIds,
        bool topicsInspected)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !Guid.TryParse(ReadString(args, "noteId"), out var noteId)
            || !reviewedNoteIds.Contains(noteId)
            || (Guid.TryParse(currentReviewNoteId, out var currentNoteId) && currentNoteId != noteId)
            || !topicsInspected
            || !args.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
            return false;

        return candidates.EnumerateArray().All(candidate =>
            candidate.ValueKind == JsonValueKind.Object
            && Guid.TryParse(ReadString(candidate, "topicId"), out var topicId)
            && inspectedTopicIds.Contains(topicId));
    }

    /// <summary>
    /// Only an explicit, validated proposal capability can produce suggestions.
    /// Ordinary topic reads are evidence, never implicit clickable actions.
    /// </summary>
    private static IEnumerable<AssistantSuggestionDto> ExtractSuggestions(
        string capabilityName,
        JsonElement? data)
    {
        if (!string.Equals(capabilityName, TopicProposalCapability, StringComparison.Ordinal)
            || data is not { } element
            || element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var noteId = ReadString(element, "noteId");
        foreach (var item in candidates.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var reason = ReadString(item, "reason");
            if (string.IsNullOrWhiteSpace(reason)) continue;

            yield return new AssistantSuggestionDto(
                "topic",
                name,
                reason,
                ReadString(item, "id"),
                noteId);
        }
    }

    /// <summary>
    /// Adds incoming suggestions to the response, de-duplicated by identity and
    /// capped for topics. The cap is what makes the review a small set rather
    /// than a dump of the library; de-duplication handles repeated explicit
    /// proposals in one turn. Nothing here creates or mutates anything.
    /// </summary>
    private static void MergeSuggestions(
        List<AssistantSuggestionDto> target,
        IEnumerable<AssistantSuggestionDto> incoming)
    {
        foreach (var suggestion in incoming)
        {
            var key = suggestion.Value ?? suggestion.Label;
            if (target.Any(existing =>
                    string.Equals(existing.Kind, suggestion.Kind, StringComparison.Ordinal)
                    && string.Equals(existing.Value ?? existing.Label, key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (string.Equals(suggestion.Kind, "topic", StringComparison.Ordinal)
                && target.Count(existing => string.Equals(existing.Kind, "topic", StringComparison.Ordinal))
                    >= MaxTopicSuggestions)
            {
                continue;
            }

            target.Add(suggestion);
        }
    }

    // ------------------------------------------------------------------
    // Small JSON helpers
    // ------------------------------------------------------------------

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// The id of the note a successful <c>notes_capture</c> produced, read from
    /// the canonical result envelope. Null when the shape is not what we expect:
    /// the surface then simply does not offer its raw-transcript affordance.
    /// </summary>
    private static string? ReadNoteId(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        return element.TryGetProperty("value", out var value)
            && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
    }

    /// <summary>
    /// True when the saved note is a rewrite of the user's words, read from the
    /// stored note: a rewrite that fell back to the original reports verbatim.
    /// </summary>
    private static bool Rewritten(JsonElement? data) =>
        data is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty("value", out var value)
        && value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("processingMode", out var mode)
        && mode.ValueKind == JsonValueKind.String
        && !ThoughtProcessingModes.IsVerbatim(mode.GetString());

    private static string ToolJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

}
