using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 7 failure handling: Phase A failures clean their candidate artifacts
/// and leave the job retryable with the live library untouched; a destination
/// that changes after preparation is refused under the gate without losing the
/// intervening write; post-commit work failing never fails the activation; and
/// the bounded drain fails closed with a typed busy error.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class ActivationCoordinatorFailureTests
{
    public static IEnumerable<object[]> PhaseASteps()
    {
        yield return [SelfHostedActivationSteps.PhaseCandidatePrepared];
        yield return [SelfHostedActivationSteps.AfterCapture];
        yield return [SelfHostedActivationSteps.AfterAdmission];
        yield return [SelfHostedActivationSteps.AfterBuildMedia];
        yield return [SelfHostedActivationSteps.AfterBuildDatabase];
        yield return [SelfHostedActivationSteps.AfterVerifyCandidate];
    }

    [Theory]
    [MemberData(nameof(PhaseASteps))]
    public async Task PhaseAFailure_CleansCandidateArtifacts_AndLeavesTheJobRetryable(string step)
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true, observer: reached =>
            {
                if (string.Equals(reached, step, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("injected phase A failure");
                }
            }))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);

        bed.AssertOriginalGeneration();
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.ReadyToActivate);
        File.Exists(bed.Paths.CandidateDatabase(bed.JobId)).Should().BeFalse();
        Directory.Exists(bed.Paths.CandidateMedia(bed.JobId)).Should().BeFalse();

        // The same job can be activated again after the transient failure.
        var retry = await bed.ActivateAsync(confirm: true);
        retry.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.AssertImportedGenerationAsync();
    }

    [Fact]
    public async Task CorruptStagedMedia_FailsPreparedVerification_WithoutChangingTheLibrary()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true, freshStaging: true);
        var stagingDirectory = Path.Combine(bed.StagingRoot, bed.Prepared.Metadata.StagingId.Value.ToString("N"), "media");
        var staged = Directory.EnumerateFiles(stagingDirectory, "*.bin").First();
        var bytes = await File.ReadAllBytesAsync(staged);
        bytes[0] ^= 0xFF;
        await File.WriteAllBytesAsync(staged, bytes);

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);

        bed.AssertOriginalGeneration();
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.ReadyToActivate);
    }

    [Fact]
    public async Task TamperedCandidateMedia_FailsCandidateVerification_WithoutChangingTheLibrary()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true, observer: reached =>
            {
                if (string.Equals(reached, SelfHostedActivationSteps.AfterBuildDatabase, StringComparison.Ordinal))
                {
                    ActivationCoordinatorTestBed.CorruptFirstFile(bed.Paths.CandidateMedia(bed.JobId));
                }
            }))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);

        bed.AssertOriginalGeneration();
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.ReadyToActivate);
        Directory.Exists(bed.Paths.CandidateMedia(bed.JobId)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DestinationChangedAfterPreparation_IsRefusedUnderTheGate_AndTheWriteSurvives(bool populated)
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated);

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true, observer: reached =>
            {
                if (string.Equals(reached, SelfHostedActivationSteps.AfterVerifyCandidate, StringComparison.Ordinal))
                {
                    bed.WritePortableRevisionBumpAsync().GetAwaiter().GetResult();
                }
            }))
            .Should().ThrowAsync<MigrationActivationException>();
        // A populated destination was confirmed against the old revision, so the
        // write invalidates the confirmation (#681). An empty destination needed
        // no confirmation, but the write made it populated inside the window:
        // that is the empty-destination abort, which asks for a fresh review.
        failure.Which.Code.Should().Be(populated
            ? MigrationActivationErrorCodes.DestinationConflict
            : MigrationActivationErrorCodes.ConfirmationRequired);

        (await bed.CurrentRevisionAsync()).Should().Be(42, "the intervening write is never silently discarded");
        await using (var db = bed.OpenDatabase())
        {
            var collections = db.Collections.Select(collection => collection.Name).ToArray();
            collections.Should().Contain("WRITE-DURING-ACTIVATION");
        }

        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.ReadyToActivate);
        if (populated)
        {
            ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia).Should().BeEquivalentTo(bed.OriginalMedia);
        }
    }

    [Fact]
    public async Task HostStateWriteAfterPreparation_SurvivesIntoTheActivatedDatabase()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var result = await bed.ActivateAsync(confirm: true, observer: reached =>
        {
            if (string.Equals(reached, SelfHostedActivationSteps.AfterVerifyCandidate, StringComparison.Ordinal))
            {
                bed.WriteHostRecordAsync().GetAwaiter().GetResult();
            }
        });

        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.AssertImportedGenerationAsync();
        await using var db = bed.OpenDatabase();
        db.BackupRecords.Select(record => record.LocalArchivePath)
            .Should().Contain("Storage/backups/during-activation.nostos");
    }

    [Fact]
    public async Task NewLibraryWriterDuringTheWindow_GetsTheMaintenanceResponse()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        bool? admitted = null;
        var result = await bed.ActivateAsync(confirm: true, observer: reached =>
        {
            if (string.Equals(reached, SelfHostedActivationSteps.AfterQuiesce, StringComparison.Ordinal))
            {
                admitted = bed.Maintenance.TryEnterOperation() is not null;
            }
        });

        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        admitted.Should().BeFalse("the exclusive window refuses new library work");
    }

    [Fact]
    public async Task BoundedDrainTimeout_FailsClosedWithBusy_AndNothingChanges()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(
            populated: true, drainTimeout: TimeSpan.FromMilliseconds(150));
        await using var held = await bed.Maintenance.EnterOperationAsync(default);

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.Busy);

        bed.AssertOriginalGeneration();
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.ReadyToActivate);
        bed.Maintenance.IsMaintenanceActive.Should().BeFalse("a timed-out drain reopens admission");

        await held.DisposeAsync();

        // Once the writer leaves, the same request succeeds.
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
    }

    [Fact]
    public async Task PostCommitRollForwardFailure_KeepsAdmissionClosed_UntilRestartReconciles()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true, observer: reached =>
            {
                if (string.Equals(reached, SelfHostedActivationSteps.AfterReopen, StringComparison.Ordinal))
                {
                    // Strip the worker lease from the newly activated database: the
                    // post-commit projection then cannot complete in-process.
                    using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                        $"Data Source={bed.Paths.LiveDatabase};Pooling=False");
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "UPDATE \"MigrationJobRecords\" SET \"MigrationLeaseToken\" = NULL, \"LeaseExpiresAtUtc\" = NULL;";
                    command.ExecuteNonQuery();
                }
            }))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryFailed);

        // The committed generation is intact, but admission stays closed and the
        // journal stays unresolved until a restart reconciles it.
        await bed.AssertImportedGenerationAsync();
        bed.Maintenance.IsRecoveryRequired.Should().BeTrue();
        bed.Maintenance.IsMaintenanceActive.Should().BeTrue();
        bed.Maintenance.TryEnterOperation().Should().BeNull();
        bed.Journals.Read(bed.JobId)!.Phase.Should().Be(SelfHostedActivationPhase.Committed);
        File.Exists(new LibraryMaintenanceMarker(bed.Paths.LiveDatabase).MarkerPath).Should().BeTrue();
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.Activating);

        // A fresh host reconciles the committed journal and reopens admission.
        await bed.RecoverHostAsync();
        await bed.AssertImportedGenerationAsync();
        bed.Maintenance.IsMaintenanceActive.Should().BeFalse();
        var probe = bed.Maintenance.TryEnterOperation();
        probe.Should().NotBeNull();
        probe!.Dispose();

        // Resume completes only the post-commit work; the revision never moves twice.
        var resumed = await bed.ActivateAsync(confirm: true);
        resumed.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.Completed);
        (await bed.CurrentRevisionAsync()).Should().Be(ActivationCoordinatorTemplate.AdvancedRevision);
    }

    [Fact]
    public async Task RollbackFailureInMediaStep_KeepsAdmissionClosed_UntilRestartReconciles()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        bed.MediaRecoveryRenameFailure = () =>
            throw new InvalidOperationException("injected media rollback failure");

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true, observer: reached =>
            {
                if (string.Equals(reached, SelfHostedActivationSteps.AfterReopen, StringComparison.Ordinal))
                {
                    ActivationCoordinatorTestBed.CorruptFirstFile(bed.Paths.LiveMedia);
                }
            }))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryFailed);

        AssertStickyRecoveryState(bed, SelfHostedActivationPhase.RollingBack);
        var frozen = bed.SnapshotActivationTree();
        await Task.Yield();
        await Task.Yield();
        bed.SnapshotActivationTree().Should().BeEquivalentTo(frozen, "no further file mutation may occur while closed");

        bed.MediaRecoveryRenameFailure = null;
        await bed.RecoverHostAsync();
        bed.AssertOriginalGeneration();
        bed.Maintenance.IsMaintenanceActive.Should().BeFalse();
        var probe = bed.Maintenance.TryEnterOperation();
        probe.Should().NotBeNull();
        probe!.Dispose();

        bed.Clock.Advance(SelfHostedActivationCoordinator.ActivationLeaseDuration + TimeSpan.FromMinutes(1));
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.AssertImportedGenerationAsync();
    }

    [Fact]
    public async Task RollbackFailureInDatabaseStep_KeepsAdmissionClosed_UntilRestartReconciles()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        bed.DatabaseRecoveryRenameFailure = () =>
            throw new InvalidOperationException("injected database rollback failure");

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true, observer: reached =>
            {
                if (string.Equals(reached, SelfHostedActivationSteps.AfterReopen, StringComparison.Ordinal))
                {
                    ActivationCoordinatorTestBed.CorruptFirstFile(bed.Paths.LiveMedia);
                }
            }))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryFailed);

        AssertStickyRecoveryState(bed, SelfHostedActivationPhase.RollingBack);
        var frozen = bed.SnapshotActivationTree();
        await Task.Yield();
        bed.SnapshotActivationTree().Should().BeEquivalentTo(frozen, "no further file mutation may occur while closed");

        bed.DatabaseRecoveryRenameFailure = null;
        await bed.RecoverHostAsync();
        bed.AssertOriginalGeneration();
        bed.Maintenance.IsMaintenanceActive.Should().BeFalse();
        var probe = bed.Maintenance.TryEnterOperation();
        probe.Should().NotBeNull();
        probe!.Dispose();

        bed.Clock.Advance(SelfHostedActivationCoordinator.ActivationLeaseDuration + TimeSpan.FromMinutes(1));
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.AssertImportedGenerationAsync();
    }

    [Fact]
    public async Task PostActivationVerificationFailure_RollsBackInProcess_AndTheJobIsRetryable()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true, observer: reached =>
            {
                if (string.Equals(reached, SelfHostedActivationSteps.AfterReopen, StringComparison.Ordinal))
                {
                    ActivationCoordinatorTestBed.CorruptFirstFile(bed.Paths.LiveMedia);
                }
            }))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);

        bed.AssertOriginalGeneration();
        bed.Maintenance.IsMaintenanceActive.Should().BeFalse("a completed rollback reopens admission");
        var probe = bed.Maintenance.TryEnterOperation();
        probe.Should().NotBeNull();
        probe!.Dispose();
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.Failed);

        await using var scope = bed.Host.CreateAsyncScope();
        var retried = await scope.ServiceProvider.GetRequiredService<IMigrationJobStore>()
            .RetryAsync(bed.JobId, new MigrationRetryRequest(), default);
        retried.State.Should().Be(MigrationJobState.Pending, "a rolled-back activation failure is retryable");
    }

    [Fact]
    public async Task ReopenIntegrityFailure_RollsBackInProcess_AndKeepsTheOriginal()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true, observer: reached =>
            {
                if (string.Equals(reached, SelfHostedActivationSteps.AfterActivateDatabase, StringComparison.Ordinal))
                {
                    ActivationCoordinatorTestBed.CorruptDatabaseByte(bed.Paths.LiveDatabase);
                }
            }))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);

        bed.AssertOriginalGeneration();
        bed.Maintenance.IsMaintenanceActive.Should().BeFalse();
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.Failed);
    }

    private static void AssertStickyRecoveryState(
        ActivationCoordinatorTestBed bed,
        SelfHostedActivationPhase expectedPhase)
    {
        bed.Maintenance.IsRecoveryRequired.Should().BeTrue();
        bed.Maintenance.IsMaintenanceActive.Should().BeTrue();
        bed.Maintenance.TryEnterOperation().Should().BeNull(
            "admission must stay closed until startup reconciliation succeeds");
        bed.Journals.Read(bed.JobId)!.Phase.Should().Be(expectedPhase, "the journal must stay unresolved");
        File.Exists(new LibraryMaintenanceMarker(bed.Paths.LiveDatabase).MarkerPath).Should().BeTrue(
            "the durable maintenance marker must survive for the restart");
    }
}
