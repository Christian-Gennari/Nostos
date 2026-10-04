// Nostos.Product/Services/Portability/Transfers/TransferStorageCapacity.cs

using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability.Transfers;

/// <summary>
/// Physical free-space measurement for the volume that holds the configured
/// transfer root. Injected so tests can simulate a full disk without touching
/// the developer machine, and so a host can substitute a volume abstraction.
/// </summary>
public interface ITransferVolume
{
    /// <summary>Bytes available to the current process on the transfer volume.</summary>
    long AvailableFreeSpaceBytes { get; }

    /// <summary>Total size of the transfer volume in bytes.</summary>
    long TotalSizeBytes { get; }
}

/// <summary>
/// A point-in-time view of transfer capacity. All byte counts are exact; the
/// timestamp is the accounting clock the reservation predicates used.
/// </summary>
public sealed record TransferCapacitySnapshot(
    long PhysicalAvailableBytes,
    long PhysicalTotalBytes,
    long GlobalSafetyMarginBytes,
    long OutstandingReservedBytes,
    int ActiveReservationCount,
    long UsableAvailableBytes,
    DateTimeOffset MeasuredAtUtc)
{
    public static TransferCapacitySnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, default);
}

/// <summary>Result of an admission attempt. Rejections carry the snapshot that produced them.</summary>
public sealed record TransferReservationResult(
    bool IsAdmitted,
    Guid? ReservationId,
    long RequestedBytes,
    long OutstandingAfter,
    DateTimeOffset? ExpiresAtUtc,
    TransferCapacitySnapshot Snapshot);

public enum TransferReservationConflictKind
{
    NotFound = 0,
    AlreadyClaimed = 1,
    Expired = 2,
    Released = 3,

    /// <summary>
    /// Admission could not be serialized against competing writers within the
    /// bounded retry budget. The caller may retry the whole request.
    /// </summary>
    Contended = 4,

    /// <summary>
    /// Materializing the requested bytes would push materialized accounting
    /// beyond the reserved amount.
    /// </summary>
    OverMaterialized = 5,
}

/// <summary>
/// Typed failure for a reservation mutation that lost its exactly-once
/// predicate (already claimed, expired, released, over-materialized, or
/// missing), or for admission that exhausted its serialization retries.
/// </summary>
public sealed class TransferReservationException : InvalidOperationException
{
    public TransferReservationException(
        TransferReservationConflictKind kind,
        Guid reservationId)
        : this(kind, $"Transfer reservation '{reservationId}' cannot be used: {kind}.")
    {
        ReservationId = reservationId;
    }

    private TransferReservationException(
        TransferReservationConflictKind kind,
        string message)
        : base(message) => Kind = kind;

    public TransferReservationConflictKind Kind { get; }

    /// <summary>Empty for admission-contention failures, which have no reservation yet.</summary>
    public Guid ReservationId { get; }

    public static TransferReservationException AdmissionContended(int attempts) =>
        new(
            TransferReservationConflictKind.Contended,
            $"Transfer storage admission could not be serialized after {attempts} attempts.");
}

/// <summary>
/// Durable, race-safe storage admission for library transfer jobs (issue #679,
/// plan section 2.6). Preflight reserves peak bytes before a large upload;
/// capacity accounting subtracts only
/// <c>max(ReservedBytes - MaterializedBytes, 0)</c> from physical free space
/// because materialized bytes are already reflected in the filesystem.
/// </summary>
public interface ITransferStorageCapacity
{
    Task<TransferCapacitySnapshot> GetSnapshotAsync(CancellationToken ct);

    /// <summary>
    /// Reserves <paramref name="requiredBytes"/> of the transfer volume for a
    /// preflight hold of <paramref name="ttl"/>. The amount is the host peak
    /// requirement for the transfer as returned by
    /// <see cref="TransferCapacityMath.CalculateHostPeakReservationBytes"/>:
    /// contract bytes plus the effective chunk plus the fixed per-job
    /// overhead. The global safety margin is <b>not</b> part of
    /// <paramref name="requiredBytes"/>; admission applies it exactly once
    /// against the measured physical free space, so adding it here would
    /// charge it twice.
    ///
    /// The hold expires only while it is unclaimed. Once
    /// <see cref="ClaimAsync"/> has bound it to a job, it counts toward used
    /// capacity until <see cref="ReleaseAsync"/> is called by terminal job
    /// cleanup, regardless of the preflight expiry.
    /// </summary>
    Task<TransferReservationResult> TryReserveAsync(
        long requiredBytes,
        MigrationSessionPurpose purpose,
        TimeSpan ttl,
        CancellationToken ct);

