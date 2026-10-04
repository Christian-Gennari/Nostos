using FluentAssertions;
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
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.DestinationConflict);

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
    public async Task PostCommitJobCompletionFailure_KeepsTheActivationCommitted_AndResumes()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var result = await bed.ActivateAsync(confirm: true, observer: reached =>
        {
            if (string.Equals(reached, SelfHostedActivationSteps.AfterReopen, StringComparison.Ordinal))
            {
                // Strip the worker lease from the newly activated database: the
                // post-commit projection then cannot run until a resumed call
                // acquires a fresh lease.
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    $"Data Source={bed.Paths.LiveDatabase};Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE \"MigrationJobRecords\" SET \"MigrationLeaseToken\" = NULL, \"LeaseExpiresAtUtc\" = NULL;";
                command.ExecuteNonQuery();
            }
        });

        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed,
            "the durable commit already happened; post-commit work must not turn it into a failure");
        await bed.AssertImportedGenerationAsync();
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.Activating,
            "job completion is retryable after the committed switch");

        // Resume: a later activation call completes the committed activation.
        var resumed = await bed.ActivateAsync(confirm: true);
        resumed.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        resumed.State.Should().Be(MigrationJobState.Completed);
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.Completed);
        (await bed.CurrentRevisionAsync()).Should().Be(ActivationCoordinatorTemplate.AdvancedRevision,
            "the resumed post-commit path never runs a second cutover");
    }
}
