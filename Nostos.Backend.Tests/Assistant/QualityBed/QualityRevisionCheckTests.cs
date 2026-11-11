// Deterministic unit tests for the live revision check (#1573).
//
// A capability running is not proof that it wrote: the backend's receipt says
// whether the act changed anything (`"duplicate": true` for an idempotent
// re-add). These tests pin the three cases the live lane must keep apart —
// a no-op re-add, a real write that forgot its revision bump, and a real write
// that moved it — plus the fail-closed path when no receipt can be read.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;
using Xunit;

public sealed class QualityRevisionCheckTests
{
    private const string Before = "2";
    private const string After = "3";

    // The shape the orchestrator puts on the tool message: the capability
    // result wrapper around the capability's own data.
    private const string ReaddReceipt =
        """{"success":true,"errorCode":null,"errorMessage":null,"data":{"duplicate":true,"entry":{"id":"entry-1"}}}""";

    private const string WriteReceipt =
        """{"success":true,"errorCode":null,"errorMessage":null,"data":{"duplicate":false,"entry":{"id":"entry-2"}}}""";

    [Fact]
    public void Idempotent_readd_that_leaves_the_revision_alone_passes()
    {
        // C23 turn 2 live: reading_list_add finds the book already there, so
        // the revision correctly stays put. That is not a missed update.
        var result = Evaluate(Turn(Before, Before, ("reading_list_add", ReaddReceipt)));

        Assert.Empty(result.Failures);
    }
    [Fact]
    public void Real_write_that_leaves_the_revision_alone_still_fails()
    {
        var result = Evaluate(Turn(Before, Before, ("reading_list_add", WriteReceipt)));

        Assert.Contains(
            result.Failures,
            failure => failure.Contains("the turn wrote but the library revision stayed at 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Real_write_that_moves_the_revision_passes()
    {
        var result = Evaluate(Turn(Before, After, ("reading_list_add", WriteReceipt)));

        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Missing_receipt_keeps_the_turn_a_write()
    {
        // No tool result was recorded: the check must fail closed rather than
        // assume every Act was a no-op.
        var result = Evaluate(TurnCore(Before, Before, ["reading_list_add"], []));

        Assert.Contains(
            result.Failures,
            failure => failure.Contains("the turn wrote but the library revision stayed at 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Unreadable_receipt_keeps_the_turn_a_write()
    {
        var result = Evaluate(Turn(Before, Before, ("reading_list_add", "not-json")));

        Assert.Contains(
            result.Failures,
            failure => failure.Contains("the turn wrote but the library revision stayed at 2", StringComparison.Ordinal));
    }

    [Fact]
    public void An_idempotent_result_cannot_stand_in_for_another_call_of_the_same_capability()
    {
        // Two adds of the same capability in one turn: the first created an
        // entry, the second found it. Matching by capability name alone would
        // read only the last receipt and let the real write through.
        var result = Evaluate(Turn(
            Before,
            Before,
            ("reading_list_add", WriteReceipt),
            ("reading_list_add", ReaddReceipt)));

        Assert.Contains(
            result.Failures,
            failure => failure.Contains("the turn wrote but the library revision stayed at 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Idempotent_readd_with_a_revision_move_still_fails()
    {
        // A no-op must not move the revision either: that would re-read every
        // open screen for nothing.
        var result = Evaluate(Turn(Before, After, ("reading_list_add", ReaddReceipt)));

        Assert.Contains(
            result.Failures,
            failure => failure.Contains("the library revision moved (2 -> 3) on a turn that wrote nothing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Live_request_capture_records_the_tool_results_the_check_reads()
    {
        // Proves the live lane's plumbing without a model call: the metered
        // provider records the requests it sends, and the last one carries the
        // call name and tool result the revision check matches.
        var capture = new QualityProviderRequestCapture();
        var metered = new MeteredLlmProvider(
            new CompletingProvider(), new QualityMetricsSink(), "stub", capture: capture);
        var request = new LlmCompletionRequest(
            [
                LlmMessage.User("synthetic"),
                LlmMessage.Assistant(null, [new LlmToolCall("call-1", "reading_list_add", "{}")]),
                LlmMessage.Tool("call-1", ReaddReceipt),
            ],
            [],
            16);

        await metered.CompleteAsync(request);

        var toolResult = Assert.Single(capture.ToolResultsSince(0));
        Assert.Equal("reading_list_add", toolResult.Name);
        Assert.True(QualityExpectationEvaluator.IsDuplicateReceipt(toolResult.Content));
        Assert.Empty(capture.ToolResultsSince(capture.Count));
    }

    private static QualityEvaluationResult Evaluate(QualityTurnRecord turn) =>
        QualityExpectationEvaluator.Evaluate(
            new QualityTurnSpec(
                "synthetic",
                Context: QualityContexts.SecondBrain,
                Expect: QualityTurnExpect.Empty),
            turn,
            live: true);

    private static QualityTurnRecord Turn(
        string revisionBefore,
        string revisionAfter,
        params (string Name, string Receipt)[] calls) =>
        TurnCore(revisionBefore, revisionAfter, calls.Select(call => call.Name).ToList(), calls);

    private static QualityTurnRecord TurnCore(
        string revisionBefore,
        string revisionAfter,
        IReadOnlyList<string> executed,
        IReadOnlyList<(string Name, string Receipt)> calls)
    {
        // Deterministic requests stay empty here: the revision check reads the
        // tool results both modes now record, not the request log.
        var toolResults = calls
            .Select(call => new QualityToolResultLog(call.Name, call.Receipt))
            .ToList();
        var response = new AssistantTurnResponse(
            "synthetic reply",
            null,
            null,
            [],
            null,
            ExecutedCapabilities: executed);
        return new QualityTurnRecord(
            0,
            "qb-synth-t00",
            "synthetic",
            "completed",
            FailureCode: null,
            FailureMessage: null,
            Response: response,
            UpstreamCalls: [],
            RequestedTools: executed,
            Activities: [],
            TtfActivityMs: 0,
            TtfTerminalMs: 0,
            TotalMs: 0,
            ProviderRequestCount: 0,
            ProviderRequests: [],
            NoteCountBefore: 0,
            NoteCountAfter: 0,
            CollectionCountBefore: 0,
            CollectionCountAfter: 0,
            HarnessError: null,
            RevisionBefore: revisionBefore,
            RevisionAfter: revisionAfter,
            ToolResults: toolResults);
    }

    private sealed class CompletingProvider : ILlmProvider
    {
        public Task<LlmCompletion> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new LlmCompletion("synthetic", "stop", []));
    }
}
