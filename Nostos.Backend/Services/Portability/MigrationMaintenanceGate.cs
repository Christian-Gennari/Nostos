using Nostos.Backend.Services.Portability.Migration;

namespace Nostos.Backend.Services.Portability;

/// <summary>Uses the same host barrier as requests, restore, activation and other workers.</summary>
public sealed class MigrationMaintenanceGate(ILibraryMaintenanceCoordinator maintenance) : IMigrationMaintenanceGate
{
    public bool IsMaintenanceRequested => maintenance.IsMaintenanceActive;
    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken ct) => await maintenance.EnterOperationAsync(ct);
}
