using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <c>Summary</c>. There is no MCP round trip: the assistant reaches its own
/// Nostos instance through the same canonical services the REST surface uses.</item>
/// <item>Trust classes are enforced here as well as in the registry.
/// <see cref="AssistantTrustClass.Capture"/> and normal
/// <see cref="AssistantTrustClass.Act"/> work execute inside the tool loop,
/// <see cref="AssistantTrustClass.Suggest"/> never mutates, and only destructive
/// <see cref="AssistantTrustClass.PlanAndAct"/> work is collected into a
/// server-held plan for explicit approval.</item>
/// <item>The source-location follow-up is deterministic: when a capture has no
/// nearby anchor and the format cannot supply one, the bridge asks (physical
/// book → a page; externally played audiobook → a timestamp) instead of
/// guessing. An explicitly skipped anchor still captures as <c>unknown</c>.</item>
/// </list>
/// </summary>
public sealed class AssistantOrchestrator(
    AssistantCapabilityRegistry registry,
    ILlmProvider llm,
    AssistantPlanStore plans,
    AssistantContinuationStore continuations,
    IAssistantSettingsService settings,
    ILibraryService library,
    AssistantOptions options,
    ILogger<AssistantOrchestrator> logger,
    IAiUsageAccountingService? usageAccounting = null)
{
    private readonly IAiUsageAccountingService _usageAccounting =
        usageAccounting ?? NoOpAiUsageAccountingService.Instance;
    private readonly AssistantConversationBuilder _conversation =
        new(registry, new AssistantContextPacker(options));
    private readonly AssistantPlanExecutor _planExecutor = new(registry, plans);
    private readonly AssistantCapturePolicy _capturePolicy = new(library);
    private readonly AssistantCaptureIntentGuard _captureIntentGuard = new(library);

    /// <summary>
    /// Generous on purpose: a tool-calling turn can spend reasoning tokens even
    /// on a two-word reply, and a tighter budget truncates real answers.
    /// </summary>
    public const int MaxResponseTokens = 4096;

    /// <summary>
    /// The review flow's "small set": at most three candidate concepts are shown
    /// for one unlinked note. A longer list is a search result, not a suggestion
    /// (issue #261 §5).
    /// </summary>
    public const int MaxConceptSuggestions = 3;

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

    public const string ApprovalRequiredReply = "I've prepared a plan for your approval.";

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
    private const string BookTextCapability = "book_text_search";
    private const string KnowledgeSearchCapability = "knowledge_search";
    private const string KnowledgeReadCapability = "knowledge_read_evidence";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string ConceptProposalCapability = "concepts_propose_links";

    // ------------------------------------------------------------------
    // A turn
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs one assistant turn: build the conversation and tools, loop over the
    /// model's tool calls, and return prose plus any suggestions / follow-up
    /// question / pending plan.
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
                PendingPlan: null,
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

        var executionMeter = new AssistantExecutionMeter();
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

        // The capture post-processing mode is the owner's stored setting, resolved
        // once per turn (issue #262 §7). It is deliberately not read from the
        // request or from the tool call: the owner chose it once, and a
        // per-capture mode would make that choice meaningless.
        var captureProcessingMode = await settings.GetCaptureProcessingModeAsync(ct);

        var suggestions = new List<AssistantSuggestionDto>();
        var reviewedNoteIds = new HashSet<Guid>();
        var inspectedConceptIds = new HashSet<Guid>();
        var conceptsInspected = false;
        var executedCapabilities = new List<string>();
        var sourceReferences = new List<AssistantSourceReferenceDto>();
        var evidenceReferences = new List<AssistantEvidenceReferenceDto>();
        var planSteps = new List<AssistantPlanStep>();
        AssistantAnchorPromptDto? anchorPrompt = null;
        string? acknowledgement = null;
        string? finalContent = null;
        string? lastAssistantContent = null;
        string? capturedNoteId = null;
        AssistantTurnErrorDto? terminalError = null;
        var retrieval = new AssistantRetrievalTurnState(turnContext);
        if (captureIntent.ResolvedBook is { } clarifiedScope)
            retrieval.RecordResolvedBook(clarifiedScope);
        var mutationCompleted = false;

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

                turnDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                turnDeadline.CancelAfter(TimeSpan.FromMilliseconds(remainingMilliseconds));
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

            // Approval is a boundary for the whole model response, not just one
            // entry in its tool-call list. Calls in the same completion cannot
            // depend on each other's results, so once any PlanAndAct proposal is
            // present there is no legitimate reason to execute ordinary actions
            // beside it before the user has approved the destructive plan.
            var approvalCalls = completion.ToolCalls
                .Select(call => new
                {
                    Call = call,
                    Capability = capabilityByName.GetValueOrDefault(call.Name),
                })
                .Where(item => item.Capability?.Trust == AssistantTrustClass.PlanAndAct)
                .ToList();

            if (approvalCalls.Count > 0)
            {
                foreach (var item in approvalCalls)
                {
                    planSteps.Add(new AssistantPlanStep(
                        item.Capability!.Name,
                        item.Capability.Summary,
                        item.Call.ArgumentsJson));
                }

                stopReason = AssistantTurnStopReason.ApprovalRequired;
                break;
            }

            messages.Add(LlmMessage.Assistant(
                completion.Content,
                completion.ToolCalls,
                completion.ProviderState));

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

                    capture = await _capturePolicy.PrepareAsync(
                        call.ArgumentsJson,
                        turnContext,
                        captureProcessingMode,
                        ct);

                    if (capture.Prompt is { } prompt)
                    {
                        var storedContinuation = continuations.Create(
                            conversationKey,
                            turnId,
                            prompt.Kind,
                            call.ArgumentsJson,
                            turnContext,
                            captureProcessingMode);

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
                    args = ParseArguments(call.ArgumentsJson);
                    if (string.Equals(capability.Name, BookTextCapability, StringComparison.Ordinal)
                        || string.Equals(capability.Name, KnowledgeSearchCapability, StringComparison.Ordinal))
                    {
                        args = retrieval.ApplyImplicitBookScope(
                            args,
                            request.History);
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
                    IdempotencyKey: AssistantMutationIdentity.Key(turnId, capability.Name, args));

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
                    result = string.Equals(capability.Name, ConceptProposalCapability, StringComparison.Ordinal)
                        && !ProposalUsesReadEvidence(
                            args, turnContext.BrainReviewNoteId,
                            reviewedNoteIds, inspectedConceptIds, conceptsInspected)
                        ? AssistantToolResult.Fail(
                            AssistantErrorCodes.InvalidArguments,
                            "Read the target note and existing concept evidence in this turn before proposing those IDs.")
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

                messages.Add(LlmMessage.Tool(call.Id, ToolJson(result)));

                if (result.Success && mutation)
                {
                    mutationCompleted = true;
                    terminalError = null;
                }
                else if (!result.Success && mutation)
                {
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
                }

                if (result.Success && capability.Trust == AssistantTrustClass.Suggest)
                {
                    if (string.Equals(capability.Name, "notes_read_for_review", StringComparison.Ordinal)
                        && Guid.TryParse(ReadString(args, "noteId"), out var readNoteId))
                        reviewedNoteIds.Add(readNoteId);

                    if (string.Equals(capability.Name, "concepts_list", StringComparison.Ordinal)
                        || string.Equals(capability.Name, "concepts_search", StringComparison.Ordinal))
                    {
                        conceptsInspected = true;
                        if (result.Data is { } conceptRows && conceptRows.ValueKind == JsonValueKind.Array)
                            foreach (var row in conceptRows.EnumerateArray())
                                if (row.ValueKind == JsonValueKind.Object
                                    && Guid.TryParse(ReadString(row, "id"), out var readConceptId))
                                    inspectedConceptIds.Add(readConceptId);
                    }

                    MergeSuggestions(suggestions, ExtractSuggestions(capability.Name, result.Data));

                    if (string.Equals(capability.Name, BookTextCapability, StringComparison.Ordinal))
                    {
                        var sources = ExtractBookTextSources(result.Data).ToList();
                        sourceReferences.AddRange(sources);
                        retrieval.RecordSources(sources);
                        MergeEvidenceReferences(evidenceReferences, ExtractBookTextEvidence(result.Data));
                    }
                    else if (string.Equals(capability.Name, KnowledgeSearchCapability, StringComparison.Ordinal))
                    {
                        var sources = ExtractKnowledgeSearchSources(result.Data).ToList();
                        sourceReferences.AddRange(sources);
                        retrieval.RecordSources(sources);
                        MergeEvidenceReferences(evidenceReferences, ExtractKnowledgeSearchEvidence(result.Data));
                    }
                    else if (string.Equals(capability.Name, KnowledgeReadCapability, StringComparison.Ordinal))
                    {
                        var beforeEvidence = evidenceReferences.Count;
                        var sources = ExtractKnowledgeReadSources(result.Data).ToList();
                        sourceReferences.AddRange(sources);
                        retrieval.RecordSources(sources);
                        MergeEvidenceReferences(evidenceReferences, ExtractKnowledgeReadEvidence(result.Data));
                        if (evidenceReferences.Count > beforeEvidence)
                            retrieval.MarkExactEvidenceAvailable();
                    }
                }

                if (result.Success
                    && capability.Trust == AssistantTrustClass.Capture
                    && string.Equals(capability.Name, CaptureCapability, StringComparison.Ordinal))
                {
                    acknowledgement = AssistantCapturePolicy.BuildAcknowledgement(capture!.BookTitle, capture.QuoteFidelity);
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

            // Asking for deterministic capture input ends this turn. The user's
            // actual answer arrives as the next Message plus continuation id; it
            // is never hidden in context or replayed as the original message.
            if (anchorPrompt is not null)
            {
                stopReason = AssistantTurnStopReason.UserInputRequired;
                break;
            }

        }

        // A PlanAndAct call is only a proposal until the user approves it. A
        // second proposal supersedes the first: one pending plan per conversation.
        AssistantPendingPlanDto? pendingPlan = null;
        if (planSteps.Count > 0 && stopReason != AssistantTurnStopReason.Cancelled)
        {
            // Describe the server-held proposal, not model narration that might
            // incorrectly imply the PlanAndAct work has already run.
            var summary = string.Join("; ", planSteps.Select(step => step.Summary));

            var stored = plans.Create(conversationKey, turnId, summary, planSteps);
            pendingPlan = ToPendingPlanDto(stored);
        }

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
            terminalError = new AssistantTurnErrorDto(
                AssistantErrorCodes.ExecutionBudgetExhausted,
                "This turn reached its execution limit before it could finish.");
        }
        else if (terminalError is null)
        {
            terminalError = retrieval.TerminalFailure(request.Message);
        }

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
        else if (pendingPlan is not null)
        {
            reply = ApprovalRequiredReply;
        }
        else if (stoppedByExecutionGuard && acknowledgement is null)
        {
            reply = IncompleteTurnReply;
        }
        else
        {
            reply = lastAssistantContent?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reply) && acknowledgement is null)
            {
                reply = IncompleteTurnReply;
            }
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

        var finalMetrics = executionMeter.Finish(stopReason);
        LogExecutionMetrics(finalMetrics);
        await CompleteUsageAsync(usageLease, finalMetrics);

        logger.LogDebug(
            "Assistant turn handled: {Suggestions} suggestion(s), plan {HasPlan}, anchor prompt {HasPrompt}.",
            suggestions.Count,
            pendingPlan is not null,
            anchorPrompt is not null);

        var resolvedBook = retrieval.ResolvedBook;

        return new AssistantTurnResponse(
            reply,
            acknowledgement,
            anchorPrompt,
            suggestions,
            pendingPlan,
            capturedNoteId,
            executedCapabilities,
            sourceReferences,
            terminalError,
            evidenceReferences,
            resolvedBook);
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
                continuationId,
                conversationKey,
                turnId,
                out var replay))
        {
            return replay;
        }

        var lookup = continuations.Find(continuationId, conversationKey);
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
            stored.ProcessingMode,
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
                PendingPlan: null,
                CapturedNoteId: null,
                ExecutedCapabilities: [],
                Sources: [],
                Error: null);

            continuations.RecordReceipt(
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
                capture.QuoteFidelity),
            AnchorPrompt: null,
            Suggestions: [],
            PendingPlan: null,
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
            PendingPlan: null,
            CapturedNoteId: null,
            ExecutedCapabilities: [],
            Sources: [],
            Error: new AssistantTurnErrorDto(code, message));

    // ------------------------------------------------------------------
    // Approval — executes exactly the stored plan, once
    // ------------------------------------------------------------------

    /// <summary>
    /// Executes exactly the stored plan whose id and token are presented. A
    /// missing token, an unknown id, a superseded id, or a mismatched token is
    /// refused by the store before any capability is reached, so a refused
    /// approval mutates nothing.
    /// </summary>
    public Task<AssistantPlanApproveResponse> ApproveAsync(
        string? planId,
        string? approvalToken,
        CancellationToken ct = default) =>
        _planExecutor.ApproveAsync(planId, approvalToken, ct);

    // ------------------------------------------------------------------
    // Suggestions (non-mutating)
    // ------------------------------------------------------------------

    private static bool ProposalUsesReadEvidence(
        JsonElement args,
        string? currentReviewNoteId,
        HashSet<Guid> reviewedNoteIds,
        HashSet<Guid> inspectedConceptIds,
        bool conceptsInspected)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !Guid.TryParse(ReadString(args, "noteId"), out var noteId)
            || !reviewedNoteIds.Contains(noteId)
            || (Guid.TryParse(currentReviewNoteId, out var currentNoteId) && currentNoteId != noteId)
            || !conceptsInspected
            || !args.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
            return false;

        return candidates.EnumerateArray().All(candidate =>
            candidate.ValueKind == JsonValueKind.Object
            && Guid.TryParse(ReadString(candidate, "conceptId"), out var conceptId)
            && inspectedConceptIds.Contains(conceptId));
    }

    /// <summary>
    /// Only an explicit, validated proposal capability can produce suggestions.
    /// Ordinary concept reads are evidence, never implicit clickable actions.
    /// </summary>
    private static IEnumerable<AssistantSuggestionDto> ExtractSuggestions(
        string capabilityName,
        JsonElement? data)
    {
        if (!string.Equals(capabilityName, ConceptProposalCapability, StringComparison.Ordinal)
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
                "concept",
                name,
                reason,
                ReadString(item, "id"),
                noteId);
        }
    }

    /// <summary>
    /// Adds incoming suggestions to the response, de-duplicated by identity and
    /// capped for concepts. The cap is what makes the review a small set rather
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

            if (string.Equals(suggestion.Kind, "concept", StringComparison.Ordinal)
                && target.Count(existing => string.Equals(existing.Kind, "concept", StringComparison.Ordinal))
                    >= MaxConceptSuggestions)
            {
                continue;
            }

            target.Add(suggestion);
        }
    }

    // ------------------------------------------------------------------
    // Small JSON helpers
    // ------------------------------------------------------------------

    private static AssistantPendingPlanDto ToPendingPlanDto(StoredAssistantPlan stored) =>
        new(
            stored.PlanId,
            stored.Summary,
            stored.Steps
                .Select(step => new AssistantPlanStepDto(step.Capability, step.Summary, step.ArgumentsJson))
                .ToList(),
            stored.ApprovalToken);

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static JsonElement ParseArguments(string? json) =>
        JsonSerializer.SerializeToElement(ParseObject(json), JsonOptions);

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


    private static void MergeEvidenceReferences(
        List<AssistantEvidenceReferenceDto> target,
        IEnumerable<AssistantEvidenceReferenceDto> incoming)
    {
        foreach (var evidence in incoming)
        {
            if (target.Count >= MaxEvidenceArtifacts)
                return;

            var key = EvidenceKey(evidence.Handle);
            if (target.Any(existing => string.Equals(
                    EvidenceKey(existing.Handle),
                    key,
                    StringComparison.Ordinal)))
                continue;

            target.Add(evidence);
        }
    }

    private static string EvidenceKey(AssistantEvidenceHandleDto handle) =>
        string.Join(
            "|",
            handle.Kind,
            handle.NoteId,
            handle.ConceptId,
            handle.BookId,
            handle.SourceSha256,
            handle.ExtractorVersion,
            handle.Ordinal);

    private static AssistantEvidenceHandleDto ToEvidenceHandle(KnowledgeEvidenceHandle handle) =>
        new(
            handle.Kind,
            handle.NoteId,
            handle.ConceptId,
            handle.BookId,
            handle.SourceSha256,
            handle.ExtractorVersion,
            handle.Ordinal);

    private static IEnumerable<AssistantEvidenceReferenceDto> ExtractKnowledgeSearchEvidence(
        JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        KnowledgeSearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<KnowledgeSearchResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response is null)
            yield break;

        foreach (var note in response.Notes)
        {
            yield return new AssistantEvidenceReferenceDto(
                ToEvidenceHandle(note.Handle),
                string.IsNullOrWhiteSpace(note.BookTitle) ? "Note" : $"Note · {note.BookTitle}",
                ClipEvidence(note.Snippet),
                BookTitle: note.BookTitle);
        }

        foreach (var concept in response.Concepts)
        {
            var excerpt = concept.MatchSnippet
                ?? concept.SupportingNotes.FirstOrDefault()?.Snippet;
            yield return new AssistantEvidenceReferenceDto(
                ToEvidenceHandle(concept.Handle),
                concept.Name,
                ClipEvidence(excerpt));
        }

        foreach (var passage in response.BookPassages)
            yield return ToEvidenceReference(passage);
    }

    private static IEnumerable<AssistantEvidenceReferenceDto> ExtractKnowledgeReadEvidence(
        JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        KnowledgeReadResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<KnowledgeReadResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response is null)
            yield break;

        if (response.Note is { } note)
        {
            yield return new AssistantEvidenceReferenceDto(
                ToEvidenceHandle(response.Handle),
                string.IsNullOrWhiteSpace(note.BookTitle) ? "Note" : $"Note · {note.BookTitle}",
                ClipEvidence(note.SelectedText ?? note.Content),
                BookTitle: note.BookTitle);
        }
        else if (response.Concept is { } concept)
        {
            yield return new AssistantEvidenceReferenceDto(
                ToEvidenceHandle(response.Handle),
                concept.Name,
                ClipEvidence(concept.Notes.FirstOrDefault()?.Snippet));
        }
        else if (response.BookPassage is { } passage)
        {
            yield return ToEvidenceReference(passage);
        }
    }

    private static IEnumerable<AssistantEvidenceReferenceDto> ExtractBookTextEvidence(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        BookTextSearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<BookTextSearchResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response is null)
            yield break;

        foreach (var passage in response.Passages)
        {
            var handle = new AssistantEvidenceHandleDto(
                KnowledgeEvidenceKinds.BookText,
                BookId: passage.BookId,
                SourceSha256: passage.SourceSha256,
                ExtractorVersion: passage.ExtractorVersion,
                Ordinal: passage.Ordinal);
            var source = ToAssistantSource(
                passage.BookId,
                passage.BookTitle,
                passage.BookAuthor,
                passage.Format,
                passage.SourceSha256,
                passage.Text,
                passage.SourceSegments);

            yield return new AssistantEvidenceReferenceDto(
                handle,
                passage.BookTitle,
                ClipEvidence(passage.Text),
                passage.BookTitle,
                passage.BookAuthor,
                passage.Format.ToString().ToLowerInvariant(),
                source?.Locators ?? []);
        }
    }

    private static AssistantEvidenceReferenceDto ToEvidenceReference(KnowledgeBookEvidence passage)
    {
        var source = ToAssistantSource(
            passage.BookId,
            passage.BookTitle,
            passage.BookAuthor,
            passage.Format,
            passage.SourceSha256,
            passage.Text,
            passage.SourceSegments);

        return new AssistantEvidenceReferenceDto(
            ToEvidenceHandle(passage.Handle),
            passage.BookTitle,
            ClipEvidence(passage.Text),
            passage.BookTitle,
            passage.BookAuthor,
            passage.Format.ToString().ToLowerInvariant(),
            source?.Locators ?? []);
    }

    private static string? ClipEvidence(string? value, int max = 320)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var flat = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }


    private static IEnumerable<AssistantSourceReferenceDto> ExtractKnowledgeSearchSources(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        KnowledgeSearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<KnowledgeSearchResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response is null)
            yield break;

        foreach (var passage in response.BookPassages)
        {
            if (ToAssistantSource(
                passage.BookId,
                passage.BookTitle,
                passage.BookAuthor,
                passage.Format,
                passage.SourceSha256,
                passage.Text,
                passage.SourceSegments) is { } source)
            {
                yield return source;
            }
        }
    }

    private static IEnumerable<AssistantSourceReferenceDto> ExtractKnowledgeReadSources(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        KnowledgeReadResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<KnowledgeReadResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response?.BookPassage is not { } passage)
            yield break;

        if (ToAssistantSource(
            passage.BookId,
            passage.BookTitle,
            passage.BookAuthor,
            passage.Format,
            passage.SourceSha256,
            passage.Text,
            passage.SourceSegments) is { } source)
        {
            yield return source;
        }
    }

    private static IEnumerable<AssistantSourceReferenceDto> ExtractBookTextSources(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
            yield break;

        BookTextSearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<BookTextSearchResponse>(
                element.GetRawText(),
                JsonOptions);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (response is null)
            yield break;

        foreach (var passage in response.Passages)
        {
            if (ToAssistantSource(
                passage.BookId,
                passage.BookTitle,
                passage.BookAuthor,
                passage.Format,
                passage.SourceSha256,
                passage.Text,
                passage.SourceSegments) is { } source)
            {
                yield return source;
            }
        }
    }

    private static AssistantSourceReferenceDto? ToAssistantSource(
        Guid bookId,
        string bookTitle,
        string? bookAuthor,
        BookTextSourceFormat format,
        string sourceSha256,
        string text,
        IReadOnlyList<BookTextSourceSegment> sourceSegments)
    {
        var locators = sourceSegments
            .Select(segment => segment.Locator switch
            {
                PdfBookTextSourceLocator pdf => new AssistantSourceLocatorDto(
                    Type: "pdf",
                    PdfPageIndex: pdf.PageIndex,
                    PdfPageLabel: pdf.PageLabel,
                    StartTextOffset: pdf.StartTextOffset,
                    EndTextOffset: pdf.EndTextOffset),
                EpubBookTextSourceLocator epub => new AssistantSourceLocatorDto(
                    Type: "epub",
                    EpubSpineIndex: epub.SpineIndex,
                    EpubResourceHref: epub.ResourceHref,
                    EpubCfi: epub.Cfi,
                    StartTextOffset: epub.StartTextOffset,
                    EndTextOffset: epub.EndTextOffset),
                AudioBookTextSourceLocator audio => new AssistantSourceLocatorDto(
                    Type: "audio",
                    StartTextOffset: checked((int)Math.Min(int.MaxValue, audio.StartMs)),
                    EndTextOffset: checked((int)Math.Min(int.MaxValue, audio.EndMs))),
                _ => null,
            })
            .Where(locator => locator is not null)
            .Cast<AssistantSourceLocatorDto>()
            .ToList();

        if (locators.Count == 0)
            return null;

        return new AssistantSourceReferenceDto(
            bookId,
            bookTitle,
            bookAuthor,
            format.ToString().ToLowerInvariant(),
            sourceSha256,
            text,
            locators);
    }

    private static string ToolJson(object value) => JsonSerializer.Serialize(value, JsonOptions);

}
