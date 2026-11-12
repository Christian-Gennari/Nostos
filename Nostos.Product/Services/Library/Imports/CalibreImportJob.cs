using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Runs at most one Calibre import at a time and keeps its progress for the
/// dialog, like the external media import does. The work belongs to the server:
/// closing the page does not stop it, and reopening Settings shows where it is.
/// A restart loses the live progress, not the books already written — each one
/// is in the batch, so undo still takes them back.
/// </summary>
public sealed class CalibreImportJob(
    IServiceScopeFactory scopes,
    ICalibreLibrarySource source,
    ILogger<CalibreImportJob> logger)
{
    private readonly Lock _gate = new();
    private CalibreImportJobState _state = new(
        CalibreImportJobState.Idle, null, null, 0, 0, null, null, [], null);
    private Task _running = Task.CompletedTask;

    public CalibreImportJobState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public Task Completion
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    public enum StartResult
    {
        Started,
        AlreadyRunning,
        NotAvailable,
        NothingSelected,
    }

    public StartResult TryStart(IReadOnlyList<CalibreImportDecision> decisions, CancellationToken stopping)
    {
        lock (_gate)
        {
            if (_state.Status == CalibreImportJobState.Importing)
                return StartResult.AlreadyRunning;
            if (!source.IsAvailable)
                return StartResult.NotAvailable;
            if (decisions.Count == 0)
                return StartResult.NothingSelected;

            _state = new CalibreImportJobState(
                CalibreImportJobState.Importing,
                DateTime.UtcNow,
                null,
                decisions.Count,
                0,
                null,
                null,
                [],
                null);
            _running = Task.Run(() => RunAsync(decisions, stopping), CancellationToken.None);
            return StartResult.Started;
        }
    }

    private async Task RunAsync(IReadOnlyList<CalibreImportDecision> decisions, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var importer = scope.ServiceProvider.GetRequiredService<CalibreImportService>();
            var result = await importer.CommitAsync(
                decisions,
                progress => Update(state => state with
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

            Update(state => state with
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
            Update(state => state with
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
            Update(state => state with
            {
                Status = CalibreImportJobState.Failed,
                FinishedAt = DateTime.UtcNow,
                Current = null,
                Error = ex is CalibreLibraryException
                    ? ex.Message
                    : "The Calibre library could not be read. Check that the folder is still available.",
            });
        }
    }

    private void Update(Func<CalibreImportJobState, CalibreImportJobState> change)
    {
        lock (_gate)
            _state = change(_state);
    }
}
