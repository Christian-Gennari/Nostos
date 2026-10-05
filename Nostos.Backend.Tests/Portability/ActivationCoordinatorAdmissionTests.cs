using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 7 admission hardening: the destination revision is bound through
/// <see cref="Nostos.Backend.Services.Library.ILibraryDestinationRevisionProvider"/>
/// at both checks; "empty" is proven on disk as well as in the database; the
/// post-quiesce window never opens SQLite on the live path; and the empty
/// roll-forward never recursively deletes unverified media.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class ActivationCoordinatorAdmissionTests
{
    [Fact]
    public async Task PopulatedRevisionChangeBetweenPhases_AbortsAsStaleDestination()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var switched = false;
        bed.RevisionOverride = _ => Task.FromResult(switched ? "changed-between-phases" : bed.RevisionToken);

        var failure = await FluentActions
            .Awaiting(() => bed.ActivateAsync(confirm: true, observer: step =>
            {
                if (string.Equals(step, SelfHostedActivationSteps.AfterVerifyCandidate, StringComparison.Ordinal))
                {
                    switched = true;
                }
            }))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.DestinationConflict);

        bed.AssertOriginalGeneration();
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.ReadyToActivate);
    }

    [Fact]
    public async Task EmptyDestinationRevisionChangeBetweenPhases_StillActivatesWithNothingToConfirm()
    {
        // Issue #681: an empty destination needs no confirmation, so a revision
        // that moved while it stayed empty is not a conflict; the exclusive
        // window re-proves emptiness and there is no user content to lose.
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: false);
        var switched = false;
        bed.RevisionOverride = _ => Task.FromResult(switched ? "changed-between-phases" : bed.RevisionToken);

        var result = await bed.ActivateAsync(confirm: false, observer: step =>
        {
            if (string.Equals(step, SelfHostedActivationSteps.AfterVerifyCandidate, StringComparison.Ordinal))
            {
                switched = true;
            }
        });

        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        result.RecoveryStatus.Should().Be(MigrationRecoveryStatus.NotRequired);
        await bed.AssertImportedGenerationAsync();
    }

    [Fact]
    public async Task PopulatedDestination_IsConfirmedAgainstTheCurrentRevision_NotTheJobBaseline()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        // A portable write after job creation (here a real saved collection,
        // the same shape as a reading-progress or note-edit save) advances the
        // live revision: the stored baseline is now stale.
        await bed.WritePortableRevisionBumpAsync();
        var current = await ReadCurrentRevisionAsync(bed);
        current.Should().NotBe(bed.RevisionToken, "the fixture must prove the baseline moved");

        var afterWrite = DumpAllTablesExceptActivationJob(bed);
        var stale = await FluentActions
            .Awaiting(() => ActivateWithRevisionAsync(bed, bed.RevisionToken, confirm: true))
            .Should().ThrowAsync<MigrationActivationException>();
        stale.Which.Code.Should().Be(MigrationActivationErrorCodes.DestinationConflict);
        DumpAllTablesExceptActivationJob(bed).Should().BeEquivalentTo(afterWrite,
            "a stale confirmation must mutate nothing");
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.ReadyToActivate);

        var result = await ActivateWithRevisionAsync(bed, current, confirm: true);
        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        result.RecoveryStatus.Should().Be(MigrationRecoveryStatus.Available);
        await bed.AssertImportedGenerationAsync();
    }

    [Fact]
    public async Task EmptyDestinationThatBecamePopulatedInsideTheWindow_AbortsWithConfirmationRequiredAndMutatesNothing()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: false);
        var revisionAtAdmission = bed.RevisionToken;

        // Admitted as empty (no confirmation asked). A real portable save lands
        // after admission and before the exclusive recheck; snapshot the exact
        // post-write state there so the comparison below proves the aborted
        // activation added nothing of its own.
        Dictionary<string, List<string>>? afterWrite = null;
        var failure = await FluentActions
            .Awaiting(() => ActivateWithRevisionAsync(bed, revisionAtAdmission, confirm: false, observer: step =>
            {
                if (string.Equals(step, SelfHostedActivationSteps.AfterVerifyCandidate, StringComparison.Ordinal))
                {
                    bed.WritePortableRevisionBumpAsync().GetAwaiter().GetResult();
                    afterWrite = DumpAllTablesExceptActivationJob(bed);
                }
            }))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.ConfirmationRequired);

        // The run must not have replaced content the user never confirmed:
        // the destination is exactly the state the test's own write committed.
        afterWrite.Should().NotBeNull("the intervening write must have been observed");
        DumpAllTablesExceptActivationJob(bed).Should().BeEquivalentTo(afterWrite);
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.ReadyToActivate);

        // Confirming the now-populated current generation completes and retains it.
        var current = await ReadCurrentRevisionAsync(bed);
        current.Should().NotBe(revisionAtAdmission);
        var result = await ActivateWithRevisionAsync(bed, current, confirm: true);
        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        result.RecoveryStatus.Should().Be(MigrationRecoveryStatus.Available);
        await bed.AssertImportedGenerationAsync();
    }

    [Fact]
    public async Task ZeroPortableRowsWithOrphanMedia_IsPopulated_RequiresConfirmation_AndRetainsRecovery()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: false);
        bed.WriteOrphanMedia();

        var refusal = await FluentActions
            .Awaiting(() => bed.ActivatePublicAsync())
            .Should().ThrowAsync<MigrationActivationException>();
        refusal.Which.Code.Should().Be(MigrationActivationErrorCodes.ConfirmationRequired);
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.ReadyToActivate);

        var result = await bed.ActivateAsync(confirm: true);
        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        result.RecoveryStatus.Should().Be(MigrationRecoveryStatus.Available);

        var manifest = new SelfHostedRecoveryManifestStore(bed.Paths).Read(bed.JobId);
        manifest.Should().NotBeNull("orphan media must be retained as a full recovery copy");
        manifest!.Status.Should().Be(MigrationRecoveryStatus.Available);

        var orphanSha = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("ORPHAN-USER-MEDIA"))).ToLowerInvariant();
        ActivationCoordinatorHappyPathTests
            .SweepRetainedMedia(bed.Paths.PreviousMedia(bed.JobId))
            .Should().Contain(item => item.Sha256 == orphanSha,
                "the orphan media file must be in the retained recovery copy, not deleted");

        await bed.AssertImportedGenerationAsync();
    }

    [Fact]
    public async Task NonEmptyPreviousMedia_IsNeverRecursivelyDeleted()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: false);
        var previous = bed.Paths.PreviousMedia(bed.JobId);
        Directory.CreateDirectory(previous);
        await File.WriteAllTextAsync(Path.Combine(previous, "orphan.bin"), "KEEP-ME");

        var journal = new SelfHostedActivationJournal(
            bed.JobId,
            Guid.NewGuid(),
            SelfHostedActivationPhase.Committed,
            bed.RevisionToken,
            RetainPreviousLibrary: false,
            DateTimeOffset.UtcNow);
        var step = new SelfHostedActivationEmptyRetentionStep(
            bed.Paths, new SelfHostedRecoveryManifestStore(bed.Paths));
        step.Execute(journal, SelfHostedRecoveryAction.RollForwardCandidate);

        File.Exists(Path.Combine(previous, "orphan.bin")).Should().BeTrue(
            "a directory with files must never be recursively deleted by the empty roll-forward");
    }

    [Fact]
    public async Task NoSqliteConnectionToTheLiveDatabaseBetweenQuiesceAndTheSwap()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var livePath = Path.GetFullPath(bed.Paths.LiveDatabase);
        var guardActive = false;
        SelfHostedSqliteFile.ConnectionOpeningForTesting = path =>
        {
            if (guardActive && string.Equals(Path.GetFullPath(path), livePath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SQLite opened the live database after quiesce");
            }
        };

        try
        {
            var result = await bed.ActivateAsync(confirm: true, observer: step =>
            {
                if (string.Equals(step, SelfHostedActivationSteps.AfterQuiesce, StringComparison.Ordinal))
                {
                    guardActive = true;
                }

                if (string.Equals(step, SelfHostedActivationSteps.PhaseCandidateDatabaseActivated, StringComparison.Ordinal))
                {
                    guardActive = false;
                }

                if (guardActive)
                {
                    File.Exists(bed.Paths.LiveDatabase + "-wal").Should().BeFalse(
                        $"no WAL may exist at {step}");
                    File.Exists(bed.Paths.LiveDatabase + "-shm").Should().BeFalse(
                        $"no SHM may exist at {step}");
                }
            });
            result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        }
        finally
        {
            SelfHostedSqliteFile.ConnectionOpeningForTesting = null;
        }
    }

    private static async Task<string> ReadCurrentRevisionAsync(ActivationCoordinatorTestBed bed)
    {
        await using var scope = bed.Host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILibraryDestinationRevisionProvider>()
            .GetCurrentAsync(default);
    }

    private static async Task<SelfHostedActivationResult> ActivateWithRevisionAsync(
        ActivationCoordinatorTestBed bed,
        string revision,
        bool confirm,
        Action<string>? observer = null)
    {
        await using var scope = bed.Host.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedActivationCoordinator>();
        coordinator.StepObserverForTesting = observer;
        return await coordinator.ActivateAsync(
            bed.JobId, new MigrationActivateRequest(revision, confirm), default);
    }

    /// <summary>
    /// Whole-database dump excluding only the activation job's own row (whose
    /// lease/state metadata the protocol legitimately changes); every other
    /// row, in every table, must be identical across the aborted run.
    /// </summary>
    private static Dictionary<string, List<string>> DumpAllTablesExceptActivationJob(
        ActivationCoordinatorTestBed bed)
    {
        var all = ActivationBuildFixture.DumpAllTables(bed.Paths.LiveDatabase);
        all["MigrationJobRecords"] = all["MigrationJobRecords"]
            .Where(row => !string.Equals(
                row.Split('|', 2)[0], bed.JobId.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return all;
    }
}
