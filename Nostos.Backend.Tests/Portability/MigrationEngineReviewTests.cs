using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Configuration;
using Nostos.Backend.Middleware;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class MigrationEngineReviewTests
{
    private static TaskCompletionSource Signal() => MigrationWorkerEngineTests.Signal();

    [Fact]
    public async Task Cross_process_file_exclusion_is_enforced_by_the_filesystem()
    {
        if (!OperatingSystem.IsLinux()) return; // Windows enforces FileShare.None in the kernel.
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var id = Guid.NewGuid(); var mutex = h.Provider.GetRequiredService<MigrationFileMutex>();
        await using (await mutex.EnterAsync(id, default))
            (await Probe()).Should().Be(1, "a separate process cannot acquire the held inode");
        (await Probe()).Should().Be(0, "disposing the handle releases cross-process exclusion");
        File.Exists(h.Paths.GetMigrationLockPath(id)).Should().BeTrue("mutex inodes must never be unlinked");

        async Task<int> Probe()
        {
            var start = new System.Diagnostics.ProcessStartInfo("flock") { UseShellExecute = false };
            start.ArgumentList.Add("--nonblock"); start.ArgumentList.Add(h.Paths.GetMigrationLockPath(id)); start.ArgumentList.Add("true");
            using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(); return process.ExitCode;
        }
    }

    [Fact]
    public async Task Cancelled_mutex_wait_releases_its_reference_and_disposal_is_idempotent()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var id = Guid.NewGuid(); var mutex = h.Provider.GetRequiredService<MigrationFileMutex>();
        var owner = await mutex.EnterAsync(id, default);
        using var cancelled = new CancellationTokenSource();
        var waiter = mutex.EnterAsync(id, cancelled.Token); waiter.IsCompleted.Should().BeFalse();
        cancelled.Cancel(); Func<Task> enter = async () => await waiter;
        await enter.Should().ThrowAsync<OperationCanceledException>();
        await owner.DisposeAsync(); await owner.DisposeAsync();
        await using var successor = await mutex.EnterAsync(id, default).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Unknown_scope_ids_cannot_create_filesystem_mutex_scopes()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var id = await h.NewJobAsync(); var unknown = Guid.NewGuid();
        Func<Task> complete = () => h.WithUploads(s => s.CompleteSessionAsync(id, unknown, default));
        await complete.Should().ThrowAsync<MigrationJobStoreException>();
        Func<Task> retry = () => h.WithJobs(s => s.RetryAsync(unknown, new(), default));
        await retry.Should().ThrowAsync<MigrationJobStoreException>();
        await h.RunCleanup(c => c.CleanupJobAsync(unknown, default));
        Directory.Exists(Path.GetDirectoryName(h.Paths.GetMigrationLockPath(id))).Should().BeFalse();
    }

    [Fact]
    public async Task Blocked_request_stream_does_not_block_an_unrelated_database_writer()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var id = await h.NewJobAsync(); var session = await h.StartAsync(id, bytes);
        var entered = Signal(); var release = Signal();
        await using var body = new GatedStream(bytes, entered, release);
        var upload = h.WithUploads(s => s.UploadChunkAsync(id, session.SessionId, 0,
            new(0, bytes.Length - 1, bytes.Length, MigrationEngineHarness.Hash(bytes)), body, default));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await h.NewJobAsync(MigrationDirection.Export).WaitAsync(TimeSpan.FromSeconds(2));
            upload.IsCompleted.Should().BeFalse("the client has not released its body");
        }
        finally { release.TrySetResult(); await upload; }
        (await h.Status(id, session)).ReceivedChunks.Should().Equal(0);
    }

    [Fact]
    public async Task Cancel_and_retry_cannot_adopt_a_request_from_the_old_attempt()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var id = await h.NewJobAsync(); var session = await h.StartAsync(id, bytes);
        var entered = Signal(); var release = Signal();
        await using var body = new GatedStream(bytes, entered, release);
        var oldUpload = h.WithUploads(s => s.UploadChunkAsync(id, session.SessionId, 0,
            new(0, bytes.Length - 1, bytes.Length, MigrationEngineHarness.Hash(bytes)), body, default));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Direct store cancellation simulates a different host: it does not
        // signal this process's upload cancellation registry.
        await h.WithJobs(s => s.CancelAsync(id, new(), default));
        await h.RunCleanup(c => c.CleanupJobAsync(id, default));
        await h.WithJobs(s => s.RetryAsync(id, new(), default)); await h.StartAsync(id, bytes);
        release.TrySetResult(); await MigrationUploadEngineTests.Expect(oldUpload, MigrationTransferException.InvalidState);
        (await h.Status(id, session)).ReceivedChunks.Should().BeEmpty();
        await h.Upload(id, session, bytes); await h.Complete(id, session);
        (await h.WithJobs(s => s.GetAsync(id, default)))!.FailureCode.Should().BeNull();
    }

    [Fact]
    public async Task Gated_large_file_hash_does_not_block_a_writer_and_rejects_late_chunks()
    {
        var entered = Signal(); var release = Signal();
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddScoped<FileMigrationUploadStore>(sp => new HashGateStore(sp.GetRequiredService<TransferPathResolver>(), entered, release));
        await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(2 * MigrationContractLimits.MinChunkBytes);
        var id = await h.NewJobAsync(); var session = await h.StartAsync(id, bytes);
        await h.Upload(id, session, bytes, 1); await h.Upload(id, session, bytes, 0);
        var complete = h.Complete(id, session);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await h.NewJobAsync(MigrationDirection.Export).WaitAsync(TimeSpan.FromSeconds(2));
            await MigrationUploadEngineTests.Expect(h.Upload(id, session, bytes), MigrationTransferException.InvalidState);
            complete.IsCompleted.Should().BeFalse();
        }
        finally { release.TrySetResult(); await complete; }
        (await h.Status(id, session)).State.Should().Be(MigrationSessionState.Complete);
    }

    [Fact]
    public async Task Exact_multiple_archive_accepts_a_full_sized_final_chunk()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(2 * MigrationContractLimits.MinChunkBytes);
        var id = await h.NewJobAsync(); var session = await h.StartAsync(id, bytes);
        session.TotalChunks.Should().Be(2);
        await h.Upload(id, session, bytes, 1); await h.Upload(id, session, bytes, 0); await h.Complete(id, session);
        (await File.ReadAllBytesAsync(h.Paths.GetUploadArchivePath(session.SessionId))).Should().Equal(bytes);
    }

    [Fact]
    public async Task Crash_after_flush_before_receipt_can_resend_different_bytes_after_restart()
    {
        var crash = true;
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddScoped<FileMigrationUploadStore>(sp => new FlushCrashStore(sp.GetRequiredService<TransferPathResolver>(), () => crash));
        await h.InitializeAsync(); var correct = MigrationEngineHarness.Bytes(); var id = await h.NewJobAsync(); var session = await h.StartAsync(id, correct);
        var old = correct.Select(b => (byte)(b ^ 0xff)).ToArray();
        Func<Task> interrupted = () => h.Upload(id, session, old);
        await interrupted.Should().ThrowAsync<SimulatedCrashException>();
        (await File.ReadAllBytesAsync(h.Paths.GetUploadArchivePartPath(session.SessionId))).Should().Equal(old);
        (await h.Status(id, session)).ReceivedChunks.Should().BeEmpty();
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync())).MaterializedBytes.Should().Be(0);
        crash = false; await h.RestartAsync();
        await h.Upload(id, session, correct); await h.Complete(id, session);
        (await h.Status(id, session)).ReceivedChunks.Should().Equal(0);
        (await File.ReadAllBytesAsync(h.Paths.GetUploadArchivePath(session.SessionId))).Should().Equal(correct);
    }

    [Fact]
    public async Task Crash_during_completion_keeps_a_durable_fence_and_restart_can_finish()
    {
        var crash = true;
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddScoped<FileMigrationUploadStore>(sp => new HashCrashStore(sp.GetRequiredService<TransferPathResolver>(), () => crash));
        await h.InitializeAsync(); var bytes = MigrationEngineHarness.Bytes(); var id = await h.NewJobAsync(); var session = await h.StartAsync(id, bytes); await h.Upload(id, session, bytes);
        Func<Task> interrupted = () => h.Complete(id, session);
        await interrupted.Should().ThrowAsync<SimulatedCrashException>();
        var record = await h.WithDb(db => db.MigrationSessionRecords.SingleAsync());
        record.StorageKey.Should().Be(h.Paths.GetUploadArchiveStorageKey(session.SessionId));
        record.State.Should().Be((int)MigrationSessionState.Receiving);
        await MigrationUploadEngineTests.Expect(h.Upload(id, session, bytes), MigrationTransferException.InvalidState);
        crash = false; await h.RestartAsync();
        (await h.Complete(id, session)).State.Should().Be(MigrationSessionState.Complete);
    }

    [Fact]
    public async Task Retained_complete_upload_restores_materialization_once_on_fresh_reservation()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var id = await h.NewJobAsync(); var session = await h.StartAsync(id, bytes); await h.Upload(id, session, bytes); await h.Complete(id, session);
        await h.WithDb(async db =>
        {
            await db.MigrationJobRecords.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, (int)MigrationJobState.Failed));
            await db.MigrationStorageReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.ReleasedAtUtc, h.Clock.GetUtcNow().UtcDateTime));
        });
        await h.WithJobs(s => s.RetryAsync(id, new(), default));
        (await h.StartAsync(id, bytes)).State.Should().Be(MigrationSessionState.Complete);
        await h.StartAsync(id, bytes);
        var reservations = await h.WithDb(db => db.MigrationStorageReservations.AsNoTracking().ToListAsync());
        reservations.Should().HaveCount(2);
        reservations.Single(r => r.ReleasedAtUtc == null).MaterializedBytes.Should().Be(bytes.Length);
    }

    [Fact]
    public async Task Slow_cleanup_hook_does_not_block_an_unrelated_database_writer()
    {
        var hook = new GatedCleanup();
        await using var h = new MigrationEngineHarness(); h.Configure = s => s.AddSingleton<IMigrationStagingCleanup>(hook); await h.InitializeAsync();
        var id = await TerminalWithStaging(h);
        var sweep = h.Sweep();
        try
        {
            await hook.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await h.NewJobAsync().WaitAsync(TimeSpan.FromSeconds(2));
            sweep.IsCompleted.Should().BeFalse();
            (await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == id))).State.Should().Be((int)MigrationJobState.Cancelled);
        }
        finally { hook.Release.TrySetResult(); await sweep; }
    }

    [Fact]
    public async Task Detached_deletion_does_not_block_writers_or_erase_a_retried_live_scope()
    {
        var entered = Signal(); var release = Signal();
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddScoped<FileMigrationUploadStore>(sp => new DeleteGateStore(sp.GetRequiredService<TransferPathResolver>(), entered, release));
        await h.InitializeAsync(); var bytes = MigrationEngineHarness.Bytes(); var id = await h.NewJobAsync(); var session = await h.StartAsync(id, bytes); await h.Upload(id, session, bytes);
        h.Clock.Advance(TimeSpan.FromDays(1)); var sweep = h.Sweep();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Exists(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().BeFalse();
            await h.NewJobAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await h.WithJobs(s => s.RetryAsync(id, new(), default)).WaitAsync(TimeSpan.FromSeconds(2));
            (await h.StartAsync(id, bytes)).ReceivedChunks.Should().BeEmpty();
        }
        finally { release.TrySetResult(); await sweep; }
        File.Exists(h.Paths.GetUploadArchivePartPath(session.SessionId)).Should().BeTrue("detached deletion cannot reach the fresh scope");
        await h.Upload(id, session, bytes); await h.Complete(id, session);
        await h.Sweep(); File.Exists(h.Paths.GetUploadArchivePath(session.SessionId)).Should().BeTrue();
    }

    [Fact]
    public async Task Detached_cleanup_yields_to_maintenance_then_resumes_its_durable_queue()
    {
        var entered = Signal(); var release = Signal();
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddScoped<FileMigrationUploadStore>(sp => new DeleteGateStore(sp.GetRequiredService<TransferPathResolver>(), entered, release));
        await h.InitializeAsync(); var id = await h.NewJobAsync(); var session = await h.StartAsync(id, MigrationEngineHarness.Bytes());
        h.Clock.Advance(TimeSpan.FromDays(1)); var sweep = h.CleanupWorker.RunBatchAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var entering = h.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation); entering.IsCompleted.Should().BeFalse();
        release.TrySetResult(); await sweep.WaitAsync(TimeSpan.FromSeconds(10));
        await using (await entering.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Directory.EnumerateDirectories(h.Paths.GetDetachedScopesRoot()).Should().ContainSingle();
            Directory.Exists(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().BeFalse();
        }
        await h.CleanupWorker.RunBatchAsync(default); Directory.EnumerateDirectories(h.Paths.GetDetachedScopesRoot()).Should().BeEmpty();
        await h.CleanupWorker.RunBatchAsync(default); Directory.EnumerateDirectories(h.Paths.GetDetachedScopesRoot()).Should().BeEmpty();
    }

    [Fact]
    public async Task Worker_long_phase_yields_at_checkpoint_disposes_scope_releases_lease_and_resumes()
    {
        var entered = Signal(); var checkpoint = Signal(); var executions = 0; Witness? witness = null;
        await using var h = new MigrationEngineHarness();
        h.Configure = s =>
        {
            s.AddScoped<Witness>();
            s.AddScoped<IMigrationPhaseHandler>(sp => new Handler(async (context, ct) =>
            {
                if (context.Job.State != MigrationJobState.Preparing) return;
                witness = sp.GetRequiredService<Witness>();
                if (Interlocked.Increment(ref executions) == 1)
                {
                    entered.TrySetResult(); await checkpoint.Task.WaitAsync(ct);
                    await context.ReportProgressAsync(new(MigrationProgressPhase.Preparing, 1, 2), ct);
                }
            }));
        };
        await h.InitializeAsync(); var id = await h.NewJobAsync(MigrationDirection.Export);
        var run = h.Worker.RunCycleAsync(default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var entering = h.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        try
        {
            entering.IsCompleted.Should().BeFalse(); witness!.Disposed.Should().BeFalse();
            checkpoint.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(10));
            await using (await entering.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                witness.Disposed.Should().BeTrue("the DI scope must close before shared admission releases");
                var job = await h.WithJobs(s => s.GetAsync(id, default));
                job!.State.Should().Be(MigrationJobState.Preparing); job.LeaseToken.Should().BeNull(); job.FailureCode.Should().BeNull();
            }
        }
        finally
        {
            checkpoint.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(10));
            if (entering.IsCompletedSuccessfully) await (await entering).DisposeAsync();
        }
        await h.Worker.RunCycleAsync(default); executions.Should().Be(2);
        (await h.WithJobs(s => s.GetAsync(id, default)))!.State.Should().Be(MigrationJobState.Completed);
    }

    [Fact]
    public async Task Worker_does_not_scan_or_start_a_new_unit_while_maintenance_is_held()
    {
        var executions = 0;
        await using var h = new MigrationEngineHarness(); h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler((_, _) => { executions++; return Task.CompletedTask; }));
        await h.InitializeAsync(); var id = await h.NewJobAsync(MigrationDirection.Export);
        var exclusive = await h.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.BackupRestore);
        var run = h.Worker.RunCycleAsync(default);
        run.IsCompleted.Should().BeFalse(); executions.Should().Be(0);
        (await h.WithJobs(s => s.GetAsync(id, default)))!.LeaseToken.Should().BeNull();
        await exclusive.DisposeAsync(); await run.WaitAsync(TimeSpan.FromSeconds(10));
        executions.Should().Be(3);
    }

    [Fact]
    public async Task Sweep_yields_at_next_checkpoint_drains_scope_and_resumes_after_maintenance()
    {
        var hook = new GatedCleanup(); Witness? witness = null;
        await using var h = new MigrationEngineHarness();
        h.Configure = s => { s.AddScoped<Witness>(); s.AddScoped<IMigrationStagingCleanup>(sp => { witness = sp.GetRequiredService<Witness>(); return hook; }); };
        await h.InitializeAsync(); await TerminalWithStaging(h);
        var sweep = h.CleanupWorker.RunBatchAsync(default); await hook.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var entering = h.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        entering.IsCompleted.Should().BeFalse(); witness!.Disposed.Should().BeFalse();
        hook.Release.TrySetResult(); await sweep.WaitAsync(TimeSpan.FromSeconds(10));
        var exclusive = await entering.WaitAsync(TimeSpan.FromSeconds(10)); witness.Disposed.Should().BeTrue();
        hook.AbandonedCalls.Should().Be(0, "the sweep yields before starting another cleanup unit");
        var next = h.CleanupWorker.RunBatchAsync(default); next.IsCompleted.Should().BeFalse();
        await exclusive.DisposeAsync(); await next.WaitAsync(TimeSpan.FromSeconds(10)); hook.AbandonedCalls.Should().Be(1);
        await h.CleanupWorker.RunBatchAsync(default); hook.AbandonedCalls.Should().Be(2);
    }

    [Fact]
    public async Task Production_middleware_covers_upload_body_and_disposes_request_scope_before_drain()
    {
        var entered = Signal(); var release = Signal(); Witness? witness = null;
        await using var h = new MigrationEngineHarness(); h.Configure = s => s.AddScoped<Witness>(); await h.InitializeAsync();
        var bytes = MigrationEngineHarness.Bytes(); var id = await h.NewJobAsync(); var session = await h.StartAsync(id, bytes);
        var middleware = new LibraryMaintenanceMiddleware(async context =>
        {
            witness = context.RequestServices.GetRequiredService<Witness>();
            await context.RequestServices.GetRequiredService<ISelfHostedMigrationUploads>().UploadChunkAsync(id, session.SessionId, 0,
                new(0, bytes.Length - 1, bytes.Length, MigrationEngineHarness.Hash(bytes)), context.Request.Body, context.RequestAborted);
        });
        var context = new DefaultHttpContext { RequestServices = h.Provider };
        context.Request.Method = "PUT"; context.Request.Path = $"/api/migration/jobs/{id}/sessions/{session.SessionId}/chunks/0";
        await using var body = new GatedStream(bytes, entered, release); context.Request.Body = body;
        var request = middleware.InvokeAsync(context, h.Maintenance, new McpOptions());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var entering = h.Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation); entering.IsCompleted.Should().BeFalse();
        var denied = new DefaultHttpContext { RequestServices = h.Provider }; denied.Request.Path = context.Request.Path; denied.Request.Method = "PUT"; denied.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(denied, h.Maintenance, new McpOptions()); denied.Response.StatusCode.Should().Be(503);
        release.TrySetResult(); await request.WaitAsync(TimeSpan.FromSeconds(10));
        await using (await entering.WaitAsync(TimeSpan.FromSeconds(10))) witness!.Disposed.Should().BeTrue();
        (await h.Status(id, session)).ReceivedChunks.Should().Equal(0);
    }

    [Fact]
    public async Task Slow_phase_attempt_IO_does_not_hold_a_writer_and_expiry_prevents_publication()
    {
        var entered = Signal(); var release = Signal(); var published = 0;
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler(async (context, ct) =>
        {
            await context.ExecuteWriteAsync(async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); }, ct);
            await context.ExecuteMutationAsync((_, _) => { published++; return Task.CompletedTask; }, ct);
        }));
        await h.InitializeAsync(); var id = await h.NewJobAsync(MigrationDirection.Export);
        var run = h.Worker.RunCycleAsync(default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.NewJobAsync().WaitAsync(TimeSpan.FromSeconds(2));
        // Advance expiry in the DB without firing a heartbeat: the post-IO
        // checkpoint must independently reject this owner before publication.
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAtUtc, h.Clock.GetUtcNow().UtcDateTime)));
        release.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(10)); published.Should().Be(0);
        (await h.WithJobs(s => s.GetAsync(id, default)))!.State.Should().Be(MigrationJobState.Preparing);
    }

    private static async Task<Guid> TerminalWithStaging(MigrationEngineHarness h)
    {
        var id = await h.NewJobAsync(); await h.StartAsync(id, MigrationEngineHarness.Bytes());
        var staging = Guid.NewGuid(); h.Paths.EnsureDirectoryExists(h.Paths.GetStagingDirectory(staging));
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.PreparedStagingId, staging)));
        await h.WithJobs(s => s.CancelAsync(id, new(), default)); return id;
    }

    private sealed class Handler(Func<MigrationPhaseContext, CancellationToken, Task> action) : IMigrationPhaseHandler
    { public bool CanHandle(MigrationDirection direction, MigrationJobState state) => true; public Task ExecuteAsync(MigrationPhaseContext context, CancellationToken ct) => action(context, ct); }
    private sealed class Witness : IDisposable
    { public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }
    private sealed class GatedCleanup : IMigrationStagingCleanup
    {
        internal TaskCompletionSource Entered { get; } = Signal(); internal TaskCompletionSource Release { get; } = Signal();
        internal int AbandonedCalls;
        public async Task DeleteAsync(PortableStagingId id, CancellationToken ct) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
        public Task CleanupAbandonedAsync(DateTimeOffset cutoff, IReadOnlySet<PortableStagingId> protectedIds, CancellationToken ct) { AbandonedCalls++; return Task.CompletedTask; }
    }
    private sealed class HashGateStore(TransferPathResolver paths, TaskCompletionSource entered, TaskCompletionSource release) : FileMigrationUploadStore(paths)
    { protected override async Task BeforeHashAsync(CancellationToken ct) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } }
    private sealed class DeleteGateStore(TransferPathResolver paths, TaskCompletionSource entered, TaskCompletionSource release) : FileMigrationUploadStore(paths)
    { protected override async Task BeforeScopeDeleteAsync(CancellationToken ct) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } }
    private sealed class FlushCrashStore(TransferPathResolver paths, Func<bool> crash) : FileMigrationUploadStore(paths)
    { protected override Task AfterChunkFlushAsync(CancellationToken ct) => crash() ? throw new SimulatedCrashException() : Task.CompletedTask; }
    private sealed class HashCrashStore(TransferPathResolver paths, Func<bool> crash) : FileMigrationUploadStore(paths)
    { protected override Task BeforeHashAsync(CancellationToken ct) => crash() ? throw new SimulatedCrashException() : Task.CompletedTask; }
    private sealed class SimulatedCrashException : Exception;
    private sealed class GatedStream(byte[] bytes, TaskCompletionSource entered, TaskCompletionSource release) : Stream
    {
        private readonly AsyncOnlyStream _inner = new(bytes);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { entered.TrySetResult(); await release.Task.WaitAsync(ct); return await _inner.ReadAsync(buffer, ct); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous request read");
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
