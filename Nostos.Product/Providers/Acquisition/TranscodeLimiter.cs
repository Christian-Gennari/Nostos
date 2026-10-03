using Microsoft.Extensions.Options;

namespace Nostos.Backend.Providers.Acquisition;

/// <summary>
/// Caps how many media transcodes run at once, across the whole process.
///
/// A singleton because the resource it protects is the machine, not the
/// request: the acquisition service is scoped, so a per-instance semaphore would
/// let two imports encode at the same time and make the self-hosted box
/// unresponsive.
/// </summary>
public interface ITranscodeLimiter
{
    Task<IDisposable> AcquireAsync(CancellationToken ct);
}

public sealed class TranscodeLimiter : ITranscodeLimiter
{
    private readonly SemaphoreSlim _gate;
    private readonly ILogger<TranscodeLimiter> _logger;

    public TranscodeLimiter(IOptions<AcquisitionOptions> options, ILogger<TranscodeLimiter> logger)
    {
        var slots = options.Value.ClampTranscodeConcurrency();
        _gate = new SemaphoreSlim(slots, slots);
        _logger = logger;
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        if (_gate.CurrentCount == 0)
            _logger.LogInformation("Waiting for a media transcode slot; another acquisition is encoding.");

        await _gate.WaitAsync(ct);
        return new Slot(_gate, _logger);
    }

    private sealed class Slot(SemaphoreSlim gate, ILogger logger) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Guarded so a double-dispose cannot release a slot twice and let an
            // unbounded number of transcodes through.
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            try
            {
                gate.Release();
            }
            catch (ObjectDisposedException)
            {
                logger.LogDebug("Transcode limiter disposed before a slot was released.");
            }
        }
    }
}
