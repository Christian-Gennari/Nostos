using System.Collections.Concurrent;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>The kind of library switch a queued dispatcher work item runs.</summary>
internal enum SelfHostedLibrarySwitchRunKind
{
    Activation,
    RecoveryRestore,
}

/// <summary>One queued library-switch run: exactly one of activation or restore.</summary>
internal readonly record struct SelfHostedLibrarySwitchWorkItem(
    Guid Key,
    SelfHostedLibrarySwitchRunKind Kind)
{
    internal static SelfHostedLibrarySwitchWorkItem ForActivation(Guid jobId) =>
        new(jobId, SelfHostedLibrarySwitchRunKind.Activation);

    internal static SelfHostedLibrarySwitchWorkItem ForRestore(Guid recoveryId) =>
        new(recoveryId, SelfHostedLibrarySwitchRunKind.RecoveryRestore);
}

/// <summary>
/// Shared lifecycle core for one library-switch run (activation or recovery
/// restore). <see cref="SelfHostedActivationRunState"/> is the single run state
/// machine; the name predates the restore convergence. Every lifecycle
/// transition is a compare-and-swap so concurrent admission and worker
/// completion can never both claim the same run: exactly one accepted run
/// exists and its terminal outcome is retained for a bounded time.
/// </summary>
internal abstract class SelfHostedLibrarySwitchRunSlot(Guid key)
{
    private int _state;
    private TaskCompletionSource _finished = NewSignal();

    internal Guid Key { get; } = key;

    internal SelfHostedActivationRunState State =>
        (SelfHostedActivationRunState)Volatile.Read(ref _state);

    internal DateTimeOffset FinishedAtUtc { get; private set; }

    /// <summary>Completes when the current run reaches a terminal dispatcher state (test seam).</summary>
    internal Task Finished => Volatile.Read(ref _finished).Task;

    /// <summary>Claims the run unless one is already accepted/running.</summary>
    protected bool TryAdmitCore()
    {
        while (true)
        {
            var current = State;
            if (current is SelfHostedActivationRunState.Accepted or SelfHostedActivationRunState.Running)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)SelfHostedActivationRunState.Accepted,
                    (int)current) == (int)current)
            {
                Volatile.Write(ref _finished, NewSignal());
                return true;
            }
        }
    }

    protected bool TryMarkRunningCore() =>
        Interlocked.CompareExchange(
            ref _state,
            (int)SelfHostedActivationRunState.Running,
            (int)SelfHostedActivationRunState.Accepted) == (int)SelfHostedActivationRunState.Accepted;

    protected void MarkFinishedCore(DateTimeOffset now)
    {
        FinishedAtUtc = now;
        Volatile.Write(ref _state, (int)SelfHostedActivationRunState.Finished);
        Volatile.Read(ref _finished).TrySetResult();
    }

    protected void MarkDroppedCore(DateTimeOffset now)
    {
        FinishedAtUtc = now;
        Volatile.Write(ref _state, (int)SelfHostedActivationRunState.Finished);
        Volatile.Read(ref _finished).TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Bounded per-key run registry shared by activation and recovery restore.
/// Finished slots are retained for a TTL so a late duplicate request can replay
/// the outcome instead of starting a second run; they are pruned afterwards, at
/// which point durable state is the only truth.
/// </summary>
internal class SelfHostedLibrarySwitchRunRegistry<TSlot>(Func<Guid, TSlot> factory)
    where TSlot : SelfHostedLibrarySwitchRunSlot
{
    private readonly ConcurrentDictionary<Guid, TSlot> _slots = new();

    internal TSlot GetOrAdd(Guid key) => _slots.GetOrAdd(key, factory);

    internal TSlot? Find(Guid key) => _slots.TryGetValue(key, out var slot) ? slot : null;

    internal void Prune(DateTimeOffset now, TimeSpan ttl)
    {
        foreach (var pair in _slots)
        {
            if (pair.Value.State == SelfHostedActivationRunState.Finished
                && pair.Value.FinishedAtUtc < now - ttl)
            {
                _slots.TryRemove(pair.Key, out _);
            }
        }
    }
}
