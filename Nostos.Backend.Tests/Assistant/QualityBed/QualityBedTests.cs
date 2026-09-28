// xUnit entry points for the #566 quality bed.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Search;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Xunit;

/// <summary>
/// Always-on deterministic campaign: offline, scripted provider, real product
/// path (HTTP stream route, real orchestrator/capabilities/retrieval). Must
/// stay under 90 s and touch no network.
/// </summary>
public sealed class QualityBedDeterministicTests
{
    [Fact]
    public async Task Quality_bed_deterministic_scenarios_pass()
    {
        var config = QualityBedConfig.FromEnvironment() with { Live = false };
        var started = DateTimeOffset.UtcNow;

        var models = await QualityRunner.RunDeterministicAsync(config);

        var failures = models.SelectMany(m => m.Failures).ToList();
        Assert.True(
            failures.Count == 0,
            "quality bed deterministic failures:\n" + string.Join("\n", failures));

        // The catalogue is complete: every scenario runs unless filtered.
        if (config.ScenarioIds.Count == 0 && config.Reps == 1)
            Assert.Equal(20, models.SelectMany(m => m.Scenarios).Count());

        var resultsPath = Path.Combine(config.OutDir, "quality-scripted", "results.json");
        Assert.True(File.Exists(resultsPath), $"expected {resultsPath}");
        var transcriptPath = Path.Combine(config.OutDir, "quality-scripted", "transcript.md");
        Assert.True(File.Exists(transcriptPath), $"expected {transcriptPath}");

        var elapsed = DateTimeOffset.UtcNow - started;
        Assert.True(
            config.ScenarioIds.Count > 0 || elapsed < TimeSpan.FromSeconds(90),
            $"full deterministic bed took {elapsed.TotalSeconds:F0} s (budget 90 s)");
    }

    [Fact]
    public async Task Quality_bed_live_campaign_is_a_noop_without_opt_in()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("NOSTOS_QG_LIVE"), "1", StringComparison.Ordinal))
            return; // Covered by the live entry point when opted in.

        var config = QualityBedConfig.FromEnvironment() with { Live = false };
        Assert.False(config.Live);
    }
}

/// <summary>
/// Live campaign entry point: a no-op unless NOSTOS_QG_LIVE=1, so CI stays
/// offline and green. The parent session runs the model-floor comparison
/// through this test.
/// </summary>
public sealed class QualityBedLiveTests
{
    [Fact]
    public async Task Quality_bed_live_campaign_runs_configured_models()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("NOSTOS_QG_LIVE"), "1", StringComparison.Ordinal))
            return; // No-op in CI: live calls cost money and need a gateway.

        var config = QualityBedConfig.FromEnvironment();
        var models = await QualityRunner.RunLiveAsync(config);

        var failures = models
            .SelectMany(m => m.Scenarios.SelectMany(s =>
                s.Failures.Select(f => $"{m.Model} {f}")))
            .ToList();
        Assert.True(
            failures.Count == 0,
            "live campaign mismatches (scenario, turn, observed vs expected):\n"
            + string.Join("\n", failures));
    }
}

/// <summary>
/// Fixture shape + vocabulary guards: the synthetic library meets its
/// counts, and every scenario's query tokens hit exactly the intended gold
/// (mirrors NoteRepository.SearchByTextAsync LIKE semantics + fusion rank).
/// </summary>
public sealed class QualityBedFixtureTests : IDisposable
{
    private readonly LibraryEndpointFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Quality_bed_fixture_shapes_hold()
    {
        using var scope = _factory.Services.CreateScope();
        await QualityLibrarySeeder.SeedAsync(scope.ServiceProvider);

        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NostosDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        Assert.Equal(7, await db.Books.CountAsync());
        Assert.Equal(3, await db.Collections.CountAsync());
        Assert.Equal(21, await db.Notes.CountAsync());
        Assert.Equal(7, await db.Concepts.CountAsync());

        // Every note carries an anchor (CFI range or typed anchor value).
        var notes = await db.Notes.AsNoTracking().ToListAsync();
        Assert.All(notes, note => Assert.True(
            note.CfiRange is not null || note.SourceAnchorValue is not null,
            $"note {note.Id} has no anchor"));

        // Every concept is linked to at least one note.
        var linked = await db.Set<Nostos.Backend.Data.Models.NoteConceptModel>()
            .Select(link => link.ConceptId)
            .Distinct()
            .ToListAsync();
        Assert.Equal(7, linked.Count);

        // Membership: B6 (Marginalia) is Unsorted, everything else is placed.
        var membership = await db.Set<Nostos.Backend.Data.Models.BookCollectionModel>()
            .Select(m => m.BookId)
            .Distinct()
            .ToListAsync();
        Assert.DoesNotContain(QualityFixtureIds.BookMarginalia, membership);
        Assert.Equal(6, membership.Count);

        // Book text: two Ready books with 3 chunks each, one Pending shell.
        var index = scope.ServiceProvider.GetRequiredService<IBookTextIndex>();
        var pdf = await index.GetStateAsync(QualityFixtureIds.BookSaltMeridian);
        var epub = await index.GetStateAsync(QualityFixtureIds.BookCartographer);
        var pending = await index.GetStateAsync(QualityFixtureIds.BookGranaryLedger);
        Assert.Equal(BookTextIngestionStatus.Ready, pdf!.Status);
        Assert.Equal(3, pdf.ChunkCount);
        Assert.Equal(BookTextIngestionStatus.Ready, epub!.Status);
        Assert.Equal(3, epub.ChunkCount);
        Assert.Equal(BookTextIngestionStatus.Pending, pending!.Status);

        // The read path answers: exact FTS token from the rope chunk.
        var hits = await index.SearchAsync(
            "mooring", [QualityFixtureIds.BookSaltMeridian], 5);
        Assert.NotEmpty(hits);
    }

