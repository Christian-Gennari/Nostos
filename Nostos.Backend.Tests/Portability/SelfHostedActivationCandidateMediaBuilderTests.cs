using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

[Collection(PortableLibraryVerificationCollection.Name)]
public sealed class SelfHostedActivationCandidateMediaBuilderTests
{
    [Fact]
    public async Task Build_materializes_byte_identical_media_readable_through_live_asset_storage()
    {
        using var files = new ActivationFiles();
        ResetCandidate(files);
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var builder = new SelfHostedActivationCandidateMediaBuilder(files.Paths, staging);
        var liveBefore = SelfHostedActivationTestSupport.SnapshotTree(files.Paths.LiveMedia);
        var databaseBefore = File.ReadAllBytes(files.Paths.LiveDatabase);
        var reports = new List<SelfHostedActivationCandidateMediaProgress>();

        var result = await builder.BuildMediaAsync(
            files.Id,
            prepared,
            new TestProgress<SelfHostedActivationCandidateMediaProgress>(reports.Add));

        reports.Should().NotBeEmpty();
        reports.Should().BeInAscendingOrder(report => report.ItemsCompleted);
        reports[^1].ItemsCompleted.Should().Be(result.FileCount);
        reports[^1].BytesCompleted.Should().Be(result.Bytes);
        reports[^1].TotalItems.Should().Be(result.FileCount);

        result.FileCount.Should().Be(prepared.Media.Count);
        result.Bytes.Should().Be(prepared.Media.Sum(item => item.Descriptor.Length));
        result.CopiedFileCount.Should().Be(prepared.Media.Count);
        result.CopiedBytes.Should().Be(result.Bytes);
        result.ReusedFileCount.Should().Be(0);
        result.Root.Should().Be(files.Paths.CandidateMedia(files.Id));

        var expected = await ReadStagedMediaAsync(staging, prepared);
        foreach (var (descriptor, content) in expected)
        {
            var path = Path.Combine(
                result.Root,
                descriptor.BookId.ToString(),
                descriptor.FileName);
            File.Exists(path).Should().BeTrue();
            File.ReadAllBytes(path).Should().Equal(content);
        }

        var storage = CreateStorage(result.Root);
        foreach (var (descriptor, content) in expected)
        {
            StoredAssetRead? opened = descriptor.Kind == "book"
                ? await storage.OpenBookFileAsync(descriptor.BookId)
                : await storage.OpenBookCoverAsync(descriptor.BookId);
            opened.Should().NotBeNull();
            await using (opened!)
            {
                (await SelfHostedActivationTestSupport.ReadAllAsync(opened.Content)).Should().Equal(content);
            }
        }

        SelfHostedActivationTestSupport.SnapshotTree(files.Paths.LiveMedia)
            .Should().BeEquivalentTo(liveBefore);
        File.ReadAllBytes(files.Paths.LiveDatabase).Should().Equal(databaseBefore);
    }

    [Fact]
    public async Task Build_rerun_reuses_hash_verified_files_without_recopying()
    {
        using var files = new ActivationFiles();
        ResetCandidate(files);
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var builder = new SelfHostedActivationCandidateMediaBuilder(files.Paths, staging);

        var first = await builder.BuildMediaAsync(files.Id, prepared);
        var snapshot = SelfHostedActivationTestSupport.SnapshotTree(first.Root);
        var second = await builder.BuildMediaAsync(files.Id, prepared);

        second.ReusedFileCount.Should().Be(prepared.Media.Count);
        second.ReusedBytes.Should().Be(first.Bytes);
        second.CopiedFileCount.Should().Be(0);
        second.CopiedBytes.Should().Be(0);
        SelfHostedActivationTestSupport.SnapshotTree(second.Root).Should().BeEquivalentTo(snapshot);
    }

