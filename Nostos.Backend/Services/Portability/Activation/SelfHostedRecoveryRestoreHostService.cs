namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Host adapter behind <see cref="ISelfHostedRecoveryRestore"/>: listing comes
/// from the filesystem-backed catalog, and status/restore requests run through
/// the same library-switch dispatcher as activation, so a restore can never run
/// concurrently with an activation and a second request for the same copy never
/// starts a second run.
/// </summary>
internal sealed class SelfHostedRecoveryRestoreHostService(
    ISelfHostedRecoveryCatalog catalog,
    SelfHostedActivationDispatcher dispatcher) : ISelfHostedRecoveryRestore
{
    public Task<IReadOnlyList<MigrationRecoveryStatusResponse>> ListAsync(CancellationToken ct) =>
        catalog.ListAsync(ct);

    public Task<MigrationRecoveryRestoreStatusResponse> GetStatusAsync(
        Guid recoveryId,
        CancellationToken ct) =>
        dispatcher.GetRestoreStatusAsync(recoveryId, ct);

    public Task<MigrationRecoveryRestoreStatusResponse> RequestRestoreAsync(
        Guid recoveryId,
        MigrationRecoveryRestoreRequest request,
        CancellationToken ct) =>
        dispatcher.RequestRestoreAsync(recoveryId, request, ct);
}
