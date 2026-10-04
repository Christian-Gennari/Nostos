namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Direction-aware gate for migration job state mutations. The frozen
/// transition matrix itself lives in <see cref="MigrationJobTransitions"/>;
/// this type is only the store-facing seam that turns an illegal transition or
/// control operation into the typed <see cref="MigrationJobStoreException"/>.
/// It is the single place the job store consults, so import activation rules
/// (for example, import <c>Validating -&gt; Completed</c> is illegal) cannot be
/// bypassed by one code path.
/// </summary>
public static class MigrationJobStateMachine
{
    public static bool IsTerminal(MigrationJobState state) =>
        MigrationJobTransitions.IsTerminal(state);

    public static bool IsRetryable(MigrationJobState state) =>
        MigrationJobTransitions.IsRetryable(state);

    /// <summary>
    /// Cancellation is allowed from every active pre-activation state.
    /// <see cref="MigrationJobState.Activating"/> is the point of no return;
    /// terminal states keep their history and are rejected as invalid state.
    /// </summary>
    public static bool CanCancel(MigrationJobState state) =>
        state is MigrationJobState.Pending
            or MigrationJobState.Preparing
            or MigrationJobState.Transferring
            or MigrationJobState.Validating
            or MigrationJobState.ReadyToActivate;

    /// <summary>
    /// Validates one transition for the job's direction and throws the store's
    /// typed invalid-state failure when the frozen table does not allow it.
    /// </summary>
    public static void EnsureTransitionAllowed(
        Guid jobId,
        MigrationDirection direction,
        MigrationJobState current,
        MigrationJobState target)
    {
        if (MigrationJobTransitions.CanTransition(direction, current, target))
            return;

        throw MigrationJobStoreException.InvalidState(
            jobId,
            $"Migration job {jobId} cannot transition from {current} to {target} " +
            $"as an {direction} job.");
    }
}
