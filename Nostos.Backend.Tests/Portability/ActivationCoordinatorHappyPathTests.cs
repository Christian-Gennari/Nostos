using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 7 happy paths: a populated replacement keeps its recovery copy, an
/// empty destination activates without confirmation and retains nothing, and
/// admission refuses unconfirmed or stale requests without touching the live
/// library.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class ActivationCoordinatorHappyPathTests
{
    [Fact]
    public async Task PopulatedActivation_ServesVerifiedImport_KeepsHostState_AndRetainsRecoveryCopy()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var result = await bed.ActivateAsync(confirm: true);

        result.Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        result.State.Should().Be(MigrationJobState.Completed);
        result.RecoveryStatus.Should().Be(MigrationRecoveryStatus.Available);

        var job = await bed.ReadJobAsync();
        job.State.Should().Be((int)MigrationJobState.Completed);
        job.RecoveryStatus.Should().Be((int)MigrationRecoveryStatus.Available);

        (await bed.CurrentRevisionAsync()).Should().Be(
            ActivationCoordinatorTemplate.AdvancedRevision,
            "one activation is one committed library-state mutation");

        await bed.AssertImportedGenerationAsync();

        // Host operational rows survive into the activated database.
        await using (var scope = bed.Host.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Nostos.Backend.Data.NostosDbContext>();
            (await db.BackupRecords.CountAsync()).Should().Be(1, "local backup history is host state");
            (await db.MigrationStorageReservations.CountAsync()).Should().BeGreaterThanOrEqualTo(1);
            (await db.MigrationJobRecords.CountAsync()).Should().Be(1);
        }

        // The real storage service reads the imported bytes through the real layout.
        var assets = bed.Host.GetRequiredService<IBookAssetStorage>();
        await using (var opened = await assets.OpenBookFileAsync(bed.SourceIds.EpubBookId))
        {
            opened.Should().NotBeNull();
            using var buffer = new MemoryStream();
            await opened!.Content.CopyToAsync(buffer);
            buffer.ToArray().Should().Equal("EPUB-CONTENT-PORTABLE"u8.ToArray());
        }

        (await assets.GetBookCoverInfoAsync(bed.SourceIds.EpubBookId, default)).Should().NotBeNull();

        // Recovery copy: manifest matches its retained bytes exactly.
        var manifests = new SelfHostedRecoveryManifestStore(bed.Paths);
        var manifest = manifests.Read(bed.JobId);
        manifest.Should().NotBeNull();
        manifest!.Status.Should().Be(MigrationRecoveryStatus.Available);
        manifest.DatabaseRetained.Should().BeTrue();
        manifest.MediaRetained.Should().BeTrue();
        (manifest.ExpiresAtUtc - manifest.CreatedAtUtc)
            .Should().Be(TimeSpan.FromDays(MigrationContractLimits.RecoveryRetentionDays));

        var previousDatabase = bed.Paths.PreviousDatabase(bed.JobId);
        new FileInfo(previousDatabase).Length.Should().Be(manifest.DatabaseBytes);
        Sha256Hex(File.ReadAllBytes(previousDatabase)).Should().Be(manifest.DatabaseSha256);

        var retained = SweepRetainedMedia(bed.Paths.PreviousMedia(bed.JobId));
        var expected = manifest.Media
            .Select(descriptor => (descriptor.BookId, descriptor.Kind, descriptor.Extension,
                descriptor.Bytes, descriptor.Sha256))
            .OrderBy(item => item.BookId).ThenBy(item => item.Kind)
            .ThenBy(item => item.Extension).ThenBy(item => item.Bytes)
            .ToArray();
        retained.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());

        // Capacity accounting: the retained copy holds its own claimed reservation
        // and the job's transfer claim is materialized up to the exact target.
        await using (var db = bed.OpenDatabase())
        {
            var rows = await db.MigrationStorageReservations.AsNoTracking()
                .Where(row => row.ClaimedJobId == bed.JobId)
                .ToArrayAsync();
            rows.Should().HaveCount(2, "the transfer claim and the retention claim both belong to the job");
            var retention = rows.Single(row => row.Purpose == (int)MigrationSessionPurpose.RecoveryRetention);
            retention.ReleasedAtUtc.Should().BeNull();
            retention.ReservedBytes.Should().Be(manifest.DatabaseBytes + manifest.MediaBytes);

            var transfer = rows.Single(row => row.Purpose == (int)MigrationSessionPurpose.Import);
            transfer.MaterializedBytes.Should().Be(Math.Min(transfer.ReservedBytes, manifest.TotalBytes));
            transfer.ReservedBytes.Should().BeGreaterThanOrEqualTo(transfer.MaterializedBytes);
        }

        // Candidate artifacts were consumed by the switch.
        File.Exists(bed.Paths.CandidateDatabase(bed.JobId)).Should().BeFalse();
        Directory.Exists(bed.Paths.CandidateMedia(bed.JobId)).Should().BeFalse();

        // A repeated request after completion is an idempotent success.
        var again = await bed.ActivateAsync(confirm: true);
        again.Outcome.Should().Be(SelfHostedActivationOutcome.AlreadyCompleted);
        (await bed.CurrentRevisionAsync()).Should().Be(ActivationCoordinatorTemplate.AdvancedRevision);
    }

    [Fact]
    public async Task EmptyActivation_ActivatesWithoutConfirmation_AndRetainsNoRecoveryCopy()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: false);

        // The production interface entry point carries no request object: an
        // empty destination needs no confirmation.
        await bed.ActivatePublicAsync();

        var job = await bed.ReadJobAsync();
        job.State.Should().Be((int)MigrationJobState.Completed);
        job.RecoveryStatus.Should().Be((int)MigrationRecoveryStatus.NotRequired);

        (await bed.CurrentRevisionAsync()).Should().Be(ActivationCoordinatorTemplate.AdvancedRevision);
        await bed.AssertImportedGenerationAsync();

        new SelfHostedRecoveryManifestStore(bed.Paths).Read(bed.JobId).Should().BeNull();
        File.Exists(bed.Paths.PreviousDatabase(bed.JobId)).Should().BeFalse();
        Directory.Exists(bed.Paths.PreviousMedia(bed.JobId)).Should().BeFalse(
            "an empty library is only short-lived rollback scratch");
    }

    [Fact]
    public async Task PublicEntryPoint_PopulatedDestinationWithoutConfirmation_IsRefused()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);

        var failure = await FluentActions
            .Awaiting(() => bed.ActivatePublicAsync())
            .Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.ConfirmationRequired);

        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.ReadyToActivate);
        (await bed.CurrentRevisionAsync()).Should().Be(41);
        bed.AssertOriginalGeneration();
    }

    [Fact]
    public async Task StaleRequestRevision_IsRefused_WithoutChangingTheLibrary()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: false);

        await using (var scope = bed.Host.CreateAsyncScope())
        {
            var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedActivationCoordinator>();
            var failure = await FluentActions
                .Awaiting(() => coordinator.ActivateAsync(
                    bed.JobId, new MigrationActivateRequest("stale-revision", true), default))
                .Should().ThrowAsync<MigrationActivationException>();
            failure.Which.Code.Should().Be(MigrationActivationErrorCodes.DestinationConflict);
        }

        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.ReadyToActivate);
        (await bed.CurrentRevisionAsync()).Should().Be(41);
        bed.AssertOriginalGeneration();
    }

    internal static (Guid BookId, string Kind, string Extension, long Bytes, string Sha256)[] SweepRetainedMedia(
        string root)
    {
        var result = new List<(Guid, string, string, long, string)>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var bookId = Guid.Parse(Path.GetFileName(directory));
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                var stem = Path.GetFileNameWithoutExtension(name);
                var kind = stem switch
                {
                    "book" => "book",
                    "cover" => "cover",
                    _ when stem.StartsWith("cover-thumb-", StringComparison.OrdinalIgnoreCase) => "thumbnail",
                    _ => "other",
                };
                var bytes = File.ReadAllBytes(file);
                result.Add((bookId, kind, Path.GetExtension(name).ToLowerInvariant(), bytes.LongLength,
                    Sha256Hex(bytes)));
            }
        }

        return result
            .OrderBy(item => item.Item1).ThenBy(item => item.Item2)
            .ThenBy(item => item.Item3).ThenBy(item => item.Item4)
            .ToArray();
    }

    internal static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
