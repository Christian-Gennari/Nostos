using Nostos.Backend.Services.Library.Imports;

namespace Nostos.Backend.Services.Calibre;

/// <summary>
/// Expires abandoned Calibre library uploads (#1572): a partial zip nobody is
/// resuming, or a staged library nobody imported. The owner does not have to
/// act; choosing the zip again starts cleanly. The same pass removes a session
/// directory whose manifest is gone (a crash between mkdir and manifest write).
///
/// This is scratch, not library data, so it does not take the library
/// maintenance lease: a migration activation never waits on it and it can
/// never touch the live database.
/// </summary>
public class CalibreUploadCleanupService(
    ICalibreUploadStore store,
    ILogger<CalibreUploadCleanupService> logger) : BackgroundService
{
    protected virtual TimeSpan ScanInterval => TimeSpan.FromMinutes(30);

    internal async Task<int> ScanOnceAsync(CancellationToken ct)
    {
        try
        {
            var removed = await store.ExpireAsync(DateTime.UtcNow, ct);
            if (removed > 0)
            {
                logger.LogInformation(
                    "Calibre upload cleanup removed {Removed} expired scratch session(s).",
                    removed);
            }

            return removed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Scratch cleanup must never stop the host; the next pass retries.
            logger.LogWarning(
                "Calibre upload cleanup failed; the next pass retries. Exception type {ExceptionType}.",
                exception.GetType().Name);
            return 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ScanOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(ScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await ScanOnceAsync(stoppingToken);
    }
}
