// The same-request consistency run for Ask Nostos (#1584).
//
// docs/archive/ask-nostos-assessment.md §6.2 asks how much a user-visible answer
// varies when the same request is sent again. Six requests, 5 reps each and 10
// for the changeset and disclosure requests, every rep in a fresh conversation
// on a freshly seeded library, plus a short same-conversation pass to catch
// context effects. Per rep it records the ordered tool calls, the changeset
// (status, diffHash, steps, review reasons), the citations, the reply shape,
// provider-reported tokens and wall-clock latency, and writes the raw records.
//
// MANUAL ONLY. It calls a paid model, so it does nothing unless
// NOSTOS_QG_CONSISTENCY=1 and it is never started by a push or a pull request.
// It reuses the quality bed's gateway settings and spend cap:
//
//   NOSTOS_QG_CONSISTENCY=1           opt in
//   NOSTOS_QG_BASE_URL                OpenAI-compatible base URL, including /v1
//   NOSTOS_QG_KEY_ENV                 name of the variable that holds the key
//                                     (default NOSTOS_QG_KEY)
//   NOSTOS_QG_MODELS                  one model id (default openai/gpt-6-luna)
//   NOSTOS_QG_MAX_USD + the two price variables (QualityBudget): required
//   NOSTOS_QG_CONSISTENCY_REPS        scale factor for the reps (default 1)
//   NOSTOS_QG_CONSISTENCY_ONLY        comma list of request ids to run (default all)
//   NOSTOS_QG_MAX_TOOL_ITERATIONS     tool rounds per turn (default 12, the product's)
//   NOSTOS_QG_OUT                     where raw.jsonl / summary.json land
//
// Run it through the manual workflow `ask-consistency-live.yml`, or locally:
//   NOSTOS_QG_CONSISTENCY=1 ... dotnet test --filter QualityConsistencyRunTests
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Endpoints;
using Nostos.Shared.Dtos;
using Xunit;

internal sealed record ConsistencyRequest(
    string Id,
    string Message,
    string Why,
    Func<AssistantContextDto> Context,
    int Reps,
    // Messages sent after Message in the same conversation (fresh mode only).
    string[]? Followups = null,
    // Apply-then-undo and undo checks after the turn (the library is changed).
    bool Checks = true);

internal static class ConsistencyRequests
{
    // The assessment names Mann and Le Guin; the bed's library is synthetic, so
    // the same six shapes are worded against it (author names and titles
    // replaced, nothing else).
    public static IReadOnlyList<ConsistencyRequest> All { get; } =
    [
        new("R1-notes", "What did I note about attention?",
            "notes retrieval", Brain, 5),
        new("R2-book-text", "Where does The Salt Meridian describe coiling a rope?",
            "book-text path", Library, 5),
        new("R3-capture", "Save this thought: a library is a record of attention.",
            "capture with no book open (anchor prompt)", Library, 5),
        new("R4-changeset", "Move the books by A. Ek and M. Lindqvist into a new collection called Sea Stories.",
            "auto-apply with undo", Library, 10),
        new("R5-review", "Regroup everything into Fiction and Essays.",
            "review changeset", Library, 10),
        new("R6-disclosure", "What can you do?",
            "capability disclosure", Library, 10),
    ];

    // Repros of what the live UI walk found (docs/ask-nostos-live-run-2026-11.md).
    public static IReadOnlyList<ConsistencyRequest> Repros { get; } =
    [
        new("W1-selection-repeat", "Repeat back the exact words I have selected right now.",
            "reader with text selected: a read-only request", SelectedReader, 5, Checks: false),
        new("W1b-selection-after-question", "Where does The Cartographer's Daughter mention a ferry crossing?",
            "reader with text selected, then the read-only request again in the same conversation", SelectedReader, 5,
            Followups: ["Repeat back the exact words I have selected right now."], Checks: false),
        new("W3-topics-synthesis", "What connects my topics?",
            "Brain: a synthesis over topics and notes", Brain, 5, Checks: false),
        new("W2-sequence", "Move the books by A. Ek and M. Lindqvist into a new collection called Sea Stories.",
            "a change, then a regroup, in one conversation", Library, 5,
            Followups: ["Regroup everything into Fiction and Essays."], Checks: false),
    ];

    private static AssistantContextDto SelectedReader() =>
        QualityContexts.ReaderEbook(
            QualityFixtureIds.BookCartographer, QualityFixtureIds.TitleCartographer,
            "epubcfi(/6/4[chap02]!/4/2/2)") with
        {
            SelectedText = "a pencilled hand notes that the ferry crossing moved upstream after the spring flood",
        };

    private static AssistantContextDto Library() => new("library", "/library");

