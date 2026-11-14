using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Characterization (golden) coverage for the portable verifier (#1614, #1616).
/// </summary>
/// <remarks>
/// <para>
/// The verifier's report is an observable contract: which failure code, entity,
/// field and detail is produced, in which order, for each class of defect. Both
/// the export self-check and the legacy import decide whether a library may be
/// activated from it, so the report must not drift while the implementation is
/// split into comparison domains.
/// </para>
/// <para>
/// The seeded library is <see cref="PortableSnapshotGoldenLibrary"/>, already
/// used to pin the archive bytes (#1618); it is deterministic (fixed ids,
/// timestamps and media bytes), so the prepared-import report and the candidate
/// report are reproducible. Every report is rendered to a golden file under
/// <c>Golden/PortableVerification</c>. To intentionally accept a change, run
/// the test once with <c>NOSTOS_UPDATE_PORTABILITY_GOLDEN=1</c> and review the
/// diff; CI never sets it.
/// </para>
/// </remarks>
[Collection(PortableLibraryVerificationCollection.Name)]
public sealed class PortableLibraryVerifierGoldenTests
{
    private const string UpdateGoldenVariable = "NOSTOS_UPDATE_PORTABILITY_GOLDEN";

    private static readonly DateTimeOffset ExportTime =
        new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly PortableLibraryVerifier _verifier = new();

    [Fact]
    public async Task Clean_prepared_import_report_matches_the_golden_file()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        AssertGolden("prepared/clean.txt", RenderPrepared(verification));
    }

    [Fact]
    public async Task Prepared_data_hash_defect_report_matches_the_golden_file()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var data = staging.Store.Areas[prepared.StagingId.Value].Data!.State;
        CorruptFirstByte(data.Buffer);

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        AssertGolden("prepared/data-hash.txt", RenderPrepared(verification));
    }

    [Fact]
    public async Task Prepared_media_hash_defect_report_matches_the_golden_file()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        foreach (var item in staging.Store.Areas[prepared.StagingId.Value].MediaByIdentity.Values)
        {
            CorruptFirstByte(item.State.Buffer);
        }

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        AssertGolden("prepared/media-hash.txt", RenderPrepared(verification));
    }

    [Fact]
    public async Task Prepared_media_length_defect_report_matches_the_golden_file()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        foreach (var item in staging.Store.Areas[prepared.StagingId.Value].MediaByIdentity.Values)
        {
            var bytes = item.State.Buffer.ToArray();
            item.State.Buffer.SetLength(0);
            item.State.Buffer.Write(bytes[..^1]);
        }

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        AssertGolden("prepared/media-length.txt", RenderPrepared(verification));
    }

    [Fact]
    public async Task Prepared_missing_inventory_entry_report_matches_the_golden_file()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var area = staging.Store.Areas[prepared.StagingId.Value];
        var key = area.MediaByIdentity
            .Single(pair => pair.Value.Reference == prepared.Media[0].Reference.Value)
            .Key;
        area.MediaByIdentity.Remove(key);
        area.MediaByReference.Remove(prepared.Media[0].Reference.Value);

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        AssertGolden("prepared/missing-inventory-entry.txt", RenderPrepared(verification));
    }

    [Fact]
    public async Task Prepared_count_defect_report_matches_the_golden_file()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var fabricated = prepared with
        {
            Metadata = prepared.Metadata with
            {
                Counts = prepared.Metadata.Counts with
                {
                    Books = prepared.Metadata.Counts.Books + 1,
                },
            },
        };

        var verification = await _verifier.VerifyPreparedImportAsync(staging, fabricated);

        AssertGolden("prepared/count-mismatch.txt", RenderPrepared(verification));
    }

    [Fact]
    public async Task Prepared_manifest_count_defect_report_matches_the_golden_file()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var manifest = staging.Store.Areas[prepared.StagingId.Value].Manifest!.State;
        var node = JsonNode.Parse(Encoding.UTF8.GetString(manifest.Buffer.ToArray()))!.AsObject();
        node["counts"]!["works"] = 999;
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        manifest.Buffer.SetLength(0);
        manifest.Buffer.Write(bytes);

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        AssertGolden("prepared/manifest-count.txt", RenderPrepared(verification));
    }

    [Fact]
    public async Task Prepared_integrity_flag_defect_report_matches_the_golden_file()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var fabricated = prepared with
        {
            Metadata = prepared.Metadata with { IntegrityVerified = false },
        };

        var verification = await _verifier.VerifyPreparedImportAsync(staging, fabricated);

        AssertGolden("prepared/not-integrity-verified.txt", RenderPrepared(verification));
    }

    [Fact]
    public async Task Clean_candidate_report_matches_the_golden_file()
    {
        await using var fixture = await CandidateAsync();

        var report = await _verifier.VerifyCandidateAsync(
            fixture.Candidate.Db,
            fixture.Candidate.Storage.StorageRoot,
            fixture.Prepared,
            fixture.Expected);

        AssertGolden("candidate/clean.txt", RenderReport(report));
    }

    [Fact]
    public async Task Candidate_field_defect_report_matches_the_golden_file()
    {
        await using var fixture = await CandidateAsync();
        var book = await fixture.Candidate.Db.Books.SingleAsync(
            item => item.Id == fixture.EpubBookId);
        book.Title = "Changed title";
        await fixture.Candidate.Db.SaveChangesAsync();
        fixture.Candidate.Db.ChangeTracker.Clear();

        var report = await _verifier.VerifyCandidateAsync(
            fixture.Candidate.Db,
            fixture.Candidate.Storage.StorageRoot,
            fixture.Prepared,
            fixture.Expected);

        AssertGolden("candidate/field-mismatch.txt", RenderReport(report));
    }

    [Fact]
    public async Task Candidate_missing_entity_report_matches_the_golden_file()
    {
        await using var fixture = await CandidateAsync();
        var link = await fixture.Candidate.Db.NoteImportBookLinks.SingleAsync();
        fixture.Candidate.Db.NoteImportBookLinks.Remove(link);
        await fixture.Candidate.Db.SaveChangesAsync();
        fixture.Candidate.Db.ChangeTracker.Clear();

        var report = await _verifier.VerifyCandidateAsync(
            fixture.Candidate.Db,
            fixture.Candidate.Storage.StorageRoot,
            fixture.Prepared,
            fixture.Expected);

        AssertGolden("candidate/missing-entity.txt", RenderReport(report));
    }

    [Fact]
    public async Task Candidate_unexpected_entity_report_matches_the_golden_file()
    {
        await using var fixture = await CandidateAsync();
        fixture.Candidate.Db.Writings.Add(new WritingModel
        {
            Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            Name = "Unexpected draft",
            Type = WritingType.Document,
        });
        await fixture.Candidate.Db.SaveChangesAsync();
        fixture.Candidate.Db.ChangeTracker.Clear();

        var report = await _verifier.VerifyCandidateAsync(
            fixture.Candidate.Db,
            fixture.Candidate.Storage.StorageRoot,
            fixture.Prepared,
            fixture.Expected);

        AssertGolden("candidate/unexpected-entity.txt", RenderReport(report));
    }

    [Fact]
    public async Task Candidate_join_pair_defect_report_matches_the_golden_file()
    {
        await using var fixture = await CandidateAsync();
        var link = await fixture.Candidate.Db.NoteTopics.SingleAsync(
            row => row.NoteId == fixture.AudioNoteId);
        fixture.Candidate.Db.NoteTopics.Remove(link);
        fixture.Candidate.Db.NoteTopics.Add(new NoteTopicModel
        {
            NoteId = fixture.AudioNoteId,
            TopicId = fixture.FirstTopicId,
        });
        await fixture.Candidate.Db.SaveChangesAsync();
        fixture.Candidate.Db.ChangeTracker.Clear();

        var report = await _verifier.VerifyCandidateAsync(
            fixture.Candidate.Db,
            fixture.Candidate.Storage.StorageRoot,
            fixture.Prepared,
            fixture.Expected);

        AssertGolden("candidate/join-pair-mismatch.txt", RenderReport(report));
    }

    [Fact]
    public async Task Candidate_missing_media_report_matches_the_golden_file()
    {
        await using var fixture = await CandidateAsync();
        var cover = fixture.Expected.Media.First(
            item => item.BookId == fixture.EpubBookId
                && item.Kind == PortableArchiveFormat.CoverMediaKind);
        File.Delete(Path.Combine(
            fixture.Candidate.Storage.StorageRoot,
            cover.BookId.ToString(),
            cover.FileName));

        var report = await _verifier.VerifyCandidateAsync(
            fixture.Candidate.Db,
            fixture.Candidate.Storage.StorageRoot,
            fixture.Prepared,
            fixture.Expected);

        AssertGolden("candidate/missing-media.txt", RenderReport(report));
    }

    [Fact]
    public async Task Candidate_media_hash_defect_report_matches_the_golden_file()
    {
        await using var fixture = await CandidateAsync();
        var bookFile = fixture.Expected.Media.First(
            item => item.BookId == fixture.EpubBookId
                && item.Kind == PortableArchiveFormat.BookMediaKind);
        CorruptFile(Path.Combine(
            fixture.Candidate.Storage.StorageRoot,
            bookFile.BookId.ToString(),
            bookFile.FileName));

        var report = await _verifier.VerifyCandidateAsync(
            fixture.Candidate.Db,
            fixture.Candidate.Storage.StorageRoot,
            fixture.Prepared,
            fixture.Expected);

        AssertGolden("candidate/media-hash.txt", RenderReport(report));
    }

    [Fact]
    public async Task Candidate_unexpected_media_file_report_matches_the_golden_file()
    {
        await using var fixture = await CandidateAsync();
        var stray = Path.Combine(
            fixture.Candidate.Storage.StorageRoot,
            fixture.EpubBookId.ToString(),
            "stray.epub");
        await File.WriteAllBytesAsync(stray, [1, 2, 3]);

        var report = await _verifier.VerifyCandidateAsync(
            fixture.Candidate.Db,
            fixture.Candidate.Storage.StorageRoot,
            fixture.Prepared,
            fixture.Expected);

        AssertGolden("candidate/unexpected-media.txt", RenderReport(report));
    }

    private static void CorruptFirstByte(MemoryStream buffer)
    {
        var bytes = buffer.ToArray();
        bytes[0] ^= 0xff;
        buffer.SetLength(0);
        buffer.Write(bytes);
    }

    private static void CorruptFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xff;
        File.WriteAllBytes(path, bytes);
    }

    private async Task<(
        InMemoryPortableImportStaging Staging,
        PortablePreparedImport Prepared)> PrepareAsync()
    {
        var archive = await ExportGoldenArchiveAsync();
        var store = new InMemoryPortableImportStagingStore();
        var staging = new InMemoryPortableImportStaging(store);
        var prepared = await SelfHostedActivationTestSupport.PrepareStagedAsync(
            archive,
            staging);
        return (staging, prepared);
    }

    private async Task<CandidateFixture> CandidateAsync()
    {
        var archive = await ExportGoldenArchiveAsync();

        var store = new InMemoryPortableImportStagingStore();
        await using var staging = new InMemoryPortableImportStaging(store);
        var prepared = await SelfHostedActivationTestSupport.PrepareStagedAsync(
            archive,
            staging);
        var expected = await _verifier.VerifyPreparedImportAsync(staging, prepared);
        expected.Passed.Should().BeTrue(
            string.Join("; ", expected.Failures.Select(failure => failure.Code)));

        var candidate = await LocalPortableTestLibrary.CreateAsync();
        using var source = new MemoryStream(archive, writable: false);
        var imported = await candidate.Portability().ImportAsync(source);
        imported.IntegrityVerified.Should().BeTrue();

        return new CandidateFixture(
            candidate,
            prepared,
            expected,
            EpubBookId,
            AudioNoteId,
            FirstTopicId);
    }

    private static async Task<byte[]> ExportGoldenArchiveAsync()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableSnapshotGoldenLibrary.PopulateAsync(library);
        using var destination = new MemoryStream();
        await library.Portability(new ManualTimeProvider(ExportTime)).ExportAsync(destination);
        return destination.ToArray();
    }

    private static readonly Guid EpubBookId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");

    private static readonly Guid AudioNoteId =
        Guid.Parse("40000000-0000-0000-0000-000000000002");

    private static readonly Guid FirstTopicId =
        Guid.Parse("50000000-0000-0000-0000-000000000001");

    private sealed record CandidateFixture(
        LocalPortableTestLibrary Candidate,
        PortablePreparedImport Prepared,
        PortablePreparedImportVerification Expected,
        Guid EpubBookId,
        Guid AudioNoteId,
        Guid FirstTopicId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Candidate.DisposeAsync();
    }

    private static string RenderPrepared(PortablePreparedImportVerification verification)
    {
        var output = new StringBuilder();
        output.Append("passed=").Append(verification.Passed ? "true" : "false").Append('\n');
        output.Append("failures=").Append(verification.Failures.Count).Append('\n');
        output.Append("dataBytes=").Append(verification.Metadata.DataBytes).Append('\n');
        output.Append("dataSha256=").Append(verification.Metadata.DataSha256).Append('\n');
        output.Append("counts=").Append(Counts(verification.Metadata.Counts)).Append('\n');
        output.Append("media=").Append(verification.Descriptors.Count).Append('\n');
        output.Append("mediaBytes=")
            .Append(verification.Descriptors.Sum(item => item.Length))
            .Append('\n');
        foreach (var descriptor in verification.Descriptors
                     .OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            output.Append("descriptor|")
                .Append(descriptor.BookId.ToString("D")).Append('|')
                .Append(descriptor.Kind).Append('|')
                .Append(descriptor.Path).Append('|')
                .Append(descriptor.FileName).Append('|')
                .Append(descriptor.Length).Append('|')
                .Append(descriptor.Sha256).Append('\n');
        }

        output.Append("--\n");
        foreach (var failure in verification.Failures)
        {
            // The staging id is minted per prepared import; it identifies the
            // staging area, not portable state, so the report pins its role
            // rather than the random value.
            output.Append(Failure(failure)
                    .Replace(
                        verification.Metadata.StagingId.Value.ToString("D"),
                        "<staging>",
                        StringComparison.Ordinal))
                .Append('\n');
        }

        return output.ToString();
    }

    private static string RenderReport(PortableLibraryVerificationReport report)
    {
        var output = new StringBuilder();
        output.Append("passed=").Append(report.Passed ? "true" : "false").Append('\n');
        output.Append("failureCount=").Append(report.FailureCount).Append('\n');
        output.Append("kinds=").Append(string.Join(",", report.VerifiedKinds)).Append('\n');
        output.Append("rows=").Append(report.PortableRowsVerified).Append('\n');
        output.Append("mediaFiles=").Append(report.MediaFilesVerified).Append('\n');
        output.Append("mediaBytes=").Append(report.MediaBytesVerified).Append('\n');
        output.Append("--\n");
        foreach (var failure in report.Failures)
        {
            output.Append(Failure(failure)).Append('\n');
        }

        return output.ToString();
    }

    private static string Failure(PortableLibraryVerificationFailure failure) =>
        $"{failure.Code}|{failure.Entity}|{failure.EntityId}|{failure.Field}|{failure.Detail}";

    private static string Counts(MigrationArchiveCounts counts) =>
        $"works={counts.Works} books={counts.Books} collections={counts.Collections} " +
        $"memberships={counts.CollectionMemberships} notes={counts.Notes} topics={counts.Topics} " +
        $"noteTopics={counts.NoteTopics} writings={counts.Writings} writingNotes={counts.WritingNotes} " +
        $"acquisitions={counts.Acquisitions} assistantSettings={counts.AssistantSettings} " +
        $"importLinks={counts.NoteImportBookLinks} readingList={counts.ReadingListEntries} " +
        $"media={counts.MediaEntries}";

    private static void AssertGolden(string relativePath, string actual)
    {
        var path = Path.Combine(
            GoldenRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (Environment.GetEnvironmentVariable(UpdateGoldenVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"the golden file {path} must be committed; regenerate it once with " +
            $"{UpdateGoldenVariable}=1 and review the diff");

        var expected = File.ReadAllText(path, Encoding.UTF8);
        actual.Should().Be(
            expected,
            $"the verifier report pinned in {relativePath} is an activation contract and must not drift");
    }

    private static string GoldenRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null
                && !File.Exists(Path.Combine(directory.FullName, "Nostos.sln")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
                throw new InvalidOperationException("Could not locate the repository root.");

            return Path.Combine(
                directory.FullName,
                "Nostos.Backend.Tests",
                "Portability",
                "Golden",
                "PortableVerification");
        }
    }
}
