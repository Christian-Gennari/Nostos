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
        var sourceSha = bed.SourceRecoveryDatabaseSha256();

        // The retained original is upgraded only on a working copy; the restore
        // itself materializes the current schema plus current host state.
        var result = await bed.RestoreAsync();

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored,
            $"older-schema restore must migrate forward; error {result.ErrorCode}");
        await bed.AssertRestoredPortableGenerationAsync();
        bed.SourceRecoveryDatabaseSha256().Should().Be(
            sourceSha, "the retained original must never be modified by the upgrade");

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

    [Theory]
    [InlineData("DELETE FROM Collections WHERE rowid = (SELECT MIN(rowid) FROM Collections);")]
    [InlineData("UPDATE Collections SET Name = Name || 'X' WHERE rowid = (SELECT MIN(rowid) FROM Collections);")]
    [InlineData("INSERT INTO Collections (Id, Name, ParentId) VALUES ('AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE', 'MIGRATION-GHOST', NULL);")]
    public async Task ALossyForwardMigration_IsRefusedBeforeAnyLiveMutation(string lossySql)
    {
        await using var bed = await ActivatedBedAsync();
        var before = bed.SnapshotLiveGeneration();
        await bed.DowngradeRecoveryDatabaseOneMigrationAsync();
        var sourceSha = bed.SourceRecoveryDatabaseSha256();
        bed.RecoveryMigrationOverride = async db => await db.Database.ExecuteSqlRawAsync(lossySql);

        var result = await bed.RestoreAsync();

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Interrupted);
        result.ErrorCode.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        bed.AssertGenerationEquals(before, "a lossy migration must not replace the live library");
        bed.SourceRecoveryDatabaseSha256().Should().Be(
            sourceSha, "the retained original must survive a refused upgrade byte-for-byte");
        var manifest = bed.ReadRecoveryManifest()!;
        manifest.Status.Should().Be(MigrationRecoveryStatus.Failed);
        manifest.RestoreError.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        Directory.EnumerateFiles(bed.Paths.JournalRoot, "recovery-source.db*", SearchOption.AllDirectories)
            .Should().BeEmpty("the refused attempt cleans its working files");
        Directory.EnumerateFiles(bed.Paths.JournalRoot, "recovery-payload.json", SearchOption.AllDirectories)
            .Should().BeEmpty("the refused attempt cleans its payload file");
    }

    [Fact]
    public async Task ALossyMigrationThatDropsAMembership_IsRefusedBeforeAnyLiveMutation()
    {
        await using var bed = await ActivatedBedAsync();
        await bed.SeedRecoveryMembershipAsync();
        var before = bed.SnapshotLiveGeneration();
        await bed.DowngradeRecoveryDatabaseOneMigrationAsync();
        var sourceSha = bed.SourceRecoveryDatabaseSha256();
        bed.RecoveryMigrationOverride = async db => await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM BookCollections WHERE rowid = (SELECT MIN(rowid) FROM BookCollections);");

        var result = await bed.RestoreAsync();

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Interrupted);
        result.ErrorCode.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        bed.AssertGenerationEquals(before, "a dropped membership must not replace the live library");
        bed.SourceRecoveryDatabaseSha256().Should().Be(sourceSha);
        bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Failed);
    }

    [Fact]
    public async Task DeclaredTableAndColumnRenames_AreHandledByTheBaseline()
    {
        await using var bed = await ActivatedBedAsync();
        // Before the Concepts -> Topics table/column rename.
        await bed.DowngradeRecoveryDatabaseToAsync("20261003075922_AddEmbeddingProviderSettings");
        var sourceSha = bed.SourceRecoveryDatabaseSha256();

        var result = await bed.RestoreAsync();

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored,
            $"a declared rename in range must be translated, not rejected; error {result.ErrorCode}");
        await bed.AssertRestoredPortableGenerationAsync();
        bed.SourceRecoveryDatabaseSha256().Should().Be(sourceSha);
    }

    [Fact]
    public async Task CurrentSchemaCopy_TakesTheFastPath_AndRestores()
    {
        await using var bed = await ActivatedBedAsync();
        var sourceSha = bed.SourceRecoveryDatabaseSha256();

        var result = await bed.RestoreAsync();

        result.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored);
        await bed.AssertRestoredPortableGenerationAsync();
        bed.SourceRecoveryDatabaseSha256().Should().Be(sourceSha);
    }

    [Fact]
    public void ManifestCountsMustMatchTheExtractedPayload()
    {
        var expected = new MigrationExistingCounts(
            Works: 1,
            Books: 1,
            Notes: 2,
            Topics: 1,
            NoteTopics: 1,
            Writings: 1,
            WritingNotes: 1,
            Collections: 1,
            BookCollections: 1,
            Acquisitions: 1,
            NoteImportBookLinks: 1,
            AssistantSettings: 1);
        var matching = new MigrationArchiveCounts(
            Works: 1,
            Books: 1,
            Notes: 2,
            Topics: 1,
            NoteTopics: 1,
            Writings: 1,
            WritingNotes: 1,
            Collections: 1,
            CollectionMemberships: 1,
            Acquisitions: 1,
            AssistantSettings: 1,
            NoteImportBookLinks: 1,
            MediaEntries: 0);

        PortableLibraryRecoveryBaseline.RequireCountsPreserved(expected, matching);

        FluentActions
            .Invoking(() => PortableLibraryRecoveryBaseline.RequireCountsPreserved(
                expected, matching with { Notes = 1 }))
            .Should().Throw<PortableRecoveryBaselineException>(
                "a migration that loses a portable row must be detected independently of digests");

        // The assistant singleton row may exist without a portable value.
        PortableLibraryRecoveryBaseline.RequireCountsPreserved(
            expected with { AssistantSettings = 0 },
            matching with { AssistantSettings = 1 });
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
