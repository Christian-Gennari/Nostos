using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 6 rework: a manifest digest must describe the bytes actually retained.
/// A file is re-hashed under the exclusive lease whenever its metadata cannot
/// prove the capture hash, and only a file last written strictly before the
/// timestamp-granularity safety window keeps its capture hash.
/// </summary>
public sealed class RecoveryMediaEvidenceTests
{
    private static readonly Guid FirstBook = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondBook = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static string BookPath(RecoveryTestBed bed, Guid bookId, string fileName) =>
        Path.Combine(bed.Paths.LiveMedia, bookId.ToString("N"), fileName);

    private static void SetAllMediaTimes(RecoveryTestBed bed, DateTime utc)
    {
        foreach (var file in Directory.EnumerateFiles(bed.Paths.LiveMedia, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, utc);
    }

    private static int CountMediaHashes(RecoveryTestBed bed, SelfHostedMigrationRecoveryService service)
    {
        var hashes = 0;
        service.HashingForTesting = path =>
        {
            if (path.StartsWith(bed.Paths.LiveMedia, StringComparison.Ordinal)) hashes++;
        };
        return hashes;
    }

    private static RecoveryMediaDescriptor Descriptor(SelfHostedRecoveryManifest manifest, Guid bookId, string kind) =>
        manifest.Media.Single(descriptor => descriptor.BookId == bookId && descriptor.Kind == kind);

    [Fact]
    public async Task Same_length_rewrite_after_capture_with_a_newer_mtime_is_rehashed_under_the_lease()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        SetAllMediaTimes(bed, bed.Clock.UtcNow.AddDays(-1).UtcDateTime);
        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);
        var oldHash = capture.Media.Single(pin => pin.RelativePath == $"{FirstBook:N}/book.epub").Sha256;

        var path = BookPath(bed, FirstBook, "book.epub");
        var originalLength = new FileInfo(path).Length;
        await File.WriteAllTextAsync(path, new string('x', (int)originalLength));
        File.SetLastWriteTimeUtc(path, bed.Clock.UtcNow.UtcDateTime);

        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        var journal = await bed.AdvanceJournalAsync(bed.SeedJournal(),
            SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        var hashes = 0;
        service.HashingForTesting = _ => hashes++;

        var manifest = await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);

