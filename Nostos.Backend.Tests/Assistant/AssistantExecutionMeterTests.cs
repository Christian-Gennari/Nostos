using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

public sealed class AssistantExecutionMeterTests
{
    [Fact]
    public void Aggregates_content_free_execution_shape_and_usage()
    {
        var meter = new AssistantExecutionMeter();

        meter.RecordUpstreamRequest();
        meter.RecordCompletion(new LlmCompletion(
            null,
            "tool_calls",
            [new LlmToolCall("one", "topics_list", "{}")],
            PromptTokens: 120,
            CompletionTokens: 30,
            ThinkingTokens: 10));

        meter.RecordUpstreamRequest();
        meter.RecordCompletion(new LlmCompletion(
            "Done.",
            "stop",
            [],
            PromptTokens: 150,
            CompletionTokens: 20,
            ThinkingTokens: 5));

        var result = meter.Finish(AssistantTurnStopReason.Completed);

        result.UpstreamCallCount.Should().Be(2);
        result.ToolCallCount.Should().Be(1);
        result.ToolLoopIterations.Should().Be(1);
        result.PromptTokens.Should().Be(270);
        result.OutputTokens.Should().Be(50);
        result.ThinkingTokens.Should().Be(15);
        result.ReportedTotalTokens.Should().Be(320);
        result.StopReason.Should().Be(AssistantTurnStopReason.Completed);
        result.ProviderFinishReason.Should().Be("stop");
        result.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Missing_provider_usage_stays_unknown_instead_of_under_counting()
    {
        var meter = new AssistantExecutionMeter();

        meter.RecordUpstreamRequest();
        meter.RecordCompletion(new LlmCompletion(
            null,
            "tool_calls",
            [new LlmToolCall("one", "topics_list", "{}")],
            PromptTokens: 100,
            CompletionTokens: 25));

        meter.RecordUpstreamRequest();
        meter.RecordCompletion(new LlmCompletion(
            "Done.",
            "stop",
            [],
            PromptTokens: null,
            CompletionTokens: 20));

        var result = meter.Finish(AssistantTurnStopReason.Completed);

        result.PromptTokens.Should().BeNull();
        result.OutputTokens.Should().Be(45);
        result.ThinkingTokens.Should().BeNull();
        result.ReportedTotalTokens.Should().BeNull();
    }

    [Fact]
    public void Attempt_without_a_completion_has_unknown_token_usage()
    {
        var meter = new AssistantExecutionMeter();

        meter.RecordUpstreamRequest();

        var result = meter.Finish(AssistantTurnStopReason.ProviderError);

        result.UpstreamCallCount.Should().Be(1);
        result.ToolCallCount.Should().Be(0);
        result.PromptTokens.Should().BeNull();
        result.OutputTokens.Should().BeNull();
        result.ThinkingTokens.Should().BeNull();
        result.ReportedTotalTokens.Should().BeNull();
        result.StopReason.Should().Be(AssistantTurnStopReason.ProviderError);
    }
}
