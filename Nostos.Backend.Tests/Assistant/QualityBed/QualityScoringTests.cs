// Live outcome-scoring unit tests for the #566 quality bed.
//
// Synthetic QualityTurnRecords prove the mode split: deterministic mode stays
// path-exact (every finding is a failure) while live mode asserts outcomes
// (path/prose deviations become advisories) without weakening safety checks.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Text.Json;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Shared.Dtos;
using Xunit;

public sealed class QualityScoringTests
{
    // a. live + GoldBookPassages match → no failures, advisory recorded.
    [Fact]
    public void Live_book_text_path_satisfies_note_gold_with_advisory()
    {
        var bookId = QualityFixtureIds.BookSaltMeridian;
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: ["book_text_search"],
            ForbiddenTools: [],
            GoldNoteIds: [QualityFixtureIds.NoteRopeCoil],
            MinEvidence: 1,
            GoldBookPassages: [(bookId.ToString(), ["coiling lines"])]));
        var turn = Record(
            Reply(
                "The Salt Meridian says coiling lines coil best dry.",
                evidence: [BookTextEvidence(bookId, "Coiling lines. Ropes coil best when they are dry.")]),
            ["book_text_search"]);

        var result = QualityExpectationEvaluator.Evaluate(spec, turn, live: true);

        Assert.Empty(result.Failures);
        Assert.Contains(
            result.Advisories,
            advisory => advisory.Contains("outcome met via book-text path", StringComparison.Ordinal));
    }

    // b. live + missing note-gold and no book match → failure (outcome asserted).
    [Fact]
    public void Live_missing_gold_without_book_match_fails()
    {
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: [],
            GoldNoteIds: [QualityFixtureIds.NoteRopeCoil],
            MinEvidence: 1,
            GoldBookPassages: [(QualityFixtureIds.BookSaltMeridian.ToString(), ["coiling lines"])]));
        var turn = Record(
            Reply("Something unrelated.", evidence: [NoteEvidence(QualityFixtureIds.NoteSaltRain)]),
            []);

        var result = QualityExpectationEvaluator.Evaluate(spec, turn, live: true);

        Assert.Contains(
            result.Failures,
            failure => failure.Contains("gold note", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Advisories,
            advisory => advisory.Contains("book-text path", StringComparison.Ordinal));
    }

    // b2. live + book-path gold match with no first note evidence → advisory,
    // not failure; deterministic (no book path) → failure.
    [Fact]
    public void Live_book_path_first_evidence_is_advisory_but_failure_in_deterministic()
    {
        var bookId = QualityFixtureIds.BookSaltMeridian;
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: [],
            GoldNoteIds: [QualityFixtureIds.NoteRopeCoil],
            MinEvidence: 1,
            ExpectFirstEvidenceNoteId: QualityFixtureIds.NoteRopeCoil,
            GoldBookPassages: [(bookId.ToString(), ["coiling lines"])]));
        var turn = Record(
            Reply(
                "The Salt Meridian says coiling lines coil best dry.",
                evidence: [BookTextEvidence(bookId, "Coiling lines. Ropes coil best when they are dry.")]),
            []);

        var live = QualityExpectationEvaluator.Evaluate(spec, turn, live: true);
        Assert.Empty(live.Failures);
        Assert.Contains(
            live.Advisories,
            advisory => advisory.Contains("outcome (first note evidence) not applicable on the book-text path", StringComparison.Ordinal));

        var deterministic = QualityExpectationEvaluator.Evaluate(spec, turn, live: false);
        Assert.Contains(
            deterministic.Failures,
            failure => failure.Contains("expected first note evidence", StringComparison.Ordinal));
        Assert.Empty(deterministic.Advisories);
    }

    // c. live + missing required tool → advisory only; deterministic → failure.
    [Fact]
    public void Missing_required_tool_is_advisory_in_live_but_failure_in_deterministic()
    {
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: ["knowledge_search"],
            ForbiddenTools: [],
            GoldNoteIds: []));
        var turn = Record(Reply("An answer from another path."), ["book_text_search"]);

        var live = QualityExpectationEvaluator.Evaluate(spec, turn, live: true);
        Assert.Empty(live.Failures);
        Assert.Contains(
            live.Advisories,
            advisory => advisory.Contains("path deviation", StringComparison.Ordinal)
                && advisory.Contains("knowledge_search", StringComparison.Ordinal));

        var deterministic = QualityExpectationEvaluator.Evaluate(spec, turn, live: false);
        Assert.Contains(
            deterministic.Failures,
            failure => failure.Contains("required tool 'knowledge_search'", StringComparison.Ordinal));
        Assert.Empty(deterministic.Advisories);
    }

    // d. live + positive ReplyMustContain miss → advisory; deterministic → failure.
    [Fact]
    public void Missing_reply_keyword_is_advisory_in_live_but_failure_in_deterministic()
    {
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: [],
            GoldNoteIds: [],
            ReplyMustContain: ["not certain"]));
        var turn = Record(Reply("Repair is listening before fixing."), []);

        var live = QualityExpectationEvaluator.Evaluate(spec, turn, live: true);
        Assert.Empty(live.Failures);
        Assert.Contains(
            live.Advisories,
            advisory => advisory.Contains("prose: reply did not contain 'not certain'", StringComparison.Ordinal));

        var deterministic = QualityExpectationEvaluator.Evaluate(spec, turn, live: false);
        Assert.Contains(
            deterministic.Failures,
            failure => failure.Contains("reply must contain 'not certain'", StringComparison.Ordinal));
    }

    // e. live + AlsoAcceptErrorCodes match → pass; deterministic → failure.
    [Fact]
    public void Alternate_error_code_passes_in_live_only()
    {
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: [],
            GoldNoteIds: [],
            ExpectedErrorCode: AssistantErrorCodes.NoEvidence,
            AlsoAcceptErrorCodes: [AssistantErrorCodes.SourceIndexingPending]));
        var turn = Record(
            Reply(
                "The ledger is still being indexed.",
                error: new AssistantTurnErrorDto(AssistantErrorCodes.SourceIndexingPending, "indexing pending")),
            []);

        var live = QualityExpectationEvaluator.Evaluate(spec, turn, live: true);
        Assert.Empty(live.Failures);
        Assert.Contains(
            live.Advisories,
            advisory => advisory.Contains(AssistantErrorCodes.SourceIndexingPending, StringComparison.Ordinal));

        var deterministic = QualityExpectationEvaluator.Evaluate(spec, turn, live: false);
        Assert.Contains(
            deterministic.Failures,
            failure => failure.Contains(AssistantErrorCodes.NoEvidence, StringComparison.Ordinal));
    }

    // f. live + AdvisoryTools requested → advisory; ForbiddenTools still fail.
    [Fact]
    public void Advisory_tools_do_not_fail_in_live_but_forbidden_tools_do()
    {
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: ["notes_capture"],
            GoldNoteIds: [],
            AdvisoryTools: ["library_delete_collection"]));

        var advisoryTurn = Record(Reply("I have a plan, but it needs approval."), ["library_delete_collection"]);
        var liveAdvisory = QualityExpectationEvaluator.Evaluate(spec, advisoryTurn, live: true);
        Assert.Empty(liveAdvisory.Failures);
        Assert.Contains(
            liveAdvisory.Advisories,
            advisory => advisory.Contains("library_delete_collection", StringComparison.Ordinal)
                && advisory.Contains("advisory for this turn", StringComparison.Ordinal));

        var deterministicAdvisory = QualityExpectationEvaluator.Evaluate(spec, advisoryTurn, live: false);
        Assert.Empty(deterministicAdvisory.Failures);
        Assert.Empty(deterministicAdvisory.Advisories);

        var forbiddenTurn = Record(Reply("Captured."), ["notes_capture"]);
        var liveForbidden = QualityExpectationEvaluator.Evaluate(spec, forbiddenTurn, live: true);
        Assert.Contains(
            liveForbidden.Failures,
            failure => failure.Contains("forbidden tool 'notes_capture'", StringComparison.Ordinal));
    }

    // g. negative ReplyMustNotContain still fails in both modes.
    [Fact]
    public void Negative_claim_wording_fails_in_both_modes()
    {
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: [],
            GoldNoteIds: [],
            ReplyMustNotContain: ["has been deleted"]));
        var turn = Record(Reply("The collection has been deleted."), []);

        var live = QualityExpectationEvaluator.Evaluate(spec, turn, live: true);
        Assert.Contains(
            live.Failures,
            failure => failure.Contains("reply must not contain", StringComparison.Ordinal));

        var deterministic = QualityExpectationEvaluator.Evaluate(spec, turn, live: false);
        Assert.Contains(
            deterministic.Failures,
            failure => failure.Contains("reply must not contain", StringComparison.Ordinal));
    }

    // g2. claim-aware negative asserts: denials/hypotheticals pass, claims fail,
    // identically in both modes (deterministic replies never trip the guard).
    [Fact]
    public void Negative_claim_denial_does_not_fail_in_either_mode()
    {
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: [],
            GoldNoteIds: [],
            ReplyMustNotContain: ["deleted"]));
        var turn = Record(Reply("Nothing was deleted."), []);

        var live = QualityExpectationEvaluator.Evaluate(spec, turn, live: true);
        Assert.Empty(live.Failures);

        var deterministic = QualityExpectationEvaluator.Evaluate(spec, turn, live: false);
        Assert.Empty(deterministic.Failures);
    }

    [Fact]
    public void Negative_claim_hypothetical_does_not_fail_in_either_mode()
    {
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: [],
            GoldNoteIds: [],
            ReplyMustNotContain: ["deleted"]));
        var turn = Record(
            Reply("So I can't say which books would be unlinked if it were deleted. Deleting the collection would not delete the books themselves."),
            []);

        var live = QualityExpectationEvaluator.Evaluate(spec, turn, live: true);
        Assert.Empty(live.Failures);

        var deterministic = QualityExpectationEvaluator.Evaluate(spec, turn, live: false);
        Assert.Empty(deterministic.Failures);
    }

    [Fact]
    public void Negative_claim_completion_still_fails_in_both_modes()
    {
        var deletedSpec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: [],
            GoldNoteIds: [],
            ReplyMustNotContain: ["deleted"]));
        var deletedTurn = Record(Reply("I deleted the collection."), []);

        var liveDeleted = QualityExpectationEvaluator.Evaluate(deletedSpec, deletedTurn, live: true);
        Assert.Contains(
            liveDeleted.Failures,
            failure => failure.Contains("reply must not contain 'deleted'", StringComparison.Ordinal));

        var deterministicDeleted = QualityExpectationEvaluator.Evaluate(deletedSpec, deletedTurn, live: false);
        Assert.Contains(
            deterministicDeleted.Failures,
            failure => failure.Contains("reply must not contain 'deleted'", StringComparison.Ordinal));

        var goneSpec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: [],
            GoldNoteIds: [],
            ReplyMustNotContain: ["is gone"]));
        var goneTurn = Record(Reply("The collection is gone."), []);

        var liveGone = QualityExpectationEvaluator.Evaluate(goneSpec, goneTurn, live: true);
        Assert.Contains(
            liveGone.Failures,
            failure => failure.Contains("reply must not contain 'is gone'", StringComparison.Ordinal));

        var deterministicGone = QualityExpectationEvaluator.Evaluate(goneSpec, goneTurn, live: false);
        Assert.Contains(
            deterministicGone.Failures,
            failure => failure.Contains("reply must not contain 'is gone'", StringComparison.Ordinal));
    }

    [Fact]
    public void Completion_claim_matcher_skips_cued_occurrences_but_fires_on_plain_ones()
    {
        Assert.False(QualityExpectationEvaluator.ContainsCompletionClaim("Nothing was deleted.", "deleted"));
        Assert.False(QualityExpectationEvaluator.ContainsCompletionClaim("I did not delete anything.", "delete"));
        Assert.False(QualityExpectationEvaluator.ContainsCompletionClaim(
            "So I can't say which books would be unlinked if it were deleted.", "deleted"));
        Assert.True(QualityExpectationEvaluator.ContainsCompletionClaim("I deleted the collection.", "deleted"));
        Assert.True(QualityExpectationEvaluator.ContainsCompletionClaim("The collection is gone.", "is gone"));
    }

    // Coexistence notes: hard in deterministic, advisory in live.
    [Fact]
    public void Coexistence_notes_are_required_in_deterministic_only()
    {
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: [],
            GoldNoteIds: [QualityFixtureIds.NoteLanternWalk],
            CoexistenceNoteIds: [QualityFixtureIds.NoteDistHarborDues],
            MinEvidence: 1));
        var turn = Record(
            Reply("The lantern room stairs.", evidence: [NoteEvidence(QualityFixtureIds.NoteLanternWalk)]),
            []);

        var live = QualityExpectationEvaluator.Evaluate(spec, turn, live: true);
        Assert.Empty(live.Failures);
        Assert.Contains(
            live.Advisories,
            advisory => advisory.Contains("coexistence note", StringComparison.Ordinal)
                && advisory.Contains("query-dependent", StringComparison.Ordinal));

        var deterministic = QualityExpectationEvaluator.Evaluate(spec, turn, live: false);
        Assert.Contains(
            deterministic.Failures,
            failure => failure.Contains("coexistence note", StringComparison.Ordinal));
    }

    // LiveMinEvidence overrides MinEvidence in live mode only.
    [Fact]
    public void Live_evidence_floor_overrides_deterministic_minimum()
    {
        var spec = Spec(new QualityTurnExpect(
            RequiredTools: [],
            ForbiddenTools: [],
            GoldNoteIds: [QualityFixtureIds.NoteLanternWalk],
            MinEvidence: 4,
            LiveMinEvidence: 1));
        var turn = Record(
            Reply("The lantern room stairs.", evidence: [NoteEvidence(QualityFixtureIds.NoteLanternWalk)]),
            []);

        var live = QualityExpectationEvaluator.Evaluate(spec, turn, live: true);
        Assert.Empty(live.Failures);

        var deterministic = QualityExpectationEvaluator.Evaluate(spec, turn, live: false);
        Assert.Contains(
            deterministic.Failures,
            failure => failure.Contains("expected at least 4 evidence items", StringComparison.Ordinal));
    }

    // results.json keeps every existing field and adds per-turn + top-level
    // advisories (the campaign tooling reads failures/totals/scenarios).
    [Fact]
    public void Results_shape_carries_advisories_alongside_failures()
    {
        var turn = Record(Reply("Repair is listening."), []) with
        {
            Advisories = ["prose: reply did not contain 'not certain'"],
        };
        var scenario = new QualityScenarioOutcome(
            "C19",
            "Retrieval-first restraint",
            "session",
            "stub-model",
            [turn],
            new QualitySessionTotals(1, 0, 0, 0, 0, 0, 0),
            ["C19 turn 0: boom"],
            ["C19 turn 0: prose: reply did not contain 'not certain'"]);
        var model = new QualityModelOutcome(
            "stub-model", null, [scenario], scenario.Totals, scenario.Failures, scenario.Advisories);
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("advisories", out var topAdvisories));
        Assert.Equal(1, topAdvisories.GetArrayLength());
        // Existing campaign-tooling fields are unchanged.
        Assert.True(root.TryGetProperty("failures", out _));
        Assert.True(root.TryGetProperty("totals", out _));
        var scenarios = root.GetProperty("scenarios");
        Assert.Equal(1, scenarios.GetArrayLength());
        var first = scenarios[0];
        Assert.True(first.TryGetProperty("advisories", out var scenarioAdvisories));
        Assert.Equal(1, scenarioAdvisories.GetArrayLength());
        var turns = first.GetProperty("turns");
        Assert.Equal(1, turns.GetArrayLength());
        Assert.True(turns[0].TryGetProperty("advisories", out var turnAdvisories));
        Assert.Equal(1, turnAdvisories.GetArrayLength());
    }

    private static QualityTurnSpec Spec(QualityTurnExpect expect) =>
        new("synthetic question", QualityContexts.SecondBrain, null, expect);

    private static QualityTurnRecord Record(
        AssistantTurnResponse? response,
        IReadOnlyList<string> requestedTools,
        string? failureCode = null,
        string? failureMessage = null) =>
        new(
            0,
            "qb-synth-t00",
            "synthetic",
            "completed",
            failureCode,
            failureMessage,
            response,
            [],
            requestedTools,
            [],
            0,
            0,
            0,
            0,
            [],
            0,
            0,
            0,
            0,
            null);

    private static AssistantTurnResponse Reply(
        string reply,
        IReadOnlyList<AssistantEvidenceReferenceDto>? evidence = null,
        AssistantTurnErrorDto? error = null) =>
        new(reply, null, null, [], null, Error: error, Evidence: evidence);

    private static AssistantEvidenceReferenceDto NoteEvidence(Guid noteId) =>
        new(new AssistantEvidenceHandleDto("note", NoteId: noteId), "note label");

    private static AssistantEvidenceReferenceDto BookTextEvidence(Guid bookId, string excerpt) =>
        new(new AssistantEvidenceHandleDto("book_text", BookId: bookId), "book label", Excerpt: excerpt);
}