    /// <summary>
    /// Installation-scoped preflight admission: atomically releases every live
    /// unclaimed reservation and creates the new one in a single transaction,
    /// so at most one unclaimed preflight hold exists at a time. Claimed
    /// reservations are never touched. When the new hold cannot be admitted the
    /// transaction rolls back and the previous unclaimed hold stays in place.
    /// </summary>
    Task<TransferReservationResult> TryReplaceUnclaimedAsync(
        long requiredBytes,
        MigrationSessionPurpose purpose,
        TimeSpan ttl,
        CancellationToken ct);

    /// <summary>
    /// Atomically claims an unexpired, unreleased, unclaimed reservation for a
    /// job. Exactly one concurrent caller can win; every loser receives a
    /// typed <see cref="TransferReservationException"/>. A claimed reservation
    /// no longer expires on the preflight clock: it stays live until released.
    /// </summary>
    Task ClaimAsync(Guid reservationId, Guid jobId, CancellationToken ct);

    /// <summary>
    /// Records bytes that are now physically materialized, reducing the
    /// outstanding part of the reservation instead of double-counting them
    /// against <c>DriveInfo</c>. The increment is refused with a typed
    /// <see cref="TransferReservationConflictKind.OverMaterialized"/> failure
    /// when it would push materialized bytes beyond the reserved amount, so
    /// duplicate accounting cannot silently overstate progress.
    /// </summary>
    Task AddMaterializedBytesAsync(Guid reservationId, long deltaBytes, CancellationToken ct);

    /// <summary>
    /// Ensures at least <paramref name="targetBytes"/> are recorded as
    /// materialized on a live reservation, as one conditional UPDATE.
    /// <paramref name="targetBytes"/> is an absolute target, capped at the
    /// reserved amount, and the call is idempotent: repeating it with the same
    /// target never moves materialization backwards or forwards again, and a
    /// reservation already at or above the effective target is a no-op. A
    /// missing, released or expired reservation fails with a typed
    /// <see cref="TransferReservationException"/> instead of being treated as
    /// a fresh reservation.
    /// </summary>
    Task EnsureMaterializedAtLeastAsync(Guid reservationId, long targetBytes, CancellationToken ct);

    /// <summary>
    /// Releases a reservation out of capacity accounting. Idempotent: an
    /// already-released or unknown reservation is a no-op so cleanup sweeps
    /// can retry safely.
    /// </summary>
    Task ReleaseAsync(Guid reservationId, CancellationToken ct);
}

/// <summary>
/// Admission arithmetic from plan section 2.6, kept in one place so the
/// preflight service and the capacity service cannot disagree.
///
/// Margin ownership: the global safety margin
/// (<see cref="CalculateGlobalSafetyMarginBytes"/>) is applied exactly once,
/// by capacity admission (<see cref="ITransferStorageCapacity.TryReserveAsync"/>
/// and <see cref="ITransferStorageCapacity.GetSnapshotAsync"/>), against the
/// measured physical free space. It is deliberately <b>not</b> part of
/// <see cref="CalculateHostPeakReservationBytes"/>; a caller that adds it
/// there and passes the result to <c>TryReserveAsync</c> would charge the
/// margin twice.
/// </summary>
public static class TransferCapacityMath
{
    /// <summary>
    /// Fixed metadata/temp allowance charged per active job on top of the
    /// global unallocatable margin.
    /// </summary>
    public const long PerJobOverheadBytes = 8L * 1024 * 1024;

    /// <summary>
    /// The global unallocatable margin:
    /// <c>max(DiskSafetyMarginBytes, PhysicalVolumeSize * DiskSafetyMarginPercent / 100)</c>.
    /// Admission subtracts this once from physical free space; do not fold it
    /// into a reservation request.
    /// </summary>
    public static long CalculateGlobalSafetyMarginBytes(
        long physicalVolumeBytes,
        TransferStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var percentMargin = physicalVolumeBytes <= 0
            ? 0L
            : (long)Math.Ceiling(physicalVolumeBytes * (options.DiskSafetyMarginPercent / 100.0));
        return Math.Max(options.DiskSafetyMarginBytes, percentMargin);
    }

