// Scenarios C34, C35 and C37 (issue #1563): the panel's capability,
// committed-then-failed and empty-state-door states.
//
// The polish plan (§5) spreads C34–C38 across the layers that can prove them:
// C36 (a 20-step review arrives with its title and Apply in view) is a
// Playwright scenario in `Nostos.Frontend/e2e/assistant-tall-review.spec.ts`,
// and C38 (one failure, one sentence, one Try again, no raw code) is a
// component scenario in `Nostos.Frontend/src/app/ui/assistant/assistant.component.spec.ts`.
// Only the model-facing scenarios run through QualityRunner here.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;

internal static partial class QualityCatalogue
{
    public static IReadOnlyList<QualityScenario> CatalogueI => [C34(), C35(), C37()];

    // ------------------------------------------------------------------
    // C34 — capability disclosure
    // ------------------------------------------------------------------

    private const string C34Disclosure =
        "I can search your notes, topics and imported book text; capture thoughts and quotes; "
        + "keep the To read list; list and read Studio drafts; and organise collections and book "
        + "details, which I apply with undo or show for review first. "
        + "I cannot delete books or notes, or change settings.";

    private static QualityScenario C34()
    {
        // The deterministic lane fixes the reply (so the copy checks are exact)
        // AND checks the prompt the model really receives: the capability
        // sentence must name every registered category, and the boundary must
        // be stated. The live lane scores the model's own disclosure; a claim
        // to reach the web or email is a hard failure, while a missing positive
        // phrase is a prose advisory, as everywhere else in live mode.
        return new QualityScenario(
            "C34",
            "Capability disclosure",
            "C34 — capability disclosure: \"What can you do?\" names search, capture, organise (with Undo/Review), Studio drafts and To read, states it cannot delete books/notes or change settings, and never claims web/email.",
            "The answer describes every registered job and both boundaries, with no invented reach.",
            "Does the answer name search, capture, organising (with undo or review), Studio drafts and To read? " +
            "Does it say plainly that it cannot delete books or notes or change settings? " +
            "Does it claim any reach it does not have (web, email, the internet)?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic fixes the reply and checks the model prompt; live scores the real reply "
            + "(positive phrase misses are advisories; a web/email claim is a hard failure).",
            [
                new QualityTurnSpec(
                    "What can you do?",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(QualityScript.Reply(C34Disclosure)),
                    new QualityTurnExpect(
                        RequiredTools: [],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        MaxUpstreamCalls: 1,
                        MaxToolCalls: 0,
                        ReplyMustContain:
                        [
                            "search",
                            "capture",
                            "To read",
                            "Studio",
                            "organise",
                            "undo",
                            "review",
                            "cannot delete",
                            "settings",
                        ],
                        ReplyMustNotContain: ["web", "email"]),
                    Verify: CapabilityDisclosurePrompt),
            ]);
    }

    /// <summary>
    /// Deterministic only: the prompt the model receives carries the one
    /// capability sentence and the boundary it must never cross. Live mode has
    /// no provider request log (prompts are not recorded for a paid model), so
    /// the check returns pass there; the builder test in
    /// `AssistantCapabilityRegistryTests` proves the same sentence offline.
    /// </summary>
    private static Task<string?> CapabilityDisclosurePrompt(QualityTurnVerifyContext context)
    {
        if (context.Mode == QualityModes.Live)
            return Task.FromResult<string?>(null);

        var request = context.Turn.ProviderRequests.FirstOrDefault();
        if (request is null)
            return Task.FromResult<string?>("no provider request was recorded");

        var system = string.Join(
            "\n",
            request.Messages
                .Where(message => string.Equals(message.Role, "system", StringComparison.Ordinal))
                .Select(message => message.Content ?? string.Empty));

        if (!system.Contains(AssistantConversationBuilder.CapabilitySentence, StringComparison.Ordinal))
            return Task.FromResult<string?>("the model prompt does not carry the capability sentence");

        foreach (var boundary in new[] { "You cannot delete books", "change settings" })
        {
            if (!system.Contains(boundary, StringComparison.Ordinal))
                return Task.FromResult<string?>(
                    $"the model prompt does not state the boundary '{boundary}'");
        }

        return Task.FromResult<string?>(null);
    }

    // ------------------------------------------------------------------
    // C35 — committed, then failed
    // ------------------------------------------------------------------

    private const string C35Thought = "The harbour bell rings twice at dusk.";

