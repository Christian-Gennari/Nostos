using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

// Slice 3 of issue #679: durable, race-safe storage admission. Admission runs
// against a real SQLite database with real SQL predicates; the physical volume
// is a fake so a full disk is simulated without touching the developer machine.
public sealed class TransferStorageCapacityTests : IDisposable
{
    private readonly List<string> _databasePaths = new();
    private readonly FakeTransferVolume _volume = new()
    {
        AvailableFreeSpaceBytes = 100_000,
        TotalSizeBytes = 1_000_000,
    };

    private readonly ManualTimeProvider _time =
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private readonly TransferStorageOptions _options = new()
    {
        DiskSafetyMarginBytes = 0,
        DiskSafetyMarginPercent = 0,
    };

    [Fact]
    public async Task Admission_accepts_the_exact_usable_boundary_and_rejects_one_byte_more()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        var capacity = CreateCapacity(db);

        var snapshot = await capacity.GetSnapshotAsync(default);
        snapshot.GlobalSafetyMarginBytes.Should().Be(0);
        snapshot.UsableAvailableBytes.Should().Be(100_000);

        var over = await capacity.TryReserveAsync(
            100_001, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        over.IsAdmitted.Should().BeFalse();
        over.ReservationId.Should().BeNull();

        var exact = await capacity.TryReserveAsync(
            100_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        exact.IsAdmitted.Should().BeTrue();
        exact.ReservationId.Should().NotBeNull();
        exact.ExpiresAtUtc.Should().Be(
            new DateTimeOffset(2026, 1, 1, 0, 15, 0, TimeSpan.Zero));

        await using var verify = new NostosDbContext(dbOptions);
        (await verify.MigrationStorageReservations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Outstanding_reservations_reduce_capacity_and_materialized_bytes_do_not_double_count()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        var capacity = CreateCapacity(db);

        var first = await capacity.TryReserveAsync(
            40_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        first.IsAdmitted.Should().BeTrue();

        var tooMuch = await capacity.TryReserveAsync(
            60_001, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        tooMuch.IsAdmitted.Should().BeFalse("the first reservation already owns 40,000 bytes");

        var second = await capacity.TryReserveAsync(
            60_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        second.IsAdmitted.Should().BeTrue();

        await capacity.AddMaterializedBytesAsync(first.ReservationId!.Value, 10_000, default);

        var snapshot = await capacity.GetSnapshotAsync(default);
        snapshot.OutstandingReservedBytes.Should().Be(90_000);
        snapshot.UsableAvailableBytes.Should().Be(10_000);

        var third = await capacity.TryReserveAsync(
            10_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        third.IsAdmitted.Should().BeTrue();

        var full = await capacity.GetSnapshotAsync(default);
        full.OutstandingReservedBytes.Should().Be(100_000);
        full.UsableAvailableBytes.Should().Be(0);
    }

    [Fact]
    public async Task Materialized_bytes_can_never_exceed_the_reservation()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        var capacity = CreateCapacity(db);

        var reserved = await capacity.TryReserveAsync(
            25_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        var reservationId = reserved.ReservationId!.Value;

        var over = () => capacity.AddMaterializedBytesAsync(reservationId, 25_001, default);
        (await over.Should().ThrowAsync<TransferReservationException>())
            .Which.Kind.Should().Be(TransferReservationConflictKind.OverMaterialized);

        await capacity.AddMaterializedBytesAsync(reservationId, 25_000, default);
        (await capacity.GetSnapshotAsync(default)).OutstandingReservedBytes.Should().Be(0);

        var duplicate = () => capacity.AddMaterializedBytesAsync(reservationId, 1, default);
        (await duplicate.Should().ThrowAsync<TransferReservationException>())
            .Which.Kind.Should().Be(TransferReservationConflictKind.OverMaterialized,
                "duplicate accounting must fail closed instead of overstating materialized bytes");
    }

    [Fact]
    public async Task Concurrent_claims_of_one_reservation_succeed_exactly_once()
    {
        var dbOptions = await CreateDatabaseAsync();
        Guid reservationId;
        await using (var seed = new NostosDbContext(dbOptions))
        {
            var capacity = CreateCapacity(seed);
            var reserved = await capacity.TryReserveAsync(
                10_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
            reservationId = reserved.ReservationId!.Value;
        }

        var firstJobId = Guid.NewGuid();
        var secondJobId = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> ClaimAsync(Guid jobId)
        {
            await using var db = new NostosDbContext(dbOptions);
            var capacity = CreateCapacity(db);
            await gate.Task;
            try
            {
                await capacity.ClaimAsync(reservationId, jobId, default);
                return true;
            }
            catch (TransferReservationException conflict)
            {
                conflict.ReservationId.Should().Be(reservationId);
                conflict.Kind.Should().Be(TransferReservationConflictKind.AlreadyClaimed);
                return false;
            }
        }

        var first = ClaimAsync(firstJobId);
        var second = ClaimAsync(secondJobId);
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Count(succeeded => succeeded).Should().Be(1);

        await using var verify = new NostosDbContext(dbOptions);
        var record = await verify.MigrationStorageReservations
            .AsNoTracking()
            .SingleAsync(r => r.Id == reservationId);
        record.ClaimedJobId.Should().Be(results[0] ? firstJobId : secondJobId);
        record.Version.Should().Be(1, "exactly one claim must mutate the concurrency token");
    }

    [Fact]
    public async Task Concurrent_reservations_cannot_both_spend_the_same_capacity()
    {
        var dbOptions = await CreateDatabaseAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<TransferReservationResult> ReserveAsync()
        {
            await using var db = new NostosDbContext(dbOptions);
            var capacity = CreateCapacity(db);
            await gate.Task;
            return await capacity.TryReserveAsync(
                60_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        }

        var first = ReserveAsync();
        var second = ReserveAsync();
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Count(result => result.IsAdmitted).Should().Be(
            1,
            "two competing reservations must serialize on the shared remaining capacity");

        await using var verify = new NostosDbContext(dbOptions);
        (await verify.MigrationStorageReservations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Release_restores_capacity_and_is_idempotent()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        var capacity = CreateCapacity(db);

        var reserved = await capacity.TryReserveAsync(
            50_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        (await capacity.GetSnapshotAsync(default)).UsableAvailableBytes.Should().Be(50_000);

        await capacity.ReleaseAsync(reserved.ReservationId!.Value, default);

        var afterRelease = await capacity.GetSnapshotAsync(default);
        afterRelease.OutstandingReservedBytes.Should().Be(0);
        afterRelease.UsableAvailableBytes.Should().Be(100_000);

        await capacity.ReleaseAsync(reserved.ReservationId.Value, default);
        await capacity.ReleaseAsync(Guid.NewGuid(), default);
        (await capacity.GetSnapshotAsync(default)).UsableAvailableBytes.Should().Be(100_000);
    }

    [Fact]
    public async Task Expired_reservations_stop_counting_and_cannot_be_claimed()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        var capacity = CreateCapacity(db);

        var reserved = await capacity.TryReserveAsync(
            50_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        reserved.IsAdmitted.Should().BeTrue();

        _time.Advance(TimeSpan.FromMinutes(16));

        var snapshot = await capacity.GetSnapshotAsync(default);
        snapshot.OutstandingReservedBytes.Should().Be(0, "the reservation expired");
        snapshot.ActiveReservationCount.Should().Be(0);
        snapshot.UsableAvailableBytes.Should().Be(100_000);

        var claim = () => capacity.ClaimAsync(reserved.ReservationId!.Value, Guid.NewGuid(), default);
        var conflict = await claim.Should().ThrowAsync<TransferReservationException>();
        conflict.Which.Kind.Should().Be(TransferReservationConflictKind.Expired);

        var materialize = () => capacity.AddMaterializedBytesAsync(reserved.ReservationId!.Value, 1, default);
        (await materialize.Should().ThrowAsync<TransferReservationException>())
            .Which.Kind.Should().Be(TransferReservationConflictKind.Expired);

        await capacity.ReleaseAsync(reserved.ReservationId!.Value, default);
        var afterRelease = await capacity.GetSnapshotAsync(default);
        afterRelease.OutstandingReservedBytes.Should().Be(0);
        afterRelease.UsableAvailableBytes.Should().Be(100_000);
    }

    [Fact]
    public async Task Claimed_reservations_survive_the_preflight_window_and_still_block_admission()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        var capacity = CreateCapacity(db);

        var reserved = await capacity.TryReserveAsync(
            50_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        await capacity.ClaimAsync(reserved.ReservationId!.Value, Guid.NewGuid(), default);

        _time.Advance(TimeSpan.FromMinutes(16));

        var snapshot = await capacity.GetSnapshotAsync(default);
        snapshot.OutstandingReservedBytes.Should().Be(
            50_000,
            "a claimed reservation is job-owned and must not expire on the preflight clock");
        snapshot.ActiveReservationCount.Should().Be(1);
        snapshot.UsableAvailableBytes.Should().Be(50_000);

        var over = await capacity.TryReserveAsync(
            50_001, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        over.IsAdmitted.Should().BeFalse(
            "the claimed reservation still owns the unmaterialized bytes");

        await capacity.AddMaterializedBytesAsync(reserved.ReservationId!.Value, 10_000, default);
        (await capacity.GetSnapshotAsync(default)).OutstandingReservedBytes.Should().Be(40_000);

        await capacity.ReleaseAsync(reserved.ReservationId!.Value, default);
        var afterRelease = await capacity.GetSnapshotAsync(default);
        afterRelease.OutstandingReservedBytes.Should().Be(0);
        afterRelease.UsableAvailableBytes.Should().Be(100_000);
    }

    [Fact]
    public async Task An_unexpired_unclaimed_reservation_can_be_claimed_exactly_once()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        var capacity = CreateCapacity(db);

        var reserved = await capacity.TryReserveAsync(
            25_000, MigrationSessionPurpose.Export, TimeSpan.FromMinutes(15), default);
        var jobId = Guid.NewGuid();

        await capacity.ClaimAsync(reserved.ReservationId!.Value, jobId, default);

        var secondClaim = () => capacity.ClaimAsync(reserved.ReservationId.Value, Guid.NewGuid(), default);
        (await secondClaim.Should().ThrowAsync<TransferReservationException>())
            .Which.Kind.Should().Be(TransferReservationConflictKind.AlreadyClaimed);

        await using var verify = new NostosDbContext(dbOptions);
        var record = await verify.MigrationStorageReservations
            .AsNoTracking()
            .SingleAsync(r => r.Id == reserved.ReservationId);
        record.ClaimedJobId.Should().Be(jobId);
        (await capacity.GetSnapshotAsync(default)).OutstandingReservedBytes.Should().Be(25_000);
    }

    [Fact]
    public async Task Released_reservations_fail_closed_for_claim_and_materialization()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        var capacity = CreateCapacity(db);

        var reserved = await capacity.TryReserveAsync(
            25_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        await capacity.ReleaseAsync(reserved.ReservationId!.Value, default);

        var claim = () => capacity.ClaimAsync(reserved.ReservationId!.Value, Guid.NewGuid(), default);
        (await claim.Should().ThrowAsync<TransferReservationException>())
            .Which.Kind.Should().Be(TransferReservationConflictKind.Released);

        var materialize = () => capacity.AddMaterializedBytesAsync(reserved.ReservationId!.Value, 1, default);
        (await materialize.Should().ThrowAsync<TransferReservationException>())
            .Which.Kind.Should().Be(TransferReservationConflictKind.Released);
    }

    [Fact]
    public async Task Unknown_reservation_mutations_fail_closed()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        var capacity = CreateCapacity(db);

        var unknown = Guid.NewGuid();

        var claim = () => capacity.ClaimAsync(unknown, Guid.NewGuid(), default);
        (await claim.Should().ThrowAsync<TransferReservationException>())
            .Which.Kind.Should().Be(TransferReservationConflictKind.NotFound);

        var materialize = () => capacity.AddMaterializedBytesAsync(unknown, 1, default);
        (await materialize.Should().ThrowAsync<TransferReservationException>())
            .Which.Kind.Should().Be(TransferReservationConflictKind.NotFound);
    }

    [Fact]
    public async Task Non_positive_requests_and_materialized_deltas_are_rejected()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        var capacity = CreateCapacity(db);

        var zeroBytes = () => capacity.TryReserveAsync(
            0, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        (await zeroBytes.Should().ThrowAsync<ArgumentOutOfRangeException>())
            .Which.ParamName.Should().Be("requiredBytes");

        var zeroTtl = () => capacity.TryReserveAsync(
            1, MigrationSessionPurpose.Import, TimeSpan.Zero, default);
        (await zeroTtl.Should().ThrowAsync<ArgumentOutOfRangeException>())
            .Which.ParamName.Should().Be("ttl");

        var reserved = await capacity.TryReserveAsync(
            25_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        var zeroDelta = () => capacity.AddMaterializedBytesAsync(reserved.ReservationId!.Value, 0, default);
        (await zeroDelta.Should().ThrowAsync<ArgumentOutOfRangeException>())
            .Which.ParamName.Should().Be("deltaBytes");
    }

    [Fact]
    public async Task A_full_volume_rejects_admission()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        var capacity = CreateCapacity(db);

        _volume.AvailableFreeSpaceBytes = 0;
        _volume.TotalSizeBytes = 0;

        var result = await capacity.TryReserveAsync(
            1, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);

        result.IsAdmitted.Should().BeFalse();
        var snapshot = await capacity.GetSnapshotAsync(default);
        snapshot.UsableAvailableBytes.Should().Be(0);
    }

    [Fact]
    public async Task The_global_safety_margin_is_respected_at_the_boundary()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        _options.DiskSafetyMarginBytes = 25_000;
        var capacity = CreateCapacity(db);

        var rejected = await capacity.TryReserveAsync(
            75_001, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        rejected.IsAdmitted.Should().BeFalse();

        var admitted = await capacity.TryReserveAsync(
            75_000, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
        admitted.IsAdmitted.Should().BeTrue();
    }

    [Fact]
    public async Task The_safety_margin_uses_the_larger_of_the_byte_floor_and_the_volume_percentage()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        _options.DiskSafetyMarginBytes = 1_000;
        _options.DiskSafetyMarginPercent = 5;
        _volume.AvailableFreeSpaceBytes = 1_000_000;
        _volume.TotalSizeBytes = 1_000_000;
        var capacity = CreateCapacity(db);

        var snapshot = await capacity.GetSnapshotAsync(default);

        snapshot.GlobalSafetyMarginBytes.Should().Be(50_000);
        snapshot.UsableAvailableBytes.Should().Be(950_000);
    }

    [Fact]
    public void Host_peak_reservation_includes_chunk_and_per_job_overhead_but_not_the_safety_margin()
    {
        _options.DiskSafetyMarginBytes = 1_000;
        _options.DiskSafetyMarginPercent = 5;

        var globalMargin = TransferCapacityMath.CalculateGlobalSafetyMarginBytes(
            1_000_000, _options);
        globalMargin.Should().Be(50_000);

        var withEffectiveChunk = TransferCapacityMath.CalculateHostPeakReservationBytes(
            contractRequiredBytes: 1_000,
            effectiveChunkBytes: 8 * 1024 * 1024,
            options: _options);
        withEffectiveChunk.Should().Be(
            1_000 + (8L * 1024 * 1024) + TransferCapacityMath.PerJobOverheadBytes);

        var withConfiguredChunk = TransferCapacityMath.CalculateHostPeakReservationBytes(
            contractRequiredBytes: 1_000,
            effectiveChunkBytes: 0,
            options: _options);
        withConfiguredChunk.Should().Be(
            1_000 + _options.ChunkBytes + TransferCapacityMath.PerJobOverheadBytes);
    }

    [Fact]
    public async Task Host_peak_plus_admission_charges_the_global_margin_exactly_once()
    {
        var dbOptions = await CreateDatabaseAsync();
        await using var db = new NostosDbContext(dbOptions);
        _options.DiskSafetyMarginBytes = 25_000_000;
        _options.DiskSafetyMarginPercent = 0;
        _volume.AvailableFreeSpaceBytes = 200_000_000;
        _volume.TotalSizeBytes = 200_000_000;
        var capacity = CreateCapacity(db);

        var snapshot = await capacity.GetSnapshotAsync(default);
        snapshot.GlobalSafetyMarginBytes.Should().Be(25_000_000);
        snapshot.UsableAvailableBytes.Should().Be(175_000_000);

        var hostPeak = TransferCapacityMath.CalculateHostPeakReservationBytes(
            contractRequiredBytes: 140_000_000,
            effectiveChunkBytes: 0,
            options: _options);
        hostPeak.Should().Be(
            140_000_000 + _options.ChunkBytes + TransferCapacityMath.PerJobOverheadBytes);

        var admitted = await capacity.TryReserveAsync(
            hostPeak, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);

        admitted.IsAdmitted.Should().BeTrue(
            "the helper excludes the global margin, which admission already subtracts once; " +
            "charging it in both places would reject this request");
    }

    private async Task<DbContextOptions<NostosDbContext>> CreateDatabaseAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nostos-679-capacity-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        await using var db = new NostosDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return options;
    }

    private TransferStorageCapacity CreateCapacity(NostosDbContext db) =>
        new(db, _volume, Options.Create(_options), _time);

    public void Dispose()
    {
        foreach (var path in _databasePaths)
        {
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                try
                {
                    File.Delete(path + suffix);
                }
                catch (IOException)
                {
                    // Best-effort cleanup only.
                }
            }
        }
    }
}
