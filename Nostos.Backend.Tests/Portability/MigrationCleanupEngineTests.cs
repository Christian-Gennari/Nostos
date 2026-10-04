using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class MigrationCleanupEngineTests
{
    [Fact]
    public async Task Unclaimed_reservation_releases_exactly_at_TTL_claimed_hold_survives_until_terminal()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); await h.StartAsync(job, bytes);
        Guid unclaimed;
        await using (var scope = h.Provider.CreateAsyncScope()) unclaimed = (await scope.ServiceProvider.GetRequiredService<ITransferStorageCapacity>()
            .TryReserveAsync(100, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default)).ReservationId!.Value;
        h.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1)); await h.Sweep();
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync(r => r.Id == unclaimed))).ReleasedAtUtc.Should().BeNull();
        h.Clock.Advance(TimeSpan.FromTicks(1)); await h.Sweep();
        var all = await h.WithDb(db => db.MigrationStorageReservations.ToListAsync());
        all.Single(r => r.Id == unclaimed).ReleasedAtUtc.Should().NotBeNull();
        all.Single(r => r.ClaimedJobId == job).ReleasedAtUtc.Should().BeNull();
        await h.Sweep(); all = await h.WithDb(db => db.MigrationStorageReservations.ToListAsync()); all.Count.Should().Be(2);
    }

    [Fact]
    public async Task Session_and_job_expire_at_TTL_and_cleanup_is_idempotent()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var bytes = MigrationEngineHarness.Bytes(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, bytes); await h.Upload(job, session, bytes);
        h.Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromTicks(1)); await h.Sweep();
        File.Exists(h.Paths.GetUploadArchivePartPath(session.SessionId)).Should().BeTrue();
        (await h.Status(job, session)).State.Should().Be(MigrationSessionState.Receiving);
        h.Clock.Advance(TimeSpan.FromTicks(1)); await h.Sweep();
        (await h.Status(job, session)).State.Should().Be(MigrationSessionState.Expired);
        (await h.WithJobs(s => s.GetAsync(job, default)))!.State.Should().Be(MigrationJobState.Expired);
        Directory.Exists(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().BeFalse();
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync())).ReleasedAtUtc.Should().NotBeNull();
        await h.Sweep(); (await h.WithDb(db => db.MigrationSessionRecords.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Live_lease_fences_expiry_cleanup_until_released()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, MigrationEngineHarness.Bytes());
        var token = await h.WithJobs(s => s.TryAcquireLeaseAsync(job, TimeSpan.FromDays(2), default));
        h.Clock.Advance(TimeSpan.FromDays(1)); await h.Sweep();
        Directory.Exists(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().BeTrue();
        (await h.WithJobs(s => s.GetAsync(job, default)))!.State.Should().Be(MigrationJobState.Pending);
        await h.WithJobs(s => s.ReleaseLeaseAsync(job, token!, default)); await h.Sweep();
        Directory.Exists(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().BeFalse();
    }

    [Fact]
    public async Task Renewed_job_and_session_are_never_cleaned_using_stale_cutoff()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, MigrationEngineHarness.Bytes());
        h.Clock.Advance(TimeSpan.FromHours(23));
        await h.WithDb(async db =>
        {
            await db.MigrationJobRecords.Where(j => j.Id == job).ExecuteUpdateAsync(s => s.SetProperty(j => j.ExpiresAtUtc, h.Clock.GetUtcNow().AddDays(1).UtcDateTime));
            await db.MigrationSessionRecords.Where(s => s.Id == session.SessionId).ExecuteUpdateAsync(s => s.SetProperty(s => s.ExpiresAtUtc, h.Clock.GetUtcNow().AddDays(1).UtcDateTime));
        });
        h.Clock.Advance(TimeSpan.FromHours(2)); await h.Sweep();
        Directory.Exists(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().BeTrue();
        (await h.WithJobs(s => s.GetAsync(job, default)))!.State.Should().Be(MigrationJobState.Pending);
    }

    [Fact]
    public async Task Orphan_upload_scope_has_a_TTL_grace_and_unknown_siblings_are_untouched()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var id = Guid.NewGuid(); var orphan = h.Paths.EnsureDirectoryExists(h.Paths.GetUploadSessionDirectory(id));
        await File.WriteAllTextAsync(h.Paths.VerifyPathWithinRoot(h.Paths.GetUploadArchivePartPath(id)), "orphan");
        Directory.SetLastWriteTimeUtc(orphan, h.Clock.GetUtcNow().UtcDateTime);
        var unknown = Path.Combine(h.Paths.GetUploadsRoot(), "operator-data"); Directory.CreateDirectory(unknown); var other = Path.Combine(unknown, "keep"); await File.WriteAllTextAsync(other, "keep");
        var rootSibling = Path.Combine(h.Paths.RootPath, "keep.txt"); await File.WriteAllTextAsync(rootSibling, "keep");
        h.Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromTicks(1)); await h.Sweep(); Directory.Exists(orphan).Should().BeTrue();
        h.Clock.Advance(TimeSpan.FromTicks(1)); await h.Sweep(); Directory.Exists(orphan).Should().BeFalse();
        File.Exists(other).Should().BeTrue(); File.Exists(rootSibling).Should().BeTrue(); await h.Sweep(); File.Exists(other).Should().BeTrue();
    }

    [Fact]
    public async Task Symlinked_scope_and_descendant_never_delete_outside_root()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var outside = Path.Combine(h.DirectoryPath, "outside"); Directory.CreateDirectory(outside); var sentinel = Path.Combine(outside, "sentinel"); await File.WriteAllTextAsync(sentinel, "safe");
        var id = Guid.NewGuid(); h.Paths.EnsureDirectoryExists(h.Paths.GetUploadsRoot());
        var link = h.Paths.GetUploadSessionDirectory(id); Directory.CreateSymbolicLink(link, outside);
        h.Clock.Advance(TimeSpan.FromDays(2)); await h.Sweep(); (await File.ReadAllTextAsync(sentinel)).Should().Be("safe");
        Directory.Delete(link);
        var job = await h.NewJobAsync(); var session = await h.StartAsync(job, MigrationEngineHarness.Bytes());
        var descendant = Path.Combine(h.Paths.GetUploadSessionDirectory(session.SessionId), "linked"); Directory.CreateSymbolicLink(descendant, outside);
        await h.WithJobs(s => s.CancelAsync(job, new(), default)); h.Clock.Advance(TimeSpan.FromDays(1)); await h.Sweep();
        (await File.ReadAllTextAsync(sentinel)).Should().Be("safe"); Directory.Delete(descendant);
        await h.Sweep(); Directory.Exists(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().BeFalse();
    }

    [Fact]
    public async Task Root_symlink_preserves_operator_target_and_only_generated_eligible_scopes_are_removed()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync();
        var actualRoot = Path.Combine(h.DirectoryPath, "operator-target"); Directory.Move(h.Paths.RootPath, actualRoot);
        Directory.CreateSymbolicLink(h.Paths.RootPath, actualRoot);
        var sentinel = Path.Combine(actualRoot, "sentinel"); await File.WriteAllTextAsync(sentinel, "keep");
        var job = await h.NewJobAsync(); var session = await h.StartAsync(job, MigrationEngineHarness.Bytes());
        h.Clock.Advance(TimeSpan.FromDays(1)); await h.Sweep();
        Directory.Exists(actualRoot).Should().BeTrue(); File.Exists(sentinel).Should().BeTrue();
        Directory.Exists(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().BeFalse();
        Directory.Delete(h.Paths.RootPath);
    }

    [Fact]
    public async Task Terminal_files_reservations_and_known_staging_are_removed_and_missing_files_are_success()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, MigrationEngineHarness.Bytes());
        var staging = Guid.NewGuid(); var stagePath = h.Paths.EnsureDirectoryExists(h.Paths.GetStagingDirectory(staging));
        await File.WriteAllTextAsync(Path.Combine(stagePath, "state.json"), "staged");
        var exportPath = h.Paths.EnsureDirectoryExists(h.Paths.GetExportDirectory(job)); await File.WriteAllTextAsync(h.Paths.GetExportTempPath(job), "tmp");
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == job).ExecuteUpdateAsync(s => s.SetProperty(j => j.PreparedStagingId, staging)));
        await h.WithUploads(s => s.DeleteSessionAsync(job, session.SessionId, default));
        Directory.Exists(stagePath).Should().BeFalse(); Directory.Exists(exportPath).Should().BeFalse();
        Directory.Exists(h.Paths.GetUploadSessionDirectory(session.SessionId)).Should().BeFalse();
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync())).ReleasedAtUtc.Should().NotBeNull(); await h.Sweep();
    }

    [Fact]
    public async Task Ready_to_activate_staging_retained_until_job_expiry_even_if_upload_TTL_elapses()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var job = await h.NewJobAsync(); var session = await h.StartAsync(job, MigrationEngineHarness.Bytes());
        var staging = Guid.NewGuid(); var stagePath = h.Paths.EnsureDirectoryExists(h.Paths.GetStagingDirectory(staging));
        var expires = h.Clock.GetUtcNow().AddDays(2).UtcDateTime;
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == job).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, (int)MigrationJobState.ReadyToActivate)
            .SetProperty(j => j.PreparedStagingId, staging).SetProperty(j => j.ExpiresAtUtc, expires)));
        h.Clock.Advance(TimeSpan.FromDays(1)); await h.Sweep(); Directory.Exists(stagePath).Should().BeTrue();
        (await h.WithJobs(s => s.GetAsync(job, default)))!.State.Should().Be(MigrationJobState.ReadyToActivate);
        h.Clock.Advance(TimeSpan.FromDays(1)); await h.Sweep(); Directory.Exists(stagePath).Should().BeFalse();
        (await h.WithJobs(s => s.GetAsync(job, default)))!.State.Should().Be(MigrationJobState.Expired);
    }

    [Fact]
    public async Task Available_export_survives_until_artifact_TTL_then_deletes_without_rewriting_completed_history()
    {
        await using var h = new MigrationEngineHarness(); await h.InitializeAsync(); var job = await h.NewJobAsync(MigrationDirection.Export);
        h.Paths.EnsureDirectoryExists(h.Paths.GetExportDirectory(job)); await File.WriteAllTextAsync(h.Paths.GetExportArtifactPath(job), "artifact");
        await h.WithDb(async db =>
        {
            await db.MigrationJobRecords.Where(j => j.Id == job).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, (int)MigrationJobState.Completed));
            db.MigrationExportArtifactRecords.Add(new() { JobId = job, State = (int)MigrationExportArtifactState.Available, StorageKey = h.Paths.GetExportArtifactStorageKey(job),
                FileName = "library.nostos", ContentType = "application/vnd.nostos.portable+zip", SizeBytes = 8, CreatedAtUtc = h.Clock.GetUtcNow().UtcDateTime,
                ExpiresAtUtc = h.Clock.GetUtcNow().AddDays(1).UtcDateTime }); await db.SaveChangesAsync();
        });
        h.Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromTicks(1)); await h.Sweep(); File.Exists(h.Paths.GetExportArtifactPath(job)).Should().BeTrue();
        h.Clock.Advance(TimeSpan.FromTicks(1)); await h.Sweep(); File.Exists(h.Paths.GetExportArtifactPath(job)).Should().BeFalse();
        (await h.WithDb(db => db.MigrationExportArtifactRecords.SingleAsync())).State.Should().Be((int)MigrationExportArtifactState.Deleted);
        (await h.WithJobs(s => s.GetAsync(job, default)))!.State.Should().Be(MigrationJobState.Completed); await h.Sweep();
    }

    [Fact]
    public async Task Abandoned_staging_hook_receives_cutoff_and_protected_nonterminal_ids()
    {
        var hook = new StagingHook(); await using var h = new MigrationEngineHarness(); h.Configure = s => s.AddSingleton<IMigrationStagingCleanup>(hook); await h.InitializeAsync();
        var job = await h.NewJobAsync(); var protectedId = new PortableStagingId(Guid.NewGuid()); var abandoned = new PortableStagingId(Guid.NewGuid());
        hook.Areas[protectedId] = h.Clock.GetUtcNow(); hook.Areas[abandoned] = h.Clock.GetUtcNow();
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == job).ExecuteUpdateAsync(s => s.SetProperty(j => j.PreparedStagingId, protectedId.Value)
            .SetProperty(j => j.State, (int)MigrationJobState.ReadyToActivate).SetProperty(j => j.ExpiresAtUtc, h.Clock.GetUtcNow().AddDays(2).UtcDateTime)));
        h.Clock.Advance(TimeSpan.FromDays(1) - TimeSpan.FromTicks(1)); await h.Sweep(); hook.Areas.Should().HaveCount(2);
        h.Clock.Advance(TimeSpan.FromTicks(1)); await h.Sweep(); hook.Areas.Keys.Should().Equal(protectedId);
        hook.Protected.Should().Contain(protectedId); await h.Sweep(); hook.Areas.Should().ContainKey(protectedId);
    }

    [Fact]
    public async Task Transient_delete_failure_keeps_metadata_for_retry_and_releases_reservation()
    {
        var hook = new StagingHook { FailNextDelete = true };
        await using var h = new MigrationEngineHarness(); h.Configure = s => s.AddSingleton<IMigrationStagingCleanup>(hook); await h.InitializeAsync();
        var job = await h.NewJobAsync(); await h.StartAsync(job, MigrationEngineHarness.Bytes()); var id = new PortableStagingId(Guid.NewGuid());
        hook.Areas[id] = h.Clock.GetUtcNow(); h.Paths.EnsureDirectoryExists(h.Paths.GetStagingDirectory(id.Value));
        await h.WithDb(db => db.MigrationJobRecords.Where(j => j.Id == job).ExecuteUpdateAsync(s => s.SetProperty(j => j.PreparedStagingId, id.Value)));
        await h.WithJobs(s => s.CancelAsync(job, new(), default)); await h.Sweep();
        hook.Areas.Should().ContainKey(id); (await h.WithDb(db => db.MigrationJobRecords.SingleAsync())).PreparedStagingId.Should().Be(id.Value);
        (await h.WithDb(db => db.MigrationStorageReservations.SingleAsync())).ReleasedAtUtc.Should().NotBeNull();
        await h.Sweep(); hook.Areas.Should().BeEmpty(); Directory.Exists(h.Paths.GetStagingDirectory(id.Value)).Should().BeFalse();
        await h.Sweep(); hook.DeleteSuccesses.Should().Be(1);
    }

    private sealed class StagingHook : IMigrationStagingCleanup
    {
        internal Dictionary<PortableStagingId, DateTimeOffset> Areas { get; } = new();
        internal IReadOnlySet<PortableStagingId> Protected { get; private set; } = new HashSet<PortableStagingId>();
        internal bool FailNextDelete { get; set; }
        internal int DeleteSuccesses { get; private set; }
        public Task CleanupAbandonedAsync(DateTimeOffset cutoffUtc, IReadOnlySet<PortableStagingId> protectedIds, CancellationToken ct)
        { Protected = protectedIds; foreach (var id in Areas.Where(a => a.Value <= cutoffUtc && !protectedIds.Contains(a.Key)).Select(a => a.Key).ToList()) Areas.Remove(id); return Task.CompletedTask; }
        public Task DeleteAsync(PortableStagingId id, CancellationToken ct)
        { if (FailNextDelete) { FailNextDelete = false; throw new IOException("Transient delete"); } if (Areas.Remove(id)) DeleteSuccesses++; return Task.CompletedTask; }
    }
}