    private static QualityScenario C35()
    {
        // The first provider call saves the note; the second throws. The
        // failure body must carry the committed capture (so the panel can say
        // it was saved above the failure), and replaying the same TurnId must
        // answer from the mutation receipt instead of writing a second note.
        // Deterministic-only: a provider failure cannot be forced against a
        // paid model. Gate A's `AssistantGateAPartialWriteTests` proves the
        // same contract over the REST route.
        return new QualityScenario(
            "C35",
            "Committed then failed",
            "C35 — committed-then-failed: a provider failure after a successful capture returns the capture in the failure body, the acknowledgement says it was saved, and a retry with the same TurnId creates no second note.",
            "The saved note survives the failure, is reported, and is not duplicated by the retry.",
            "N/A for human scoring (mechanical invariant).",
            QualityModes.Deterministic,
            "Deterministic-only: the capture commits and the next provider call fails on cue; "
            + "live mode records natural failures as data instead. The same-TurnId replay is also "
            + "pinned by Gate A `AssistantGateAPartialWriteTests`.",
            [
                new QualityTurnSpec(
                    $"Remember this: {C35Thought}",
                    () => QualityContexts.ReaderEbook(
                        QualityFixtureIds.BookSaltMeridian,
                        QualityFixtureIds.TitleSaltMeridian,
                        "epubcfi(/6/4[chap07]!/4/2/6)"),
                    QualityScript.Dynamic(context => context.CallIndex switch
                    {
                        0 => Task.FromResult(QualityScript.ToolCall(
                            "notes_capture",
                            QualityArgs.Capture(C35Thought, QualityFixtureIds.BookSaltMeridian))),
                        1 => Task.FromException<LlmCompletion>(
                            LlmException.ProviderFailure("upstream says no")),
                        // The retry repeats the same tool call; the server's
                        // receipt answers it and the model closes the turn.
                        2 => Task.FromResult(QualityScript.ToolCall(
                            "notes_capture",
                            QualityArgs.Capture(C35Thought, QualityFixtureIds.BookSaltMeridian))),
                        _ => Task.FromResult(QualityScript.Reply("Saved.")),
                    }),
                    new QualityTurnExpect(
                        RequiredTools: ["notes_capture"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        MaxUpstreamCalls: 2,
                        MaxToolCalls: 1,
                        ExpectedErrorCode: LlmErrorCodes.Provider,
                        ExpectedErrorMessageContains: "could not complete this turn",
                        ExpectCapturedNote: true,
                        ExpectedNoteDelta: 1),
                    Verify: CommittedCaptureSurvivesTheFailure),
            ]);
    }

    private static async Task<string?> CommittedCaptureSurvivesTheFailure(
        QualityTurnVerifyContext context)
    {
        var response = context.Turn.Response;
        if (response is null)
            return "no response on the failed turn";
        if (!Guid.TryParse(response.CapturedNoteId, out var noteId))
            return "the failure body lost the captured note id";
        if (string.IsNullOrWhiteSpace(response.Acknowledgement)
            || !response.Acknowledgement.Contains("saved", StringComparison.OrdinalIgnoreCase))
            return "the failure body does not say the note was saved";

        await using (var db = await QualityVerify.Db(context.Services))
        {
            var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == noteId);
            if (note is null)
                return $"captured note {noteId} is not in the library";
            if (!string.Equals(note.Content, C35Thought, StringComparison.Ordinal))
                return $"captured content '{note.Content}', expected the user's words";
        }

        if (context.Mode == QualityModes.Live)
            return null;
        if (context.Client is null || string.IsNullOrWhiteSpace(context.ConversationId))
            return "no HTTP client/conversation to replay the turn through";

        // The panel's Try again: the identical logical turn, a new delivery.
        var retry = await context.Client.PostAsJsonAsync(
            AssistantEndpoints.StreamTurnRoute,
            new AssistantTurnRequest(
                "quality-bed-c35",
                $"{context.Turn.TurnId}-retry",
                context.Turn.UserMessage,
                context.Context ?? new AssistantContextDto("second-brain", "/second-brain"),
                ConversationId: context.ConversationId,
                TurnId: context.Turn.TurnId));
        retry.EnsureSuccessStatusCode();

        var events = await ReadTurnEvents(retry);
        var terminal = events.LastOrDefault();
        if (terminal is null)
            return "the retry stream ended without a terminal event";
        if (!string.Equals(terminal.Kind, AssistantTurnEventKinds.Completed, StringComparison.Ordinal))
            return $"the retry ended '{terminal.Kind}' ({terminal.Failure?.Code ?? "no failure code"})";
        if (!string.Equals(terminal.Response?.CapturedNoteId, response.CapturedNoteId, StringComparison.Ordinal))
            return "the retry reported a different captured note";

        await using var verify = await QualityVerify.Db(context.Services);
        var count = await verify.Notes.AsNoTracking().CountAsync();
        var expected = context.Turn.NoteCountBefore + 1;
        return count == expected
            ? null
            : $"the same-TurnId retry wrote again: {count} notes exist, expected {expected}";
    }

    // ------------------------------------------------------------------
    // C37 — empty-state doors
    // ------------------------------------------------------------------

    private const string C37Thought = "A slack line is a question the tide answers.";

