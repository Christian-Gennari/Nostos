using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Services;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Workers;

/// <summary>
/// Startup reconciliation worker for interrupted acquisitions.
/// Finds any books left stranded in Downloading or Transcoding status (which cannot be
/// actively running across a server restart since jobs are kept only in memory),
/// clears their scratch/staging directories under the acquisition working root,
/// and updates their status to Failed ("Import interrupted by server restart.").
/// Does NOT auto-resume anything.
/// </summary>
public sealed class AcquisitionReconciliationWorker(
    IDbContextFactory<NostosDbContext> contextFactory,
    IWebHostEnvironment environment,
    IOptions<FileStorageOptions> storageOptions,
    IOptions<AcquisitionOptions> options,
    ILogger<AcquisitionReconciliationWorker> logger,
    ILibraryMaintenanceCoordinator? maintenance = null) : IHostedService
{
    /// <summary>
    /// The exact StatusMessage written onto a book whose import a restart cut
    /// short.
    ///
    /// A shared constant rather than a literal in two places: the import feed
    /// recognises these rows BY THIS STRING to report them as Failed, and a
    /// reworded literal on one side would silently make interrupted imports
    /// disappear from the UI again.
    /// </summary>
    public const string InterruptedByRestartMessage = AcquisitionJobMessages.InterruptedByRestart;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting acquisition reconciliation...");

        try
        {
            await ReconcileAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // A failure in reconciliation must not crash the entire application startup.
            logger.LogError(ex, "Error occurred during acquisition reconciliation.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await using var operation = maintenance is null ? null : await maintenance.EnterOperationAsync(cancellationToken);
        // 1. Clean up the acquisition working/staging root directory if it exists.
        var localBooksRoot = FileStorageOptions.ResolveBooksRoot(
            environment.ContentRootPath,
            storageOptions.Value);
        var workingRoot = AcquisitionOptions.ResolveWorkingRoot(
            environment.ContentRootPath,
            localBooksRoot,
            options.Value);

        if (Directory.Exists(workingRoot))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(workingRoot))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: true);
                        logger.LogInformation("Cleaned up orphaned acquisition staging directory: {Directory}", dir);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to delete orphaned staging directory: {Directory}", dir);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to enumerate staging directories under {WorkingRoot}", workingRoot);
            }
        }

        // SelfHosted has one local database, so stranded rows can be reconciled directly.
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var strandedBooks = await db.Books
            .Where(b => b.Status == BookStatus.Downloading || b.Status == BookStatus.Transcoding)
            .ToListAsync(cancellationToken);

        if (strandedBooks.Count == 0)
        {
            logger.LogInformation("No stranded book acquisitions found.");
            return;
        }

        logger.LogInformation("Found {Count} stranded book acquisition(s). Reconciling to Failed.", strandedBooks.Count);

        foreach (var book in strandedBooks)
        {
            book.Status = BookStatus.Failed;
            book.StatusMessage = InterruptedByRestartMessage;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Reconciled {Count} stranded book acquisition(s) to Failed.", strandedBooks.Count);
    }
}
