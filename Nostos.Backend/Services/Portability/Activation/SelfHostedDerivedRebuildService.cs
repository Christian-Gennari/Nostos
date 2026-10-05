using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
/// Durable record that the one-time derived wipe for a committed cutover has
/// already happened. It is written after the wipe and before any scheduling, so
/// a crash in between repeats only the harmless wipe, while a crash after it
/// never wipes again and can never destroy ingestion progress made since.
/// </summary>
internal sealed record SelfHostedDerivedResetRecord(
    Guid JobId,
    Guid OperationId,
    DateTimeOffset ResetAtUtc,
    int MarkerVersion = 1);

/// <summary>Observable outcome of one derived rebuild pass.</summary>
internal sealed record SelfHostedDerivedRebuildPassResult(int Completed, int Failed);

/// <summary>
/// Post-activation derived rebuild (issue #681, Slice 10). A committed cutover
/// leaves a resolved <c>Committed</c> activation journal. The rebuild ensures
/// the book-text schema exists, wipes every derived row exactly once (recorded
/// durably), and then reschedules every file-backed book that is not already
/// durably queued through a strict scheduler that reports failures.
///
/// <para><b>Success contract.</b> The <c>derived.rebuilt.json</c> marker is
/// written only when every file-backed book was either durably scheduled or
/// intentionally unsupported. A book whose scheduling throws leaves the marker
/// absent; the next pass skips the wipe (reset record), skips books whose state
/// already carries the current extractor version, and retries only the rest.
/// A failure is logged and can never roll back or fail the committed
/// activation. Missing thumbnails are reconstructible lazily by
/// <c>FileStorageService</c> and are not rebuilt here.</para>
/// </summary>
internal sealed class SelfHostedDerivedRebuildService(
    SelfHostedActivationPaths paths,
    SelfHostedActivationJournalStore journals,
    LibraryMaintenanceCoordinator maintenance,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<SelfHostedDerivedRebuildService> logger)
{
    /// <summary>Test seam: throwing here models a crash after the wipe and before scheduling.</summary>
    internal Action? AfterResetForTesting { get; set; }

    internal async Task<int> RunPendingAsync(CancellationToken ct) =>
        (await RunPendingCoreAsync(ct)).Completed;

    internal async Task<SelfHostedDerivedRebuildPassResult> RunPendingCoreAsync(CancellationToken ct)
    {
        var pending = ReadPending();
        if (pending.Count == 0 || maintenance.IsRecoveryRequired)
        {
            return new SelfHostedDerivedRebuildPassResult(0, 0);
        }

        await using var operation = await maintenance.EnterOperationAsync(ct);

        // A concurrent pass may have completed between the scan and the lease.
        pending = pending.Where(journal => !HasRecord<SelfHostedDerivedRebuildMarker>(
            MarkerPath(journal.JobId), journal.JobId, journal.OperationId)).ToArray();
        if (pending.Count == 0)
        {
            return new SelfHostedDerivedRebuildPassResult(0, 0);
        }

        await using var scope = scopes.CreateAsyncScope();
        var index = scope.ServiceProvider.GetRequiredService<IBookTextIndex>();
        await index.EnsureSchemaAsync(ct);

        var needsReset = pending.Any(journal => !HasRecord<SelfHostedDerivedResetRecord>(
            ResetPath(journal.JobId), journal.JobId, journal.OperationId));
        if (needsReset)
        {
            await scope.ServiceProvider.GetRequiredService<IBookTextDerivedReset>().ResetAllAsync(ct);
            foreach (var journal in pending)
            {
                WriteRecord(ResetPath(journal.JobId),
                    new SelfHostedDerivedResetRecord(journal.JobId, journal.OperationId, clock.GetUtcNow()));
            }
        }

        AfterResetForTesting?.Invoke();

        var failed = await ScheduleMissingBooksAsync(scope, index, ct);
        if (failed > 0)
        {
            logger.LogWarning(
                "Derived rebuild left its success marker absent: {Failed} book(s) were not durably scheduled; the next pass retries them.",
                failed);
            return new SelfHostedDerivedRebuildPassResult(0, failed);
        }

        foreach (var journal in pending)
        {
            WriteRecord(MarkerPath(journal.JobId),
                new SelfHostedDerivedRebuildMarker(journal.JobId, journal.OperationId, clock.GetUtcNow()));
        }

        logger.LogInformation(
            "Derived rebuild completed for {Count} committed cutover(s); book-text indexing was rescheduled.",
            pending.Count);
        return new SelfHostedDerivedRebuildPassResult(pending.Count, 0);
    }

    /// <summary>
    /// Schedules every file-backed book that is not already durably queued.
    /// A book whose state already carries the current extractor version was
    /// scheduled or ingested since the wipe and is never reset again. Returns
    /// the number of books that could not be scheduled.
    /// </summary>
    private async Task<int> ScheduleMissingBooksAsync(IServiceScope scope, IBookTextIndex index, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var books = await db.Books.AsNoTracking()
            .Where(book => book.FileDetails.HasFile
                && book.FileDetails.FileName != null
                && book.FileDetails.FileName != "")
            .OrderBy(book => book.Id)
            .Select(book => new { book.Id, book.FileDetails.FileName })
            .ToListAsync(ct);
        var scheduler = scope.ServiceProvider.GetRequiredService<IBookTextDerivedScheduler>();
        var failed = 0;
        foreach (var book in books)
        {
            ct.ThrowIfCancellationRequested();
            var state = await index.GetStateAsync(book.Id, ct);
            if (state is not null
                && string.Equals(state.ExtractorVersion, BookTextArtifactSchema.CurrentExtractorVersion, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                await scheduler.ScheduleAsync(book.Id, book.FileName!, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                logger.LogWarning(
                    "Derived rebuild could not schedule book {BookId}; exception type {ExceptionType}. The marker stays absent and the next pass retries.",
                    book.Id,
                    exception.GetType().Name);
            }
        }

        return failed;
    }

    /// <summary>
    /// Every resolved terminal journal whose derived rebuild marker is missing.
    /// The cutover clears the live <c>derived/</c> caches before the media root
    /// is retained, so a committed replacement and a rolled-back (or restored)
    /// generation both need the wipe-and-reschedule pass: neither may serve or
    /// keep derived state from the other generation. A corrupt resolved journal
    /// is skipped: the operator guide owns it, and the derived rebuild must not
    /// guess.
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
                if (journal is { Phase: SelfHostedActivationPhase.Committed or SelfHostedActivationPhase.RolledBack }
                    && !HasRecord<SelfHostedDerivedRebuildMarker>(MarkerPath(journal.JobId), journal.JobId, journal.OperationId))
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

    private string ResetPath(Guid jobId) =>
        Path.Combine(Path.GetDirectoryName(paths.Journal(jobId))!, "derived.reset.json");

    private bool HasRecord<T>(string path, Guid jobId, Guid operationId)
    {
        paths.VerifyDatabasePath(path);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var record = JsonSerializer.Deserialize<T>(File.ReadAllBytes(path));
            return record switch
            {
                SelfHostedDerivedRebuildMarker marker =>
                    marker.MarkerVersion == 1 && marker.JobId == jobId && marker.OperationId == operationId,
                SelfHostedDerivedResetRecord reset =>
                    reset.MarkerVersion == 1 && reset.JobId == jobId && reset.OperationId == operationId,
                _ => false,
            };
        }
        catch (JsonException)
        {
            // A torn or foreign record means the step is simply repeated.
            return false;
        }
    }

    private void WriteRecord<T>(string path, T record)
    {
        paths.VerifyDatabasePath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            paths.VerifyDatabasePath(temporary);
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(record));
            stream.Flush(flushToDisk: true);
        }

        paths.VerifyDatabasePath(path);
        ActivationFileSystem.Rename(temporary, path, overwrite: true);
    }
}
