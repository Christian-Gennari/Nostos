using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Workers;

public class BackupWorker(
    IServiceScopeFactory scopeFactory,
    BackupSettingsProvider settingsProvider,
    ILogger<BackupWorker> logger
) : BackgroundService
{
    // Virtual so tests can poll on a clock they control rather than wall time.
    protected virtual TimeSpan PollingInterval => TimeSpan.FromMinutes(5);

    /// <summary>
    /// The clock the poll delay runs on. Overridden in tests so a poll is due
    /// deterministically and no test waits five real minutes.
    /// </summary>
    protected virtual TimeProvider Clock => TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Backup Worker started. Polling every {Interval} minutes.", (int)PollingInterval.TotalMinutes);

        RunStartupStagingCleanup();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollingInterval, Clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await DoWorkAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown in the middle of a backup cycle is not a failure.
                break;
            }
            catch (MigrationActivationException ex) when (ex.Code == MigrationActivationErrorCodes.Busy)
            {
                // Exclusive maintenance (restore or activation) began between the
                // due-check and CreateBackupAsync. That is an expected skip, not a
                // failed backup: the next poll retries once the library is free.
                logger.LogInformation(
                    "Scheduled backup skipped because the library is busy or in maintenance; retrying on the next poll.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred during the scheduled backup cycle.");
            }
        }

        logger.LogInformation("Backup Worker is shutting down.");
    }

    private async Task DoWorkAsync(CancellationToken ct)
    {
        var settings = settingsProvider.Current;
        if (!settings.IsEnabled)
            return;

        DateTime? lastBackupTime;
        await using (var operation = await settingsProvider.Maintenance.EnterOperationAsync(ct))
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            lastBackupTime = await db.BackupRecords
                .Where(b => b.Status == BackupStatus.Completed)
                .OrderByDescending(b => b.CreatedAt)
                .Select(b => (DateTime?)b.CreatedAt)
                .FirstOrDefaultAsync(ct);
        }

        if (lastBackupTime.HasValue)
        {
            var interval = TimeSpan.FromHours(Math.Max(1, settings.IntervalHours));
            var elapsed = DateTime.UtcNow - lastBackupTime.Value;
            if (elapsed < interval)
                return;
        }

        await using var workScope = scopeFactory.CreateAsyncScope();
        var backupService = workScope.ServiceProvider.GetRequiredService<IBackupService>();

        logger.LogInformation("Running scheduled backup...");
        var result = await backupService.CreateBackupAsync(ct);

        if (result.Status == "Completed")
            logger.LogInformation("Scheduled backup completed: {Id} ({Size} bytes)", result.Id, result.SizeBytes);
        else
            logger.LogWarning("Scheduled backup {Id} finished with status: {Status}", result.Id, result.Status);
    }

    internal void RunStartupStagingCleanup()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<IBackupService>() is BackupService backupService)
            {
                backupService.CleanupOrphanedStaging();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Backup startup staging cleanup failed; continuing.");
        }
    }
}
