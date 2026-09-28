// Turn/session outcome records and the shared structural expectation
// evaluator for the #566 quality bed. The evaluator runs identically in
// deterministic and live modes; only the evidence source differs (scripted
// provider vs real model).
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
    string? HarnessError);

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
    IReadOnlyList<string> Failures);

internal static class QualityExpectationEvaluator
{
    public static List<string> Evaluate(QualityTurnSpec spec, QualityTurnRecord turn)
    {
        var failures = new List<string>();
        if (turn.HarnessError is not null)
        {
            failures.Add($"harness error: {turn.HarnessError}");
            return failures;
        }

        var expect = spec.Expect ?? QualityTurnExpect.Empty;

        // A turn that ends in a typed product error evaluates the error code;
        // tool/evidence expectations do not apply to it.
        var errorCode = turn.Response?.Error?.Code ?? turn.FailureCode;
        if (expect.ExpectedErrorCode is not null)
        {
            if (!string.Equals(errorCode, expect.ExpectedErrorCode, StringComparison.Ordinal))
                failures.Add($"expected error '{expect.ExpectedErrorCode}' but observed '{errorCode ?? "(none)"}'");
            var errorMessage = turn.Response?.Error?.Message ?? turn.FailureMessage ?? string.Empty;
            if (expect.ExpectedErrorMessageContains is not null
                && !errorMessage.Contains(expect.ExpectedErrorMessageContains, StringComparison.OrdinalIgnoreCase))
                failures.Add($"error message must contain '{expect.ExpectedErrorMessageContains}' (was: '{Truncate(errorMessage, 200)}')");
            return failures;
        }

        if (turn.Response is null)
        {
            failures.Add($"no turn response (terminal={turn.TerminalKind}, failure={turn.FailureCode ?? "(none)"})");
            return failures;
        }

        var response = turn.Response;
        foreach (var required in expect.RequiredTools)
            if (!turn.RequestedTools.Contains(required, StringComparer.Ordinal))
                failures.Add($"required tool '{required}' was never requested (requested: [{string.Join(",", turn.RequestedTools)}])");
        foreach (var forbidden in expect.ForbiddenTools)
        {
            if (turn.RequestedTools.Contains(forbidden, StringComparer.Ordinal))
                failures.Add($"forbidden tool '{forbidden}' was requested");
            if (response.ExecutedCapabilities?.Contains(forbidden, StringComparer.Ordinal) == true)
                failures.Add($"forbidden tool '{forbidden}' executed");
        }

        var evidenceNoteIds = (response.Evidence ?? [])
            .Where(e => string.Equals(e.Handle.Kind, "note", StringComparison.Ordinal) && e.Handle.NoteId.HasValue)
            .Select(e => e.Handle.NoteId!.Value)
            .ToList();
        foreach (var gold in expect.GoldNoteIds)
            if (!evidenceNoteIds.Contains(gold))
                failures.Add($"gold note {gold} missing from evidence (evidence notes: [{string.Join(",", evidenceNoteIds)}])");

        if ((response.Evidence?.Count ?? 0) < expect.MinEvidence)
            failures.Add($"expected at least {expect.MinEvidence} evidence items, observed {response.Evidence?.Count ?? 0}");

        if (turn.UpstreamCalls.Count > expect.MaxUpstreamCalls)
            failures.Add($"expected at most {expect.MaxUpstreamCalls} upstream calls, observed {turn.UpstreamCalls.Count}");
        if (turn.RequestedTools.Count > expect.MaxToolCalls)
            failures.Add($"expected at most {expect.MaxToolCalls} tool calls, observed {turn.RequestedTools.Count} ([{string.Join(",", turn.RequestedTools)}])");

        foreach (var required in expect.ReplyMustContain ?? [])
            if (!response.Reply.Contains(required, StringComparison.OrdinalIgnoreCase))
                failures.Add($"reply must contain '{required}' (reply was: '{Truncate(response.Reply, 200)}')");
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

        return failures;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
