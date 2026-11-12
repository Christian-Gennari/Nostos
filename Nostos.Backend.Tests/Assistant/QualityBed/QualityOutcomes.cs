// Turn/session outcome records and the shared structural expectation
// evaluator for the #566 quality bed. Deterministic mode stays path-exact
// (every finding is a failure); live mode asserts outcomes (retrieval
// success, grounded provenance, honest insufficiency) and records path/prose
// deviations as advisories. Safety checks stay hard in both modes.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Text.Json;
using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;

internal sealed record QualityActivityEvent(
    string Code,
    string Message,
    double ReceivedMs);

// One message the provider saw (deterministic mode only).
internal sealed record QualityProviderMessageLog(string Role, string? Content);

// One capability result the model read, in call order: the tool call's name
// and the result JSON the orchestrator put on the tool message. This is the
// backend's own receipt for what the act did (#1573), recorded in both modes
// so the live lane can tell an idempotent re-add from a real write without
// keeping live prompts around.
internal sealed record QualityToolResultLog(string Name, string Content)
{
    /// <summary>
    /// Reads the tool results out of one request's messages: each tool call's
    /// name is matched to the result that repeats its id.
    /// </summary>
    public static IReadOnlyList<QualityToolResultLog> From(IReadOnlyList<LlmMessage> messages)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var results = new List<QualityToolResultLog>();
        foreach (var message in messages)
        {
            if (message.ToolCalls is { } calls)
                foreach (var call in calls)
                    names[call.Id] = call.Name;
            if (string.Equals(message.Role, "tool", StringComparison.Ordinal)
                && message.ToolCallId is { Length: > 0 } callId
                && message.Content is { } content
                && names.TryGetValue(callId, out var name))
                results.Add(new QualityToolResultLog(name, content));
        }

        return results;
    }
}

internal sealed record QualityProviderRequestLog(IReadOnlyList<QualityProviderMessageLog> Messages);

internal sealed record QualityTurnRecord(
    int TurnIndex,
    string TurnId,
    string UserMessage,
    // completed | failed | cancelled | transport_error | harness_error
    string TerminalKind,
    string? FailureCode,
    string? FailureMessage,
    AssistantTurnResponse? Response,
    IReadOnlyList<QualityUpstreamCall> UpstreamCalls,
    IReadOnlyList<string> RequestedTools,
    IReadOnlyList<QualityActivityEvent> Activities,
    double TtfActivityMs,
    double TtfTerminalMs,
    double TotalMs,
    int ProviderRequestCount,
    // Deterministic mode only: the exact provider requests (for history /
    // continuity asserts). Empty in live mode; live tool results are in
    // ToolResults below.
    IReadOnlyList<QualityProviderRequestLog> ProviderRequests,
    int NoteCountBefore,
    int NoteCountAfter,
    int CollectionCountBefore,
    int CollectionCountAfter,
    string? HarnessError,
    // Live-mode outcome scoring notes. Deterministic mode never populates
    // this: every finding there is a failure. Serialized per turn as
    // `advisories` in results.json.
    IReadOnlyList<string>? Advisories = null,
    // The library revision (LibraryState.StateVersion) around the turn. The
    // live library feed re-reads every open screen when it moves, so a turn
    // that writes nothing must leave it alone, and one that writes must move it.
    string? RevisionBefore = null,
    string? RevisionAfter = null,
    // The capability receipts the model read this turn, recorded in both
    // modes. The revision check reads them to see what an executed act did
    // (#1573): an idempotent re-add says `"duplicate": true` instead of
    // leaving a revision bump.
    IReadOnlyList<QualityToolResultLog>? ToolResults = null);

internal sealed record QualitySessionTotals(
    int Turns,
    int UpstreamCalls,
    int ToolCalls,
    long PromptTokens,
    long CompletionTokens,
    long ThinkingTokens,
    double ElapsedMs);

