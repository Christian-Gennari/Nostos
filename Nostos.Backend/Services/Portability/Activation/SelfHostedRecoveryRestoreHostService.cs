namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Host adapter behind <see cref="ISelfHostedRecoveryRestore"/>: listing/status
/// come from the filesystem-backed catalog, and a restore request is claimed
/// exactly once under the runner's per-copy gate before the detached run starts.
/// </summary>
internal sealed class SelfHostedRecoveryRestoreHostService(
    ISelfHostedRecoveryCatalog catalog,
    SelfHostedRecoveryRestoreCoordinator coordinator,
    SelfHostedRecoveryRestoreRunner runner) : ISelfHostedRecoveryRestore
{
    public Task<IReadOnlyList<MigrationRecoveryStatusResponse>> ListAsync(CancellationToken ct) =>
        catalog.ListAsync(ct);

    public Task<MigrationRecoveryStatusResponse?> GetAsync(Guid recoveryId, CancellationToken ct) =>
        catalog.GetAsync(recoveryId, ct);

    public Task<MigrationRecoveryStatusResponse> RequestRestoreAsync(
        Guid recoveryId,
        MigrationRecoveryRestoreRequest request,
        CancellationToken ct) =>
        runner.WithGateAsync(recoveryId, async () =>
        {
            var status = await coordinator.RequestRestoreAsync(recoveryId, request, ct);
            runner.Start(recoveryId);
            return status;
        }, ct);
}
