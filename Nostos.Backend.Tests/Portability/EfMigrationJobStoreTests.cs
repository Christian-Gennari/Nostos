using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

// Slice 5 of issue #679: the EF-backed migration job store. Every test runs
// against a real SQLite database file and a manual TimeProvider, so lease
// expiry, restart and concurrency behaviour is deterministic and never sleeps.
public sealed class EfMigrationJobStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LeaseDuration =
        TimeSpan.FromMinutes(MigrationContractLimits.WorkerLeaseDurationMinutes);

    private readonly List<string> _databasePaths = new();

    [Fact]
    public async Task Create_with_same_key_and_payload_replays_the_original_job()
    {
        await using var harness = await NewHarnessAsync();

        var first = await harness.Store.CreateAsync(
            MigrationDirection.Import,
            "create-key",
            CancellationToken.None);

        first.IsConflict.Should().BeFalse();
        first.WasReplay.Should().BeFalse();
        var job = first.Resource!;
        job.State.Should().Be(MigrationJobState.Pending);
        job.Direction.Should().Be(MigrationDirection.Import);
        job.RecoveryStatus.Should().Be(MigrationRecoveryStatus.NotRequired);
        job.LeaseToken.Should().BeNull();
        job.LeaseExpiresAtUtc.Should().BeNull();
        job.FailureCode.Should().BeNull();
        job.FailureMessage.Should().BeNull();
        job.CreatedAtUtc.Should().Be(T0);
        job.UpdatedAtUtc.Should().Be(T0);

        var replay = await harness.Store.CreateAsync(
            MigrationDirection.Import,
            "create-key",
            CancellationToken.None);

        replay.WasReplay.Should().BeTrue();
        replay.IsConflict.Should().BeFalse();
        replay.Resource!.Id.Should().Be(job.Id);

        (await harness.CountJobsAsync()).Should().Be(1);
        var record = await harness.GetRecordAsync(job.Id);
        record.AttemptNumber.Should().Be(1);
        record.Version.Should().Be(0);
        record.ExpiresAtUtc.Should().Be(T0.UtcDateTime.Add(EfMigrationJobStore.JobLifetime));
        record.CreationPayloadHash.Should().Be(
            EfMigrationJobStore.ComputeCreationPayloadHash(MigrationDirection.Import));
    }

    [Fact]
    public async Task Create_with_same_key_and_different_direction_returns_typed_conflict()
    {
        await using var harness = await NewHarnessAsync();

        var created = await harness.Store.CreateAsync(
            MigrationDirection.Import,
            "reused-key",
            CancellationToken.None);
        created.Resource.Should().NotBeNull();

        var conflict = await harness.Store.CreateAsync(
            MigrationDirection.Export,
            "reused-key",
            CancellationToken.None);

        conflict.IsConflict.Should().BeTrue();
        conflict.WasReplay.Should().BeFalse();
        conflict.Resource.Should().BeNull();
        conflict.Conflict!.Kind.Should().Be(
            MigrationIdempotencyConflictKind.KeyReusedWithDifferentPayload);

        (await harness.CountJobsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Parallel_creators_of_the_same_key_create_exactly_one_job()
    {
        await using var harness = await NewHarnessAsync();
        await using var firstDb = new NostosDbContext(harness.Options);
        await using var secondDb = new NostosDbContext(harness.Options);
        var firstStore = new EfMigrationJobStore(firstDb, harness.Clock);
        var secondStore = new EfMigrationJobStore(secondDb, harness.Clock);

        var results = await Task.WhenAll(
            firstStore.CreateAsync(MigrationDirection.Import, "race-key", CancellationToken.None),
            secondStore.CreateAsync(MigrationDirection.Import, "race-key", CancellationToken.None));

        results.Count(result => result.IsConflict).Should().Be(0);
        results.Count(result => !result.WasReplay).Should().Be(1);
        results.Count(result => result.WasReplay).Should().Be(1);
        results.Select(result => result.Resource!.Id).Distinct().Should().ContainSingle();
        (await harness.CountJobsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Parallel_creators_with_different_payloads_create_one_job_and_one_conflict()
    {
        await using var harness = await NewHarnessAsync();
        await using var firstDb = new NostosDbContext(harness.Options);
        await using var secondDb = new NostosDbContext(harness.Options);
        var firstStore = new EfMigrationJobStore(firstDb, harness.Clock);
        var secondStore = new EfMigrationJobStore(secondDb, harness.Clock);

        var results = await Task.WhenAll(
            firstStore.CreateAsync(MigrationDirection.Import, "race-key", CancellationToken.None),
            secondStore.CreateAsync(MigrationDirection.Export, "race-key", CancellationToken.None));

        results.Count(result => !result.IsConflict && !result.WasReplay).Should().Be(1);
        var conflict = results.Single(result => result.IsConflict);
        conflict.Conflict!.Kind.Should().Be(
            MigrationIdempotencyConflictKind.KeyReusedWithDifferentPayload);
        conflict.Resource.Should().BeNull();
        (await harness.CountJobsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_job()
    {
        await using var harness = await NewHarnessAsync();

        (await harness.Store.GetAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task First_acquire_wins_and_second_acquire_returns_null()
    {
        await using var harness = await NewHarnessAsync();
        var jobId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Transferring);

        var first = await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None);

        first.Should().NotBeNull();
        var second = await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None);
        second.Should().BeNull("an unexpired lease must block a second acquire");

        var record = await harness.GetRecordAsync(jobId);
        record.MigrationLeaseToken.Should().Be(first);
        record.LeaseExpiresAtUtc.Should().Be(T0.UtcDateTime.Add(LeaseDuration));
        record.HeartbeatAtUtc.Should().Be(T0.UtcDateTime);
        record.UpdatedAtUtc.Should().Be(T0.UtcDateTime);
        record.Version.Should().Be(1);
    }

    [Fact]
    public async Task Acquire_after_expiry_succeeds_and_the_old_token_is_rejected_everywhere()
    {
        await using var harness = await NewHarnessAsync();
        var jobId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Transferring);

        var oldToken = await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None);
        oldToken.Should().NotBeNull();

        var unexpiredSteal = await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None);
        unexpiredSteal.Should().BeNull("an unexpired lease can never be stolen");

        harness.Clock.Advance(LeaseDuration + TimeSpan.FromSeconds(1));

        var newToken = await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None);
        newToken.Should().NotBeNull("an expired lease must be reclaimable");
        newToken.Should().NotBe(oldToken);

        (await harness.Store.RenewLeaseAsync(
                jobId,
                oldToken!,
                LeaseDuration,
                CancellationToken.None))
            .Should().BeFalse("a superseded token must never renew");

        var transition = () => harness.Store.TransitionAsync(
            jobId,
            MigrationJobState.Validating,
            oldToken!,
            CancellationToken.None);
        var transitionException =
            await transition.Should().ThrowAsync<MigrationJobStoreException>();
        transitionException.Which.Code.Should().Be(MigrationJobStoreErrorCodes.LeaseConflict);

        var progress = () => harness.Store.UpdateProgressAsync(
            jobId,
            new MigrationProgress(MigrationProgressPhase.Transferring, 1, 100),
            oldToken!,
            CancellationToken.None);
        var progressException = await progress.Should().ThrowAsync<MigrationJobStoreException>();
        progressException.Which.Code.Should().Be(MigrationJobStoreErrorCodes.LeaseConflict);

        var afterRejection = await harness.GetRecordAsync(jobId);
        afterRejection.State.Should().Be((int)MigrationJobState.Transferring);
        afterRejection.ProgressBytesProcessed.Should().Be(0);
        afterRejection.MigrationLeaseToken.Should().Be(newToken);

        (await harness.Store.RenewLeaseAsync(
                jobId,
                newToken!,
                LeaseDuration,
                CancellationToken.None))
            .Should().BeTrue("the new owner must be able to renew");

        await harness.Store.UpdateProgressAsync(
            jobId,
            new MigrationProgress(MigrationProgressPhase.Transferring, 7, 100),
            newToken!,
            CancellationToken.None);

        var transitioned = await harness.Store.TransitionAsync(
            jobId,
            MigrationJobState.Validating,
            newToken!,
            CancellationToken.None);
        transitioned.State.Should().Be(MigrationJobState.Validating);
    }

    [Fact]
    public async Task Renew_extends_only_an_unexpired_matching_lease()
    {
        await using var harness = await NewHarnessAsync();
        var jobId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Transferring);
        var token = (await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None))!;

        (await harness.Store.RenewLeaseAsync(
                jobId,
                "someone-elses-token",
                LeaseDuration,
                CancellationToken.None))
            .Should().BeFalse("a mismatched token must never renew");

        var initial = await harness.GetRecordAsync(jobId);
        initial.LeaseExpiresAtUtc.Should().Be(T0.UtcDateTime.Add(LeaseDuration));
        initial.HeartbeatAtUtc.Should().Be(T0.UtcDateTime);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        (await harness.Store.RenewLeaseAsync(
                jobId,
                token,
                LeaseDuration,
                CancellationToken.None))
            .Should().BeTrue();

        var renewed = await harness.GetRecordAsync(jobId);
        renewed.LeaseExpiresAtUtc.Should().Be(
            T0.UtcDateTime.AddMinutes(1).Add(LeaseDuration));
        renewed.HeartbeatAtUtc.Should().Be(T0.UtcDateTime.AddMinutes(1));
        renewed.Version.Should().Be(2);

        harness.Clock.Advance(LeaseDuration);
        (await harness.Store.RenewLeaseAsync(
                jobId,
                token,
                LeaseDuration,
                CancellationToken.None))
            .Should().BeFalse("renewal never revives a lease that already expired");

        var afterExpiry = await harness.GetRecordAsync(jobId);
        afterExpiry.LeaseExpiresAtUtc.Should().Be(
            T0.UtcDateTime.AddMinutes(1).Add(LeaseDuration),
            "a failed renewal must not extend the stored expiry");
    }

    [Fact]
    public async Task Renew_fails_when_the_job_is_no_longer_processable()
    {
        await using var harness = await NewHarnessAsync();
        var jobId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Cancelled,
            leaseToken: "still-present-token",
            leaseExpiresAtUtc: T0.UtcDateTime.AddMinutes(5));

        (await harness.Store.RenewLeaseAsync(
                jobId,
                "still-present-token",
                LeaseDuration,
                CancellationToken.None))
            .Should().BeFalse(
                "cancellation must stop renewal even when the token row was not yet cleared");
    }

    [Fact]
    public async Task Release_with_a_stale_token_is_a_no_op()
    {
        await using var harness = await NewHarnessAsync();
        var jobId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Transferring);

        var staleToken = (await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None))!;

        harness.Clock.Advance(LeaseDuration + TimeSpan.FromSeconds(1));
        var currentToken = (await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None))!;

        await harness.Store.ReleaseLeaseAsync(jobId, staleToken, CancellationToken.None);
        await harness.Store.ReleaseLeaseAsync(jobId, "never-issued", CancellationToken.None);

        var record = await harness.GetRecordAsync(jobId);
        record.MigrationLeaseToken.Should().Be(
            currentToken,
            "releasing with a stale token must not clear the current owner's lease");
        record.LeaseExpiresAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Release_with_the_current_token_clears_it_and_unknown_jobs_are_a_no_op()
    {
        await using var harness = await NewHarnessAsync();
        var jobId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Transferring);
        var token = (await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None))!;

        await harness.Store.ReleaseLeaseAsync(jobId, token, CancellationToken.None);

        var record = await harness.GetRecordAsync(jobId);
        record.MigrationLeaseToken.Should().BeNull();
        record.LeaseExpiresAtUtc.Should().BeNull();

        await harness.Store.ReleaseLeaseAsync(jobId, token, CancellationToken.None);

        (await harness.GetRecordAsync(jobId)).Version.Should().Be(record.Version);

        await harness.Store.ReleaseLeaseAsync(
            Guid.NewGuid(),
            token,
            CancellationToken.None);
    }

    [Fact]
    public async Task TryAcquire_rejects_non_positive_durations_and_unknown_jobs_are_not_found()
    {
        await using var harness = await NewHarnessAsync();
        var jobId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Pending);

        var zero = () => harness.Store.TryAcquireLeaseAsync(
            jobId,
            TimeSpan.Zero,
            CancellationToken.None);
        await zero.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var negative = () => harness.Store.TryAcquireLeaseAsync(
            jobId,
            TimeSpan.FromSeconds(-1),
            CancellationToken.None);
        await negative.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var unknown = () => harness.Store.TryAcquireLeaseAsync(
            Guid.NewGuid(),
            LeaseDuration,
            CancellationToken.None);
        var exception = await unknown.Should().ThrowAsync<MigrationJobStoreException>();
        exception.Which.Code.Should().Be(MigrationJobStoreErrorCodes.NotFound);

        var terminalId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Completed);
        (await harness.Store.TryAcquireLeaseAsync(
                terminalId,
                LeaseDuration,
                CancellationToken.None))
            .Should().BeNull("a terminal job cannot be leased");
    }

    [Fact]
    public async Task Every_legal_transition_for_each_direction_succeeds_through_the_store()
    {
        await using var harness = await NewHarnessAsync();

        foreach (var direction in new[] { MigrationDirection.Import, MigrationDirection.Export })
        {
            var transitions = MigrationJobTransitions.AllowedTransitions(direction);
            foreach (var (from, targets) in transitions)
            {
                foreach (var target in targets)
                {
                    var jobId = await harness.SeedJobAsync(direction, from);
                    var token = await harness.Store.TryAcquireLeaseAsync(
                        jobId,
                        LeaseDuration,
                        CancellationToken.None);
                    token.Should().NotBeNull(
                        $"the seeded {direction} job in {from} is non-terminal and must be leasable");

                    var now = harness.Clock.GetUtcNow().UtcDateTime;
                    var job = await harness.Store.TransitionAsync(
                        jobId,
                        target,
                        token!,
                        CancellationToken.None);

                    job.Id.Should().Be(jobId);
                    job.State.Should().Be(
                        target,
                        $"{direction} {from} -> {target} is legal");
                    job.LeaseToken.Should().Be(token);
                    job.UpdatedAtUtc.Should().Be(harness.Clock.GetUtcNow());

                    var record = await harness.GetRecordAsync(jobId);
                    record.State.Should().Be((int)target);
                    record.UpdatedAtUtc.Should().Be(now);
                    if (target == MigrationJobState.Completed)
                        record.CompletedAtUtc.Should().Be(now);
                    if (target == MigrationJobState.Cancelled)
                        record.CancelledAtUtc.Should().Be(now);
                }
            }
        }
    }

    [Fact]
    public async Task Every_illegal_transition_for_each_direction_is_typed_invalid_state_and_leaves_state_unchanged()
    {
        await using var harness = await NewHarnessAsync();

        foreach (var direction in new[] { MigrationDirection.Import, MigrationDirection.Export })
        {
            var allowed = MigrationJobTransitions.AllowedTransitions(direction);
            foreach (var from in Enum.GetValues<MigrationJobState>())
            {
                var illegalTargets = Enum.GetValues<MigrationJobState>()
                    .Where(target => !allowed[from].Contains(target))
                    .ToList();

                var jobId = await harness.SeedJobAsync(direction, from);
                string token;
                if (MigrationJobTransitions.IsTerminal(from))
                {
                    token = "no-lease-token";
                }
                else
                {
                    token = (await harness.Store.TryAcquireLeaseAsync(
                        jobId,
                        LeaseDuration,
                        CancellationToken.None))!;
                    token.Should().NotBeNull();
                }

                foreach (var target in illegalTargets)
                {
                    var transition = () => harness.Store.TransitionAsync(
                        jobId,
                        target,
                        token,
                        CancellationToken.None);
                    var exception =
                        await transition.Should().ThrowAsync<MigrationJobStoreException>();
                    exception.Which.Code.Should().Be(
                        MigrationJobStoreErrorCodes.InvalidState,
                        $"{direction} {from} -> {target} must be rejected");

                    var record = await harness.GetRecordAsync(jobId);
                    record.State.Should().Be(
                        (int)from,
                        "a rejected transition must not mutate the job");
                }
            }
        }
    }

    [Fact]
    public async Task Stale_version_with_a_valid_lease_token_is_a_typed_lease_conflict()
    {
        var path = NewDatabasePath();
        var clock = new ManualTimeProvider(T0);
        var jobId = Guid.NewGuid();
        var versionBumper = new VersionBumpingInterceptor(path, jobId);
        await using var harness = new Harness(
            path,
            clock,
            builder => builder.AddInterceptors(versionBumper));
        await harness.InitializeAsync();

        await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Transferring,
            id: jobId);
        var token = (await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None))!;

        var before = await harness.GetRecordAsync(jobId);
        versionBumper.Enabled = true;
        var transition = () => harness.Store.TransitionAsync(
            jobId,
            MigrationJobState.Validating,
            token,
            CancellationToken.None);
        var exception = await transition.Should().ThrowAsync<MigrationJobStoreException>();
        exception.Which.Code.Should().Be(
            MigrationJobStoreErrorCodes.LeaseConflict,
            "a row version that keeps moving is an optimistic concurrency conflict");
        versionBumper.Enabled = false;

        var after = await harness.GetRecordAsync(jobId);
        after.State.Should().Be((int)MigrationJobState.Transferring);
        after.MigrationLeaseToken.Should().Be(token);
        after.Version.Should().BeGreaterThan(before.Version);
    }

    [Fact]
    public async Task Progress_updates_are_rejected_without_a_valid_unexpired_lease()
    {
        await using var harness = await NewHarnessAsync();
        var jobId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Transferring);
        var token = (await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None))!;
        var progress = new MigrationProgress(MigrationProgressPhase.Transferring, 10, 100);

        var wrongToken = () => harness.Store.UpdateProgressAsync(
            jobId,
            progress,
            "not-the-token",
            CancellationToken.None);
        var wrongTokenException =
            await wrongToken.Should().ThrowAsync<MigrationJobStoreException>();
        wrongTokenException.Which.Code.Should().Be(MigrationJobStoreErrorCodes.LeaseConflict);

        harness.Clock.Advance(LeaseDuration + TimeSpan.FromSeconds(1));
        var expired = () => harness.Store.UpdateProgressAsync(
            jobId,
            progress,
            token,
            CancellationToken.None);
        var expiredException = await expired.Should().ThrowAsync<MigrationJobStoreException>();
        expiredException.Which.Code.Should().Be(MigrationJobStoreErrorCodes.LeaseConflict);

        var unknown = () => harness.Store.UpdateProgressAsync(
            Guid.NewGuid(),
            progress,
            token,
            CancellationToken.None);
        var unknownException = await unknown.Should().ThrowAsync<MigrationJobStoreException>();
        unknownException.Which.Code.Should().Be(MigrationJobStoreErrorCodes.NotFound);

        var record = await harness.GetRecordAsync(jobId);
        record.ProgressPhase.Should().Be((int)MigrationProgressPhase.Pending);
        record.ProgressBytesProcessed.Should().Be(0);
        record.ProgressTotalBytes.Should().BeNull();
        record.ProgressMessage.Should().BeNull();
        record.MigrationLeaseToken.Should().Be(token);
    }

    [Fact]
    public async Task Progress_updates_under_the_valid_lease_are_durable_and_map_back()
    {
        await using var harness = await NewHarnessAsync();
        var jobId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Transferring);
        var token = (await harness.Store.TryAcquireLeaseAsync(
            jobId,
            LeaseDuration,
            CancellationToken.None))!;

        await harness.Store.UpdateProgressAsync(
            jobId,
            new MigrationProgress(
                MigrationProgressPhase.Transferring,
                1234,
                5000,
                CompletedChunks: 1,
                TotalChunks: 2,
                Message: "halfway"),
            token,
            CancellationToken.None);

        var record = await harness.GetRecordAsync(jobId);
        record.ProgressPhase.Should().Be((int)MigrationProgressPhase.Transferring);
        record.ProgressBytesProcessed.Should().Be(1234);
        record.ProgressTotalBytes.Should().Be(5000);
        record.ProgressCompletedChunks.Should().Be(1);
        record.ProgressTotalChunks.Should().Be(2);
        record.ProgressMessage.Should().Be("halfway");
        record.HeartbeatAtUtc.Should().Be(T0.UtcDateTime);
        record.UpdatedAtUtc.Should().Be(T0.UtcDateTime);
        record.Version.Should().Be(2);

        var mapped = await harness.Store.GetAsync(jobId, CancellationToken.None);
        mapped!.State.Should().Be(MigrationJobState.Transferring);
        mapped.UpdatedAtUtc.Should().Be(T0);
    }

    [Fact]
    public async Task Cancel_is_allowed_from_every_pre_activation_state_and_is_idempotent()
    {
        await using var harness = await NewHarnessAsync();

        foreach (var state in new[]
                 {
                     MigrationJobState.Pending,
                     MigrationJobState.Preparing,
                     MigrationJobState.Transferring,
                     MigrationJobState.Validating,
                     MigrationJobState.ReadyToActivate,
                 })
        {
            var jobId = await harness.SeedJobAsync(MigrationDirection.Import, state);
            if (state == MigrationJobState.Transferring)
            {
                var token = await harness.Store.TryAcquireLeaseAsync(
                    jobId,
                    LeaseDuration,
                    CancellationToken.None);
                token.Should().NotBeNull("cancel must work for a leased job without the token");
            }

            var now = harness.Clock.GetUtcNow().UtcDateTime;
            await harness.Store.CancelAsync(
                jobId,
                new MigrationCancelRequest("user asked"),
                CancellationToken.None);

            var record = await harness.GetRecordAsync(jobId);
            record.State.Should().Be((int)MigrationJobState.Cancelled, $"{state} is cancellable");
            record.CancellationReason.Should().Be("user asked");
            record.CancelledAtUtc.Should().Be(now);
            record.UpdatedAtUtc.Should().Be(now);
            record.MigrationLeaseToken.Should().BeNull();
            record.LeaseExpiresAtUtc.Should().BeNull();
            record.AttemptNumber.Should().Be(1);
            var cancelledVersion = record.Version;

            await harness.Store.CancelAsync(
                jobId,
                new MigrationCancelRequest("second click"),
                CancellationToken.None);

            var replayed = await harness.GetRecordAsync(jobId);
            replayed.State.Should().Be((int)MigrationJobState.Cancelled);
            replayed.CancellationReason.Should().Be(
                "user asked",
                "an idempotent replay must not rewrite cancellation history");
            replayed.Version.Should().Be(cancelledVersion);
        }
    }

    [Fact]
    public async Task Cancel_marks_active_sessions_cancelled_and_preserves_chunk_receipts()
    {
        await using var harness = await NewHarnessAsync();
        var jobId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Transferring);
        var sessionId = await harness.SeedSessionAsync(
            jobId,
            MigrationSessionState.Receiving,
            withReceipt: true);

        await harness.Store.CancelAsync(
            jobId,
            new MigrationCancelRequest("stop the transfer"),
            CancellationToken.None);

        var session = await harness.GetSessionAsync(sessionId);
        session!.State.Should().Be((int)MigrationSessionState.Cancelled);
        session.ReceivedBytes.Should().Be(16L * 1024 * 1024);
        (await harness.CountReceiptsAsync(sessionId)).Should().Be(
            1,
            "verified chunk receipts survive cancellation for a possible retry");
    }

    [Fact]
    public async Task Cancel_is_rejected_at_the_activation_boundary_and_for_terminal_states()
    {
        await using var harness = await NewHarnessAsync();

        var activatingId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Activating);
        var activating = () => harness.Store.CancelAsync(
            activatingId,
            new MigrationCancelRequest(),
            CancellationToken.None);
        var activatingException =
            await activating.Should().ThrowAsync<MigrationJobStoreException>();
        activatingException.Which.Code.Should().Be(MigrationJobStoreErrorCodes.CannotCancel);

        foreach (var state in new[]
                 {
                     MigrationJobState.Completed,
                     MigrationJobState.Failed,
                     MigrationJobState.Expired,
                 })
        {
            var jobId = await harness.SeedJobAsync(MigrationDirection.Import, state);
            var cancel = () => harness.Store.CancelAsync(
                jobId,
                new MigrationCancelRequest(),
                CancellationToken.None);
            var exception = await cancel.Should().ThrowAsync<MigrationJobStoreException>();
            exception.Which.Code.Should().Be(MigrationJobStoreErrorCodes.InvalidState);

            var record = await harness.GetRecordAsync(jobId);
            record.State.Should().Be((int)state);
            record.CancelledAtUtc.Should().BeNull();
        }

        var unknown = () => harness.Store.CancelAsync(
            Guid.NewGuid(),
            new MigrationCancelRequest(),
            CancellationToken.None);
        var unknownException = await unknown.Should().ThrowAsync<MigrationJobStoreException>();
        unknownException.Which.Code.Should().Be(MigrationJobStoreErrorCodes.NotFound);
    }

    [Fact]
    public async Task Retry_reactivates_each_retryable_state_and_clears_operational_fields()
    {
        await using var harness = await NewHarnessAsync();

        foreach (var state in new[]
                 {
                     MigrationJobState.Failed,
                     MigrationJobState.Cancelled,
                     MigrationJobState.Expired,
                 })
        {
            var preparedStagingId = Guid.NewGuid();
            var reservationId = Guid.NewGuid();
            var jobId = await harness.SeedJobAsync(
                MigrationDirection.Import,
                state,
                mutate: record =>
                {
                    record.AttemptNumber = 2;
                    record.MigrationLeaseToken = "old-lease";
                    record.LeaseExpiresAtUtc = T0.UtcDateTime.AddMinutes(5);
                    record.HeartbeatAtUtc = T0.UtcDateTime;
                    record.FailureCode = "source_media_changed";
                    record.FailureMessage = "The archive changed.";
                    record.CancellationReason = "user asked";
                    record.CancelledAtUtc = T0.UtcDateTime;
                    record.CompletedAtUtc = T0.UtcDateTime;
                    record.ProgressPhase = (int)MigrationProgressPhase.Validating;
                    record.ProgressBytesProcessed = 42;
                    record.ProgressTotalBytes = 100;
                    record.ProgressCompletedChunks = 1;
                    record.ProgressTotalChunks = 2;
                    record.ProgressMessage = "stale";
                    record.DestinationRevision = "rev-9";
                    record.PreparedStagingId = preparedStagingId;
                    record.PreparedImportMetadataJson = "{\"version\":1}";
                    record.ReservedStorageBytes = 321;
                    record.ReservationId = reservationId;
                });

            harness.Clock.Advance(TimeSpan.FromHours(1));
            var now = harness.Clock.GetUtcNow();
            var retried = await harness.Store.RetryAsync(
                jobId,
                new MigrationRetryRequest(),
                CancellationToken.None);

            retried.State.Should().Be(MigrationJobState.Pending, $"{state} is retryable");
            retried.LeaseToken.Should().BeNull();
            retried.LeaseExpiresAtUtc.Should().BeNull();
            retried.FailureCode.Should().BeNull();
            retried.FailureMessage.Should().BeNull();
            retried.UpdatedAtUtc.Should().Be(now);

            var record = await harness.GetRecordAsync(jobId);
            record.State.Should().Be((int)MigrationJobState.Pending);
            record.AttemptNumber.Should().Be(3, "retry increments the attempt number once");
            record.MigrationLeaseToken.Should().BeNull();
            record.LeaseExpiresAtUtc.Should().BeNull();
            record.HeartbeatAtUtc.Should().BeNull();
            record.FailureCode.Should().BeNull();
            record.FailureMessage.Should().BeNull();
            record.CancellationReason.Should().BeNull();
            record.CancelledAtUtc.Should().BeNull();
            record.CompletedAtUtc.Should().BeNull();
            record.ProgressPhase.Should().Be((int)MigrationProgressPhase.Pending);
            record.ProgressBytesProcessed.Should().Be(0);
            record.ProgressTotalBytes.Should().BeNull();
            record.ProgressCompletedChunks.Should().BeNull();
            record.ProgressTotalChunks.Should().BeNull();
            record.ProgressMessage.Should().BeNull();
            record.ExpiresAtUtc.Should().Be(now.UtcDateTime.Add(EfMigrationJobStore.JobLifetime));
            record.DestinationRevision.Should().Be("rev-9");
            record.PreparedStagingId.Should().Be(preparedStagingId);
            record.PreparedImportMetadataJson.Should().Be("{\"version\":1}");
            record.ReservedStorageBytes.Should().Be(321);
            record.ReservationId.Should().Be(reservationId);
        }
    }

    [Fact]
    public async Task Retry_preserves_sessions_and_chunk_receipts_for_reuse()
    {
        await using var harness = await NewHarnessAsync();
        var jobId = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Failed);
        var sessionId = await harness.SeedSessionAsync(
            jobId,
            MigrationSessionState.Expired,
            withReceipt: true);

        await harness.Store.RetryAsync(
            jobId,
            new MigrationRetryRequest("double-click"),
            CancellationToken.None);

        var session = await harness.GetSessionAsync(sessionId);
        session.Should().NotBeNull("retry preserves the retained upload session");
        session!.State.Should().Be(
            (int)MigrationSessionState.Expired,
            "session reactivation belongs to the transfer service, not the job store");
        (await harness.CountReceiptsAsync(sessionId)).Should().Be(1);
        (await harness.GetRecordAsync(jobId)).AttemptNumber.Should().Be(2);
    }

    [Fact]
    public async Task Retry_is_rejected_from_every_non_retryable_state()
    {
        await using var harness = await NewHarnessAsync();

        foreach (var state in new[]
                 {
                     MigrationJobState.Pending,
                     MigrationJobState.Preparing,
                     MigrationJobState.Transferring,
                     MigrationJobState.Validating,
                     MigrationJobState.ReadyToActivate,
                     MigrationJobState.Activating,
                     MigrationJobState.Completed,
                 })
        {
            var jobId = await harness.SeedJobAsync(MigrationDirection.Import, state);
            var retry = () => harness.Store.RetryAsync(
                jobId,
                new MigrationRetryRequest(),
                CancellationToken.None);
            var exception = await retry.Should().ThrowAsync<MigrationJobStoreException>();
            exception.Which.Code.Should().Be(MigrationJobStoreErrorCodes.NotRetryable);

            var record = await harness.GetRecordAsync(jobId);
            record.State.Should().Be((int)state);
            record.AttemptNumber.Should().Be(1);
        }

        var unknown = () => harness.Store.RetryAsync(
            Guid.NewGuid(),
            new MigrationRetryRequest(),
            CancellationToken.None);
        var unknownException = await unknown.Should().ThrowAsync<MigrationJobStoreException>();
        unknownException.Which.Code.Should().Be(MigrationJobStoreErrorCodes.NotFound);
    }

    [Fact]
    public async Task Recovery_discovery_returns_exactly_jobs_with_absent_or_expired_leases_in_mutation_order()
    {
        await using var harness = await NewHarnessAsync();

        var noLease = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Pending,
            updatedAtUtc: T0.UtcDateTime.AddMinutes(-30));
        var expiredLease = await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Transferring,
            leaseToken: "expired-lease",
            leaseExpiresAtUtc: T0.UtcDateTime.AddMinutes(-10),
            updatedAtUtc: T0.UtcDateTime.AddMinutes(-20));
        var nullExpiry = await harness.SeedJobAsync(
            MigrationDirection.Export,
            MigrationJobState.Preparing,
            leaseToken: "orphaned-token",
            leaseExpiresAtUtc: null,
            updatedAtUtc: T0.UtcDateTime.AddMinutes(-10));

        await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Transferring,
            leaseToken: "live-lease",
            leaseExpiresAtUtc: T0.UtcDateTime.AddMinutes(10),
            updatedAtUtc: T0.UtcDateTime.AddMinutes(-40));
        await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Preparing,
            leaseToken: "boundary-lease",
            leaseExpiresAtUtc: T0.UtcDateTime,
            updatedAtUtc: T0.UtcDateTime.AddMinutes(-50));
        await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Completed,
            updatedAtUtc: T0.UtcDateTime.AddMinutes(-60));
        await harness.SeedJobAsync(
            MigrationDirection.Import,
            MigrationJobState.Failed,
            leaseToken: "failed-lease",
            leaseExpiresAtUtc: T0.UtcDateTime.AddMinutes(-10),
            updatedAtUtc: T0.UtcDateTime.AddMinutes(-70));

        var jobs = await harness.Store.GetJobsNeedingRecoveryAsync(
            T0,
            CancellationToken.None);

        jobs.Select(job => job.Id).Should().Equal(
            noLease,
            expiredLease,
            nullExpiry);
    }

    [Fact]
    public async Task Parallel_workers_contending_for_one_job_yield_exactly_one_lease()
    {
        await using var harness = await NewHarnessAsync();

        for (var iteration = 0; iteration < 8; iteration++)
        {
            var jobId = await harness.SeedJobAsync(
                MigrationDirection.Import,
                MigrationJobState.Transferring);

            var attempts = Enumerable.Range(0, 8)
                .Select(async _ =>
                {
                    await using var db = new NostosDbContext(harness.Options);
                    var store = new EfMigrationJobStore(db, harness.Clock);
                    return await store.TryAcquireLeaseAsync(
                        jobId,
                        LeaseDuration,
                        CancellationToken.None);
                })
                .ToArray();

            var tokens = await Task.WhenAll(attempts);

            tokens.Count(token => token is not null).Should().Be(
                1,
                $"iteration {iteration}: exactly one worker may hold the lease");
            var winner = tokens.Single(token => token is not null)!;
            var record = await harness.GetRecordAsync(jobId);
            record.MigrationLeaseToken.Should().Be(winner);

            await harness.Store.ReleaseLeaseAsync(jobId, winner, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_store_recreated_over_the_same_database_file_sees_the_same_state()
    {
        await using var harness = await NewHarnessAsync();
        var job = await harness.CreateJobAsync(MigrationDirection.Import, "restart-key");
        var token = (await harness.Store.TryAcquireLeaseAsync(
            job.Id,
            LeaseDuration,
            CancellationToken.None))!;
        await harness.Store.UpdateProgressAsync(
            job.Id,
            new MigrationProgress(MigrationProgressPhase.Transferring, 512, 2048),
            token,
            CancellationToken.None);

        await harness.RestartAsync();

        var reloaded = await harness.Store.GetAsync(job.Id, CancellationToken.None);
        reloaded.Should().NotBeNull();
        reloaded!.State.Should().Be(MigrationJobState.Pending);
        reloaded.LeaseToken.Should().Be(token, "the lease must survive a process restart");

        (await harness.Store.RenewLeaseAsync(
                job.Id,
                token,
                LeaseDuration,
                CancellationToken.None))
            .Should().BeTrue("the persisted lease is still renewable after restart");

        var record = await harness.GetRecordAsync(job.Id);
        record.ProgressBytesProcessed.Should().Be(512);
        record.ProgressTotalBytes.Should().Be(2048);

        var recovery = await harness.Store.GetJobsNeedingRecoveryAsync(
            T0.AddMinutes(1),
            CancellationToken.None);
        recovery.Should().NotContain(
            candidate => candidate.Id == job.Id,
            "a job with an unexpired persisted lease is not an orphan");
    }

    private async Task<Harness> NewHarnessAsync(DateTimeOffset? now = null)
    {
        var harness = new Harness(
            NewDatabasePath(),
            new ManualTimeProvider(now ?? T0),
            configure: null);
        await harness.InitializeAsync();
        return harness;
    }

    private string NewDatabasePath()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"nostos-679-job-store-{Guid.NewGuid():N}.db");
        _databasePaths.Add(path);
        return path;
    }

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

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    // Forces a lost optimistic race even though the caller still owns an
    // unexpired lease: before every UPDATE of the job row it bumps the row's
    // Version on a separate connection, so the store's Version predicate can
    // never match.
    private sealed class VersionBumpingInterceptor(string databasePath, Guid jobId)
        : DbCommandInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled
                && command.CommandText.Contains(
                    "UPDATE \"MigrationJobRecords\"",
                    StringComparison.Ordinal))
            {
                using var connection = new SqliteConnection($"Data Source={databasePath}");
                connection.Open();
                using var bump = connection.CreateCommand();
                bump.CommandText =
                    "UPDATE \"MigrationJobRecords\" " +
                    "SET \"Version\" = \"Version\" + 1 WHERE \"Id\" = @id";
                bump.Parameters.AddWithValue("@id", jobId);
                bump.ExecuteNonQuery();
            }

            return new ValueTask<InterceptionResult<int>>(result);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            string databasePath,
            ManualTimeProvider clock,
            Action<DbContextOptionsBuilder<NostosDbContext>>? configure)
        {
            DatabasePath = databasePath;
            Clock = clock;
            var builder = new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={databasePath}");
            configure?.Invoke(builder);
            Options = builder.Options;
            Db = new NostosDbContext(Options);
            Store = new EfMigrationJobStore(Db, clock);
        }

        public string DatabasePath { get; }

        public ManualTimeProvider Clock { get; }

        public DbContextOptions<NostosDbContext> Options { get; }

        public NostosDbContext Db { get; private set; }

        public EfMigrationJobStore Store { get; private set; }

        public Task InitializeAsync() => Db.Database.EnsureCreatedAsync();

        public async Task RestartAsync()
        {
            await Db.DisposeAsync();
            Db = new NostosDbContext(Options);
            Store = new EfMigrationJobStore(Db, Clock);
        }

        public async Task<MigrationJob> CreateJobAsync(
            MigrationDirection direction,
            string idempotencyKey)
        {
            var result = await Store.CreateAsync(
                direction,
                idempotencyKey,
                CancellationToken.None);
            result.IsConflict.Should().BeFalse();
            return result.Resource!;
        }

        public async Task<Guid> SeedJobAsync(
            MigrationDirection direction,
            MigrationJobState state,
            string? leaseToken = null,
            DateTime? leaseExpiresAtUtc = null,
            DateTime? updatedAtUtc = null,
            Action<MigrationJobRecord>? mutate = null,
            Guid? id = null)
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            var record = new MigrationJobRecord
            {
                Id = id ?? Guid.NewGuid(),
                Direction = (int)direction,
                State = (int)state,
                RecoveryStatus = (int)MigrationRecoveryStatus.NotRequired,
                ProgressPhase = (int)MigrationProgressPhase.Pending,
                IdempotencyKey = $"seed-{Guid.NewGuid():N}",
                CreationPayloadHash = new string('a', 64),
                CreatedAtUtc = now,
                UpdatedAtUtc = updatedAtUtc ?? now,
                ExpiresAtUtc = now.AddDays(7),
                AttemptNumber = 1,
                MigrationLeaseToken = leaseToken,
                LeaseExpiresAtUtc = leaseExpiresAtUtc,
            };
            mutate?.Invoke(record);

            await using var db = new NostosDbContext(Options);
            db.MigrationJobRecords.Add(record);
            await db.SaveChangesAsync();
            return record.Id;
        }

        public async Task<Guid> SeedSessionAsync(
            Guid jobId,
            MigrationSessionState state,
            bool withReceipt)
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            var session = new MigrationSessionRecord
            {
                JobId = jobId,
                Purpose = (int)MigrationSessionPurpose.Import,
                State = (int)state,
                TotalBytes = 32L * 1024 * 1024,
                ChunkSize = MigrationContractLimits.DefaultChunkBytes,
                TotalChunks = 2,
                FileIdentitySizeBytes = 32L * 1024 * 1024,
                FileIdentitySha256 = new string('b', 64),
                IdempotencyKey = $"seed-session-{Guid.NewGuid():N}",
                CreationPayloadHash = new string('c', 64),
                ReceivedBytes = withReceipt ? 16L * 1024 * 1024 : 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ExpiresAtUtc = now.AddHours(MigrationContractLimits.SessionExpiryHours),
                StorageKey = $"uploads/session-{Guid.NewGuid():N}/archive.part",
            };

            await using var db = new NostosDbContext(Options);
            db.MigrationSessionRecords.Add(session);
            if (withReceipt)
            {
                db.MigrationChunkReceiptRecords.Add(new MigrationChunkReceiptRecord
                {
                    SessionId = session.Id,
                    ChunkIndex = 0,
                    OffsetBytes = 0,
                    LengthBytes = 16 * 1024 * 1024,
                    Sha256 = new string('d', 64),
                    ReceivedAtUtc = now,
                });
            }

            await db.SaveChangesAsync();
            return session.Id;
        }

        public async Task<MigrationJobRecord> GetRecordAsync(Guid jobId)
        {
            await using var db = new NostosDbContext(Options);
            return await db.MigrationJobRecords
                .AsNoTracking()
                .SingleAsync(record => record.Id == jobId);
        }

        public async Task<MigrationSessionRecord?> GetSessionAsync(Guid sessionId)
        {
            await using var db = new NostosDbContext(Options);
            return await db.MigrationSessionRecords
                .AsNoTracking()
                .SingleOrDefaultAsync(session => session.Id == sessionId);
        }

        public async Task<int> CountJobsAsync()
        {
            await using var db = new NostosDbContext(Options);
            return await db.MigrationJobRecords.CountAsync();
        }

        public async Task<int> CountReceiptsAsync(Guid sessionId)
        {
            await using var db = new NostosDbContext(Options);
            return await db.MigrationChunkReceiptRecords
                .CountAsync(receipt => receipt.SessionId == sessionId);
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
