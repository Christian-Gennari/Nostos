// Scenarios C11–C15 (issue #566).
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Net.Http.Json;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;

internal static partial class QualityCatalogue
{
    // ------------------------------------------------------------------
    // C11 — source recall over multiple turns
    // ------------------------------------------------------------------

    private static QualityScenario C11()
    {
        // The follow-up re-reads the exact turn-1 handle (inert references
        // must be re-read through knowledge_read_evidence, never trusted
        // from history prose). Deterministic proves the handle round-trip;
        // live scores whether the model stays tied to the passage.
        return new QualityScenario(
            "C11",
            "Source recall over multiple turns",
            "C11 — source recall over multiple turns: later follow-up remains tied to the correct exact note/book passage.",
            "The follow-up re-reads the chapter-two handle and cites the same passage.",
            "Does the follow-up quote the chapter-two margin entry (ferry crossing, spring flood) rather than a neighboring passage? " +
            "Is the re-read visible as a return to the same source?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic proves exact handle re-read; live scores recall precision.",
            [
                new QualityTurnSpec(
                    "What did the margin entry in chapter two say?",
                    () => QualityContexts.ReaderEbook(
                        QualityFixtureIds.BookCartographer,
                        QualityFixtureIds.TitleCartographer,
                        "epubcfi(/6/4[chap02]!/4/2/6)"),
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search("margin chapter two river")),
                        QualityScript.Reply("The margins say the ferry crossing moved upstream after the spring flood.")),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [QualityFixtureIds.NoteMarginChapterTwo],
                        MinEvidence: 1)),
                new QualityTurnSpec(
                    "Quote that margin passage back exactly.",
                    () => QualityContexts.ReaderEbook(
                        QualityFixtureIds.BookCartographer,
                        QualityFixtureIds.TitleCartographer,
                        "epubcfi(/6/4[chap02]!/4/2/6)"),
                    QualityScript.Dynamic(context =>
                    {
                        var handle = context.PriorResponses[0].Evidence
                            ?.FirstOrDefault(e => e.Handle.NoteId == QualityFixtureIds.NoteMarginChapterTwo)
                            ?.Handle
                            ?? context.PriorResponses[0].Evidence
                                ?.FirstOrDefault(e => string.Equals(e.Handle.Kind, "note", StringComparison.Ordinal))
                                ?.Handle;
                        return Task.FromResult(
                            handle?.NoteId is null
                                ? QualityScript.Reply("I cannot find that passage again.")
                                : QualityScript.ToolCall(
                                    "knowledge_read_evidence",
                                    QualityArgs.ReadNote(handle.NoteId.Value)));
                    }),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_read_evidence"],
                        ForbiddenTools: [],
                        GoldNoteIds: [QualityFixtureIds.NoteMarginChapterTwo],
                        MinEvidence: 1,
                        ExpectFirstEvidenceNoteId: QualityFixtureIds.NoteMarginChapterTwo)),
            ]);
    }

    // ------------------------------------------------------------------
    // C12 — slow retrieval progress
    // ------------------------------------------------------------------

    private static QualityScenario C12()
    {
        // The provider is gated 1.2 s before answering so the turn is
        // observably slow; the product must still emit owned activity
        // (searching_material) without chain-of-thought or agent theatre.
        // Deterministic proves the plumbing; live measures real TTFA.
        return new QualityScenario(
            "C12",
            "Slow retrieval progress",
            "C12 — slow retrieval progress: useful activity is visible without chain-of-thought or agent theatre.",
            "Product-owned activity is visible while retrieval is slow.",
            "Did activity appear promptly and read as product language (searching, saving) rather than reasoning traces? " +
            "Was there any agent theatre — redundant or self-narrating updates?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic proves activity plumbing under a gated provider; live measures real time-to-first-activity.",
            [
                new QualityTurnSpec(
                    "Search everything I have about rope work and boat repair.",
                    QualityContexts.SecondBrain,
                    QualityScript.Dynamic(async context =>
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1200), context.CancellationToken);
                        return context.CallIndex == 0
                            ? QualityScript.ToolCall(
                                "knowledge_search",
                                QualityArgs.Search("rope boat repair work"))
                            : QualityScript.Reply("Rope work and boat repair, from your material.");
                    }),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        MinEvidence: 2),
                    Verify: context =>
                    {
                        var known = new HashSet<string>(StringComparer.Ordinal)
                        {
                            "searching_material", "searching_books", "opening_evidence",
                            "saving_note", "updating_collection", "linking_note",
                            "applying_action", "checking_library", "searching_notes",
                            "opening_source",
                        };
                        var unknown = context.Turn.Activities
                            .Select(a => a.Code)
                            .Where(code => !known.Contains(code))
                            .ToList();
                        if (unknown.Count > 0)
                            return Task.FromResult<string?>(
                                $"non-product activity codes: [{string.Join(",", unknown)}]");
                        if (context.Turn.Activities.Count == 0)
                            return Task.FromResult<string?>("no activity events on a slow retrieval turn");
                        if (context.Mode == QualityModes.Live)
                            return Task.FromResult<string?>(null);
                        // Deterministic only: the gate proves the activity
                        // arrived while retrieval was still slow.
                        return Task.FromResult<string?>(context.Turn.TtfActivityMs > 500
                            ? null
                            : $"first activity at {context.Turn.TtfActivityMs:F0} ms, expected it during the 1200 ms gated wait");
                    }),
            ]);
    }

    // ------------------------------------------------------------------
    // C13 — cancel before mutation
    // ------------------------------------------------------------------

    private static QualityScenario C13()
    {
        // The provider blocks before any tool runs; the runner cancels via
        // POST /turn/cancel. Deterministic-only: live timing cannot place
        // the stop reliably before the write.
        return new QualityScenario(
            "C13",
            "Cancel before mutation",
            "C13 — cancel before mutation: no mutation.",
            "Stopping before any write leaves the library untouched.",
            "N/A for human scoring (mechanical invariant).",
            QualityModes.Deterministic,
            "Deterministic-only: the stop must land before the write, which only a gated provider can guarantee.",
            [
                new QualityTurnSpec(
                    "Remember this: the spare key is under the third flowerpot.",
                    () => QualityContexts.ReaderEbook(
                        QualityFixtureIds.BookSaltMeridian,
                        QualityFixtureIds.TitleSaltMeridian,
                        "epubcfi(/6/4[chap03]!/4/2/6)"),
                    QualityScript.Dynamic(async context =>
                    {
                        context.Gate.SignalEntered();
                        await Task.Delay(Timeout.Infinite, context.CancellationToken);
                        return QualityScript.Reply(string.Empty);
                    }),
                    new QualityTurnExpect(
                        RequiredTools: [],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        MaxUpstreamCalls: 1,
                        MaxToolCalls: 0,
                        ExpectedErrorCode: AssistantErrorCodes.TurnCancelled,
                        ExpectedErrorMessageContains: "Stopped"),
                    Hook: QualityCatalogue.CancelWhenEntered),
            ]);
    }

    // ------------------------------------------------------------------
    // C14 — cancel after committed safe mutation
    // ------------------------------------------------------------------

    private static QualityScenario C14()
    {
        // The capture commits (mutations run on CancellationToken.None past
        // the write boundary), then the provider blocks and the runner
        // stops the turn. The applied change must remain and be reported
        // truthfully. Deterministic-only, same timing reason as C13.
        return new QualityScenario(
            "C14",
            "Cancel after committed safe mutation",
            "C14 — cancel after committed safe mutation: applied change remains and is reported truthfully.",
            "The committed note survives the stop and the error says so.",
            "N/A for human scoring (mechanical invariant).",
            QualityModes.Deterministic,
            "Deterministic-only: the stop must land after commit but before turn end, which only a gated provider can guarantee.",
            [
                new QualityTurnSpec(
                    "Remember this: the spare oar is behind the boathouse door.",
                    () => QualityContexts.ReaderEbook(
                        QualityFixtureIds.BookSaltMeridian,
                        QualityFixtureIds.TitleSaltMeridian,
                        "epubcfi(/6/4[chap03]!/4/2/6)"),
                    QualityScript.Dynamic(async context =>
                    {
                        if (context.CallIndex == 0)
                            return QualityScript.ToolCall(
                                "notes_capture",
                                QualityArgs.Capture(
                                    "the spare oar is behind the boathouse door.",
                                    QualityFixtureIds.BookSaltMeridian));
                        context.Gate.SignalEntered();
                        await Task.Delay(Timeout.Infinite, context.CancellationToken);
                        return QualityScript.Reply(string.Empty);
                    }),
                    new QualityTurnExpect(
                        RequiredTools: ["notes_capture"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ExpectCapturedNote: true,
                        ExpectedNoteDelta: 1,
                        ExpectedErrorCode: AssistantErrorCodes.TurnCancelled,
                        ExpectedErrorMessageContains: "remain"),
                    Hook: QualityCatalogue.CancelWhenEntered),
            ]);
    }

    internal static async Task CancelWhenEntered(QualityHookContext hook)
    {
        var entered = await Task.WhenAny(
            hook.Gate.Entered, Task.Delay(TimeSpan.FromSeconds(20)));
        if (!ReferenceEquals(entered, hook.Gate.Entered))
            throw new TimeoutException("quality bed: provider never entered the gated call");
        using var response = await hook.Client.PostAsJsonAsync(
            AssistantEndpoints.CancelTurnRoute,
            new AssistantTurnCancelRequest(hook.ConversationId, hook.TurnId));
        response.EnsureSuccessStatusCode();
    }

    // ------------------------------------------------------------------
    // C15 — typed operational failures
    // ------------------------------------------------------------------

    private static QualityScenario C15()
    {
        // Each turn injects one typed failure and asserts the product
        // surface, never a crash. Deterministic-only: live failures cannot
        // be produced on demand (and must never be: provider errors in live
        // mode are recorded as data, never forced).
        //
        // Allowance/quota (ai_usage_limit_reached) is exercised by the
        // existing AssistantEndpointTests streaming test that swaps in a
        // blocking usage service (AssistantEndpointTests.cs ~line 344); it is
        // a host-service behavior, not a provider behavior, so re-driving it
        // here would duplicate that test rather than extend coverage.
        return new QualityScenario(
            "C15",
            "Typed operational failures",
            "C15 — typed operational failures: timeout / rate limit / allowance or quota / offline / provider failure / indexing pending / no evidence.",
            "Every failure class surfaces as typed data on the turn.",
            "N/A for human scoring (mechanical invariant).",
            QualityModes.Deterministic,
            "Deterministic-only: failures are injected; live mode records natural failures as data instead.",
            [
                C15FailureTurn(
                    "Summarize the rope entries (this turn will time out).",
                    QualityScript.Throw(LlmException.TimedOut()),
                    LlmErrorCodes.Timeout),
                C15FailureTurn(
                    "Summarize the rope entries (this turn will be rate limited).",
                    QualityScript.Throw(LlmException.RateLimited()),
                    LlmErrorCodes.RateLimited),
                C15FailureTurn(
                    "Summarize the rope entries (the provider will fail).",
                    QualityScript.Throw(LlmException.ProviderFailure("stub HTTP 500")),
                    LlmErrorCodes.Provider),
                C15FailureTurn(
                    "Summarize the rope entries (the gateway is unreachable).",
                    // The product wraps an unreachable gateway as a provider
                    // failure with the transport detail (NineRouterLlmProvider:
                    // HttpRequestException -> ProviderFailure); the bed
                    // injects the wrapped shape directly.
                    QualityScript.Throw(LlmException.ProviderFailure(
                        "the gateway could not be reached (no route to host)")),
                    LlmErrorCodes.Provider),
                new QualityTurnSpec(
                    "Search the granary ledger for rye totals.",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search(
                                "quasar brass abacus zephyr",
                                QualityFixtureIds.BookGranaryLedger)),
                        QualityScript.Reply("The ledger is still being indexed.")),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ExpectedErrorCode: AssistantErrorCodes.SourceIndexingPending)),
                new QualityTurnSpec(
                    "What did I conclude about quasar brass abacus zephyr alignments?",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search(
                                "quasar brass abacus zephyr",
                                QualityFixtureIds.BookSaltMeridian,
                                QualityFixtureIds.BookCartographer)),
                        QualityScript.Reply("I could not find usable evidence for that.")),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ExpectedErrorCode: AssistantErrorCodes.NoEvidence)),
            ]);
    }

    private static QualityTurnSpec C15FailureTurn(
        string message,
        Func<QualityScriptContext, Task<LlmCompletion>> script,
        string expectedCode) =>
        new(
            message,
            QualityContexts.SecondBrain,
            script,
            new QualityTurnExpect(
                RequiredTools: [],
                ForbiddenTools: [],
                GoldNoteIds: [],
                MaxUpstreamCalls: 1,
                MaxToolCalls: 0,
                ExpectedErrorCode: expectedCode));
}
