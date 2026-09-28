// Unit + stub-gateway coverage for the NOSTOS_QG_REASONING_EFFORT live-run
// control (production posture: reasoning_effort=none).
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class QualityReasoningEffortTests
{
    private const string KeyVariable = "NOSTOS_QB_REASONING_KEY";
    private const string KeyValue = "qb-reasoning-dummy-not-a-real-secret";
    private const string StubModel = "stub-reasoning-model";

    [Fact]
    public async Task Handler_adds_reasoning_effort_exactly_once_and_keeps_other_fields()
    {
        var original = """{"model":"m","stream":false,"max_tokens":64,"messages":[{"role":"user","content":"hi"}]}""";
        var observed = await SendThroughHandlerAsync(
            "none", HttpMethod.Post, "http://127.0.0.1:9/v1/chat/completions", original);

        using var document = JsonDocument.Parse(observed);
        var root = document.RootElement;
        Assert.Equal("none", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal("m", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(64, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("hi", root.GetProperty("messages")[0].GetProperty("content").GetString());

        // Exactly once, even when the product body already carries the param.
        var count = CountOccurrences(observed, "\"reasoning_effort\"");
        Assert.Equal(1, count);

        var already = """{"model":"m","reasoning_effort":"low","messages":[]}""";
        var rewritten = await SendThroughHandlerAsync(
            "none", HttpMethod.Post, "http://127.0.0.1:9/v1/chat/completions", already);
        using var redocument = JsonDocument.Parse(rewritten);
        Assert.Equal("none", redocument.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(1, CountOccurrences(rewritten, "\"reasoning_effort\""));
    }

    [Fact]
    public async Task Handler_leaves_non_chat_and_non_post_requests_untouched()
    {
        var body = """{"model":"m","messages":[]}""";

        var otherPath = await SendThroughHandlerAsync(
            "none", HttpMethod.Post, "http://127.0.0.1:9/v1/embeddings", body);
        Assert.Equal(body, otherPath);

        var get = await SendThroughHandlerAsync(
            "none", HttpMethod.Get, "http://127.0.0.1:9/v1/chat/completions", body);
        Assert.Equal(body, get);
    }

    [Fact]
    public async Task Blank_effort_leaves_the_body_unchanged()
    {
        var body = """{"model":"m","messages":[]}""";
        var observed = await SendThroughHandlerAsync(
            "  ", HttpMethod.Post, "http://127.0.0.1:9/v1/chat/completions", body);
        Assert.Equal(body, observed);
    }

    [Fact]
    public void Config_defaults_to_null_and_reads_the_env_var()
    {
        var previous = Environment.GetEnvironmentVariable(
            QualityReasoningEffortHandler.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                QualityReasoningEffortHandler.EnvironmentVariable, null);
            Assert.Null(QualityBedConfig.FromEnvironment().ReasoningEffort);

            Environment.SetEnvironmentVariable(
                QualityReasoningEffortHandler.EnvironmentVariable, "none");
            Assert.Equal("none", QualityBedConfig.FromEnvironment().ReasoningEffort);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                QualityReasoningEffortHandler.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Model_outcome_defaults_to_null_and_serializes_the_posture()
    {
        var model = new QualityModelOutcome(
            "stub", null, [], new QualitySessionTotals(0, 0, 0, 0, 0, 0, 0), [], []);
        Assert.Null(model.ReasoningEffort);

        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("reasoningEffort", out var unset));
        Assert.Equal(JsonValueKind.Null, unset.ValueKind);

        var live = model with { ReasoningEffort = "none" };
        var liveJson = JsonSerializer.Serialize(live, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var liveDocument = JsonDocument.Parse(liveJson);
        Assert.Equal("none", liveDocument.RootElement.GetProperty("reasoningEffort").GetString());
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData(null, false)]
    public async Task Stub_gateway_sees_the_param_only_when_configured(
        string? effort, bool expectParam)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var bodies = new System.Collections.Concurrent.ConcurrentBag<string>();
        var calls = 0;
        var serve = ServeAsync(listener, bodies, () => Interlocked.Increment(ref calls), cts.Token);

        var previous = Environment.GetEnvironmentVariable(KeyVariable);
        Environment.SetEnvironmentVariable(KeyVariable, KeyValue);
        try
        {
            var outDir = Path.Combine(Path.GetTempPath(), $"nostos-qb-reasoning-{Guid.NewGuid():N}");
            var scenario = QualityCatalogue.ById("C20");
            var sink = new QualityMetricsSink();
            QualityBedHost CreateAndSeed()
            {
                var seeded = QualityBedHost.CreateLive(
                    sink, $"http://127.0.0.1:{port}/v1", KeyVariable, StubModel, effort);
                using var seed = seeded.Services.CreateScope();
                QualityLibrarySeeder.SeedAsync(seed.ServiceProvider).GetAwaiter().GetResult();
                return seeded;
            }

            var outcome = await QualityRunner.RunScenarioAsync(
                scenario, StubModel, 0, QualityModes.Live, CreateAndSeed, cts.Token);

            // Existing wiring assertions stay green: the real provider flows
            // through the stub gateway and meters the scripted turn.
            Assert.Empty(outcome.Failures);
            var turn = Assert.Single(outcome.Turns);
            Assert.Equal("completed", turn.TerminalKind);
            Assert.Equal(2, turn.UpstreamCalls.Count);
            Assert.Contains("knowledge_search", turn.RequestedTools);
            Assert.True(calls >= 2, $"stub saw {calls} upstream calls");

            Assert.NotEmpty(bodies);
            foreach (var body in bodies)
            {
                using var document = JsonDocument.Parse(body);
                var hasParam = document.RootElement.TryGetProperty("reasoning_effort", out var value);
                Assert.Equal(expectParam, hasParam);
                if (expectParam)
                    Assert.Equal("none", value.GetString());
            }

            var config = new QualityBedConfig(
                true, $"http://127.0.0.1:{port}/v1", KeyVariable,
                [StubModel], ["C20"], 1, outDir, false, effort);
            var written = new QualityModelOutcome(
                StubModel, null, [outcome],
                new QualitySessionTotals(1, 2, 1, 0, 0, 0, turn.TotalMs), [], [],
                config.ReasoningEffort);
            QualityRunner.WriteModelOutputs(config, written, null);

            var resultsPath = Path.Combine(outDir, "stub-reasoning-model", "results.json");
            var results = await File.ReadAllTextAsync(resultsPath, cts.Token);
            using var resultsDocument = JsonDocument.Parse(results);
            var recorded = resultsDocument.RootElement.TryGetProperty("reasoningEffort", out var posture)
                ? posture.GetString()
                : "--missing--";
            Assert.Equal(effort, recorded);
            Assert.DoesNotContain(KeyValue, results);
        }
        finally
        {
            Environment.SetEnvironmentVariable(KeyVariable, previous);
            cts.Cancel();
            listener.Stop();
            await serve;
        }
    }

    private static async Task<string> SendThroughHandlerAsync(
        string effort, HttpMethod method, string url, string body)
    {
        string? observed = null;
        var recorder = new RecordingHandler(async request =>
        {
            observed = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });
        using var handler = new QualityReasoningEffortHandler(effort) { InnerHandler = recorder };
        using var client = new HttpClient(handler);
        using var message = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(message);
        response.EnsureSuccessStatusCode();
        return observed ?? string.Empty;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static async Task ServeAsync(
        HttpListener listener,
        System.Collections.Concurrent.ConcurrentBag<string> bodies,
        Func<int> count,
        CancellationToken ct)
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
            bodies.Add(await reader.ReadToEndAsync());
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
