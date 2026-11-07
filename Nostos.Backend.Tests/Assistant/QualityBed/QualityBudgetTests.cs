// Offline proof that the live quality bed's spend cap holds (issue #1466).
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Text.Json;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

public sealed class QualityBudgetTests
{
    private const string KeyVariable = "NOSTOS_QB_BUDGET_KEY";
    private const string Model = "stub-budget-model";

    // 1,000 prompt + 100 completion tokens per call.
    private const string Answer =
        """{"choices":[{"message":{"role":"assistant","content":"I could not find that."},"finish_reason":"stop"}],"usage":{"prompt_tokens":1000,"completion_tokens":100}}""";

    [Fact]
    public async Task A_live_campaign_stops_calling_the_model_once_the_cap_is_spent()
    {
        await using var server = LoopbackHttpServer.Responding(200, Answer);
        var outDir = Path.Combine(Path.GetTempPath(), $"nostos-qb-budget-{Guid.NewGuid():N}");
        // $1 per million tokens both ways: one call costs $0.0011, so a
        // $0.002 cap is spent by the second call and nothing runs after it
        // (scenarios run in catalogue order: C7, C10, then C19 and C20 are skipped).
        var budget = new QualityBudget(0.002m, 1m, 1m, null);

        var models = await WithKey(() => QualityRunner.RunLiveAsync(
            Config(server.BaseUrl, outDir, ["C10", "C19", "C20", "C7"]), budget));

        Assert.Equal(2, server.Requests);
        Assert.True(budget.Exhausted);
        var failures = models.Single().Failures;
        Assert.Contains(failures, f => f.Contains("cost cap reached", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("C20 r0: not run", StringComparison.Ordinal));

        using var written = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "budget.json")));
        Assert.True(written.RootElement.GetProperty("capReached").GetBoolean());
        Assert.Equal(0.0022m, written.RootElement.GetProperty("spentUsd").GetDecimal());
    }

    [Fact]
    public async Task A_campaign_larger_than_the_run_cap_is_refused_before_any_call()
    {
        await using var server = LoopbackHttpServer.Responding(200, Answer);
        var budget = new QualityBudget(2m, 1m, 1m, maxScenarioRuns: 1);

        await Assert.ThrowsAsync<QualityBudgetExceededException>(() => WithKey(() =>
            QualityRunner.RunLiveAsync(
                Config(server.BaseUrl, Path.GetTempPath(), ["C10", "C20"]), budget)));

        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task A_live_campaign_without_a_cap_is_refused()
    {
        await using var server = LoopbackHttpServer.Responding(200, Answer);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => WithKey(() =>
            QualityRunner.RunLiveAsync(
                Config(server.BaseUrl, Path.GetTempPath(), ["C20"]), QualityBudget.Unlimited)));

        Assert.Contains(QualityBudget.MaxUsdVariable, refused.Message);
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public void A_call_without_reported_usage_still_counts_against_the_cap()
    {
        var budget = new QualityBudget(1m, 1m, 1m, null);
        var request = new LlmCompletionRequest([LlmMessage.User(new string('x', 3000))], [], 64);

        budget.Record(request, new LlmCompletion("ok", "stop", []));

        Assert.Equal(1, budget.EstimatedCalls);
        Assert.True(budget.SpentUsd > 0.0009m, $"estimated {budget.SpentUsd}");
    }

    private static QualityBedConfig Config(string baseUrl, string outDir, string[] scenarios) =>
        new(true, baseUrl, KeyVariable, [Model], scenarios, 1, outDir, false);

    private static async Task<T> WithKey<T>(Func<Task<T>> run)
    {
        var previous = Environment.GetEnvironmentVariable(KeyVariable);
        Environment.SetEnvironmentVariable(KeyVariable, "qb-budget-dummy-not-a-secret");
        try
        {
            return await run();
        }
        finally
        {
            Environment.SetEnvironmentVariable(KeyVariable, previous);
        }
    }
}
