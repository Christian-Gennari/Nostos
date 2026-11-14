using Nostos.Backend.Services;

namespace Nostos.Backend.Tests.Support;

/// <summary>
/// Delegates to a real <see cref="LibraryMaintenanceCoordinator"/> and records
/// every background pass that asks for a shared library lease.
///
/// That question is the observable point where a worker has actually reached
/// the library, so a test can wait for it instead of sleeping "long enough" and
/// hoping the pass got there. A pass that is still waiting for the lease has
/// recorded its attempt but not returned, which is exactly the state the
/// maintenance-window tests need to assert.
/// </summary>
internal sealed class SignallingMaintenanceCoordinator(ILibraryMaintenanceCoordinator inner)
    : ILibraryMaintenanceCoordinator
{
    private readonly object _sync = new();
    private int _attempts;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Passes that have asked for a lease, granted or still waiting.</summary>
    public int Attempts
    {
        get
        {
            lock (_sync) return _attempts;
        }
    }

    public bool IsMaintenanceActive => inner.IsMaintenanceActive;

    public ILibraryOperationLease? TryEnterOperation() => inner.TryEnterOperation();

    public async ValueTask<ILibraryOperationLease> EnterOperationAsync(CancellationToken ct = default)
    {
        Record();
        return await inner.EnterOperationAsync(ct);
    }

    public Task<IAsyncDisposable> EnterExclusiveAsync(
        LibraryMaintenanceReason reason,
        CancellationToken ct = default) =>
        inner.EnterExclusiveAsync(reason, ct);

    /// <summary>
    /// Waits until at least <paramref name="count"/> passes have asked for the
    /// library. The wait is signalled, never slept, so a loaded machine only
    /// makes it slower to observe, not random.
    /// </summary>
    public async Task WaitForAttemptAsync(int count)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (_attempts >= count) return;
                wait = _changed.Task;
            }

            await wait.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private void Record()
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            _attempts++;
            signal = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
    }
}
