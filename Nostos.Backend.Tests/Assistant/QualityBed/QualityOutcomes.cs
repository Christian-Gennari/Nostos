// Turn/session outcome records and the shared structural expectation
// evaluator for the #566 quality bed. Deterministic mode stays path-exact
// (every finding is a failure); live mode asserts outcomes (retrieval
// success, grounded provenance, honest insufficiency) and records path/prose
// deviations as advisories. Safety checks stay hard in both modes.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using Nostos.Shared.Dtos;

internal sealed record QualityActivityEvent(
    string Code,
    string Message,
    double ReceivedMs);

internal sealed record QualityProviderMessageLog(string Role, string? Content);

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
    // continuity asserts). Empty in live mode.
    IReadOnlyList<QualityProviderRequestLog> ProviderRequests,
    int NoteCountBefore,
    int NoteCountAfter,
    int CollectionCountBefore,
    int CollectionCountAfter,
    string? HarnessError,
    // Live-mode outcome scoring notes. Deterministic mode never populates
    // this: every finding there is a failure. Serialized per turn as
    // `advisories` in results.json.
    IReadOnlyList<string>? Advisories = null);

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
        if (missingGold.Count > 0)
        {
            var bookPath = live ? MatchGoldBookPassage(expect, response) : null;
            if (bookPath is not null)
                advisories.Add($"outcome met via book-text path: {bookPath}");
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
            if (response.Reply.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                failures.Add($"reply must not contain '{forbidden}'");

        if (expect.ExpectPendingPlan && response.PendingPlan is null)
            failures.Add("expected a pending plan, observed none");
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
                failures.Add($"expected first note evidence {first}, observed {observed?.Handle.NoteId?.ToString() ?? "(none)"}");
        }

        if (expect.ExpectedNoteDelta != (turn.NoteCountAfter - turn.NoteCountBefore))
            failures.Add($"expected note delta {expect.ExpectedNoteDelta}, observed {turn.NoteCountAfter - turn.NoteCountBefore}");
        if (expect.ExpectedCollectionDelta != (turn.CollectionCountAfter - turn.CollectionCountBefore))
            failures.Add($"expected collection delta {expect.ExpectedCollectionDelta}, observed {turn.CollectionCountAfter - turn.CollectionCountBefore}");

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