internal sealed record QualityScenarioOutcome(
    string ScenarioId,
    string Title,
    string SessionId,
    string Model,
    IReadOnlyList<QualityTurnRecord> Turns,
    QualitySessionTotals Totals,
    IReadOnlyList<string> Failures,
    // Flat live-mode advisories with turn context ("C1 turn 0: ...").
    // Empty in deterministic mode. Serialized as `advisories`.
    IReadOnlyList<string>? Advisories = null);

/// <summary>
/// Evaluator output split: failures gate the campaign, advisories record
/// live-mode outcome deviations that stay visible without failing the run.
/// Deterministic mode never produces advisories.
/// </summary>
internal sealed record QualityEvaluationResult(
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Advisories);

internal static class QualityExpectationEvaluator
{
    /// <summary>
    /// Deterministic evaluation: every finding is a failure, exactly as
    /// before. New live-only expectation fields are ignored here.
    /// </summary>
    public static List<string> Evaluate(QualityTurnSpec spec, QualityTurnRecord turn) =>
        Evaluate(spec, turn, live: false).Failures.ToList();

    /// <summary>
    /// Mode-aware evaluation. Live mode asserts outcomes: required-tool
    /// misses and positive reply-keyword misses become advisories, while
    /// safety checks (forbidden tools, no-execution, deltas, pending plans,
    /// economy caps, negative claim wording) stay hard in both modes.
    /// </summary>
    public static QualityEvaluationResult Evaluate(QualityTurnSpec spec, QualityTurnRecord turn, bool live)
    {
        var failures = new List<string>();
        var advisories = new List<string>();
        if (turn.HarnessError is not null)
        {
            failures.Add($"harness error: {turn.HarnessError}");
            return new QualityEvaluationResult(failures, advisories);
        }

        var expect = spec.Expect ?? QualityTurnExpect.Empty;

        // A turn that ends in a typed product error evaluates the error code;
        // tool/evidence expectations do not apply to it.
        var errorCode = turn.Response?.Error?.Code ?? turn.FailureCode;
        if (expect.ExpectedErrorCode is not null)
        {
            var accepted = string.Equals(errorCode, expect.ExpectedErrorCode, StringComparison.Ordinal)
                || (live && (expect.AlsoAcceptErrorCodes ?? [])
                    .Contains(errorCode ?? string.Empty, StringComparer.Ordinal));
            if (!accepted)
                failures.Add($"expected error '{expect.ExpectedErrorCode}' but observed '{errorCode ?? "(none)"}'");
            else if (live
                && !string.Equals(errorCode, expect.ExpectedErrorCode, StringComparison.Ordinal)
                && errorCode is not null)
                advisories.Add($"error code '{errorCode}' accepted via AlsoAcceptErrorCodes");
            var errorMessage = turn.Response?.Error?.Message ?? turn.FailureMessage ?? string.Empty;
            if (expect.ExpectedErrorMessageContains is not null
                && !errorMessage.Contains(expect.ExpectedErrorMessageContains, StringComparison.OrdinalIgnoreCase))
                failures.Add($"error message must contain '{expect.ExpectedErrorMessageContains}' (was: '{Truncate(errorMessage, 200)}')");
            return new QualityEvaluationResult(failures, advisories);
        }

        if (turn.Response is null)
        {
            failures.Add($"no turn response (terminal={turn.TerminalKind}, failure={turn.FailureCode ?? "(none)"})");
            return new QualityEvaluationResult(failures, advisories);
        }

        var response = turn.Response;
        foreach (var required in expect.RequiredTools)
        {
            if (turn.RequestedTools.Contains(required, StringComparer.Ordinal))
                continue;
            var detail = $"required tool '{required}' was never requested (requested: [{string.Join(",", turn.RequestedTools)}])";
            if (live)
                advisories.Add($"path deviation: {detail}");
            else
                failures.Add(detail);
        }

        foreach (var forbidden in expect.ForbiddenTools)
        {
            if (turn.RequestedTools.Contains(forbidden, StringComparer.Ordinal))
                failures.Add($"forbidden tool '{forbidden}' was requested");
            if (response.ExecutedCapabilities?.Contains(forbidden, StringComparer.Ordinal) == true)
                failures.Add($"forbidden tool '{forbidden}' executed");
        }

        if (live)
            foreach (var advisory in expect.AdvisoryTools ?? [])
                if (turn.RequestedTools.Contains(advisory, StringComparer.Ordinal))
                    advisories.Add($"tool '{advisory}' requested (advisory for this turn)");

        var evidenceNoteIds = (response.Evidence ?? [])
            .Where(e => string.Equals(e.Handle.Kind, "note", StringComparison.Ordinal) && e.Handle.NoteId.HasValue)
            .Select(e => e.Handle.NoteId!.Value)
            .ToList();
        var missingGold = expect.GoldNoteIds.Where(gold => !evidenceNoteIds.Contains(gold)).ToList();
        var bookPathMatched = false;
        if (missingGold.Count > 0)
        {
            var bookPath = live ? MatchGoldBookPassage(expect, response) : null;
            if (bookPath is not null)
            {
                bookPathMatched = true;
                advisories.Add($"outcome met via book-text path: {bookPath}");
            }
            else
                foreach (var gold in missingGold)
                    failures.Add($"gold note {gold} missing from evidence (evidence notes: [{string.Join(",", evidenceNoteIds)}])");
        }

        // Deterministic-only coexistence: distractors that must sit alongside
        // gold. In live they are query-dependent, so an absence is advisory.
        foreach (var coexisting in expect.CoexistenceNoteIds ?? [])
        {
            if (evidenceNoteIds.Contains(coexisting))
                continue;
            if (live)
                advisories.Add($"coexistence note {coexisting} absent (query-dependent)");
            else
                failures.Add($"coexistence note {coexisting} missing from evidence (evidence notes: [{string.Join(",", evidenceNoteIds)}])");
        }

        var minEvidence = live && expect.LiveMinEvidence.HasValue ? expect.LiveMinEvidence.Value : expect.MinEvidence;
        if ((response.Evidence?.Count ?? 0) < minEvidence)
            failures.Add($"expected at least {minEvidence} evidence items, observed {response.Evidence?.Count ?? 0}");

        if (turn.UpstreamCalls.Count > expect.MaxUpstreamCalls)
            failures.Add($"expected at most {expect.MaxUpstreamCalls} upstream calls, observed {turn.UpstreamCalls.Count}");
        if (turn.RequestedTools.Count > expect.MaxToolCalls)
            failures.Add($"expected at most {expect.MaxToolCalls} tool calls, observed {turn.RequestedTools.Count} ([{string.Join(",", turn.RequestedTools)}])");

        foreach (var required in expect.ReplyMustContain ?? [])
        {
            if (response.Reply.Contains(required, StringComparison.OrdinalIgnoreCase))
                continue;
            var detail = $"reply must contain '{required}' (reply was: '{Truncate(response.Reply, 200)}')";
            if (live)
                advisories.Add($"prose: reply did not contain '{required}'");
            else
                failures.Add(detail);
        }

        foreach (var forbidden in expect.ReplyMustNotContain ?? [])
            if (ContainsCompletionClaim(response.Reply, forbidden))
                failures.Add($"reply must not contain '{forbidden}'");

        if (expect.ExpectReview && response.Changeset?.Status != "review")
            failures.Add("expected changes waiting for review, observed none");
        if (expect.ExpectCapturedNote && string.IsNullOrWhiteSpace(response.CapturedNoteId))
            failures.Add("expected a captured note id, observed none");

        if (expect.ExpectedAnchorPromptKind is not null)
        {
            if (!string.Equals(response.AnchorPrompt?.Kind, expect.ExpectedAnchorPromptKind, StringComparison.Ordinal))
                failures.Add($"expected anchor prompt '{expect.ExpectedAnchorPromptKind}', observed '{response.AnchorPrompt?.Kind ?? "(none)"}'");
            if (string.IsNullOrWhiteSpace(response.AnchorPrompt?.ContinuationId))
                failures.Add("expected a continuation id on the anchor prompt, observed none");
        }

        if (expect.ExpectFirstEvidenceNoteId is { } first)
        {
            var observed = (response.Evidence ?? [])
                .FirstOrDefault(e => string.Equals(e.Handle.Kind, "note", StringComparison.Ordinal));
            if (observed?.Handle.NoteId != first)
            {
                var detail = $"expected first note evidence {first}, observed {observed?.Handle.NoteId?.ToString() ?? "(none)"}";
                if (live && bookPathMatched)
                    advisories.Add($"outcome (first note evidence) not applicable on the book-text path: {detail}");
                else
                    failures.Add(detail);
            }
        }

        if (expect.ExpectedNoteDelta != (turn.NoteCountAfter - turn.NoteCountBefore))
            failures.Add($"expected note delta {expect.ExpectedNoteDelta}, observed {turn.NoteCountAfter - turn.NoteCountBefore}");
        if (expect.ExpectedCollectionDelta != (turn.CollectionCountAfter - turn.CollectionCountBefore))
            failures.Add($"expected collection delta {expect.ExpectedCollectionDelta}, observed {turn.CollectionCountAfter - turn.CollectionCountBefore}");

        // Live updates: the library feed re-reads every open screen when the
        // revision moves. A turn that wrote nothing must not move it; a turn
        // that wrote must, or the open app would not show the change.
        if (turn.RevisionBefore is { } revisionBefore && turn.RevisionAfter is { } revisionAfter)
        {
            var wrote = (response.ExecutedCapabilities?.Count ?? 0) > 0
                || !string.IsNullOrWhiteSpace(response.CapturedNoteId)
                || string.Equals(response.Changeset?.Status, "applied", StringComparison.Ordinal)
                || turn.NoteCountAfter != turn.NoteCountBefore
                || turn.CollectionCountAfter != turn.CollectionCountBefore;
            // An act that found its target already there (a book already on To
            // read) ran but changed nothing; the backend says so in its
            // receipt, not in the revision. Only when every executed
            // capability's own result carries that receipt may the turn be
            // treated as a non-write. A missing result leaves `wrote` alone,
            // so a real write that failed to bump still fails.
            if (wrote
                && string.IsNullOrWhiteSpace(response.CapturedNoteId)
                && response.Changeset?.Status is not "applied"
                && turn.NoteCountAfter == turn.NoteCountBefore
                && turn.CollectionCountAfter == turn.CollectionCountBefore
                && AllExecutedCapabilitiesWereIdempotentReadds(response, turn.ToolResults))
            {
                wrote = false;
            }

            var moved = !string.Equals(revisionBefore, revisionAfter, StringComparison.Ordinal);
            if (!wrote && moved)
                failures.Add($"the library revision moved ({revisionBefore} -> {revisionAfter}) on a turn that wrote nothing");
            if (wrote && !moved)
                failures.Add($"the turn wrote but the library revision stayed at {revisionBefore}: open screens would not update");
        }

        if (expect.ExpectedExecuted is { Length: > 0 } expectedList)
        {
            var executed = new HashSet<string>(response.ExecutedCapabilities ?? [], StringComparer.Ordinal);
            var expected = new HashSet<string>(expectedList, StringComparer.Ordinal);
            if (!executed.SetEquals(expected))
                failures.Add($"expected executed [{string.Join(",", expected)}], observed [{string.Join(",", executed)}]");
        }

        return new QualityEvaluationResult(failures, advisories);
    }

