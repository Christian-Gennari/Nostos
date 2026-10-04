using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class RecoveryCleanupTests
{
    private static readonly Guid FirstBook = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Cleanup_RemovesOnlyCopiesWhoseExpiryHasPassed_AndIsIdempotent()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var unexpired = SeedCopy(bed, bed.Clock.UtcNow);
        var daySix = SeedCopy(bed, bed.Clock.UtcNow.AddDays(-6));
        var expired = SeedCopy(bed, bed.Clock.UtcNow.AddDays(-MigrationContractLimits.RecoveryRetentionDays));
        var service = bed.CreateService();

        (await service.DeleteExpiredAsync(default)).Should().Be(1);

        Directory.Exists(bed.Paths.PreviousMedia(unexpired.JobId)).Should().BeTrue("day 0 is retained");
        Directory.Exists(bed.Paths.PreviousMedia(daySix.JobId)).Should().BeTrue("day 6 is retained");
        Directory.Exists(bed.Paths.PreviousMedia(expired.JobId)).Should().BeFalse();
        File.Exists(bed.Paths.PreviousDatabase(expired.JobId)).Should().BeFalse();
        bed.Manifests.Read(expired.JobId).Should().BeNull();
        Directory.Exists(Path.GetDirectoryName(bed.Paths.RecoveryManifest(expired.JobId))).Should().BeFalse();

        (await service.DeleteExpiredAsync(default)).Should().Be(0, "repeated cleanup is idempotent");
        (await service.DeleteExpiredAsync(default)).Should().Be(0);
    }

    [Fact]
    public async Task Cleanup_RefusesAnActiveJobAndARestoringCopy()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var expired = bed.Clock.UtcNow.AddDays(-MigrationContractLimits.RecoveryRetentionDays);
        var active = SeedCopy(bed, expired);
        var restoring = SeedCopy(bed, expired, MigrationRecoveryStatus.Restoring);
        bed.SeedJournal(jobId: active.JobId);
        var service = bed.CreateService();

        (await service.DeleteExpiredAsync(default)).Should().Be(0);
        Directory.Exists(bed.Paths.PreviousMedia(active.JobId)).Should().BeTrue(
            "an unresolved activation journal must protect the copy");
        Directory.Exists(bed.Paths.PreviousMedia(restoring.JobId)).Should().BeTrue(
            "a restore in progress must protect the copy");

        File.Delete(bed.Paths.Journal(active.JobId));
        (await service.DeleteExpiredAsync(default)).Should().Be(1);
        Directory.Exists(bed.Paths.PreviousMedia(active.JobId)).Should().BeFalse();
        Directory.Exists(bed.Paths.PreviousMedia(restoring.JobId)).Should().BeTrue();
    }

    [Fact]
    public async Task Cleanup_ResumesAfterInterruptedDeletion_AndReleasesTheReservation()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var expired = bed.Clock.UtcNow.AddDays(-MigrationContractLimits.RecoveryRetentionDays);
        var mediaGone = SeedCopy(bed, expired);
        bed.Manifests.CreateDeletionMarker(mediaGone.JobId);
        Directory.Delete(bed.Paths.PreviousMedia(mediaGone.JobId), recursive: true);
        var databaseGone = SeedCopy(bed, expired);
        bed.Manifests.CreateDeletionMarker(databaseGone.JobId);
        File.Delete(bed.Paths.PreviousDatabase(databaseGone.JobId));
        var service = bed.CreateService();

        (await service.DeleteExpiredAsync(default)).Should().Be(2);

        foreach (var jobId in new[] { mediaGone.JobId, databaseGone.JobId })
        {
            Directory.Exists(Path.GetDirectoryName(bed.Paths.RecoveryManifest(jobId))).Should().BeFalse();
            bed.Manifests.HasDeletionMarker(jobId).Should().BeFalse();
        }

        await using var verify = bed.OpenDatabase();
        var released = await verify.MigrationStorageReservations.AsNoTracking()
            .Where(reservation => reservation.ReleasedAtUtc != null)
            .Select(reservation => reservation.Id)
            .ToArrayAsync();
        released.Should().Contain(mediaGone.ReservationId).And.Contain(databaseGone.ReservationId);
    }

    [Fact]
    public async Task Cleanup_NeverTouchesAnythingOutsideTheRemovedCopyDirectory()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var expired = SeedCopy(bed, bed.Clock.UtcNow.AddDays(-MigrationContractLimits.RecoveryRetentionDays));
        var unexpired = SeedCopy(bed, bed.Clock.UtcNow);
        var stray = Path.Combine(bed.Paths.RecoveryRoot, "keep.txt");
        File.WriteAllText(stray, "keep");
        var sibling = Path.Combine(Path.GetDirectoryName(bed.Paths.PreviousMedia(unexpired.JobId))!, "keep.txt");
        File.WriteAllText(sibling, "keep");
        var service = bed.CreateService();

        (await service.DeleteExpiredAsync(default)).Should().Be(1);

        File.Exists(stray).Should().BeTrue("cleanup never removes unknown files at the recovery root");
        File.Exists(sibling).Should().BeTrue();
        Directory.Exists(bed.Paths.PreviousMedia(unexpired.JobId)).Should().BeTrue();
        File.Exists(bed.Paths.PreviousDatabase(unexpired.JobId)).Should().BeTrue();
        Directory.Exists(Path.GetDirectoryName(bed.Paths.RecoveryManifest(expired.JobId))).Should().BeFalse();
    }

    [Fact]
    public async Task ListingAndLookup_AreProviderNeutral_AndFailClosedOnCorruption()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var available = SeedCopy(bed, bed.Clock.UtcNow, withReservation: false);
        var restoring = SeedCopy(bed, bed.Clock.UtcNow, MigrationRecoveryStatus.Restoring, withReservation: false);
        var deleting = SeedCopy(bed, bed.Clock.UtcNow, withReservation: false);
        bed.Manifests.CreateDeletionMarker(deleting.JobId);
        var service = bed.CreateService();

        var listed = await service.ListAsync(default);
        listed.Should().HaveCount(2);
        listed.Single(item => item.JobId == available.JobId).Status.Should().Be(MigrationRecoveryStatus.Available);
        listed.Single(item => item.JobId == restoring.JobId).Status.Should().Be(MigrationRecoveryStatus.Restoring);
        listed.Should().OnlyContain(item => item.SizeBytes == 300 && item.Counts.Books == 1);
        foreach (var property in typeof(MigrationRecoveryStatusResponse).GetProperties())
            property.Name.Should().NotContain("Path");
        (await service.GetAsync(deleting.JobId, default)).Should().BeNull("a deletion in progress is not offered");
        (await service.GetAsync(Guid.NewGuid(), default)).Should().BeNull();

        File.WriteAllText(bed.Paths.RecoveryManifest(available.JobId), "{torn");
        Func<Task> corruptListing = () => service.ListAsync(default);
        await corruptListing.Should().ThrowAsync<MigrationActivationException>()
            .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryCorrupt);
        Func<Task> corruptLookup = () => service.GetAsync(available.JobId, default);
        await corruptLookup.Should().ThrowAsync<MigrationActivationException>()
            .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryCorrupt);

        bed.Clock.Advance(TimeSpan.FromDays(MigrationContractLimits.RecoveryRetentionDays + 1));
        Func<Task> corruptCleanup = () => service.DeleteExpiredAsync(default);
        await corruptCleanup.Should().ThrowAsync<MigrationActivationException>()
            .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryCorrupt);
        File.Exists(bed.Paths.PreviousDatabase(available.JobId)).Should().BeTrue(
            "a corrupt manifest is never silently deleted");
    }

    private static (Guid JobId, Guid ReservationId) SeedCopy(
        RecoveryTestBed bed,
        DateTimeOffset createdAt,
        MigrationRecoveryStatus status = MigrationRecoveryStatus.Available,
        bool withReservation = true)
    {
        var jobId = Guid.NewGuid();
        bed.CreatePreviousMaterial(jobId, "retained", $"{FirstBook:N}/book.epub");
        var reservationId = Guid.Empty;
        if (withReservation)
        {
            using var db = bed.OpenDatabase();
            var capacity = new TransferStorageCapacity(db, bed.TransferVolume, Options.Create(bed.Options), bed.Clock);
            var reserved = capacity.TryReserveAsync(1_234, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default)
                .GetAwaiter().GetResult();
            capacity.ClaimAsync(reserved.ReservationId!.Value, jobId, default).GetAwaiter().GetResult();
            reservationId = reserved.ReservationId.Value;
        }

        var manifest = new SelfHostedRecoveryManifest(
            jobId,
            Guid.NewGuid(),
            createdAt,
            SelfHostedRecoveryManifest.Expiry(createdAt),
            status,
            "revision-1",
            new MigrationExistingCounts(Books: 1),
            100,
            200,
            new string('a', 64),
            [new RecoveryMediaDescriptor(FirstBook, "book", ".epub", 100, new string('b', 64))],
            DatabaseSchemaVersion: "20260101000000_Initial",
            DatabaseMigrationCount: 1,
            DatabaseRetained: true,
            MediaRetained: true,
            RetentionReservationId: withReservation ? reservationId : null);
        bed.Manifests.Write(manifest);
        return (jobId, reservationId);
    }
}
