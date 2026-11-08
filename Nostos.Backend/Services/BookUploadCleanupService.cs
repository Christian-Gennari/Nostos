using Nostos.Backend.Services.BookUpload;

namespace Nostos.Backend.Services;

/// <summary>
/// Expires abandoned resumable book uploads (#1266) and retires finished
/// session rows, deleting their staging bytes. A session that stops receiving
/// parts is not an error the owner must act on: the book row is unchanged and
/// picking the file again starts a fresh session.
/// </summary>
public class BookUploadCleanupService(
    IServiceScopeFactory scopes,
    ILogger<BookUploadCleanupService> logger,
    ILibraryMaintenanceCoordinator? maintenance = null) : BackgroundService
{
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromHours(24);

    // Virtual so tests can drive the loop with a short interval; the
    // production cleanup runs every thirty minutes.
    protected virtual TimeSpan ScanInterval => TimeSpan.FromMinutes(30);

    /// <summary>
    /// Runs one scoped cleanup pass. The pass takes a shared library lease
    /// before touching the database or staging volume, so a backup restore or
    /// migration activation that owns the library exclusively never sees it
    /// write; it waits outside the library and runs when that window closes.
    /// </summary>
    internal async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        await using var operation = maintenance is null
            ? null
            : await maintenance.EnterOperationAsync(cancellationToken);

        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<BookUploadService>();
        var now = DateTime.UtcNow;
        var expired = await service.ExpireStaleAsync(now, cancellationToken);
        var purged = await service.PurgeTerminalAsync(
            now - TerminalRetention,
            cancellationToken);
        if (expired > 0 || purged > 0)
        {
            logger.LogInformation(
                "Book upload cleanup expired {Expired} session(s) and retired {Purged} finished session(s).",
                expired,
                purged);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ScanOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A cleanup failure must never stop the host; the next pass
                // retries the same rows.
                logger.LogWarning(
                    exception,
                    "Book upload cleanup pass failed; the next pass retries.");
            }
        }
    }
}