    /// <summary>
    /// <c>ContractRequiredBytes + EffectiveChunkBytes + PerJobOverheadBytes</c>.
    /// The global safety margin is excluded on purpose: admission subtracts it
    /// once from physical free space, so the result can be passed directly to
    /// <see cref="ITransferStorageCapacity.TryReserveAsync"/> without
    /// double-charging the margin.
    /// </summary>
    public static long CalculateHostPeakReservationBytes(
        long contractRequiredBytes,
        int effectiveChunkBytes,
        TransferStorageOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contractRequiredBytes);
        ArgumentNullException.ThrowIfNull(options);

        var chunkBytes = effectiveChunkBytes > 0 ? effectiveChunkBytes : options.ChunkBytes;
        return checked(contractRequiredBytes + chunkBytes + PerJobOverheadBytes);
    }
}

/// <summary>
/// SelfHosted capacity service: measures the physical volume and persists
/// reservations in <see cref="MigrationStorageReservationRecord"/> with
/// atomic claim-exactly-once semantics. All time comparisons are real SQL
/// predicates over UTC <see cref="DateTime"/> columns.
/// </summary>
public sealed class TransferStorageCapacity : ITransferStorageCapacity
{
    private readonly NostosDbContext _db;
    private readonly ITransferVolume _volume;
    private readonly TransferStorageOptions _options;
    private readonly TimeProvider _timeProvider;

    public TransferStorageCapacity(
        NostosDbContext db,
        ITransferVolume volume,
        IOptions<TransferStorageOptions> options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(options);

        TransferStorageOptions.Validate(options.Value);

        _db = db;
        _volume = volume;
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TransferCapacitySnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        return await ReadSnapshotAsync(ClockUtcNow(), ct);
    }