    /// <summary>
    /// True when every Act capability the turn reports as executed returned a
    /// receipt whose data says <c>"duplicate": true</c> — the backend's own
    /// signal that an idempotent write found its target already there and
    /// changed nothing. Results are matched to capabilities by name and in
    /// call order, so a second call of the same capability cannot stand in for
    /// the first. A result that is missing, unattributable or not that receipt
    /// returns false: the revision check then fails closed and a real write
    /// still has to move the revision.
    /// </summary>
    internal static bool AllExecutedCapabilitiesWereIdempotentReadds(
        AssistantTurnResponse response,
        IReadOnlyList<QualityToolResultLog>? toolResults)
    {
        var executed = response.ExecutedCapabilities;
        if (executed is not { Count: > 0 } || toolResults is not { Count: > 0 })
            return false;

        var available = toolResults.ToList();
        foreach (var capability in executed)
        {
            var index = available.FindLastIndex(result =>
                string.Equals(result.Name, capability, StringComparison.Ordinal));
            if (index < 0)
                return false;
            var content = available[index].Content;
            available.RemoveAt(index);
            if (!IsDuplicateReceipt(content))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the capability receipt (<c>{ success, data }</c>): true only when
    /// its data carries the boolean <c>"duplicate": true</c>. Anything else —
    /// a real write, an error, or unreadable JSON — is not a no-op.
    /// </summary>
    internal static bool IsDuplicateReceipt(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data))
                root = data;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("duplicate", out var duplicate)
                && duplicate.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Claim-aware matcher for <c>ReplyMustNotContain</c>: fires only when the
    /// phrase appears as a completion claim, not inside a denial, a
    /// hypothetical, or a modal/passive general rule (e.g. "can be deleted").
    /// A ~40-character window immediately before each
    /// case-insensitive occurrence is scanned for negation/hypothetical and
    /// modal/passive cues;
    /// cued occurrences are skipped, other occurrences still fire.
    /// </summary>
    internal static bool ContainsCompletionClaim(string reply, string phrase)
    {
        if (string.IsNullOrEmpty(reply) || string.IsNullOrEmpty(phrase))
            return false;
        string[] cues =
        [
            "not ", "n't ", "never ", "nothing ", "no ", "if ", "would ",
            "were ", "had been ", "was not", "isn't", "aren't", "didn't",
            "doesn't", "can't", "cannot", "without ",
            "can be ", "may be ", "could be ", "should be ", "would be ",
            "will be ", "to be ", "to delete", "for deletion",
            "requires approval",
        ];
        var start = 0;
        while (true)
        {
            var index = reply.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;
            var windowStart = Math.Max(0, index - 40);
            var window = reply.Substring(windowStart, index - windowStart);
            var cued = false;
            foreach (var cue in cues)
            {
                if (window.Contains(cue, StringComparison.OrdinalIgnoreCase))
                {
                    cued = true;
                    break;
                }
            }

            if (!cued)
                return true;
            start = index + phrase.Length;
        }
    }

    /// <summary>
    /// Live-only gold alternative: returns the matched passage BookId when at
    /// least one book_text evidence item carries that book and its excerpt
    /// contains any declared marker (case-insensitive), else null.
    /// </summary>
    private static string? MatchGoldBookPassage(QualityTurnExpect expect, AssistantTurnResponse response)
    {
        var passages = expect.GoldBookPassages;
        if (passages is not { Length: > 0 })
            return null;
        var bookItems = (response.Evidence ?? [])
            .Where(e => string.Equals(e.Handle.Kind, "book_text", StringComparison.Ordinal)
                && e.Handle.BookId.HasValue)
            .ToList();
        if (bookItems.Count == 0)
            return null;
        foreach (var (bookId, markers) in passages)
        {
            var matchesBook = bookItems.Where(item =>
                Guid.TryParse(bookId, out var parsed)
                    ? item.Handle.BookId == parsed
                    : string.Equals(item.Handle.BookId?.ToString(), bookId, StringComparison.OrdinalIgnoreCase));
            foreach (var item in matchesBook)
            {
                var excerpt = item.Excerpt ?? string.Empty;
                if ((markers ?? []).Any(marker =>
                        !string.IsNullOrWhiteSpace(marker)
                        && excerpt.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                    return bookId;
            }
        }

        return null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
