using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Durable, installation-scoped <see cref="IMigrationJobStore"/> over
/// <see cref="NostosDbContext"/>. Every guarded mutation is one conditional
/// database statement (scoped by state, the caller's lease token, the store
/// clock's lease-expiry instant and the integer row version) or a bounded
/// retry of that statement when the row version moved because of a concurrent
/// same-owner write; no in-memory lock is ever the correctness boundary. The
/// store obtains current time exclusively from its injected
/// <see cref="TimeProvider"/>. That injected provider is the authoritative
/// clock for one host only: every store instance sharing one database must be
/// configured with a synchronized clock, otherwise two hosts can disagree
/// about lease expiry.
/// </summary>
public sealed class EfMigrationJobStore : IMigrationJobStore
{
    // SQLite extended result codes (SQLITE_CONSTRAINT_UNIQUE / _PRIMARYKEY)
    // and the PostgreSQL SQLSTATE for a unique violation. Only these identify
    // the idempotency-key race; any other database failure must propagate.
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;
    private const string PostgresUniqueViolation = "23505";

    internal static readonly TimeSpan JobLifetime =
        TimeSpan.FromHours(MigrationContractLimits.SessionExpiryHours);

    private const int MaxIdempotencyKeyLength = 128;
    private const int MaxCancellationReasonLength = 512;
    private const int MaxProgressMessageLength = 512;
    private const int MaxGuardedMutationAttempts = 3;

    private readonly NostosDbContext _db;
    private readonly TimeProvider _timeProvider;

    public EfMigrationJobStore(NostosDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<MigrationJob?> GetAsync(Guid jobId, CancellationToken ct)
    {
        var record = await FindAsync(jobId, ct);
        return record is null ? null : Map(record);
    }

    /// <summary>
    /// Discovers non-terminal jobs that need a worker. A job qualifies when its
    /// lease is absent or its lease expiry is strictly before
    /// <paramref name="cutoffUtc"/>; a job with an unexpired lease is owned and
    /// therefore excluded. Results are ordered by the last durable mutation so
    /// the oldest orphan is picked first.
    /// </summary>
    public async Task<IReadOnlyList<MigrationJob>> GetJobsNeedingRecoveryAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken ct)
    {
        var cutoff = cutoffUtc.UtcDateTime;
        var records = await _db.MigrationJobRecords
            .AsNoTracking()
            .Where(j =>
                j.State != (int)MigrationJobState.Completed
                && j.State != (int)MigrationJobState.Failed
                && j.State != (int)MigrationJobState.Cancelled
                && j.State != (int)MigrationJobState.Expired
                && (j.MigrationLeaseToken == null
                    || j.LeaseExpiresAtUtc == null
                    || j.LeaseExpiresAtUtc < cutoff))
            .OrderBy(j => j.UpdatedAtUtc)
            .ToListAsync(ct);

        return records.Select(Map).ToList();
    }

    public async Task<MigrationIdempotencyResult<MigrationJob>> CreateAsync(
        MigrationDirection direction,
        string idempotencyKey,
        CancellationToken ct)
    {
        if (direction is not (MigrationDirection.Import or MigrationDirection.Export))
        {
            throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "Unknown migration direction.");
        }

        if (string.IsNullOrEmpty(idempotencyKey))
        {
            throw new ArgumentException(
                "A non-empty idempotency key is required.",
                nameof(idempotencyKey));
        }

        if (idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            throw new ArgumentException(
                $"The idempotency key must be at most {MaxIdempotencyKeyLength} characters.",
                nameof(idempotencyKey));
        }

        var payloadHash = ComputeCreationPayloadHash(direction);
        var existing = await FindByIdempotencyKeyAsync(idempotencyKey, ct);
        if (existing is not null)
            return ResultForExisting(existing, payloadHash);

        var now = UtcNow();
        var record = new MigrationJobRecord
        {
            Id = Guid.NewGuid(),
            Direction = (int)direction,
            State = (int)MigrationJobState.Pending,
            RecoveryStatus = (int)MigrationRecoveryStatus.NotRequired,
            ProgressPhase = (int)MigrationProgressPhase.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IdempotencyKey = idempotencyKey,
            CreationPayloadHash = payloadHash,
            ExpiresAtUtc = now.Add(JobLifetime),
            AttemptNumber = 1,
        };

