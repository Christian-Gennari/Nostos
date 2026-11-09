using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.BookUpload;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services;

/// <summary>
/// Expires abandoned resumable book uploads (#1266) and retires finished
/// session rows, deleting their staging bytes. A session that stops receiving
/// parts is not an error the owner must act on: the book row is unchanged and
/// picking the file again starts a fresh session. A staging folder whose
/// session row is gone (a backup restore brought back an older database, or a
/// crash interrupted a purge) is removed once it has been idle longer than a
/// session can live.
/// </summary>
public class BookUploadCleanupService(
    IServiceScopeFactory scopes,
    ILogger<BookUploadCleanupService> logger,
    ILibraryMaintenanceCoordinator? maintenance = null) : BackgroundService
{
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromHours(24);

    // No session lives longer than this, so a folder idle for longer and with
    // no row can never be resumed.
    private static readonly TimeSpan OrphanGrace = BookUploadLimits.SessionLifetime;

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
        var orphans = await RemoveOrphanStagingAsync(scope.ServiceProvider, now, cancellationToken);
        if (expired > 0 || purged > 0 || orphans > 0)
        {
            logger.LogInformation(
                "Book upload cleanup expired {Expired} session(s), retired {Purged} finished session(s) and removed {Orphans} orphaned staging folder(s).",
                expired,
                purged,
                orphans);
        }
    }

    private static async Task<int> RemoveOrphanStagingAsync(
        IServiceProvider services,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var paths = services.GetRequiredService<TransferPathResolver>();
        var root = paths.VerifyPathWithinRoot(paths.GetBookUploadsRoot());
        if (!Directory.Exists(root)) return 0;

        var idle = new List<Guid>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || id == Guid.Empty)
                continue;
            try
            {
                var verified = paths.VerifyPathWithinRoot(directory);
                if (TransferPathResolver.NewestWriteTimeUtc(verified) <= nowUtc - OrphanGrace)
                    idle.Add(id);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or TransferPathException)
            {
                // Unreadable or linked folders are left alone; the next pass looks again.
            }
        }

        if (idle.Count == 0) return 0;

        var db = services.GetRequiredService<NostosDbContext>();
        var owned = await db.BookUploadSessionRecords.AsNoTracking()
            .Where(s => idle.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        var store = services.GetRequiredService<IBookFileUploadStore>();
        var removed = 0;
        foreach (var id in idle.Except(owned))
        {
            await store.DiscardAsync(id, cancellationToken);
            removed++;
        }

        return removed;
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
