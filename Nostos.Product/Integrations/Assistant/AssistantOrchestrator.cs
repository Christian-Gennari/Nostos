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
///
/// <para>
/// The turn is split along those seams (issue #1598):
/// <see cref="AssistantTurnClocks"/> owns the two wall clocks,
/// <see cref="AssistantTurnMetering"/> the usage lease, metrics and provider
/// failure settlement, <see cref="AssistantCapabilityDispatcher"/> every
/// per-call guard, argument rewrite and bounded recovery, and
/// <see cref="AssistantTurnFinalizer"/> the ending, the reply and its guards.
/// This class keeps the turn's own observations and the loop that ties the four
/// together.
/// </para>
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
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IAssistantOwner _owner = owner ?? SingleAssistantOwner.Instance;
    private readonly AssistantConversationBuilder _conversation =
        new(registry, new AssistantContextPacker(options));
    private readonly AssistantCapturePolicy _capturePolicy = new(library);
    private readonly AssistantCaptureIntentGuard _captureIntentGuard = new(library);
    private readonly AssistantTurnMetering _metering = new(
        usageAccounting ?? NoOpAiUsageAccountingService.Instance,
        logger);
    private readonly AssistantTurnClocks _clocks = new(
        options,
        timeProvider ?? TimeProvider.System,
        logger);
    private readonly AssistantTurnFinalizer _finalizer = new(changesets, logger);

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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads that return text the user did not write in this conversation:
    /// imported book text, notes (which include imported highlights), and book
    /// descriptions from metadata providers. Any of them can carry instructions.
    /// </summary>
    private static readonly HashSet<string> UntrustedContentCapabilities = new(StringComparer.Ordinal)
    {
        AssistantCapabilityNames.BookTextSearch,
        AssistantCapabilityNames.FindQuote,
        AssistantCapabilityNames.KnowledgeSearch,
        AssistantCapabilityNames.KnowledgeRead,
        "knowledge_overview",
        AssistantCapabilityNames.NotesListForBook,
        AssistantCapabilityNames.NotesSearch,
        "notes_list_unlinked",
        "notes_read_for_review",
        "library_get_book",
        "topics_get",
        "writings_read",
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

        var capabilityByName = registry.For(AssistantSurface.AskNostos)
            .ToDictionary(c => c.Name, StringComparer.Ordinal);

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
                || !string.Equals(tool.Name, AssistantCapabilityNames.Capture, StringComparison.Ordinal))
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
        string? lastAssistantContent = null;
        string? capturedNoteId = null;
        AssistantTurnErrorDto? terminalError = null;
        var retrieval = new AssistantRetrievalTurnState(turnContext);
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

        var dispatcher = new AssistantCapabilityDispatcher(
            registry,
            _capturePolicy,
            continuations,
            draft,
            retrieval,
            turnContext,
            request.History,
            request.Message,
            earlierUserMessages,
            captureSuppressed,
            _owner.Key,
            conversationKey,
            turnId,
            proposalUsesReadEvidence: args => AssistantTopicProposals.UsesReadEvidence(
                args,
                turnContext.BrainReviewNoteId,
                reviewedNoteIds,
                inspectedTopicIds,
                topicsInspected),
            activity);

        var iterations = Math.Max(1, options.MaxToolIterations);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            if (ct.IsCancellationRequested)
            {
                stopReason = AssistantTurnStopReason.Cancelled;
                break;
            }

            // The turn's clocks (#406, #1561) live in AssistantTurnClocks: a
            // per-request timeout inside a whole-turn budget, both on the turn's
            // TimeProvider. The ceiling is checked before anything is spent, so
            // a reached budget never buys another upstream call.
            var usageBeforeCall = executionMeter.Snapshot();
            if (_clocks.Open(usageBeforeCall) is not { } window)
            {
                stopReason = AssistantTurnStopReason.SafetyCeiling;
                break;
            }

            // A host can reserve usage before the first provider call. SelfHosted
            // uses a no-op accounting service.
            usageLease ??= await _metering.BeginAsync(ct);
            executionMeter.RecordUpstreamRequest();

            AssistantUpstreamResult upstream;
            using (var call = _clocks.Begin(window, ct))
            {
                upstream = await call.CompleteAsync(
                    llm,
                    new LlmCompletionRequest(messages, tools, MaxResponseTokens));
            }

            if (upstream.Outcome == AssistantUpstreamOutcome.Cancelled)
            {
                stopReason = AssistantTurnStopReason.Cancelled;
                break;
            }

            if (upstream.Outcome == AssistantUpstreamOutcome.TurnCeilingReached)
            {
                stopReason = AssistantTurnStopReason.SafetyCeiling;
                break;
            }

            if (upstream.Outcome is AssistantUpstreamOutcome.RequestTimedOut
                or AssistantUpstreamOutcome.ProviderFailed)
            {
                throw await _metering.FailAsync(
                    upstream.Failure!,
                    executionMeter,
                    usageLease,
                    CommittedOutcomesOrNull(
                        mutationCompleted, acknowledgement, capturedNoteId,
                        executedCapabilities, sourceReferences, evidenceReferences,
                        retrieval.ResolvedBook));
            }

            var completion = upstream.Completion!;
            executionMeter.RecordCompletion(completion);

            if (ct.IsCancellationRequested)
            {
                stopReason = AssistantTurnStopReason.Cancelled;
                break;
            }

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

                var dispatched = await dispatcher.DispatchAsync(call, capability, execution, ct);

                if (dispatched.ClearsLastAssistantContent)
                    lastAssistantContent = null;

                if (dispatched.Status == AssistantCapabilityCallStatus.Stopped)
                {
                    stopReason = AssistantTurnStopReason.Cancelled;
                    break;
                }

                if (dispatched.Status == AssistantCapabilityCallStatus.Rejected)
                {
                    messages.Add(LlmMessage.Tool(call.Id, ToolJson(new
                    {
                        status = dispatched.RejectionStatus,
                        message = dispatched.RejectionMessage,
                    })));
                    continue;
                }

                if (dispatched.Status == AssistantCapabilityCallStatus.CapturePrompt)
                {
                    // Asking for deterministic capture input ends this turn. The
                    // user's actual answer arrives as the next Message plus this
                    // continuation id; it is never hidden in context or replayed
                    // as the original message.
                    var prompt = dispatched.Prompt!;
                    anchorPrompt = prompt;
                    messages.Add(LlmMessage.Tool(call.Id, ToolJson(new
                    {
                        status = dispatched.PromptStatus,
                        kind = prompt.Kind,
                        question = prompt.Question,
                        continuationId = prompt.ContinuationId,
                        message = dispatched.PromptMessage,
                    })));
                    continue;
                }

                var result = dispatched.Result!;
                // The effective arguments the capability ran with: a prepared
                // capture, the app's open book, an implicit reader scope, or a
                // bounded retrieval retry. Turn-state truth reads this, not the
                // model's original call.
                var args = dispatched.Arguments;
                var mutation = capability.Trust is
                    AssistantTrustClass.Capture or AssistantTrustClass.Act;

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
                        && AssistantCapabilityPayloads.ReadNoteId(args) is { } readNoteId)
                        reviewedNoteIds.Add(readNoteId);

                    if (string.Equals(capability.Name, "topics_list", StringComparison.Ordinal)
                        || string.Equals(capability.Name, "topics_search", StringComparison.Ordinal))
                    {
                        topicsInspected = true;
                        inspectedTopicIds.UnionWith(AssistantCapabilityPayloads.ReadTopicIds(result.Data));
                    }

                    if (string.Equals(
                            capability.Name,
                            AssistantCapabilityNames.TopicProposal,
                            StringComparison.Ordinal))
                    {
                        AssistantTopicProposals.Merge(
                            suggestions,
                            AssistantTopicProposals.ExtractSuggestions(result.Data),
                            MaxTopicSuggestions);
                    }

                    if (string.Equals(capability.Name, AssistantCapabilityNames.BookTextSearch, StringComparison.Ordinal))
                    {
                        var sources = AssistantEvidenceProjection
                            .ExtractBookTextSources(result.Data)
                            .ToList();
                        sourceReferences.AddRange(sources);
                        retrieval.RecordSources(sources);
                    }
                    else if (string.Equals(capability.Name, AssistantCapabilityNames.KnowledgeSearch, StringComparison.Ordinal))
                    {
                        var sources = AssistantEvidenceProjection
                            .ExtractKnowledgeSearchSources(result.Data)
                            .ToList();
                        sourceReferences.AddRange(sources);
                        retrieval.RecordSources(sources);
                    }
                    else if (string.Equals(capability.Name, AssistantCapabilityNames.KnowledgeRead, StringComparison.Ordinal))
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

                if (result.Success
                    && capability.Trust == AssistantTrustClass.Capture
                    && string.Equals(capability.Name, AssistantCapabilityNames.Capture, StringComparison.Ordinal))
                {
                    acknowledgement = AssistantCapturePolicy.BuildAcknowledgement(
                        dispatched.Capture!.BookTitle,
                        dispatched.Capture.QuoteFidelity,
                        AssistantCapabilityPayloads.IsRewritten(result.Data));
                    capturedNoteId = AssistantCapabilityPayloads.ReadCapturedNoteId(result.Data);
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

        // The turn's ending — changeset, ceiling, error precedence, reply and the
        // three reply guards — belongs to AssistantTurnFinalizer. The loop above
        // only reports what it observed.
        var final = await _finalizer.CompleteAsync(
            new AssistantTurnFinalInput(
                conversationKey,
                turnId,
                request.Message,
                draft,
                retrieval,
                stopReason,
                readUntrustedContent,
                mutationCompleted,
                mutationFailed,
                lastRoundOnlySucceededMutations,
                acknowledgement,
                capturedNoteId,
                lastAssistantContent,
                anchorPrompt,
                terminalError,
                successfulCapabilities,
                evidenceReferences.Count,
                execution),
            ct);

        var finalMetrics = executionMeter.Finish(final.StopReason);
        _metering.Log(finalMetrics);
        await _metering.CompleteAsync(usageLease, finalMetrics);

        logger.LogDebug(
            "Assistant turn handled: {Suggestions} suggestion(s), changeset {Changeset}, anchor prompt {HasPrompt}.",
            suggestions.Count,
            final.Changeset?.Status ?? "none",
            anchorPrompt is not null);

        return new AssistantTurnResponse(
            final.Reply,
            acknowledgement,
            anchorPrompt,
            suggestions,
            final.Changeset,
            capturedNoteId,
            executedCapabilities,
            sourceReferences,
            final.Error,
            evidenceReferences,
            retrieval.ResolvedBook);
    }

    /// <summary>
    /// The committed half of a turn that a provider failure ended (#1558). Null
    /// when nothing canonical was written: an ordinary provider failure keeps the
    /// contract it had, with no body to explain. Never includes the changeset
    /// draft — its staged changes were not applied and must not be presented.
    /// </summary>
    private static AssistantTurnResponse? CommittedOutcomesOrNull(
        bool mutationCompleted,
        string? acknowledgement,
        string? capturedNoteId,
        IReadOnlyList<string> executedCapabilities,
        IReadOnlyList<AssistantSourceReferenceDto> sourceReferences,
        IReadOnlyList<AssistantEvidenceReferenceDto> evidenceReferences,
        AssistantResolvedBookDto? resolvedBook) =>
        mutationCompleted
            ? new AssistantTurnResponse(
                Reply: string.Empty,
                Acknowledgement: acknowledgement,
                AnchorPrompt: null,
                Suggestions: [],
                Changeset: null,
                CapturedNoteId: capturedNoteId,
                ExecutedCapabilities: [.. executedCapabilities],
                Sources: [.. sourceReferences],
                Error: null,
                Evidence: [.. evidenceReferences],
                ResolvedBook: resolvedBook)
            : null;

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
                AssistantCapabilityNames.Capture,
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
                AssistantCapabilityNames.Capture,
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
                AssistantCapabilityPayloads.IsRewritten(result.Data)),
            AnchorPrompt: null,
            Suggestions: [],
            Changeset: null,
            CapturedNoteId: AssistantCapabilityPayloads.ReadCapturedNoteId(result.Data),
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

    private static string ToolJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

}