    [Fact]
    public async Task Build_discards_partial_scratch_and_rebuilds_the_file()
    {
        using var files = new ActivationFiles();
        ResetCandidate(files);
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var builder = new SelfHostedActivationCandidateMediaBuilder(files.Paths, staging);

        var first = await builder.BuildMediaAsync(files.Id, prepared);
        var descriptor = prepared.Media[0].Descriptor;
        var target = Path.Combine(first.Root, descriptor.BookId.ToString(), descriptor.FileName);
        File.Delete(target);
        var partial = target + ".partial";
        File.WriteAllText(partial, "partial scratch that must be discarded");
        var expected = (await ReadStagedMediaAsync(staging, prepared))[descriptor];

        var second = await builder.BuildMediaAsync(files.Id, prepared);

        File.Exists(partial).Should().BeFalse();
        File.ReadAllBytes(target).Should().Equal(expected);
        second.CopiedFileCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Build_does_not_adopt_a_complete_but_unrenamed_partial()
    {
        using var files = new ActivationFiles();
        var (inner, prepared) = await PrepareAsync();
        await using var _ = inner;
        var first = await new SelfHostedActivationCandidateMediaBuilder(files.Paths, inner)
            .BuildMediaAsync(files.Id, prepared);
        var descriptor = prepared.Media[0].Descriptor;
        var item = prepared.Media[0];
        var target = Path.Combine(first.Root, descriptor.BookId.ToString(), descriptor.FileName);
        var content = (await ReadStagedMediaAsync(inner, prepared))[descriptor];
        File.Delete(target);
        File.WriteAllBytes(target + ".partial", content);
        var counting = new CountingPortableImportStaging(inner);

        var result = await new SelfHostedActivationCandidateMediaBuilder(files.Paths, counting)
            .BuildMediaAsync(files.Id, prepared);

        File.Exists(target + ".partial").Should().BeFalse();
        File.ReadAllBytes(target).Should().Equal(content);
        counting.OpenMediaReads.Should().Contain(item.Reference.Value);
        counting.OpenMediaReadCounts[item.Reference.Value].Should().Be(1);
        result.ReusedFileCount.Should().Be(prepared.Media.Count - 1);
    }

    [Fact]
    public async Task Build_replaces_a_final_file_that_no_longer_hashes()
    {
        using var files = new ActivationFiles();
        ResetCandidate(files);
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var builder = new SelfHostedActivationCandidateMediaBuilder(files.Paths, staging);

        var first = await builder.BuildMediaAsync(files.Id, prepared);
        var descriptor = prepared.Media[0].Descriptor;
        var target = Path.Combine(first.Root, descriptor.BookId.ToString(), descriptor.FileName);
        var bytes = File.ReadAllBytes(target);
        bytes[0] ^= 0xff;
        File.WriteAllBytes(target, bytes);
        var expected = (await ReadStagedMediaAsync(staging, prepared))[descriptor];

        var second = await builder.BuildMediaAsync(files.Id, prepared);

        File.ReadAllBytes(target).Should().Equal(expected);
        second.CopiedFileCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Build_same_length_staging_corruption_fails_typed_and_leaves_no_unverified_file()
    {
        using var files = new ActivationFiles();
        ResetCandidate(files);
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var store = StagingStoreOf(staging);
        TamperAllStagedMedia(store, prepared.StagingId, shorten: false);
        var builder = new SelfHostedActivationCandidateMediaBuilder(files.Paths, staging);

        var build = async () => await builder.BuildMediaAsync(files.Id, prepared);

        var exception = await build.Should().ThrowAsync<MigrationActivationException>();
        exception.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);
        AssertCandidateHasNoUnverifiedFiles(files.Paths.CandidateMedia(files.Id), prepared);
    }

    [Fact]
    public async Task Build_length_corruption_in_staging_fails_typed_and_leaves_no_partial()
    {
        using var files = new ActivationFiles();
        ResetCandidate(files);
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var store = StagingStoreOf(staging);
        TamperAllStagedMedia(store, prepared.StagingId, shorten: true);
        var builder = new SelfHostedActivationCandidateMediaBuilder(files.Paths, staging);

        var build = async () => await builder.BuildMediaAsync(files.Id, prepared);

        var exception = await build.Should().ThrowAsync<MigrationActivationException>();
        exception.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);
        AssertCandidateHasNoUnverifiedFiles(files.Paths.CandidateMedia(files.Id), prepared);
    }

    [Fact]
    public async Task Build_cancellation_is_cleanup_safe_and_resumable()
    {
        using var files = new ActivationFiles();
        var (inner, prepared) = await PrepareAsync();
        await using var _ = inner;
        using var cancellation = new CancellationTokenSource();
        var cancelAfterThird = 0;
        var staging = new CountingPortableImportStaging(inner)
        {
            OnMediaRead = _ =>
            {
                if (++cancelAfterThird >= 2)
                {
                    cancellation.Cancel();
                }
            },
        };
        var builder = new SelfHostedActivationCandidateMediaBuilder(files.Paths, staging);

        var build = async () => await builder.BuildMediaAsync(files.Id, prepared, ct: cancellation.Token);

        await build.Should().ThrowAsync<OperationCanceledException>();
        var root = files.Paths.CandidateMedia(files.Id);
        if (Directory.Exists(root))
        {
            Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories)
                .Should().BeEmpty();
        }

