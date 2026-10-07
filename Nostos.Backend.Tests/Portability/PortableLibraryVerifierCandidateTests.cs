using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Activation verification materializes real libraries under the process temp path.
/// The scaled archive measurement test snapshots that path and fails on any
/// concurrent scratch directory, so these tests must not run beside other
/// collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PortableLibraryVerificationCollection
{
    public const string Name = "PortableLibraryVerification";
}

/// <summary>
/// Builds one representative archive and its verified expected state once per test
/// class; every test materializes a fresh candidate through the real import-apply
/// path and mutates exactly one portable fact.
/// </summary>
public sealed class CandidateVerificationFixture : IAsyncLifetime
{
    public byte[] ArchiveBytes { get; private set; } = [];

    public PortablePreparedImport Prepared { get; private set; } = null!;

    public PortablePreparedImportVerification Expected { get; private set; } = null!;

    public PortableLibraryVerifier Verifier { get; } = new();

    public Guid EpubBookId { get; private set; }

    public Guid AudioBookId { get; private set; }

    public Guid NoteId { get; private set; }

    public Guid TopicId { get; private set; }

    public Guid CollectionId { get; private set; }

    public Guid WritingDocumentId { get; private set; }

    public Guid WorkId { get; private set; }

    public Guid AcquisitionId { get; private set; }

    public Guid ImportLinkId { get; private set; }

    public async Task InitializeAsync()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var ids = await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            source.Db,
            source.Storage);
        EpubBookId = ids.EpubBookId;
        AudioBookId = ids.AudioBookId;
        NoteId = ids.NoteId;
        TopicId = ids.TopicId;
        CollectionId = ids.ReadingCollectionId;
        WritingDocumentId = ids.WritingDocumentId;
        WorkId = await source.Db.Books
            .Where(book => book.Id == ids.EpubBookId)
            .Select(book => book.WorkId)
            .SingleAsync();
        AcquisitionId = await source.Db.BookAcquisitions
            .Select(acquisition => acquisition.Id)
            .SingleAsync();

        ImportLinkId = Guid.NewGuid();
        source.Db.NoteImportBookLinks.Add(new NoteImportBookLink
        {
            Id = ImportLinkId,
            Source = "kobo",
            SourceKey = "device-book-one",
            BookId = ids.EpubBookId,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
        });
        await source.Db.SaveChangesAsync();

        ArchiveBytes = await SelfHostedActivationTestSupport.ExportArchiveAsync(source);

        var store = new InMemoryPortableImportStagingStore();
        await using var staging = new InMemoryPortableImportStaging(store);
        Prepared = await SelfHostedActivationTestSupport.PrepareStagedAsync(
            ArchiveBytes,
            staging);
        Expected = await Verifier.VerifyPreparedImportAsync(staging, Prepared);
        Expected.Passed.Should().BeTrue(
            string.Join("; ", Expected.Failures.Select(failure => failure.Code)));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    internal async Task<LocalPortableTestLibrary> CreateCandidateAsync()
    {
        var candidate = await LocalPortableTestLibrary.CreateAsync();
        try
        {
            using var archive = new MemoryStream(ArchiveBytes, writable: false);
            await candidate.Portability().ImportAsync(archive);
            return candidate;
        }
        catch
        {
            await candidate.DisposeAsync();
            throw;
        }
    }
}

