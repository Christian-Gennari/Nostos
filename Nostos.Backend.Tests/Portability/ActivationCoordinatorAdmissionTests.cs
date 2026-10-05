using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevisionChangeBetweenPhases_AbortsAsStaleDestination(bool populated)
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated);
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
}
