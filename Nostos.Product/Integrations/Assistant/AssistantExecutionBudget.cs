using Nostos.Backend.Configuration;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Per-turn execution ceilings (#406): one runaway or overlong turn is stopped
/// before it spends another upstream call.
///
/// The values live in <see cref="AssistantOptions"/> and bound one interactive
/// turn independently of any host-level rate or usage policy.
/// </summary>
public static class AssistantExecutionBudget
{
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
}
