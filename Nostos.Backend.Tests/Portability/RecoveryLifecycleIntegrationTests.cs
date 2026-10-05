using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// The composed recovery lifecycle across slices 7, 9 and 10: a populated
/// replacement retains its previous library, "restore previous library" turns
/// that retained copy into a <c>Restored</c> provenance copy and retains the
/// replaced library as a new seven-day copy, and expiry cleanup then removes
/// exactly the restored source while the new copy stays available. The pair
/// each PR tested green independently; this is the merge-result integration
/// test recommended by the Slice 10 review.
/// </summary>
public sealed class RecoveryLifecycleIntegrationTests
{
    [Fact]
    public async Task Expiry_cleanup_removes_the_restored_source_and_keeps_the_replaced_library_copy()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        (await bed.ActivateAsync(confirm: true)).Outcome
            .Should().Be(SelfHostedActivationOutcome.Completed);
        var source = bed.ReadRecoveryManifest()!;
        source.Status.Should().Be(MigrationRecoveryStatus.Available);
        var sourceReservation = source.RetentionReservationId;
        sourceReservation.Should().NotBeNull("the retained previous library holds a recovery reservation");

        // Undo the replacement: the imported generation becomes the new
        // retained copy and the original library is live again.
        var restore = await bed.RestoreAsync();
        restore.Outcome.Should().Be(SelfHostedRecoveryRestoreOutcome.Restored);
        await bed.AssertRestoredPortableGenerationAsync();

        var restoredSource = bed.ReadRecoveryManifest()!;
        restoredSource.Status.Should().Be(MigrationRecoveryStatus.Restored);
        var replacementId = restoredSource.RestoreOperationId;
        replacementId.Should().NotBeNull("the replaced library was retained by the restore");
        var replacement = bed.ReadRecoveryManifest(replacementId)!;
        replacement.Status.Should().Be(MigrationRecoveryStatus.Available);
        replacement.RetentionReservationId.Should().NotBeNull();

        // The restored source reaches its original seven-day expiry.
        bed.ExpireRecoveryCopy();

        await using (var scope = bed.Host.CreateAsyncScope())
        {
            var cleanup = scope.ServiceProvider.GetRequiredService<SelfHostedMigrationRecoveryService>();
            (await cleanup.DeleteExpiredAsync(default)).Should().Be(1);
        }

        // The restored source is physically gone and its reservation released.
        File.Exists(bed.Paths.PreviousDatabase(source.JobId)).Should().BeFalse();
        Directory.Exists(bed.Paths.PreviousMedia(source.JobId)).Should().BeFalse();
        bed.Manifests.Read(source.JobId).Should().BeNull(
            "a fully deleted copy is no longer a manifest");

        await using (var verify = bed.OpenDatabase())
        {
            var released = await verify.MigrationStorageReservations.AsNoTracking()
                .Where(reservation => reservation.Id == sourceReservation!.Value)
                .Select(reservation => new { reservation.ReleasedAtUtc })
                .SingleAsync();
            released.ReleasedAtUtc.Should().NotBeNull(
                "the source copy's retained-byte reservation is released after physical cleanup");
        }

        // The replaced library retained by the restore is untouched.
        File.Exists(bed.Paths.PreviousDatabase(replacementId!.Value)).Should().BeTrue();
        Directory.Exists(bed.Paths.PreviousMedia(replacementId.Value)).Should().BeTrue();
        bed.Manifests.Read(replacementId.Value)!.Status.Should().Be(MigrationRecoveryStatus.Available);

        await using (var verify = bed.OpenDatabase())
        {
            var held = await verify.MigrationStorageReservations.AsNoTracking()
                .Where(reservation => reservation.Id == replacement.RetentionReservationId!.Value)
                .Select(reservation => new { reservation.ReleasedAtUtc })
                .SingleAsync();
            held.ReleasedAtUtc.Should().BeNull(
                "the still-available copy keeps its reservation until its own expiry");
        }
    }
}
