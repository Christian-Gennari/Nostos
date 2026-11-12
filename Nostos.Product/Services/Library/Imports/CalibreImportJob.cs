using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Runs at most one Calibre import at a time per owner and keeps its progress
/// for the dialog, like the external media import does. The work belongs to
/// the server: closing the page does not stop it, and reopening Settings shows
/// where it is. A restart loses the live progress, not the books already
/// written — each one is in the batch, so undo still takes them back.
///
/// State is keyed by owner because a hosted deployment serves several tenants
/// from one process; SelfHosted has one owner key and behaves as before.
/// </summary>
public sealed class CalibreImportJob(
    IServiceScopeFactory scopes,
    CalibreUploadOwnerScope ownerScope,
    ILogger<CalibreImportJob> logger)
{
    /// <summary>Used by a host with no upload owner provider (a folder-only host).</summary>
    public const string DefaultOwnerKey = "default";

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    private static CalibreImportJobState IdleState() =>
        new(CalibreImportJobState.Idle, null, null, 0, 0, null, null, [], null);

    public CalibreImportJobState StateFor(string ownerKey) => SlotFor(ownerKey).State;

    public Task CompletionFor(string ownerKey) => SlotFor(ownerKey).Running;

    public enum StartResult
    {
        Started,
        AlreadyRunning,
        NotAvailable,
        NothingSelected,
    }

    public StartResult TryStart(
        IReadOnlyList<CalibreImportDecision> decisions,
        string ownerKey,
        CancellationToken stopping)
    {
        var slot = SlotFor(ownerKey);
        lock (slot.Gate)
        {
            if (slot.State.Status == CalibreImportJobState.Importing)
                return StartResult.AlreadyRunning;
            if (decisions.Count == 0)
                return StartResult.NothingSelected;

            slot.State = new CalibreImportJobState(
                CalibreImportJobState.Importing,
                DateTime.UtcNow,
                null,
                decisions.Count,
                0,
                null,
                null,
                [],
                null);
            slot.Running = Task.Run(
                () => RunAsync(decisions, ownerKey, slot, stopping),
                CancellationToken.None);
            return StartResult.Started;
        }
    }

    private async Task RunAsync(
        IReadOnlyList<CalibreImportDecision> decisions,
        string ownerKey,
        Slot slot,
        CancellationToken ct)
    {
        // The request that started this job is gone; the scope restores the
        // owner so the upload source reads this tenant's staged library only.
        using var scope = scopes.CreateScope();
        using var restored = ownerScope.Enter(ownerKey);
        try
        {
            var importer = scope.ServiceProvider.GetRequiredService<CalibreImportService>();
            var source = scope.ServiceProvider.GetRequiredService<ICalibreLibrarySource>();
            var result = await importer.CommitAsync(
                decisions,
                progress => Update(slot, state => state with
                {
                    Completed = progress.Completed,
                    Total = progress.Total,
                    Current = progress.Current,
                    BatchId = progress.BatchId,
                    Results = progress.LastResult is null
                        ? state.Results
                        : [.. state.Results, progress.LastResult],
                }),
                ct);

            // The chosen files are now Nostos's own copies. An uploaded zip is
            // not needed any more; a mounted folder has nothing to release.
            await source.ReleaseAfterCommitAsync(CancellationToken.None);

            Update(slot, state => state with
            {
                Status = CalibreImportJobState.Done,
                FinishedAt = DateTime.UtcNow,
                Completed = state.Total,
                Current = null,
                BatchId = result.BatchId,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Update(slot, state => state with
            {
                Status = CalibreImportJobState.Interrupted,
                FinishedAt = DateTime.UtcNow,
                Current = null,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "The Calibre import failed; exception type {ExceptionType}. Details suppressed.",
                ex.GetType().Name);
            Update(slot, state => state with
            {
                Status = CalibreImportJobState.Failed,
                FinishedAt = DateTime.UtcNow,
                Current = null,
                Error = ex is CalibreLibraryException
                    ? ex.Message
                    : "The Calibre library could not be read. Check that it is still available.",
            });
        }
    }

    private Slot SlotFor(string ownerKey) =>
        _slots.GetOrAdd(ownerKey, _ => new Slot());

    private static void Update(Slot slot, Func<CalibreImportJobState, CalibreImportJobState> change)
    {
        lock (slot.Gate)
            slot.State = change(slot.State);
    }

    private sealed class Slot
    {
        public Lock Gate { get; } = new();
        public CalibreImportJobState State { get; set; } = IdleState();
        public Task Running { get; set; } = Task.CompletedTask;
    }
}
