// Scenarios C6–C10 (issue #566).
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Ai;

internal static partial class QualityCatalogue
{
    // ------------------------------------------------------------------
    // C6 — missing-book continuation
    // ------------------------------------------------------------------

    private static QualityScenario C6()
    {
        // The book is the app's: with no open book the product asks, and the
        // exact title answer resolves deterministically (ExactMatch). The
        // model never chooses the target.
        return new QualityScenario(
            "C6",
            "Missing-book continuation",
            "C6 — missing-book continuation: real continuation turn; deterministic target resolution.",
            "The titled answer files the note against The Salt Meridian.",
            "Is the resolution stated plainly and correctly? Would an ambiguous answer have been re-asked rather than guessed?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic proves ask -> exact-match resolution -> capture; live scores the acknowledgement.",
            [
                new QualityTurnSpec(
                    "Remember this: the granary key hangs behind the ledger.",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "notes_capture",
                            """{"content":"the granary key hangs behind the ledger."}"""),
                        QualityScript.Reply("Which book?")),
                    new QualityTurnExpect(
                        RequiredTools: ["notes_capture"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ReplyMustContain: [AssistantOrchestrator.WhichBookQuestion],
                        ExpectedAnchorPromptKind: AssistantOrchestrator.BookPromptKind),
                    Verify: context => Task.FromResult(QualityVerify.NoExecuted(context.Turn))),
                new QualityTurnSpec(
                    "The Salt Meridian",
                    QualityContexts.SecondBrain,
                    null,
                    new QualityTurnExpect(
                        RequiredTools: [],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ExpectCapturedNote: true,
                        ExpectedNoteDelta: 1),
                    ContinueFromPrompt: true,
                    Verify: async context =>
                        QualityVerify.NoProviderCall(context.Turn)
                        ?? await QualityVerify.CapturedNoteBook(context, QualityFixtureIds.BookSaltMeridian)
                        ?? await QualityVerify.CapturedNoteAnchor(context, "unknown", null, verified: false)),
            ]);
    }

    // ------------------------------------------------------------------
    // C7 — low lexical overlap
    // ------------------------------------------------------------------

    private static QualityScenario C7()
    {
        // The gold note shares exactly two content tokens ("keeper",
        // "night" via "midnight") with a nine-token question. Deterministic
        // proves the ranked fusion still surfaces it first and the turn
        // carries its handle; live scores whether the model uses it.
        return new QualityScenario(
            "C7",
            "Low lexical overlap",
            "C7 — low lexical overlap: gold relevant note/passage deliberately differs in wording from the question.",
            "The keeper note is retrieved first despite minimal shared wording.",
            "Does the answer use the keeper note rather than guessing or drifting to unrelated material? " +
            "Is the low-overlap connection made explicit instead of overstated?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic asserts ranked retrieval surfaces the gold note first; live scores its use.",
            [
                new QualityTurnSpec(
                    "What did the keeper say on night watch about staying focused?",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search("What did the keeper say on night watch about staying focused?")),
                        QualityScript.Reply(
                            "Your keeper entry says a steady lamp is kept, never chased — dusk trimming, one midnight round.")),

                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [QualityFixtureIds.NoteKeeper],
                        MinEvidence: 1,
                        ExpectFirstEvidenceNoteId: QualityFixtureIds.NoteKeeper)),
            ]);
    }

    // ------------------------------------------------------------------
    // C8 — distractor retrieval
    // ------------------------------------------------------------------

    private static QualityScenario C8()
    {
        // Three distractors each share one surface word; the gold shares all
        // four query tokens, so fusion ranks it first. Deterministic asserts
        // the ranking and that distractors coexist in evidence (the live
        // model must still prefer the gold).
        return new QualityScenario(
            "C8",
            "Distractor retrieval",
            "C8 — distractor retrieval: lexical distractors coexist with stronger multi-term/relevant evidence.",
            "The lantern-walk note ranks first with all distractors present but subordinate.",
            "Does the answer center the lantern-walk entry and resist the harbor-dues, lantern-gift, and evening-train distractors? " +
            "Are the distractors ignored or explicitly set aside rather than blended in?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic asserts multi-term fusion ranking; live scores distractor resistance.",
            [
                new QualityTurnSpec(
                    "Where is my best thinking spot on an evening walk near the harbor lantern?",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search("evening harbor lantern walk thinking spot")),
                        QualityScript.Reply(
                            "The lantern room stairs at the end of your evening walk past the harbor.")),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [QualityFixtureIds.NoteLanternWalk],
                        CoexistenceNoteIds:
                        [
                            QualityFixtureIds.NoteDistHarborDues,
                            QualityFixtureIds.NoteDistLanternGift,
                            QualityFixtureIds.NoteDistEveningTrain,
                        ],
                        MinEvidence: 4,
                        LiveMinEvidence: 1,
                        ExpectFirstEvidenceNoteId: QualityFixtureIds.NoteLanternWalk)),
            ]);
    }

    // ------------------------------------------------------------------
    // C9 — cross-book evidence comparison
    // ------------------------------------------------------------------

    private static QualityScenario C9()
    {
        // Both mornings notes share the query's only content token. The
        // structural check is cross-book co-presence; preserving the frost
        // qualification (faithful comparison, not grand synthesis) is scored
        // by the rubric in live mode.
        return new QualityScenario(
            "C9",
            "Cross-book evidence comparison",
            "C9 — cross-book evidence comparison: surface the relevant passages/notes and preserve an important qualification or tension (faithful comparison, not a grand synthesis).",
            "Both mornings notes surface across the book boundary.",
            "Does the answer present BOTH entries with their tension intact (mend-before-breakfast vs never-work-mornings) AND preserve the frost qualification on the mending side? " +
            "Does it avoid inventing a grand synthesis that dissolves the disagreement?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic asserts cross-book co-presence; live scores qualification-preserving comparison.",
            [
                new QualityTurnSpec(
                    "What did I write about mornings?",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search("write mornings")),
                        QualityScript.Reply(
                            "Two entries pull apart: mending before breakfast while the frost holds, versus walking and never starting work.")),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds:
                        [
                            QualityFixtureIds.NoteMendingMornings,
                            QualityFixtureIds.NoteWalkingMornings,
                        ],
                        MinEvidence: 2)),
            ]);
    }

    // ------------------------------------------------------------------
    // C10 — no evidence
    // ------------------------------------------------------------------

    private static QualityScenario C10()
    {
        // The gibberish query matches nothing stored. The product — not the
        // model — owns the insufficiency verdict (assistant_no_evidence).
        // Scoped to Ready books: the unscoped library also contains the
        // Pending B7 shell, which would (correctly) report indexing-pending
        // instead; that interaction is C15's subject, not this scenario's.
        return new QualityScenario(
            "C10",
            "No evidence",
            "C10 — no evidence: assistant clearly states what the available Nostos material does not establish.",
            "The turn ends in typed no-evidence, never a fabricated answer.",
            "Does the answer plainly state what the material does not establish, without hedging into a fabricated guess? " +
            "Does it avoid presenting the absence as a positive claim?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic asserts the typed no-evidence surface; live scores the insufficiency statement.",
            [
                new QualityTurnSpec(
                    "What did I conclude about quasar brass abacus zephyr alignments?",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search(
                                "quasar brass abacus zephyr",
                                QualityFixtureIds.BookSaltMeridian,
                                QualityFixtureIds.BookCartographer,
                                QualityFixtureIds.BookQuietMornings,
                                QualityFixtureIds.BookWoodenBoats,
                                QualityFixtureIds.BookSleeperCar,
                                QualityFixtureIds.BookMarginalia)),
                        QualityScript.Reply("I could not find usable evidence for that in your Nostos material.")),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ReplyMustContain: ["could not find"],
                        ExpectedErrorCode: AssistantErrorCodes.NoEvidence,
                        AlsoAcceptErrorCodes:
                        [
                            AssistantErrorCodes.SourceIndexingPending,
                            AssistantErrorCodes.SourceIndexingUnsupported,
                            AssistantErrorCodes.SourceIndexingFailed,
                        ])),
            ]);
    }
}
