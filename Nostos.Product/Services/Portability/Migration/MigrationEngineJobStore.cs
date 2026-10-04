namespace Nostos.Backend.Services.Portability.Migration;

// Retrying and detaching terminal files must use the same cross-process mutex.
// All ordinary state/lease guards remain owned by the existing EF store.
internal sealed class MigrationEngineJobStore(EfMigrationJobStore inner, MigrationFileMutex files) : IMigrationJobStore
{
    public Task<MigrationJob?> GetAsync(Guid id, CancellationToken ct) => inner.GetAsync(id, ct);
    public Task<IReadOnlyList<MigrationJob>> GetJobsNeedingRecoveryAsync(DateTimeOffset cutoff, CancellationToken ct) => inner.GetJobsNeedingRecoveryAsync(cutoff, ct);
    public Task<MigrationIdempotencyResult<MigrationJob>> CreateAsync(MigrationDirection direction, string key, CancellationToken ct) => inner.CreateAsync(direction, key, ct);
    public Task<MigrationJob> TransitionAsync(Guid id, MigrationJobState target, string token, CancellationToken ct) => inner.TransitionAsync(id, target, token, ct);
    public Task UpdateProgressAsync(Guid id, MigrationProgress progress, string token, CancellationToken ct) => inner.UpdateProgressAsync(id, progress, token, ct);
    public Task<string?> TryAcquireLeaseAsync(Guid id, TimeSpan duration, CancellationToken ct) => inner.TryAcquireLeaseAsync(id, duration, ct);
    public Task<bool> RenewLeaseAsync(Guid id, string token, TimeSpan duration, CancellationToken ct) => inner.RenewLeaseAsync(id, token, duration, ct);
    public Task ReleaseLeaseAsync(Guid id, string token, CancellationToken ct) => inner.ReleaseLeaseAsync(id, token, ct);
    public Task CancelAsync(Guid id, MigrationCancelRequest request, CancellationToken ct) => inner.CancelAsync(id, request, ct);
    public async Task<MigrationJob> RetryAsync(Guid id, MigrationRetryRequest request, CancellationToken ct)
    {
        _ = await inner.GetAsync(id, ct) ?? throw MigrationJobStoreException.NotFound(id);
        await using var lease = await files.EnterAsync(id, ct);
        return await inner.RetryAsync(id, request, ct);
    }
}
