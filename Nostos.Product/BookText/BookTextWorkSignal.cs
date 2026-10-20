namespace Nostos.Product.BookText;

/// <summary>
/// Wakes an idle in-process book-text worker when a book is scheduled for
/// extraction, so the worker need not poll the queue every few seconds. Its
/// slow fallback scan still picks up stale claims and missed signals.
/// </summary>
public sealed class BookTextWorkSignal
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
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero)
            return;

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, timer.Token);
        var signalled = _signal.WaitAsync(timer.Token);
        await Task.WhenAny(delay, signalled).ConfigureAwait(false);
        timer.Cancel();
        ct.ThrowIfCancellationRequested();
    }
}
