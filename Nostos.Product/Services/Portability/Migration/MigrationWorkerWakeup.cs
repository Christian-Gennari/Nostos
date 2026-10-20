namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Wakes the idle <see cref="MigrationJobWorker"/> when work may have appeared.
///
/// Every transition that makes a job runnable (a job created, an upload
/// session completed, a retry or a resume) arrives through the migration HTTP
/// endpoints, which signal here after each write. The worker therefore waits on
/// this signal instead of scanning the job tables every few seconds; its slow
/// fallback scan still recovers expired leases and anything signalled while it
/// was busy.
/// </summary>
public sealed class MigrationWorkerWakeup
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake-up is enough.
        }
    }

    /// <summary>Returns when signalled or after <paramref name="timeout"/>.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider clock, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, clock, timer.Token);
        var signalled = _signal.WaitAsync(timer.Token);
        await Task.WhenAny(delay, signalled).ConfigureAwait(false);
        timer.Cancel();
        ct.ThrowIfCancellationRequested();
    }
}
