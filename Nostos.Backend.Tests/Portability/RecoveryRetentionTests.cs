using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class RecoveryRetentionTests
{
    private static readonly Guid FirstBook = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondBook = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Capture_PinsEveryMediaFileWithFullHashes_AndRecordsSchemaLevel()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var service = bed.CreateService();

        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision,
            new MigrationExistingCounts(Books: 2, BookCollections: 4), default);

        capture.OperationId.Should().Be(bed.OperationId);
        capture.PreviousDestinationRevision.Should().Be(bed.Revision);
        capture.CaptureStartedAtUtc.Should().Be(bed.Clock.UtcNow);
        capture.DatabaseSchemaVersion.Should().Be("20260101000000_Initial");
        capture.DatabaseMigrationCount.Should().Be(1);
        capture.DatabaseBytes.Should().Be(new FileInfo(bed.Paths.LiveDatabase).Length);
        capture.Media.Should().HaveCount(5);
        capture.Media.Single(pin => pin.RelativePath == $"{FirstBook:N}/book.epub").Kind.Should().Be("book");
        capture.Media.Single(pin => pin.RelativePath == $"{FirstBook:N}/cover.jpg").Kind.Should().Be("cover");
        capture.Media.Single(pin => pin.RelativePath == $"{FirstBook:N}/cover-thumb-160.webp").Kind.Should().Be("thumbnail");
        capture.Media.Single(pin => pin.RelativePath == $"{SecondBook:N}/book.epub.partial").Kind.Should().Be("partial");
        foreach (var pin in capture.Media)
        {
            var bytes = File.ReadAllBytes(Path.Combine(bed.Paths.LiveMedia, pin.RelativePath));
            pin.Bytes.Should().Be(bytes.Length);
            pin.Sha256.Should().Be(RecoveryTestBed.Sha256Hex(bytes));
        }

        capture.MediaBytes.Should().Be(capture.Media.Sum(pin => pin.Bytes));
    }

    [Fact]
    public async Task PrepareRetention_RequiresJournalIdentityAndExclusiveLease()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);

        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        Func<Task> withoutJournal = () => service.PrepareRetentionAsync(bed.JobId, capture, lease, default);
        await withoutJournal.Should().ThrowAsync<MigrationActivationException>()
            .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryFailed);

        bed.SeedJournal(operationId: Guid.NewGuid());
        Func<Task> wrongIdentity = () => service.PrepareRetentionAsync(bed.JobId, capture, lease, default);
        await wrongIdentity.Should().ThrowAsync<MigrationActivationException>()
            .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryFailed);

        var foreignGate = new LibraryMaintenanceCoordinator();
        var foreignLease = await foreignGate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        Func<Task> foreign = () => service.PrepareRetentionAsync(bed.JobId, capture, foreignLease, default);
        await foreign.Should().ThrowAsync<InvalidOperationException>();
        await foreignLease.DisposeAsync();

        File.Delete(bed.Paths.Journal(bed.JobId));
        var journal = bed.SeedJournal();
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        var manifest = await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);
        manifest.Status.Should().Be(MigrationRecoveryStatus.Creating);
        manifest.MediaRetained.Should().BeFalse();
        manifest.DatabaseRetained.Should().BeFalse();
        manifest.Media.Should().HaveCount(capture.Media.Count);
        manifest.DatabaseSchemaVersion.Should().Be("20260101000000_Initial");
        bed.Manifests.Read(bed.JobId).Should().BeEquivalentTo(manifest);
        await lease.DisposeAsync();
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("media")]
    [InlineData("database")]
    public async Task CrashBeforeCommit_AlwaysLeavesOneIntactManifestDescribedGeneration(string crashPoint)
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);
        var journal = bed.SeedJournal();
        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        var manifest = await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);
        AssertComponentLocationsAreIntact(bed, manifest);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);

        if (crashPoint is "media" or "database")
        {
            manifest = await service.RetainMediaAsync(bed.JobId, lease, default);
            manifest.MediaRetained.Should().BeTrue();
            (await service.RetainMediaAsync(bed.JobId, lease, default)).Should().BeEquivalentTo(manifest);
            journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousMediaRetained, lease);
            AssertComponentLocationsAreIntact(bed, manifest);
        }

        if (crashPoint == "database")
        {
            manifest = await service.RetainDatabaseAsync(bed.JobId, lease, default);
            manifest.DatabaseRetained.Should().BeTrue();
            (await service.RetainDatabaseAsync(bed.JobId, lease, default)).Should().BeEquivalentTo(manifest);
            journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousDatabaseRetained, lease);
            AssertComponentLocationsAreIntact(bed, manifest);
        }

        await lease.DisposeAsync();
        await bed.ReconcileAsync();

        RecoveryTestBed.DatabaseGeneration(bed.Paths.LiveDatabase).Should().Be("original");
        AssertMediaGeneration(bed.Paths.LiveMedia, "original");
        File.Exists(bed.Paths.Journal(bed.JobId)).Should().BeFalse(
            "a cutover-prepared journal rolls back and resolves even when no rename happened");
        File.Exists(bed.Paths.ResolvedJournal(bed.JobId)).Should().BeTrue();
    }

    [Fact]
    public async Task RetainDatabase_RefusesAnUncheckpointedWal_AndRemovesAnEmptyOne()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);
        var journal = bed.SeedJournal();
        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);

        Func<Task> beforeMedia = () => service.RetainDatabaseAsync(bed.JobId, lease, default);
        await beforeMedia.Should().ThrowAsync<MigrationActivationException>();

        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
        await service.RetainMediaAsync(bed.JobId, lease, default);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousMediaRetained, lease);
        await File.WriteAllTextAsync(bed.Paths.LiveDatabase + "-wal", "committed frames");
        Func<Task> hotWal = () => service.RetainDatabaseAsync(bed.JobId, lease, default);
        await hotWal.Should().ThrowAsync<MigrationActivationException>()
            .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryFailed);
        File.Exists(bed.Paths.LiveDatabase).Should().BeTrue();

        File.Delete(bed.Paths.LiveDatabase + "-wal");
        await File.WriteAllTextAsync(bed.Paths.LiveDatabase + "-shm", "");
        var manifest = await service.RetainDatabaseAsync(bed.JobId, lease, default);
        manifest.DatabaseRetained.Should().BeTrue();
        File.Exists(bed.Paths.LiveDatabase).Should().BeFalse();
        File.Exists(bed.Paths.PreviousDatabase(bed.JobId)).Should().BeTrue();
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Finalize_ClaimsTheRetainedBytesAndSetsTheSevenDayExpiry()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        Guid transferReservationId;
        await using (var seed = bed.OpenDatabase())
        {
            var capacity = new TransferStorageCapacity(seed, bed.TransferVolume, Options.Create(bed.Options), bed.Clock);
            var reserved = await capacity.TryReserveAsync(4096, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default);
            await capacity.ClaimAsync(reserved.ReservationId!.Value, bed.JobId, default);
            transferReservationId = reserved.ReservationId.Value;
        }

        bed.CloneLiveDatabaseToCandidate();
        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);
        var journal = bed.SeedJournal();
        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
        await service.RetainMediaAsync(bed.JobId, lease, default);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousMediaRetained, lease);
        await service.RetainDatabaseAsync(bed.JobId, lease, default);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousDatabaseRetained, lease);
        Directory.Move(bed.Paths.CandidateMedia(bed.JobId), bed.Paths.LiveMedia);
        File.Move(bed.Paths.CandidateDatabase(bed.JobId), bed.Paths.LiveDatabase);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CandidateMediaActivated, lease);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CandidateDatabaseActivated, lease);

        var manifest = await service.FinalizeRetentionAsync(bed.JobId, transferReservationId, lease, default);

        manifest.Status.Should().Be(MigrationRecoveryStatus.Available);
        manifest.RetentionReservationId.Should().NotBeNull();
        manifest.CreatedAtUtc.Should().Be(bed.Clock.UtcNow);
        manifest.ExpiresAtUtc.Should().Be(bed.Clock.UtcNow.AddDays(MigrationContractLimits.RecoveryRetentionDays));
        await using var verify = bed.OpenDatabase();
        var retention = await verify.MigrationStorageReservations.AsNoTracking()
            .SingleAsync(reservation => reservation.Id == manifest.RetentionReservationId);
        retention.ClaimedJobId.Should().Be(bed.JobId);
        retention.ReleasedAtUtc.Should().BeNull();
        retention.ReservedBytes.Should().Be(manifest.TotalBytes);

        // The claimed retention never expires on the preflight clock.
        bed.Clock.Advance(TimeSpan.FromDays(30));
        var capacityService = new TransferStorageCapacity(verify, bed.TransferVolume, Options.Create(bed.Options), bed.Clock);
        (await capacityService.GetSnapshotAsync(default)).OutstandingReservedBytes.Should().Be(manifest.TotalBytes);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Abandon_RemovesAnUnusedPlanButRefusesWhileMaterialExists()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);
        var journal = bed.SeedJournal();
        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);

        await service.AbandonAsync(bed.JobId, lease, default);
        bed.Manifests.Read(bed.JobId).Should().BeNull();

        await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
        await service.RetainMediaAsync(bed.JobId, lease, default);
        Func<Task> withMaterial = () => service.AbandonAsync(bed.JobId, lease, default);
        await withMaterial.Should().ThrowAsync<MigrationActivationException>()
            .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryFailed);
        Directory.Exists(bed.Paths.PreviousMedia(bed.JobId)).Should().BeTrue();
        await lease.DisposeAsync();
    }

    private static void AssertComponentLocationsAreIntact(RecoveryTestBed bed, SelfHostedRecoveryManifest manifest)
    {
        if (File.Exists(bed.Paths.LiveDatabase))
        {
            RecoveryTestBed.DatabaseGeneration(bed.Paths.LiveDatabase).Should().Be("original");
        }
        else
        {
            var retained = bed.Paths.PreviousDatabase(manifest.JobId);
            File.Exists(retained).Should().BeTrue();
            RecoveryTestBed.Sha256Hex(File.ReadAllBytes(retained)).Should().Be(manifest.DatabaseSha256);
        }

        if (Directory.Exists(bed.Paths.LiveMedia))
        {
            AssertMediaGeneration(bed.Paths.LiveMedia, "original");
        }
        else
        {
            var retained = bed.Paths.PreviousMedia(manifest.JobId);
            Directory.Exists(retained).Should().BeTrue();
            AssertManifestDescribesDirectory(retained, manifest);
        }
    }

    private static void AssertMediaGeneration(string root, string generation)
    {
        Directory.Exists(root).Should().BeTrue();
        File.ReadAllText(Path.Combine(root, FirstBook.ToString("N"), "book.epub"))
            .Should().Be($"{generation}-book-a");
    }

    private static void AssertManifestDescribesDirectory(string root, SelfHostedRecoveryManifest manifest)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        files.Should().HaveCount(manifest.Media.Count);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file);
            var bookId = Guid.ParseExact(Path.GetFileName(Path.GetDirectoryName(file))!, "N");
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name).ToLowerInvariant();
            var stem = Path.GetFileNameWithoutExtension(name);
            var kind = name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ? "partial"
                : stem.Equals("book", StringComparison.OrdinalIgnoreCase) ? "book"
                : stem.Equals("cover", StringComparison.OrdinalIgnoreCase) ? "cover"
                : stem.StartsWith("cover-thumb-", StringComparison.OrdinalIgnoreCase) ? "thumbnail"
                : "other";
            var bytes = File.ReadAllBytes(file);
            manifest.Media.Should().Contain(descriptor =>
                descriptor.BookId == bookId
                && descriptor.Kind == kind
                && descriptor.Extension == extension
                && descriptor.Bytes == bytes.Length
                && descriptor.Sha256 == RecoveryTestBed.Sha256Hex(bytes),
                $"the manifest must describe '{relative}'");
        }
    }
}