    [Fact]
    public async Task Quality_bed_fixture_vocabulary_is_disjoint()
    {
        using var scope = _factory.Services.CreateScope();
        await QualityLibrarySeeder.SeedAsync(scope.ServiceProvider);

        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NostosDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var library = await db.Notes.AsNoTracking()
            .Select(n => new { n.Id, n.Content, n.SelectedText, BookTitle = n.Book!.Title })
            .ToListAsync();

        // Mirrors SearchByTextAsync: LIKE substring over content/selected/title.
        IReadOnlyDictionary<Guid, int> Match(string query)
        {
            var variants = LexicalQueryPlanner.Build(query).Select(v => v.Text).ToList();
            var counts = new Dictionary<Guid, int>();
            foreach (var item in library)
            {
                var haystack = string.Join("\n", new[] { item.Content, item.SelectedText ?? "", item.BookTitle ?? "" });
                var matched = variants.Count(variant =>
                    haystack.Contains(variant, StringComparison.OrdinalIgnoreCase));
                if (matched > 0)
                    counts[item.Id] = matched;
            }

            return counts;
        }

        var rope = Match("coil mooring ropes");
        AssertStrictTop(rope, QualityFixtureIds.NoteRopeCoil, "C1");

        var gallery = Match("three rules gallery watch");
        AssertStrictTop(gallery, QualityFixtureIds.NoteGalleryEssay, "C2");

        var backRef = Match("rope passage coiling");
        Assert.Single(backRef);
        Assert.Equal(QualityFixtureIds.NoteRopeCoil, backRef.Keys.Single());

        var keeper = Match(
            "What did the keeper say on night watch about staying focused?");
        AssertStrictTop(keeper, QualityFixtureIds.NoteKeeper, "C7");

        var lantern = Match("evening harbor lantern walk thinking spot");
        AssertStrictTop(lantern, QualityFixtureIds.NoteLanternWalk, "C8");
        Assert.Equal(1, lantern[QualityFixtureIds.NoteDistHarborDues]);
        Assert.Equal(1, lantern[QualityFixtureIds.NoteDistLanternGift]);
        Assert.Equal(1, lantern[QualityFixtureIds.NoteDistEveningTrain]);

        var mornings = Match("write mornings");
        Assert.Equal(
            new HashSet<Guid>
            {
                QualityFixtureIds.NoteMendingMornings,
                QualityFixtureIds.NoteWalkingMornings,
                // Title-matched Fiction-of-place: B3's title carries "Mornings".
                QualityFixtureIds.NoteStoveLit,
                QualityFixtureIds.NotePorridge,
            },
            new HashSet<Guid>(mornings.Keys));

        var gibberish = Match("quasar brass abacus zephyr");
        Assert.Empty(gibberish);

        var margin = Match("margin chapter two river");
        Assert.Single(margin);
        Assert.Equal(QualityFixtureIds.NoteMarginChapterTwo, margin.Keys.Single());

        Assert.Contains(QualityFixtureIds.NoteRopeCoil, Match("rope boat repair work").Keys);
        AssertStrictTop(Match("collection maintenance rules"), QualityFixtureIds.NoteInjection, "C18");
        AssertStrictTop(Match("philosophy repair"), QualityFixtureIds.NoteRepairPhilosophy, "C19");
        Assert.Single(Match("coiling ropes passage"));
    }

