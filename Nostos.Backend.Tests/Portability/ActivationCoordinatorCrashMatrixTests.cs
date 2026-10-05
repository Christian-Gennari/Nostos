using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// The central Slice 7 proof: crashing at every durable phase write and every
/// rename boundary in Phase B, then starting a fresh host and reconciler over
/// the same files, always converges on exactly one complete generation. A
/// pre-commit crash returns the original library and a retry succeeds; a crash
/// at or after the durable commit keeps the verified import and completes the
/// job. Assertions compare content, never flags.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class ActivationCoordinatorCrashMatrixTests
{
    private static readonly string[] PhaseABoundaries =
    [
        SelfHostedActivationSteps.PhaseCandidatePrepared,
        SelfHostedActivationSteps.AfterCapture,
        SelfHostedActivationSteps.AfterAdmission,
        SelfHostedActivationSteps.AfterBuildMedia,
        SelfHostedActivationSteps.AfterBuildDatabase,
        SelfHostedActivationSteps.AfterVerifyCandidate,
    ];

    private static readonly string[] SharedBoundaries =
    [
        SelfHostedActivationSteps.PhaseExclusiveEntered,
        SelfHostedActivationSteps.AfterFinalize,
        SelfHostedActivationSteps.AfterQuiesce,
        SelfHostedActivationSteps.PhaseDatabaseCheckpointed,
        SelfHostedActivationSteps.PhaseCutoverPrepared,
        SelfHostedActivationSteps.AfterRetainMedia,
        SelfHostedActivationSteps.PhasePreviousMediaRetained,
        SelfHostedActivationSteps.AfterRetainDatabase,
        SelfHostedActivationSteps.PhasePreviousDatabaseRetained,
        SelfHostedActivationSteps.AfterActivateMedia,
        SelfHostedActivationSteps.PhaseCandidateMediaActivated,
        SelfHostedActivationSteps.AfterActivateDatabase,
        SelfHostedActivationSteps.PhaseCandidateDatabaseActivated,
        SelfHostedActivationSteps.AfterReopen,
        SelfHostedActivationSteps.AfterPostVerify,
        SelfHostedActivationSteps.PhasePostActivationVerified,
        SelfHostedActivationSteps.PhaseCommitted,
        SelfHostedActivationSteps.AfterJournalResolved,
    ];

    private static readonly string[] PopulatedOnlyBoundaries =
    [
        SelfHostedActivationSteps.AfterPrepareRetention,
    ];

    public static IEnumerable<object[]> Cases()
    {
        // after:capture exists only for a populated destination (recovery
        // evidence is captured only when a previous library is retained).
        yield return [SelfHostedActivationSteps.AfterCapture, true];
        foreach (var boundary in PhaseABoundaries.Where(step => step != SelfHostedActivationSteps.AfterCapture))
        {
            yield return [boundary, true];
            yield return [boundary, false];
        }

        foreach (var boundary in SharedBoundaries)
        {
            yield return [boundary, true];
            yield return [boundary, false];
        }

        foreach (var boundary in PopulatedOnlyBoundaries)
        {
            yield return [boundary, true];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CrashAtEveryBoundary_ConvergesOnOneCompleteGeneration(string boundary, bool populated)
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated);

        var crashed = false;
        try
        {
            await bed.ActivateAsync(confirm: true, observer: step =>
            {
                if (string.Equals(step, boundary, StringComparison.Ordinal))
                {
                    throw new SelfHostedActivationAbandonedException();
                }
            });
        }
        catch (SelfHostedActivationAbandonedException)
        {
            crashed = true;
        }

        crashed.Should().BeTrue($"the {boundary} boundary must be reached");

        await bed.RecoverHostAsync();

        // A real restart waits for the crashed owner's job lease to expire; the
        // fake clock is advanced instead of sleeping.
        bed.Clock.Advance(SelfHostedActivationCoordinator.ActivationLeaseDuration + TimeSpan.FromMinutes(1));

        var durablyCommitted = boundary is SelfHostedActivationSteps.PhaseCommitted
            or SelfHostedActivationSteps.AfterJournalResolved;
        if (durablyCommitted)
        {
            await bed.AssertImportedGenerationAsync();
        }
        else
        {
            bed.AssertOriginalGeneration();
        }

        var job = await bed.ReadJobAsync();
        if (boundary == SelfHostedActivationSteps.AfterJournalResolved)
        {
            job.State.Should().Be((int)MigrationJobState.Completed);
            (await bed.ActivateAsync(confirm: true)).Outcome
                .Should().Be(SelfHostedActivationOutcome.AlreadyCompleted);
            return;
        }

        // A crash never invents a terminal failure; the job stays retryable.
        job.State.Should().BeOneOf(
            (int)MigrationJobState.ReadyToActivate,
            (int)MigrationJobState.Activating,
            (int)MigrationJobState.Completed);

        // Where rollback happened a fresh activation retry must succeed.
        var retry = await bed.ActivateAsync(confirm: true);
        retry.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.Completed);
        (await bed.CurrentRevisionAsync()).Should().Be(ActivationCoordinatorTemplate.AdvancedRevision);
        await bed.AssertImportedGenerationAsync();
    }

    [Fact]
    public async Task WithoutTheMediaRecoveryStep_TheSameCrashAdmitsADetectableMixedGeneration()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        await CrashAndStopAtAsync(bed, SelfHostedActivationSteps.PhaseCandidateMediaActivated);

        // The database step alone restores the original database while the
        // imported media stays live: a mix the production reconciler prevents
        // because the media step restores the matching root.
        await bed.ReconcileWithStepsAsync(mediaStep: false, databaseStep: true, emptyDiscardStep: true);

        var portable = ActivationCoordinatorTemplate.DumpPortable(bed.Paths.LiveDatabase);
        portable.Should().BeEquivalentTo(bed.OriginalPortable);
        ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia)
            .Should().NotBeEquivalentTo(bed.OriginalMedia);
        ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia)
            .Should().BeEquivalentTo(bed.ExpectedImportedMedia);
    }

    [Fact]
    public async Task WithoutTheDatabaseRecoveryStep_TheSameCrashAdmitsADetectableMixedGeneration()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        await CrashAndStopAtAsync(bed, SelfHostedActivationSteps.PhaseCandidateDatabaseActivated);

        // The media step alone restores the original media while the imported
        // database stays live: the mirror-image mix.
        await bed.ReconcileWithStepsAsync(mediaStep: true, databaseStep: false, emptyDiscardStep: true);

        await using (var scope = bed.Host.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Nostos.Backend.Data.NostosDbContext>();
            var report = await new PortableLibraryVerifier()
                .VerifyCandidateAsync(db, bed.Paths.LiveMedia, bed.Prepared, bed.ExpectedHandle, default);
            report.Passed.Should().BeFalse("the database is imported while the media was restored");
        }

        ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia)
            .Should().BeEquivalentTo(bed.OriginalMedia);
    }

    [Fact]
    public async Task WithoutTheEmptyRetentionStep_ACommittedEmptyCutoverLeavesForbiddenScratch()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: false);
        await CrashAndStopAtAsync(bed, SelfHostedActivationSteps.PhaseCommitted);

        await bed.ReconcileWithStepsAsync(mediaStep: true, databaseStep: true, emptyDiscardStep: false);
        var retained = File.Exists(bed.Paths.PreviousDatabase(bed.JobId))
            || Directory.Exists(bed.Paths.PreviousMedia(bed.JobId));
        retained.Should().BeTrue(
            "without the discard step the committed empty cutover leaves rollback scratch the plan forbids retaining");

        // The production step removes exactly that scratch on roll-forward.
        var manifests = new SelfHostedRecoveryManifestStore(bed.Paths);
        new SelfHostedActivationEmptyRetentionStep(bed.Paths, manifests)
            .Execute(bed.Journals.ReadResolved(bed.JobId)!, SelfHostedRecoveryAction.RollForwardCandidate);
        File.Exists(bed.Paths.PreviousDatabase(bed.JobId)).Should().BeFalse();
        Directory.Exists(bed.Paths.PreviousMedia(bed.JobId)).Should().BeFalse();
    }

    private static async Task CrashAndStopAtAsync(ActivationCoordinatorTestBed bed, string boundary)
    {
        var crashed = false;
        try
        {
            await bed.ActivateAsync(confirm: true, observer: step =>
            {
                if (string.Equals(step, boundary, StringComparison.Ordinal))
                {
                    throw new SelfHostedActivationAbandonedException();
                }
            });
        }
        catch (SelfHostedActivationAbandonedException)
        {
            crashed = true;
        }

        crashed.Should().BeTrue($"the {boundary} boundary must be reached");
    }
}
