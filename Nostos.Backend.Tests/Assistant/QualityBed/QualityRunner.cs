// Campaign runner for the #566 quality bed.
//
// One host per scenario (fresh temp database + seed): scenarios never see
// each other's notes. Every turn goes through the streamed turn route
// (POST /api/assistant/turn/stream) so time-to-first-activity and
// time-to-first-answer are measurable from the NDJSON events in both modes.
// Deterministic mode swaps ILlmProvider for the scripted provider; live mode
// keeps the real NineRouterLlmProvider and only meters it.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;

internal sealed record QualityBedConfig(
    bool Live,
    string BaseUrl,
    string KeyEnvName,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> ScenarioIds,
    int Reps,
    string OutDir,
    bool Blind)
{
    public static QualityBedConfig FromEnvironment()
    {
        var live = string.Equals(
            Environment.GetEnvironmentVariable("NOSTOS_QG_LIVE"), "1", StringComparison.Ordinal);
        var models = (Environment.GetEnvironmentVariable("NOSTOS_QG_MODELS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scenarios = (Environment.GetEnvironmentVariable("NOSTOS_QG_SCENARIOS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var reps = int.TryParse(
            Environment.GetEnvironmentVariable("NOSTOS_QG_REPS"), out var parsed) ? Math.Max(1, parsed) : 1;
        var outDir = Environment.GetEnvironmentVariable("NOSTOS_QG_OUT")
            ?? Path.Combine(Path.GetTempPath(), $"nostos-quality-bed-{Guid.NewGuid():N}");
        return new QualityBedConfig(
            live,
            Environment.GetEnvironmentVariable("NOSTOS_QG_BASE_URL") ?? string.Empty,
            Environment.GetEnvironmentVariable("NOSTOS_QG_KEY_ENV") ?? "NOSTOS_QG_KEY",
            models,
            scenarios,
            reps,
            outDir,
            string.Equals(
                Environment.GetEnvironmentVariable("NOSTOS_QG_BLIND"), "1", StringComparison.Ordinal));
    }
}

internal sealed record QualityModelOutcome(
    string Model,
    string? BlindId,
    IReadOnlyList<QualityScenarioOutcome> Scenarios,
    QualitySessionTotals Totals,
    IReadOnlyList<string> Failures,
    // Flat live-mode advisories with scenario/turn context. Serialized as the
    // top-level `advisories` array in results.json; existing fields unchanged.
    IReadOnlyList<string>? Advisories = null);

internal sealed class QualityBedHost : IDisposable
{
    private readonly IDisposable _host;

    public LibraryEndpointFactory Factory { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services { get; }

    public QualityScriptedProvider? Scripted { get; }

    public QualityMetricsSink Sink { get; }

    public string ModelId { get; }

    private QualityBedHost(
        LibraryEndpointFactory factory,
        IDisposable host,
        HttpClient client,
        IServiceProvider services,
        QualityScriptedProvider? scripted,
        QualityMetricsSink sink,
        string modelId)
    {
        Factory = factory;
        _host = host;
        Client = client;
        Services = services;
        Scripted = scripted;
        Sink = sink;
        ModelId = modelId;
    }

    public void Dispose()
    {
        Client.Dispose();
        _host.Dispose();
        Factory.Dispose();
    }

    public static QualityBedHost CreateDeterministic(QualityMetricsSink sink, string keyVariable)
    {
        var factory = new LibraryEndpointFactory();
        var scripted = new QualityScriptedProvider();
        var metered = new MeteredLlmProvider(scripted, sink, "quality-scripted");
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Assistant:Enabled", "true");
            builder.UseSetting("Assistant:BaseUrl", "http://assistant.invalid/v1");
            builder.UseSetting("Assistant:ApiKeyEnvironmentVariable", keyVariable);
            builder.UseSetting("Assistant:Model", "quality-scripted");
            builder.UseSetting("Assistant:MaxToolIterations", "6");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmProvider>();
                services.AddSingleton<ILlmProvider>(metered);
            });
        });
        var client = host.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        return new QualityBedHost(
            factory, host, client, host.Services, scripted, sink, "quality-scripted");
    }

    public static QualityBedHost CreateLive(
        QualityMetricsSink sink, string baseUrl, string keyVariable, string model)
    {
        var factory = new LibraryEndpointFactory();
        var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Assistant:Enabled", "true");
            builder.UseSetting("Assistant:BaseUrl", baseUrl);
            builder.UseSetting("Assistant:ApiKeyEnvironmentVariable", keyVariable);
            builder.UseSetting("Assistant:Model", model);
            builder.UseSetting("Assistant:MaxToolIterations", "6");
            builder.ConfigureServices(services =>
            {
                // The real product provider, constructed from the host's own
                // services; the decorator only observes (never alters) calls.
                services.RemoveAll<ILlmProvider>();
                services.AddSingleton<ILlmProvider>(provider => new MeteredLlmProvider(
                    new NineRouterLlmProvider(
                        provider.GetRequiredService<IHttpClientFactory>(),
                        provider.GetRequiredService<IAiProviderConfigResolver>(),
                        provider.GetRequiredService<ILogger<NineRouterLlmProvider>>()),
                    sink,
                    model));
            });
        });
        var client = host.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        return new QualityBedHost(
            factory, host, client, host.Services, null, sink, model);
    }
}

