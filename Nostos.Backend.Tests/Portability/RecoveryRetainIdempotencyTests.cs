using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 6 rework: a rename that completed before the manifest flag write must
/// continue (verified against the manifest), while ambiguous layouts fail
/// closed.
/// </summary>
public sealed class RecoveryRetainIdempotencyTests
{
    private static readonly Guid FirstBook = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static async Task<(SelfHostedMigrationRecoveryService Service, IAsyncDisposable Lease,
        SelfHostedActivationJournal Journal, SelfHostedRecoveryCapture Capture)> PrepareAsync(RecoveryTestBed bed)
    {
        await bed.SeedLiveLibraryAsync();
        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);
        var journal = bed.SeedJournal();
        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);
        return (service, lease, journal, capture);
    }

    [Fact]
    public async Task RetainMedia_requires_the_cutover_prepared_phase()
    {
        using var bed = new RecoveryTestBed();
        var (service, lease, _, _) = await PrepareAsync(bed);

        Func<Task> tooEarly = () => service.RetainMediaAsync(bed.JobId, lease, default);
        await tooEarly.Should().ThrowAsync<MigrationActivationException>()
            .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryFailed);
        Directory.Exists(bed.Paths.LiveMedia).Should().BeTrue();
        Directory.Exists(bed.Paths.PreviousMedia(bed.JobId)).Should().BeFalse();
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task RetainMedia_continues_when_the_rename_completed_but_the_flag_did_not()
    {
        using var bed = new RecoveryTestBed();
        var (service, lease, journal, _) = await PrepareAsync(bed);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);

        // Simulate the rename succeeding and the manifest write crashing.
        Directory.Move(bed.Paths.LiveMedia, bed.Paths.PreviousMedia(bed.JobId));
        var manifest = await service.RetainMediaAsync(bed.JobId, lease, default);

        manifest.MediaRetained.Should().BeTrue();
        Directory.Exists(bed.Paths.LiveMedia).Should().BeFalse();
        Directory.Exists(bed.Paths.PreviousMedia(bed.JobId)).Should().BeTrue();
        bed.Manifests.Read(bed.JobId)!.MediaRetained.Should().BeTrue();
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task RetainMedia_already_done_still_fails_closed_on_a_mismatched_destination()
    {
        using var bed = new RecoveryTestBed();
        var (service, lease, journal, _) = await PrepareAsync(bed);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
        Directory.Move(bed.Paths.LiveMedia, bed.Paths.PreviousMedia(bed.JobId));
        await File.AppendAllTextAsync(
            Path.Combine(bed.Paths.PreviousMedia(bed.JobId), FirstBook.ToString("N"), "book.epub"), "changed");

        Func<Task> mismatch = () => service.RetainMediaAsync(bed.JobId, lease, default);
        await mismatch.Should().ThrowAsync<MigrationActivationException>()
            .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryFailed);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task RetainMedia_fails_closed_when_both_or_neither_component_exists()
    {
        using (var both = new RecoveryTestBed())
        {
            var (service, lease, journal, _) = await PrepareAsync(both);
            journal = await both.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
            Directory.Move(both.Paths.LiveMedia, both.Paths.PreviousMedia(both.JobId));
            Directory.CreateDirectory(both.Paths.LiveMedia);
            Func<Task> ambiguous = () => service.RetainMediaAsync(both.JobId, lease, default);
            await ambiguous.Should().ThrowAsync<MigrationActivationException>();
            await lease.DisposeAsync();
        }

        using (var neither = new RecoveryTestBed())
        {
            var (service, lease, journal, _) = await PrepareAsync(neither);
            journal = await neither.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
            Directory.Delete(neither.Paths.LiveMedia, recursive: true);
            Func<Task> missing = () => service.RetainMediaAsync(neither.JobId, lease, default);
            await missing.Should().ThrowAsync<MigrationActivationException>();
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task RetainDatabase_continues_when_the_rename_completed_but_the_flag_did_not()
    {
        using var bed = new RecoveryTestBed();
        var (service, lease, journal, _) = await PrepareAsync(bed);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
        await service.RetainMediaAsync(bed.JobId, lease, default);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousMediaRetained, lease);

        // Simulate the database rename succeeding and the manifest write crashing.
        File.Move(bed.Paths.LiveDatabase, bed.Paths.PreviousDatabase(bed.JobId));
        var manifest = await service.RetainDatabaseAsync(bed.JobId, lease, default);

        manifest.DatabaseRetained.Should().BeTrue();
        File.Exists(bed.Paths.LiveDatabase).Should().BeFalse();
        File.Exists(bed.Paths.PreviousDatabase(bed.JobId)).Should().BeTrue();
        bed.Manifests.Read(bed.JobId)!.DatabaseRetained.Should().BeTrue();
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task RetainDatabase_already_done_verifies_length_and_hash_and_refuses_a_hot_wal()
    {
        using (var mismatch = new RecoveryTestBed())
        {
            var (service, lease, journal, _) = await PrepareAsync(mismatch);
            journal = await mismatch.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
            await service.RetainMediaAsync(mismatch.JobId, lease, default);
            journal = await mismatch.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousMediaRetained, lease);
            File.Move(mismatch.Paths.LiveDatabase, mismatch.Paths.PreviousDatabase(mismatch.JobId));
            await File.AppendAllTextAsync(mismatch.Paths.PreviousDatabase(mismatch.JobId), "corrupt");

            Func<Task> changed = () => service.RetainDatabaseAsync(mismatch.JobId, lease, default);
            await changed.Should().ThrowAsync<MigrationActivationException>()
                .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryFailed);
            await lease.DisposeAsync();
        }

        using (var hotWal = new RecoveryTestBed())
        {
            var (service, lease, journal, _) = await PrepareAsync(hotWal);
            journal = await hotWal.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
            await service.RetainMediaAsync(hotWal.JobId, lease, default);
            journal = await hotWal.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousMediaRetained, lease);
            File.Move(hotWal.Paths.LiveDatabase, hotWal.Paths.PreviousDatabase(hotWal.JobId));
            await File.WriteAllTextAsync(hotWal.Paths.PreviousDatabase(hotWal.JobId) + "-wal", "committed frames");

            Func<Task> wal = () => service.RetainDatabaseAsync(hotWal.JobId, lease, default);
            await wal.Should().ThrowAsync<MigrationActivationException>()
                .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryFailed);
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task RetainDatabase_fails_closed_when_both_or_neither_component_exists()
    {
        using (var both = new RecoveryTestBed())
        {
            var (service, lease, journal, _) = await PrepareAsync(both);
            journal = await both.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
            await service.RetainMediaAsync(both.JobId, lease, default);
            journal = await both.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousMediaRetained, lease);
            File.Copy(both.Paths.LiveDatabase, both.Paths.PreviousDatabase(both.JobId));
            Func<Task> ambiguous = () => service.RetainDatabaseAsync(both.JobId, lease, default);
            await ambiguous.Should().ThrowAsync<MigrationActivationException>();
            await lease.DisposeAsync();
        }

        using (var neither = new RecoveryTestBed())
        {
            var (service, lease, journal, _) = await PrepareAsync(neither);
            journal = await neither.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
            await service.RetainMediaAsync(neither.JobId, lease, default);
            journal = await neither.AdvanceJournalAsync(journal, SelfHostedActivationPhase.PreviousMediaRetained, lease);
            File.Delete(neither.Paths.LiveDatabase);
            Func<Task> missing = () => service.RetainDatabaseAsync(neither.JobId, lease, default);
            await missing.Should().ThrowAsync<MigrationActivationException>();
            await lease.DisposeAsync();
        }
    }
}
