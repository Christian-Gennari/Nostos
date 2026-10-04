using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;

namespace Nostos.Backend.Workers;

public class BackupWorker(
    IServiceScopeFactory scopeFactory,
    BackupSettingsProvider settingsProvider,
    ILogger<BackupWorker> logger
) : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Backup Worker started. Polling every {Interval} minutes.", (int)PollingInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await DoWorkAsync(stoppingToken);
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
        using (var scope = scopeFactory.CreateScope())
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

        using var workScope = scopeFactory.CreateScope();
        var backupService = workScope.ServiceProvider.GetRequiredService<IBackupService>();

        logger.LogInformation("Running scheduled backup...");
        var result = await backupService.CreateBackupAsync(ct);

        if (result.Status == "Completed")
            logger.LogInformation("Scheduled backup completed: {Id} ({Size} bytes)", result.Id, result.SizeBytes);
        else
            logger.LogWarning("Scheduled backup {Id} finished with status: {Status}", result.Id, result.Status);
    }
}
