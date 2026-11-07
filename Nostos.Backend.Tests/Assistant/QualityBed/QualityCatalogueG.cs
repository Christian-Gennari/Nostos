// Scenarios C23–C24 (issue #1468): the To read list and Studio drafts.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Ai;

internal static partial class QualityCatalogue
{
    // Fixture ids for the drafts and the To read list, kept here so the shared
    // fixture file stays untouched.
    private static readonly Guid FolderEssays = Guid.Parse("e1111111-1111-4111-8111-111111111111");
    private static readonly Guid DraftLighthouse = Guid.Parse("e2222222-2222-4222-8222-222222222222");
    private static readonly Guid DraftGroceries = Guid.Parse("e3333333-3333-4333-8333-333333333333");
    private static readonly Guid ToReadNextFirst = Guid.Parse("e4444444-4444-4444-8444-444444444444");
    private static readonly Guid ToReadNextSecond = Guid.Parse("e5555555-5555-4555-8555-555555555555");
    private static readonly Guid ToReadSomeday = Guid.Parse("e6666666-6666-4666-8666-666666666666");

    private const string DraftLighthouseName = "The lighthouse keeper, first pass";
    private const string DraftLighthouseMarker = "Every night the lamp is the same lamp";
    private const string ToReadAddedTitle = "The Rings of Saturn";

    /// <summary>
    /// Two Studio documents in a folder and three To read entries. Neither store
    /// is searched by knowledge_search, so no other scenario's vocabulary moves.
    /// </summary>
    internal static void SeedDraftsAndReadingList(NostosDbContext db)
    {
        var updated = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        db.Writings.AddRange(
            new WritingModel { Id = FolderEssays, Name = "Essays", Type = WritingType.Folder, UpdatedAt = updated },
            new WritingModel
            {
                Id = DraftLighthouse,
                Name = DraftLighthouseName,
                Type = WritingType.Document,
                ParentId = FolderEssays,
                Content = $"# {DraftLighthouseName}\n\n{DraftLighthouseMarker}, and still the keeper climbs. " +
                    $"[The keeper's night](nostos:cite/{QualityFixtureIds.BookSaltMeridian}?as=note&src={QualityFixtureIds.NoteKeeper}).",
                UpdatedAt = updated.AddDays(2),
            },
            new WritingModel
            {
                Id = DraftGroceries,
                Name = "Groceries",
                Type = WritingType.Document,
                Content = "Oats, pears, coffee.",
                UpdatedAt = updated.AddDays(5),
            });

        db.ReadingListEntries.AddRange(
            new ReadingListEntryModel
            {
                Id = ToReadNextFirst,
                Title = "The Peregrine",
                Author = "J. A. Baker",
                Why = "For the essay on attention",
                NextPosition = 0,
                CreatedAt = updated,
            },
            new ReadingListEntryModel
            {
                Id = ToReadNextSecond,
                Title = "Pilgrim at Tinker Creek",
                Author = "Annie Dillard",
                NextPosition = 1,
                CreatedAt = updated.AddDays(1),
            },
            new ReadingListEntryModel
            {
                Id = ToReadSomeday,
                Title = "The Living Mountain",
                Author = "Nan Shepherd",
                RecommendedBy = "Mia",
                CreatedAt = updated.AddDays(3),
            });
    }

    // ------------------------------------------------------------------
    // C23 — what's on my To read list
    // ------------------------------------------------------------------