        var resumed = await builder.BuildMediaAsync(files.Id, prepared);
        resumed.FileCount.Should().Be(prepared.Media.Count);
        resumed.Bytes.Should().Be(prepared.Media.Sum(item => item.Descriptor.Length));
    }

    [Fact]
    public async Task Build_rejects_hostile_descriptors_without_writing_anything()
    {
        using var files = new ActivationFiles();
        ResetCandidate(files);
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var builder = new SelfHostedActivationCandidateMediaBuilder(files.Paths, staging);
        var original = prepared.Media[0];

        var cases = new[]
        {
            new PortableArchiveMediaEntry(Guid.NewGuid(), "book", "media/books/x/book.epub", "../escape.epub", "application/epub+zip", 1, "aa"),
            new PortableArchiveMediaEntry(Guid.NewGuid(), "book", "media/books/x/book.exe", "book.exe", "application/octet-stream", 1, "aa"),
            new PortableArchiveMediaEntry(Guid.NewGuid(), "book", "media/books/x/book.epub", "book.epub/..", "application/epub+zip", 1, "aa"),
            new PortableArchiveMediaEntry(Guid.NewGuid(), "video", "media/books/x/video.mp4", "video.mp4", "video/mp4", 1, "aa"),
            new PortableArchiveMediaEntry(Guid.NewGuid(), "book", "media/books/other.epub", "book.epub", "application/epub+zip", 1, "aa"),
            new PortableArchiveMediaEntry(Guid.Empty, "book", "media/books/x/book.epub", "book.epub", "application/epub+zip", 1, "aa"),
        };

        foreach (var descriptor in cases)
        {
            var fabricated = Fabricate(prepared, [new PortablePreparedMedia(descriptor, original.Reference)]);
            var build = async () => await builder.BuildMediaAsync(files.Id, fabricated);

            var exception = await build.Should().ThrowAsync<MigrationActivationException>();
            exception.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);
        }

        var duplicate = Fabricate(
            prepared,
            [original, new PortablePreparedMedia(original.Descriptor, original.Reference)]);
        var duplicateBuild = async () => await builder.BuildMediaAsync(files.Id, duplicate);
        var duplicateException = await duplicateBuild.Should().ThrowAsync<MigrationActivationException>();
        duplicateException.Which.Code.Should().Be(MigrationActivationErrorCodes.Failed);

        var root = files.Paths.CandidateMedia(files.Id);
        if (Directory.Exists(root))
        {
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Build_refuses_a_cross_volume_candidate_before_writing_anything()
    {
        using var files = new ActivationFiles();
        ResetCandidate(files);
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var paths = new SelfHostedActivationPaths(
            files.Paths.LiveDatabase,
            files.Paths.LiveMedia,
            new RefusingVolume());
        var builder = new SelfHostedActivationCandidateMediaBuilder(paths, staging);

        var build = async () => await builder.BuildMediaAsync(files.Id, prepared);

        var exception = await build.Should().ThrowAsync<MigrationActivationException>();
        exception.Which.Code.Should().Be("migration_activation_cross_volume");
        Directory.Exists(paths.CandidateMedia(files.Id)).Should().BeFalse();
    }

    private sealed class RefusingVolume : IActivationVolume
    {
        public bool SameVolume(string first, string second) => false;
    }

    private static void ResetCandidate(ActivationFiles files)
    {
        var candidate = files.Paths.CandidateMedia(files.Id);
        if (Directory.Exists(candidate))
        {
            Directory.Delete(candidate, true);
        }
    }

    private static void AssertCandidateHasNoUnverifiedFiles(
        string root,
        PortablePreparedImport prepared)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Should().BeEmpty();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            var segments = relative.Split(Path.DirectorySeparatorChar);
            segments.Should().HaveCount(2);
            var bookId = Guid.Parse(segments[0]);
            var descriptor = prepared.Media
                .Select(item => item.Descriptor)
                .Where(item => item.BookId == bookId && item.FileName == segments[1])
                .Should().ContainSingle().Which;
            var bytes = File.ReadAllBytes(file);
            bytes.LongLength.Should().Be(descriptor.Length);
            SelfHostedActivationTestSupport.Sha256Hex(bytes).Should().Be(descriptor.Sha256);
        }
    }

    internal static InMemoryPortableImportStagingStore StagingStoreOf(InMemoryPortableImportStaging staging) =>
        staging.Store;

    internal static void TamperAllStagedMedia(
        InMemoryPortableImportStagingStore store,
        PortableStagingId stagingId,
        bool shorten)
    {
        foreach (var item in store.Areas[stagingId.Value].MediaByIdentity.Values)
        {
            var bytes = item.State.Buffer.ToArray();
            if (shorten)
            {
                bytes = bytes[..^1];
            }
            else
            {
                bytes[0] ^= 0xff;
            }

            item.State.Buffer.SetLength(0);
            item.State.Buffer.Write(bytes);
        }
    }

    internal static PortablePreparedImport Fabricate(
        PortablePreparedImport prepared,
        IReadOnlyList<PortablePreparedMedia> media)
    {
        var metadata = prepared.Metadata with
        {
            MediaFiles = media.Count,
            MediaBytes = media.Sum(item => item.Descriptor.Length),
            Counts = prepared.Metadata.Counts with { MediaEntries = media.Count },
        };
        return new PortablePreparedImport(metadata, media);
    }

    internal static async Task<(InMemoryPortableImportStaging Staging, PortablePreparedImport Prepared)> PrepareAsync()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(source.Db, source.Storage);
        var archive = await SelfHostedActivationTestSupport.ExportArchiveAsync(source);
        var store = new InMemoryPortableImportStagingStore();
        var staging = new InMemoryPortableImportStaging(store);
        var prepared = await SelfHostedActivationTestSupport.PrepareStagedAsync(archive, staging);
        return (staging, prepared);
    }

    internal static async Task<Dictionary<PortableArchiveMediaEntry, byte[]>> ReadStagedMediaAsync(
        InMemoryPortableImportStaging staging,
        PortablePreparedImport prepared)
    {
        var result = new Dictionary<PortableArchiveMediaEntry, byte[]>();
        foreach (var item in prepared.Media)
        {
            await using var stream = await staging.OpenMediaReadAsync(
                prepared.StagingId,
                item.Reference);
            result[item.Descriptor] = await SelfHostedActivationTestSupport.ReadAllAsync(stream);
        }

        return result;
    }

    internal static FileStorageService CreateStorage(string booksRoot)
    {
        var env = new PortableTestWebHostEnvironment(Path.GetDirectoryName(booksRoot)!);
        return new FileStorageService(
            env,
            Options.Create(new FileStorageOptions { BooksRoot = booksRoot }),
            NullLogger<FileStorageService>.Instance);
    }
}

