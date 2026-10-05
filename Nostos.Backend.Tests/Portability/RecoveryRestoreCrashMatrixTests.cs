using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// The central Slice 9 proof: crashing at every Phase A step and every durable
/// journal phase/rename boundary of the restore, then starting a fresh host and
/// reconciler over the same files, always converges on exactly one complete
/// generation. A pre-commit crash leaves the pre-restore library intact (and
/// the claimed restore is then resumed to completion); a crash at or after the
/// durable commit keeps the fully restored library. Assertions compare content.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class RecoveryRestoreCrashMatrixTests
{
    private static readonly string[] Boundaries =
    [
        SelfHostedRecoveryRestoreSteps.PhaseCandidatePrepared,
        SelfHostedRecoveryRestoreSteps.AfterCapture,
        SelfHostedRecoveryRestoreSteps.AfterAdmission,
        SelfHostedRecoveryRestoreSteps.AfterVerifySourceDatabase,
        SelfHostedRecoveryRestoreSteps.AfterBuildMedia,
        SelfHostedRecoveryRestoreSteps.AfterExtractPayload,
        SelfHostedRecoveryRestoreSteps.AfterBuildDatabase,
        SelfHostedRecoveryRestoreSteps.AfterVerifyCandidate,
        SelfHostedRecoveryRestoreSteps.PhaseExclusiveEntered,
        SelfHostedRecoveryRestoreSteps.AfterFinalize,
        SelfHostedRecoveryRestoreSteps.AfterQuiesce,
        SelfHostedRecoveryRestoreSteps.PhaseDatabaseCheckpointed,
        SelfHostedRecoveryRestoreSteps.AfterPrepareRetention,
        SelfHostedRecoveryRestoreSteps.PhaseCutoverPrepared,
        SelfHostedRecoveryRestoreSteps.AfterRetainMedia,
        SelfHostedRecoveryRestoreSteps.PhasePreviousMediaRetained,
        SelfHostedRecoveryRestoreSteps.AfterRetainDatabase,
        SelfHostedRecoveryRestoreSteps.PhasePreviousDatabaseRetained,
        SelfHostedRecoveryRestoreSteps.AfterActivateMedia,
        SelfHostedRecoveryRestoreSteps.PhaseCandidateMediaActivated,
        SelfHostedRecoveryRestoreSteps.AfterActivateDatabase,
        SelfHostedRecoveryRestoreSteps.PhaseCandidateDatabaseActivated,
        SelfHostedRecoveryRestoreSteps.AfterReopen,
        SelfHostedRecoveryRestoreSteps.AfterPostVerify,
        SelfHostedRecoveryRestoreSteps.PhasePostActivationVerified,
        SelfHostedRecoveryRestoreSteps.PhaseCommitted,
        SelfHostedRecoveryRestoreSteps.AfterFinalizeReplacedRetention,
        SelfHostedRecoveryRestoreSteps.AfterMarkSourceRestored,
        SelfHostedRecoveryRestoreSteps.AfterFinalizeRestore,
    ];

    public static IEnumerable<object[]> Cases() => Boundaries.Select(boundary => new object[] { boundary });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CrashAtEveryBoundary_ConvergesOnOneCompleteGeneration(string boundary)
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();
        await bed.WriteHostRecordAsync();
        var preRestore = bed.SnapshotLiveGeneration();

        var crashed = false;
        try
        {
            await bed.RestoreAsync(observer: step =>
            {
                if (string.Equals(step, boundary, StringComparison.Ordinal))
                {
                    throw new SelfHostedRecoveryRestoreAbandonedException();
                }
            });
        }
        catch (SelfHostedRecoveryRestoreAbandonedException)
        {
            crashed = true;
        }

        crashed.Should().BeTrue($"the {boundary} boundary must be reached");

        await bed.RecoverHostAsync();

        var durablyCommitted = boundary is SelfHostedRecoveryRestoreSteps.PhaseCommitted
            or SelfHostedRecoveryRestoreSteps.AfterFinalizeReplacedRetention
            or SelfHostedRecoveryRestoreSteps.AfterMarkSourceRestored
            or SelfHostedRecoveryRestoreSteps.AfterFinalizeRestore;
        if (durablyCommitted)
        {
            await bed.AssertRestoredPortableGenerationAsync();
        }
        else
        {
            bed.AssertGenerationEquals(preRestore,
                $"a pre-commit crash at {boundary} must leave the pre-restore library intact");
        }

        // The claimed restore is restartable: resuming on the fresh host either
        // finishes the committed bookkeeping or retries the pre-commit attempt.
        var resumed = await bed.ResumeRestoreAsync();
        resumed.Outcome.Should().BeOneOf(
            SelfHostedRecoveryRestoreOutcome.Restored,
            SelfHostedRecoveryRestoreOutcome.AlreadyRestored);
        await bed.AssertRestoredPortableGenerationAsync();
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Restored);
    }

    [Fact]
    public async Task WithoutTheMediaRecoveryStep_TheSameCrashAdmitsADetectableMixedGeneration()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();
        var preRestore = bed.SnapshotLiveGeneration();
        await CrashAndStopAtAsync(bed, SelfHostedRecoveryRestoreSteps.PhaseCandidateMediaActivated);

        // The database step alone restores the pre-restore database while the
        // restored media stays live: a mix the production reconciler prevents.
        await bed.ReconcileWithStepsAsync(mediaStep: false, databaseStep: true, emptyDiscardStep: true);

        bed.AssertPortableEquals(preRestore.Tables, "the database step restores the pre-restore database");
        ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia)
            .Should().BeEquivalentTo(bed.OriginalMedia, "the restored media stays live");
        ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia)
            .Should().NotBeEquivalentTo(preRestore.Media,
                "the pre-restore database is paired with the restored media: a detectable mix");
    }

    [Fact]
    public async Task WithoutTheDatabaseRecoveryStep_TheSameCrashAdmitsADetectableMixedGeneration()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();
        var preRestore = bed.SnapshotLiveGeneration();
        await CrashAndStopAtAsync(bed, SelfHostedRecoveryRestoreSteps.PhaseCandidateDatabaseActivated);

        // The media step alone restores the pre-restore media while the restored
        // database stays live: the mirror-image mix.
        await bed.ReconcileWithStepsAsync(mediaStep: true, databaseStep: false, emptyDiscardStep: true);

        ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia)
            .Should().BeEquivalentTo(preRestore.Media, "the media step restores the pre-restore media");
        ActivationCoordinatorTemplate.DumpPortable(bed.Paths.LiveDatabase)
            .Should().BeEquivalentTo(bed.OriginalPortable, "the restored database stays live");
        ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia)
            .Should().NotBeEquivalentTo(bed.OriginalMedia,
                "the restored database is paired with the pre-restore media: a detectable mix");
    }

    [Fact]
    public async Task WithoutTheComponentSteps_AComponentRetentionCrashLeavesNoLiveLibrary()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await CrashAndStopAtAsync(bed, SelfHostedRecoveryRestoreSteps.PhasePreviousDatabaseRetained);

        // With every component step removed the reconciler cannot produce a
        // complete generation and must fail closed rather than guess.
        var failure = await FluentActions
            .Awaiting(() => bed.ReconcileWithStepsAsync(
                mediaStep: false, databaseStep: false, emptyDiscardStep: true))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryFailed);
        File.Exists(bed.Paths.LiveDatabase).Should().BeFalse();
        Directory.Exists(bed.Paths.LiveMedia).Should().BeFalse();
    }

    private static async Task CrashAndStopAtAsync(ActivationCoordinatorTestBed bed, string boundary)
    {
        var crashed = false;
        try
        {
            await bed.RestoreAsync(observer: step =>
            {
                if (string.Equals(step, boundary, StringComparison.Ordinal))
                {
                    throw new SelfHostedRecoveryRestoreAbandonedException();
                }
            });
        }
        catch (SelfHostedRecoveryRestoreAbandonedException)
        {
            crashed = true;
        }

        crashed.Should().BeTrue($"the {boundary} boundary must be reached");
    }
}
