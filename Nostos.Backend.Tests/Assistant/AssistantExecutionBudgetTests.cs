using FluentAssertions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// Per-turn execution ceilings (#406): the call, token and wall-clock limits
/// applied to the meter snapshot before another provider request is made.
/// </summary>
public sealed class AssistantExecutionBudgetTests
{
    private static AssistantExecutionUsage Usage(
        int upstreamCalls = 2,
        int? promptTokens = 1_000,
        int? outputTokens = 100,
        int? thinkingTokens = null,
        long elapsedMs = 1_000) =>
        new(
            upstreamCalls,
            promptTokens,
            outputTokens,
            thinkingTokens,
            promptTokens is not null && outputTokens is not null ? promptTokens + outputTokens : null,
            elapsedMs);

    [Fact]
    public void Cumulative_token_ceiling_trips_at_or_above_the_configured_total()
    {
        var options = new AssistantOptions { MaxTurnTokens = 1_100 };

        AssistantExecutionBudget.ExceededCeiling(Usage(outputTokens: 99), options).Should().BeNull();
        AssistantExecutionBudget.ExceededCeiling(Usage(outputTokens: 100), options).Should().Be("cumulative-token");
        AssistantExecutionBudget.ExceededCeiling(Usage(promptTokens: 50_000, outputTokens: 400), options).Should().Be("cumulative-token");
    }

    [Fact]
    public void Wall_clock_ceiling_trips_at_or_above_the_configured_elapsed_time()
    {
        var options = new AssistantOptions { MaxTurnElapsedMilliseconds = 1_000 };

        AssistantExecutionBudget.ExceededCeiling(Usage(elapsedMs: 999), options).Should().BeNull();
        AssistantExecutionBudget.ExceededCeiling(Usage(elapsedMs: 1_000), options).Should().Be("wall-clock");
    }

    [Fact]
    public void A_ceiling_configured_as_zero_or_negative_is_disabled()
    {
        var options = new AssistantOptions
        {
            MaxTurnTokens = 0,
            MaxTurnElapsedMilliseconds = -1,
        };

        var huge = Usage(promptTokens: 5_000_000, outputTokens: 5_000_000, elapsedMs: 3_600_000);
        AssistantExecutionBudget.ExceededCeiling(huge, options).Should().BeNull();
    }

    [Fact]
    public void A_missing_usage_field_can_never_trip_the_token_ceiling()
    {
        var options = new AssistantOptions
        {
            MaxTurnTokens = 1,
        };

        var unknown = Usage(promptTokens: null, outputTokens: null);
        unknown.ReportedTotalTokens.Should().BeNull();

        AssistantExecutionBudget.ExceededCeiling(unknown, options).Should().BeNull(
            "unknown usage is not over budget — the hard iteration ceiling still bounds the turn");
    }

    [Fact]
    public void Ceilings_are_evaluated_in_a_documented_order()
    {
        var options = new AssistantOptions
        {
            MaxTurnTokens = 1,
            MaxTurnElapsedMilliseconds = 1,
        };

        AssistantExecutionBudget.ExceededCeiling(Usage(), options).Should().Be("cumulative-token");
    }

    [Fact]
    public void Measured_policy_ships_as_the_configured_defaults()
    {
        var options = new AssistantOptions();

        // The measurement notes and limit rationale live in
        // docs/assistant-execution-budgets.md.
        options.MaxToolIterations.Should().Be(12);
        options.MaxTurnTokens.Should().Be(120_000);
        options.MaxTurnElapsedMilliseconds.Should().Be(90_000);
        options.MaxTurnElapsedMilliseconds.Should().BeLessThan(options.RequestTimeoutSeconds * 1000,
            "a stalled call is stopped by the turn budget, not by a transport error");
    }

    [Fact]
    public void The_measured_defaults_stop_a_turn_that_is_already_over_budget()
    {
        var options = new AssistantOptions();

        // 121,000 reported tokens: past what even a whole-library reorganisation
        // re-sending its overview spends, so this is the runaway class the ceiling
        // exists for.
        AssistantExecutionBudget.ExceededCeiling(
            Usage(promptTokens: 115_000, outputTokens: 6_000), options).Should().Be("cumulative-token");

        // Two minutes of wall clock with or without a cheap token bill: the measured
        // stalls (141-145 s) ran on low call counts, which is why latency needs its own
        // dimension.
        AssistantExecutionBudget.ExceededCeiling(
            Usage(elapsedMs: 120_000), options).Should().Be("wall-clock");

    }

    [Fact]
    public void Meter_snapshot_reports_live_usage_and_keeps_unknown_dimensions_unknown()
    {
        var meter = new AssistantExecutionMeter();
        meter.RecordUpstreamRequest();
        meter.RecordCompletion(new LlmCompletion("hi", "stop", [], PromptTokens: 10, CompletionTokens: 5));

        var snapshot = meter.Snapshot();
        snapshot.UpstreamCallCount.Should().Be(1);
        snapshot.ReportedTotalTokens.Should().Be(15);

        Thread.Sleep(30);
        meter.Snapshot().ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(30);

        var unknownMeter = new AssistantExecutionMeter();
        unknownMeter.RecordUpstreamRequest();
        unknownMeter.RecordCompletion(new LlmCompletion("hi", "stop", []));
        unknownMeter.Snapshot().ReportedTotalTokens.Should().BeNull();
        unknownMeter.Snapshot().PromptTokens.Should().BeNull();

        var finished = meter.Finish(AssistantTurnStopReason.Completed);
        finished.UpstreamCallCount.Should().Be(1);
        finished.ReportedTotalTokens.Should().Be(15);
        finished.StopReason.Should().Be(AssistantTurnStopReason.Completed);
    }
}
