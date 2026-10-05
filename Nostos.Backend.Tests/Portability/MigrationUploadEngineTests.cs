using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class MigrationUploadEngineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chunks_complete_in_order_or_out_of_order_with_exact_bytes(bool reverse)
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(MigrationContractLimits.MinChunkBytes + 31);
        var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        foreach (var i in reverse ? new[] { 1, 0 } : new[] { 0, 1 }) (await h.Upload(job, session, bytes, i)).AlreadyPresent.Should().BeFalse();
        var complete = await h.Complete(job, session);
        complete.State.Should().Be(MigrationSessionState.Complete); complete.ReceivedChunks.Should().Equal(0, 1);
        (await File.ReadAllBytesAsync(h.Paths.GetUploadArchivePath(session.SessionId))).Should().Equal(bytes);
        File.Exists(h.Paths.GetUploadArchivePartPath(session.SessionId)).Should().BeFalse();
        var reservation = await h.WithDb(db => db.MigrationStorageReservations.SingleAsync());
        reservation.MaterializedBytes.Should().Be(bytes.Length); reservation.MaterializedBytes.Should().BeLessThanOrEqualTo(reservation.ReservedBytes);
        (await h.Complete(job, session)).Should().BeEquivalentTo(complete);
    }

    [Fact]
    public async Task Identical_duplicate_is_idempotent_and_does_not_rewrite_or_charge_twice()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        await h.Upload(job, session, bytes);
        var part = h.Paths.GetUploadArchivePartPath(session.SessionId);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(part, stamp);
        (await h.Upload(job, session, bytes)).AlreadyPresent.Should().BeTrue();
        File.GetLastWriteTimeUtc(part).Should().Be(stamp);
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync())).MaterializedBytes.Should().Be(bytes.Length);
        (await h.WithDb(db => db.MigrationChunkReceiptRecords.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Conflicting_duplicate_leaves_original_receipt_and_bytes()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        await h.Upload(job, session, bytes);
        var other = bytes.Select(b => (byte)(b ^ 255)).ToArray();
        await Expect(h.UploadRaw(job, session, 0, other, new(0, other.Length - 1, other.Length, MigrationEngineHarness.Hash(other))), MigrationTransferException.ChunkConflict);
        await h.Complete(job, session);
        (await File.ReadAllBytesAsync(h.Paths.GetUploadArchivePath(session.SessionId))).Should().Equal(bytes);
        (await h.WithDb(db => db.MigrationChunkReceiptRecords.SingleAsync())).Sha256.Should().Be(MigrationEngineHarness.Hash(bytes));
    }

    [Theory]
    [InlineData("short-non-final")]
    [InlineData("empty-final")]
    [InlineData("oversize")]
    [InlineData("hash")]
    [InlineData("index-negative")]
    [InlineData("index-high")]
    [InlineData("total")]
    [InlineData("start")]
    [InlineData("end")]
    public async Task Invalid_chunks_publish_no_bytes_or_receipts(string kind)
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(kind == "short-non-final" ? MigrationContractLimits.MinChunkBytes + 31 : 31);
        var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        var body = bytes.Take(31).ToArray();
        var m = new MigrationChunkMetadata(0, kind == "short-non-final" ? session.ChunkSize - 1 : 30, bytes.Length, MigrationEngineHarness.Hash(body));
        if (kind == "empty-final") body = Array.Empty<byte>();
        if (kind == "oversize") body = MigrationEngineHarness.Bytes(32);
        if (kind == "hash") m = m with { Sha256 = new string('0', 64) };
        if (kind == "total") m = m with { Total = 999 };
        if (kind == "start") m = m with { Start = 1 };
        if (kind == "end") m = m with { End = 31 };
        await Expect(h.UploadRaw(job, session, kind == "index-negative" ? -1 : kind == "index-high" ? 1 : 0, body, m),
            kind == "hash" ? MigrationTransferException.HashMismatch : MigrationTransferException.RangeInvalid);
        (await h.Status(job, session)).ReceivedChunkCount.Should().Be(0);
        (await h.WithDb(db => db.MigrationSessionRecords.SingleAsync())).ReceivedBytes.Should().Be(0);
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync())).MaterializedBytes.Should().Be(0);
        (await File.ReadAllBytesAsync(h.Paths.GetUploadArchivePartPath(session.SessionId))).Should().OnlyContain(b => b == 0);
        Directory.EnumerateFiles(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("expiry")]
    public async Task Closed_sessions_reject_writes(string kind)
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        if (kind == "complete") { await h.Upload(job, session, bytes); await h.Complete(job, session); }
        if (kind == "cancel") await h.WithUploads(s => s.CancelAsync(job, new(), default));
        if (kind == "expiry") { h.Clock.Advance(TimeSpan.FromDays(1)); await h.Sweep(); }
        await Expect(h.Upload(job, session, bytes), kind == "expiry" ? MigrationTransferException.Expired : MigrationTransferException.InvalidState);
        if (kind != "complete") (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync())).ReleasedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Restart_reports_exact_receipts_and_accepts_only_missing_chunks()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(MigrationContractLimits.MinChunkBytes + 31); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        await h.Upload(job, session, bytes, 1); await h.RestartAsync();
        (await h.Status(job, session)).ReceivedChunks.Should().Equal(1);
        (await h.StartAsync(job, bytes)).SessionId.Should().Be(session.SessionId);
        await h.Upload(job, session, bytes, 0); (await h.Complete(job, session)).ReceivedChunks.Should().Equal(0, 1);
    }

    [Fact]
    public async Task Session_idempotency_replays_or_returns_typed_conflict_and_mismatched_resume_is_rejected()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var r = MigrationEngineHarness.Request(bytes);
        var first = await h.WithUploads(s => s.CreateSessionAsync(job, r, default));
        var replay = await h.WithUploads(s => s.CreateSessionAsync(job, r, default));
        replay.WasReplay.Should().BeTrue(); replay.Resource!.SessionId.Should().Be(first.Resource!.SessionId);
        var different = r with { FileIdentity = r.FileIdentity with { Sha256Checksum = new string('a', 64) } };
        (await h.WithUploads(s => s.CreateSessionAsync(job, different, default))).IsConflict.Should().BeTrue();
        await Expect(h.WithUploads(s => s.CreateSessionAsync(job, different with { IdempotencyKey = "another" }, default)), MigrationTransferException.IdentityMismatch);
        (await h.WithDb(db => db.MigrationSessionRecords.CountAsync())).Should().Be(1);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("identity-size")]
    [InlineData("identity-hash")]
    [InlineData("chunks")]
    [InlineData("overflow")]
    [InlineData("chunk-size")]
    [InlineData("total")]
    public async Task Invalid_session_payloads_allocate_nothing(string kind)
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var job = await h.NewJobAsync(); var r = MigrationEngineHarness.Request(MigrationEngineHarness.Bytes());
        r = kind switch { "key" => r with { IdempotencyKey = " " }, "identity-size" => r with { FileIdentity = r.FileIdentity with { TotalSizeBytes = 8 } },
            "identity-hash" => r with { FileIdentity = r.FileIdentity with { Sha256Checksum = "bad" } }, "chunks" => r with { TotalChunks = 2 },
            "overflow" => r with { TotalBytes = long.MaxValue }, "chunk-size" => r with { ChunkSize = 1 }, _ => r with { TotalBytes = 0 } };
        await Expect(h.WithUploads(s => s.CreateSessionAsync(job, r, default)), MigrationTransferException.InvalidRequest);
        (await h.WithDb(db => db.MigrationStorageReservations.CountAsync())).Should().Be(0);
        Directory.Exists(h.Paths.GetUploadsRoot()).Should().BeFalse();
    }

    [Fact]
    public async Task Largest_archive_arithmetic_does_not_allocate_whole_file_memory()
    {
        await using var h = new MigrationEngineHarness(); h.Volume.AvailableFreeSpaceBytes = 1024L * 1024 * 1024 * 1024; await h.InitializeAsync();
        var job = await h.NewJobAsync(); var size = MigrationContractLimits.MaxArchiveBytes;
        var r = new MigrationSessionRequest(MigrationSessionPurpose.Import, size, MigrationContractLimits.MaxChunkBytes,
            (int)(size / MigrationContractLimits.MaxChunkBytes), new(size, new string('a', 64)), "huge");
        var session = (await h.WithUploads(s => s.CreateSessionAsync(job, r, default))).Resource!;
        new FileInfo(h.Paths.GetUploadArchivePartPath(session.SessionId)).Length.Should().Be(size);
        session.TotalChunks.Should().Be(8192); session.ReceivedChunkCount.Should().Be(0);
    }

    [Fact]
    public async Task Missing_chunk_blocks_completion()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(MigrationContractLimits.MinChunkBytes + 31); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        await h.Upload(job, session, bytes, 1);
        await Expect(h.Complete(job, session), MigrationTransferException.MissingChunks);
        (await h.Status(job, session)).State.Should().Be(MigrationSessionState.Receiving);
        File.Exists(h.Paths.GetUploadArchivePath(session.SessionId)).Should().BeFalse();
    }

    [Fact]
    public async Task Whole_file_identity_mismatch_fails_job_and_releases_all_resources()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes, new string('0', 64));
        await h.Upload(job, session, bytes);
        await Expect(h.Complete(job, session), MigrationTransferException.IdentityMismatch);
        (await h.WithJobs(s => s.GetAsync(job, default)))!.FailureCode.Should().Be(MigrationTransferException.IdentityMismatch);
        (await h.WithJobs(s => s.GetAsync(job, default)))!.State.Should().Be(MigrationJobState.Failed);
        Directory.Exists(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().BeFalse();
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync())).ReleasedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Rename_before_commit_crash_is_recovered_by_identity_verification()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes); await h.Upload(job, session, bytes);
        File.Move(h.Paths.GetUploadArchivePartPath(session.SessionId), h.Paths.GetUploadArchivePath(session.SessionId));
        await h.RestartAsync(); (await h.Complete(job, session)).State.Should().Be(MigrationSessionState.Complete);
    }

    [Fact]
    public async Task Cancel_then_retry_reuses_verified_chunks_only_after_new_admission()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(MigrationContractLimits.MinChunkBytes + 31); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes); await h.Upload(job, session, bytes, 1);
        await h.WithUploads(s => s.CancelAsync(job, new(), default));
        await h.WithJobs(s => s.RetryAsync(job, new(), default));
        var resumed = await h.StartAsync(job, bytes); resumed.ReceivedChunks.Should().Equal(1);
        await h.Upload(job, resumed, bytes, 0); await h.Complete(job, resumed);
        var reservations = await h.WithDb(db => db.MigrationStorageReservations.AsNoTracking().ToListAsync());
        reservations.Count(r => r.ReleasedAtUtc == null).Should().Be(1);
        reservations.Single(r => r.ReleasedAtUtc == null).MaterializedBytes.Should().Be(bytes.Length);
    }

    [Fact]
    public async Task Disk_full_during_temp_write_has_no_receipt_and_fails_with_storage_code()
    {
        await using var h = new MigrationEngineHarness(); h.Configure = s => s.AddScoped<FileMigrationUploadStore, FullDiskStore>(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        await Expect(h.Upload(job, session, bytes), MigrationTransferException.StorageExhausted);
        (await h.Status(job, session)).ReceivedChunkCount.Should().Be(0);
        (await h.WithDb(db => db.MigrationSessionRecords.SingleAsync())).ReceivedBytes.Should().Be(0);
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync())).ReleasedAtUtc.Should().NotBeNull();
        Directory.Exists(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().BeFalse();
    }

    [Fact]
    public async Task Request_abort_during_chunk_body_leaves_the_job_resumable()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(32);
        var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        using var aborted = new CancellationTokenSource();

        Func<Task> upload = () => h.WithUploads(s => s.UploadChunkAsync(job, session.SessionId, 0,
            new MigrationChunkMetadata(0, bytes.Length - 1, bytes.Length, MigrationEngineHarness.Hash(bytes)),
            new DisconnectingStream(aborted), aborted.Token));

        await upload.Should().ThrowAsync<OperationCanceledException>();
        var record = (await h.WithJobs(s => s.GetAsync(job, default)))!;
        record.State.Should().Be(MigrationJobState.Pending,
            "a client abort is an interrupted transfer, not a storage failure");
        record.FailureCode.Should().BeNull();
        (await h.Status(job, session)).ReceivedChunkCount.Should().Be(0);

        // The same session resumes normally: the missing chunk uploads and completes.
        (await h.Upload(job, session, bytes)).ChunkIndex.Should().Be(0);
        (await h.Complete(job, session)).State.Should().Be(MigrationSessionState.Complete);
    }

    [Fact]
    public async Task Reservation_exhaustion_creates_no_session_or_file()
    {
        await using var h = new MigrationEngineHarness(); h.Volume.AvailableFreeSpaceBytes = 1; await h.InitializeAsync(); var job = await h.NewJobAsync();
        await Expect(h.StartAsync(job, MigrationEngineHarness.Bytes()), MigrationTransferException.StorageExhausted);
        (await h.WithDb(db => db.MigrationSessionRecords.CountAsync())).Should().Be(0);
        Directory.Exists(h.Paths.GetUploadsRoot()).Should().BeFalse();
    }

    [Fact]
    public async Task Parallel_same_chunk_writers_publish_exactly_one_receipt()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => h.Upload(job, session, bytes))));
        results.Count(r => !r.AlreadyPresent).Should().Be(1); results.Count(r => r.AlreadyPresent).Should().Be(3);
        await h.Complete(job, session);
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync())).MaterializedBytes.Should().Be(bytes.Length);
    }

    [Fact]
    public async Task Chunk_racing_cancel_never_leaves_an_active_session_or_torn_receipt()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        var upload = Task.Run(async () => { try { await h.Upload(job, session, bytes); } catch (MigrationTransferException e) { e.Code.Should().Be(MigrationTransferException.InvalidState); } });
        var cancel = Task.Run(() => h.WithUploads(s => s.CancelAsync(job, new(), default)));
        await Task.WhenAll(upload, cancel);
        (await h.Status(job, session)).State.Should().Be(MigrationSessionState.Cancelled);
        var receipt = await h.WithDb(db => db.MigrationChunkReceiptRecords.SingleOrDefaultAsync());
        if (receipt is not null) (await File.ReadAllBytesAsync(h.Paths.GetUploadArchivePartPath(session.SessionId))).Should().Equal(bytes);
        await Expect(h.Upload(job, session, bytes), MigrationTransferException.InvalidState);
    }

    [Fact]
    public async Task Chunk_racing_completion_has_one_legal_outcome_and_can_complete_on_retry()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        await Task.WhenAll(Task.Run(() => h.Upload(job, session, bytes)), Task.Run(async () =>
        { try { await h.Complete(job, session); } catch (MigrationTransferException e) { e.Code.Should().Be(MigrationTransferException.MissingChunks); } }));
        (await h.Complete(job, session)).State.Should().Be(MigrationSessionState.Complete);
        (await File.ReadAllBytesAsync(h.Paths.GetUploadArchivePath(session.SessionId))).Should().Equal(bytes);
    }

    [Fact]
    public async Task Stream_only_contract_rejects_missing_checksum_metadata()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var job = await h.NewJobAsync(); var bytes = MigrationEngineHarness.Bytes(); var session = await h.StartAsync(job, bytes);
        await Expect(h.WithUploads(s => s.UploadChunkAsync(job, session.SessionId, 0, new AsyncOnlyStream(bytes), default)), MigrationTransferException.MetadataRequired);
        (await h.Status(job, session)).ReceivedChunkCount.Should().Be(0);
    }
    [Fact]
    public async Task Completion_racing_cancel_never_rewrites_cancelled_history()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes); await h.Upload(job, session, bytes);
        await Task.WhenAll(Task.Run(async () =>
        { try { await h.Complete(job, session); } catch (MigrationTransferException e) { e.Code.Should().Be(MigrationTransferException.InvalidState); } }),
            Task.Run(() => h.WithUploads(s => s.CancelAsync(job, new(), default))));
        (await h.WithJobs(s => s.GetAsync(job, default)))!.State.Should().Be(MigrationJobState.Cancelled);
        (await h.Status(job, session)).State.Should().Be(MigrationSessionState.Cancelled);
        await Expect(h.Upload(job, session, bytes), MigrationTransferException.InvalidState);
        var path = File.Exists(h.Paths.GetUploadArchivePartPath(session.SessionId)) ? h.Paths.GetUploadArchivePartPath(session.SessionId) : h.Paths.GetUploadArchivePath(session.SessionId);
        (await File.ReadAllBytesAsync(path)).Should().Equal(bytes);
    }

    [Fact]
    public async Task Parallel_conflicting_writers_leave_bytes_matching_exactly_one_receipt()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var first = MigrationEngineHarness.Bytes(); var second = first.Select(b => (byte)(b ^ 255)).ToArray();
        var job = await h.NewJobAsync(); var session = await h.StartAsync(job, first);
        var accepted = 0; var conflicts = 0;
        await Task.WhenAll(new[] { first, second }.Select(body => Task.Run(async () =>
        {
            try { await h.UploadRaw(job, session, 0, body, new(0, body.Length - 1, body.Length, MigrationEngineHarness.Hash(body))); Interlocked.Increment(ref accepted); }
            catch (MigrationTransferException e) { e.Code.Should().Be(MigrationTransferException.ChunkConflict); Interlocked.Increment(ref conflicts); }
        })));
        accepted.Should().Be(1); conflicts.Should().Be(1);
        var receipt = await h.WithDb(db => db.MigrationChunkReceiptRecords.SingleAsync());
        MigrationEngineHarness.Hash(await File.ReadAllBytesAsync(h.Paths.GetUploadArchivePartPath(session.SessionId))).Should().Be(receipt.Sha256);
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync())).MaterializedBytes.Should().Be(first.Length);
    }

    [Fact]
    public async Task Expired_session_is_typed_even_when_the_job_remains_alive()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == job).ExecuteUpdateAsync(s => s.SetProperty(j => j.ExpiresAtUtc, h.Clock.GetUtcNow().AddDays(2).UtcDateTime)));
        h.Clock.Advance(TimeSpan.FromDays(1));
        await Expect(h.Upload(job, session, bytes), MigrationTransferException.Expired);
        await Expect(h.Complete(job, session), MigrationTransferException.Expired);
        (await h.Status(job, session)).ReceivedChunkCount.Should().Be(0);
    }

    [Fact]
    public async Task Explicit_delete_then_retry_restarts_empty_without_trusting_retained_receipts()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes); await h.Upload(job, session, bytes);
        await h.WithUploads(s => s.DeleteSessionAsync(job, session.SessionId, default));
        await h.WithJobs(s => s.RetryAsync(job, new(), default)); var resumed = await h.StartAsync(job, bytes);
        resumed.SessionId.Should().Be(session.SessionId); resumed.ReceivedChunkCount.Should().Be(0);
        await h.Upload(job, resumed, bytes); (await h.Complete(job, resumed)).State.Should().Be(MigrationSessionState.Complete);
    }

    [Fact]
    public async Task Session_path_symlink_rejected_before_any_outside_bytes_change()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        var outside = Path.Combine(h.DirectoryPath, "outside.part"); await File.WriteAllTextAsync(outside, "sentinel");
        var part = h.Paths.GetUploadArchivePartPath(session.SessionId); File.Delete(part); File.CreateSymbolicLink(part, outside);
        await Assert.ThrowsAsync<TransferPathException>(() => h.Upload(job, session, bytes));
        (await File.ReadAllTextAsync(outside)).Should().Be("sentinel"); (await h.Status(job, session)).ReceivedChunkCount.Should().Be(0);
        File.Delete(part);
    }

    [Fact]
    public async Task Cancel_interrupts_a_paused_request_read_before_waiting_for_its_database_lock()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes);
        var entered = MigrationWorkerEngineTests.Signal();
        var upload = Task.Run(() => h.WithUploads(async s =>
        {
            await using var stream = new PausedRequest(entered);
            return await s.UploadChunkAsync(job, session.SessionId, 0, new(0, bytes.Length - 1, bytes.Length, MigrationEngineHarness.Hash(bytes)), stream, default);
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.WithUploads(s => s.CancelAsync(job, new(), default)).WaitAsync(TimeSpan.FromSeconds(10));
        await Expect(upload, MigrationTransferException.InvalidState);
        (await h.Status(job, session)).State.Should().Be(MigrationSessionState.Cancelled);
        (await h.Status(job, session)).ReceivedChunkCount.Should().Be(0);
    }

    [Fact]
    public async Task Completion_replay_remains_idempotent_after_worker_reaches_ready_to_activate()
    {
        await using var h = new MigrationEngineHarness(); h.Configure = s => s.AddSingleton<IMigrationPhaseHandler, SuccessfulPreparation>(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes); await h.Upload(job, session, bytes); await h.Complete(job, session);
        await h.Worker.RunCycleAsync(default); (await h.WithJobs(s => s.GetAsync(job, default)))!.State.Should().Be(MigrationJobState.ReadyToActivate);
        (await h.Complete(job, session)).State.Should().Be(MigrationSessionState.Complete);
    }
    private sealed class SuccessfulPreparation : IMigrationPhaseHandler
    { public bool CanHandle(MigrationDirection direction, MigrationJobState state) => true; public Task ExecuteAsync(MigrationPhaseContext context, CancellationToken ct) => Task.CompletedTask; }

    private sealed class PausedRequest(TaskCompletionSource entered) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A browser that reloads mid-upload: the request token is cancelled while
    /// Kestrel reads the body, and the read fails with an IOException.
    /// </summary>
    private sealed class DisconnectingStream(CancellationTokenSource requestAbort) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { requestAbort.Cancel(); throw new IOException("The client disconnected while sending the request body."); }
        public override int Read(byte[] buffer, int offset, int count)
        { requestAbort.Cancel(); throw new IOException("The client disconnected while sending the request body."); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FullDiskStore(TransferPathResolver paths) : FileMigrationUploadStore(paths)
    {
        protected override async ValueTask WriteChunkBytesAsync(FileStream output, ReadOnlyMemory<byte> bytes, CancellationToken ct)
        { await base.WriteChunkBytesAsync(output, bytes[..Math.Min(3, bytes.Length)], ct); throw new IOException("Simulated ENOSPC after partial temp write"); }
    }
    internal static async Task Expect(Task task, string code)
    { var error = await Assert.ThrowsAsync<MigrationTransferException>(async () => await task); error.Code.Should().Be(code); }
}
