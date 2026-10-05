using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 9 happy paths: after a populated replacement, restoring the retained
/// copy returns the original portable library and media byte-for-byte, keeps
/// the CURRENT host operational state (the previous library's week-old host
/// rows are never resurrected), advances the revision exactly once, and retains
/// the replaced library as a new seven-day recovery copy so the restore is
/// itself reversible.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class RecoveryRestoreHappyPathTests
{
    [Fact]
    public async Task PopulatedActivationThenRestore_ReturnsTheOriginalLibrary_KeepsCurrentHostState_AndRetainsTheReplacedLibrary()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);

        // Distinguish the current generation from the retained one: a portable
        // edit (new collection + revision bump) and a host-operational record.
        await bed.WritePortableRevisionBumpAsync();
        await bed.WriteHostRecordAsync();
        var before = bed.SnapshotLiveGeneration();
        var beforeRevision = await bed.CurrentRevisionAsync();

        var result = await bed.RestoreAsync();

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored,
            $"error code: {result.ErrorCode}; manifest: {bed.ReadRecoveryManifest()?.RestoreError}");
        result.Status.Should().Be(MigrationRecoveryStatus.Restored);

        // The restored portable state and media are the retained original library.
        await bed.AssertRestoredPortableGenerationAsync();

        // The portable edit is gone; every host-operational table keeps the
        // CURRENT state except the revision and the replaced library's new
        // claim. Portable tables are asserted separately above.
        var after = bed.SnapshotLiveGeneration();
        foreach (var table in before.Tables.Keys)
        {
            if (table is "LibraryStates" or "MigrationStorageReservations"
                || ActivationCoordinatorTestBed.PortableTableNames.Contains(table, StringComparer.Ordinal))
            {
                continue;
            }

            after.Tables[table].Should().BeEquivalentTo(
                before.Tables[table], $"table {table} must keep the current host state");
        }

        (await bed.CurrentRevisionAsync()).Should().Be(
            beforeRevision + 1, "one restore is one committed library-state mutation");

        var beforeReservations = before.Tables["MigrationStorageReservations"].ToHashSet();
        var afterReservations = after.Tables["MigrationStorageReservations"];
        afterReservations.Count(row => beforeReservations.Contains(row)).Should().Be(
            beforeReservations.Count, "every pre-existing reservation row is unchanged");
        var added = afterReservations.Except(beforeReservations).ToArray();
        added.Should().HaveCount(1, "the replaced library holds exactly one new retention claim");

        // The replaced library is retained as a new available recovery copy.
        var restored = bed.ReadRecoveryManifest()!;
        restored.Status.Should().Be(MigrationRecoveryStatus.Restored);
        restored.RestoreOperationId.Should().NotBeNull();
        restored.RestoredAtUtc.Should().NotBeNull();
        var replacedId = restored.RestoreOperationId!.Value;
        added[0].Should().ContainEquivalentOf(replacedId.ToString(), "the new claim belongs to the restore operation");

        var replaced = bed.ReadRecoveryManifest(replacedId);
        replaced.Should().NotBeNull("the replaced library must be a normal available recovery copy");
        replaced!.Status.Should().Be(MigrationRecoveryStatus.Available);
        replaced.RestoreOperationId.Should().BeNull();
        (replaced.ExpiresAtUtc - replaced.CreatedAtUtc).Should().Be(
            TimeSpan.FromDays(MigrationContractLimits.RecoveryRetentionDays));

        var replacedDatabase = bed.Paths.PreviousDatabase(replacedId);
        new FileInfo(replacedDatabase).Length.Should().Be(replaced.DatabaseBytes);
        RecoveryRestoreHappyPathTests.Sha256Hex(File.ReadAllBytes(replacedDatabase))
            .Should().Be(replaced.DatabaseSha256);
        ActivationBuildFixture.MediaSnapshot(bed.Paths.PreviousMedia(replacedId))
            .Should().BeEquivalentTo(bed.ExpectedImportedMedia,
                "the replaced imported generation is retained byte-for-byte");

        // The cutover journal committed and candidate artifacts were consumed.
        bed.Journals.ReadResolved(replacedId)!.Phase.Should().Be(SelfHostedActivationPhase.Committed);
        File.Exists(bed.Paths.CandidateDatabase(replacedId)).Should().BeFalse();
        Directory.Exists(bed.Paths.CandidateMedia(replacedId)).Should().BeFalse();
        File.Exists(bed.Paths.RestoreSourceDatabase(replacedId)).Should().BeFalse();
        File.Exists(bed.Paths.RestorePayload(replacedId)).Should().BeFalse();
    }

    [Fact]
    public async Task Restore_WithoutConfirmation_IsRefused_AndNothingChanges()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        var before = bed.SnapshotLiveGeneration();

        var failure = await FluentActions
            .Awaiting(() => bed.RequestRestoreOnlyAsync(confirm: false))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.ConfirmationRequired);

        bed.AssertGenerationEquals(before, "an unconfirmed restore must not touch the library");
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Available);
    }

    [Fact]
    public async Task Restore_WithAStaleRevision_IsRefused_AndNothingChanges()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        var before = bed.SnapshotLiveGeneration();

        var failure = await FluentActions
            .Awaiting(() => bed.RequestRestoreOnlyAsync(revision: "stale-revision"))
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.DestinationConflict);

        bed.AssertGenerationEquals(before, "a stale confirmation must not touch the library");
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Available);
    }

    [Fact]
    public async Task Restore_AfterTheLiveLibraryChangedAgain_IsRefused_AndTheInterveningWriteSurvives()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);

        // Claim with the current revision, then write before the worker runs.
        await bed.RequestRestoreOnlyAsync();
        await bed.WritePortableRevisionBumpAsync();
        var before = bed.SnapshotLiveGeneration();

        var result = await bed.ResumeRestoreAsync();

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Interrupted);
        result.ErrorCode.Should().Be(MigrationActivationErrorCodes.DestinationConflict);
        bed.AssertGenerationEquals(before, "the intervening write must survive");
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Available,
            "the copy returns to Available so the user can confirm a fresh revision");
    }

    [Fact]
    public async Task ARepeatedRestoreRequest_AfterCompletion_IsAnIdempotentAlreadyRestored()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);

        (await bed.RestoreAsync()).Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored);
        var after = bed.SnapshotLiveGeneration();
        var runs = bed.Host.GetRequiredService<SelfHostedActivationDispatcher>().StartedRestoreRunCount;

        var again = await bed.ResumeRestoreAsync();
        again.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored,
            "a repeated request is an idempotent success, not a second run");
        bed.Host.GetRequiredService<SelfHostedActivationDispatcher>().StartedRestoreRunCount
            .Should().Be(runs, "a repeated request must not start a second run");
        bed.AssertGenerationEquals(after, "a repeated run must not restore twice");
    }

    [Fact]
    public async Task RestoringTheNewRecoveryCopy_BringsTheReplacedImportedLibraryBack()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();
        var importedTables = ActivationBuildFixture.DumpAllTables(bed.Paths.LiveDatabase);

        // First restore: the original library is back and the imported
        // generation is retained as a new available copy.
        (await bed.RestoreAsync()).Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored);
        var replacedId = bed.ReadRecoveryManifest()!.RestoreOperationId!.Value;
        var beforeSecond = await bed.CurrentRevisionAsync();

        // Restoring that new copy brings the imported generation back.
        var result = await bed.RestoreAsync(recoveryId: replacedId);

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored);
        bed.AssertPortableEquals(importedTables, "the replaced generation is restored");
        ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia)
            .Should().BeEquivalentTo(bed.ExpectedImportedMedia,
                "the replaced generation's media is restored");
        (await bed.CurrentRevisionAsync()).Should().Be(beforeSecond + 1);
        bed.ReadRecoveryManifest(replacedId)!.Status.Should().Be(MigrationRecoveryStatus.Restored,
            "the source copy is consumed by the restore");

        // The library that was just replaced is retained again, so restore is
        // reversible in both directions.
        var newCopyId = bed.ReadRecoveryManifest(replacedId)!.RestoreOperationId!.Value;
        newCopyId.Should().NotBe(replacedId);
        var newCopy = bed.ReadRecoveryManifest(newCopyId);
        newCopy.Should().NotBeNull();
        newCopy!.Status.Should().Be(MigrationRecoveryStatus.Available);
        ActivationBuildFixture.MediaSnapshot(bed.Paths.PreviousMedia(newCopyId))
            .Should().BeEquivalentTo(bed.OriginalMedia,
                "the just-replaced original library is retained byte-for-byte");
    }

    internal static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}
