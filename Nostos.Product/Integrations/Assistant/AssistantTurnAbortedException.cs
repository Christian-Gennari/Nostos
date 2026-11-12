using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// A provider failure that ended a turn after canonical writes had already
/// committed (#1558). A capture, or an Act capability, can finish and a later
/// provider call in the same turn still fail; the failure contract alone would
/// then say nothing about the note that exists, and the panel would drop the
/// turn's artifacts.
///
/// <see cref="Committed"/> is the same response shape a completed turn returns,
/// carrying only what actually committed (the note id and acknowledgement, the
/// capabilities that ran, the evidence the turn read). It is never a plan:
/// staged changes are not applied and are not reported. A retried delivery
/// reuses the TurnId, so the canonical receipts replay instead of writing
/// again. The exception is only thrown when there is something to report —
/// an ordinary failure with nothing committed stays a plain
/// <see cref="LlmException"/>.
/// </summary>
public sealed class AssistantTurnAbortedException(
    LlmException cause,
    AssistantTurnResponse committed)
    : Exception(cause.Message, cause)
{
    /// <summary>The typed provider failure the endpoint maps to its contract.</summary>
    public LlmException Cause { get; } = cause;

    /// <summary>What this turn committed before the provider call failed.</summary>
    public AssistantTurnResponse Committed { get; } = committed;
}
