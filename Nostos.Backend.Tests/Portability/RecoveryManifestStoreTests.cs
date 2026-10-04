using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class RecoveryManifestStoreTests
{
    private static SelfHostedRecoveryManifest Manifest(
        Guid jobId,
        MigrationRecoveryStatus status = MigrationRecoveryStatus.Available,
        DateTimeOffset? created = null,
        Guid? retentionReservationId = null)
    {
        var createdAt = created ?? new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        return new SelfHostedRecoveryManifest(
            jobId,
            Guid.NewGuid(),
            createdAt,
            SelfHostedRecoveryManifest.Expiry(createdAt),
            status,
            "revision-1",
            new MigrationExistingCounts(Books: 2, BookCollections: 3),
            4096,
            8192,
            new string('a', 64),
            [new RecoveryMediaDescriptor(Guid.NewGuid(), "book", ".epub", 512, new string('b', 64))],
            DatabaseSchemaVersion: "20260101000000_Initial",
            DatabaseMigrationCount: 2,
            DatabaseRetained: true,
            MediaRetained: true,
            RetentionReservationId: retentionReservationId);
    }

    [Fact]
    public void Manifest_RoundTripsEveryField_AndCarriesNoPaths()
    {
        using var bed = new RecoveryTestBed();
        var store = bed.Manifests;
        var reservationId = Guid.NewGuid();
        var creating = Manifest(bed.JobId, MigrationRecoveryStatus.Creating,
            new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)) with
        {
            DatabaseRetained = false,
            MediaRetained = false,
        };
        store.Write(creating);
        store.Read(bed.JobId).Should().BeEquivalentTo(creating);

        var available = Manifest(bed.JobId, retentionReservationId: reservationId);
        store.Write(available);
        store.Read(bed.JobId).Should().BeEquivalentTo(available);
        (store.Read(bed.JobId)!.ExpiresAtUtc - store.Read(bed.JobId)!.CreatedAtUtc)
            .Should().Be(TimeSpan.FromDays(MigrationContractLimits.RecoveryRetentionDays));

        var json = File.ReadAllText(bed.Paths.RecoveryManifest(bed.JobId));
        json.Should().NotContain("db-volume").And.NotContain("media-volume").And.NotContain(bed.Root);
        foreach (var property in typeof(SelfHostedRecoveryManifest).GetProperties())
            property.Name.Should().NotContain("Path");
    }

    [Fact]
    public void Manifest_TornCorruptUnknownVersionAndMissingFields_FailClosed()
    {
        using var bed = new RecoveryTestBed();
        var store = bed.Manifests;
        var manifest = Manifest(bed.JobId);
        store.Write(manifest);
        var path = bed.Paths.RecoveryManifest(bed.JobId);
        var encoded = SelfHostedActivationDocument.Encode(manifest);
        var bytes = Encoding.UTF8.GetBytes(encoded);

        for (var length = 0; length < bytes.Length; length += 7)
        {
            File.WriteAllBytes(path, bytes[..length]);
            Action read = () => store.Read(bed.JobId);
            read.Should().Throw<MigrationActivationException>()
                .Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        }

        foreach (var corrupt in new[]
        {
            encoded.Replace("revision-1", "revision-2"),
            encoded.Replace("aaaaaaaa", "zzzzzzzz"),
            SelfHostedActivationDocument.Encode(manifest with { ManifestVersion = 2 }),
            SelfHostedActivationDocument.Encode(manifest with { ExpiresAtUtc = manifest.CreatedAtUtc.AddDays(1) }),
            SelfHostedActivationDocument.Encode(manifest with { DatabaseSha256 = "bad" }),
            SelfHostedActivationDocument.Encode(manifest with { OperationId = Guid.Empty }),
            SelfHostedActivationDocument.Encode(manifest with
            {
                Media = [new RecoveryMediaDescriptor(Guid.NewGuid(), "book", ".epub", 1, "not-a-digest")],
            }),
            "{}",
            "null",
            "{",
        })
        {
            File.WriteAllText(path, corrupt);
            Action read = () => store.Read(bed.JobId);
            read.Should().Throw<MigrationActivationException>()
                .Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        }

        // A checksummed payload that drops a required field must fail closed too.
        var envelope = JsonNode.Parse(encoded)!.AsObject();
        var payload = JsonNode.Parse(envelope["PayloadJson"]!.GetValue<string>())!.AsObject();
        payload.Remove("DatabaseRetained");
        File.WriteAllText(path, SelfHostedActivationDocument.Encode(payload));
        Action missing = () => store.Read(bed.JobId);
        missing.Should().Throw<MigrationActivationException>()
            .Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);

        // A manifest addressed to another job in this directory is corrupt.
        File.WriteAllText(path, SelfHostedActivationDocument.Encode(manifest with { JobId = Guid.NewGuid() }));
        Action mismatch = () => store.Read(bed.JobId);
        mismatch.Should().Throw<MigrationActivationException>();
    }

    [Fact]
    public void ReadAll_FailsClosedOnMaterialWithoutManifest_AndSkipsDeletionMarkers()
    {
        using var bed = new RecoveryTestBed();
        var store = bed.Manifests;
        var orphan = Guid.NewGuid();
        bed.CreatePreviousMaterial(orphan, "orphan", "11111111-1111-1111-1111-111111111111/book.epub");
        Action readOrphan = () => store.ReadAll();
        readOrphan.Should().Throw<MigrationActivationException>()
            .Which.Code.Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);

        Directory.Delete(bed.Paths.PreviousMedia(orphan), recursive: true);
        File.Delete(bed.Paths.PreviousDatabase(orphan));

        var visible = Guid.NewGuid();
        store.Write(Manifest(visible));
        var deleting = Guid.NewGuid();
        store.Write(Manifest(deleting));
        store.CreateDeletionMarker(deleting);

        store.ReadAll().Select(m => m.JobId).Should().Equal(visible);
        store.HasDeletionMarker(deleting).Should().BeTrue();
    }

    [Fact]
    public void DeleteUnusedPlan_RemovesTemporaryDocuments_AndNeverMaterial()
    {
        using var bed = new RecoveryTestBed();
        var store = bed.Manifests;
        store.Write(Manifest(bed.JobId, MigrationRecoveryStatus.Creating));
        File.WriteAllText(bed.Paths.TemporaryRecoveryManifest(bed.JobId, Guid.NewGuid()), "torn");
        store.DeleteUnusedPlan(bed.JobId);
        Directory.Exists(Path.GetDirectoryName(bed.Paths.RecoveryManifest(bed.JobId))).Should().BeFalse();
        store.Read(bed.JobId).Should().BeNull();

        var withMaterial = Guid.NewGuid();
        bed.CreatePreviousMaterial(withMaterial, "original", "11111111-1111-1111-1111-111111111111/book.epub");
        store.Write(Manifest(withMaterial));
        store.DeleteUnusedPlan(withMaterial);
        File.Exists(bed.Paths.PreviousDatabase(withMaterial)).Should().BeTrue();
    }
}
