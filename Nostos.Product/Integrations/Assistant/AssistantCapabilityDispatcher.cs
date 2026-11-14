using System.Text.Json;
using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// The one place an assistant capability is named: the registry, the tool
/// catalogues, the orchestration loop and the capability dispatcher all compare
/// the same strings, so they are defined once.
/// </summary>
internal static class AssistantCapabilityNames
{
    public const string Capture = "notes_capture";
    public const string RewriteCapture = "notes_rewrite_capture";
    public const string BookTextSearch = "book_text_search";
    public const string FindQuote = "book_text_find_quote";
    public const string KnowledgeSearch = "knowledge_search";
    public const string KnowledgeRead = "knowledge_read_evidence";
    public const string NotesListForBook = "notes_list_for_book";
    public const string NotesSearch = "notes_search";
    public const string TopicProposal = AssistantTopicProposals.Capability;
}

/// <summary>What one model tool call did.</summary>
internal enum AssistantCapabilityCallStatus
{
    /// <summary>A capability ran (or refused) and its result goes to the model.</summary>
    ModelResult,

    /// <summary>A capture needs deterministic input from the user first.</summary>
    CapturePrompt,

    /// <summary>The server refused the call outright; the model gets the reason.</summary>
    Rejected,

    /// <summary>Stop won the atomic mutation boundary; no write began.</summary>
    Stopped,
}

/// <summary>
/// The outcome of dispatching one tool call: either a result for the model, a
/// prompt the turn must ask, a server-side refusal, or a stop.
/// </summary>
internal sealed record AssistantCapabilityCallOutcome(
    AssistantCapabilityCallStatus Status,
    JsonElement Arguments = default,
    AssistantToolResult? Result = null,
    AssistantCapturePreparation? Capture = null,
    AssistantAnchorPromptDto? Prompt = null,
    string? PromptStatus = null,
    string? PromptMessage = null,
    string? RejectionStatus = null,
    string? RejectionMessage = null,
    bool ClearsLastAssistantContent = false)
{
    public static AssistantCapabilityCallOutcome Stopped() =>
        new(AssistantCapabilityCallStatus.Stopped);

    public static AssistantCapabilityCallOutcome Rejected(
        string status,
        string message,
        bool clearsLastAssistantContent = false) =>
        new(
            AssistantCapabilityCallStatus.Rejected,
            RejectionStatus: status,
            RejectionMessage: message,
            ClearsLastAssistantContent: clearsLastAssistantContent);

    public static AssistantCapabilityCallOutcome ForModel(
        JsonElement arguments,
        AssistantToolResult result,
        AssistantCapturePreparation? capture = null) =>
        new(
            AssistantCapabilityCallStatus.ModelResult,
            Arguments: arguments,
            Result: result,
            Capture: capture);
}