    public async Task<TransferReservationResult> TryReserveAsync(
        long requiredBytes,
        MigrationSessionPurpose purpose,
        TimeSpan ttl,
        CancellationToken ct)
    {
        if (requiredBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredBytes),
                requiredBytes,
                "A storage reservation must request at least one byte.");
        }

        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                ttl,
                "A storage reservation must have a positive lifetime.");
        }

        try
        {
            return await TransferAdmissionRetry.ExecuteAsync(
                token => TryReserveOnceAsync(requiredBytes, purpose, ttl, token),
                onRetry: _db.ChangeTracker.Clear,
                ct);
        }
        catch (Exception exception) when (TransferAdmissionRetry.IsTransientContention(exception))
        {
            throw TransferReservationException.AdmissionContended(
                TransferAdmissionRetry.MaxAttempts);
        }
    }

    public async Task<TransferReservationResult> TryReplaceUnclaimedAsync(
        long requiredBytes,
        MigrationSessionPurpose purpose,
        TimeSpan ttl,
        CancellationToken ct)
    {
        if (requiredBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredBytes),
                requiredBytes,
                "A storage reservation must request at least one byte.");
        }

        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                ttl,
                "A storage reservation must have a positive lifetime.");
        }

        try
        {
            return await TransferAdmissionRetry.ExecuteAsync(
                token => TryReplaceUnclaimedOnceAsync(requiredBytes, purpose, ttl, token),
                onRetry: _db.ChangeTracker.Clear,
                ct);
        }
        catch (Exception exception) when (TransferAdmissionRetry.IsTransientContention(exception))
        {
            throw TransferReservationException.AdmissionContended(
                TransferAdmissionRetry.MaxAttempts);
        }
    }

    public async Task ClaimAsync(Guid reservationId, Guid jobId, CancellationToken ct)
    {
        if (reservationId == Guid.Empty)
        {
            throw new ArgumentException("A reservation identifier is required.", nameof(reservationId));
        }

        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A claiming job identifier is required.", nameof(jobId));
        }

        var now = ClockUtcNow();
        var affected = await _db.MigrationStorageReservations
            .Where(r => r.Id == reservationId
                && r.ClaimedJobId == null
                && r.ReleasedAtUtc == null
                && r.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.ClaimedJobId, jobId)
                    .SetProperty(r => r.Version, r => r.Version + 1),
                ct);

        if (affected == 1)
        {
            return;
        }

        throw new TransferReservationException(
            await ClassifyClaimFailureAsync(reservationId, now, ct),
            reservationId);
    }

    public async Task AddMaterializedBytesAsync(
        Guid reservationId,
        long deltaBytes,
        CancellationToken ct)
    {
        if (reservationId == Guid.Empty)
        {
            throw new ArgumentException("A reservation identifier is required.", nameof(reservationId));
        }

        if (deltaBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaBytes),
                deltaBytes,
                "Materialized bytes must be positive.");
        }

        var now = ClockUtcNow();
        var affected = await _db.MigrationStorageReservations
            .Where(r => r.Id == reservationId
                && r.ReleasedAtUtc == null
                && (r.ClaimedJobId != null || r.ExpiresAtUtc > now)
                && r.MaterializedBytes + deltaBytes <= r.ReservedBytes)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.MaterializedBytes, r => r.MaterializedBytes + deltaBytes)
                    .SetProperty(r => r.Version, r => r.Version + 1),
                ct);

        if (affected == 0)
        {
            throw new TransferReservationException(
                await ClassifyMaterializeFailureAsync(reservationId, now, ct),
                reservationId);
        }
    }

    public async Task EnsureMaterializedAtLeastAsync(
        Guid reservationId,
        long targetBytes,
        CancellationToken ct)
    {
        if (reservationId == Guid.Empty)
        {
            throw new ArgumentException("A reservation identifier is required.", nameof(reservationId));
        }

        if (targetBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetBytes),
                targetBytes,
                "A materialized target cannot be negative.");
        }

        var now = ClockUtcNow();
        var affected = await _db.MigrationStorageReservations
            .Where(r => r.Id == reservationId
                && r.ReleasedAtUtc == null
                && (r.ClaimedJobId != null || r.ExpiresAtUtc > now)
                && r.MaterializedBytes < (r.ReservedBytes < targetBytes ? r.ReservedBytes : targetBytes))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        r => r.MaterializedBytes,
                        r => r.ReservedBytes < targetBytes ? r.ReservedBytes : targetBytes)
                    .SetProperty(r => r.Version, r => r.Version + 1),
                ct);

        if (affected == 1)
        {
            return;
        }

        var state = await ReadReservationStateAsync(reservationId, ct);
        if (state is null)
        {
            throw new TransferReservationException(TransferReservationConflictKind.NotFound, reservationId);
        }

        if (state.ReleasedAtUtc is not null)
        {
            throw new TransferReservationException(TransferReservationConflictKind.Released, reservationId);
        }

        var effectiveTarget = Math.Min(targetBytes, state.ReservedBytes);
        if (state.MaterializedBytes >= effectiveTarget)
        {
            return; // already at (or above) the requested absolute target: idempotent no-op
        }

        throw new TransferReservationException(
            state.ClaimedJobId is null && state.ExpiresAtUtc <= now
                ? TransferReservationConflictKind.Expired
                : TransferReservationConflictKind.Contended,
            reservationId);
    }

    public async Task ReleaseAsync(Guid reservationId, CancellationToken ct)
    {
        if (reservationId == Guid.Empty)
        {
            throw new ArgumentException("A reservation identifier is required.", nameof(reservationId));
        }

        var now = ClockUtcNow();
        await _db.MigrationStorageReservations
            .Where(r => r.Id == reservationId && r.ReleasedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.ReleasedAtUtc, now)
                    .SetProperty(r => r.Version, r => r.Version + 1),
                ct);
    }

    private async Task<TransferReservationResult> TryReserveOnceAsync(
        long requiredBytes,
        MigrationSessionPurpose purpose,
        TimeSpan ttl,
        CancellationToken ct)
    {
        var nowOffset = _timeProvider.GetUtcNow();
        var now = nowOffset.UtcDateTime;

        await using var transaction = await BeginAdmissionTransactionAsync(ct);
        var snapshot = await ReadSnapshotAsync(now, ct);
        if (requiredBytes > snapshot.UsableAvailableBytes)
        {
            await transaction.RollbackAsync(ct);
            return new TransferReservationResult(
                IsAdmitted: false,
                ReservationId: null,
                RequestedBytes: requiredBytes,
                OutstandingAfter: snapshot.OutstandingReservedBytes,
                ExpiresAtUtc: null,
                Snapshot: snapshot with { MeasuredAtUtc = nowOffset });
        }

        var reservation = new MigrationStorageReservationRecord
        {
            Id = Guid.NewGuid(),
            Purpose = (int)purpose,
            ReservedBytes = requiredBytes,
            MaterializedBytes = 0,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(ttl),
        };

        _db.MigrationStorageReservations.Add(reservation);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new TransferReservationResult(
            IsAdmitted: true,
            ReservationId: reservation.Id,
            RequestedBytes: requiredBytes,
            OutstandingAfter: snapshot.OutstandingReservedBytes + requiredBytes,
            ExpiresAtUtc: new DateTimeOffset(
                DateTime.SpecifyKind(reservation.ExpiresAtUtc, DateTimeKind.Utc)),
            Snapshot: snapshot with { MeasuredAtUtc = nowOffset });
    }

    private async Task<TransferReservationResult> TryReplaceUnclaimedOnceAsync(
        long requiredBytes,
        MigrationSessionPurpose purpose,
        TimeSpan ttl,
        CancellationToken ct)
    {
        var nowOffset = _timeProvider.GetUtcNow();
        var now = nowOffset.UtcDateTime;

        await using var transaction = await BeginAdmissionTransactionAsync(ct);

        // Installation-scoped bound: at most one live unclaimed preflight hold.
        // Claimed reservations are untouched; a rejected replacement rolls back
        // with the previous unclaimed hold still live.
        await _db.MigrationStorageReservations
            .Where(r => r.ReleasedAtUtc == null && r.ClaimedJobId == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.ReleasedAtUtc, now)
                    .SetProperty(r => r.Version, r => r.Version + 1),
                ct);

        var snapshot = await ReadSnapshotAsync(now, ct);
        if (requiredBytes > snapshot.UsableAvailableBytes)
        {
            await transaction.RollbackAsync(ct);
            return new TransferReservationResult(
                IsAdmitted: false,
                ReservationId: null,
                RequestedBytes: requiredBytes,
                OutstandingAfter: snapshot.OutstandingReservedBytes,
                ExpiresAtUtc: null,
                Snapshot: snapshot with { MeasuredAtUtc = nowOffset });
        }

        var reservation = new MigrationStorageReservationRecord
        {
            Id = Guid.NewGuid(),
            Purpose = (int)purpose,
            ReservedBytes = requiredBytes,
            MaterializedBytes = 0,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(ttl),
        };

        _db.MigrationStorageReservations.Add(reservation);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new TransferReservationResult(
            IsAdmitted: true,
            ReservationId: reservation.Id,
            RequestedBytes: requiredBytes,
            OutstandingAfter: snapshot.OutstandingReservedBytes + requiredBytes,
            ExpiresAtUtc: new DateTimeOffset(
                DateTime.SpecifyKind(reservation.ExpiresAtUtc, DateTimeKind.Utc)),
            Snapshot: snapshot with { MeasuredAtUtc = nowOffset });
    }

    private async Task<TransferCapacitySnapshot> ReadSnapshotAsync(
        DateTime nowUtc,
        CancellationToken ct)
    {
        var physicalAvailable = _volume.AvailableFreeSpaceBytes;
        var physicalTotal = _volume.TotalSizeBytes;
        var margin = TransferCapacityMath.CalculateGlobalSafetyMarginBytes(physicalTotal, _options);

        var outstandingReservations = _db.MigrationStorageReservations
            .Where(r => r.ReleasedAtUtc == null
                && (r.ClaimedJobId != null || r.ExpiresAtUtc > nowUtc));

        var activeCount = await outstandingReservations.CountAsync(ct);
        var outstanding = await outstandingReservations.SumAsync(
            r => r.ReservedBytes > r.MaterializedBytes
                ? r.ReservedBytes - r.MaterializedBytes
                : 0L,
            ct);

        return new TransferCapacitySnapshot(
            PhysicalAvailableBytes: physicalAvailable,
            PhysicalTotalBytes: physicalTotal,
            GlobalSafetyMarginBytes: margin,
            OutstandingReservedBytes: outstanding,
            ActiveReservationCount: activeCount,
            UsableAvailableBytes: physicalAvailable - margin - outstanding,
            MeasuredAtUtc: new DateTimeOffset(nowUtc, TimeSpan.Zero));
    }

    private async Task<IDbContextTransaction> BeginAdmissionTransactionAsync(CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection is SqliteConnection sqlite)
        {
            if (sqlite.State != System.Data.ConnectionState.Open)
            {
                await sqlite.OpenAsync(ct);
            }

            // BEGIN IMMEDIATE: acquire the write lock before reading capacity so
            // two competing admissions serialize at the database, never at an
            // in-process lock.
            var sqliteTransaction = sqlite.BeginTransaction(
                System.Data.IsolationLevel.Serializable,
                deferred: false);
            var contextTransaction = await _db.Database.UseTransactionAsync(sqliteTransaction, ct);
            return contextTransaction
                ?? throw new InvalidOperationException(
                    "Could not enlist the SQLite admission transaction on the transfer context.");
        }

        return await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            ct);
    }

    private async Task<TransferReservationConflictKind> ClassifyClaimFailureAsync(
        Guid reservationId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var state = await ReadReservationStateAsync(reservationId, ct);
        if (state is null)
        {
            return TransferReservationConflictKind.NotFound;
        }

        if (state.ReleasedAtUtc is not null)
        {
            return TransferReservationConflictKind.Released;
        }

        // A claimed reservation is live regardless of the preflight clock; the
        // claim predicate can then only have failed because another job owns it.
        if (state.ClaimedJobId is not null)
        {
            return TransferReservationConflictKind.AlreadyClaimed;
        }

        return state.ExpiresAtUtc <= nowUtc
            ? TransferReservationConflictKind.Expired
            : TransferReservationConflictKind.AlreadyClaimed;
    }

    private async Task<TransferReservationConflictKind> ClassifyMaterializeFailureAsync(
        Guid reservationId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var state = await ReadReservationStateAsync(reservationId, ct);
        if (state is null)
        {
            return TransferReservationConflictKind.NotFound;
        }

        if (state.ReleasedAtUtc is not null)
        {
            return TransferReservationConflictKind.Released;
        }

        if (state.ClaimedJobId is null && state.ExpiresAtUtc <= nowUtc)
        {
            return TransferReservationConflictKind.Expired;
        }

        // The row is live, so the only remaining predicate that can have
        // failed is the materialized-bytes cap.
        return TransferReservationConflictKind.OverMaterialized;
    }

    private async Task<ReservationState?> ReadReservationStateAsync(
        Guid reservationId,
        CancellationToken ct) =>
        await _db.MigrationStorageReservations
            .AsNoTracking()
            .Where(r => r.Id == reservationId)
            .Select(r => new ReservationState(
                r.ReleasedAtUtc, r.ClaimedJobId, r.ExpiresAtUtc, r.ReservedBytes, r.MaterializedBytes))
            .FirstOrDefaultAsync(ct);

    private DateTime ClockUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private sealed record ReservationState(
        DateTime? ReleasedAtUtc,
        Guid? ClaimedJobId,
        DateTime ExpiresAtUtc,
        long ReservedBytes = 0,
        long MaterializedBytes = 0);
}

