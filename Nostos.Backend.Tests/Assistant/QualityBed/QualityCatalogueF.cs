// Scenario C22 (issue #1291): a paper-book quote named by its first and last words.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Services.Ai;

internal static partial class QualityCatalogue
{
    private const string FindQuoteTool = "book_text_find_quote";

    // ------------------------------------------------------------------
    // C22 — paper-book quote by its first and last words
    // ------------------------------------------------------------------

    private static QualityScenario C22()
    {
        // Reading the paper edition, the user names a long passage by how it
        // starts and ends instead of reading it all out. Nostos finds it in the
        // EPUB edition of the same work and saves the EPUB's exact words on the
        // paper book, at the page the user named. Deterministic proves the
        // product path (exact, dictation errors, two matches, no digital
        // edition); live scores whether the model uses book_text_find_quote and
        // the quoteRef instead of saving the dictated fragments.
        var paper = () => QualityContexts.PhysicalBook(
            QualityFixtureIds.BookValleyRoadPaper, QualityFixtureIds.TitleValleyRoad);

        return new QualityScenario(
            "C22",
            "Paper-book quote by first and last words",
            "C22 — paper-book quote: find a passage named by its first and last words in the digital edition, save it exactly on the paper book.",
            "The EPUB's exact passage is saved on the paper book at the named page; two matches ask; no digital edition saves nothing.",
            "Does the model look the passage up instead of saving the dictated fragments? " +
            "When two passages match, does it ask which one rather than guess? " +
            "Without a digital edition, does it say so in one line and point to photographing the page?",
            QualityModes.Deterministic | QualityModes.Live,
            "Deterministic proves exact, fuzzy, ambiguous and no-edition outcomes; live scores tool choice and the replies.",
            [
                // Exact words.
                new QualityTurnSpec(
                    "Page 143. It starts with \"the time of the thaw\" and ends with \"they never came back\". Save it.",
                    paper,
                    FindThenCapture("the time of the thaw", "they never came back", page: 143),
                    new QualityTurnExpect(
                        RequiredTools: [FindQuoteTool, "notes_capture"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ExpectCapturedNote: true,
                        ExpectedNoteDelta: 1),
                    Verify: context => CapturedQuote(context, QualityLibrarySeeder.ValleyRoadQuote, "143")),

                // What dictation gets wrong: a misheard word at each end.
                new QualityTurnSpec(
                    "page 144, starts with the tyme of the thaw, ends with they never come back, save that one",
                    paper,
                    FindThenCapture("the tyme of the thaw", "they never come back", page: 144),
                    new QualityTurnExpect(
                        RequiredTools: [FindQuoteTool, "notes_capture"],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ExpectCapturedNote: true,
                        ExpectedNoteDelta: 1),
                    Verify: context => CapturedQuote(context, QualityLibrarySeeder.ValleyRoadQuote, "144")),

                // Two passages match: ask, save nothing.
                new QualityTurnSpec(
                    "Save the bit that starts with \"the road turns\" and ends with \"toward home\".",
                    paper,
                    QualityScript.Play(
                        QualityScript.ToolCall(FindQuoteTool, FindArgs("the road turns", "toward home")),
                        QualityScript.Reply(
                            "Two passages match: 1. \"The road turns east at the old mill … toward home.\" (Chapter One) " +
                            "2. \"The road turns again below the chapel … toward home.\" (Chapter Nine). Which one?")),
                    new QualityTurnExpect(
                        RequiredTools: [FindQuoteTool],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ExpectedNoteDelta: 0,
                        ReplyMustContain: ["1", "2"]),
                    Verify: context => Task.FromResult(FindStatus(context, "choose"))),

                // No digital edition: one line, nothing saved.
                new QualityTurnSpec(
                    "Page 12, it starts with the kettle sings and ends with before the light. Save it.",
                    () => QualityContexts.PhysicalBook(
                        QualityFixtureIds.BookQuietMornings, QualityFixtureIds.TitleQuietMornings),
                    QualityScript.Play(
                        QualityScript.ToolCall(FindQuoteTool, FindArgs("the kettle sings", "before the light")),
                        QualityScript.Reply(
                            "There's no digital edition of this book to look the quote up in. You can photograph the page instead.")),
                    new QualityTurnExpect(
                        RequiredTools: [FindQuoteTool],
                        ForbiddenTools: [],
                        GoldNoteIds: [],
                        ExpectedNoteDelta: 0,
                        ReplyMustContain: ["photograph"]),
                    Verify: context => Task.FromResult(FindStatus(context, "no_digital_edition"))),
            ]);
    }

    private static string FindArgs(string startsWith, string endsWith, int? page = null)
    {
        var args = new JsonObject { ["startsWith"] = startsWith, ["endsWith"] = endsWith };
        if (page is { } value)
            args["page"] = value;
        return args.ToJsonString();
    }

    /// <summary>Looks the passage up, then saves the quoteRef the lookup returned.</summary>
    private static Func<QualityScriptContext, Task<LlmCompletion>> FindThenCapture(
        string startsWith, string endsWith, int page) =>
        QualityScript.Dynamic(context => Task.FromResult(context.CallIndex switch
        {
            0 => QualityScript.ToolCall(FindQuoteTool, FindArgs(startsWith, endsWith, page)),
            1 when LastToolResult(context.Request) is { } result
                && result["candidates"] is JsonArray { Count: 1 } candidates
                && candidates[0]?["quoteRef"]?.GetValue<string>() is { } quoteRef =>
                QualityScript.ToolCall("notes_capture", new JsonObject { ["quoteRef"] = quoteRef }.ToJsonString()),
            1 => QualityScript.Reply("I couldn't find that passage."),
            _ => QualityScript.Reply("Saved the quote."),
        }));

    private static JsonNode? LastToolResult(LlmCompletionRequest request)
    {
        var tool = request.Messages.LastOrDefault(message => message.Role == "tool");
        if (tool?.Content is not { } content)
            return null;
        var root = JsonNode.Parse(content);
        return root?["data"] ?? root;
    }

    private static async Task<string?> CapturedQuote(QualityTurnVerifyContext context, string quote, string page)
    {
        if (!Guid.TryParse(context.Turn.Response?.CapturedNoteId, out var noteId))
            return "no captured note id on the turn";
        await using var db = await QualityVerify.Db(context.Services);
        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId);
        if (note is null)
            return $"captured note {noteId} not found";
        if (note.BookId != QualityFixtureIds.BookValleyRoadPaper)
            return $"quote filed under {note.BookId}, expected the paper edition";
        if (!string.Equals(note.SelectedText, quote, StringComparison.Ordinal))
            return $"quotation '{note.SelectedText}', expected the EPUB's exact '{quote}'";
        if (note.Content.Contains("Quoted by hand", StringComparison.Ordinal))
            return "a quote from the digital text carries the hand-copied note";
        return await QualityVerify.NoteAnchor(context.Services, noteId, "physical_page", page, verified: false);
    }

    private static string? FindStatus(QualityTurnVerifyContext context, string expected)
    {
        // Live runs keep no provider requests; their replies are scored instead.
        if (context.Turn.ProviderRequests.Count == 0)
            return null;

        foreach (var request in Enumerable.Reverse(context.Turn.ProviderRequests))
        {
            foreach (var message in Enumerable.Reverse(request.Messages.ToList()))
            {
                if (message.Role != "tool" || message.Content is not { } content)
                    continue;
                var status = (JsonNode.Parse(content)?["data"] ?? JsonNode.Parse(content))?["status"]?.GetValue<string>();
                if (status is not null && status != "capture_rejected")
                    return status == expected ? null : $"{FindQuoteTool} answered '{status}', expected '{expected}'";
            }
        }

        return $"no {FindQuoteTool} result in the turn";
    }
}
