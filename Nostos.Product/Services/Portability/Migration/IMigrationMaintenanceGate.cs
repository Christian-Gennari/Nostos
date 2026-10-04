namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Host maintenance adapter. A worker acquires admission before creating a DI
/// scope, disposes the scope before releasing admission, and never sleeps with
/// admission held. Long handlers check IsMaintenanceRequested at every bounded
/// IO/progress checkpoint, stop without failing the job, and remain resumable.
/// </summary>
public interface IMigrationMaintenanceGate
{
    bool IsMaintenanceRequested { get; }
    ValueTask<IAsyncDisposable> EnterAsync(CancellationToken ct);
}

internal sealed class MigrationMaintenanceRequestedException : OperationCanceledException;
