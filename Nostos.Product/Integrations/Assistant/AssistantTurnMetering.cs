using Microsoft.Extensions.Logging;
using Nostos.Backend.Services.Ai;
using Nostos.Product.Services.Ai;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// One turn's upstream metering: the host usage lease, the content-free
/// execution metrics, and the failure settlement that reports a provider error
/// after recording what the turn already committed (issue #1598).
///
/// The log category stays the orchestrator's on purpose: a host filters on it,
/// so moving the calls to a collaborator must not move the category. Nothing
/// here reads a prompt, a reply, tool arguments or a private library result.
/// </summary>
internal sealed class AssistantTurnMetering(
    IAiUsageAccountingService usageAccounting,
    ILogger logger)
{
    /// <summary>
    /// A host can reserve usage before the first provider call. SelfHosted uses a
    /// no-op accounting service.
    /// </summary>
    public Task<AiUsageLease?> BeginAsync(CancellationToken ct) =>
        usageAccounting.BeginLlmTurnAsync(ct);

    public Task CompleteAsync(AiUsageLease? lease, AssistantExecutionMetrics metrics) =>
        usageAccounting.CompleteLlmTurnAsync(
            lease,
            new LlmProviderUsage(
                metrics.UpstreamCallCount,
                metrics.PromptTokens,
                metrics.OutputTokens,
                metrics.ThinkingTokens,
                metrics.ReportedTotalTokens,
                metrics.ToolLoopIterations,
                metrics.StopReason.ToString(),
                metrics.ProviderFinishReason),
            CancellationToken.None);

    public void Log(AssistantExecutionMetrics metrics)
    {
        // Content-free by design: this is safe to promote into Cloud usage
        // accounting later without storing prompts, replies, tool arguments or
        // private library results.
        logger.LogDebug(
            "Assistant turn execution: {UpstreamCalls} upstream call(s), {ToolCalls} tool call(s), " +
            "{ToolLoopIterations} tool-loop iteration(s), prompt tokens {PromptTokens}, " +
            "output tokens {OutputTokens}, thinking tokens {ThinkingTokens}, " +
            "reported total tokens {ReportedTotalTokens}, elapsed {ElapsedMilliseconds} ms, " +
            "stop {StopReason}, provider finish {ProviderFinishReason}.",
            metrics.UpstreamCallCount,
            metrics.ToolCallCount,
            metrics.ToolLoopIterations,
            metrics.PromptTokens,
            metrics.OutputTokens,
            metrics.ThinkingTokens,
            metrics.ReportedTotalTokens,
            metrics.ElapsedMilliseconds,
            metrics.StopReason,
            metrics.ProviderFinishReason ?? "(none)");
    }

    /// <summary>
    /// Records a provider failure as a provider-error stop and returns the exception
    /// for the caller to throw. The panel shows plain words; the provider's own
    /// detail (the HTTP status, the gateway's message, an unreachable host) is what
    /// an owner needs to fix a misconfigured endpoint, so it goes to the log and
    /// never carries the key.
    ///
    /// When the turn already committed something, the returned exception wraps
    /// the typed failure together with those outcomes (#1558) so the endpoint can
    /// report them beside the failure instead of dropping them.
    /// </summary>
    public async Task<Exception> FailAsync(
        LlmException exception,
        AssistantExecutionMeter executionMeter,
        AiUsageLease? usageLease,
        AssistantTurnResponse? committed)
    {
        logger.LogWarning(
            "Ask Nostos provider call failed ({Code}): {Detail}",
            exception.Code,
            exception.Message);
        var metrics = executionMeter.Finish(AssistantTurnStopReason.ProviderError);
        Log(metrics);
        await CompleteAsync(usageLease, metrics);
        return committed is null
            ? exception
            : new AssistantTurnAbortedException(exception, committed);
    }
}
