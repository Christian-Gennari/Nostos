namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Customer-facing "restore previous library" surface (issue #681, Slice 9).
/// Implemented by the SelfHosted host; mapped by
/// <c>Nostos.Backend.Endpoints.MigrationRecoveryEndpoints</c> under the same
/// route group policies as the migration transfer routes. Listing and status
/// reads answer from durable filesystem manifests only, so they remain usable
/// while the exclusive maintenance window is active; a restore request
/// validates against the live library and returns as soon as the copy is
/// durably claimed, while the work runs outside the request.
/// </summary>
public interface ISelfHostedRecoveryRestore
{
    Task<IReadOnlyList<MigrationRecoveryStatusResponse>> ListAsync(CancellationToken ct);

    Task<MigrationRecoveryStatusResponse?> GetAsync(Guid recoveryId, CancellationToken ct);

    Task<MigrationRecoveryStatusResponse> RequestRestoreAsync(
        Guid recoveryId,
        MigrationRecoveryRestoreRequest request,
        CancellationToken ct);
}
