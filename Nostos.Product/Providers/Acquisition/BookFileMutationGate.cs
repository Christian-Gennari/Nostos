using System.Collections.Concurrent;

namespace Nostos.Backend.Providers.Acquisition;

/// <summary>
/// Serializes provider acquisition and manual source-file writes for a book.
/// A manual upload must *try*, never wait behind a multi-hour transcode and
/// silently overwrite the newly acquired asset after the job completes.
/// Process-local, matching the process-local acquisition jobs.
/// </summary>
public sealed class BookFileMutationGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> EnterAsync(Guid bookId, CancellationToken ct)
    {
        var semaphore = _locks.GetOrAdd(bookId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return new Lease(semaphore);
    }

    public IDisposable? TryEnter(Guid bookId)
    {
        var semaphore = _locks.GetOrAdd(bookId, static _ => new SemaphoreSlim(1, 1));
        return semaphore.Wait(0) ? new Lease(semaphore) : null;
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                semaphore.Release();
        }
    }
}
