using System.Collections.Concurrent;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>Prompt same-host cancellation. Database guards remain authoritative across hosts and restarts.</summary>
public sealed class MigrationJobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    internal IDisposable Register(Guid id, CancellationTokenSource source)
    {
        if (!_running.TryAdd(id, source)) throw new InvalidOperationException("Job already running in this host.");
        return new Registration(_running, id, source);
    }
    public void Cancel(Guid id)
    {
        if (_running.TryGetValue(id, out var source))
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
    private sealed class Registration(ConcurrentDictionary<Guid, CancellationTokenSource> running,
        Guid id, CancellationTokenSource source) : IDisposable
    {
        public void Dispose() => running.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(id, source));
    }
}
