using System.Diagnostics;
using Nostos.Backend.Services.Ai;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Content-free measurements for one assistant turn. These values describe
/// execution shape only; prompts, replies, tool arguments and tool results are
/// deliberately absent.
/// </summary>
public sealed record AssistantExecutionMetrics(
    int UpstreamCallCount,
    int ToolCallCount,
    int ToolLoopIterations,
    int? PromptTokens,
    int? OutputTokens,
    int? ThinkingTokens,
    int? ReportedTotalTokens,
    long ElapsedMilliseconds,
    AssistantTurnStopReason StopReason,
    string? ProviderFinishReason);

public enum AssistantTurnStopReason
{
    Completed,
    UserInputRequired,
    ApprovalRequired,
    RepeatedToolLoop,
    SafetyCeiling,
    ProviderError,
    Cancelled,
}

/// <summary>
/// Mid-turn snapshot of <see cref="AssistantExecutionMeter"/>. The per-turn
/// execution ceilings are evaluated against this before another upstream call is
/// spent, so the same accounting that is logged at the end of the turn also
/// guards it while it runs.
/// </summary>
public readonly record struct AssistantExecutionUsage(
    int UpstreamCallCount,
    int? PromptTokens,
    int? OutputTokens,
    int? ThinkingTokens,
    int? ReportedTotalTokens,
    long ElapsedMilliseconds);

/// <summary>
/// Accumulates one turn's provider-reported usage without inventing missing
/// values. If any completion omits a token dimension, that aggregate remains
/// unknown rather than silently under-counting it.
/// </summary>
public sealed class AssistantExecutionMeter
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private int _upstreamCalls;
    private int _completedCalls;
    private int _toolCalls;
    private int _toolLoopIterations;
    private int _promptTokens;
    private int _outputTokens;
    private int _thinkingTokens;
    private bool _promptTokensComplete = true;
    private bool _outputTokensComplete = true;
    private bool _thinkingTokensComplete = true;
    private string? _providerFinishReason;

    public void RecordUpstreamRequest() => _upstreamCalls++;

    public void RecordCompletion(LlmCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        _completedCalls++;
        _toolCalls += completion.ToolCalls.Count;
        if (completion.ToolCalls.Count > 0)
        {
            _toolLoopIterations++;
        }

        _providerFinishReason = completion.FinishReason;
        Accumulate(completion.PromptTokens, ref _promptTokens, ref _promptTokensComplete);
        Accumulate(completion.CompletionTokens, ref _outputTokens, ref _outputTokensComplete);
        Accumulate(completion.ThinkingTokens, ref _thinkingTokens, ref _thinkingTokensComplete);
    }

    /// <summary>
    /// Current cumulative usage. Token dimensions stay unknown while any completion
    /// omitted them, mirroring <see cref="Finish"/>; the clock keeps running.
    /// </summary>
    public AssistantExecutionUsage Snapshot()
    {
        var allRequestsCompleted = _completedCalls == _upstreamCalls;
        int? promptTokens = allRequestsCompleted && _promptTokensComplete ? _promptTokens : null;
        int? outputTokens = allRequestsCompleted && _outputTokensComplete ? _outputTokens : null;
        int? thinkingTokens = allRequestsCompleted && _thinkingTokensComplete ? _thinkingTokens : null;

        return new AssistantExecutionUsage(
            _upstreamCalls,
            promptTokens,
            outputTokens,
            thinkingTokens,
            promptTokens is not null && outputTokens is not null
                ? checked(promptTokens.Value + outputTokens.Value)
                : null,
            (long)_clock.Elapsed.TotalMilliseconds);
    }

    public AssistantExecutionMetrics Finish(AssistantTurnStopReason stopReason)
    {
        _clock.Stop();
        var usage = Snapshot();

        return new AssistantExecutionMetrics(
            usage.UpstreamCallCount,
            _toolCalls,
            _toolLoopIterations,
            usage.PromptTokens,
            usage.OutputTokens,
            usage.ThinkingTokens,
            usage.ReportedTotalTokens,
            usage.ElapsedMilliseconds,
            stopReason,
            _providerFinishReason);
    }

    private static void Accumulate(int? value, ref int total, ref bool complete)
    {
        if (value is null)
        {
            complete = false;
            return;
        }

        total = checked(total + value.Value);
    }
}
