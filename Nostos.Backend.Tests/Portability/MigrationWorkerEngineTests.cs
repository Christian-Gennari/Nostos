using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class MigrationWorkerEngineTests
{
    [Theory]
    [InlineData(MigrationDirection.Import, MigrationJobState.ReadyToActivate)]
    [InlineData(MigrationDirection.Export, MigrationJobState.Completed)]
    public async Task Fake_archive_phases_follow_direction_and_persist_progress(MigrationDirection direction, MigrationJobState expected)
    {
        var phases = new List<MigrationJobState>();
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler(async (context, ct) =>
        {
            phases.Add(context.Job.State);
            await context.ReportProgressAsync(new(MigrationProgressPhase.Validating, 7, 7), ct);
            await context.ExecuteWriteAsync(token => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, ct);
        }));
        await h.InitializeAsync(); var id = await Runnable(h, direction);
        (await h.Worker.RunCycleAsync(default)).Should().Be(1);
        var job = await h.WithJobs(s => s.GetAsync(id, default)); job!.State.Should().Be(expected); job.LeaseToken.Should().BeNull();
        var record = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == id));
        record.ProgressBytesProcessed.Should().Be(7);
        phases.Should().Equal(direction == MigrationDirection.Import ? new[] { MigrationJobState.Validating }
            : new[] { MigrationJobState.Preparing, MigrationJobState.Transferring, MigrationJobState.Validating });
    }

    [Theory]
    [InlineData(MigrationDirection.Import, MigrationTransferException.ImportPreparationUnavailable)]
    [InlineData(MigrationDirection.Export, MigrationTransferException.ExportArtifactUnavailable)]
    public async Task Missing_integrations_fail_truthfully_and_release_resources(MigrationDirection direction, string code)
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var id = await Runnable(h, direction);
        await h.Worker.RunCycleAsync(default);
        var job = await h.WithJobs(s => s.GetAsync(id, default)); job!.State.Should().Be(MigrationJobState.Failed); job.FailureCode.Should().Be(code); job.LeaseToken.Should().BeNull();
        (await h.WithDb(db => db.MigrationStorageReservations.Where(r => r.ClaimedJobId == id).ToListAsync()))
            .Should().OnlyContain(r => r.ReleasedAtUtc != null);
    }

    [Fact]
    public async Task Import_waits_for_browser_chunks_without_holding_a_lease()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var id = await h.NewJobAsync(); await h.StartAsync(id, MigrationEngineHarness.Bytes());
        await h.Worker.RunCycleAsync(default);
        var job = await h.WithJobs(s => s.GetAsync(id, default)); job!.State.Should().Be(MigrationJobState.Transferring); job.LeaseToken.Should().BeNull();
        (await h.Worker.RunCycleAsync(default)).Should().Be(0);
    }

    [Fact]
    public async Task Single_database_lease_among_four_workers_with_independent_load_gates()
    {
        var entered = Signal(); var release = Signal(); var executions = 0;
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler(async (context, ct) =>
        { if (context.Job.State != MigrationJobState.Preparing) return; Interlocked.Increment(ref executions); entered.TrySetResult(); await release.Task.WaitAsync(ct); }));
        await h.InitializeAsync(); var id = await h.NewJobAsync(MigrationDirection.Export);
        var scopeFactory = h.Provider.GetRequiredService<IServiceScopeFactory>();
        var workers = Enumerable.Range(0, 4).Select(_ => new MigrationJobWorker(scopeFactory, h.Clock,
            new MigrationJobCancellationRegistry(), new MigrationProcessingSlots(Options.Create(h.Settings)), NullLogger<MigrationJobWorker>.Instance)).ToArray();
        var first = Task.Run(() => workers[0].RunCycleAsync(default)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(workers.Skip(1).Select(w => Task.Run(() => w.RunCycleAsync(default))));
        executions.Should().Be(1); release.SetResult(); await first;
        (await h.WithJobs(s => s.GetAsync(id, default)))!.State.Should().Be(MigrationJobState.Completed);
        foreach (var worker in workers) worker.Dispose();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Configured_concurrency_limits_execution_and_gives_independent_budgets(int max)
    {
        var entered = Signal(); var release = Signal(); var seen = new List<PortableArchiveBufferBudget>();
        await using var h = new MigrationEngineHarness(); h.Settings.MaxConcurrentJobs = max;
        h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler(async (context, ct) =>
        {
            if (context.Job.State != MigrationJobState.Preparing) return;
            lock (seen) { seen.Add(context.BufferBudget); if (seen.Count == max) entered.SetResult(); }
            await release.Task.WaitAsync(ct);
        }));
        await h.InitializeAsync(); await h.NewJobAsync(MigrationDirection.Export); await h.NewJobAsync(MigrationDirection.Export);
        var cycle = Task.Run(() => h.Worker.RunCycleAsync(default)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        seen.Should().HaveCount(max); seen.Distinct().Should().HaveCount(max);
        using (var buffer = seen[0].Rent(128 * 1024))
        { seen[0].CurrentBytes.Should().Be(128 * 1024); if (max == 2) seen[1].CurrentBytes.Should().Be(0); }
        release.SetResult(); (await cycle).Should().Be(max);
        (await h.WithDb(db => db.MigrationJobRecords.CountAsync(j => j.State == (int)MigrationJobState.Completed))).Should().Be(max);
    }

    [Fact]
    public async Task Heartbeat_renews_in_its_own_scope_using_fake_clock()
    {
        var entered = Signal(); var release = Signal(); var probe = new RenewalProbe();
        await using var h = new MigrationEngineHarness();
        h.Configure = s =>
        {
            s.AddDbContext<NostosDbContext>(o => o.AddInterceptors(probe));
            s.AddSingleton<IMigrationPhaseHandler>(new Handler(async (context, ct) =>
            { if (context.Job.State == MigrationJobState.Preparing) { entered.SetResult(); await release.Task.WaitAsync(ct); } }));
        };
        await h.InitializeAsync(); var id = await h.NewJobAsync(MigrationDirection.Export);
        var run = Task.Run(() => h.Worker.RunCycleAsync(default)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var before = await h.WithDb(db => db.MigrationJobRecords.AsNoTracking().SingleAsync());
        h.Clock.Advance(TimeSpan.FromSeconds(60)); await probe.Renewed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var after = await h.WithDb(db => db.MigrationJobRecords.AsNoTracking().SingleAsync());
        after.LeaseExpiresAtUtc.Should().Be(before.LeaseExpiresAtUtc!.Value.AddMinutes(1));
        after.HeartbeatAtUtc.Should().Be(h.Clock.GetUtcNow().UtcDateTime);
        release.SetResult(); await run;
        probe.AcquireContext.Should().NotBeNull();
        probe.AcquireContext!.Value.Should().NotBe(probe.RenewContext!.Value);
        (await h.WithJobs(s => s.GetAsync(id, default)))!.LeaseToken.Should().BeNull();
    }

    [Fact]
    public async Task Lease_loss_prevents_the_handlers_next_write_even_before_heartbeat()
    {
        var entered = Signal(); var release = Signal(); var writes = 0;
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler(async (context, ct) =>
        {
            await context.ExecuteWriteAsync(_ => { Interlocked.Increment(ref writes); return Task.CompletedTask; }, ct);
            entered.SetResult(); await release.Task.WaitAsync(ct);
            await context.ExecuteWriteAsync(_ => { Interlocked.Increment(ref writes); return Task.CompletedTask; }, ct);
        }));
        await h.InitializeAsync(); var id = await h.NewJobAsync(MigrationDirection.Export);
        var run = Task.Run(() => h.Worker.RunCycleAsync(default)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.MigrationLeaseToken, "successor")));
        release.SetResult(); await run; writes.Should().Be(1);
        var job = await h.WithJobs(s => s.GetAsync(id, default)); job!.State.Should().Be(MigrationJobState.Preparing); job.LeaseToken.Should().Be("successor");
    }

    [Fact]
    public async Task Heartbeat_loss_cancels_a_waiting_phase_without_rewriting_history()
    {
        var entered = Signal(); var stopped = Signal();
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler(async (_, ct) =>
        { entered.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); } finally { stopped.SetResult(); } }));
        await h.InitializeAsync(); var id = await h.NewJobAsync(MigrationDirection.Export);
        var run = Task.Run(() => h.Worker.RunCycleAsync(default)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.MigrationLeaseToken, "successor")));
        h.Clock.Advance(TimeSpan.FromSeconds(60)); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)); await run;
        (await h.WithJobs(s => s.GetAsync(id, default)))!.State.Should().Be(MigrationJobState.Preparing);
    }

    [Fact]
    public async Task Cancellation_mid_phase_is_prompt_and_cannot_publish_success()
    {
        var entered = Signal(); var stopped = Signal();
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler(async (_, ct) =>
        { entered.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); } finally { stopped.SetResult(); } }));
        await h.InitializeAsync(); var id = await h.NewJobAsync(MigrationDirection.Export);
        var run = Task.Run(() => h.Worker.RunCycleAsync(default)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.WithUploads(s => s.CancelAsync(id, new(), default)); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)); await run;
        var job = await h.WithJobs(s => s.GetAsync(id, default)); job!.State.Should().Be(MigrationJobState.Cancelled); job.FailureCode.Should().BeNull(); job.LeaseToken.Should().BeNull();
    }

    [Fact]
    public async Task Graceful_shutdown_releases_lease_and_restart_resumes_current_phase_idempotently()
    {
        var entered = Signal(); var stopped = Signal(); var once = true; var outputs = new HashSet<Guid>();
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler(async (context, ct) =>
        {
            if (context.Job.State == MigrationJobState.Preparing)
            {
                await context.ExecuteWriteAsync(_ => { outputs.Add(context.Job.Id); return Task.CompletedTask; }, ct);
                if (once) { once = false; entered.SetResult(); try { await Task.Delay(Timeout.Infinite, ct); } finally { stopped.SetResult(); } }
            }
        }));
        await h.InitializeAsync(); var id = await h.NewJobAsync(MigrationDirection.Export);
        await h.Worker.StartAsync(default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Worker.StopAsync(default); await stopped.Task;
        var job = await h.WithJobs(s => s.GetAsync(id, default)); job!.State.Should().Be(MigrationJobState.Preparing); job.LeaseToken.Should().BeNull();
        await h.RestartAsync(); await h.Worker.RunCycleAsync(default);
        (await h.WithJobs(s => s.GetAsync(id, default)))!.State.Should().Be(MigrationJobState.Completed); outputs.Should().ContainSingle();
    }

    [Fact]
    public async Task Crash_with_stale_lease_is_discovered_after_expiry_and_resumes_validation()
    {
        var phases = new List<MigrationJobState>();
        await using var h = new MigrationEngineHarness(); h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler((context, _) =>
        { phases.Add(context.Job.State); return Task.CompletedTask; }));
        await h.InitializeAsync(); var id = await Runnable(h, MigrationDirection.Import);
        var token = await h.WithJobs(s => s.TryAcquireLeaseAsync(id, MigrationJobWorker.LeaseDuration, default));
        foreach (var state in new[] { MigrationJobState.Preparing, MigrationJobState.Transferring, MigrationJobState.Validating })
            await h.WithJobs(s => s.TransitionAsync(id, state, token!, default));
        await h.RestartAsync(); (await h.Worker.RunCycleAsync(default)).Should().Be(0);
        h.Clock.Advance(MigrationJobWorker.LeaseDuration + TimeSpan.FromTicks(1));
        await h.Worker.RunCycleAsync(default);
        phases.Should().Equal(MigrationJobState.Validating);
        (await h.WithJobs(s => s.GetAsync(id, default)))!.State.Should().Be(MigrationJobState.ReadyToActivate);
    }

    [Fact]
    public async Task Failure_then_retry_runs_new_attempt_and_clears_typed_failure()
    {
        var fail = true;
        await using var h = new MigrationEngineHarness(); h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler((_, _) =>
        { if (fail) throw new IOException("ENOSPC"); return Task.CompletedTask; }));
        await h.InitializeAsync(); var id = await h.NewJobAsync(MigrationDirection.Export); await h.Worker.RunCycleAsync(default);
        (await h.WithJobs(s => s.GetAsync(id, default)))!.FailureCode.Should().Be(MigrationTransferException.StorageExhausted);
        await h.WithJobs(s => s.RetryAsync(id, new(), default)); fail = false;
        await h.Worker.RunCycleAsync(default); var job = await h.WithJobs(s => s.GetAsync(id, default));
        job!.State.Should().Be(MigrationJobState.Completed); job.FailureCode.Should().BeNull();
        (await h.WithDb(db => db.MigrationJobRecords.SingleAsync())).AttemptNumber.Should().Be(2);
    }

    [Fact]
    public async Task Pending_cancel_interrupts_new_requests_too_and_releases_its_registration_gate()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var id = await h.NewJobAsync();
        var registry = h.Provider.GetRequiredService<MigrationJobCancellationRegistry>();
        using var oldRequest = new CancellationTokenSource(); using var oldRegistration = registry.RegisterUpload(id, oldRequest);
        using (registry.BeginUploadCancellation(id))
        {
            oldRequest.IsCancellationRequested.Should().BeTrue();
            using var newRequest = new CancellationTokenSource(); using var newRegistration = registry.RegisterUpload(id, newRequest);
            newRequest.IsCancellationRequested.Should().BeTrue();
        }
        using var laterRequest = new CancellationTokenSource(); using var laterRegistration = registry.RegisterUpload(id, laterRequest);
        laterRequest.IsCancellationRequested.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guarded_phase_metadata_uses_the_fenced_context_and_commits_or_rolls_back(bool fail)
    {
        var staging = Guid.NewGuid();
        await using var h = new MigrationEngineHarness();
        h.Configure = s => s.AddSingleton<IMigrationPhaseHandler>(new Handler(async (context, ct) =>
        {
            await context.ExecuteMutationAsync(async (fencedDb, token) =>
            {
                await fencedDb.MigrationJobRecords.Where(j => j.Id == context.Job.Id && j.State == (int)context.Job.State
                    && j.MigrationLeaseToken == context.Job.LeaseToken)
                    .ExecuteUpdateAsync(set => set.SetProperty(j => j.PreparedStagingId, staging)
                        .SetProperty(j => j.PreparedImportMetadataJson, "fake-prepared-metadata")
                        .SetProperty(j => j.Version, j => j.Version + 1), token);
                if (fail) throw new IOException("Crash before metadata commit");
            }, ct);
        }));
        await h.InitializeAsync(); var id = await Runnable(h, MigrationDirection.Import);
        await h.Worker.RunCycleAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        var record = await h.WithDb(db => db.MigrationJobRecords.SingleAsync(j => j.Id == id));
        if (fail)
        {
            record.State.Should().Be((int)MigrationJobState.Failed); record.PreparedStagingId.Should().BeNull();
            record.PreparedImportMetadataJson.Should().BeNull(); record.FailureCode.Should().Be(MigrationTransferException.StorageExhausted);
        }
        else
        {
            record.State.Should().Be((int)MigrationJobState.ReadyToActivate); record.PreparedStagingId.Should().Be(staging);
            record.PreparedImportMetadataJson.Should().Be("fake-prepared-metadata");
        }
        record.MigrationLeaseToken.Should().BeNull();
    }

    [Fact]
    public async Task Takeover_between_acquisition_and_read_never_borrows_successors_token()
    {
        var executions = 0; var probe = new ReplaceAcquiredLeaseProbe();
        await using var h = new MigrationEngineHarness();
        h.Configure = s =>
        {
            s.AddDbContext<NostosDbContext>(o => o.AddInterceptors(probe));
            s.AddSingleton<IMigrationPhaseHandler>(new Handler((_, _) => { executions++; return Task.CompletedTask; }));
        };
        await h.InitializeAsync(); var id = await h.NewJobAsync(MigrationDirection.Export);
        await h.Worker.RunCycleAsync(default);
        probe.Replaced.Should().BeTrue(); executions.Should().Be(0);
        var job = await h.WithJobs(s => s.GetAsync(id, default)); job!.State.Should().Be(MigrationJobState.Pending);
        job.LeaseToken.Should().Be("successor");
    }

    [Fact]
    public async Task Successor_registration_cancels_old_owner_and_delayed_old_registration_cannot_cancel_successor()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var id = await h.NewJobAsync();
        var registry = h.Provider.GetRequiredService<MigrationJobCancellationRegistry>();
        await h.WithJobs(s => s.TryAcquireLeaseAsync(id, MigrationJobWorker.LeaseDuration, default));
        var oldExpiry = (await h.WithJobs(s => s.GetAsync(id, default)))!.LeaseExpiresAtUtc!.Value;
        using var oldSource = new CancellationTokenSource(); var oldRegistration = registry.Register(id, oldSource, oldExpiry);
        h.Clock.Advance(MigrationJobWorker.LeaseDuration + TimeSpan.FromTicks(1));
        await h.WithJobs(s => s.TryAcquireLeaseAsync(id, MigrationJobWorker.LeaseDuration, default));
        var newExpiry = (await h.WithJobs(s => s.GetAsync(id, default)))!.LeaseExpiresAtUtc!.Value;
        using var newSource = new CancellationTokenSource(); using var newRegistration = registry.Register(id, newSource, newExpiry);
        oldSource.IsCancellationRequested.Should().BeTrue(); newSource.IsCancellationRequested.Should().BeFalse();
        oldRegistration.Dispose();
        using var delayedSource = new CancellationTokenSource(); using var delayedRegistration = registry.Register(id, delayedSource, oldExpiry);
        delayedSource.IsCancellationRequested.Should().BeTrue(); newSource.IsCancellationRequested.Should().BeFalse();
        registry.Cancel(id); newSource.IsCancellationRequested.Should().BeTrue();
    }

    private static async Task<Guid> Runnable(MigrationEngineHarness h, MigrationDirection direction)
    {
        var id = await h.NewJobAsync(direction);
        if (direction == MigrationDirection.Import)
        { var bytes = MigrationEngineHarness.Bytes(); var session = await h.StartAsync(id, bytes); await h.Upload(id, session, bytes); await h.Complete(id, session); }
        else
        {
            await using var scope = h.Provider.CreateAsyncScope();
            var capacity = scope.ServiceProvider.GetRequiredService<Nostos.Backend.Services.Portability.Transfers.ITransferStorageCapacity>();
            var reservation = (await capacity.TryReserveAsync(1024, MigrationSessionPurpose.Export, TimeSpan.FromMinutes(15), default)).ReservationId!.Value;
            await capacity.ClaimAsync(reservation, id, default);
            await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.ReservationId, reservation).SetProperty(j => j.ReservedStorageBytes, 1024)));
        }
        return id;
    }
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Handler(Func<MigrationPhaseContext, CancellationToken, Task> action) : IMigrationPhaseHandler
    { public bool CanHandle(MigrationDirection direction, MigrationJobState state) => true; public Task ExecuteAsync(MigrationPhaseContext context, CancellationToken ct) => action(context, ct); }
    private sealed class ReplaceAcquiredLeaseProbe : DbCommandInterceptor
    {
        private int _replaced;
        internal bool Replaced => _replaced != 0;
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"HeartbeatAtUtc\" =") && Interlocked.Exchange(ref _replaced, 1) == 0)
            {
                // A real second connection changes the owner after acquisition
                // commits, before the worker's GetAsync executes.
                var options = new DbContextOptionsBuilder<NostosDbContext>().UseSqlite(command.Connection!.ConnectionString).Options;
                await using var db = new NostosDbContext(options);
                await db.MigrationJobRecords.ExecuteUpdateAsync(s => s.SetProperty(j => j.MigrationLeaseToken, "successor")
                    .SetProperty(j => j.Version, j => j.Version + 1), cancellationToken);
            }
            return result;
        }
    }
    private sealed class RenewalProbe : DbCommandInterceptor
    {
        private int _updates;
        internal Guid? AcquireContext { get; private set; }
        internal Guid? RenewContext { get; private set; }
        internal TaskCompletionSource Renewed { get; } = Signal();
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"HeartbeatAtUtc\" ="))
            {
                if (Interlocked.Increment(ref _updates) == 1) AcquireContext = eventData.Context?.ContextId.InstanceId;
                else { RenewContext = eventData.Context?.ContextId.InstanceId; Renewed.TrySetResult(); }
            }
            return ValueTask.FromResult(result);
        }
    }
}