        var descriptor = Descriptor(manifest, FirstBook, "book");
        descriptor.Sha256.Should().NotBe(oldHash);
        descriptor.Sha256.Should().Be(RecoveryTestBed.Sha256Hex(await File.ReadAllBytesAsync(path)));
        manifest.MediaRehashedCount.Should().Be(1);
        hashes.Should().Be(2, "one media re-hash plus the database hash");
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Same_length_rewrite_with_an_mtime_inside_the_safety_window_is_rehashed()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        var insideWindow = bed.Clock.UtcNow.AddSeconds(-1);
        SetAllMediaTimes(bed, bed.Clock.UtcNow.AddDays(-1).UtcDateTime);
        var path = BookPath(bed, FirstBook, "book.epub");
        File.SetLastWriteTimeUtc(path, insideWindow.UtcDateTime);
        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);
        var pinned = capture.Media.Single(pin => pin.RelativePath == $"{FirstBook:N}/book.epub");
        pinned.LastWriteUtc.Should().Be(insideWindow.UtcDateTime, "the capture saw the in-window timestamp");

        // Same length, same in-window timestamp: only the safety window can catch it.
        var originalLength = new FileInfo(path).Length;
        await File.WriteAllTextAsync(path, new string('y', (int)originalLength));
        File.SetLastWriteTimeUtc(path, insideWindow.UtcDateTime);

        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        var journal = await bed.AdvanceJournalAsync(bed.SeedJournal(),
            SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        var hashes = 0;
        service.HashingForTesting = _ => hashes++;

        var manifest = await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);

        var descriptor = Descriptor(manifest, FirstBook, "book");
        descriptor.Sha256.Should().Be(RecoveryTestBed.Sha256Hex(await File.ReadAllBytesAsync(path)));
        manifest.MediaRehashedCount.Should().Be(1);
        hashes.Should().Be(2);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Derived_book_text_directories_are_retained_but_stay_out_of_the_manifest()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();

        // The book-text artifact store keeps regenerable caches under
        // <bookId>/derived/<source-sha>/<extractor-version>/.
        var derived = Path.Combine(bed.Paths.LiveMedia, FirstBook.ToString("N"), "derived",
            new string('a', 64), "extractor-v1");
        Directory.CreateDirectory(derived);
        await File.WriteAllTextAsync(Path.Combine(derived, "chunks.json.gz"), "derived-cache");
        var derivedBytes = new FileInfo(Path.Combine(derived, "chunks.json.gz")).Length;

        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);

        capture.Media.Should().HaveCount(5, "derived caches are not primary media pins");
        capture.Media.Should().NotContain(pin => pin.RelativePath.Contains("/derived/"));
        capture.MediaBytes.Should().Be(capture.Media.Sum(pin => pin.Bytes) + derivedBytes,
            "the retained copy's measured size still includes the derived bytes");

        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        var journal = await bed.AdvanceJournalAsync(bed.SeedJournal(),
            SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        var manifest = await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);

        manifest.Media.Should().HaveCount(5);
        manifest.MediaBytes.Should().Be(capture.MediaBytes);

        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
        manifest = await service.RetainMediaAsync(bed.JobId, lease, default);

        manifest.MediaRetained.Should().BeTrue();
        File.Exists(Path.Combine(bed.Paths.PreviousMedia(bed.JobId), FirstBook.ToString("N"), "derived",
            new string('a', 64), "extractor-v1", "chunks.json.gz")).Should().BeTrue(
            "the derived cache rides along with the media-root rename");
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Unknown_directories_in_a_book_folder_still_fail_closed()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        Directory.CreateDirectory(Path.Combine(bed.Paths.LiveMedia, FirstBook.ToString("N"), "mystery"));
        var service = bed.CreateService();

        Func<Task> capture = () =>
            service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);
        await capture.Should().ThrowAsync<MigrationActivationException>()
            .Where(exception => exception.Code == MigrationActivationErrorCodes.RecoveryFailed)
            .WithMessage("*unexpected directory*");
    }

    [Fact]
    public async Task Unchanged_files_older_than_the_window_keep_their_capture_hash_and_are_not_rehashed()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        SetAllMediaTimes(bed, bed.Clock.UtcNow.AddDays(-1).UtcDateTime);
        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);

        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        var journal = await bed.AdvanceJournalAsync(bed.SeedJournal(),
            SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        var hashes = 0;
        service.HashingForTesting = _ => hashes++;

        var manifest = await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);

        manifest.MediaRehashedCount.Should().Be(0);
        hashes.Should().Be(1, "only the database is hashed");
        manifest.Media.Should().BeEquivalentTo(capture.Media.Select(pin =>
            new RecoveryMediaDescriptor(pin.BookId, pin.Kind, pin.Extension, pin.Bytes, pin.Sha256)));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Added_and_removed_files_are_reflected_as_the_exact_retained_set()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        SetAllMediaTimes(bed, bed.Clock.UtcNow.AddDays(-1).UtcDateTime);
        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);

        File.Delete(BookPath(bed, SecondBook, "book.epub.partial"));
        var added = Path.Combine(bed.Paths.LiveMedia, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(added);
        var addedPath = Path.Combine(added, "book.epub");
        await File.WriteAllTextAsync(addedPath, "added-after-capture");
        File.SetLastWriteTimeUtc(addedPath, bed.Clock.UtcNow.AddDays(-1).UtcDateTime);

        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        var journal = await bed.AdvanceJournalAsync(bed.SeedJournal(),
            SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        var manifest = await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);

        manifest.Media.Should().HaveCount(capture.Media.Count, "one file was removed and one added");
        manifest.Media.Should().NotContain(descriptor => descriptor.Kind == "partial");
        var addedHash = RecoveryTestBed.Sha256Hex(await File.ReadAllBytesAsync(addedPath));
        manifest.Media.Should().Contain(descriptor =>
            descriptor.Extension == ".epub" && descriptor.Sha256 == addedHash);
        var expected = Directory.EnumerateFiles(bed.Paths.LiveMedia, "*", SearchOption.AllDirectories)
            .Select(file => new
            {
                BookId = Guid.ParseExact(Path.GetFileName(Path.GetDirectoryName(file))!, "N"),
                Bytes = new FileInfo(file).Length,
                Sha256 = RecoveryTestBed.Sha256Hex(File.ReadAllBytes(file)),
            })
            .OrderBy(item => item.BookId).ThenBy(item => item.Bytes).ThenBy(item => item.Sha256)
            .ToArray();
        manifest.Media
            .Select(descriptor => new { descriptor.BookId, descriptor.Bytes, descriptor.Sha256 })
            .OrderBy(item => item.BookId).ThenBy(item => item.Bytes).ThenBy(item => item.Sha256)
            .Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Every_manifest_hash_equals_the_hash_of_the_retained_bytes_after_the_rename()
    {
        using var bed = new RecoveryTestBed();
        await bed.SeedLiveLibraryAsync();
        SetAllMediaTimes(bed, bed.Clock.UtcNow.AddDays(-1).UtcDateTime);
        var service = bed.CreateService();
        var capture = await service.CaptureAsync(bed.JobId, bed.OperationId, bed.Revision, new(Books: 2), default);

        // Mutate two files between capture and the under-lease pass.
        var rewritten = BookPath(bed, FirstBook, "book.epub");
        var rewrittenBytes = await File.ReadAllBytesAsync(rewritten);
        await File.WriteAllBytesAsync(rewritten, rewrittenBytes.Reverse().ToArray());
        File.SetLastWriteTimeUtc(rewritten, bed.Clock.UtcNow.UtcDateTime);
        var added = Path.Combine(bed.Paths.LiveMedia, SecondBook.ToString("N"), "other.bin");
        await File.WriteAllTextAsync(added, "other");
        File.SetLastWriteTimeUtc(added, bed.Clock.UtcNow.UtcDateTime);

        var lease = await bed.Gate.EnterExclusiveAsync(LibraryMaintenanceReason.Activation);
        var journal = await bed.AdvanceJournalAsync(bed.SeedJournal(),
            SelfHostedActivationPhase.DatabaseCheckpointed, lease);
        var manifest = await service.PrepareRetentionAsync(bed.JobId, capture, lease, default);
        journal = await bed.AdvanceJournalAsync(journal, SelfHostedActivationPhase.CutoverPrepared, lease);
        manifest = await service.RetainMediaAsync(bed.JobId, lease, default);

        var retainedRoot = bed.Paths.PreviousMedia(bed.JobId);
        var retainedFiles = Directory.EnumerateFiles(retainedRoot, "*", SearchOption.AllDirectories)
            .Select(file => new
            {
                BookId = Guid.ParseExact(Path.GetFileName(Path.GetDirectoryName(file))!, "N"),
                Bytes = new FileInfo(file).Length,
                Sha256 = RecoveryTestBed.Sha256Hex(File.ReadAllBytes(file)),
            })
            .OrderBy(item => item.BookId).ThenBy(item => item.Bytes).ThenBy(item => item.Sha256)
            .ToArray();
        manifest.Media.Should().HaveCount(retainedFiles.Length);
        manifest.Media
            .Select(descriptor => new { descriptor.BookId, descriptor.Bytes, descriptor.Sha256 })
            .OrderBy(item => item.BookId).ThenBy(item => item.Bytes).ThenBy(item => item.Sha256)
            .Should().BeEquivalentTo(retainedFiles, options => options.WithStrictOrdering());
        await lease.DisposeAsync();
    }
}