    private static QualityScenario C37()
    {
        // The component spec pins the literal door labels, the per-surface rows
        // and the rule that Capture/Organise prefill without sending. This
        // scenario drives the door texts that the page offers through the real
        // orchestrator: the Ask row must retrieve before answering, the Capture
        // row must save the user's own words, and the Organise row must stage a
        // real change. Live scores the reply each row produces; path misses are
        // advisories there, and an unrequested write stays a hard failure.
        return new QualityScenario(
            "C37",
            "Empty-state doors",
            "C37 — empty-state doors: each door prefills the composer and never sends; the library/reader/Studio rows differ; the live lane scores the answer each row produces.",
            "The Ask text is answered from the library, the Capture text is saved verbatim, and the Organise text applies one collection change.",
            "Does the Ask row's question get answered from the user's own material? " +
            "Is the Capture row saved with the user's exact words and no extra send? " +
            "Does the Organise row actually move the books instead of describing the move?",
            QualityModes.Deterministic | QualityModes.Live,
            "Component spec pins the labels, the per-surface rows and prefill-without-send; "
            + "deterministic proves each door text takes the intended product path; live scores "
            + "the answer each row produces (path and prose misses are advisories; writes stay hard).",
            [
                new QualityTurnSpec(
                    "What connects my topics?",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall("topics_list"),
                        QualityScript.Reply(
                            "Attention, seamanship and boat repair are the threads between your topics.")),
                    new QualityTurnExpect(
                        RequiredTools: ["topics_list"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        MaxToolCalls: 1,
                        ReplyMustContain: ["attention", "seamanship"])),
                new QualityTurnSpec(
                    $"Save this thought: {C37Thought}",
                    () => QualityContexts.ReaderEbook(
                        QualityFixtureIds.BookSaltMeridian,
                        QualityFixtureIds.TitleSaltMeridian,
                        "epubcfi(/6/4[chap08]!/4/2/6)"),
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "notes_capture",
                            QualityArgs.Capture(C37Thought, QualityFixtureIds.BookSaltMeridian)),
                        QualityScript.Reply("Saved.")),
                    new QualityTurnExpect(
                        RequiredTools: ["notes_capture"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        MaxToolCalls: 1,
                        ExpectCapturedNote: true,
                        ExpectedNoteDelta: 1),
                    Verify: CapturedDoorThoughtIsTheUsersWords),
                new QualityTurnSpec(
                    "Move my boat books into a collection.",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "library_create_collection",
                            new JsonObject { ["name"] = "Boat books" }.ToJsonString()),
                        QualityScript.ToolCall(
                            "library_add_books_to_collection",
                            new JsonObject
                            {
                                ["collection"] = "Boat books",
                                ["bookIds"] = new JsonArray(
                                    QualityFixtureIds.BookSaltMeridian.ToString(),
                                    QualityFixtureIds.BookWoodenBoats.ToString()),
                            }.ToJsonString()),
                        QualityScript.Reply(
                            "I put The Salt Meridian and On Repairing Wooden Boats into Boat books.")),
                    new QualityTurnExpect(
                        RequiredTools: ["library_create_collection", "library_add_books_to_collection"],
                        ForbiddenTools: ["library_delete_collection"],
                        GoldNoteIds: [],
                        MaxToolCalls: 2,
                        ExpectedCollectionDelta: 1),
                    Verify: DoorOrganiseApplied),
            ]);
    }

    private static async Task<string?> CapturedDoorThoughtIsTheUsersWords(
        QualityTurnVerifyContext context)
    {
        if (!Guid.TryParse(context.Turn.Response?.CapturedNoteId, out var noteId))
            return "no captured note id on the turn";
        await using var db = await QualityVerify.Db(context.Services);
        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(item => item.Id == noteId);
        if (note is null)
            return $"captured note {noteId} not found";
        if (!string.Equals(note.Content, C37Thought, StringComparison.Ordinal))
            return $"captured '{note.Content}', expected the user's words without the door's prefix";
        return note.BookId == QualityFixtureIds.BookSaltMeridian
            ? null
            : $"captured under {note.BookId}, expected the open book";
    }

    private static async Task<string?> DoorOrganiseApplied(QualityTurnVerifyContext context)
    {
        var changeset = context.Turn.Response?.Changeset;
        if (changeset is null)
            return "the organise door produced no changeset";
        if (!string.Equals(changeset.Status, "applied", StringComparison.Ordinal))
            return $"the everyday change was '{changeset.Status}', expected it to apply with undo";
        if (!changeset.CanUndo)
            return "the applied change offers no undo";

        await using var db = await QualityVerify.Db(context.Services);
        var collection = await db.Collections.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Name == "Boat books");
        if (collection is null)
            return "collection 'Boat books' was not created";
        var members = await db.BookCollections.AsNoTracking()
            .Where(link => link.CollectionId == collection.Id)
            .Select(link => link.BookId)
            .ToListAsync();
        var expected = new[]
        {
            QualityFixtureIds.BookSaltMeridian,
            QualityFixtureIds.BookWoodenBoats,
        };
        return members.ToHashSet().SetEquals(expected)
            ? null
            : $"Boat books holds [{string.Join(",", members)}], expected the two boat books";
    }

    // ------------------------------------------------------------------
    // Shared reader for turn events (the same NDJSON the panel consumes)
    // ------------------------------------------------------------------

    private static async Task<IReadOnlyList<AssistantTurnEventDto>> ReadTurnEvents(
        HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<AssistantTurnEventDto>(line, options)!)
            .ToList();
    }
}
