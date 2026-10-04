// Nostos.Product/Services/Portability/Transfers/TransferStorageCapacity.cs

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
}

/// <summary>
/// Typed failure for a reservation mutation that lost its exactly-once
/// predicate (already claimed, expired, released, or missing).
/// </summary>
public sealed class TransferReservationException : InvalidOperationException
{
    public TransferReservationException(
        TransferReservationConflictKind kind,
        Guid reservationId)
        : base($"Transfer reservation '{reservationId}' cannot be used: {kind}.")
    {
        Kind = kind;
        ReservationId = reservationId;
    }

    public TransferReservationConflictKind Kind { get; }

    public Guid ReservationId { get; }
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

    Task<TransferReservationResult> TryReserveAsync(
        long requiredBytes,
        MigrationSessionPurpose purpose,
        TimeSpan ttl,
        CancellationToken ct);

    /// <summary>
    /// Atomically claims an unexpired, unreleased, unclaimed reservation for a
    /// job. Exactly one concurrent caller can win; every loser receives a
    /// typed <see cref="TransferReservationException"/>.
    /// </summary>
    Task ClaimAsync(Guid reservationId, Guid jobId, CancellationToken ct);

    /// <summary>
    /// Records bytes that are now physically materialized, reducing the
    /// outstanding part of the reservation instead of double-counting them
    /// against <c>DriveInfo</c>.
    /// </summary>
    Task AddMaterializedBytesAsync(Guid reservationId, long deltaBytes, CancellationToken ct);

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
    /// <c>ContractRequiredBytes + EffectiveChunkBytes + FilesystemSafetyOverhead</c>
    /// where the overhead is the global margin plus the fixed per-job allowance.
    /// </summary>
    public static long CalculateHostPeakReservationBytes(
        long contractRequiredBytes,
        int effectiveChunkBytes,
        long physicalVolumeBytes,
        TransferStorageOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contractRequiredBytes);
        ArgumentNullException.ThrowIfNull(options);

        var chunkBytes = effectiveChunkBytes > 0 ? effectiveChunkBytes : options.ChunkBytes;
        return checked(
            contractRequiredBytes
            + chunkBytes
            + CalculateGlobalSafetyMarginBytes(physicalVolumeBytes, options)
            + PerJobOverheadBytes);
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
    private const int MaxContentionAttempts = 5;
    private static readonly TimeSpan ContentionRetryDelay = TimeSpan.FromMilliseconds(25);

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

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryReserveOnceAsync(requiredBytes, purpose, ttl, ct);
            }
            catch (Exception exception) when (
                attempt < MaxContentionAttempts && IsTransientContention(exception))
            {
                _db.ChangeTracker.Clear();
                await Task.Delay(ContentionRetryDelay * attempt, ct);
            }
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

        var current = await _db.MigrationStorageReservations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, ct);

        throw new TransferReservationException(
            current is null
                ? TransferReservationConflictKind.NotFound
                : current.ReleasedAtUtc is not null
                    ? TransferReservationConflictKind.Released
                    : current.ExpiresAtUtc <= now
                        ? TransferReservationConflictKind.Expired
                        : TransferReservationConflictKind.AlreadyClaimed,
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
                && r.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.MaterializedBytes, r => r.MaterializedBytes + deltaBytes)
                    .SetProperty(r => r.Version, r => r.Version + 1),
                ct);

        if (affected == 0)
        {
            throw new TransferReservationException(
                await ClassifyMissingAsync(reservationId, now, ct),
                reservationId);
        }
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

    private async Task<TransferCapacitySnapshot> ReadSnapshotAsync(
        DateTime nowUtc,
        CancellationToken ct)
    {
        var physicalAvailable = _volume.AvailableFreeSpaceBytes;
        var physicalTotal = _volume.TotalSizeBytes;
        var margin = TransferCapacityMath.CalculateGlobalSafetyMarginBytes(physicalTotal, _options);

        var outstandingReservations = _db.MigrationStorageReservations
            .Where(r => r.ReleasedAtUtc == null && r.ExpiresAtUtc > nowUtc);

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

    private async Task<TransferReservationConflictKind> ClassifyMissingAsync(
        Guid reservationId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var state = await _db.MigrationStorageReservations
            .AsNoTracking()
            .Where(r => r.Id == reservationId)
            .Select(r => new { r.ReleasedAtUtc, r.ExpiresAtUtc })
            .FirstOrDefaultAsync(ct);

        if (state is null)
        {
            return TransferReservationConflictKind.NotFound;
        }

        if (state.ReleasedAtUtc is not null)
        {
            return TransferReservationConflictKind.Released;
        }

        return state.ExpiresAtUtc <= nowUtc
            ? TransferReservationConflictKind.Expired
            : TransferReservationConflictKind.AlreadyClaimed;
    }

    private DateTime ClockUtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static bool IsTransientContention(Exception exception)
    {
        var sqlite = exception as SqliteException
            ?? (exception as DbUpdateException)?.InnerException as SqliteException;
        return sqlite is { SqliteErrorCode: 5 or 6 };
    }
}
