using Microsoft.Extensions.Logging;
using Nostos.Backend.Configuration;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>Everything the turn observed that its ending depends on.</summary>
internal sealed record AssistantTurnFinalInput(
    string ConversationKey,
    string TurnId,
    string UserMessage,
    AssistantChangesetDraft Draft,
    AssistantRetrievalTurnState Retrieval,
    AssistantTurnStopReason StopReason,
    bool ReadUntrustedContent,
    bool MutationCompleted,
    bool MutationFailed,
    bool LastRoundOnlySucceededMutations,
    string? Acknowledgement,
    string? CapturedNoteId,
    string? LastAssistantContent,
    bool ReplyCutShort,
    AssistantAnchorPromptDto? AnchorPrompt,
    AssistantTurnErrorDto? TerminalError,
    IReadOnlySet<string> SuccessfulCapabilities,
    int EvidenceCount,
    AssistantTurnExecution? Execution,
    // APPENDED (issue #1770 review): the library's own count of topics with no
    // kind, so a turn cut short by a ceiling can say how many are left.
    int? UnkindedTopicCount = null);

/// <summary>The turn's ending: the one thing the person sees.</summary>
internal sealed record AssistantTurnFinal(
    string Reply,
    AssistantTurnErrorDto? Error,
    AssistantChangesetDto? Changeset,
    AssistantTurnStopReason StopReason);

