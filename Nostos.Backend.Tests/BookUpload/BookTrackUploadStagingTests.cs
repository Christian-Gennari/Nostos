using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Providers.Acquisition.Media;
using Nostos.Backend.Services;
using Nostos.Backend.Services.BookUpload;
using Nostos.Backend.Tests.ExternalMedia;
using Nostos.Backend.Tests.Portability;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Storage;
using Xunit;

namespace Nostos.Backend.Tests.BookUpload;

/// <summary>
/// Several uploaded audio files are staged before any track is adopted
/// (issue #1373). The staging directory follows the product scratch seam
/// (issue #1596), so an audiobook is never assembled in a RAM-backed /tmp.
/// </summary>
public sealed class BookTrackUploadStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nostos-track-upload-scratch-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public async Task Tracks_are_staged_on_the_host_scratch_root_and_the_directory_is_removed()
    {
        var scratch = new RecordingProductScratchStorage(Path.Combine(_root, "scratch"));
        var tracks = new RecordingTrackStorage();
        var upload = new BookTrackUpload(
            new RecordingBookRepository(),
            new NullBookAssetStorage(),
            new AtlAudioDurationProbe(),
            tracks,
            scratch);

        var result = await upload.CommitAsync(
            new AudioBookModel { Title = "Read aloud" },
            [
                File("01 - Start.mp3", SyntheticMedia.Mp3(100)),
                File("02 - End.mp3", SyntheticMedia.Mp3(200)),
            ],
            CancellationToken.None);

        result.Ok.Should().BeTrue(result.Error);
        result.Tracks.Should().Be(2);

        // The one staging directory came from the seam, and the files put in it
        // were adopted by 1-based number.
        scratch.Directories.Should().ContainSingle()
            .Which.Should().StartWith(scratch.Root);
        Path.GetFileName(scratch.Directories[0]).Should().StartWith("nostos-tracks-");
        tracks.Adopted.Select(track => track.Number).Should().Equal(1, 2);
        tracks.Adopted[0].SourcePath.Should().StartWith(scratch.Directories[0]);
        tracks.Adopted[0].SourcePath.Should().EndWith(".mp3");

        scratch.Leftovers().Should().BeEmpty("the staging directory is deleted after the upload");
    }

    [Fact]
    public async Task A_file_that_is_not_audio_still_deletes_the_staging_directory()
    {
        var scratch = new RecordingProductScratchStorage(Path.Combine(_root, "scratch"));
        var tracks = new RecordingTrackStorage();
        var upload = new BookTrackUpload(
            new RecordingBookRepository(),
            new NullBookAssetStorage(),
            new AtlAudioDurationProbe(),
            tracks,
            scratch);

        var result = await upload.CommitAsync(
            new AudioBookModel { Title = "Read aloud" },
            [
                File("01 - Start.mp3", SyntheticMedia.Mp3(100)),
                File("02 - End.mp3", "not audio"u8.ToArray()),
            ],
            CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("File 2 of 2 is not playable audio.");
        tracks.Adopted.Should().BeEmpty();
        scratch.Directories.Should().ContainSingle();
        scratch.Leftovers().Should().BeEmpty("a rejected upload still removes its staging directory");
    }

    private static IFormFile File(string name, byte[] bytes) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, name, name);

    private sealed class RecordingBookRepository : IBookRepository
    {
        public List<BookModel> Updated { get; } = [];

        public Task<BookModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<BookModel?>(null);

        public Task<IReadOnlyDictionary<Guid, int>> CountEditionsByWorkAsync(
            IReadOnlyCollection<Guid> workIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>());

        public Task<int> CountBooksWithFilesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<List<BookModel>> GetBooksWithFilesPageAsync(
            int skip,
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<BookModel>());

        public Task UpdateAsync(BookModel book, CancellationToken cancellationToken = default)
        {
            Updated.Add(book);
            return Task.CompletedTask;
        }
    }

    /// <summary>An asset storage that owns no file: only the replaced file is touched.</summary>
    private sealed class NullBookAssetStorage : DelegatingBookAssetStorage
    {
        public override Task<bool> DeleteBookFileAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class RecordingTrackStorage : IBookTrackStorage
    {
        public List<(Guid BookId, int Number, string SourcePath, string FileName)> Adopted { get; } = [];

        public Task<string> SaveTrackAsync(
            Guid bookId,
            int number,
            Stream content,
            string fileName,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Uploads adopt a staged file.");

        public Task<string> AdoptTrackAsync(
            Guid bookId,
            int number,
            string sourcePath,
            string fileName,
            CancellationToken ct = default)
        {
            Adopted.Add((bookId, number, sourcePath, fileName));
            return Task.FromResult(fileName);
        }

        public Task<StoredAssetInfo?> GetTrackInfoAsync(
            Guid bookId,
            int number,
            CancellationToken ct = default) =>
            Task.FromResult<StoredAssetInfo?>(null);

        public Task<StoredAssetRead?> OpenTrackAsync(
            Guid bookId,
            int number,
            StorageByteRange? range = null,
            CancellationToken ct = default) =>
            Task.FromResult<StoredAssetRead?>(null);

        public Task DeleteTracksAsync(Guid bookId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