internal static class QualityRunner
{
    private const string DeterministicKeyVariable = "NOSTOS_QB_DET_KEY";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions Ndjson = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------
    // Campaigns
    // ------------------------------------------------------------------

    public static async Task<IReadOnlyList<QualityModelOutcome>> RunDeterministicAsync(
        QualityBedConfig config,
        CancellationToken ct = default)
    {
        var scenarios = FilteredScenarios(config, QualityModes.Deterministic);
        var previous = Environment.GetEnvironmentVariable(DeterministicKeyVariable);
        Environment.SetEnvironmentVariable(DeterministicKeyVariable, "qb-dummy-not-a-secret");
        try
        {
            var sink = new QualityMetricsSink();
            var outcomes = new List<QualityScenarioOutcome>();
            foreach (var scenario in scenarios)
                for (var rep = 0; rep < config.Reps; rep++)
                    outcomes.Add(await RunScenarioAsync(
                        scenario, "quality-scripted", rep, QualityModes.Deterministic,
                        () => CreateSeededDeterministicHost(new QualityMetricsSink()), ct));

            var model = new QualityModelOutcome(
                "quality-scripted", null, outcomes,
                Sum(outcomes.SelectMany(o => new[] { o.Totals })),
                outcomes.SelectMany(o => o.Failures).ToList(),
                outcomes.SelectMany(o => o.Advisories ?? []).ToList());
            var written = WriteModelOutputs(config, model, blindId: null);
            Console.WriteLine($"quality bed (deterministic): {written}");
            return [model];
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeterministicKeyVariable, previous);
        }
    }

    public static async Task<IReadOnlyList<QualityModelOutcome>> RunLiveAsync(
        QualityBedConfig config,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new InvalidOperationException(
                "NOSTOS_QG_BASE_URL is required for a live campaign (never commit its value).");
        if (config.Models.Count == 0)
            throw new InvalidOperationException(
                "NOSTOS_QG_MODELS is required for a live campaign (comma-separated model ids).");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.KeyEnvName)))
            throw new InvalidOperationException(
                $"The key holder '{config.KeyEnvName}' is empty: export it before running live. " +
                "The key is read by the host at call time and never logged or written.");

        var scenarios = FilteredScenarios(config, QualityModes.Live);
        var models = new List<QualityModelOutcome>();
        var index = 0;
        foreach (var model in config.Models)
        {
            index++;
            var blindId = config.Blind ? $"qb-model-{index:00}" : null;
            var outcomes = new List<QualityScenarioOutcome>();
            foreach (var scenario in scenarios)
                for (var rep = 0; rep < config.Reps; rep++)
                    // Provider errors are recorded per turn inside the
                    // outcome; a transport failure on one scenario must not
                    // abort the whole campaign.
                    outcomes.Add(await RunScenarioRobustAsync(
                        scenario, model, rep, config, blindId, ct));

            models.Add(new QualityModelOutcome(
                model, blindId, outcomes,
                Sum(outcomes.SelectMany(o => new[] { o.Totals })),
                outcomes.SelectMany(o => o.Failures).ToList(),
                outcomes.SelectMany(o => o.Advisories ?? []).ToList()));
            Console.WriteLine($"quality bed (live): {WriteModelOutputs(config, models[^1], blindId)}");
        }

        if (config.Blind)
            WriteBlindPack(config, models);
        return models;
    }

    private static async Task<QualityScenarioOutcome> RunScenarioRobustAsync(
        QualityScenario scenario,
        string model,
        int rep,
        QualityBedConfig config,
        string? blindId,
        CancellationToken ct)
    {
        try
        {
            var sink = new QualityMetricsSink();
            return await RunScenarioAsync(
                scenario, model, rep, QualityModes.Live,
                () => CreateSeededLiveHost(sink, config, model), ct);
        }
        catch (Exception exception)
        {
            // Recorded as data, never thrown: one broken scenario (bad model
            // id, dead gateway) must not crash the campaign.
            var sessionId = $"qb-{scenario.Id}-r{rep}-broken";
            return new QualityScenarioOutcome(
                scenario.Id, scenario.Title, sessionId, blindId ?? model, [],
                new QualitySessionTotals(0, 0, 0, 0, 0, 0, 0),
                [$"scenario setup failed: {exception.GetType().Name}: {exception.Message}"]);
        }
    }

    private static QualityBedHost CreateSeededDeterministicHost(QualityMetricsSink sink)
    {
        var host = QualityBedHost.CreateDeterministic(sink, DeterministicKeyVariable);
        using var scope = host.Services.CreateScope();
        QualityLibrarySeeder.SeedAsync(scope.ServiceProvider).GetAwaiter().GetResult();
        return host;
    }

    private static QualityBedHost CreateSeededLiveHost(
        QualityMetricsSink sink, QualityBedConfig config, string model)
    {
        var host = QualityBedHost.CreateLive(sink, config.BaseUrl, config.KeyEnvName, model);
        using var scope = host.Services.CreateScope();
        QualityLibrarySeeder.SeedAsync(scope.ServiceProvider).GetAwaiter().GetResult();
        return host;
    }

    private static List<QualityScenario> FilteredScenarios(QualityBedConfig config, QualityModes mode)
    {
        var all = QualityCatalogue.All.Where(s => s.Modes.HasFlag(mode)).ToList();
        if (config.ScenarioIds.Count == 0)
            return all;
        var wanted = new HashSet<string>(config.ScenarioIds, StringComparer.OrdinalIgnoreCase);
        return all.Where(s => wanted.Contains(s.Id)).ToList();
    }

    // ------------------------------------------------------------------
    // One scenario, one session
    // ------------------------------------------------------------------

    public static async Task<QualityScenarioOutcome> RunScenarioAsync(
        QualityScenario scenario,
        string model,
        int rep,
        QualityModes mode,
        Func<QualityBedHost> createHost,
        CancellationToken ct = default)
    {
        using var host = createHost();
        var sessionId = $"qb-{scenario.Id.ToLowerInvariant()}-r{rep}-{Guid.NewGuid():N}";
        var history = new List<AssistantHistoryMessageDto>();
        var priors = new List<AssistantTurnResponse>();
        var turns = new List<QualityTurnRecord>();
        var failures = new List<string>();
        var advisories = new List<string>();
        var live = mode == QualityModes.Live;

        for (var index = 0; index < scenario.Turns.Count; index++)
        {
            var spec = scenario.Turns[index];
            var record = await ExecuteTurnAsync(
                host, scenario.Id, index, spec, sessionId, history, priors, mode, ct);
            turns.Add(record);
            if (record.Response is not null)
                priors.Add(record.Response);

            var evaluation = QualityExpectationEvaluator.Evaluate(spec, record, live);
            foreach (var failure in evaluation.Failures)
                failures.Add($"{scenario.Id} turn {index}: {failure}");
            var turnAdvisories = evaluation.Advisories.ToList();
            foreach (var advisory in turnAdvisories)
                advisories.Add($"{scenario.Id} turn {index}: {advisory}");

            if (spec.Verify is not null)
            {
                try
                {
                    var verifyNeeded = new QualityTurnVerifyContext(
                        host.Services, record, turns, mode);
                    var problem = await spec.Verify(verifyNeeded);
                    if (problem is not null)
                    {
                        if (live && (spec.Expect?.VerifyAdvisoryInLive == true))
                        {
                            var advisory = $"verify: {problem}";
                            turnAdvisories.Add(advisory);
                            advisories.Add($"{scenario.Id} turn {index}: {advisory}");
                        }
                        else
                        {
                            failures.Add($"{scenario.Id} turn {index}: {problem}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(
                        $"{scenario.Id} turn {index}: verify threw {exception.GetType().Name}: {exception.Message}");
                }
            }

            turns[index] = record with { Advisories = turnAdvisories };
            AppendHistory(history, spec, record);
        }

        var totals = new QualitySessionTotals(
            turns.Count,
            turns.Sum(t => t.UpstreamCalls.Count),
            turns.Sum(t => t.RequestedTools.Count),
            turns.SelectMany(t => t.UpstreamCalls).Sum(c => (long)(c.PromptTokens ?? 0)),
            turns.SelectMany(t => t.UpstreamCalls).Sum(c => (long)(c.CompletionTokens ?? 0)),
            turns.SelectMany(t => t.UpstreamCalls).Sum(c => (long)(c.ThinkingTokens ?? 0)),
            turns.Sum(t => t.TotalMs));

        return new QualityScenarioOutcome(
            scenario.Id, scenario.Title, sessionId, model, turns, totals, failures, advisories);
    }

    private static void AppendHistory(
        List<AssistantHistoryMessageDto> history,
        QualityTurnSpec spec,
        QualityTurnRecord record)
    {
        var context = spec.Context?.Invoke();
        history.Add(new AssistantHistoryMessageDto(
            "user",
            spec.Message,
            Context: context is null ? null : new AssistantHistoricalContextDto(
                Surface: context.Surface,
                BookId: context.BookId,
                BookTitle: context.BookTitle),
            Evidence: null,
            Actions: null,
            CapturedNoteId: null,
            EvidenceHandles: null));

        if (record.Response is { } response)
        {
            history.Add(new AssistantHistoryMessageDto(
                "assistant",
                response.Reply,
                Context: null,
                Evidence: response.Sources
                    ?.Select(s => new AssistantHistoricalEvidenceDto(
                        s.BookId.ToString(), s.BookTitle, s.SourceSha256, s.Locators))
                    .ToList(),
                Actions: response.ExecutedCapabilities,
                CapturedNoteId: response.CapturedNoteId,
                EvidenceHandles: response.Evidence?.Select(e => e.Handle).ToList()));
        }
    }

    // ------------------------------------------------------------------
    // One turn through the stream route
    // ------------------------------------------------------------------

    public static async Task<QualityTurnRecord> ExecuteTurnAsync(
        QualityBedHost host,
        string scenarioId,
        int turnIndex,
        QualityTurnSpec spec,
        string sessionId,
        List<AssistantHistoryMessageDto> history,
        IReadOnlyList<AssistantTurnResponse> priors,
        QualityModes mode,
        CancellationToken ct = default)
    {
        var turnId = $"{sessionId}-t{turnIndex:00}";
        string? continuationId = null;
        if (spec.ContinueFromPrompt)
        {
            continuationId = priors.Count == 0
                ? null
                : priors[^1].AnchorPrompt?.ContinuationId;
            if (continuationId is null)
                return HarnessErrorRecord(
                    host, turnIndex, turnId, spec,
                    "no continuation id on the previous turn (expected an anchor prompt)");
        }

        host.Scripted?.BeginTurn(
            scenarioId,
            turnIndex,
            spec.Script ?? (_ => Task.FromResult(QualityScript.Reply(string.Empty))),
            priors);

        var context = spec.Context?.Invoke()
            ?? new AssistantContextDto("second-brain", "/second-brain");
        var request = new AssistantTurnRequest(
            ClientId: sessionId,
            IdempotencyKey: turnId,
            Message: spec.Message,
            Context: context,
            History: history.Count == 0 ? null : history.ToList(),
            ConversationId: sessionId,
            TurnId: turnId,
            ContinuationId: continuationId,
            ContinuationSkipped: false);

        var (notesBefore, collectionsBefore) = await CountAsync(host, ct);
        var meterFrom = host.Sink.Count;
        var wall = System.Diagnostics.Stopwatch.StartNew();

        List<QualityActivityEvent> activities = new();
        AssistantTurnEventDto? terminal = null;
        double ttfActivity = -1;
        string? harnessError = null;
        Task? hookTask = null;
        Exception? hookError = null;

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, AssistantEndpoints.StreamTurnRoute)
            {
                Content = JsonContent.Create(request, options: Ndjson),
            };
            using var response = await host.Client.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                AssistantTurnEventDto? turnEvent;
                try
                {
                    turnEvent = JsonSerializer.Deserialize<AssistantTurnEventDto>(line, Ndjson);
                }
                catch (JsonException)
                {
                    harnessError = $"unreadable NDJSON line: {line[..Math.Min(line.Length, 120)]}";
                    break;
                }

                if (turnEvent is null)
                    continue;

                if (string.Equals(turnEvent.Kind, AssistantTurnEventKinds.Activity, StringComparison.Ordinal)
                    && turnEvent.Activity is { } activity)
                {
                    if (ttfActivity < 0)
                        ttfActivity = wall.Elapsed.TotalMilliseconds;
                    activities.Add(new QualityActivityEvent(
                        activity.Code, activity.Message, wall.Elapsed.TotalMilliseconds));
                }
                else if (string.Equals(turnEvent.Kind, AssistantTurnEventKinds.Started, StringComparison.Ordinal))
                {
                    if (spec.Hook is not null)
                    {
                        if (host.Scripted is null)
                        {
                            harnessError = "turn hook requires the scripted provider (deterministic-only scenario?)";
                            break;
                        }

                        var hookContext = new QualityHookContext(
                            host.Client, sessionId, turnId, host.Scripted.Gate);
                        hookTask = Task.Run(async () =>
                        {
                            try
                            {
                                await spec.Hook(hookContext);
                            }
                            catch (Exception exception)
                            {
                                hookError = exception;
                            }
                        }, ct);
                    }
                }
                else if (turnEvent.Kind is var eventKind
                    && (string.Equals(eventKind, AssistantTurnEventKinds.Completed, StringComparison.Ordinal)
                        || string.Equals(eventKind, AssistantTurnEventKinds.Failed, StringComparison.Ordinal)
                        || string.Equals(eventKind, AssistantTurnEventKinds.Cancelled, StringComparison.Ordinal)))
                {
                    terminal = turnEvent;
                }
            }

            if (hookTask is not null)
            {
                await hookTask;
                if (hookError is not null)
                    harnessError = $"hook failed: {hookError.GetType().Name}: {hookError.Message}";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            harnessError = $"transport failed: {exception.GetType().Name}: {exception.Message}";
        }
        catch (OperationCanceledException)
        {
            harnessError = "transport cancelled (client timeout?)";
        }

        wall.Stop();
        var (notesAfter, collectionsAfter) = await CountAsync(host, CancellationToken.None);
        var upstream = host.Sink.Since(meterFrom).ToList();
        var requested = upstream
            .SelectMany(c => c.ToolNames)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var providerRequests = (host.Scripted?.TurnRequests ?? new List<LlmCompletionRequest>())
            .Select(r => new QualityProviderRequestLog(
                r.Messages.Select(m => new QualityProviderMessageLog(m.Role, m.Content)).ToList()))
            .ToList();

        if (harnessError is not null)
            return new QualityTurnRecord(
                turnIndex, turnId, spec.Message, "harness_error", null, null, terminal?.Response,
                upstream, requested, activities, ttfActivity, wall.Elapsed.TotalMilliseconds,
                wall.Elapsed.TotalMilliseconds, providerRequests.Count,
                providerRequests, notesBefore, notesAfter, collectionsBefore, collectionsAfter,
                harnessError);

        if (terminal is null)
            return HarnessErrorRecord(
                host, turnIndex, turnId, spec, "stream ended without a terminal event",
                upstream, requested, activities, wall, providerRequests,
                notesBefore, notesAfter, collectionsBefore, collectionsAfter);

        var kind = terminal.Kind;
        return new QualityTurnRecord(
            turnIndex, turnId, spec.Message, kind,
            terminal.Failure?.Code, terminal.Failure?.Message,
            terminal.Response, upstream, requested, activities,
            ttfActivity, wall.Elapsed.TotalMilliseconds, wall.Elapsed.TotalMilliseconds,
            providerRequests.Count, providerRequests,
            notesBefore, notesAfter, collectionsBefore, collectionsAfter, null);
    }

    private static QualityTurnRecord HarnessErrorRecord(
        QualityBedHost host,
        int turnIndex,
        string turnId,
        QualityTurnSpec spec,
        string error,
        IReadOnlyList<QualityUpstreamCall>? upstream = null,
        IReadOnlyList<string>? requested = null,
        IReadOnlyList<QualityActivityEvent>? activities = null,
        System.Diagnostics.Stopwatch? wall = null,
        IReadOnlyList<QualityProviderRequestLog>? providerRequests = null,
        int notesBefore = 0, int notesAfter = 0, int collectionsBefore = 0, int collectionsAfter = 0) =>
        new(
            turnIndex, turnId, spec.Message, "harness_error", null, null, null,
            upstream ?? [], requested ?? [], activities ?? [],
            -1, wall?.Elapsed.TotalMilliseconds ?? 0, wall?.Elapsed.TotalMilliseconds ?? 0,
            providerRequests?.Count ?? 0, providerRequests ?? [],
            notesBefore, notesAfter, collectionsBefore, collectionsAfter, error);

    private static async Task<(int Notes, int Collections)> CountAsync(
        QualityBedHost host, CancellationToken ct)
    {
        try
        {
            using var scope = host.Services.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NostosDbContext>>();
            await using var db = await factory.CreateDbContextAsync(ct);
            return (
                await db.Notes.CountAsync(ct),
                await db.Collections.CountAsync(ct));
        }
        catch
        {
            // Counts are auxiliary: a failed count must not fail the turn.
            return (0, 0);
        }
    }

    // ------------------------------------------------------------------
    // Outputs
    // ------------------------------------------------------------------

    internal static string WriteModelOutputs(
        QualityBedConfig config, QualityModelOutcome model, string? blindId)
    {
        var slug = Slug(model.Model);
        var directory = Path.Combine(config.OutDir, slug);
        Directory.CreateDirectory(directory);

        var resultsPath = Path.Combine(directory, "results.json");
        File.WriteAllText(resultsPath, JsonSerializer.Serialize(model, Json));
        var transcriptPath = Path.Combine(directory, "transcript.md");
        File.WriteAllText(transcriptPath, BuildTranscript(model));

        return $"{directory} (results.json, transcript.md)";
    }

    private static void WriteBlindPack(
        QualityBedConfig config, IReadOnlyList<QualityModelOutcome> models)
    {
        var blindDirectory = Path.Combine(config.OutDir, "blind");
        Directory.CreateDirectory(blindDirectory);
        var mapping = new Dictionary<string, string>();
        foreach (var model in models)
        {
            if (model.BlindId is null)
                continue;
            mapping[model.BlindId] = model.Model;
            var redacted = model with { Model = model.BlindId };
            File.WriteAllText(
                Path.Combine(blindDirectory, $"{model.BlindId}.transcript.md"),
                BuildTranscript(redacted));
            File.WriteAllText(
                Path.Combine(blindDirectory, $"{model.BlindId}.results.json"),
                JsonSerializer.Serialize(redacted, Json));
        }

        // The mapping stays OUT of the blind directory by design.
        File.WriteAllText(
            Path.Combine(config.OutDir, "mapping.json"),
            JsonSerializer.Serialize(mapping, Json));
        Console.WriteLine($"quality bed (blind): {blindDirectory} + mapping.json");
    }

    private static string Slug(string model)
    {
        var builder = new StringBuilder(model.ToLowerInvariant());
        foreach (var banned in Path.GetInvalidFileNameChars())
            builder.Replace(banned, '-');
        builder.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
        var slug = builder.ToString();
        return slug.Length <= 80 ? slug : slug[..80];
    }

    private static string BuildTranscript(QualityModelOutcome model)
    {
        var output = new StringBuilder();
        output.AppendLine($"# Ask Nostos quality bed — {model.Model}");
        output.AppendLine();
        foreach (var scenario in model.Scenarios)
        {
            var catalogue = QualityCatalogue.All.FirstOrDefault(
                s => string.Equals(s.Id, scenario.ScenarioId, StringComparison.Ordinal));
            output.AppendLine($"## {scenario.ScenarioId} — {scenario.Title}");
            output.AppendLine();
            if (catalogue is not null)
            {
                output.AppendLine($"Goal: {catalogue.Goal}");
                output.AppendLine();
                output.AppendLine($"Rubric: {catalogue.Rubric}");
                output.AppendLine();
            }

            foreach (var turn in scenario.Turns)
            {
                output.AppendLine($"### Turn {turn.TurnIndex} (`{turn.TurnId}`, {turn.TerminalKind})");
                output.AppendLine();
                output.AppendLine($"User: {turn.UserMessage}");
                output.AppendLine();
                if (turn.Response is { } response)
                {
                    output.AppendLine($"Reply: {response.Reply}");
                    output.AppendLine();
                    if (!string.IsNullOrWhiteSpace(response.Acknowledgement))
                    {
                        output.AppendLine($"Acknowledgement: {response.Acknowledgement}");
                        output.AppendLine();
                    }

                    if (response.Evidence is { Count: > 0 })
                    {
                        output.AppendLine("Evidence:");
                        foreach (var evidence in response.Evidence)
                            output.AppendLine(
                                $"- [{evidence.Handle.Kind}] {evidence.Label} " +
                                $"(note={evidence.Handle.NoteId}, concept={evidence.Handle.ConceptId}, " +
                                $"book={evidence.Handle.BookId})");
                        output.AppendLine();
                    }

                    if (response.Sources is { Count: > 0 })
                    {
                        output.AppendLine("Sources:");
                        foreach (var source in response.Sources)
                            output.AppendLine($"- {source.BookTitle} [{source.Format}] {source.Excerpt[..Math.Min(120, source.Excerpt.Length)]}");
                        output.AppendLine();
                    }

                    if (response.ExecutedCapabilities is { Count: > 0 })
                        output.AppendLine($"Executed: {string.Join(", ", response.ExecutedCapabilities)}");
                    if (response.PendingPlan is not null)
                        output.AppendLine($"Pending plan: {response.PendingPlan.PlanId} ({response.PendingPlan.Summary})");
                    if (response.AnchorPrompt is not null)
                        output.AppendLine($"Anchor prompt [{response.AnchorPrompt.Kind}]: {response.AnchorPrompt.Question}");
                    if (response.Error is not null)
                        output.AppendLine($"Error: {response.Error.Code}: {response.Error.Message}");
                    if (!string.IsNullOrWhiteSpace(response.CapturedNoteId))
                        output.AppendLine($"Captured: {response.CapturedNoteId}");
                    output.AppendLine();
                }

                if (turn.FailureCode is not null)
                    output.AppendLine($"Terminal failure: {turn.FailureCode}: {turn.FailureMessage}");
                output.AppendLine(
                    $"Upstream calls: {turn.UpstreamCalls.Count} " +
                    $"({string.Join(", ", turn.RequestedTools)}), " +
                    $"tokens p/c/t: {turn.UpstreamCalls.Sum(c => c.PromptTokens ?? 0)}/" +
                    $"{turn.UpstreamCalls.Sum(c => c.CompletionTokens ?? 0)}/" +
                    $"{turn.UpstreamCalls.Sum(c => c.ThinkingTokens ?? 0)}, " +
                    $"TTF activity: {(turn.TtfActivityMs < 0 ? "none" : $"{turn.TtfActivityMs:F0} ms")}, " +
                    $"total: {turn.TotalMs:F0} ms.");
                if (turn.HarnessError is not null)
                    output.AppendLine($"HARNESS ERROR: {turn.HarnessError}");
                if ((turn.Advisories ?? []).Count > 0)
                {
                    output.AppendLine("Advisories:");
                    foreach (var advisory in turn.Advisories!)
                        output.AppendLine($"- {advisory}");
                }
                output.AppendLine();
            }

            output.AppendLine(
                $"Scenario totals: {scenario.Totals.Turns} turns, {scenario.Totals.UpstreamCalls} upstream, " +
                $"{scenario.Totals.ToolCalls} tools, {scenario.Totals.ElapsedMs:F0} ms.");
            if (scenario.Failures.Count > 0)
            {
                output.AppendLine("Failures:");
                foreach (var failure in scenario.Failures)
                    output.AppendLine($"- {failure}");
            }
            if ((scenario.Advisories ?? []).Count > 0)
            {
                output.AppendLine($"Advisories ({scenario.Advisories!.Count}):");
                foreach (var advisory in scenario.Advisories!)
                    output.AppendLine($"- {advisory}");
            }

            output.AppendLine();
        }

        return output.ToString();
    }

    private static QualitySessionTotals Sum(IEnumerable<QualitySessionTotals> totals)
    {
        var list = totals.ToList();
        return new QualitySessionTotals(
            list.Sum(t => t.Turns),
            list.Sum(t => t.UpstreamCalls),
            list.Sum(t => t.ToolCalls),
            list.Sum(t => t.PromptTokens),
            list.Sum(t => t.CompletionTokens),
            list.Sum(t => t.ThinkingTokens),
            list.Sum(t => t.ElapsedMs));
    }
}