internal sealed class TestProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class CountingPortableImportStaging(IPortableImportStaging inner) : IPortableImportStaging
{
    internal List<string> OpenMediaReads { get; } = [];

    internal Dictionary<string, int> OpenMediaReadCounts { get; } = new(StringComparer.Ordinal);

    internal Action<PortableStagedMediaReference>? OnMediaRead { get; init; }

    public Task<PortableStagingId> CreateAsync(CancellationToken cancellationToken = default) =>
        inner.CreateAsync(cancellationToken);

    public Task<PortableStagingWrite> OpenMediaWriteAsync(
        PortableStagingId stagingId,
        PortableArchiveMediaEntry descriptor,
        CancellationToken cancellationToken = default) =>
        inner.OpenMediaWriteAsync(stagingId, descriptor, cancellationToken);

    public Task CompleteMediaAsync(
        PortableStagingId stagingId,
        PortableStagingWrite write,
        CancellationToken cancellationToken = default) =>
        inner.CompleteMediaAsync(stagingId, write, cancellationToken);

    public Task<Stream> OpenMediaReadAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        CancellationToken cancellationToken = default)
    {
        OnMediaRead?.Invoke(reference);
        OpenMediaReads.Add(reference.Value);
        OpenMediaReadCounts[reference.Value] =
            OpenMediaReadCounts.GetValueOrDefault(reference.Value) + 1;
        return inner.OpenMediaReadAsync(stagingId, reference, cancellationToken);
    }

    public Task<IReadOnlyList<PortablePreparedMedia>> ListMediaAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        inner.ListMediaAsync(stagingId, cancellationToken);

    public Task<PortableStagingPayloadWrite> OpenDataWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default) =>
        inner.OpenDataWriteAsync(stagingId, descriptor, cancellationToken);

    public Task CompleteDataAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        CancellationToken cancellationToken = default) =>
        inner.CompleteDataAsync(stagingId, write, cancellationToken);

    public Task<Stream> OpenDataReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        inner.OpenDataReadAsync(stagingId, cancellationToken);

    public Task<PortableStagingPayloadWrite> OpenManifestWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default) =>
        inner.OpenManifestWriteAsync(stagingId, descriptor, cancellationToken);

    public Task CompleteManifestAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        CancellationToken cancellationToken = default) =>
        inner.CompleteManifestAsync(stagingId, write, cancellationToken);

    public Task<Stream> OpenManifestReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        inner.OpenManifestReadAsync(stagingId, cancellationToken);

    public Task CommitPreparedImportAsync(
        PortableStagingId stagingId,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken = default) =>
        inner.CommitPreparedImportAsync(stagingId, metadata, cancellationToken);

    public Task<IPreparedPortableImport> RebuildPreparedImportAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        inner.RebuildPreparedImportAsync(stagingId, cancellationToken);

    public Task DeleteAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(stagingId, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
