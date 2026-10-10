using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Providers;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// A source that delivers an audiobook as several files (issue #835). Each file
/// is stored unchanged as a track of ONE book; nothing is combined or
/// re-encoded.
/// </summary>
public sealed class MultiTrackAcquisitionTests
{
    private static byte[] PartBytes(Uri url) =>
        url.AbsolutePath switch
        {
            "/part1.mp3" => Enumerable.Repeat((byte)0x11, 1000).ToArray(),
            "/part2.mp3" => Enumerable.Repeat((byte)0x22, 2500).ToArray(),
            _ => Enumerable.Repeat((byte)0x33, 40).ToArray(),
        };

    private static TimeSpan? PartDuration(string path) =>
        Path.GetFileName(path) switch
        {
            "part-0000.mp3" => TimeSpan.FromSeconds(100),
            "part-0001.mp3" => TimeSpan.FromSeconds(250.5),
            _ => TimeSpan.FromSeconds(4),
        };

    private static (AcquisitionHarness Harness, FakeMultiTrackContentProvider Provider, AcquisitionService Service)
        Arrange(bool withTrackStorage = true, IBookTrackStorage? trackStorage = null)
    {
        var h = AcquisitionHarness.Create();
        h.Downloader.PayloadFor = PartBytes;
        h.DurationProbe.DurationFor = PartDuration;

        var provider = new FakeMultiTrackContentProvider("tracks", "Track Source")
        {
            PlanResult = FakeMultiTrackContentProvider.PlanOf(
                "tracks", "rec-1", "A Recorded Book", "Preface", "  Chapter One  ", null),
        };
        var service = h.CreateService(
            new ProviderRegistry(new[] { provider }),
            trackStorageOverride: trackStorage,
            withTrackStorage: withTrackStorage);
        return (h, provider, service);
    }

