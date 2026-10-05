using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// The database-only candidate seam: the same relational comparison as
/// <c>VerifyCandidateAsync</c> against a supplied context, with no filesystem
/// access and an explicit media dimension owned by the caller.
/// </summary>
[Collection(PortableLibraryVerificationCollection.Name)]
public sealed class PortableLibraryVerifierDatabaseSeamTests(CandidateVerificationFixture fixture)
    : IClassFixture<CandidateVerificationFixture>
{
    private readonly CandidateVerificationFixture _fixture = fixture;

    [Fact]
    public async Task Database_only_verification_passes_and_covers_relational_kinds()
    {
        await using var candidate = await _fixture.CreateCandidateAsync();

        var report = await _fixture.Verifier.VerifyCandidateDatabaseAsync(
            candidate.Db,
            _fixture.Prepared,
            _fixture.Expected);

        report.Passed.Should().BeTrue(
            string.Join("; ", report.Failures.Select(failure => $"{failure.Code}:{failure.Entity}:{failure.Field}")));
        report.Failures.Should().BeEmpty();
        report.FailureCount.Should().Be(0);
        report.PortableRowsVerified.Should().BeGreaterThan(0);
        report.MediaFilesVerified.Should().Be(0);
        report.MediaBytesVerified.Should().Be(0);
        report.VerifiedKinds.Should().BeEquivalentTo(PortableLibraryVerifier.CandidateDatabaseVerifiedKinds);
        report.VerifiedKinds.Should().NotContain(nameof(PortableArchiveMediaEntry));
    }

    [Fact]
    public async Task Database_only_verification_never_reads_the_media_root()
    {
        await using var candidate = await _fixture.CreateCandidateAsync();
        var storageRoot = candidate.Storage.StorageRoot;
        Directory.Exists(storageRoot).Should().BeTrue();
        Directory.Delete(storageRoot, recursive: true);

        var report = await _fixture.Verifier.VerifyCandidateDatabaseAsync(
            candidate.Db,
            _fixture.Prepared,
            _fixture.Expected);

        report.Passed.Should().BeTrue(
            string.Join("; ", report.Failures.Select(failure => $"{failure.Code}:{failure.Entity}")));
        Directory.Exists(storageRoot).Should().BeFalse();
    }

    [Fact]
    public async Task Database_only_verification_fails_closed_on_an_unverified_expected_state()
    {
        await using var candidate = await _fixture.CreateCandidateAsync();
        var unverified = new PortablePreparedImportVerification(
            passed: false,
            failures: [],
            metadata: _fixture.Prepared.Metadata,
            stagedMedia: _fixture.Prepared.Media,
            descriptors: _fixture.Expected.Media,
            data: null);

        var act = () => _fixture.Verifier.VerifyCandidateDatabaseAsync(
            candidate.Db,
            _fixture.Prepared,
            unverified);

        var exception = await act.Should().ThrowAsync<PortableLibraryVerificationException>();
        exception.Which.Code.Should().Be(PortableLibraryVerificationErrorCodes.ExpectedStateInvalid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_candidate_row_is_reported_with_the_same_code_by_both_entry_points(bool databaseOnly)
    {
        await using var candidate = await _fixture.CreateCandidateAsync();
        var link = await candidate.Db.NoteTopics.SingleAsync();
        candidate.Db.NoteTopics.Remove(link);
        var topic = await candidate.Db.Topics.SingleAsync();
        candidate.Db.Topics.Remove(topic);
        await candidate.Db.SaveChangesAsync();

        var report = await VerifyAsync(candidate, databaseOnly);

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MissingEntity
            && failure.Entity == "topic");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Extra_candidate_row_is_reported_with_the_same_code_by_both_entry_points(bool databaseOnly)
    {
        await using var candidate = await _fixture.CreateCandidateAsync();
        candidate.Db.Topics.Add(new TopicModel { Topic = "unexpected-topic" });
        await candidate.Db.SaveChangesAsync();

        var report = await VerifyAsync(candidate, databaseOnly);

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.UnexpectedEntity
            && failure.Entity == "topic");
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.CountMismatch
            && failure.Field == nameof(MigrationArchiveCounts.Topics));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_candidate_column_is_reported_with_the_same_code_by_both_entry_points(bool databaseOnly)
    {
        await using var candidate = await _fixture.CreateCandidateAsync();
        var book = await candidate.Db.Books.SingleAsync(item => item.Id == _fixture.EpubBookId);
        book.Title = "Changed title";
        await candidate.Db.SaveChangesAsync();

        var report = await VerifyAsync(candidate, databaseOnly);

        ShouldFail(report, "book", nameof(BookModel.Title));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_candidate_relationship_is_reported_with_the_same_code_by_both_entry_points(bool databaseOnly)
    {
        await using var candidate = await _fixture.CreateCandidateAsync();
        var membership = await candidate.Db.BookCollections
            .SingleAsync(item => item.BookId == _fixture.EpubBookId
                && item.CollectionId == _fixture.CollectionId);
        candidate.Db.BookCollections.Remove(membership);
        await candidate.Db.SaveChangesAsync();

        var report = await VerifyAsync(candidate, databaseOnly);

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.RelationshipMismatch
            && failure.Entity == "bookCollection");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expected_state_from_another_prepared_import_is_rejected_by_both_entry_points(bool databaseOnly)
    {
        await using var candidate = await _fixture.CreateCandidateAsync();
        var store = new InMemoryPortableImportStagingStore();
        await using var otherStaging = new InMemoryPortableImportStaging(store);
        var otherPrepared = await SelfHostedActivationTestSupport.PrepareStagedAsync(
            _fixture.ArchiveBytes,
            otherStaging);

        var report = databaseOnly
            ? await _fixture.Verifier.VerifyCandidateDatabaseAsync(
                candidate.Db, otherPrepared, _fixture.Expected)
            : await _fixture.Verifier.VerifyCandidateAsync(
                candidate.Db, candidate.Storage.StorageRoot, otherPrepared, _fixture.Expected);

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.ExpectedStateMismatch);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Work_created_at_compares_at_utc_microsecond_precision(
        bool withinSameMicrosecond,
        bool databaseOnly)
    {
        var expectedCreatedAt = _fixture.Expected.Data!.Works
            .Single(work => work.Id == _fixture.WorkId)
            .CreatedAt;
        await using var candidate = await _fixture.CreateCandidateAsync();
        var work = await candidate.Db.Works.SingleAsync(item => item.Id == _fixture.WorkId);
        work.CreatedAt = withinSameMicrosecond
            ? PortableLibraryVerifier.Canonicalize(expectedCreatedAt).AddTicks(4)
            : expectedCreatedAt.AddTicks(10);
        await candidate.Db.SaveChangesAsync();

        var report = await VerifyAsync(candidate, databaseOnly);

        AssertTemporalOutcome(report, withinSameMicrosecond, "work", nameof(WorkModel.CreatedAt));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Nullable_progress_timestamp_compares_at_utc_microsecond_precision(
        bool withinSameMicrosecond,
        bool databaseOnly)
    {
        var expectedLastReadAt = _fixture.Expected.Data!.Books
            .Single(book => book.Id == _fixture.EpubBookId)
            .Progress.LastReadAt;
        expectedLastReadAt.Should().NotBeNull();
        await using var candidate = await _fixture.CreateCandidateAsync();
        var book = await candidate.Db.Books.SingleAsync(item => item.Id == _fixture.EpubBookId);
        book.Progress.LastReadAt = withinSameMicrosecond
            ? PortableLibraryVerifier.Canonicalize(expectedLastReadAt!.Value).AddTicks(4)
            : expectedLastReadAt!.Value.AddTicks(10);
        await candidate.Db.SaveChangesAsync();

        var report = await VerifyAsync(candidate, databaseOnly);

        AssertTemporalOutcome(report, withinSameMicrosecond, "book", nameof(ReadingProgress.LastReadAt));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Membership_added_at_compares_at_utc_microsecond_precision(
        bool withinSameMicrosecond,
        bool databaseOnly)
    {
        var expectedAddedAt = _fixture.Expected.Data!.BookCollections
            .Single(item => item.BookId == _fixture.EpubBookId
                && item.CollectionId == _fixture.CollectionId)
            .AddedAt;
        await using var candidate = await _fixture.CreateCandidateAsync();
        var membership = await candidate.Db.BookCollections
            .SingleAsync(item => item.BookId == _fixture.EpubBookId
                && item.CollectionId == _fixture.CollectionId);
        membership.AddedAt = withinSameMicrosecond
            ? PortableLibraryVerifier.Canonicalize(expectedAddedAt).AddTicks(4)
            : expectedAddedAt.AddTicks(10);
        await candidate.Db.SaveChangesAsync();

        var report = await VerifyAsync(candidate, databaseOnly);

        AssertTemporalOutcome(report, withinSameMicrosecond, "bookCollection", nameof(BookCollectionModel.AddedAt));
    }

    [Fact]
    public void Temporal_canonicalisation_covers_datetime_datetimeoffset_and_nullable_forms()
    {
        var baseUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var plusFourTicks = baseUtc.AddTicks(4);
        var plusTenTicks = baseUtc.AddTicks(10);

        PortableLibraryVerifier.CanonicalValuesEqual(plusFourTicks, plusFourTicks.AddTicks(5))
            .Should().BeTrue();
        PortableLibraryVerifier.CanonicalValuesEqual(plusFourTicks, plusTenTicks)
            .Should().BeFalse();
        PortableLibraryVerifier.CanonicalValuesEqual((DateTime?)plusFourTicks, (DateTime?)plusTenTicks.AddTicks(-1))
            .Should().BeTrue();
        PortableLibraryVerifier.CanonicalValuesEqual((DateTime?)plusFourTicks, (DateTime?)plusTenTicks)
            .Should().BeFalse();
        PortableLibraryVerifier.CanonicalValuesEqual((DateTime?)null, (DateTime?)null)
            .Should().BeTrue();
        PortableLibraryVerifier.CanonicalValuesEqual((DateTime?)plusFourTicks, (DateTime?)null)
            .Should().BeFalse();

        var utc = new DateTimeOffset(plusFourTicks, TimeSpan.Zero);
        var sameInstant = utc.ToOffset(TimeSpan.FromHours(2));
        PortableLibraryVerifier.CanonicalValuesEqual(utc, sameInstant).Should().BeTrue();
        PortableLibraryVerifier.CanonicalValuesEqual(utc, new DateTimeOffset(plusTenTicks, TimeSpan.Zero))
            .Should().BeFalse();
        PortableLibraryVerifier.CanonicalValuesEqual((DateTimeOffset?)utc, (DateTimeOffset?)sameInstant)
            .Should().BeTrue();
        PortableLibraryVerifier.CanonicalValuesEqual(
                (DateTimeOffset?)utc,
                (DateTimeOffset?)new DateTimeOffset(plusTenTicks, TimeSpan.Zero))
            .Should().BeFalse();
        PortableLibraryVerifier.CanonicalValuesEqual((DateTimeOffset?)null, (DateTimeOffset?)null)
            .Should().BeTrue();
        PortableLibraryVerifier.CanonicalValuesEqual((DateTimeOffset?)utc, (DateTimeOffset?)null)
            .Should().BeFalse();
    }

    private async Task<PortableLibraryVerificationReport> VerifyAsync(
        LocalPortableTestLibrary candidate,
        bool databaseOnly) =>
        databaseOnly
            ? await _fixture.Verifier.VerifyCandidateDatabaseAsync(
                candidate.Db,
                _fixture.Prepared,
                _fixture.Expected)
            : await _fixture.Verifier.VerifyCandidateAsync(
                candidate.Db,
                candidate.Storage.StorageRoot,
                _fixture.Prepared,
                _fixture.Expected);

    private static void AssertTemporalOutcome(
        PortableLibraryVerificationReport report,
        bool expectPass,
        string entity,
        string field)
    {
        if (expectPass)
        {
            report.Passed.Should().BeTrue(
                string.Join("; ", report.Failures.Select(failure => $"{failure.Code}:{failure.Entity}:{failure.Field}")));
            return;
        }

        ShouldFail(report, entity, field);
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
