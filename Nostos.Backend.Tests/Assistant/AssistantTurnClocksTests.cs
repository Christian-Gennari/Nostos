using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Integrations.Assistant;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// The turn's clock window (issue #1561, extracted in #1598): which ceilings
/// stop the turn before an upstream call, and when a request gets its own clock
/// inside what is left of the turn. The order is the contract — an inner clock
/// that cannot fire first would report "execution limit" for a stalled request.
/// </summary>
public sealed class AssistantTurnClocksTests
{
    private static readonly AssistantOptions Options = new()
    {
        MaxTurnTokens = 120_000,
        MaxTurnElapsedMilliseconds = 90_000,
        RequestTimeoutSeconds = 60,
    };

    private static AssistantTurnClocks Clocks(AssistantOptions? options = null) =>
        new(options ?? Options, TimeProvider.System, NullLogger.Instance);

    private static AssistantExecutionUsage Usage(long elapsedMilliseconds, int? totalTokens = null) =>
        new(
            UpstreamCallCount: 1,
            PromptTokens: totalTokens is null ? null : totalTokens,
            OutputTokens: null,
            ThinkingTokens: null,
            ReportedTotalTokens: totalTokens,
            ElapsedMilliseconds: elapsedMilliseconds);

    [Fact]
    public void A_reached_token_ceiling_stops_the_turn_before_another_call()
    {
        Clocks().Open(Usage(1_000, totalTokens: 120_000)).Should().BeNull();
    }

    [Fact]
    public void A_reached_wall_clock_stops_the_turn_before_another_call()
    {
        Clocks().Open(Usage(90_000)).Should().BeNull();
    }

    [Fact]
    public void A_live_turn_gets_the_rest_of_its_budget_and_a_full_request_timeout()
    {
        var window = Clocks().Open(Usage(10_000));

        window.Should().NotBeNull();
        window!.Value.RemainingTurnMilliseconds.Should().Be(80_000);
        window.Value.RequestTimeoutMilliseconds.Should().Be(60_000);
        window.Value.RequestClockFitsInTurn.Should().BeTrue(
            "60 s fits inside the 80 s the turn has left");
    }

    [Fact]
    public void A_turn_with_less_than_a_request_timeout_left_lets_the_turn_clock_fire()
    {
        var window = Clocks().Open(Usage(40_000));

        window.Should().NotBeNull();
        window!.Value.RemainingTurnMilliseconds.Should().Be(50_000);
        window.Value.RequestClockFitsInTurn.Should().BeFalse(
            "the turn clock is the deliberate failure when a full request cannot finish");
    }

    [Fact]
    public void A_disabled_turn_ceiling_gives_the_request_its_own_clock()
    {
        var options = new AssistantOptions
        {
            MaxTurnTokens = 0,
            MaxTurnElapsedMilliseconds = 0,
            RequestTimeoutSeconds = 60,
        };

        var window = Clocks(options).Open(Usage(3_600_000));

        window.Should().NotBeNull();
        window!.Value.RemainingTurnMilliseconds.Should().BeNull();
        window.Value.RequestClockFitsInTurn.Should().BeTrue();
    }

    [Fact]
    public void A_non_positive_request_timeout_uses_the_one_second_floor()
    {
        var options = new AssistantOptions
        {
            MaxTurnElapsedMilliseconds = 90_000,
            RequestTimeoutSeconds = 0,
        };

        var window = Clocks(options).Open(Usage(1_000));

        window.Should().NotBeNull();
        window!.Value.RequestTimeoutMilliseconds.Should().Be(1_000);
        window.Value.RequestClockFitsInTurn.Should().BeTrue();
    }
}
