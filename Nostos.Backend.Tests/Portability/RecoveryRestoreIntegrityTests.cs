using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 9 integrity: a retained copy is never trusted because its manifest
/// says <c>Available</c>. Every component hash is re-verified before any live
/// mutation, tamper and expiry fail closed, and a copy retained by an older
/// schema is upgraded on a working copy (never the retained original) or
/// refused when this build cannot interpret it.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class RecoveryRestoreIntegrityTests
{
    private static async Task<ActivationCoordinatorTestBed> ActivatedBedAsync()
    {
        var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        await bed.WritePortableRevisionBumpAsync();
        await bed.WriteHostRecordAsync();
        return bed;
    }

    [Fact]
    public async Task OneChangedMediaByte_IsRefusedBeforeAnyLiveMutation()
    {
        await using var bed = await ActivatedBedAsync();
        var before = bed.SnapshotLiveGeneration();
        bed.TamperRecoveryMediaByte();

        var result = await bed.RestoreAsync();

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Interrupted);
        result.ErrorCode.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        bed.AssertGenerationEquals(before, "a tampered copy must be refused before any live mutation");
        var manifest = bed.ReadRecoveryManifest()!;
        manifest.Status.Should().Be(MigrationRecoveryStatus.Failed);
        manifest.RestoreError.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
    }

    [Fact]
    public async Task OneChangedDatabaseByte_IsRefusedBeforeAnyLiveMutation()
    {
        await using var bed = await ActivatedBedAsync();
        var before = bed.SnapshotLiveGeneration();
        bed.TamperRecoveryDatabaseByte();

        var result = await bed.RestoreAsync();

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Interrupted);
        result.ErrorCode.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        bed.AssertGenerationEquals(before, "a tampered copy must be refused before any live mutation");
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Failed);
    }

    [Fact]
    public async Task CorruptManifest_IsRefused_AndTheLiveLibraryIsUntouched()
    {
        await using var bed = await ActivatedBedAsync();
        var before = bed.SnapshotLiveGeneration();
        bed.CorruptRecoveryManifest();

        var failure = await FluentActions
            .Awaiting(() => bed.RequestRestoreOnlyAsync())
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        bed.AssertGenerationEquals(before, "a corrupt manifest must fail closed without touching the library");
    }

    [Fact]
    public async Task ExpiredCopy_IsRefused_AndTheLiveLibraryIsUntouched()
    {
        await using var bed = await ActivatedBedAsync();
        var before = bed.SnapshotLiveGeneration();
        bed.ExpireRecoveryCopy();

        var failure = await FluentActions
            .Awaiting(() => bed.RequestRestoreOnlyAsync())
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryExpired);
        bed.AssertGenerationEquals(before, "an expired copy must not be restored");
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Available);
    }

    [Fact]
    public async Task OlderSchemaCopy_IsMigratedForwardOnAWorkingCopy_AndRestored()
    {
        await using var bed = await ActivatedBedAsync();
        var beforeRevision = await bed.CurrentRevisionAsync();
        await bed.DowngradeRecoveryDatabaseOneMigrationAsync();

        // The retained original is upgraded only on a working copy; the restore
        // itself materializes the current schema plus current host state.
        var result = await bed.RestoreAsync();

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored,
            $"older-schema restore must migrate forward; error {result.ErrorCode}");
        await bed.AssertRestoredPortableGenerationAsync();

        // The restored database carries the current schema and current host
        // state, never the downgraded retained schema.
        await using (var db = bed.OpenDatabase())
        {
            (await db.MigrationJobRecords.CountAsync()).Should().BeGreaterThanOrEqualTo(1);
            var job = await db.MigrationJobRecords.FindAsync(bed.JobId);
            job!.State.Should().Be((int)MigrationJobState.Completed);
        }

        (await bed.CurrentRevisionAsync()).Should().Be(beforeRevision + 1);
    }

    [Fact]
    public async Task UnknownNewerSchemaCopy_IsRefusedBeforeAnyLiveMutation()
    {
        await using var bed = await ActivatedBedAsync();
        var before = bed.SnapshotLiveGeneration();
        bed.AddUnknownMigrationToRecovery();

        var result = await bed.RestoreAsync();

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Interrupted);
        result.ErrorCode.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        bed.AssertGenerationEquals(before, "a newer-schema copy must be refused before any live mutation");
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Failed);
    }

    [Fact]
    public void ManifestsWrittenBeforeTheRestoreFieldsExisted_StillDecode()
    {
        var created = DateTimeOffset.UtcNow;
        var manifest = new SelfHostedRecoveryManifest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            created,
            created.AddDays(MigrationContractLimits.RecoveryRetentionDays),
            MigrationRecoveryStatus.Available,
            "revision",
            new MigrationExistingCounts(),
            10,
            20,
            new string('a', 64),
            [],
            DatabaseSchemaVersion: "20260101000000_Initial",
            DatabaseMigrationCount: 1,
            DatabaseRetained: true,
            MediaRetained: true,
            RetentionReservationId: Guid.NewGuid());

        var payload = System.Text.Json.JsonSerializer.Serialize(manifest);
        var node = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
        foreach (var field in new[]
                 {
                     "RestoreOperationId", "RestoreDestinationRevision", "RestoreError", "RestoredAtUtc",
                 })
        {
            node.Remove(field);
        }

        var stripped = node.ToJsonString();
        var sha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stripped)))
            .ToLowerInvariant();
        var envelope = System.Text.Json.JsonSerializer.Serialize(
            new SelfHostedActivationDocument.Envelope(1, stripped, sha));

        var decoded = SelfHostedActivationDocument.Decode<SelfHostedRecoveryManifest>(envelope);

        decoded.Status.Should().Be(MigrationRecoveryStatus.Available);
        decoded.RestoreOperationId.Should().BeNull("manifests from earlier builds must keep decoding");
        decoded.RestoreError.Should().BeNull();
    }
}
