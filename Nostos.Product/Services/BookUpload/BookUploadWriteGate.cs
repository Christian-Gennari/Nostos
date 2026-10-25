using System.Collections.Concurrent;

namespace Nostos.Backend.Services.BookUpload;

/// <summary>
/// Serializes the short "place the part, commit its receipt" window for one
/// upload session (#1266). The request body is received and hashed outside
/// this gate, so two parts still transfer concurrently; only the write into
/// the shared staging file is ordered.
///
/// Process-local, like <see cref="Nostos.Backend.Providers.Acquisition.BookFileMutationGate"/>:
/// the SelfHosted host is a single process. A hosted multi-instance
/// implementation places the same ordering in its storage provider.
/// </summary>
public sealed class BookUploadWriteGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> EnterAsync(Guid sessionId, CancellationToken ct)
    {
        var semaphore = _locks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return new Lease(semaphore);
    }

    /// <summary>Releases a finished session's semaphore; no waiter can remain.</summary>
    public void Forget(Guid sessionId)
    {
        if (_locks.TryRemove(sessionId, out var semaphore)) semaphore.Dispose();
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