    private static QualityScenario C23()
    {
        // The list is read, never guessed, Next up first; then a book named in
        // conversation is added once, and asking again adds nothing.
        return new QualityScenario(
            "C23",
            "To read list",
            "C23 — To read list: answer \"what's on my To read list\" from reading_list_list, then add a book once.",
            "The reply names the list's books in order from the tool result; the added book is one entry, even when asked twice.",
            "Does the answer list Next up before Someday and keep the user's reasons? " +
            "Is the added book confirmed without inventing details, and is a repeat request answered as already there?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic proves the read order and idempotent add; live scores the list's presentation.",
            [
                new QualityTurnSpec(
                    "What's on my To read list?",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall("reading_list_list", "{}"),
                        QualityScript.Reply(
                            "Next up: The Peregrine (for the essay on attention), then Pilgrim at Tinker Creek. " +
                            "Someday: The Living Mountain, recommended by Mia.")),
                    new QualityTurnExpect(
                        RequiredTools: ["reading_list_list"],
                        ForbiddenTools: ["reading_list_add", "writings_create", "notes_capture"],
                        GoldNoteIds: [],
                        ReplyMustContain: ["The Peregrine", "Pilgrim at Tinker Creek", "The Living Mountain"]),
                    Verify: context => Task.FromResult(ToolResultTitles(
                        context, "nextUpCount", "entries",
                        ["The Peregrine", "Pilgrim at Tinker Creek", "The Living Mountain"]))),
                new QualityTurnSpec(
                    $"Add {ToReadAddedTitle} by W. G. Sebald to it.",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "reading_list_add",
                            new JsonObject { ["title"] = ToReadAddedTitle, ["author"] = "W. G. Sebald" }.ToJsonString()),
                        QualityScript.Reply($"Added {ToReadAddedTitle} to To read.")),
                    new QualityTurnExpect(
                        RequiredTools: ["reading_list_add"],
                        ForbiddenTools: ["writings_create", "notes_capture"],
                        GoldNoteIds: [],
                        ExpectedExecuted: ["reading_list_add"]),
                    Verify: context => ToReadCount(context, ToReadAddedTitle, 1)),
                new QualityTurnSpec(
                    $"Put {ToReadAddedTitle} on my To read list.",
                    QualityContexts.SecondBrain,
                    QualityScript.Play(
                        QualityScript.ToolCall(
                            "reading_list_add",
                            new JsonObject { ["title"] = ToReadAddedTitle }.ToJsonString()),
                        QualityScript.Reply($"{ToReadAddedTitle} is already on To read.")),
                    new QualityTurnExpect(
                        RequiredTools: ["reading_list_add"],
                        ForbiddenTools: ["writings_create", "notes_capture"],
                        GoldNoteIds: []),
                    Verify: context => ToReadCount(context, ToReadAddedTitle, 1)),
            ]);
    }

    // ------------------------------------------------------------------
    // C24 — open my draft about X
    // ------------------------------------------------------------------

    private static QualityScenario C24()
    {
        // The draft is found by its name in the Studio list, then read whole:
        // the reply carries what the draft says and the link that opens it.
        // Reading never writes, and nothing is created.
        return new QualityScenario(
            "C24",
            "Open my draft about X",
            "C24 — open my draft: find a Studio document by what the user calls it, read it as Markdown, give its link.",
            "The lighthouse draft is listed, read whole, and its Studio link is in the reply; nothing is written.",
            "Does the model find the right draft rather than the most recent one, and answer from its text? " +
            "Does it give the link instead of pasting the whole draft unasked?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic proves list -> read with the real ids and no write; live scores the reply.",
            [
                new QualityTurnSpec(
                    "Open my draft about the lighthouse keeper.",
                    QualityContexts.SecondBrain,
                    ListThenReadDraft("lighthouse"),
                    new QualityTurnExpect(
                        RequiredTools: ["writings_list", "writings_read"],
                        ForbiddenTools: ["writings_create", "reading_list_add", "notes_capture"],
                        GoldNoteIds: [],
                        ReplyMustContain: [$"/studio?writingId={DraftLighthouse}"]),
                    Verify: context => DraftRead(context)),
            ]);
    }

    /// <summary>Lists drafts matching the words, then reads the first document the list returned.</summary>
    private static Func<QualityScriptContext, Task<LlmCompletion>> ListThenReadDraft(string query) =>
        QualityScript.Dynamic(context => Task.FromResult(context.CallIndex switch
        {
            0 => QualityScript.ToolCall("writings_list", new JsonObject { ["query"] = query }.ToJsonString()),
            1 when LastToolResult(context.Request)?["items"] is JsonArray items
                && items.FirstOrDefault(item => item?["type"]?.GetValue<string>() == "Document")?["id"]?.GetValue<string>() is { } id =>
                QualityScript.ToolCall("writings_read", new JsonObject { ["writingId"] = id }.ToJsonString()),
            1 => QualityScript.Reply("I couldn't find that draft."),
            _ when LastToolResult(context.Request)?["link"]?.GetValue<string>() is { } link =>
                QualityScript.Reply($"Here it is: {link}. It opens on \"{DraftLighthouseMarker}\"."),
            _ => QualityScript.Reply("I couldn't open that draft."),
        }));

    private static async Task<string?> DraftRead(QualityTurnVerifyContext context)
    {
        if (context.Turn.ProviderRequests.Count > 0)
        {
            var read = LastToolResultIn(context, "characters");
            if (read is null)
                return "no writings_read result in the turn";
            if (read["id"]?.GetValue<string>() != DraftLighthouse.ToString())
                return $"read {read["id"]}, expected the lighthouse draft";
            if (read["content"]?.GetValue<string>() is not { } content || !content.Contains(DraftLighthouseMarker, StringComparison.Ordinal))
                return "the read draft does not carry its own text";
            if (read["path"]?.GetValue<string>() != $"Essays / {DraftLighthouseName}")
                return $"draft path '{read["path"]}', expected 'Essays / {DraftLighthouseName}'";
        }

        await using var db = await QualityVerify.Db(context.Services);
        return await db.Writings.CountAsync() == 3 ? null : "reading a draft changed the Studio";
    }

    private static async Task<string?> ToReadCount(QualityTurnVerifyContext context, string title, int expected)
    {
        await using var db = await QualityVerify.Db(context.Services);
        var count = await db.ReadingListEntries.CountAsync(e => e.Title == title);
        return count == expected ? null : $"{count} To read entries titled '{title}', expected {expected}";
    }

    private static string? ToolResultTitles(
        QualityTurnVerifyContext context, string marker, string arrayName, string[] expected)
    {
        // Live runs keep no provider requests; their replies are scored instead.
        if (context.Turn.ProviderRequests.Count == 0)
            return null;

        var titles = (LastToolResultIn(context, marker)?[arrayName] as JsonArray)?
            .Select(entry => entry?["title"]?.GetValue<string>())
            .ToList();
        return titles is not null && titles.SequenceEqual(expected)
            ? null
            : $"the To read result held [{string.Join(", ", titles ?? [])}], expected [{string.Join(", ", expected)}]";
    }

    /// <summary>
    /// The data of the last tool result in the turn that has <paramref name="marker"/>.
    /// The request log keeps no tool names, so a result is told by its shape.
    /// </summary>
    private static JsonNode? LastToolResultIn(QualityTurnVerifyContext context, string marker)
    {
        foreach (var request in Enumerable.Reverse(context.Turn.ProviderRequests))
        {
            foreach (var message in Enumerable.Reverse(request.Messages.ToList()))
            {
                if (message.Role != "tool" || message.Content is not { } content)
                    continue;
                var root = JsonNode.Parse(content);
                var data = root?["data"] ?? root;
                if (data is JsonObject obj && obj.ContainsKey(marker))
                    return data;
            }
        }

        return null;
    }
}