    [Fact]
    public async Task Every_part_becomes_a_stored_track_of_one_audiobook()
    {
        var (h, _, service) = Arrange();
        using var _harness = h;
        var stages = new List<AcquisitionProgress>();

        var result = await service.AcquireAsync(
            new AcquisitionRequest("tracks", "rec-1"),
            new SynchronousProgress(stages.Add),
            CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Acquired);

        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var book = await db.Books.Include(b => b.Acquisition).SingleAsync();
        book.Should().BeOfType<AudioBookModel>();
        book.Status.Should().Be(BookStatus.Ready);
        book.FileDetails.HasFile.Should().BeTrue();
        book.FileDetails.FileName.Should().BeNull("a multi-track book has no primary file");
        ((AudioBookModel)book).Duration.Should().Be("00:05:54", "100 s + 250.5 s + 4 s, measured rather than claimed");
        book.Acquisition!.ImportedExtension.Should().Be(".mp3");

        var tracks = BookTrackList.Parse(book.FileDetails.TracksJson);
        tracks.Should().HaveCount(3);
        tracks.Select(t => t.FileName).Should().Equal("track-0001.mp3", "track-0002.mp3", "track-0003.mp3");
        tracks.Select(t => t.Title).Should().Equal("Preface", "Chapter One", null);
        tracks.Select(t => t.DurationMs).Should().Equal(100_000, 250_500, 4_000);
        tracks.Select(t => t.Bytes).Should().Equal(1000, 2500, 40);
        tracks.Select(t => t.ContentType).Should().OnlyContain(type => type == "audio/mpeg");
        for (var i = 0; i < tracks.Count; i++)
        {
            var expected = PartBytes(new Uri($"https://example.com/part{i + 1}.mp3"));
            tracks[i].Crc32.Should().Be(Crc32.Compute(expected), "the checksum is of the bytes that were stored");

            await using var stored = await h.Storage.OpenTrackAsync(book.Id, i + 1);
            using var buffer = new MemoryStream();
            await stored!.Content.CopyToAsync(buffer);
            buffer.ToArray().Should().Equal(expected, "tracks are stored byte for byte, never re-encoded");
        }

        // One chapter per track, starting where the tracks before it end.
        var chapters = JsonSerializer.Deserialize<List<BookChapterDto>>(
            book.FileDetails.ChaptersJson!,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        chapters.Select(c => c.Title).Should().Equal("Preface", "Chapter One", "Section 3");
        chapters.Select(c => c.StartTime).Should().Equal(0, 100, 350.5);

        h.Storage.GetBookFileName(book.Id).Should().BeNull();
        Directory.GetDirectories(h.WorkingRootDir).Should().BeEmpty("staging is always cleaned up");

        result.Book!.Tracks.Should().HaveCount(3);
        result.Book.Tracks!.Select(t => t.Duration).Should().Equal(100, 250.5, 4);
        result.Book.HasFile.Should().BeTrue();

        stages.Select(s => s.Stage).Should().NotContain("assembling", "nothing is combined or transcoded any more");
        stages.Where(s => s.Stage == "importing").Select(s => s.Detail)
            .Should().Contain("3/3 files");
    }

    [Fact]
    public async Task The_imported_book_downloads_as_one_archive_of_its_tracks()
    {
        var (h, _, service) = Arrange();
        using var _harness = h;
        var result = await service.AcquireAsync(
            new AcquisitionRequest("tracks", "rec-1"), null, CancellationToken.None);

        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var packages = new AudiobookPackageService(new BookRepository(db), h.Storage, h.Storage);
        var package = await packages.BuildAsync(result.BookId!.Value);

        package.Should().NotBeNull();
        var info = package!.Info(".zip", "application/zip");
        info.FileName.Should().Be("A Recorded Book.zip");

        await using var read = package.Open(".zip", "application/zip", range: null);
        using var bytes = new MemoryStream();
        await read.Content.CopyToAsync(bytes);
        bytes.Length.Should().Be(info.Length);

        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        archive.Entries.Select(e => e.FullName).Should().Equal(
            "manifest.json", "01 - Preface.mp3", "02 - Chapter One.mp3", "03.mp3");

        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        var metadata = manifest.RootElement.GetProperty("metadata");
        metadata.GetProperty("conformsTo").GetString()
            .Should().Be("https://readium.org/webpub-manifest/profiles/audiobook");
        metadata.GetProperty("title").GetString().Should().Be("A Recorded Book");
        metadata.GetProperty("duration").GetDouble().Should().Be(354.5);
        metadata.GetProperty("identifier").GetString().Should().Be($"urn:uuid:{result.BookId}");

        var readingOrder = manifest.RootElement.GetProperty("readingOrder").EnumerateArray().ToList();
        readingOrder.Select(item => item.GetProperty("href").GetString()).Should().Equal(
            "01%20-%20Preface.mp3", "02%20-%20Chapter%20One.mp3", "03.mp3");
        readingOrder.Select(item => item.GetProperty("duration").GetDouble()).Should().Equal(100, 250.5, 4);
        readingOrder.Select(item => item.GetProperty("type").GetString())
            .Should().OnlyContain(type => type == "audio/mpeg");
        readingOrder[2].GetProperty("title").GetString().Should().Be("Track 3");
        manifest.RootElement.TryGetProperty("resources", out _).Should().BeFalse("this book has no cover");
    }

    [Fact]
    public async Task A_single_file_book_has_no_package()
    {
        using var h = AcquisitionHarness.Create();
        var provider = new FakeContentProvider("gutenberg", "Project Gutenberg")
        {
            PlanResult = new ProviderAcquisitionPlan(
                ProviderId: "gutenberg",
                ExternalId: "1",
                Asset: new ProviderAsset("epub", ProviderMediaKind.Ebook, "EPUB", "epub"),
                Metadata: new ProviderMetadata(Title: "One File"),
                Parts: new[] { new ProviderDownloadPart(new Uri("https://example.com/a.epub"), ".epub") },
                Output: new ProviderOutput(".epub", "application/epub+zip", "EPUB")),
        };
        var service = h.CreateService(new ProviderRegistry(new[] { provider }));
        var result = await service.AcquireAsync(new AcquisitionRequest("gutenberg", "1"), null, CancellationToken.None);
        result.Outcome.Should().Be(AcquisitionOutcome.Acquired);

        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var book = await db.Books.SingleAsync();
        book.FileDetails.TracksJson.Should().BeNull();
        book.FileDetails.FileName.Should().Be("book.epub");
        result.Book!.Tracks.Should().BeNull();

        var packages = new AudiobookPackageService(new BookRepository(db), h.Storage, h.Storage);
        (await packages.BuildAsync(book.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Several_parts_from_a_provider_that_never_declared_tracks_are_refused_before_anything_happens()
    {
        // Without this, only the first part would reach the library as "the book".
        using var h = AcquisitionHarness.Create();
        var provider = new FakeContentProvider("plain", "Plain Source")
        {
            PlanResult = FakeMultiTrackContentProvider.PlanOf("plain", "x", "Two Files", "a", "b"),
        };
        var service = h.CreateService(new ProviderRegistry(new[] { provider }));

        var result = await service.AcquireAsync(new AcquisitionRequest("plain", "x"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be(AcquisitionException.MultiTrackUnsupported);
        h.Downloader.DownloadCallCount.Should().Be(0);
        await using var db = await h.ContextFactory.CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(0, "no row is created for an import that cannot be stored");
    }

    [Fact]
    public async Task A_host_without_track_storage_refuses_a_multi_track_item_before_downloading()
    {
        var (h, _, service) = Arrange(withTrackStorage: false);
        using var _harness = h;

        var result = await service.AcquireAsync(new AcquisitionRequest("tracks", "rec-1"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be(AcquisitionException.MultiTrackUnsupported);
        result.Reply.Should().Contain("cannot store multi-track audiobooks");
        h.Downloader.DownloadCallCount.Should().Be(0);
        await using var db = await h.ContextFactory.CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_part_that_is_not_playable_audio_fails_the_import_and_stores_nothing()
    {
        var (h, _, service) = Arrange();
        using var _harness = h;
        // The source answered with something (an error page, say) that is not audio.
        h.DurationProbe.DurationFor = path =>
            Path.GetFileName(path) == "part-0001.mp3" ? null : TimeSpan.FromSeconds(10);

        var result = await service.AcquireAsync(new AcquisitionRequest("tracks", "rec-1"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be(AcquisitionException.TrackUnreadable);

        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var book = await db.Books.SingleAsync();
        book.Status.Should().Be(BookStatus.Failed, "a failed import stays visible so it can be retried");
        book.StatusMessage.Should().Be("File 2 of 3 from the source is not playable audio.");
        book.FileDetails.HasFile.Should().BeFalse();
        book.FileDetails.TracksJson.Should().BeNull();
        (await h.Storage.GetTrackInfoAsync(book.Id, 1)).Should().BeNull();
        Directory.GetDirectories(h.WorkingRootDir).Should().BeEmpty();
    }

    [Fact]
    public async Task A_storage_failure_part_way_through_leaves_no_orphaned_tracks()
    {
        var failing = new FailingTrackStorage(failOnNumber: 3);
        var (h, _, service) = Arrange(trackStorage: failing);
        using var _harness = h;
        failing.Inner = h.Storage;

        var result = await service.AcquireAsync(new AcquisitionRequest("tracks", "rec-1"), null, CancellationToken.None);

        result.Outcome.Should().Be(AcquisitionOutcome.Failed);
        result.ErrorCode.Should().Be("storage_failed");
        failing.StoredNumbers.Should().Equal(1, 2);

        await using var db = await h.ContextFactory.CreateDbContextAsync();
        var book = await db.Books.SingleAsync();
        book.Status.Should().Be(BookStatus.Failed);
        book.FileDetails.TracksJson.Should().BeNull();
        (await h.Storage.GetTrackInfoAsync(book.Id, 1)).Should().BeNull("the tracks stored before the failure are rolled back");
        (await h.Storage.GetTrackInfoAsync(book.Id, 2)).Should().BeNull();
    }

    [Fact]
    public async Task Importing_the_same_recording_twice_downloads_nothing_the_second_time()
    {
        var (h, _, service) = Arrange();
        using var _harness = h;
        (await service.AcquireAsync(new AcquisitionRequest("tracks", "rec-1"), null, CancellationToken.None))
            .Outcome.Should().Be(AcquisitionOutcome.Acquired);
        var downloads = h.Downloader.DownloadCallCount;

        var again = await service.AcquireAsync(new AcquisitionRequest("tracks", "rec-1"), null, CancellationToken.None);

        again.Outcome.Should().Be(AcquisitionOutcome.AlreadyAcquired);
        h.Downloader.DownloadCallCount.Should().Be(downloads);
    }

    private sealed class SynchronousProgress(Action<AcquisitionProgress> report) : IProgress<AcquisitionProgress>
    {
        public void Report(AcquisitionProgress value) => report(value);
    }

    private sealed class FailingTrackStorage(int failOnNumber) : IBookTrackStorage
    {
        public IBookTrackStorage Inner { get; set; } = null!;
        public List<int> StoredNumbers { get; } = new();

        public Task<string> SaveTrackAsync(Guid bookId, int number, Stream content, string fileName, CancellationToken ct = default) =>
            Inner.SaveTrackAsync(bookId, number, content, fileName, ct);

        public async Task<string> AdoptTrackAsync(Guid bookId, int number, string sourcePath, string fileName, CancellationToken ct = default)
        {
            if (number == failOnNumber)
                throw new IOException("disk full");

            var stored = await Inner.AdoptTrackAsync(bookId, number, sourcePath, fileName, ct);
            StoredNumbers.Add(number);
            return stored;
        }

        public Task<StoredAssetInfo?> GetTrackInfoAsync(Guid bookId, int number, CancellationToken ct = default) =>
            Inner.GetTrackInfoAsync(bookId, number, ct);

        public Task<StoredAssetRead?> OpenTrackAsync(Guid bookId, int number, StorageByteRange? range = null, CancellationToken ct = default) =>
            Inner.OpenTrackAsync(bookId, number, range, ct);

        public Task DeleteTracksAsync(Guid bookId, CancellationToken ct = default) =>
            Inner.DeleteTracksAsync(bookId, ct);
    }
}
