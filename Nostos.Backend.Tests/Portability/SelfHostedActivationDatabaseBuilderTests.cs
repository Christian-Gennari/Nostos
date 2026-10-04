using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class SelfHostedActivationDatabaseBuilderTests
{
    [Fact]
    public async Task BuildPortableCandidate_IsSchemaIdenticalAndUnfinalized_WithExactPayloadAndNoHostState()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var liveBefore = ActivationBuildFixture.Sha256(fixture.Paths.LiveDatabase);
        var liveMediaBefore = ActivationBuildFixture.MediaSnapshot(fixture.Paths.LiveMedia);
        var builder = fixture.CreateBuilder();
        await builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);

        var candidatePath = fixture.Paths.CandidateDatabase(fixture.JobId);
        File.Exists(candidatePath).Should().BeTrue();
        File.Exists(candidatePath + "-wal").Should().BeFalse();
        File.Exists(candidatePath + "-shm").Should().BeFalse();
        Directory.GetFiles(Path.GetDirectoryName(candidatePath)!, "*")
            .Should().BeEquivalentTo([candidatePath], "the candidate area holds only the finished database");
        builder.IsFinalized(fixture.JobId).Should().BeFalse("host state is only frozen at the activation boundary");

        await using (var candidate = builder.OpenCandidate(fixture.JobId))
        {
            var counts = fixture.Prepared.Metadata.Counts;
            (await candidate.Works.CountAsync()).Should().Be((int)counts.Works);
            (await candidate.Books.CountAsync()).Should().Be((int)counts.Books);
            (await candidate.Collections.CountAsync()).Should().Be((int)counts.Collections);
            (await candidate.BookCollections.CountAsync()).Should().Be((int)counts.CollectionMemberships);
            (await candidate.Notes.CountAsync()).Should().Be((int)counts.Notes);
            (await candidate.Topics.CountAsync()).Should().Be((int)counts.Topics);
            (await candidate.NoteTopics.CountAsync()).Should().Be((int)counts.NoteTopics);
            (await candidate.Writings.CountAsync()).Should().Be((int)counts.Writings);
            (await candidate.WritingNotes.CountAsync()).Should().Be((int)counts.WritingNotes);
            (await candidate.BookAcquisitions.CountAsync()).Should().Be((int)counts.Acquisitions);
            (await candidate.NoteImportBookLinks.CountAsync()).Should().Be((int)counts.NoteImportBookLinks);
            (await candidate.AssistantSettings.CountAsync()).Should().Be((int)counts.AssistantSettings);

            var expectedNote = fixture.Data.Notes.Single();
            var note = await candidate.Notes.AsNoTracking().SingleAsync();
            note.Id.Should().Be(expectedNote.Id);
            note.BookId.Should().Be(expectedNote.BookId);
            note.Content.Should().Be(expectedNote.Content);
            note.RawContent.Should().Be(expectedNote.RawContent);
            note.SelectedText.Should().Be(expectedNote.SelectedText);
            note.CfiRange.Should().Be(expectedNote.CfiRange);
            note.CaptureSource.Should().Be(expectedNote.CaptureSource);
            note.ProcessingMode.Should().Be(expectedNote.ProcessingMode);
            note.SourceAnchorKind.Should().Be(expectedNote.SourceAnchorKind);
            note.SourceAnchorValue.Should().Be(expectedNote.SourceAnchorValue);
            note.AnchorVerified.Should().Be(expectedNote.AnchorVerified);
            note.CreatedAt.Should().Be(expectedNote.CreatedAt);

            var expectedLink = fixture.Data.NoteImportBookLinks!.Single();
            var link = await candidate.NoteImportBookLinks.AsNoTracking().SingleAsync();
            link.Id.Should().Be(expectedLink.Id);
            link.Source.Should().Be(expectedLink.Source);
            link.SourceKey.Should().Be(expectedLink.SourceKey);
            link.BookId.Should().Be(expectedLink.BookId);
            link.CreatedAtUtc.Should().Be(expectedLink.CreatedAtUtc);

            foreach (var membership in fixture.Data.BookCollections)
            {
                var row = await candidate.BookCollections
                    .AsNoTracking()
                    .SingleAsync(x => x.BookId == membership.BookId && x.CollectionId == membership.CollectionId);
                row.AddedAt.Should().Be(membership.AddedAt);
            }

            var expectedWriting = fixture.Data.Writings.Single(
                x => string.Equals(x.Type, "document", StringComparison.OrdinalIgnoreCase));
            var writing = await candidate.Writings.AsNoTracking().SingleAsync(x => x.Type == WritingType.Document);
            writing.Content.Should().Be(expectedWriting.Content);
            writing.Name.Should().Be(expectedWriting.Name);
            writing.ParentId.Should().Be(expectedWriting.ParentId);

            var expectedBook = fixture.Data.Books.Single(x => x.Id == fixture.SourceIds.EpubBookId);
            var book = await candidate.Books.AsNoTracking().OfType<EBookModel>()
                .SingleAsync(x => x.Id == expectedBook.Id);
            book.Title.Should().Be(expectedBook.Title);
            book.Metadata.Publisher.Should().Be(expectedBook.Metadata.Publisher);
            book.Progress.LastLocation.Should().Be(expectedBook.Progress.LastLocation);
            book.Progress.ProgressPercent.Should().Be(expectedBook.Progress.ProgressPercent);
            book.Progress.Rating.Should().Be(expectedBook.Progress.Rating);
            book.Progress.IsFavorite.Should().Be(expectedBook.Progress.IsFavorite);
            book.Progress.PersonalReview.Should().Be(expectedBook.Progress.PersonalReview);
            book.Progress.LastReadAt.Should().Be(expectedBook.Progress.LastReadAt);

            var expectedStagedMedia = fixture.Prepared.Media
                .ToDictionary(x => (x.Descriptor.BookId, x.Descriptor.Kind));
            var epubMedia = expectedStagedMedia[(expectedBook.Id, PortableArchiveFormat.BookMediaKind)];
            book.FileDetails.HasFile.Should().BeTrue();
            book.FileDetails.FileName.Should().Be(epubMedia.Descriptor.FileName);
            book.FileDetails.LocationsJson.Should().BeNull("epub.js locations are a reconstructible cache");

            var assistant = await candidate.AssistantSettings.AsNoTracking().SingleAsync();
            assistant.CaptureProcessingMode.Should().Be(fixture.Data.AssistantSettings!.CaptureProcessingMode);
            assistant.UpdatedAtUtc.Should().Be(fixture.Data.AssistantSettings.UpdatedAtUtc);

            // Portable state is a replacement, never an overlay of the old library.
            (await candidate.Books.AnyAsync(x => x.Title == ActivationBuildFixture.LiveOnlyBookTitle))
                .Should().BeFalse("the previous live library must be replaced entirely");
            (await candidate.Notes.AnyAsync(x => x.Id == fixture.LiveOnlyNoteId)).Should().BeFalse();

            // No host operational state is frozen into the pre-build candidate.
            (await candidate.BackupRecords.CountAsync()).Should().Be(0);
            (await candidate.MigrationJobRecords.CountAsync()).Should().Be(0);
            (await candidate.MigrationStorageReservations.CountAsync()).Should().Be(0);
            (await candidate.AiProviderSettings.CountAsync()).Should().Be(0);
            (await candidate.LibraryCommandReceipts.CountAsync()).Should().Be(0);
            (await candidate.NoteCommandReceipts.CountAsync()).Should().Be(0);
            (await candidate.NoteImportBatches.CountAsync()).Should().Be(0);
            (await candidate.NoteImportBatchNotes.CountAsync()).Should().Be(0);
        }

        var referencePath = Path.Combine(fixture.DatabaseRoot, "reference.db");
        await ActivationBuildFixture.BootstrapAsync(referencePath);
        ActivationBuildFixture.SchemaFingerprint(candidatePath)
            .Should().Be(ActivationBuildFixture.SchemaFingerprint(referencePath));

        // The build never writes into the live database or media territory.
        ActivationBuildFixture.Sha256(fixture.Paths.LiveDatabase).Should().Be(liveBefore);
        File.Exists(fixture.Paths.LiveDatabase + "-wal").Should().BeFalse();
        File.Exists(fixture.Paths.LiveDatabase + "-shm").Should().BeFalse();
        ActivationBuildFixture.MediaSnapshot(fixture.Paths.LiveMedia)
            .Should().BeEquivalentTo(liveMediaBefore);
    }

    [Fact]
    public async Task Finalize_ImportsAuthoritativeLiveHostState_AndNeverRevertsOperationalWrites()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var builder = fixture.CreateBuilder();
        await builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);

        // Everything below happens while the live library keeps serving, after
        // the long build phase. A correct finalizer must not revert any of it.
        await using (var live = fixture.OpenLive())
        {
            var provider = await live.AiProviderSettings.SingleAsync();
            provider.LlmModel = "post-build-model";
            provider.LlmApiKeyEncrypted = "POST-BUILD-SECRET";

            var backup = new BackupRecord
            {
                Id = Guid.NewGuid(),
                CreatedAt = ActivationBuildFixture.FixedNow.AddMinutes(30),
                SizeBytes = 777,
                Status = BackupStatus.Completed,
                Provider = BackupProvider.Local,
                IncludeBookFiles = false,
            };
            live.BackupRecords.Add(backup);

            var job = await live.MigrationJobRecords.SingleAsync(x => x.Id == fixture.JobId);
            job.State = (int)MigrationJobState.Activating;
            job.Version = 4;
            job.MigrationLeaseToken = "post-build-lease";
            job.UpdatedAtUtc = ActivationBuildFixture.FixedNow.AddMinutes(31);

            var reservation = await live.MigrationStorageReservations.SingleAsync();
            reservation.MaterializedBytes = 4096;
            reservation.Version = 2;

            await live.SaveChangesAsync();
        }

        await using var lease = await fixture.EnterExclusiveAsync();
        await builder.FinalizeCandidateAsync(fixture.JobId, lease);
        builder.IsFinalized(fixture.JobId).Should().BeTrue();

        await using var candidate = builder.OpenCandidate(fixture.JobId);
        var finalizedProvider = await candidate.AiProviderSettings.AsNoTracking().SingleAsync();
        finalizedProvider.LlmModel.Should().Be("post-build-model");
        finalizedProvider.LlmApiKeyEncrypted.Should().Be("POST-BUILD-SECRET");

        var finalizedJob = await candidate.MigrationJobRecords.AsNoTracking().SingleAsync(x => x.Id == fixture.JobId);
        finalizedJob.State.Should().Be((int)MigrationJobState.Activating);
        finalizedJob.Version.Should().Be(4);
        finalizedJob.MigrationLeaseToken.Should().Be("post-build-lease");

        (await candidate.BackupRecords.CountAsync())
            .Should().Be(ActivationBuildFixture.LiveBackupCount + 1);
        (await candidate.MigrationStorageReservations.AsNoTracking().SingleAsync())
            .MaterializedBytes.Should().Be(4096);
        (await candidate.LibraryStates.AsNoTracking().SingleAsync())
            .StateVersion.Should().Be("8", "finalization advances the authoritative live revision");

        // Everything in the final live host snapshot is represented.
        ActivationBuildFixture.AssertCarriedTablesMatch(
            fixture.Paths.LiveDatabase,
            fixture.Paths.CandidateDatabase(fixture.JobId));
    }

    [Fact]
    public async Task Finalize_WithoutACurrentExclusiveLease_IsRejected_AndCandidateStaysUnfinalized()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var builder = fixture.CreateBuilder();
        await builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);

        Func<Task> noLease = () => builder.FinalizeCandidateAsync(fixture.JobId, null!);
        await noLease.Should().ThrowAsync<InvalidOperationException>();

        var foreignGate = new LibraryMaintenanceCoordinator();
        await using (var foreign = await foreignGate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
        {
            Func<Task> foreignLease = () => builder.FinalizeCandidateAsync(fixture.JobId, foreign);
            await foreignLease.Should().ThrowAsync<InvalidOperationException>();
        }

        var disposed = await fixture.EnterExclusiveAsync();
        await disposed.DisposeAsync();
        Func<Task> disposedLease = () => builder.FinalizeCandidateAsync(fixture.JobId, disposed);
        await disposedLease.Should().ThrowAsync<InvalidOperationException>();

        builder.IsFinalized(fixture.JobId).Should().BeFalse();
        (await fixture.CountCandidateRowsAsync(fixture.JobId, "BackupRecords")).Should().Be(0);
    }

    [Fact]
    public async Task Finalize_IsRepeatableUnderTheLease_AndReplacesRatherThanAppendsHostState()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var builder = fixture.CreateBuilder();
        await builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);

        await using var lease = await fixture.EnterExclusiveAsync();
        await builder.FinalizeCandidateAsync(fixture.JobId, lease);

        // More live host state changes while the exclusive lease is held; the
        // repeated finalization must reflect the newest snapshot, not append to
        // the previous candidate content.
        await using (var live = fixture.OpenLive())
        {
            var provider = await live.AiProviderSettings.SingleAsync();
            provider.LlmModel = "second-finalize-model";
            var state = await live.LibraryStates.SingleAsync();
            state.StateVersion = "9";
            live.BackupRecords.Add(new BackupRecord
            {
                Id = Guid.NewGuid(),
                CreatedAt = ActivationBuildFixture.FixedNow.AddMinutes(40),
                SizeBytes = 888,
                Status = BackupStatus.Completed,
                Provider = BackupProvider.Local,
                IncludeBookFiles = false,
            });
            await live.SaveChangesAsync();
        }

        await builder.FinalizeCandidateAsync(fixture.JobId, lease);
        builder.IsFinalized(fixture.JobId).Should().BeTrue();

        await using var candidate = builder.OpenCandidate(fixture.JobId);
        (await candidate.AiProviderSettings.AsNoTracking().SingleAsync()).LlmModel
            .Should().Be("second-finalize-model");
        (await candidate.LibraryStates.AsNoTracking().SingleAsync()).StateVersion.Should().Be("10");
        (await candidate.BackupRecords.CountAsync())
            .Should().Be(ActivationBuildFixture.LiveBackupCount + 1, "replacement must not duplicate rows");
    }

    [Fact]
    public async Task Finalize_WithInvalidLiveLibraryRevision_FailsClosed_AndLeavesCandidateUnfinalized()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var builder = fixture.CreateBuilder();
        await builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);

        await using (var live = fixture.OpenLive())
        {
            (await live.LibraryStates.SingleAsync()).StateVersion = "not-a-number";
            await live.SaveChangesAsync();
        }

        await using (var lease = await fixture.EnterExclusiveAsync())
        {
            Func<Task> invalid = () => builder.FinalizeCandidateAsync(fixture.JobId, lease);
            (await invalid.Should().ThrowAsync<MigrationActivationException>())
                .Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);
            builder.IsFinalized(fixture.JobId).Should().BeFalse();

            // A missing singleton is equally authoritative and equally fatal.
            await using (var live = fixture.OpenLive())
            {
                live.LibraryStates.RemoveRange(live.LibraryStates);
                await live.SaveChangesAsync();
            }

            Func<Task> missing = () => builder.FinalizeCandidateAsync(fixture.JobId, lease);
            (await missing.Should().ThrowAsync<MigrationActivationException>())
                .Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);
            builder.IsFinalized(fixture.JobId).Should().BeFalse();

            await using (var live = fixture.OpenLive())
            {
                live.LibraryStates.Add(new LibraryState
                {
                    Id = LibraryState.WellKnownId,
                    SingletonSlot = LibraryState.SingletonSentinel,
                    StateVersion = "7",
                });
                await live.SaveChangesAsync();
            }

            await builder.FinalizeCandidateAsync(fixture.JobId, lease);
            builder.IsFinalized(fixture.JobId).Should().BeTrue();
        }
    }

    [Fact]
    public async Task BuildAndFinalize_LeaveTheLiveDatabaseByteForByteUnchanged()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var builder = fixture.CreateBuilder();
        var before = ActivationBuildFixture.Sha256(fixture.Paths.LiveDatabase);

        await builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);
        ActivationBuildFixture.Sha256(fixture.Paths.LiveDatabase)
            .Should().Be(before, "the build reads the live database read-only");

        await using (var lease = await fixture.EnterExclusiveAsync())
        {
            await builder.FinalizeCandidateAsync(fixture.JobId, lease);
        }

        ActivationBuildFixture.Sha256(fixture.Paths.LiveDatabase)
            .Should().Be(before, "finalization reads the authoritative snapshot read-only");
    }

    [Fact]
    public async Task OpenCandidateAndFinalize_LeaveNoOpenHandle_SoTheCandidateCanBeRenamedImmediately()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var builder = fixture.CreateBuilder();
        await builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);
        var candidatePath = fixture.Paths.CandidateDatabase(fixture.JobId);

        // Verification access must not pool a handle on the file to be renamed.
        await using (var candidate = builder.OpenCandidate(fixture.JobId))
        {
            (await candidate.Books.CountAsync()).Should().BeGreaterThan(0);
        }

        var probe = candidatePath + ".probe";
        File.Move(candidatePath, probe);
        File.Move(probe, candidatePath);

        // Even a deliberately pooled candidate handle must be released by finalize.
        using (var pooled = new SqliteConnection($"Data Source={candidatePath}"))
        {
            pooled.Open();
            using var command = pooled.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM \"Books\";";
            Convert.ToInt32(command.ExecuteScalar()).Should().BeGreaterThan(0);
        }

        await using (var lease = await fixture.EnterExclusiveAsync())
        {
            await builder.FinalizeCandidateAsync(fixture.JobId, lease);
        }

        var moved = candidatePath + ".moved";
        File.Move(candidatePath, moved);
        File.Exists(moved).Should().BeTrue();
        if (OperatingSystem.IsLinux())
        {
            HasOpenDescriptor(candidatePath).Should().BeFalse();
            HasOpenDescriptor(moved).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Finalize_ClearsImportUndoHistory_AndCarriesNoRowReferencingMissingPortableState()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var builder = fixture.CreateBuilder();
        await builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);
        await using (var lease = await fixture.EnterExclusiveAsync())
        {
            await builder.FinalizeCandidateAsync(fixture.JobId, lease);
        }

        var candidatePath = fixture.Paths.CandidateDatabase(fixture.JobId);

        // Old-generation undo ownership is cleared, even when a GUID collides
        // with an imported note, while the rest of the host state was carried.
        await using (var candidate = builder.OpenCandidate(fixture.JobId))
        {
            (await candidate.MigrationJobRecords.CountAsync()).Should().BeGreaterThan(0);
            (await candidate.NoteImportBatches.CountAsync()).Should().Be(0);
            (await candidate.NoteImportBatchNotes.CountAsync()).Should().Be(0);
            (await candidate.Notes.AnyAsync(x => x.Id == fixture.Data.Notes.Single().Id)).Should().BeTrue();
        }

        ActivationBuildFixture.ForeignKeyCheckClean(candidatePath).Should().BeTrue();
        ActivationBuildFixture.QueryLong(
                candidatePath,
                "SELECT COUNT(*) FROM \"MigrationJobRecords\" j " +
                "WHERE j.\"ReservationId\" IS NOT NULL AND NOT EXISTS " +
                "(SELECT 1 FROM \"MigrationStorageReservations\" r WHERE r.\"Id\" = j.\"ReservationId\");")
            .Should().Be(0, "no carried job may reference a missing carried reservation");
        ActivationBuildFixture.QueryLong(
                candidatePath,
                "SELECT COUNT(*) FROM \"MigrationStorageReservations\" r " +
                "WHERE r.\"ClaimedJobId\" IS NOT NULL AND NOT EXISTS " +
                "(SELECT 1 FROM \"MigrationJobRecords\" j WHERE j.\"Id\" = r.\"ClaimedJobId\");")
            .Should().Be(0, "no carried reservation may reference a missing carried job");
    }

    [Theory]
    [InlineData("data")]
    [InlineData("portable")]
    public async Task Build_CrashAtEachStep_RebuildsFromScratch_WithIdenticalContent(string crashStep)
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var cleanJob = Guid.NewGuid();
        var cleanBuilder = fixture.CreateBuilder();
        await cleanBuilder.BuildPortableCandidateAsync(cleanJob, fixture.Prepared);

        var crashedBuilder = fixture.CreateBuilder();
        crashedBuilder.AfterBuildStepForTesting = step =>
        {
            if (step == crashStep)
            {
                throw new SimulatedBuildCrash();
            }
        };

        var act = () => crashedBuilder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);
        await act.Should().ThrowAsync<SimulatedBuildCrash>();
        File.Exists(fixture.Paths.CandidateDatabase(fixture.JobId))
            .Should().BeFalse("a failed build removes its partial candidate");

        var rebuiltBuilder = fixture.CreateBuilder();
        await rebuiltBuilder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);

        // Finalization stamps host state from the fixed clock, so both candidates
        // are fully deterministic and must be byte-identical table dumps.
        await using (var lease = await fixture.EnterExclusiveAsync())
        {
            await cleanBuilder.FinalizeCandidateAsync(cleanJob, lease);
        }

        cleanBuilder.IsFinalized(cleanJob).Should().BeTrue();
        ActivationBuildFixture.QueryLong(
                fixture.Paths.CandidateDatabase(cleanJob),
                "SELECT COUNT(*) FROM \"MigrationJobRecords\";")
            .Should().BeGreaterThan(0);
        ActivationBuildFixture.QueryLong(
                fixture.Paths.CandidateDatabase(cleanJob),
                "SELECT CAST(\"StateVersion\" AS INTEGER) FROM \"LibraryStates\";")
            .Should().Be(8);

        await using (var lease = await fixture.EnterExclusiveAsync())
        {
            await rebuiltBuilder.FinalizeCandidateAsync(fixture.JobId, lease);
        }

        rebuiltBuilder.IsFinalized(fixture.JobId).Should().BeTrue();
        ActivationBuildFixture.QueryLong(
                fixture.Paths.CandidateDatabase(fixture.JobId),
                "SELECT CAST(\"StateVersion\" AS INTEGER) FROM \"LibraryStates\";")
            .Should().Be(8);

        ActivationBuildFixture.DumpAllTables(fixture.Paths.CandidateDatabase(cleanJob))["LibraryStates"]
            .Should().ContainSingle().Which.Should().Contain("|8|");
        ActivationBuildFixture.DumpAllTables(fixture.Paths.CandidateDatabase(fixture.JobId))["LibraryStates"]
            .Should().ContainSingle().Which.Should().Contain("|8|");

        ActivationBuildFixture.DumpAllTables(fixture.Paths.CandidateDatabase(fixture.JobId))
            .Should().BeEquivalentTo(
                ActivationBuildFixture.DumpAllTables(fixture.Paths.CandidateDatabase(cleanJob)),
                "a rebuilt candidate must be identical to a clean build");
    }

    [Theory]
    [InlineData("write")]
    [InlineData("verify")]
    [InlineData("marker")]
    public async Task Finalize_CrashBeforeMarker_LeavesCandidateUnfinalized_AndReFinalizeSucceeds(string crashStep)
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var builder = fixture.CreateBuilder();
        await builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);

        await using var lease = await fixture.EnterExclusiveAsync();
        builder.BeforeFinalizeStepForTesting = step =>
        {
            if (step == crashStep)
            {
                throw new SimulatedBuildCrash();
            }
        };

        Func<Task> act = () => builder.FinalizeCandidateAsync(fixture.JobId, lease);
        await act.Should().ThrowAsync<SimulatedBuildCrash>();
        builder.IsFinalized(fixture.JobId).Should().BeFalse("only a complete finalization may mark the candidate");

        builder.BeforeFinalizeStepForTesting = null;
        await builder.FinalizeCandidateAsync(fixture.JobId, lease);
        builder.IsFinalized(fixture.JobId).Should().BeTrue();
        await using var candidate = builder.OpenCandidate(fixture.JobId);
        (await candidate.MigrationJobRecords.CountAsync()).Should().BeGreaterThan(0);
        (await candidate.BackupRecords.CountAsync()).Should().Be(ActivationBuildFixture.LiveBackupCount);
    }

    [Fact]
    public async Task Build_DiscardsAnAbandonedCrashLeftoverCandidate()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var candidatePath = fixture.Paths.CandidateDatabase(fixture.JobId);
        fixture.Paths.Prepare(fixture.JobId);
        await File.WriteAllTextAsync(candidatePath, "torn sqlite bytes");
        await File.WriteAllTextAsync(candidatePath + "-wal", "orphaned wal");
        await File.WriteAllTextAsync(
            fixture.Paths.CandidateFinalizationMarker(fixture.JobId),
            "{torn marker");

        var builder = fixture.CreateBuilder();
        await builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);

        File.Exists(candidatePath + "-wal").Should().BeFalse();
        builder.IsFinalized(fixture.JobId).Should().BeFalse();
        await using var candidate = builder.OpenCandidate(fixture.JobId);
        (await candidate.Books.CountAsync()).Should().Be((int)fixture.Prepared.Metadata.Counts.Books);
        ActivationBuildFixture.IntegrityOk(candidatePath).Should().BeTrue();
    }

    [Fact]
    public async Task Build_StagedDataHashMismatch_FailsClosed_WithoutTouchingTheLiveDatabase()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        var liveBefore = ActivationBuildFixture.Sha256(fixture.Paths.LiveDatabase);
        var tampered = new PortablePreparedImport(
            fixture.Prepared.Metadata with { DataSha256 = new string('0', 64) },
            fixture.Prepared.Media);

        var builder = fixture.CreateBuilder();
        var act = () => builder.BuildPortableCandidateAsync(fixture.JobId, tampered);
        var failure = await act.Should().ThrowAsync<MigrationActivationException>();
        failure.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);

        File.Exists(fixture.Paths.CandidateDatabase(fixture.JobId)).Should().BeFalse();
        ActivationBuildFixture.Sha256(fixture.Paths.LiveDatabase).Should().Be(liveBefore);
    }

    [Fact]
    public async Task Build_LeavesLiveDatabaseFileByteForByteUnchanged_UnderConcurrentReadsAndWrites()
    {
        await using var fixture = await ActivationBuildFixture.CreateAsync();
        // WAL plus disabled autocheckpoint means committed live writes land in
        // the WAL; the main file must stay byte-identical unless someone
        // checkpoints it. The anchor connection also keeps the WAL alive.
        SqliteConnection.ClearAllPools();
        using var anchor = new SqliteConnection(
            $"Data Source={fixture.Paths.LiveDatabase};Pooling=False");
        anchor.Open();
        Execute(anchor, "PRAGMA journal_mode=WAL;");
        Execute(anchor, "PRAGMA wal_autocheckpoint=0;");
        var liveBefore = ActivationBuildFixture.Sha256(fixture.Paths.LiveDatabase);

        var reachedPortable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = fixture.CreateBuilder();
        builder.AfterBuildStepForTesting = step =>
        {
            if (step == "data")
            {
                reachedPortable.TrySetResult();
            }
        };

        var build = builder.BuildPortableCandidateAsync(fixture.JobId, fixture.Prepared);
        await reachedPortable.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var writes = 0;
        for (var index = 0; index < 10; index++)
        {
            await using (var live = fixture.OpenLive())
            {
                _ = await live.BackupRecords.CountAsync();
                live.BackupRecords.Add(new BackupRecord
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = ActivationBuildFixture.FixedNow.AddMinutes(index),
                    SizeBytes = 100 + index,
                    Status = BackupStatus.Completed,
                    Provider = BackupProvider.Local,
                    IncludeBookFiles = false,
                });
                await live.SaveChangesAsync();
            }

            writes++;
        }

        await build;
        writes.Should().Be(10);

        ActivationBuildFixture.Sha256(fixture.Paths.LiveDatabase)
            .Should().Be(liveBefore, "the builder must never write or checkpoint the live main file");

        await using (var live = fixture.OpenLive())
        {
            (await live.BackupRecords.CountAsync())
                .Should().Be(ActivationBuildFixture.LiveBackupCount + writes,
                    "concurrent live writes must have committed while the build ran");
        }
    }

    private static bool HasOpenDescriptor(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var descriptor in Directory.EnumerateFiles("/proc/self/fd"))
        {
            string? target;
            try
            {
                target = File.ResolveLinkTarget(descriptor, returnFinalTarget: true)?.FullName;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (string.Equals(target, full, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class SimulatedBuildCrash : Exception;
}

/// <summary>
/// One live database, one fully prepared import staged on disk, and the paths
/// and maintenance coordinator needed to build and finalize a candidate beside
/// them.
/// </summary>
internal sealed class ActivationBuildFixture : IAsyncDisposable
{
    internal const string LiveOnlyBookTitle = "LIVE-ONLY-BOOK";
    internal const string LiveProviderModel = "live-private-model";
    internal const string LiveProviderSecret = "LIVE-PROVIDER-SECRET";
    internal const int LiveBackupCount = 1;

    internal static readonly DateTime FixedNow = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime FixedBuildTime = new(2026, 10, 4, 13, 30, 0, DateTimeKind.Utc);

    private ActivationBuildFixture(
        string root,
        string databaseRoot,
        string mediaRoot,
        string livePath,
        string liveMedia,
        LocalPortableTestLibrary source,
        LocalPortableImportStaging staging,
        IPreparedPortableImport prepared,
        PortableLibraryData data,
        PortableFixtureIds sourceIds)
    {
        Root = root;
        DatabaseRoot = databaseRoot;
        MediaRoot = mediaRoot;
        Paths = new SelfHostedActivationPaths(livePath, liveMedia);
        Source = source;
        Staging = staging;
        Prepared = prepared;
        Data = data;
        SourceIds = sourceIds;
    }

    internal string Root { get; }
    internal string DatabaseRoot { get; }
    internal string MediaRoot { get; }
    internal Guid JobId { get; } = Guid.NewGuid();
    internal Guid LiveOnlyNoteId { get; private set; }
    internal SelfHostedActivationPaths Paths { get; }
    internal LibraryMaintenanceCoordinator Maintenance { get; } = new();
    internal LocalPortableTestLibrary Source { get; }
    internal LocalPortableImportStaging Staging { get; }
    internal IPreparedPortableImport Prepared { get; }
    internal PortableLibraryData Data { get; }
    internal PortableFixtureIds SourceIds { get; }

    internal static async Task<ActivationBuildFixture> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nostos-activation-build-{Guid.NewGuid():N}");
        var databaseRoot = Path.Combine(root, "db-volume");
        var mediaRoot = Path.Combine(root, "media-volume");
        Directory.CreateDirectory(databaseRoot);
        Directory.CreateDirectory(mediaRoot);
        var livePath = Path.Combine(databaseRoot, "nostos.db");
        var liveMedia = Path.Combine(mediaRoot, "library");
        Directory.CreateDirectory(liveMedia);

        await BootstrapAsync(livePath);

        var source = await LocalPortableTestLibrary.CreateAsync();
        var sourceIds = await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);
        source.Db.NoteImportBookLinks.Add(new NoteImportBookLink
        {
            Id = Guid.NewGuid(),
            Source = "koreader",
            SourceKey = "activation-device-book",
            BookId = sourceIds.EpubBookId,
            CreatedAtUtc = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc),
        });
        await source.Db.SaveChangesAsync();

        var archivePath = Path.Combine(root, "source.nostos");
        await using (var archive = File.Create(archivePath))
        {
            await source.Portability().ExportAsync(archive);
        }

        var staging = new LocalPortableImportStaging(Path.Combine(root, "staging"));
        IPreparedPortableImport prepared;
        await using (var archiveSource = new FilePortableArchiveSource(archivePath))
        {
            prepared = await new PortableArchiveReader().PrepareImportAsync(
                archiveSource,
                staging,
                progress: null,
                cancellationToken: default);
        }

        var data = await PortableLibraryDatabaseMaterializer.ReadRelationalDataAsync(
            staging,
            prepared.Metadata.StagingId,
            default);

        var fixture = new ActivationBuildFixture(
            root,
            databaseRoot,
            mediaRoot,
            livePath,
            liveMedia,
            source,
            staging,
            prepared,
            data,
            sourceIds);
        await fixture.PopulateLiveHostStateAsync();
        return fixture;
    }

    internal static async Task BootstrapAsync(string path)
    {
        await using var db = new NostosDbContext(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}", sqlite =>
                sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options);
        await new DatabaseBootstrapService(db).EnsureReadyAsync();
    }

    internal SelfHostedActivationDatabaseBuilder CreateBuilder() =>
        new(Paths, Staging, Maintenance, new FixedBuildTimeProvider(FixedBuildTime));

    internal Task<IAsyncDisposable> EnterExclusiveAsync() =>
        Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);

    internal NostosDbContext OpenLive() => Open(Paths.LiveDatabase);

    internal async Task<long> CountCandidateRowsAsync(Guid jobId, string table)
    {
        await Task.CompletedTask;
        return QueryLong(Paths.CandidateDatabase(jobId), $"SELECT COUNT(*) FROM \"{table}\";");
    }

    private static NostosDbContext Open(string path) =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}", sqlite =>
                sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options);

    private async Task PopulateLiveHostStateAsync()
    {
        await using var live = Open(Paths.LiveDatabase);

        var state = await live.LibraryStates.SingleAsync();
        state.StateVersion = "7";
        state.UpdatedAt = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

        live.BackupRecords.Add(new BackupRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc),
            SizeBytes = 4096,
            Status = BackupStatus.Completed,
            Provider = BackupProvider.Local,
            LocalArchivePath = "Storage/backups/live.nostos",
            ManifestJson = "{\"version\":1}",
            IncludeBookFiles = true,
        });

        live.AiProviderSettings.Add(new AiProviderSettingsModel
        {
            LlmEnabled = true,
            LlmBaseUrl = "https://live-provider.example.test",
            LlmModel = LiveProviderModel,
            LlmApiKeyEncrypted = LiveProviderSecret,
            EmbeddingModel = "live-embedding",
            UpdatedAtUtc = new DateTime(2026, 9, 28, 7, 0, 0, DateTimeKind.Utc),
        });

        live.LibraryCommandReceipts.Add(new LibraryCommandReceipt
        {
            Id = Guid.NewGuid(),
            ClientId = "activation-client",
            IdempotencyKey = "activation-command",
            CommandKind = "book.update",
            ResponseJson = "{\"stateVersion\":\"7\"}",
            CreatedAt = new DateTime(2026, 9, 27, 6, 0, 0, DateTimeKind.Utc),
        });

        live.NoteCommandReceipts.Add(new NoteCommandReceipt
        {
            Id = Guid.NewGuid(),
            ClientId = "activation-client",
            IdempotencyKey = "activation-note-command",
            Command = "note.create",
            ResultJson = "{\"noteId\":\"00000000-0000-0000-0000-000000000001\"}",
            CreatedAtUtc = new DateTime(2026, 9, 26, 5, 0, 0, DateTimeKind.Utc),
        });

        // The job performing activation and its transfer bookkeeping.
        var reservation = new MigrationStorageReservationRecord
        {
            Id = Guid.NewGuid(),
            Purpose = 0,
            ReservedBytes = 20480,
            MaterializedBytes = 1024,
            CreatedAtUtc = new DateTime(2026, 9, 25, 4, 0, 0, DateTimeKind.Utc),
            ExpiresAtUtc = new DateTime(2026, 10, 25, 4, 0, 0, DateTimeKind.Utc),
            ClaimedJobId = JobId,
            Version = 1,
        };
        var activationJob = new MigrationJobRecord
        {
            Id = JobId,
            Direction = (int)MigrationDirection.Import,
            State = (int)MigrationJobState.ReadyToActivate,
            RecoveryStatus = (int)MigrationRecoveryStatus.NotRequired,
            CreatedAtUtc = new DateTime(2026, 9, 25, 4, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 9, 25, 4, 30, 0, DateTimeKind.Utc),
            HeartbeatAtUtc = new DateTime(2026, 9, 25, 4, 30, 0, DateTimeKind.Utc),
            IdempotencyKey = "activation-job",
            CreationPayloadHash = new string('a', 64),
            ExpiresAtUtc = new DateTime(2026, 10, 25, 4, 0, 0, DateTimeKind.Utc),
            AttemptNumber = 1,
            DestinationRevision = "revision-7",
            ReservedStorageBytes = 12345,
            PreparedStagingId = Prepared.Metadata.StagingId.Value,
            PreparedImportMetadataJson = "{\"integrity\":true}",
            ReservationId = reservation.Id,
            Version = 3,
        };
        var exportJob = new MigrationJobRecord
        {
            Id = Guid.NewGuid(),
            Direction = (int)MigrationDirection.Export,
            State = (int)MigrationJobState.Completed,
            RecoveryStatus = (int)MigrationRecoveryStatus.NotRequired,
            CreatedAtUtc = new DateTime(2026, 9, 24, 3, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 9, 24, 3, 30, 0, DateTimeKind.Utc),
            IdempotencyKey = "export-job",
            CreationPayloadHash = new string('b', 64),
            ExpiresAtUtc = new DateTime(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc),
            AttemptNumber = 1,
            Version = 2,
        };
        live.MigrationJobRecords.AddRange(activationJob, exportJob);
        live.MigrationStorageReservations.Add(reservation);

        var session = new MigrationSessionRecord
        {
            Id = Guid.NewGuid(),
            JobId = JobId,
            Purpose = 0,
            State = 3,
            TotalBytes = 1024,
            ChunkSize = MigrationContractLimits.DefaultChunkBytes,
            TotalChunks = 1,
            FileIdentitySizeBytes = 1024,
            FileIdentitySha256 = new string('c', 64),
            ClientFingerprint = "activation-client-fingerprint",
            IdempotencyKey = "activation-session",
            CreationPayloadHash = new string('d', 64),
            ReceivedBytes = 1024,
            CreatedAtUtc = new DateTime(2026, 9, 25, 4, 1, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 9, 25, 4, 2, 0, DateTimeKind.Utc),
            ExpiresAtUtc = new DateTime(2026, 10, 25, 4, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 9, 25, 4, 2, 0, DateTimeKind.Utc),
            StorageKey = "migration/sessions/activation-session",
            Version = 1,
        };
        live.MigrationSessionRecords.Add(session);
        live.MigrationChunkReceiptRecords.Add(new MigrationChunkReceiptRecord
        {
            SessionId = session.Id,
            ChunkIndex = 0,
            OffsetBytes = 0,
            LengthBytes = 1024,
            Sha256 = new string('e', 64),
            ReceivedAtUtc = new DateTime(2026, 9, 25, 4, 2, 0, DateTimeKind.Utc),
        });
        live.MigrationExportArtifactRecords.Add(new MigrationExportArtifactRecord
        {
            JobId = exportJob.Id,
            State = (int)MigrationExportArtifactState.Available,
            StorageKey = "migration/exports/live-export.nostos",
            FileName = "live-export.nostos",
            ContentType = "application/zip",
            SizeBytes = 8192,
            Sha256 = new string('f', 64),
            CreatedAtUtc = new DateTime(2026, 9, 24, 3, 1, 0, DateTimeKind.Utc),
            AvailableAtUtc = new DateTime(2026, 9, 24, 3, 2, 0, DateTimeKind.Utc),
            ExpiresAtUtc = new DateTime(2026, 9, 29, 3, 0, 0, DateTimeKind.Utc),
            Version = 1,
        });

        // Import undo history: one link whose note also arrives with the portable
        // payload (GUID collision) and one link whose note only exists in the old
        // live library. Both must be cleared by finalization.
        var work = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Live-only work",
            NormalizedTitle = "LIVE-ONLY WORK",
            NormalizedAuthor = string.Empty,
            CreatedAt = FixedNow,
        };
        var liveOnlyBook = new EBookModel
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Title = LiveOnlyBookTitle,
            CreatedAt = FixedNow,
        };
        var liveOnlyNote = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = liveOnlyBook.Id,
            Book = liveOnlyBook,
            Content = "live-only note",
            CreatedAt = FixedNow,
        };
        LiveOnlyNoteId = liveOnlyNote.Id;

        var collisionWork = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Collision work",
            NormalizedTitle = "COLLISION WORK",
            NormalizedAuthor = string.Empty,
            CreatedAt = FixedNow,
        };
        var collisionBook = new EBookModel
        {
            Id = SourceIds.EpubBookId,
            WorkId = collisionWork.Id,
            Work = collisionWork,
            Title = "collision book",
            CreatedAt = FixedNow,
        };
        var collisionNote = new NoteModel
        {
            Id = SourceIds.NoteId,
            BookId = collisionBook.Id,
            Book = collisionBook,
            Content = "collision note",
            CreatedAt = FixedNow,
        };

        var survivingBatch = new NoteImportBatch
        {
            Id = Guid.NewGuid(),
            Source = "koreader",
            FileName = "surviving.ko",
            CreatedAtUtc = new DateTime(2026, 9, 23, 2, 0, 0, DateTimeKind.Utc),
        };
        var droppedBatch = new NoteImportBatch
        {
            Id = Guid.NewGuid(),
            Source = "koreader",
            FileName = "dropped.ko",
            CreatedAtUtc = new DateTime(2026, 9, 22, 2, 0, 0, DateTimeKind.Utc),
        };

        live.Works.AddRange(work, collisionWork);
        live.Books.AddRange(liveOnlyBook, collisionBook);
        live.Notes.AddRange(liveOnlyNote, collisionNote);
        live.NoteImportBatches.AddRange(survivingBatch, droppedBatch);
        live.NoteImportBatchNotes.AddRange(
            new NoteImportBatchNote
            {
                BatchId = survivingBatch.Id,
                Batch = survivingBatch,
                NoteId = collisionNote.Id,
                Note = collisionNote,
                ClientId = "activation-client",
                IdempotencyKey = "surviving-link",
            },
            new NoteImportBatchNote
            {
                BatchId = droppedBatch.Id,
                Batch = droppedBatch,
                NoteId = liveOnlyNote.Id,
                Note = liveOnlyNote,
                ClientId = "activation-client",
                IdempotencyKey = "dropped-link",
            });

        await live.SaveChangesAsync();
    }

    /// <summary>
    /// Every carried table must be byte-identical between the live snapshot and
    /// the finalized candidate; the singleton revision is asserted separately.
    /// </summary>
    internal static void AssertCarriedTablesMatch(string livePath, string candidatePath)
    {
        var tables = CarryTables();
        var live = DumpTables(livePath, tables);
        var candidate = DumpTables(candidatePath, tables);
        foreach (var table in tables)
        {
            if (table == "LibraryStates")
            {
                continue; // advanced by finalization; asserted explicitly
            }

            candidate[table].Should().BeEquivalentTo(
                live[table],
                $"carry-over table {table} must survive activation unchanged");
        }
    }

    internal static IReadOnlyList<string> CarryTables()
    {
        using var model = new NostosDbContext(
            new DbContextOptionsBuilder<NostosDbContext>().UseSqlite("Data Source=:memory:").Options);
        return SelfHostedHostStateCarryOver.CarryDecisions
            .Select(decision => model.Model.FindEntityType(decision.EntityType)!.GetTableName()!)
            .ToArray();
    }

    private static Dictionary<string, List<string>> DumpTables(
        string path,
        IReadOnlyList<string> tables)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\";";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new List<string>();
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    var value = reader.IsDBNull(index) ? null : reader.GetValue(index);
                    values.Add(value switch
                    {
                        null => "null",
                        byte[] bytes => Convert.ToHexString(bytes),
                        IFormattable formattable => formattable.ToString(
                            null,
                            System.Globalization.CultureInfo.InvariantCulture),
                        _ => value.ToString() ?? string.Empty,
                    });
                }

                rows.Add(string.Join("|", values));
            }

            rows.Sort(StringComparer.Ordinal);
            result[table] = rows;
        }

        return result;
    }

    /// <summary>Dumps the complete database (every user table) deterministically.</summary>
    internal static Dictionary<string, List<string>> DumpAllTables(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        return DumpTables(path, tables);
    }

    internal static string SchemaFingerprint(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var builder = new StringBuilder();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT type || '|' || name || '|' || COALESCE(tbl_name, '') || '|' || COALESCE(sql, '') " +
                "FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                builder.AppendLine(reader.GetString(0));
            }
        }

        builder.AppendLine("--history--");
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT MigrationId || '|' || ProductVersion FROM \"__EFMigrationsHistory\" ORDER BY MigrationId;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                builder.AppendLine(reader.GetString(0));
            }
        }

        return builder.ToString();
    }

    internal static bool IntegrityOk(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal);
    }

    internal static bool ForeignKeyCheckClean(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        return !reader.Read();
    }

    internal static long QueryLong(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static Dictionary<string, string> MediaSnapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        await Staging.DisposeAsync();
        await Source.DisposeAsync();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup only.
        }
    }
}

internal sealed class FixedBuildTimeProvider(DateTime utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
}