[Collection(PortableLibraryVerificationCollection.Name)]
public sealed class PortableLibraryVerifierCandidateTests(CandidateVerificationFixture fixture)
    : IClassFixture<CandidateVerificationFixture>
{
    private readonly CandidateVerificationFixture _fixture = fixture;

    [Fact]
    public async Task Matching_candidate_passes_and_covers_every_portable_kind()
    {
        await using var candidate = await _fixture.CreateCandidateAsync();

        var report = await _fixture.Verifier.VerifyCandidateAsync(
            candidate.Db,
            candidate.Storage.StorageRoot,
            _fixture.Prepared,
            _fixture.Expected);

        report.Passed.Should().BeTrue(
            string.Join("; ", report.Failures.Select(failure => $"{failure.Code}:{failure.Entity}:{failure.Field}")));
        report.Failures.Should().BeEmpty();
        report.FailureCount.Should().Be(0);
        report.VerifiedKinds.Should().BeEquivalentTo(PortableLibraryVerifier.CandidateVerifiedKinds);
        report.MediaFilesVerified.Should().Be(_fixture.Expected.Media.Count);
        report.MediaBytesVerified.Should().Be(_fixture.Expected.Media.Sum(item => item.Length));
        report.PortableRowsVerified.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Verification_is_read_only_and_ignores_host_operational_state()
    {
        await using var candidate = await _fixture.CreateCandidateAsync();
        candidate.Db.BackupRecords.Add(new BackupRecord
        {
            Status = BackupStatus.Completed,
            SizeBytes = 1,
        });
        candidate.Db.MigrationJobRecords.Add(new MigrationJobRecord
        {
            Direction = 0,
            State = 4,
            RecoveryStatus = 0,
            IdempotencyKey = "host-operational-row",
            CreationPayloadHash = "hash",
            AttemptNumber = 1,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
        });
        // The candidate carries the live revision row (the portable restore is
        // a portable mutation, so the revision writer seeds one); stamp the
        // host revision in place instead of inserting a duplicate singleton.
        var state = await candidate.Db.LibraryStates.SingleOrDefaultAsync();
        if (state is null)
        {
            candidate.Db.LibraryStates.Add(new LibraryState { StateVersion = "42" });
        }
        else
        {
            state.StateVersion = "42";
        }
        var linkCount = await candidate.Db.NoteImportBookLinks.CountAsync();
        await candidate.Db.SaveChangesAsync();
        candidate.Db.ChangeTracker.Clear();

        var report = await _fixture.Verifier.VerifyCandidateAsync(
            candidate.Db,
            candidate.Storage.StorageRoot,
            _fixture.Prepared,
            _fixture.Expected);

        report.Passed.Should().BeTrue(
            string.Join("; ", report.Failures.Select(failure => $"{failure.Code}:{failure.Entity}:{failure.Field}")));
        candidate.Db.ChangeTracker.HasChanges().Should().BeFalse();
        (await candidate.Db.NoteImportBookLinks.CountAsync()).Should().Be(linkCount);
        (await candidate.Db.LibraryStates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Book_title_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var book = await candidate.Db.Books.SingleAsync(item => item.Id == _fixture.EpubBookId);
            book.Title = "Changed title";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "book", nameof(BookModel.Title));
    }

    [Fact]
    public async Task Book_reading_state_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var book = await candidate.Db.Books.SingleAsync(item => item.Id == _fixture.EpubBookId);
            book.Progress.Rating = 1;
            book.Progress.IsFavorite = false;
            book.Progress.PersonalReview = "Changed review";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "book", nameof(ReadingProgress.PersonalReview));
    }

    [Fact]
    public async Task Book_metadata_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var book = await candidate.Db.Books.SingleAsync(item => item.Id == _fixture.EpubBookId);
            book.Metadata.Publisher = "Changed publisher";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "book", nameof(BookMetadata.Publisher));
    }

    [Fact]
    public async Task Note_content_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var note = await candidate.Db.Notes.SingleAsync(item => item.Id == _fixture.NoteId);
            note.Content = "Changed content";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "note", nameof(NoteModel.Content));
    }

    [Fact]
    public async Task Note_anchor_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var note = await candidate.Db.Notes.SingleAsync(item => item.Id == _fixture.NoteId);
            note.SourceAnchorValue = "changed-anchor";
            note.AnchorVerified = false;
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "note", nameof(NoteModel.SourceAnchorValue));
    }

    [Fact]
    public async Task Collection_membership_timestamp_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var membership = await candidate.Db.BookCollections
                .SingleAsync(item => item.BookId == _fixture.EpubBookId
                    && item.CollectionId == _fixture.CollectionId);
            membership.AddedAt = membership.AddedAt.AddDays(1);
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "bookCollection", nameof(BookCollectionModel.AddedAt));
    }

    [Fact]
    public async Task Collection_membership_removal_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var membership = await candidate.Db.BookCollections
                .SingleAsync(item => item.BookId == _fixture.EpubBookId
                    && item.CollectionId == _fixture.CollectionId);
            candidate.Db.BookCollections.Remove(membership);
            await candidate.Db.SaveChangesAsync();
        });

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.RelationshipMismatch
            && failure.Entity == "bookCollection");
    }

    [Fact]
    public async Task Collection_name_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var collection = await candidate.Db.Collections.SingleAsync(
                item => item.Id == _fixture.CollectionId);
            collection.Name = "Changed collection";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "collection", nameof(CollectionModel.Name));
    }

    [Fact]
    public async Task Writing_content_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var writing = await candidate.Db.Writings.SingleAsync(
                item => item.Id == _fixture.WritingDocumentId);
            writing.Content = "<p>Changed prose.</p>";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "writing", nameof(WritingModel.Content));
    }

    [Fact]
    public async Task Writing_note_timestamp_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var link = await candidate.Db.WritingNotes.SingleAsync(
                item => item.WritingId == _fixture.WritingDocumentId);
            link.AddedAt = link.AddedAt.AddDays(1);
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "writingNote", nameof(WritingNoteModel.AddedAt));
    }

    [Fact]
    public async Task Work_title_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var work = await candidate.Db.Works.SingleAsync(item => item.Id == _fixture.WorkId);
            work.Title = "Changed work";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "work", nameof(WorkModel.Title));
    }

    [Fact]
    public async Task Acquisition_field_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var acquisition = await candidate.Db.BookAcquisitions.SingleAsync(
                item => item.Id == _fixture.AcquisitionId);
            acquisition.ExternalId = "changed-external";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "acquisition", nameof(BookAcquisitionModel.ExternalId));
    }

    [Fact]
    public async Task Note_import_book_link_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var link = await candidate.Db.NoteImportBookLinks.SingleAsync(
                item => item.Id == _fixture.ImportLinkId);
            link.SourceKey = "changed-source-key";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "noteImportBookLink", nameof(NoteImportBookLink.SourceKey));
    }

    [Fact]
    public async Task Assistant_setting_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var row = await candidate.Db.AssistantSettings.SingleAsync();
            row.CaptureProcessingMode = "clarify";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "assistantSettings", nameof(AssistantSettingsModel.CaptureProcessingMode));
    }

    [Fact]
    public async Task Extra_portable_row_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            candidate.Db.Topics.Add(new TopicModel { Topic = "unexpected-topic" });
            await candidate.Db.SaveChangesAsync();
        });

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.UnexpectedEntity
            && failure.Entity == "topic");
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.CountMismatch
            && failure.Field == nameof(MigrationArchiveCounts.Topics));
    }

    [Fact]
    public async Task Missing_portable_row_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var link = await candidate.Db.NoteTopics.SingleAsync();
            candidate.Db.NoteTopics.Remove(link);
            var topic = await candidate.Db.Topics.SingleAsync();
            candidate.Db.Topics.Remove(topic);
            await candidate.Db.SaveChangesAsync();
        });

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MissingEntity
            && failure.Entity == "topic");
    }

    [Fact]
    public async Task Broken_note_book_relationship_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var note = await candidate.Db.Notes.SingleAsync(item => item.Id == _fixture.NoteId);
            note.BookId = _fixture.AudioBookId;
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "note", nameof(NoteModel.BookId));
    }

    [Fact]
    public async Task Corrupt_candidate_media_byte_keeping_length_is_detected()
    {
        var report = await VerifyAsync(candidate =>
        {
            var path = MediaPath(candidate, 0);
            var bytes = File.ReadAllBytes(path);
            bytes[0] ^= 0xff;
            File.WriteAllBytes(path, bytes);
            return Task.CompletedTask;
        });

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MediaHashMismatch
            && failure.Entity == "media");
        report.MediaFilesVerified.Should().Be(_fixture.Expected.Media.Count - 1);
    }

    [Fact]
    public async Task Missing_candidate_media_file_is_detected()
    {
        var report = await VerifyAsync(candidate =>
        {
            File.Delete(MediaPath(candidate, 0));
            return Task.CompletedTask;
        });

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MediaMissing);
    }

    [Fact]
    public async Task Unexpected_candidate_media_file_is_detected()
    {
        var report = await VerifyAsync(candidate =>
        {
            var folder = Path.Combine(
                candidate.Storage.StorageRoot,
                Guid.NewGuid().ToString());
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "book.txt"), "stale media");
            return Task.CompletedTask;
        });

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MediaUnexpectedFile);
    }

    [Fact]
    public async Task Assistant_value_is_rejected_when_expected_absent()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(source.Db, source.Storage);
        source.Db.AssistantSettings.RemoveRange(source.Db.AssistantSettings);
        await source.Db.SaveChangesAsync();
        var archive = await SelfHostedActivationTestSupport.ExportArchiveAsync(source);

        var store = new InMemoryPortableImportStagingStore();
        await using var staging = new InMemoryPortableImportStaging(store);
        var prepared = await SelfHostedActivationTestSupport.PrepareStagedAsync(archive, staging);
        var expected = await _fixture.Verifier.VerifyPreparedImportAsync(staging, prepared);
        expected.Passed.Should().BeTrue();
        expected.Media.Should().NotBeEmpty();

        await using var candidate = await LocalPortableTestLibrary.CreateAsync();
        using var stream = new MemoryStream(archive, writable: false);
        await candidate.Portability().ImportAsync(stream);
        var row = await candidate.Db.AssistantSettings.SingleOrDefaultAsync();
        if (row is null)
        {
            row = new AssistantSettingsModel();
            candidate.Db.AssistantSettings.Add(row);
        }

        row.CaptureProcessingMode = null;
        await candidate.Db.SaveChangesAsync();

        var report = await _fixture.Verifier.VerifyCandidateAsync(
            candidate.Db,
            candidate.Storage.StorageRoot,
            prepared,
            expected);

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.SingletonMismatch);
    }

    [Fact]
    public async Task Book_file_name_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var book = await candidate.Db.Books.SingleAsync(item => item.Id == _fixture.EpubBookId);
            book.FileDetails.FileName = "book.pdf";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "book", nameof(FileInfoDetails.FileName));
    }

    [Fact]
    public async Task Book_cover_file_name_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var book = await candidate.Db.Books.SingleAsync(item => item.Id == _fixture.EpubBookId);
            book.FileDetails.CoverFileName = "cover.png";
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "book", nameof(FileInfoDetails.CoverFileName));
    }

    [Fact]
    public async Task Stale_cover_thumbnail_is_rejected()
    {
        var report = await VerifyAsync(candidate =>
        {
            var cover = _fixture.Expected.Media.Single(descriptor => descriptor.Kind == "cover"
                && descriptor.BookId == _fixture.EpubBookId);
            var folder = Path.Combine(candidate.Storage.StorageRoot, cover.BookId.ToString());
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "cover-thumb-320.webp"), "stale thumbnail");
            return Task.CompletedTask;
        });

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MediaUnexpectedFile);
    }

    [Fact]
    public async Task Assistant_updated_at_change_is_detected()
    {
        var report = await VerifyAsync(async candidate =>
        {
            var row = await candidate.Db.AssistantSettings.SingleAsync();
            row.UpdatedAtUtc = row.UpdatedAtUtc.AddDays(1);
            await candidate.Db.SaveChangesAsync();
        });

        ShouldFail(report, "assistantSettings", nameof(AssistantSettingsModel.UpdatedAtUtc));
    }

    [Fact]
    public async Task Null_mode_singleton_baseline_passes()
    {
        var (candidate, prepared, expected) = await CreateNullModeScenarioAsync();
        await using var _ = candidate;

        var report = await _fixture.Verifier.VerifyCandidateAsync(
            candidate.Db,
            candidate.Storage.StorageRoot,
            prepared,
            expected);

        report.Passed.Should().BeTrue(
            string.Join("; ", report.Failures.Select(failure => $"{failure.Code}:{failure.Field}")));
    }

    [Fact]
    public async Task Null_mode_singleton_row_removal_is_detected()
    {
        var (candidate, prepared, expected) = await CreateNullModeScenarioAsync();
        await using var _ = candidate;
        candidate.Db.AssistantSettings.RemoveRange(candidate.Db.AssistantSettings);
        await candidate.Db.SaveChangesAsync();

        var report = await _fixture.Verifier.VerifyCandidateAsync(
            candidate.Db,
            candidate.Storage.StorageRoot,
            prepared,
            expected);

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.SingletonMismatch);
    }

    [Fact]
    public async Task Null_mode_singleton_timestamp_change_is_detected()
    {
        var (candidate, prepared, expected) = await CreateNullModeScenarioAsync();
        await using var _ = candidate;
        var row = await candidate.Db.AssistantSettings.SingleAsync();
        row.UpdatedAtUtc = row.UpdatedAtUtc.AddDays(1);
        await candidate.Db.SaveChangesAsync();

        var report = await _fixture.Verifier.VerifyCandidateAsync(
            candidate.Db,
            candidate.Storage.StorageRoot,
            prepared,
            expected);

        ShouldFail(report, "assistantSettings", nameof(AssistantSettingsModel.UpdatedAtUtc));
    }

    [Fact]
    public async Task Null_mode_singleton_mode_swap_is_detected()
    {
        var (candidate, prepared, expected) = await CreateNullModeScenarioAsync();
        await using var _ = candidate;
        var row = await candidate.Db.AssistantSettings.SingleAsync();
        row.CaptureProcessingMode = "verbatim";
        await candidate.Db.SaveChangesAsync();

        var report = await _fixture.Verifier.VerifyCandidateAsync(
            candidate.Db,
            candidate.Storage.StorageRoot,
            prepared,
            expected);

        ShouldFail(report, "assistantSettings", nameof(AssistantSettingsModel.CaptureProcessingMode));
    }

    [Fact]
    public async Task Verification_handle_from_another_prepared_import_is_rejected()
    {
        await using var candidate = await _fixture.CreateCandidateAsync();
        var store = new InMemoryPortableImportStagingStore();
        await using var otherStaging = new InMemoryPortableImportStaging(store);
        var otherPrepared = await SelfHostedActivationTestSupport.PrepareStagedAsync(
            _fixture.ArchiveBytes,
            otherStaging);

        var report = await _fixture.Verifier.VerifyCandidateAsync(
            candidate.Db,
            candidate.Storage.StorageRoot,
            otherPrepared,
            _fixture.Expected);

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.ExpectedStateMismatch);
    }

    [Fact]
    public async Task Divergent_candidate_failure_details_are_bounded()
    {
        var report = await VerifyAsync(async candidate =>
        {
            for (var index = 0; index < 200; index++)
            {
                candidate.Db.Topics.Add(new TopicModel { Topic = $"unexpected-{index}" });
            }

            await candidate.Db.SaveChangesAsync();
        });

        report.Passed.Should().BeFalse();
        report.FailureCount.Should().BeGreaterThan(PortableLibraryVerifier.MaxRetainedFailures);
        report.Failures.Count.Should().BeLessThanOrEqualTo(PortableLibraryVerifier.MaxRetainedFailures);
    }

    [Fact]
    public async Task Candidate_media_is_served_by_live_storage_and_thumbnails_generate_on_demand()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        var now = DateTime.UtcNow.AddDays(-1);
        var work = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Real image",
            NormalizedTitle = "REAL IMAGE",
            NormalizedAuthor = string.Empty,
            CreatedAt = now,
        };
        var book = new EBookModel
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Title = "Real image",
            CreatedAt = now,
            FileDetails = new FileInfoDetails
            {
                HasFile = true,
                FileName = "book.epub",
                CoverFileName = "cover.png",
            },
        };
        source.Db.Works.Add(work);
        source.Db.Books.Add(book);
        await source.Db.SaveChangesAsync();

        using var image = new Image<Rgba32>(4, 4);
        image[0, 0] = new Rgba32(10, 20, 30);
        using var png = new MemoryStream();
        await image.SaveAsPngAsync(png);
        png.Position = 0;
        await source.Storage.SaveBookCoverAsync(book.Id, png, "cover.png");
        await source.Storage.SaveBookFileAsync(
            book.Id,
            new MemoryStream(Encoding.UTF8.GetBytes("EPUB-BYTES")),
            "book.epub");

        var archive = await SelfHostedActivationTestSupport.ExportArchiveAsync(source);
        var store = new InMemoryPortableImportStagingStore();
        await using var staging = new InMemoryPortableImportStaging(store);
        var prepared = await SelfHostedActivationTestSupport.PrepareStagedAsync(archive, staging);
        var verifier = new PortableLibraryVerifier();
        var expected = await verifier.VerifyPreparedImportAsync(staging, prepared);
        expected.Passed.Should().BeTrue();

        await using var candidate = await LocalPortableTestLibrary.CreateAsync();
        using var stream = new MemoryStream(archive, writable: false);
        await candidate.Portability().ImportAsync(stream);

        var report = await verifier.VerifyCandidateAsync(
            candidate.Db,
            candidate.Storage.StorageRoot,
            prepared,
            expected);
        report.Passed.Should().BeTrue(
            string.Join("; ", report.Failures.Select(failure => $"{failure.Code}:{failure.Field}")));

        foreach (var descriptor in expected.Media)
        {
            StoredAssetRead? opened = descriptor.Kind == "book"
                ? await candidate.Storage.OpenBookFileAsync(descriptor.BookId)
                : await candidate.Storage.OpenBookCoverAsync(descriptor.BookId);
            opened.Should().NotBeNull();
            await using (opened!)
            {
                var payload = await SelfHostedActivationTestSupport.ReadAllAsync(opened.Content);
                payload.LongLength.Should().Be(descriptor.Length);
                PortableArchiveTestSupport.Sha256Hex(payload).Should().Be(descriptor.Sha256);
            }
        }

        var thumbnail = await candidate.Storage.GetBookCoverThumbnailInfoAsync(book.Id, 320);
        thumbnail.Should().NotBeNull();
        File.Exists(Path.Combine(
            candidate.Storage.StorageRoot,
            book.Id.ToString(),
            "cover-thumb-320.webp")).Should().BeTrue();

        await using var thumbnailRead = await candidate.Storage.OpenBookCoverThumbnailAsync(book.Id, 320);
        thumbnailRead.Should().NotBeNull();
        thumbnailRead!.Info.Length.Should().BeGreaterThan(0);
    }

    private static async Task<(
        LocalPortableTestLibrary Candidate,
        PortablePreparedImport Prepared,
        PortablePreparedImportVerification Expected)> CreateNullModeScenarioAsync()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(source.Db, source.Storage);
        var assistant = await source.Db.AssistantSettings.SingleAsync();
        assistant.CaptureProcessingMode = null;
        assistant.UpdatedAtUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        await source.Db.SaveChangesAsync();
        var archive = await SelfHostedActivationTestSupport.ExportArchiveAsync(source);

        PortablePreparedImport prepared;
        PortablePreparedImportVerification expected;
        var store = new InMemoryPortableImportStagingStore();
        await using (var staging = new InMemoryPortableImportStaging(store))
        {
            prepared = await SelfHostedActivationTestSupport.PrepareStagedAsync(archive, staging);
            expected = await new PortableLibraryVerifier().VerifyPreparedImportAsync(staging, prepared);
            expected.Passed.Should().BeTrue(
                string.Join("; ", expected.Failures.Select(failure => failure.Code)));
        }

        var candidate = await LocalPortableTestLibrary.CreateAsync();
        using var stream = new MemoryStream(archive, writable: false);
        await candidate.Portability().ImportAsync(stream);
        return (candidate, prepared, expected);
    }

    private async Task<PortableLibraryVerificationReport> VerifyAsync(
        Func<LocalPortableTestLibrary, Task> mutate)
    {
        await using var candidate = await _fixture.CreateCandidateAsync();
        await mutate(candidate);
        return await _fixture.Verifier.VerifyCandidateAsync(
            candidate.Db,
            candidate.Storage.StorageRoot,
            _fixture.Prepared,
            _fixture.Expected);
    }

    private string MediaPath(LocalPortableTestLibrary candidate, int index)
    {
        var descriptor = _fixture.Expected.Media[index];
        return Path.Combine(
            candidate.Storage.StorageRoot,
            descriptor.BookId.ToString(),
            descriptor.FileName);
    }

    private static void ShouldFail(
        PortableLibraryVerificationReport report,
        string entity,
        string? field = null)
    {
        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.FieldMismatch
            && failure.Entity == entity
            && (field == null || failure.Field == field));
    }
}
