// Scenarios C1–C5 (issue #566).
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using Microsoft.EntityFrameworkCore;

internal static partial class QualityCatalogue
{
    public static IReadOnlyList<QualityScenario> All =>
    [
        C1(), C2(), C3(), C4(), C5(), C6(), C7(), C8(), C9(), C10(),
        C11(), C12(), C13(), C14(), C15(), C16(), C17(), C18(), C19(), C20(), C21(),
    ];

    public static QualityScenario ById(string id) =>
        All.First(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------
    // C1 — sustained conversation
    // ------------------------------------------------------------------

    private static QualityScenario C1()
    {
        // Deterministic: proves evidence-handle stability and history threading
        // across 22 turns (the scripted provider plays realistic retrieval +
        // capture sequences; the product owns storage, history, and handles).
        // Live: measures whether the real model still grounds the early
        // callback after sustained depth.
        var turns = new List<QualityTurnSpec>
        {
            new(
                "How should I coil the mooring ropes?",
                () => QualityContexts.ReaderEbook(
                    QualityFixtureIds.BookSaltMeridian,
                    QualityFixtureIds.TitleSaltMeridian,
                    "epubcfi(/6/4[chap03]!/4/2/6)"),
                QualityScript.Play(
                    QualityScript.ToolCall(
                        "knowledge_search",
                        QualityArgs.Search("coil mooring ropes")),
                    QualityScript.Reply(
                        "Coil each rope clockwise and flake the ropes left to right across the thwart.")),
                new QualityTurnExpect(
                    RequiredTools: ["knowledge_search"],
                    ForbiddenTools: [],
                    GoldNoteIds: [QualityFixtureIds.NoteRopeCoil],
                    MinEvidence: 1,
                    GoldBookPassages:
                    [
                        (QualityFixtureIds.BookSaltMeridian.ToString(), ["coiling lines"]),
                    ])),
        };
        turns.AddRange(C1Fillers());
        turns.Add(new QualityTurnSpec(
            "Going back to that rope-coiling passage from the very start of this conversation — which book was it in?",
            () => QualityContexts.ReaderEbook(
                QualityFixtureIds.BookSaltMeridian,
                QualityFixtureIds.TitleSaltMeridian,
                "epubcfi(/6/4[chap03]!/4/2/6)"),
            QualityScript.Dynamic(context =>
            {
                var handle = context.PriorResponses[0].Evidence
                    ?.FirstOrDefault(e => string.Equals(e.Handle.Kind, "note", StringComparison.Ordinal))
                    ?.Handle;
                if (handle?.NoteId is null)
                    return Task.FromResult(QualityScript.Reply("I cannot find that passage again."));
                return Task.FromResult(QualityScript.ToolCall(
                    "knowledge_read_evidence",
                    QualityArgs.ReadNote(handle.NoteId.Value)));
            }),
            new QualityTurnExpect(
                RequiredTools: ["knowledge_read_evidence"],
                ForbiddenTools: [],
                GoldNoteIds: [QualityFixtureIds.NoteRopeCoil],
                MinEvidence: 1,
                ExpectFirstEvidenceNoteId: QualityFixtureIds.NoteRopeCoil,
                GoldBookPassages:
                [
                    (QualityFixtureIds.BookSaltMeridian.ToString(), ["coiling lines"]),
                ])));

        return new QualityScenario(
            "C1",
            "Sustained conversation",
            "C1 — sustained conversation: 20–30 turns with an earlier material/reference mentioned again after many later exchanges.",
            "The final follow-up re-reads the exact turn-1 evidence handle and stays tied to the correct book.",
            "Does the closing answer name the right book and passage without drifting to filler material? " +
            "Is the callback specific (rope-coiling, Salt Meridian) rather than a generic summary of the session?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic proves handle stability + history threading over 22 turns; live scores callback grounding.",
            turns);
    }

    private static IEnumerable<QualityTurnSpec> C1Fillers()
    {
        // 20 filler turns (10 captures, 10 lookups) between the opening
        // question and the late callback. Contents avoid every other
        // scenario's query tokens so this scenario's DB stays self-contained.
        string[] captures =
        [
            "Bread, salt, lamp oil for the crossing.",
            "The spare chart is rolled behind the stove.",
            "Ask the ferryman about the dusk tide.",
            "Mend the blue mitten before the frost.",
            "The stove draws best with birch bark.",
            "Return the borrowed auger on Sunday.",
            "Salt pork and peas for the crew chest.",
            "The cabin lamp smokes when the wind backs.",
            "Count the fenders before we cast off.",
            "A button for the grey coat.",
        ];
        (string Message, string Query)[] questions =
        [
            ("What did I jot about the crossing stores?", "crossing stores provisions"),
            ("Where did I leave the spare chart?", "spare chart stove"),
            ("What did the ferryman say about the tide?", "ferryman dusk tide"),
            ("What needs mending before the frost?", "mitten frost mending"),
            ("How do I get the stove going?", "stove birch bark"),
            ("What must I return on Sunday?", "borrowed auger Sunday"),
            ("What goes in the crew chest?", "crew chest salt pork"),
            ("Why does the cabin lamp smoke?", "cabin lamp smoke wind"),
            ("What do I check before casting off?", "fenders cast off"),
            ("What does the grey coat need?", "grey coat button"),
        ];

        for (var i = 0; i < 10; i++)
        {
            var content = captures[i];
            yield return new QualityTurnSpec(
                $"Remember this: {content}",
                () => QualityContexts.ReaderEbook(
                    i % 2 == 0 ? QualityFixtureIds.BookSaltMeridian : QualityFixtureIds.BookCartographer,
                    i % 2 == 0 ? QualityFixtureIds.TitleSaltMeridian : QualityFixtureIds.TitleCartographer,
                    "epubcfi(/6/4[fill]!/4/2/2)"),
                QualityScript.Play(
                    QualityScript.ToolCall(
                        "notes_capture",
                        QualityArgs.Capture(
                            content,
                            i % 2 == 0 ? QualityFixtureIds.BookSaltMeridian : QualityFixtureIds.BookCartographer)),
                    QualityScript.Reply("Kept.")),
                new QualityTurnExpect(
                    RequiredTools: ["notes_capture"],
                    ForbiddenTools: [],
                    GoldNoteIds: [],
                    ExpectCapturedNote: true,
                    ExpectedNoteDelta: 1));

            var (message, query) = questions[i];
            yield return new QualityTurnSpec(
                message,
                QualityContexts.SecondBrain,
                QualityScript.Play(
                    QualityScript.ToolCall("knowledge_search", QualityArgs.Search(query)),
                    QualityScript.Reply("Here is what I found in your material, with the usual uncertainty where it is thin.")),
                new QualityTurnExpect(
                    RequiredTools: ["knowledge_search"],
                    ForbiddenTools: [],
                    GoldNoteIds: [],
                    ReplyMustNotContain: ["I saved", "I've saved", "has been deleted", "I deleted", "I created", "I've created"]));
        }
    }

    // ------------------------------------------------------------------
    // C2 — long-answer continuity
    // ------------------------------------------------------------------

    private static QualityScenario C2()
    {
        // Deterministic: proves the full prior assistant text (2,400+ chars)
        // reaches the provider on the follow-up turn — the old 2,000-char
        // history cap must not truncate the point the follow-up references.
        // Live: scores whether the model actually uses that tail material.
        const string tailMarker = "never trim a lit wick";
        return new QualityScenario(
            "C2",
            "Long-answer continuity",
            "C2 — long-answer continuity: follow-up references a point near the end of a long explanation that would have exceeded the old 2,000-character history cap.",
            "The follow-up turn observes the complete long explanation, including its final point.",
            "Does the follow-up answer engage the third rule specifically, rather than restarting with generic lamp advice? " +
            "Is the long explanation itself coherent across its full length?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic asserts the full long text is threaded into the follow-up provider request; live scores use.",
            [
                new QualityTurnSpec(
                    "Explain the three rules of the gallery watch.",
                    () => QualityContexts.ReaderEbook(
                        QualityFixtureIds.BookSaltMeridian,
                        QualityFixtureIds.TitleSaltMeridian,
                        "epubcfi(/6/4[chap05]!/4/2/6)"),
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search("three rules gallery watch")),
                        QualityScript.Reply(C2LongExplanation())),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [QualityFixtureIds.NoteGalleryEssay],
                        MinEvidence: 1)),
                new QualityTurnSpec(
                    "Why does that third rule about the lit wick matter?",
                    () => QualityContexts.ReaderEbook(
                        QualityFixtureIds.BookSaltMeridian,
                        QualityFixtureIds.TitleSaltMeridian,
                        "epubcfi(/6/4[chap05]!/4/2/6)"),
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search("third rule lit wick matter")),
                        QualityScript.Reply(
                            "Because a wick trimmed while lit flares and smokes the glass, so the whole round begins again.")),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [QualityFixtureIds.NoteGalleryEssay],
                        MinEvidence: 1),
                    Verify: context =>
                    {
                        // Live mode cannot observe provider requests; the
                        // evaluator's gold-evidence check still applies there.
                        if (context.Mode == QualityModes.Live)
                            return Task.FromResult<string?>(null);
                        var marker = context.Turn.ProviderRequests
                            .SelectMany(r => r.Messages)
                            .Where(m => m.Content is not null && m.Content.Length > 2000)
                            .FirstOrDefault(m => m.Content!.Contains(tailMarker, StringComparison.Ordinal));
                        return Task.FromResult<string?>(marker is null
                            ? $"follow-up provider request has no >2000-char message containing '{tailMarker}'"
                            : null);
                    }),
            ]);
    }

    internal static string C2LongExplanation()
    {
        var paragraphs = new[]
        {
            "The gallery round keeps three rules. The first rule concerns the oil can: carry it in the left hand, so the right stays free for the rail, because every door on the ascent opens against the climber and a bruised elbow is the least of what a fall costs. ",
            "The second rule concerns the vent: open it before the lamp is lit, never after. A lamp lit inside a closed gallery smokes within minutes, and the soot takes a full round of polishing to lift from the glass, while a grey pane quietly steals half the light it should throw. ",
            "Between the rules sit the smaller habits that make the round survivable across a season. Count the panes as you pass, all fourteen of them, and tap each frame once to hear whether the putty still holds. Feel each door for the wind before trusting the flame to it. Hum while climbing, so the next watch hears you coming and holds the hatch open. None of this is difficult on its own; it is a circuit of small cautions, and each one was paid for by someone who skipped it exactly once. The old watch kept a slate of mishaps beside the oil store, and nearly every line on it ends with the same lesson, namely that haste at height costs more than patience ever will. ",
            "The slate is worth reading in full on a quiet round, because the entries repeat with small variations and the variations are where the craft hides. One watch learned to test the rail with a full hand before committing weight to it. Another learned to carry a spare wick wrapped dry, after a damp one crumbled at the worst hour. A third learned that cold glass fogs when the door opens too fast, and now warms each pane with a palm before lifting it. These are not rules in the formal sense. They are the sediment of attention, settling year over year into something a newcomer can stand on. ",
            "All of that preparation exists for the sake of the third rule, which governs the wick itself, and which I place last because everything before it is merely the price of reaching it calmly: never trim a lit wick. That is the point that matters most. ",
        };
        var text = string.Concat(paragraphs);
        if (text.Length < 2000)
            throw new InvalidOperationException($"C2 explanation too short: {text.Length}");
        return text;
    }

    // ------------------------------------------------------------------
    // C3 — application-context switch
    // ------------------------------------------------------------------

    private static QualityScenario C3()
    {
        // Deterministic: proves the explicit back-reference ("Back in The
        // Salt Meridian") scopes retrieval to book A while the ambient
        // context resolves to book B, and that history carries both contexts.
        // Live: scores whether the model keeps "this book" = B while
        // answering from A.
        return new QualityScenario(
            "C3",
            "Application-context switch",
            "C3 — application-context switch: discuss Book A → navigate to Book B → refer explicitly back to A while \"this book\" resolves to B.",
            "Turn 3 retrieves from book A despite the ambient context pointing at book B.",
            "Does the third answer draw on The Salt Meridian while keeping the current-book framing on The Cartographer's Daughter? " +
            "Is there any confusion about which book is open?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic asserts scoped retrieval + history threading; live scores the referential precision.",
            [
                new QualityTurnSpec(
                    "How do I coil the ropes here?",
                    () => QualityContexts.ReaderEbook(
                        QualityFixtureIds.BookSaltMeridian,
                        QualityFixtureIds.TitleSaltMeridian,
                        "epubcfi(/6/4[chap03]!/4/2/6)"),
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search("coil ropes mooring", QualityFixtureIds.BookSaltMeridian)),
                        QualityScript.Reply("Coil each rope clockwise, flakes left to right.")),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [QualityFixtureIds.NoteRopeCoil],
                        MinEvidence: 1,
                        GoldBookPassages:
                        [
                            (QualityFixtureIds.BookSaltMeridian.ToString(), ["coiling lines"]),
                        ])),
                new QualityTurnSpec(
                    "Now in this book: what do the map margins say about the redrawn river?",
                    () => QualityContexts.ReaderEbook(
                        QualityFixtureIds.BookCartographer,
                        QualityFixtureIds.TitleCartographer,
                        "epubcfi(/6/4[chap02]!/4/2/6)"),
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search("map margins redrawn river", QualityFixtureIds.BookCartographer)),
                        QualityScript.Reply("The ferry crossing moved upstream after the spring flood.")),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [QualityFixtureIds.NoteMarginChapterTwo],
                        MinEvidence: 1,
                        GoldBookPassages:
                        [
                            (QualityFixtureIds.BookCartographer.ToString(), ["moved upstream"]),
                        ])),
                new QualityTurnSpec(
                    "Back in The Salt Meridian, what did the rope passage say?",
                    () => QualityContexts.ReaderEbook(
                        QualityFixtureIds.BookCartographer,
                        QualityFixtureIds.TitleCartographer,
                        "epubcfi(/6/4[chap02]!/4/2/6)"),
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search("rope passage coiling", QualityFixtureIds.BookSaltMeridian)),
                        QualityScript.Reply("Back in The Salt Meridian: coil clockwise, flakes left to right.")),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [QualityFixtureIds.NoteRopeCoil],
                        MinEvidence: 1,
                        GoldBookPassages:
                        [
                            (QualityFixtureIds.BookSaltMeridian.ToString(), ["coiling lines"]),
                        ]),
                    Verify: async context =>
                    {
                        // Live mode cannot observe provider requests, and an
                        // unscoped-but-correct model answer is still correct:
                        // scope precision is scored by the rubric there.
                        if (context.Mode == QualityModes.Live)
                            return null;
                        // The scoped call really executed: every surfaced note
                        // must belong to book A even though the ambient
                        // context points at book B.
                        await using var db = await QualityVerify.Db(context.Services);
                        var noteIds = (context.Turn.Response?.Evidence ?? [])
                            .Where(e => string.Equals(e.Handle.Kind, "note", StringComparison.Ordinal)
                                && e.Handle.NoteId.HasValue)
                            .Select(e => e.Handle.NoteId!.Value)
                            .ToList();
                        if (noteIds.Count == 0) return "no note evidence to scope-check";
                        var books = await db.Notes
                            .Where(n => noteIds.Contains(n.Id))
                            .Select(n => n.BookId)
                            .ToListAsync();
                        if (books.Any(b => b != QualityFixtureIds.BookSaltMeridian))
                            return "evidence leaked outside the explicitly referenced book";
                        // And history still shows the turn-1 question, so the
                        // back-reference had conversational grounding.
                        var sawFirst = context.Turn.ProviderRequests
                            .SelectMany(r => r.Messages)
                            .Any(m => m.Content?.Contains("How do I coil the ropes here?", StringComparison.Ordinal) == true);
                        return sawFirst ? null : "turn-1 question missing from turn-3 provider history";
                    }),
            ]);
    }

    // ------------------------------------------------------------------
    // C4 — physical-page continuation
    // ------------------------------------------------------------------

    private static QualityScenario C4()
    {
        // Real continuation turn through the product path: prompt, answer,
        // exactly-once capture. Deterministic asserts the mechanism end to
        // end; live additionally scores the acknowledgement prose.
        return new QualityScenario(
            "C4",
            "Physical-page continuation",
            "C4 — physical-page continuation: real continuation turn; exactly-once capture.",
            "One note is captured with the user-typed page, unverified, exactly once.",
            "Is the acknowledgement specific to the book and honest about the page being user-supplied? " +
            "Does anything suggest the page was verified when it was not?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic proves prompt -> answer -> exactly-once capture; live scores acknowledgement truthfulness.",
            [
                new QualityTurnSpec(
                    "Remember this: linseed oil first, then wax.",
                    () => QualityContexts.PhysicalBook(
                        QualityFixtureIds.BookQuietMornings,
                        QualityFixtureIds.TitleQuietMornings),
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "notes_capture",
                            QualityArgs.Capture(
                                "linseed oil first, then wax.",
                                QualityFixtureIds.BookQuietMornings)),
                        QualityScript.Reply("On what page?")),
                    new QualityTurnExpect(
                        RequiredTools: ["notes_capture"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ReplyMustContain: ["What page are you on?"],
                        ExpectedAnchorPromptKind: "physical_page"),
                    Verify: context => Task.FromResult(QualityVerify.NoExecuted(context.Turn))),
                new QualityTurnSpec(
                    "247",
                    () => QualityContexts.PhysicalBook(
                        QualityFixtureIds.BookQuietMornings,
                        QualityFixtureIds.TitleQuietMornings),
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
                        ?? await QualityVerify.CapturedNoteAnchor(context, "physical_page", "247", verified: false)),
            ]);
    }

    // ------------------------------------------------------------------
    // C5 — external-audio continuation
    // ------------------------------------------------------------------

    private static QualityScenario C5()
    {
        // Same mechanism as C4 for the external-audio timestamp shape.
        return new QualityScenario(
            "C5",
            "External-audio continuation",
            "C5 — external-audio continuation: real continuation turn; exactly-once capture.",
            "One note is captured with the user-typed timestamp, exactly once.",
            "Is the acknowledgement specific and honest about the timestamp being user-supplied? " +
            "Does anything present the timestamp as app-measured?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic proves prompt -> answer -> exactly-once capture; live scores acknowledgement truthfulness.",
            [
                new QualityTurnSpec(
                    "Remember this: mark the gull passage ending for the chapter list.",
                    () => QualityContexts.ExternalAudio(
                        QualityFixtureIds.BookSleeperCar,
                        QualityFixtureIds.TitleSleeperCar),
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "notes_capture",
                            QualityArgs.Capture(
                                "mark the gull passage ending for the chapter list.",
                                QualityFixtureIds.BookSleeperCar)),
                        QualityScript.Reply("Which timestamp?")),
                    new QualityTurnExpect(
                        RequiredTools: ["notes_capture"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ReplyMustContain: ["What's the current timestamp?"],
                        ExpectedAnchorPromptKind: "external_audio_timestamp"),
                    Verify: context => Task.FromResult(QualityVerify.NoExecuted(context.Turn))),
                new QualityTurnSpec(
                    "12:40",
                    () => QualityContexts.ExternalAudio(
                        QualityFixtureIds.BookSleeperCar,
                        QualityFixtureIds.TitleSleeperCar),
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
                        // User-typed "12:40" is normalized to seconds by the
                        // product (760); it is never marked verified.
                        ?? await QualityVerify.CapturedNoteAnchor(
                            context, "external_audio_timestamp", "760", verified: false)),
            ]);
    }
}
