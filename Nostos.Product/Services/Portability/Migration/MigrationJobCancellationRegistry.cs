using System.Collections.Concurrent;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>Prompt same-host cancellation. Database guards remain authoritative across hosts and restarts.</summary>
public sealed class MigrationJobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, RunningOwner> _running = new();
    private readonly object _uploadGate = new();
    private readonly Dictionary<Guid, HashSet<CancellationTokenSource>> _uploads = new();
    private readonly Dictionary<Guid, int> _cancellingUploads = new();

    internal IDisposable RegisterUpload(Guid id, CancellationTokenSource source)
    {
        bool cancelling;
        lock (_uploadGate)
        {
            if (!_uploads.TryGetValue(id, out var requests)) _uploads[id] = requests = new();
            requests.Add(source);
            cancelling = _cancellingUploads.ContainsKey(id);
        }
        if (cancelling) source.Cancel();
        return new UploadRegistration(this, id, source);
    }

    // Stop same-host request reads before asking the DB to lock cancellation;
    // a slow request must not indefinitely block the cancel control operation.
    internal IDisposable BeginUploadCancellation(Guid id)
    {
        CancellationTokenSource[] requests;
        lock (_uploadGate)
        {
            _cancellingUploads[id] = _cancellingUploads.GetValueOrDefault(id) + 1;
            requests = _uploads.TryGetValue(id, out var current) ? current.ToArray() : [];
        }
        foreach (var source in requests)
        { try { source.Cancel(); } catch (ObjectDisposedException) { } }
        return new CancellationIntent(this, id);
    }

    private sealed class CancellationIntent(MigrationJobCancellationRegistry registry, Guid id) : IDisposable
    {
        public void Dispose()
        {
            lock (registry._uploadGate)
            {
                if (registry._cancellingUploads[id] == 1) registry._cancellingUploads.Remove(id);
                else registry._cancellingUploads[id]--;
            }
        }
    }

    private sealed class UploadRegistration(MigrationJobCancellationRegistry registry, Guid id, CancellationTokenSource source) : IDisposable
    {
        public void Dispose()
        {
            lock (registry._uploadGate)
            {
                if (!registry._uploads.TryGetValue(id, out var requests)) return;
                requests.Remove(source);
                if (requests.Count == 0) registry._uploads.Remove(id);
            }
        }
    }

    // Registration follows successful DB acquisition. Constant worker lease
    // durations and the shared host clock make a successor's acquisition expiry
    // strictly newer. A delayed old registration must not cancel that successor.
    internal IDisposable Register(Guid id, CancellationTokenSource source, DateTimeOffset acquisitionExpiry)
    {
        var owner = new RunningOwner(source, acquisitionExpiry);
        while (true)
        {
            if (_running.TryGetValue(id, out var previous))
            {
                if (previous.AcquisitionExpiry >= acquisitionExpiry)
                { source.Cancel(); return new Registration(_running, id, owner); }
                if (!_running.TryUpdate(id, owner, previous)) continue;
                try { previous.Source.Cancel(); } catch (ObjectDisposedException) { }
                return new Registration(_running, id, owner);
            }
            if (_running.TryAdd(id, owner)) return new Registration(_running, id, owner);
        }
    }
    public void Cancel(Guid id)
    {
        if (_running.TryGetValue(id, out var owner))
        {
            try { owner.Source.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
    private sealed class RunningOwner(CancellationTokenSource source, DateTimeOffset acquisitionExpiry)
    {
        internal CancellationTokenSource Source { get; } = source;
        internal DateTimeOffset AcquisitionExpiry { get; } = acquisitionExpiry;
    }
    private sealed class Registration(ConcurrentDictionary<Guid, RunningOwner> running,
        Guid id, RunningOwner owner) : IDisposable
    {
        public void Dispose() => running.TryRemove(new KeyValuePair<Guid, RunningOwner>(id, owner));
    }
}