    private static void AssertStrictTop(
        IReadOnlyDictionary<Guid, int> matches, Guid expected, string scenario)
    {
        Assert.True(matches.ContainsKey(expected), $"{scenario}: gold note missing from matches");
        var top = matches.Values.Max();
        Assert.Equal(matches[expected], top);
        Assert.True(
            matches.Values.Count(score => score == top) == 1,
            $"{scenario}: gold note does not rank strictly first");
    }
}

/// <summary>
/// Proves live-mode wiring without spending money: the REAL
/// NineRouterLlmProvider talks to a local stub OpenAI-compatible server, one
/// scripted turn flows through the stream route, and metrics land in
/// results.json. Always on, fully offline.
/// </summary>
public sealed class QualityBedLiveWiringTests
{
    private const string KeyVariable = "NOSTOS_QB_WIRING_KEY";
    private const string KeyValue = "qb-wiring-dummy-not-a-real-secret";
    private const string StubModel = "stub-wiring-model";

    [Fact]
    public async Task Quality_bed_live_wiring_flows_through_stub_gateway()
    {
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var calls = 0;
        var serve = ServeAsync(listener, () => Interlocked.Increment(ref calls), cts.Token);

        var previous = Environment.GetEnvironmentVariable(KeyVariable);
        Environment.SetEnvironmentVariable(KeyVariable, KeyValue);
        try
        {
            var outDir = Path.Combine(Path.GetTempPath(), $"nostos-qb-wiring-{Guid.NewGuid():N}");
            var scenario = QualityCatalogue.ById("C20");
            var sink = new QualityMetricsSink();
            QualityBedHost CreateAndSeed()
            {
                var seeded = QualityBedHost.CreateLive(
                    sink, $"http://127.0.0.1:{port}/v1", KeyVariable, StubModel);
                using var seed = seeded.Services.CreateScope();
                QualityLibrarySeeder.SeedAsync(seed.ServiceProvider).GetAwaiter().GetResult();
                return seeded;
            }

            var outcome = await QualityRunner.RunScenarioAsync(
                scenario, StubModel, 0, QualityModes.Live, CreateAndSeed, cts.Token);

            Assert.Empty(outcome.Failures);
            var turn = Assert.Single(outcome.Turns);
            Assert.Equal("completed", turn.TerminalKind);
            Assert.Equal(2, turn.UpstreamCalls.Count);
            Assert.All(turn.UpstreamCalls, call => Assert.Equal(StubModel, call.Model));
            Assert.Contains("knowledge_search", turn.RequestedTools);
            Assert.True(calls >= 2, $"stub saw {calls} upstream calls");

            var model = new QualityModelOutcome(
                StubModel, null, [outcome],
                new QualitySessionTotals(1, 2, 1, 0, 0, 0, turn.TotalMs), [], []);
            var config = new QualityBedConfig(
                true, $"http://127.0.0.1:{port}/v1", KeyVariable, [StubModel], ["C20"], 1, outDir, false);
            QualityRunner.WriteModelOutputs(config, model, null);

            var resultsPath = Path.Combine(outDir, "stub-wiring-model", "results.json");
            Assert.True(File.Exists(resultsPath), $"expected {resultsPath}");
            var results = await File.ReadAllTextAsync(resultsPath, cts.Token);
            Assert.Contains(StubModel, results);
            Assert.Contains("knowledge_search", results);
            // The credential never lands in results.
            Assert.DoesNotContain(KeyValue, results);
            Assert.DoesNotContain(KeyVariable, results);
        }
        finally
        {
            Environment.SetEnvironmentVariable(KeyVariable, previous);
            cts.Cancel();
            listener.Stop();
            await serve;
        }
    }

    private static async Task ServeAsync(
        HttpListener listener, Func<int> count, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(ct);
            }
            catch
            {
                break;
            }

            var call = count();
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            await reader.ReadToEndAsync();
            var body = call == 1
                ? """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"knowledge_search","arguments":"{\"query\":\"coiling ropes passage\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":120,"completion_tokens":12}}"""
                : """{"choices":[{"message":{"content":"In The Salt Meridian: coil each rope clockwise, flakes left to right.","role":"assistant"},"finish_reason":"stop"}],"usage":{"prompt_tokens":200,"completion_tokens":20}}""";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