/// <summary>
/// How one Ask Nostos turn ends (issue #1598): the changeset is finished as one
/// unit, the ceiling outcome and the error precedence are computed, the reply is
/// chosen, and the three reply guards run in their fixed order.
///
/// The precedence is the contract, in this order:
///
/// <list type="number">
/// <item>a stopped turn replies with nothing and changes nothing;</item>
/// <item>a follow-up question the server must ask beats everything else;</item>
/// <item>a review card speaks for the turn, and its arrival clears the stop's
/// error so the same stop is not reported twice;</item>
/// <item>a changeset failure replaces the reply with its own honest words;</item>
/// <item>an execution guard speaks with the server's fixed language, never with
/// the model's narration;</item>
/// <item>otherwise the model's last prose is the reply.</item>
/// </list>
///
/// Then the guards run, and only over that conversational reply: a completion
/// claim no server-observed call supports is replaced (never a note's words), a
/// self-asserted vendor/model mention is replaced, and a citation marker only
/// survives when it points at this turn's evidence.
/// </summary>
internal sealed class AssistantTurnFinalizer(
    AssistantChangesetCoordinator changesets,
    ILogger logger)
{
    private enum CeilingOutcome
    {
        None,
        NothingCommitted,
        Completed,
        PartiallyCompleted,
    }

    public async Task<AssistantTurnFinal> CompleteAsync(
        AssistantTurnFinalInput input,
        CancellationToken ct)
    {
        var stopReason = input.StopReason;
        var mutationCompleted = input.MutationCompleted;
        var terminalError = input.TerminalError;

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
            using var applyLease = input.Draft.IsEmpty ? null : input.Execution?.TryBeginMutation();
            if (!input.Draft.IsEmpty && input.Execution is not null && applyLease is null)
            {
                stopReason = AssistantTurnStopReason.Cancelled;
            }
            else
            {
                // A change the user spelled out in the same message is theirs, even
                // when the turn also read notes to answer another part of it.
                var derivedFromText = input.ReadUntrustedContent
                    && !AssistantDictatedChange.Covers(input.Draft.Steps, input.UserMessage);
                changeOutcome = await changesets.FinishTurnAsync(
                    input.ConversationKey, input.TurnId, input.Draft, derivedFromText, incomplete, ct);
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
                : !input.MutationFailed && input.LastRoundOnlySucceededMutations
                    ? CeilingOutcome.Completed
                    : CeilingOutcome.PartiallyCompleted;

            terminalError = ceilingOutcome switch
            {
                CeilingOutcome.Completed => null,
                CeilingOutcome.PartiallyCompleted => new AssistantTurnErrorDto(
                    AssistantErrorCodes.PartiallyCompleted,
                    AssistantOrchestrator.PartiallyCompletedReply),
                _ => new AssistantTurnErrorDto(
                    AssistantErrorCodes.ExecutionBudgetExhausted,
                    "This turn reached its execution limit before it could finish."),
            };
        }
        else if (terminalError is null)
        {
            terminalError = input.Retrieval.TerminalFailure(input.UserMessage);
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
        // True only while the reply is the model's own prose that the provider
        // ended at its output limit; any server-owned reply replaces it.
        var replyCutShort = false;
        if (stopReason == AssistantTurnStopReason.Cancelled)
        {
            reply = string.Empty;
        }
        else if (input.AnchorPrompt is not null)
        {
            reply = input.AnchorPrompt.Question;
        }
        else if (changeOutcome.AwaitingReview)
        {
            // A turn that answered a question from the user's material and also
            // proposed a change keeps its answer above the review card (#1745).
            // A turn that only organised keeps the fixed sentence alone.
            // A turn the ceiling stopped says what the diff it staged did and
            // what to do next, rather than letting the fixed sentence hide it.
            var answer = input.LastAssistantContent?.Trim();
            var answered = input.EvidenceCount > 0 && !string.IsNullOrWhiteSpace(answer);
            var review = stoppedByExecutionGuard
                ? SizedReviewNote(changeOutcome.Changeset?.Diff, input.UnkindedTopicCount)
                    is { } sized
                    ? $"{sized}\n\n{AssistantChangesetCoordinator.ReviewReply}"
                    : AssistantChangesetCoordinator.ReviewReply
                : AssistantChangesetCoordinator.ReviewReply;
            reply = answered ? $"{answer}\n\n{review}" : review;
            replyCutShort = answered && input.ReplyCutShort;
        }
        else if (changeOutcome.Error is { } changeError)
        {
            terminalError = changeError;
            reply = changeError.Message;
        }
        else if (stoppedByExecutionGuard && input.Acknowledgement is null)
        {
            reply = ceilingOutcome switch
            {
                CeilingOutcome.Completed => AssistantOrchestrator.CompletedBeforeCeilingReply,
                CeilingOutcome.PartiallyCompleted => AssistantOrchestrator.PartiallyCompletedReply,
                _ => AssistantOrchestrator.IncompleteTurnReply,
            };
        }
        else
        {
            reply = input.LastAssistantContent?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reply) && input.Acknowledgement is null)
            {
                reply = AssistantOrchestrator.IncompleteTurnReply;
            }
            else
            {
                replyCutShort = input.ReplyCutShort && !string.IsNullOrWhiteSpace(reply);
            }
        }

        // A model may narrate a write, empty search result, or staged review
        // that does not match what the current turn produced. Only server-
        // observed state can support those claims; otherwise replace the reply
        // with honest server-owned words.
        if (AssistantCompletionClaimGuard.Evaluate(
                reply,
                input.SuccessfulCapabilities,
                changeOutcome,
                input.Draft.LastStageWasNoChange && input.Draft.Diff is null) is { } unverifiedClaim)
        {
            logger.LogWarning(
                "Assistant completion-claim guard replaced an unverified {ClaimKind} claim; reply content is not logged.",
                unverifiedClaim.Kind);
            reply = unverifiedClaim.ReplacementReply;
            replyCutShort = false;
            if (unverifiedClaim.IsError)
            {
                terminalError ??= new AssistantTurnErrorDto(
                    AssistantErrorCodes.UnverifiedCompletionClaim,
                    unverifiedClaim.ReplacementReply);
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

        // A citation marker only survives when it points at this turn's evidence.
        reply = AssistantCitations.Normalize(reply, input.EvidenceCount);

        // Added after the guards, which judge only the model's words. The
        // person sees that the answer stopped early, never a silent truncation.
        if (replyCutShort)
            reply = $"{reply}\n\n{AssistantOrchestrator.CutShortNotice}";

        return new AssistantTurnFinal(
            reply,
            terminalError,
            changeOutcome.Changeset,
            stopReason);
    }

    /// <summary>
    /// The sentence above a review card when the turn's ceiling stopped it
    /// part way. Built from the server's own diff, never the model's narration:
    /// how many topics it sorted, and — when the library was read — how many
    /// still have no kind, so the reader knows a second run continues there.
    /// Internal so the old-host diff (no topic-kind fields at all) is covered
    /// directly (issue #1770 review, round 3).
    /// </summary>
    internal static string? SizedReviewNote(LibraryChangesetDiffDto? diff, int? unkindedTopicCount)
    {
        if (diff is null)
            return null;

        var sorted = diff.TopicKindAssignments.Count(assignment => assignment.KindId is not null);
        if (sorted == 0)
            return null;

        var head = sorted == 1
            ? "I sorted 1 topic before this turn's limit"
            : $"I sorted {sorted} topics before this turn's limit";
        // Only a topic that had no kind before this changeset is subtracted from
        // the library's count; a changed kind was never part of it.
        var given = diff.TopicKindAssignments.Count(assignment =>
            assignment.KindId is not null && assignment.PreviousKindName is null);
        var remaining = unkindedTopicCount is { } total ? Math.Max(0, total - given) : (int?)null;
        return remaining switch
        {
            // The count is unknown: keep the generic sentence.
            null => $"{head}. Apply these, then ask again.",
            // Nothing is left to sort: there is no second run to ask for.
            0 => $"{head}. Apply these.",
            1 => $"{head}; 1 still has no kind. Apply these, then ask again.",
            _ => $"{head}; {remaining} still have no kind. Apply these, then ask again.",
        };
    }
}
