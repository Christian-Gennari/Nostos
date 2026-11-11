using Nostos.Backend.Configuration;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Per-turn execution ceilings (#406): one runaway or overlong turn is stopped
/// before it spends another upstream call.
///
/// The values live in <see cref="AssistantOptions"/> and bound one interactive
/// turn independently of any host-level rate or usage policy. The same class
/// owns the ordering contract between the two wall clocks (#1561):
///
/// <list type="number">
/// <item>One provider request is stopped by <see cref="AssistantOptions.RequestTimeoutSeconds"/>
/// (the inner clock) and reports <c>assistant_provider_timeout</c>.</item>
/// <item>The whole turn is stopped by <see cref="AssistantOptions.MaxTurnElapsedMilliseconds"/>
/// (the outer clock) and reports <c>assistant_execution_budget_exhausted</c>.</item>
/// </list>
///
/// <see cref="ValidateClocks"/> rejects a configuration where the inner clock
/// cannot fire before the outer one with room left for one tool round.
/// </summary>
public static class AssistantExecutionBudget
{
    /// <summary>
    /// The minimum gap between the per-request timeout and the whole-turn budget.
    /// It is the room one tool round needs after a request's own timeout fires, so
    /// a stalled provider is reported as a provider timeout instead of being
    /// hidden behind the turn's "execution limit" message.
    /// </summary>
    public const int MinimumToolRoundMarginMilliseconds = 15_000;

    /// <summary>
    /// Returns the name of the first ceiling the snapshot has reached, or null when
    /// the turn may spend another upstream call. A ceiling configured as zero or
    /// below is disabled.
    ///
    /// Unknown token dimensions cannot trip the token ceiling: a provider that
    /// omitted a usage field must not be read as either "safe" or "over budget",
    /// and the hard iteration ceiling still bounds the turn.
    /// </summary>
    public static string? ExceededCeiling(AssistantExecutionUsage usage, AssistantOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxTurnTokens > 0 &&
            usage.ReportedTotalTokens is { } tokens &&
            tokens >= options.MaxTurnTokens)
        {
            return "cumulative-token";
        }

        if (options.MaxTurnElapsedMilliseconds > 0 &&
            usage.ElapsedMilliseconds >= options.MaxTurnElapsedMilliseconds)
        {
            return "wall-clock";
        }

        return null;
    }

    /// <summary>
    /// The per-provider-request ceiling in milliseconds, using the same one-second
    /// floor as <see cref="AssistantOptions.RequestTimeout"/> so both clocks agree.
    /// </summary>
    public static long RequestTimeoutMilliseconds(AssistantOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (long)options.RequestTimeout.TotalMilliseconds;
    }

    /// <summary>
    /// Enforces the clock order at startup. The per-request clock must be positive
    /// and, while the turn ceiling is enabled, sit at least
    /// <see cref="MinimumToolRoundMarginMilliseconds"/> below the turn budget.
    ///
    /// Without that gap a stalled request is cancelled by the turn deadline first
    /// and the person sees "This turn reached its execution limit" even though no
    /// execution limit was reached — the 95 s request timeout against the 90 s turn
    /// budget shipped before #1561 was exactly that.
    /// </summary>
    public static void ValidateClocks(AssistantOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.RequestTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"Assistant:RequestTimeoutSeconds must be greater than zero " +
                $"(currently {options.RequestTimeoutSeconds}). It is the per-provider-request " +
                "clock; without a positive value its order against the whole-turn budget " +
                "cannot be guaranteed.");
        }

        // A disabled turn ceiling has nothing to order the request clock against.
        if (options.MaxTurnElapsedMilliseconds <= 0)
            return;

        var requestMilliseconds = (long)options.RequestTimeoutSeconds * 1000;
        var minimumTurnMilliseconds =
            requestMilliseconds + MinimumToolRoundMarginMilliseconds;

        if (options.MaxTurnElapsedMilliseconds >= minimumTurnMilliseconds)
            return;

        var maximumRequestSeconds = Math.Max(
            0,
            (options.MaxTurnElapsedMilliseconds - MinimumToolRoundMarginMilliseconds) / 1000);

        throw new InvalidOperationException(
            $"Assistant:RequestTimeoutSeconds ({options.RequestTimeoutSeconds} s) must sit at least " +
            $"{MinimumToolRoundMarginMilliseconds / 1000} s below " +
            $"Assistant:MaxTurnElapsedMilliseconds ({options.MaxTurnElapsedMilliseconds} ms), so one " +
            "provider request can time out and still leave room for one tool round. " +
            $"Set Assistant:RequestTimeoutSeconds to {maximumRequestSeconds} s or less, raise the turn " +
            $"budget to at least {minimumTurnMilliseconds} ms, or set " +
            "Assistant:MaxTurnElapsedMilliseconds to 0 to disable the whole-turn ceiling.");
    }
}
