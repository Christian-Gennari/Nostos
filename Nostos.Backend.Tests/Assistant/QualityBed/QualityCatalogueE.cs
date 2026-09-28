// Scenario C21 (issue #566): Swedish-language probe.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

internal static partial class QualityCatalogue
{
    private const string C21Message =
        "Anteckna: kvällspromenaden går förbi fyrrummet — nej, förbi fyren — precis innan det blir mörkt.";

    private const string C21CapturedContent =
        "kvällspromenaden går förbi fyrrummet — nej, förbi fyren — precis innan det blir mörkt.";

    private const string C21SearchQuery = "kvällspromenaden fyrrummet fyren mörkt";

    // ------------------------------------------------------------------
    // C21 — Swedish navigation
    // ------------------------------------------------------------------

    private static QualityScenario C21()
    {
        // Swedish instruction following + Swedish query retrieval + verbatim
        // capture, not translation. Turn 0 stores the user's Swedish words
        // exactly (minus the leading "Anteckna:"); turn 1 finds the entry
        // again with a Swedish query. Deterministic proves the Swedish words
        // survive storage, retrieval-by-Swedish-query, and evidence re-read;
        // live scores whether the model follows the Swedish instruction and
        // answers from evidence rather than memory.
        //
        // SecondBrain has no open book, so a bare SecondBrain capture would
        // ask WhichBook (that ask-flow is C6's subject). The resolved title
        // rides on the SecondBrain context here to keep the probe to two
        // turns on the second-brain surface; the target book is the app's
        // (ExactMatch), never the model's.
        return new QualityScenario(
            "C21",
            "Swedish navigation",
            "C21 — Swedish navigation: Swedish instruction following + Swedish query retrieval + verbatim capture, not translation.",
            "The Swedish capture is stored verbatim and found again by a Swedish query.",
            "Does the capture preserve the Swedish words exactly, self-correction included, rather than translating or tidying them? " +
            "Does the follow-up answer from the retrieved Swedish entry rather than from memory?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic proves verbatim Swedish capture + Swedish-query retrieval; live scores Swedish instruction following.",
            [
                new QualityTurnSpec(
                    C21Message,
                    () => QualityContexts.SecondBrain() with
                    {
                        CaptureBookTitle = QualityFixtureIds.TitleSaltMeridian,
                    },
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "notes_capture",
                            new JsonObject { ["content"] = C21CapturedContent }.ToJsonString()),
                        QualityScript.Reply("Antecknat.")),
                    new QualityTurnExpect(
                        RequiredTools: ["notes_capture"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ExpectCapturedNote: true,
                        ExpectedNoteDelta: 1),
                    Verify: async context =>
                    {
                        if (!Guid.TryParse(context.Turn.Response?.CapturedNoteId, out var noteId))
                            return "no captured note id on the turn";
                        await using var db = await QualityVerify.Db(context.Services);
                        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId);
                        if (note is null)
                            return $"captured note {noteId} not found";
                        return string.Equals(note.Content, C21CapturedContent, StringComparison.Ordinal)
                            ? null
                            : $"captured content '{note.Content}', expected verbatim '{C21CapturedContent}'";
                    }),
                new QualityTurnSpec(
                    "Vad skrev jag om kvällspromenaden?",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "knowledge_search",
                            QualityArgs.Search(C21SearchQuery)),
                        QualityScript.Reply(
                            "Du skrev att kvällspromenaden går förbi fyren precis innan det blir mörkt.")),
                    new QualityTurnExpect(
                        RequiredTools: ["knowledge_search"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        MinEvidence: 1),
                    Verify: context =>
                    {
                        var excerpt = string.Concat(
                            (context.Turn.Response?.Evidence ?? [])
                                .Select(e => e.Excerpt ?? string.Empty));
                        return Task.FromResult<string?>(excerpt.Contains("kvällspromenaden", StringComparison.OrdinalIgnoreCase)
                            ? null
                            : "search evidence does not contain the Swedish 'kvällspromenaden' marker");
                    }),
            ]);
    }
}
