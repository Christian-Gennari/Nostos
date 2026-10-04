using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Cooperating hosts must share the transfer filesystem as well as the DB. An
/// exclusive handle serializes authoritative file placement, hashing, detach,
/// and retry across processes; it is independent of database transactions.
/// Persistent lock files must never be deleted (an unlinked inode splits locks).
/// The local semaphore only avoids polling between instances in this process.
/// State/receipt publication still requires a guarded database transaction.
/// </summary>
internal sealed class MigrationFileMutex(TransferPathResolver paths)
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, LocalGate> Gates = new(StringComparer.Ordinal);
    private sealed class LocalGate
    {
        internal readonly SemaphoreSlim Semaphore = new(1);
        internal int Users;
    }

    internal async Task<IAsyncDisposable> EnterAsync(Guid id, CancellationToken ct)
    {
        var path = paths.VerifyPathWithinRoot(paths.GetMigrationLockPath(id));
        LocalGate gate;
        lock (Sync)
        {
            if (!Gates.TryGetValue(path, out gate!)) Gates.Add(path, gate = new());
            gate.Users++;
        }
        var ownsGate = false;
        try
        {
            await gate.Semaphore.WaitAsync(ct);
            ownsGate = true;
            paths.EnsureParentDirectoryExists(path);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                FileStream handle;
                try { handle = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33)
                { await Task.Delay(TimeSpan.FromMilliseconds(25), ct); continue; }
                try { paths.VerifyPathWithinRoot(path); return new Lease(handle, path, gate); }
                catch { await handle.DisposeAsync(); throw; }
            }
        }
        catch { Release(path, gate, ownsGate); throw; }
    }

    private static void Release(string path, LocalGate gate, bool ownsGate)
    {
        if (ownsGate) gate.Semaphore.Release();
        lock (Sync)
            if (--gate.Users == 0)
            { Gates.Remove(path); gate.Semaphore.Dispose(); }
    }

    private sealed class Lease(FileStream handle, string path, LocalGate gate) : IAsyncDisposable
    {
        private FileStream? _handle = handle;
        public async ValueTask DisposeAsync()
        {
            var owned = Interlocked.Exchange(ref _handle, null);
            if (owned is null) return;
            try { await owned.DisposeAsync(); } finally { Release(path, gate, true); }
        }
    }
}