/// <summary>
/// One model tool call, from the server's argument guards to the canonical
/// invocation and its one bounded recovery (issue #1598).
///
/// What lives here is per-call server authority, never model authority:
///
/// <list type="bullet">
/// <item>a capture the turn suppressed, a quote lookup that never resolved, and
/// a rewrite the user did not ask for are refused before anything runs;</item>
/// <item>the open book is the app's: a quote is looked up in the book being
/// read, and retrieval gets the implicit reader scope only when the model
/// omitted one;</item>
/// <item>topic proposals must be grounded in this turn's own reads;</item>
/// <item>a canonical write claims the turn's atomic mutation boundary against
/// Stop;</item>
/// <item>a ready but empty book-text search is retried once with the user's
/// literal terms, and a sparse reader-scoped knowledge lookup widens once to
/// the whole library.</item>
/// </list>
///
/// What it deliberately does not own: the turn's mutable observations (evidence,
/// sources, suggestions, mutation flags), which stay in the loop, and the reply,
/// which belongs to <see cref="AssistantTurnFinalizer"/>.
/// </summary>
internal sealed class AssistantCapabilityDispatcher(
    AssistantCapabilityRegistry registry,
    AssistantCapturePolicy capturePolicy,
    AssistantContinuationStore continuations,
    AssistantChangesetDraft draft,
    AssistantRetrievalTurnState retrieval,
    AssistantContextDto context,
    IReadOnlyList<AssistantHistoryMessageDto>? history,
    string message,
    IReadOnlyList<string> earlierUserMessages,
    bool captureSuppressed,
    string ownerKey,
    string conversationKey,
    string turnId,
    Func<JsonElement, bool> proposalUsesReadEvidence,
    Func<AssistantTurnActivityDto, ValueTask>? activity)
{
    // A quote looked up by its first and last words that did not resolve to one
    // passage leaves nothing to save this turn: the dictated words are not the
    // quote, and saving them would pass them off as one.
    private bool _quoteLookupUnresolved;

    public async Task<AssistantCapabilityCallOutcome> DispatchAsync(
        LlmToolCall call,
        AssistantCapability capability,
        AssistantTurnExecution? execution,
        CancellationToken ct)
    {
        JsonElement args;
        var implicitReaderScope = false;
        AssistantCapturePreparation? capture = null;

        if (capability.Trust == AssistantTrustClass.Capture
            && string.Equals(capability.Name, AssistantCapabilityNames.Capture, StringComparison.Ordinal))
        {
            if (captureSuppressed)
            {
                // Durable writes must not depend on the model correctly
                // interpreting an explicit "do not save" instruction or
                // a short source clarification. The tool is also omitted
                // from the advertised catalogue for these turns; this is
                // the server-side backstop if a provider still emits it.
                return AssistantCapabilityCallOutcome.Rejected(
                    "capture_rejected",
                    "This turn is conversational control, not a capture. Nothing was saved. Do not call notes_capture again for this turn.",
                    clearsLastAssistantContent: true);
            }

            if (_quoteLookupUnresolved
                && string.IsNullOrWhiteSpace(ReadString(
                    AssistantToolArguments.ParseElement(call.ArgumentsJson), "quoteRef")))
            {
                return AssistantCapabilityCallOutcome.Rejected(
                    "capture_rejected",
                    "book_text_find_quote did not find one passage, so there is nothing to save. Nothing was saved. Tell the user what book_text_find_quote said.",
                    clearsLastAssistantContent: true);
            }

            capture = await capturePolicy.PrepareAsync(
                call.ArgumentsJson,
                context,
                message,
                fallbackContent: null,
                ct,
                earlierUserMessages);

            if (capture.Prompt is { } prompt)
            {
                var storedContinuation = continuations.Create(
                    ownerKey,
                    conversationKey,
                    turnId,
                    prompt.Kind,
                    call.ArgumentsJson,
                    message,
                    context);

                return new AssistantCapabilityCallOutcome(
                    AssistantCapabilityCallStatus.CapturePrompt,
                    Prompt: prompt with { ContinuationId = storedContinuation.ContinuationId },
                    PromptStatus: capture.PromptStatus,
                    PromptMessage: capture.PromptMessage);
            }

            args = capture.Arguments!.Value;
        }
        else
        {
            args = AssistantToolArguments.ParseElement(call.ArgumentsJson);

            // A rewrite is the user's to ask for, never the model's to
            // offer: without a request in their own words, nothing changes.
            // Which notes it may touch is the note service's check.
            if (string.Equals(capability.Name, AssistantCapabilityNames.RewriteCapture, StringComparison.Ordinal)
                && !AssistantRewriteIntent.Requested(message))
            {
                return AssistantCapabilityCallOutcome.Rejected(
                    "rewrite_rejected",
                    "The user did not ask for a rewrite in this message. Nothing was changed.");
            }

            // Like a capture, the open book is the app's: the quote is
            // looked up in the book the user is reading, never one the
            // model went and found.
            if (string.Equals(capability.Name, AssistantCapabilityNames.FindQuote, StringComparison.Ordinal)
                && Guid.TryParse(context.BookId, out var openBookId))
            {
                var findArgs = AssistantToolArguments.ParseObject(call.ArgumentsJson);
                findArgs["bookId"] = openBookId.ToString();
                args = JsonSerializer.SerializeToElement(findArgs);
            }

            if (string.Equals(capability.Name, AssistantCapabilityNames.BookTextSearch, StringComparison.Ordinal)
                || string.Equals(capability.Name, AssistantCapabilityNames.KnowledgeSearch, StringComparison.Ordinal))
            {
                args = retrieval.ApplyImplicitBookScope(
                    args,
                    history,
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
                return AssistantCapabilityCallOutcome.Stopped();
            }
        }

        AssistantToolResult result;
        try
        {
            result = string.Equals(capability.Name, AssistantCapabilityNames.TopicProposal, StringComparison.Ordinal)
                && !proposalUsesReadEvidence(args)
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
                && string.Equals(capability.Name, AssistantCapabilityNames.BookTextSearch, StringComparison.Ordinal)
                && result.Data is { } bookTextData
                && retrieval.BuildBookTextFallbackArguments(
                    args,
                    message,
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
                && string.Equals(capability.Name, AssistantCapabilityNames.KnowledgeSearch, StringComparison.Ordinal)
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
            return AssistantCapabilityCallOutcome.Stopped();
        }
        finally
        {
            mutationLease?.Dispose();
        }

        if (string.Equals(capability.Name, AssistantCapabilityNames.FindQuote, StringComparison.Ordinal))
        {
            _quoteLookupUnresolved = !(result.Success
                && string.Equals(
                    AssistantCapabilityPayloads.ReadQuoteLookupStatus(result.Data),
                    "found",
                    StringComparison.Ordinal));
        }

        return AssistantCapabilityCallOutcome.ForModel(args, result, capture);
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