    private static AssistantContextDto Brain() => new("brain", "/brain");
}

public sealed class QualityConsistencyRunTests
{
    private const string OptIn = "NOSTOS_QG_CONSISTENCY";

    // The product default (appsettings.json "Assistant:MaxToolIterations"), not
    // the 6 the deterministic bed pins: a user gets 12 rounds per turn.
    private static readonly int MaxToolIterations =
        int.TryParse(Environment.GetEnvironmentVariable("NOSTOS_QG_MAX_TOOL_ITERATIONS"), out var rounds)
            && rounds > 0 ? rounds : 12;
    private static readonly Regex Marker = new(@"\[\^\d+\]", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*([-*•]|\d+[.)])\s+", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly JsonSerializerOptions Out = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    [Fact]
    public async Task Consistency_run_is_a_noop_without_opt_in()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(OptIn), "1", StringComparison.Ordinal))
            return;
        // Nothing to assert: the live test below returns before any call.
        Assert.NotEmpty(ConsistencyRequests.All);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Consistency_run_records_every_rep()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptIn), "1", StringComparison.Ordinal))
            return; // Live calls cost money: manual only.

        var config = QualityBedConfig.FromEnvironment();
        var model = config.Models.FirstOrDefault() ?? "openai/gpt-6-luna";
        var budget = QualityBudget.Process;
        Assert.NotNull(budget.MaxUsd);
        var scale = int.TryParse(Environment.GetEnvironmentVariable("NOSTOS_QG_CONSISTENCY_REPS"), out var s)
            ? Math.Max(1, s) : 1;
        Directory.CreateDirectory(config.OutDir);
        var rawPath = Path.Combine(config.OutDir, "raw.jsonl");
        await using var raw = new StreamWriter(rawPath, append: false);
        var records = new List<Dictionary<string, object?>>();

        // NOSTOS_QG_CONSISTENCY_ONLY=W3-topics-synthesis,W1b-... runs just those
        // (a rerun of one repro costs cents); empty runs everything.
        var only = (Environment.GetEnvironmentVariable("NOSTOS_QG_CONSISTENCY_ONLY") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        bool Wanted(ConsistencyRequest r) => only.Count == 0 || only.Contains(r.Id);

        // Fresh conversation per rep: every rep sees an identical seeded library.
        foreach (var request in ConsistencyRequests.All.Where(Wanted))
        {
            for (var rep = 1; rep <= request.Reps * scale; rep++)
            {
                if (budget.Exhausted)
                    break;
                var record = await RunAsync(config, model, budget, request, rep, mode: "fresh", turns: 1);
                foreach (var turn in record)
                {
                    records.Add(turn);
                    await raw.WriteLineAsync(JsonSerializer.Serialize(turn, Out));
                }
            }
        }

        // Repros of what the live UI walk found, each rep in a fresh conversation.
        foreach (var request in ConsistencyRequests.Repros.Where(Wanted))
        {
            for (var rep = 1; rep <= request.Reps * scale; rep++)
            {
                if (budget.Exhausted)
                    break;
                var record = await RunAsync(config, model, budget, request, rep, mode: "repro", turns: 1);
                foreach (var turn in record)
                {
                    records.Add(turn);
                    await raw.WriteLineAsync(JsonSerializer.Serialize(turn, Out));
                }
            }
        }

        // Same conversation: the request repeated three times in one thread.
        foreach (var request in ConsistencyRequests.All.Where(Wanted))
        {
            if (budget.Exhausted)
                break;
            var record = await RunAsync(config, model, budget, request, rep: 1, mode: "same-conversation", turns: 3);
            foreach (var turn in record)
            {
                records.Add(turn);
                await raw.WriteLineAsync(JsonSerializer.Serialize(turn, Out));
            }
        }

        await raw.FlushAsync();
        var summary = new
        {
            model,
            records = records.Count,
            spentUsd = budget.SpentUsd,
            calls = budget.Calls,
            estimatedCalls = budget.EstimatedCalls,
            capUsd = budget.MaxUsd,
            budget.InputPerMTok,
            budget.OutputPerMTok,
            exhausted = budget.Exhausted,
        };
        await File.WriteAllTextAsync(
            Path.Combine(config.OutDir, "summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Assert.False(budget.Exhausted, "spend cap reached before every rep ran: " + budget.Describe());
    }

    private static async Task<List<Dictionary<string, object?>>> RunAsync(
        QualityBedConfig config,
        string model,
        QualityBudget budget,
        ConsistencyRequest request,
        int rep,
        string mode,
        int turns)
    {
        // An applied changeset is undone through the card's route right after
        // the turn, as the Undo button does, and what came back is recorded.
        var undoResults = new ConcurrentDictionary<int, object>();
        var applyUndoResults = new ConcurrentDictionary<int, object>();
        var specTurn = 0;
        var scenario = new QualityScenario(
            request.Id, request.Why, request.Message, request.Why, string.Empty,
            QualityModes.Live, "consistency run",
            Enumerable.Range(0, turns + (request.Followups?.Length ?? 0))
                .Select(_ =>
                {
                    var index = specTurn++;
                    return new QualityTurnSpec(
                        index == 0 || turns > 1 ? request.Message : request.Followups![index - 1],
                        request.Context, Expect: QualityTurnExpect.Empty,
                        Verify: async context =>
                        {
                            // Same-conversation turns keep the change so the next turn sees it.
                            if (turns == 1 && request.Checks)
                            {
                                await TryUndoAsync(context, index, undoResults);
                                await TryApplyThenUndoAsync(context, index, applyUndoResults);
                            }
                            return null;
                        });
                })
                .ToList());

        var sink = new QualityMetricsSink();
        QualityBedHost Create()
        {
            var host = QualityBedHost.CreateLive(
                sink, config.BaseUrl, config.KeyEnvName, model, config.ReasoningEffort, budget,
                MaxToolIterations);
            using var scope = host.Services.CreateScope();
            QualityLibrarySeeder.SeedAsync(scope.ServiceProvider).GetAwaiter().GetResult();
            return host;
        }

        var outcome = await QualityRunner.RunScenarioAsync(scenario, model, rep, QualityModes.Live, Create);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var turn in outcome.Turns)
            rows.Add(Describe(
                request, mode, rep, turn, undoResults.GetValueOrDefault(turn.TurnIndex),
                applyUndoResults.GetValueOrDefault(turn.TurnIndex)));
        return rows;
    }

    /// <summary>
    /// A changeset left waiting for review is applied as the card's button does,
    /// then undone as its Undo does. Undo promises the library as it was, so the
    /// collections and their members are compared before and after.
    /// </summary>
    private static async Task TryApplyThenUndoAsync(
        QualityTurnVerifyContext context, int index, ConcurrentDictionary<int, object> results)
    {
        var changeset = context.Turn.Response?.Changeset;
        if (changeset is not { Status: "review" } || context.Client is null)
            return;
        try
        {
            var before = await SnapshotAsync(context.Services);
            var route = AssistantEndpoints.ChangesetRoute.Replace("{changesetId}", changeset.ChangesetId);
            var applyResponse = await context.Client.PostAsJsonAsync(
                route + "/apply",
                new AssistantChangesetApplyRequest(changeset.ActionToken, null, changeset.DiffHash));
            var applied = await applyResponse.Content.ReadFromJsonAsync<AssistantChangesetActionResponse>();
            var afterApply = await SnapshotAsync(context.Services);
            if (applied is not { Success: true } || applied.Changeset is not { } appliedSet)
            {
                results[index] = new { applied = false, applyError = applied?.ErrorCode ?? $"HTTP {(int)applyResponse.StatusCode}" };
                return;
            }

            var undoResponse = await context.Client.PostAsJsonAsync(
                route + "/undo", new AssistantChangesetTokenRequest(appliedSet.ActionToken));
            var undone = await undoResponse.Content.ReadFromJsonAsync<AssistantChangesetActionResponse>();
            var afterUndo = await SnapshotAsync(context.Services);
            results[index] = new
            {
                applied = true,
                appliedStatus = applied.Changeset?.Status,
                undone = undone?.Success,
                undoStatus = undone?.Changeset?.Status,
                undoConflicts = undone?.Conflicts,
                before,
                afterApply,
                afterUndo,
                restoredExactly = before == afterUndo,
            };
        }
        catch (Exception exception)
        {
            results[index] = new { error = $"{exception.GetType().Name}: {exception.Message}" };
        }
    }

    // Collection name -> its books, sorted: what a user sees in the sidebar.
    private static async Task<string> SnapshotAsync(IServiceProvider services)
    {
        await using var db = await QualityVerify.Db(services);
        var rows = await db.BookCollections.AsNoTracking()
            .Select(m => new { Collection = m.Collection!.Name, Book = m.Book!.Title })
            .ToListAsync();
        var names = await db.Collections.AsNoTracking().Select(c => c.Name).ToListAsync();
        var placed = rows.GroupBy(r => r.Collection)
            .ToDictionary(g => g.Key, g => string.Join(",", g.Select(r => r.Book).Order()));
        return string.Join(
            " | ",
            names.Order().Select(n => $"{n}=[{(placed.TryGetValue(n, out var books) ? books : "")}]"));
    }

    private static async Task TryUndoAsync(
        QualityTurnVerifyContext context, int index, ConcurrentDictionary<int, object> results)
    {
        var changeset = context.Turn.Response?.Changeset;
        if (changeset is not { Status: "applied", CanUndo: true } || context.Client is null)
            return;
        try
        {
            await using var db = await QualityVerify.Db(context.Services);
            var before = await db.Collections.CountAsync();
            var response = await context.Client.PostAsJsonAsync(
                AssistantEndpoints.ChangesetRoute.Replace("{changesetId}", changeset.ChangesetId) + "/undo",
                new AssistantChangesetTokenRequest(changeset.ActionToken));
            var undone = await response.Content.ReadFromJsonAsync<AssistantChangesetActionResponse>();
            await using var after = await QualityVerify.Db(context.Services);
            results[index] = new
            {
                http = (int)response.StatusCode,
                success = undone?.Success,
                status = undone?.Changeset?.Status,
                conflicts = undone?.Conflicts?.Count,
                collectionsBefore = before,
                collectionsAfter = await after.Collections.CountAsync(),
            };
        }
        catch (Exception exception)
        {
            results[index] = new { error = $"{exception.GetType().Name}: {exception.Message}" };
        }
    }

    private static Dictionary<string, object?> Describe(
        ConsistencyRequest request, string mode, int rep, QualityTurnRecord turn, object? undo, object? applyUndo)
    {
        var response = turn.Response;
        var reply = response?.Reply ?? string.Empty;
        var toolOrder = turn.UpstreamCalls.SelectMany(c => c.ToolNames).ToList();
        var changeset = response?.Changeset;
        return new Dictionary<string, object?>
        {
            ["request"] = request.Id,
            ["message"] = turn.UserMessage,
            ["mode"] = mode,
            ["rep"] = rep,
            ["turn"] = turn.TurnIndex,
            ["terminal"] = turn.TerminalKind,
            ["errorCode"] = turn.FailureCode ?? response?.Error?.Code,
            ["errorMessage"] = turn.FailureMessage,
            ["harnessError"] = turn.HarnessError,
            ["toolOrder"] = toolOrder,
            ["toolPath"] = string.Join(">", toolOrder),
            ["upstreamCalls"] = turn.UpstreamCalls.Count,
            ["executedCapabilities"] = response?.ExecutedCapabilities ?? [],
            ["changeset"] = changeset is null ? null : new
            {
                changeset.ChangesetId,
                changeset.Status,
                changeset.DiffHash,
                steps = changeset.Steps.Count,
                stepSummaries = changeset.Steps.Select(x => x.Summary).ToList(),
                changeset.ReviewReasons,
                changeset.CanUndo,
            },
            ["undo"] = undo,
            ["applyUndo"] = applyUndo,
            ["toolResults"] = (turn.ToolResults ?? []).Select(t => new
            {
                t.Name,
                content = t.Content.Length > 3000 ? t.Content[..3000] + "…(truncated)" : t.Content,
            }).ToList(),
            ["anchorPrompt"] = response?.AnchorPrompt?.Kind,
            ["capturedNote"] = response?.CapturedNoteId is not null,
            ["sources"] = (response?.Sources ?? []).Select(x => $"{x.BookTitle}|{string.Join(",", x.Locators.Select(l => l.ToString()))}").ToList(),
            ["evidence"] = (response?.Evidence ?? []).Select(x => x.Handle.ToString()).ToList(),
            ["citationMarkers"] = Marker.Matches(reply).Count,
            ["replyChars"] = reply.Length,
            ["replyWords"] = reply.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            ["replyParagraphs"] = reply.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length,
            ["replyBullets"] = Bullet.Matches(reply).Count,
            ["replyShape"] = Shape(reply),
            ["reply"] = reply,
            ["promptTokens"] = turn.UpstreamCalls.Sum(c => c.PromptTokens ?? 0),
            ["completionTokens"] = turn.UpstreamCalls.Sum(c => c.CompletionTokens ?? 0),
            ["thinkingTokens"] = turn.UpstreamCalls.Sum(c => c.ThinkingTokens ?? 0),
            ["latencyMs"] = Math.Round(turn.TotalMs),
            ["ttfActivityMs"] = Math.Round(turn.TtfActivityMs),
            ["notesDelta"] = turn.NoteCountAfter - turn.NoteCountBefore,
            ["collectionsDelta"] = turn.CollectionCountAfter - turn.CollectionCountBefore,
            ["revisionMoved"] = turn.RevisionBefore != turn.RevisionAfter,
        };
    }

    private static string Shape(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return "empty";
        var bullets = Bullet.Matches(reply).Count;
        if (bullets >= 2)
            return "list";
        return reply.Contains("\n\n", StringComparison.Ordinal) ? "multi-paragraph" : "prose";
    }
}