/// <summary>
/// Bounded retry policy for admission transactions. SQLite busy/locked and
/// PostgreSQL serialization/deadlock failures are expected under competing
/// admissions; anything else is rethrown immediately. Exhausting the budget
/// surfaces the last transient failure so the caller can translate it into a
/// typed contention result.
/// </summary>
internal static class TransferAdmissionRetry
{
    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    internal static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Action onRetry,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(onRetry);

        var wait = delay;
        if (wait is null)
        {
            wait = static (duration, token) => Task.Delay(duration, token);
        }
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(ct);
            }
            catch (Exception exception) when (
                attempt < MaxAttempts && IsTransientContention(exception))
            {
                onRetry();
                await wait(RetryDelay * attempt, ct);
            }
        }
    }

    /// <summary>
    /// SQLite 5/6 (busy/locked) and PostgreSQL 40001/40P01
    /// (serialization_failure/deadlock_detected), found anywhere in the
    /// exception chain. Provider types are matched structurally through
    /// <see cref="DbException.SqlState"/>, so Nostos.Product keeps no Npgsql
    /// dependency.
    /// </summary>
    internal static bool IsTransientContention(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite)
            {
                return sqlite.SqliteErrorCode is 5 or 6;
            }

            if (current is DbException db && db.SqlState is "40001" or "40P01")
            {
                return true;
            }
        }

        return false;
    }
}