        _db.MigrationJobRecords.Add(record);
        try
        {
            await _db.SaveChangesAsync(ct);
            return MigrationIdempotencyResult<MigrationJob>.Created(Map(record));
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            // A concurrent creator won the unique idempotency-key race. The
            // database is the arbiter; answer replay or conflict from the row
            // that actually exists. Detach only the failed insert so unrelated
            // tracked entities in this scoped context stay intact.
            _db.Entry(record).State = EntityState.Detached;
            var winner = await FindByIdempotencyKeyAsync(idempotencyKey, ct);
            if (winner is not null)
                return ResultForExisting(winner, payloadHash);
            throw;
        }
    }

    internal static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && (sqlite.SqliteExtendedErrorCode == SqliteConstraintUnique
                    || sqlite.SqliteExtendedErrorCode == SqliteConstraintPrimaryKey))
            {
                return true;
            }

            // The product assembly has no Npgsql reference; read SqlState from
            // the provider exception by reflection so both providers are
            // recognized without adding a database-provider dependency.
            var sqlState = current.GetType()
                .GetProperty("SqlState")
                ?.GetValue(current) as string;
            if (string.Equals(sqlState, PostgresUniqueViolation, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public async Task<MigrationJob> TransitionAsync(
        Guid jobId,
        MigrationJobState targetState,
        string leaseToken,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(leaseToken);

        for (var attempt = 1; attempt <= MaxGuardedMutationAttempts; attempt++)
        {
            var snapshot = await FindAsync(jobId, ct)
                ?? throw MigrationJobStoreException.NotFound(jobId);
            var direction = (MigrationDirection)snapshot.Direction;
            var currentState = (MigrationJobState)snapshot.State;

            MigrationJobStateMachine.EnsureTransitionAllowed(
                jobId,
                direction,
                currentState,
                targetState);

            var now = UtcNow();
            var query = _db.MigrationJobRecords.Where(j =>
                j.Id == jobId
                && j.State == snapshot.State
                && j.Version == snapshot.Version
                && j.MigrationLeaseToken == leaseToken
                && j.LeaseExpiresAtUtc != null
                && j.LeaseExpiresAtUtc > now);

            if (targetState == MigrationJobState.Cancelled)
            {
                // Cancelled has side effects beyond the job row. Route it
                // through the same atomic cancellation protocol as
                // CancelAsync so both ways of entering Cancelled produce the
                // identical durable state (lease cleared, sessions cancelled).
                if (await TryCommitCancellationTransitionAsync(query, jobId, now, ct))
                {
                    var cancelled = await FindAsync(jobId, ct);
                    return Map(cancelled!);
                }
            }
            else
            {
                var updated = targetState == MigrationJobState.Completed
                    ? await query.ExecuteUpdateAsync(
                        s => s
                            .SetProperty(j => j.State, (int)targetState)
                            .SetProperty(j => j.CompletedAtUtc, now)
                            .SetProperty(j => j.UpdatedAtUtc, now)
                            .SetProperty(j => j.Version, j => j.Version + 1),
                        ct)
                    : await query.ExecuteUpdateAsync(
                        s => s
                            .SetProperty(j => j.State, (int)targetState)
                            .SetProperty(j => j.UpdatedAtUtc, now)
                            .SetProperty(j => j.Version, j => j.Version + 1),
                        ct);

                if (updated == 1)
                {
                    var record = await FindAsync(jobId, ct);
                    return Map(record!);
                }
            }

            await EnsureRetryStillValidAsync(jobId, snapshot, leaseToken, now, ct);
        }

        // The lease stayed ours but the row version moved on every attempt:
        // the caller lost an optimistic race and must re-read before retrying.
        throw MigrationJobStoreException.LeaseConflict(jobId);
    }

    public async Task UpdateProgressAsync(
        Guid jobId,
        MigrationProgress progress,
        string leaseToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrEmpty(leaseToken);
        ValidateProgress(progress);

        for (var attempt = 1; attempt <= MaxGuardedMutationAttempts; attempt++)
        {
            var snapshot = await FindAsync(jobId, ct)
                ?? throw MigrationJobStoreException.NotFound(jobId);

            var now = UtcNow();
            var updated = await _db.MigrationJobRecords
                .Where(j =>
                    j.Id == jobId
                    && j.State == snapshot.State
                    && j.State != (int)MigrationJobState.Completed
                    && j.State != (int)MigrationJobState.Failed
                    && j.State != (int)MigrationJobState.Cancelled
                    && j.State != (int)MigrationJobState.Expired
                    && j.Version == snapshot.Version
                    && j.MigrationLeaseToken == leaseToken
                    && j.LeaseExpiresAtUtc != null
                    && j.LeaseExpiresAtUtc > now)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(j => j.ProgressPhase, (int)progress.Phase)
                        .SetProperty(j => j.ProgressBytesProcessed, progress.BytesProcessed)
                        .SetProperty(j => j.ProgressTotalBytes, progress.TotalBytes)
                        .SetProperty(j => j.ProgressCompletedChunks, progress.CompletedChunks)
                        .SetProperty(j => j.ProgressTotalChunks, progress.TotalChunks)
                        .SetProperty(j => j.ProgressMessage, progress.Message)
                        .SetProperty(j => j.HeartbeatAtUtc, now)
                        .SetProperty(j => j.UpdatedAtUtc, now)
                        .SetProperty(j => j.Version, j => j.Version + 1),
                    ct);

            if (updated == 1)
                return;

            await EnsureRetryStillValidAsync(jobId, snapshot, leaseToken, now, ct);
        }

        throw MigrationJobStoreException.LeaseConflict(jobId);
    }

    public async Task<string?> TryAcquireLeaseAsync(
        Guid jobId,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                leaseDuration,
                "The lease duration must be positive.");
        }

        for (var attempt = 1; attempt <= MaxGuardedMutationAttempts; attempt++)
        {
            var snapshot = await FindAsync(jobId, ct)
                ?? throw MigrationJobStoreException.NotFound(jobId);
            if (MigrationJobStateMachine.IsTerminal((MigrationJobState)snapshot.State))
                return null;

            var now = UtcNow();
            var token = NewLeaseToken();
            var updated = await _db.MigrationJobRecords
                .Where(j =>
                    j.Id == jobId
                    && j.State != (int)MigrationJobState.Completed
                    && j.State != (int)MigrationJobState.Failed
                    && j.State != (int)MigrationJobState.Cancelled
                    && j.State != (int)MigrationJobState.Expired
                    && j.Version == snapshot.Version
                    && (j.MigrationLeaseToken == null
                        || j.LeaseExpiresAtUtc == null
                        || j.LeaseExpiresAtUtc <= now))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(j => j.MigrationLeaseToken, token)
                        .SetProperty(j => j.LeaseExpiresAtUtc, now.Add(leaseDuration))
                        .SetProperty(j => j.HeartbeatAtUtc, now)
                        .SetProperty(j => j.UpdatedAtUtc, now)
                        .SetProperty(j => j.Version, j => j.Version + 1),
                    ct);

            if (updated == 1)
                return token;
        }

        return null;
    }

    public async Task<bool> RenewLeaseAsync(
        Guid jobId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(leaseToken);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                leaseDuration,
                "The lease duration must be positive.");
        }

        for (var attempt = 1; attempt <= MaxGuardedMutationAttempts; attempt++)
        {
            var snapshot = await FindAsync(jobId, ct);
            if (snapshot is null)
                return false;

            var now = UtcNow();
            var updated = await _db.MigrationJobRecords
                .Where(j =>
                    j.Id == jobId
                    && j.Version == snapshot.Version
                    && j.MigrationLeaseToken == leaseToken
                    && j.LeaseExpiresAtUtc != null
                    && j.LeaseExpiresAtUtc > now
                    && j.State != (int)MigrationJobState.Completed
                    && j.State != (int)MigrationJobState.Failed
                    && j.State != (int)MigrationJobState.Cancelled
                    && j.State != (int)MigrationJobState.Expired)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(j => j.LeaseExpiresAtUtc, now.Add(leaseDuration))
                        .SetProperty(j => j.HeartbeatAtUtc, now)
                        .SetProperty(j => j.UpdatedAtUtc, now)
                        .SetProperty(j => j.Version, j => j.Version + 1),
                    ct);

            if (updated == 1)
                return true;

            // Only version churn with an unchanged state/token/expiry may be
            // retried; a state change means the job moved on and this renewal
            // must stop (the contract is non-throwing for renewal contention).
            if (await ClassifyGuardFailureAsync(jobId, snapshot, leaseToken, now, ct)
                != GuardDisposition.Retry)
            {
                return false;
            }
        }

        return false;
    }

    public async Task ReleaseLeaseAsync(Guid jobId, string leaseToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(leaseToken);

        for (var attempt = 1; attempt <= MaxGuardedMutationAttempts; attempt++)
        {
            var snapshot = await FindAsync(jobId, ct);
            if (snapshot is null)
                return;

            var now = UtcNow();
            var updated = await _db.MigrationJobRecords
                .Where(j =>
                    j.Id == jobId
                    && j.Version == snapshot.Version
                    && j.MigrationLeaseToken == leaseToken)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(j => j.MigrationLeaseToken, (string?)null)
                        .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(j => j.UpdatedAtUtc, now)
                        .SetProperty(j => j.Version, j => j.Version + 1),
                    ct);

            if (updated == 1)
                return;

            // A superseded or already-released token is a no-op, never an error.
            if (!string.Equals(snapshot.MigrationLeaseToken, leaseToken, StringComparison.Ordinal))
                return;

            // Only version churn with an unchanged state/token may be retried.
            if (await ClassifyGuardFailureAsync(jobId, snapshot, leaseToken, now, ct)
                != GuardDisposition.Retry)
            {
                return;
            }
        }
    }

    public async Task CancelAsync(
        Guid jobId,
        MigrationCancelRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reason = request.Reason;
        if (reason is { Length: > MaxCancellationReasonLength })
        {
            throw new ArgumentException(
                $"The cancellation reason must be at most {MaxCancellationReasonLength} characters.",
                nameof(request));
        }

        for (var attempt = 1; attempt <= MaxGuardedMutationAttempts; attempt++)
        {
            var snapshot = await FindAsync(jobId, ct)
                ?? throw MigrationJobStoreException.NotFound(jobId);
            var state = (MigrationJobState)snapshot.State;

            if (state == MigrationJobState.Cancelled)
            {
                await CancelActiveSessionsAsync(jobId, UtcNow(), ct);
                return;
            }

            ThrowIfCannotCancel(jobId, state);

            var now = UtcNow();
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            var updated = await _db.MigrationJobRecords
                .Where(j =>
                    j.Id == jobId
                    && j.Version == snapshot.Version
                    && (j.State == (int)MigrationJobState.Pending
                        || j.State == (int)MigrationJobState.Preparing
                        || j.State == (int)MigrationJobState.Transferring
                        || j.State == (int)MigrationJobState.Validating
                        || j.State == (int)MigrationJobState.ReadyToActivate))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(j => j.State, (int)MigrationJobState.Cancelled)
                        .SetProperty(j => j.CancellationReason, reason)
                        .SetProperty(j => j.CancelledAtUtc, now)
                        .SetProperty(j => j.UpdatedAtUtc, now)
                        .SetProperty(j => j.MigrationLeaseToken, (string?)null)
                        .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(j => j.Version, j => j.Version + 1),
                    ct);

            if (updated != 1)
            {
                await transaction.RollbackAsync(ct);
                continue;
            }

            // Both durable cancellation effects commit or roll back together,
            // so a crash can never leave a terminal job with active sessions.
            await CancelActiveSessionsAsync(jobId, now, ct);
            await transaction.CommitAsync(ct);
            return;
        }

        // The row version moved on every attempt. Classify the current row one
        // final time so an idempotent replay or activation boundary reports the
        // contract's typed result rather than a spurious conflict.
        var current = await FindAsync(jobId, ct)
            ?? throw MigrationJobStoreException.NotFound(jobId);
        if ((MigrationJobState)current.State == MigrationJobState.Cancelled)
        {
            await CancelActiveSessionsAsync(jobId, UtcNow(), ct);
            return;
        }

        ThrowIfCannotCancel(jobId, (MigrationJobState)current.State);
        throw MigrationJobStoreException.LeaseConflict(jobId);
    }

    /// <summary>
    /// Reactivates a retryable terminal job as a new attempt. Chunk receipts
    /// and any retained session rows are left untouched: the transfer service
    /// revalidates the file identity before accepting more bytes, and only
    /// this method may move a retryable terminal job back to
    /// <see cref="MigrationJobState.Pending"/>.
    /// </summary>
    public async Task<MigrationJob> RetryAsync(
        Guid jobId,
        MigrationRetryRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        for (var attempt = 1; attempt <= MaxGuardedMutationAttempts; attempt++)
        {
            var snapshot = await FindAsync(jobId, ct)
                ?? throw MigrationJobStoreException.NotFound(jobId);
            var state = (MigrationJobState)snapshot.State;
            if (!MigrationJobStateMachine.IsRetryable(state))
                throw MigrationJobStoreException.NotRetryable(jobId, state);

            var now = UtcNow();
            var updated = await _db.MigrationJobRecords
                .Where(j =>
                    j.Id == jobId
                    && j.Version == snapshot.Version
                    && (j.State == (int)MigrationJobState.Failed
                        || j.State == (int)MigrationJobState.Cancelled
                        || j.State == (int)MigrationJobState.Expired))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(j => j.State, (int)MigrationJobState.Pending)
                        .SetProperty(j => j.AttemptNumber, j => j.AttemptNumber + 1)
                        .SetProperty(j => j.MigrationLeaseToken, (string?)null)
                        .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null)
                        .SetProperty(j => j.HeartbeatAtUtc, (DateTime?)null)
                        .SetProperty(j => j.FailureCode, (string?)null)
                        .SetProperty(j => j.FailureMessage, (string?)null)
                        .SetProperty(j => j.CancellationReason, (string?)null)
                        .SetProperty(j => j.CancelledAtUtc, (DateTime?)null)
                        .SetProperty(j => j.CompletedAtUtc, (DateTime?)null)
                        .SetProperty(j => j.ProgressPhase, (int)MigrationProgressPhase.Pending)
                        .SetProperty(j => j.ProgressBytesProcessed, 0L)
                        .SetProperty(j => j.ProgressTotalBytes, (long?)null)
                        .SetProperty(j => j.ProgressCompletedChunks, (int?)null)
                        .SetProperty(j => j.ProgressTotalChunks, (int?)null)
                        .SetProperty(j => j.ProgressMessage, (string?)null)
                        .SetProperty(j => j.ExpiresAtUtc, now.Add(JobLifetime))
                        .SetProperty(j => j.UpdatedAtUtc, now)
                        .SetProperty(j => j.Version, j => j.Version + 1),
                    ct);

            if (updated == 1)
            {
                var record = await FindAsync(jobId, ct);
                return Map(record!);
            }
        }

        var current = await FindAsync(jobId, ct)
            ?? throw MigrationJobStoreException.NotFound(jobId);
        var currentState = (MigrationJobState)current.State;
        if (!MigrationJobStateMachine.IsRetryable(currentState))
            throw MigrationJobStoreException.NotRetryable(jobId, currentState);
        throw MigrationJobStoreException.LeaseConflict(jobId);
    }

    internal static string ComputeCreationPayloadHash(MigrationDirection direction)
    {
        var payload = $"direction={(int)direction}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string NewLeaseToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static void ThrowIfCannotCancel(Guid jobId, MigrationJobState state)
    {
        if (state == MigrationJobState.Activating)
            throw MigrationJobStoreException.CannotCancel(jobId);

        if (MigrationJobStateMachine.IsTerminal(state))
        {
            throw MigrationJobStoreException.InvalidState(
                jobId,
                $"Migration job {jobId} is {state}; cancellation cannot rewrite terminal history.");
        }
    }

    private static void ValidateProgress(MigrationProgress progress)
    {
        if (progress.BytesProcessed < 0)
            throw new ArgumentOutOfRangeException(nameof(progress), "Progress bytes cannot be negative.");
        if (progress.TotalBytes is < 0)
            throw new ArgumentOutOfRangeException(nameof(progress), "Total bytes cannot be negative.");
        if (progress.CompletedChunks is < 0)
            throw new ArgumentOutOfRangeException(nameof(progress), "Completed chunks cannot be negative.");
        if (progress.TotalChunks is < 0)
            throw new ArgumentOutOfRangeException(nameof(progress), "Total chunks cannot be negative.");
        if (progress.Message is { Length: > MaxProgressMessageLength })
        {
            throw new ArgumentException(
                $"The progress message must be at most {MaxProgressMessageLength} characters.",
                nameof(progress));
        }
    }

    private static bool IsActiveLeaseOwner(
        MigrationJobRecord record,
        string leaseToken,
        DateTime nowUtc) =>
        string.Equals(record.MigrationLeaseToken, leaseToken, StringComparison.Ordinal)
        && record.LeaseExpiresAtUtc is { } expiresAt
        && expiresAt > nowUtc
        && !MigrationJobStateMachine.IsTerminal((MigrationJobState)record.State);

    private enum GuardDisposition
    {
        Retry,
        LeaseConflict,
        StateChanged,
        NotFound,
    }

    // A bounded retry may only re-attempt when nothing but the row version
    // changed. Any change to the lease, its expiry or the job state means the
    // operation's observation is stale and must be reported, never re-applied.
    private async Task<GuardDisposition> ClassifyGuardFailureAsync(
        Guid jobId,
        MigrationJobRecord snapshot,
        string leaseToken,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var current = await FindAsync(jobId, ct);
        if (current is null)
            return GuardDisposition.NotFound;

        if (!IsActiveLeaseOwner(current, leaseToken, nowUtc))
            return GuardDisposition.LeaseConflict;

        if (current.State != snapshot.State)
            return GuardDisposition.StateChanged;

        return GuardDisposition.Retry;
    }

    private async Task EnsureRetryStillValidAsync(
        Guid jobId,
        MigrationJobRecord snapshot,
        string leaseToken,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var disposition = await ClassifyGuardFailureAsync(
            jobId,
            snapshot,
            leaseToken,
            nowUtc,
            ct);

        switch (disposition)
        {
            case GuardDisposition.NotFound:
                throw MigrationJobStoreException.NotFound(jobId);
            case GuardDisposition.LeaseConflict:
                throw MigrationJobStoreException.LeaseConflict(jobId);
            case GuardDisposition.StateChanged:
                throw MigrationJobStoreException.InvalidState(
                    jobId,
                    $"Migration job {jobId} moved from {(MigrationJobState)snapshot.State} " +
                    "to another state before the mutation committed; the stale operation " +
                    "was rejected. Re-read the job before retrying.");
            default:
                return;
        }
    }

    // Job and session cancellation are one durable unit: both conditional
    // updates run in one transaction and commit together, so a crash between
    // them cannot publish a terminal job that still owns active sessions.
    private async Task<bool> TryCommitCancellationTransitionAsync(
        IQueryable<MigrationJobRecord> query,
        Guid jobId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        var updated = await query.ExecuteUpdateAsync(
            s => s
                .SetProperty(j => j.State, (int)MigrationJobState.Cancelled)
                .SetProperty(j => j.CancelledAtUtc, nowUtc)
                .SetProperty(j => j.UpdatedAtUtc, nowUtc)
                .SetProperty(j => j.MigrationLeaseToken, (string?)null)
                .SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null)
                .SetProperty(j => j.Version, j => j.Version + 1),
            ct);

        if (updated != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await CancelActiveSessionsAsync(jobId, nowUtc, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task CancelActiveSessionsAsync(Guid jobId, DateTime nowUtc, CancellationToken ct)
    {
        // Cancellation marks in-flight sessions Cancelled but preserves their
        // chunk receipts: a later RetryAsync reactivates the same logical job
        // and validated chunks remain eligible for reuse until session TTL.
        await _db.MigrationSessionRecords
            .Where(s =>
                s.JobId == jobId
                && (s.State == (int)MigrationSessionState.Created
                    || s.State == (int)MigrationSessionState.Receiving))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(s => s.State, (int)MigrationSessionState.Cancelled)
                    .SetProperty(s => s.UpdatedAtUtc, nowUtc)
                    .SetProperty(s => s.Version, s => s.Version + 1),
                ct);
    }

    private Task<MigrationJobRecord?> FindAsync(Guid jobId, CancellationToken ct) =>
        _db.MigrationJobRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(j => j.Id == jobId, ct);

    private Task<MigrationJobRecord?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct) =>
        _db.MigrationJobRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(j => j.IdempotencyKey == idempotencyKey, ct);

    private static MigrationIdempotencyResult<MigrationJob> ResultForExisting(
        MigrationJobRecord existing,
        string payloadHash) =>
        string.Equals(existing.CreationPayloadHash, payloadHash, StringComparison.Ordinal)
            ? MigrationIdempotencyResult<MigrationJob>.Replayed(Map(existing))
            : MigrationIdempotencyResult<MigrationJob>.Conflicted(
                new MigrationIdempotencyConflict(
                    MigrationIdempotencyConflictKind.KeyReusedWithDifferentPayload));

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static MigrationJob Map(MigrationJobRecord record) =>
        new(
            record.Id,
            (MigrationDirection)record.Direction,
            (MigrationJobState)record.State,
            (MigrationRecoveryStatus)record.RecoveryStatus,
            AsUtc(record.CreatedAtUtc),
            AsUtc(record.UpdatedAtUtc),
            record.MigrationLeaseToken,
            record.LeaseExpiresAtUtc is { } leaseExpiresAtUtc
                ? AsUtc(leaseExpiresAtUtc)
                : null,
            record.FailureCode,
            record.FailureMessage);

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
