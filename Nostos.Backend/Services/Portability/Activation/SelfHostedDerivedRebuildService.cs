using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Services.BookText;
using Nostos.Product.BookText;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>Durable record that the derived rebuild completed for one committed cutover.</summary>
internal sealed record SelfHostedDerivedRebuildMarker(
    Guid JobId,
    Guid OperationId,
    DateTimeOffset CompletedAtUtc,
    int MarkerVersion = 1);

/// <summary>
/// Post-activation derived rebuild (issue #681, Slice 10). A committed cutover
/// leaves a resolved <c>Committed</c> activation journal; the new active
/// database deliberately carries no derived caches, and any row that could have
/// survived is wiped before every file-backed book is scheduled through the same
/// ingestion scheduler the legacy import path uses. The rebuild runs under a
/// shared operation lease, so it never overlaps the exclusive maintenance
/// window; it writes a durable marker only after the whole pass succeeds, so a
/// restart before that point runs it again. A failure is logged and retried by
/// the next pass and can never roll back or fail the already committed
/// activation. Missing thumbnails are reconstructible lazily by
/// <c>FileStorageService</c> and are not rebuilt here.
/// </summary>
internal sealed class SelfHostedDerivedRebuildService(
    SelfHostedActivationPaths paths,
    SelfHostedActivationJournalStore journals,
    LibraryMaintenanceCoordinator maintenance,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<SelfHostedDerivedRebuildService> logger)
{
    /// <summary>Test seam: throwing here models a process crash after the reset, before the marker.</summary>
    internal Action? AfterResetForTesting { get; set; }

    internal async Task<int> RunPendingAsync(CancellationToken ct)
    {
        var pending = ReadPending();
        if (pending.Count == 0 || maintenance.IsRecoveryRequired)
        {
            return 0;
        }

        await using var operation = await maintenance.EnterOperationAsync(ct);

        // A concurrent pass may have completed between the scan and the lease.
        pending = pending.Where(journal => !HasMarker(journal.JobId, journal.OperationId)).ToArray();
        if (pending.Count == 0)
        {
            return 0;
        }

        await using var scope = scopes.CreateAsyncScope();
        var index = scope.ServiceProvider.GetRequiredService<IBookTextIndex>();
        await index.EnsureSchemaAsync(ct);
        await scope.ServiceProvider.GetRequiredService<IBookTextDerivedReset>().ResetAllAsync(ct);
        AfterResetForTesting?.Invoke();
        await ScheduleAllBooksAsync(scope, ct);

        foreach (var journal in pending)
        {
            WriteMarker(journal.JobId, journal.OperationId);
        }

        logger.LogInformation(
            "Derived rebuild completed for {Count} committed cutover(s); book-text indexing was rescheduled.",
            pending.Count);
        return pending.Count;
    }

    private async Task ScheduleAllBooksAsync(IServiceScope scope, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var books = await db.Books.AsNoTracking()
            .Where(book => book.FileDetails.HasFile
                && book.FileDetails.FileName != null
                && book.FileDetails.FileName != "")
            .OrderBy(book => book.Id)
            .Select(book => new { book.Id, book.FileDetails.FileName })
            .ToListAsync(ct);
        var scheduler = scope.ServiceProvider.GetRequiredService<IBookTextIngestionScheduler>();
        foreach (var book in books)
        {
            ct.ThrowIfCancellationRequested();
            await scheduler.ScheduleAsync(book.Id, book.FileName!, ct);
        }
    }

    /// <summary>
    /// Every resolved <c>Committed</c> journal whose derived rebuild marker is
    /// missing. Resolved <c>RolledBack</c> journals keep the original
    /// generation and its derived state, so they are never rebuilt. A corrupt
    /// resolved journal is skipped: the operator guide owns it, and the derived
    /// rebuild must not guess.
    /// </summary>
    private IReadOnlyList<SelfHostedActivationJournal> ReadPending()
    {
        var root = paths.JournalRoot;
        if (!Directory.Exists(root))
        {
            return [];
        }

        paths.VerifyDatabasePath(root);
        var pending = new List<SelfHostedActivationJournal>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            paths.VerifyDatabasePath(directory);
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out var jobId)
                || jobId == Guid.Empty
                || !string.Equals(name, jobId.ToString("N"), StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var journal = journals.ReadResolved(jobId);
                if (journal is { Phase: SelfHostedActivationPhase.Committed }
                    && !HasMarker(journal.JobId, journal.OperationId))
                {
                    pending.Add(journal);
                }
            }
            catch (MigrationActivationException)
            {
                logger.LogWarning("Derived rebuild skipped a corrupt resolved activation journal; an operator must inspect it.");
            }
        }

        return pending;
    }

    private string MarkerPath(Guid jobId) =>
        Path.Combine(Path.GetDirectoryName(paths.Journal(jobId))!, "derived.rebuilt.json");

    private bool HasMarker(Guid jobId, Guid operationId)
    {
        var path = MarkerPath(jobId);
        paths.VerifyDatabasePath(path);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<SelfHostedDerivedRebuildMarker>(File.ReadAllBytes(path));
            return marker is { MarkerVersion: 1 }
                && marker.JobId == jobId
                && marker.OperationId == operationId;
        }
        catch (JsonException)
        {
            // A torn or foreign marker means the rebuild is simply repeated.
            return false;
        }
    }

    private void WriteMarker(Guid jobId, Guid operationId)
    {
        var path = MarkerPath(jobId);
        paths.VerifyDatabasePath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            paths.VerifyDatabasePath(temporary);
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(
                new SelfHostedDerivedRebuildMarker(jobId, operationId, clock.GetUtcNow())));
            stream.Flush(flushToDisk: true);
        }

        paths.VerifyDatabasePath(path);
        ActivationFileSystem.Rename(temporary, path, overwrite: true);
    }
}
