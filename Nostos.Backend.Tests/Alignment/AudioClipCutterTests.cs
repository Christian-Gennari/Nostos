using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Product.Alignment;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class AudioClipCutterTests
{
    [Fact]
    public async Task Multi_track_cut_without_track_storage_throws_media_missing()
    {
        var cutter = new FfmpegAudioClipCutter(new StubAssetStorage());
        var book = MultiTrackBook();

        var act = () => cutter.CutAsync(book, 30, 10);

        var ex = await act.Should().ThrowAsync<AudioClipException>();
        ex.Which.Code.Should().Be(AudioClipErrorCodes.MediaMissing);
        ex.Which.Message.Should().Contain("Multi-track storage");
    }

    [Fact]
    public async Task Multi_track_cut_uses_optional_track_storage_and_logger_parameters()
    {
        var tracks = new StubTrackStorage();
        var cutter = new FfmpegAudioClipCutter(
            new StubAssetStorage(),
            tracks,
            NullLogger<FfmpegAudioClipCutter>.Instance);
        var book = MultiTrackBook();

        var act = () => cutter.CutAsync(book, 30, 10);

        var ex = await act.Should().ThrowAsync<AudioClipException>();
        ex.Which.Code.Should().Be(AudioClipErrorCodes.MediaMissing);
        ex.Which.Message.Should().Contain("track is unavailable");
        tracks.RequestedBookId.Should().Be(book.Id);
        tracks.RequestedTrackNumber.Should().Be(1);
    }

    [Fact]
    public void Di_resolves_FfmpegAudioClipCutter_with_optional_dependencies_absent_or_present()
    {
        var withoutTracks = new ServiceCollection();
        withoutTracks.AddSingleton<IBookAssetStorage>(new StubAssetStorage());
        withoutTracks.AddScoped<IAudioClipCutter, FfmpegAudioClipCutter>();

        using (var provider = withoutTracks.BuildServiceProvider())
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IAudioClipCutter>()
                .Should().BeOfType<FfmpegAudioClipCutter>();
        }

        var withTracks = new ServiceCollection();
        withTracks.AddLogging();
        withTracks.AddSingleton<IBookAssetStorage>(new StubAssetStorage());
        withTracks.AddSingleton<IBookTrackStorage>(new StubTrackStorage());
        withTracks.AddScoped<IAudioClipCutter, FfmpegAudioClipCutter>();

        using (var provider = withTracks.BuildServiceProvider())
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IAudioClipCutter>()
                .Should().BeOfType<FfmpegAudioClipCutter>();
        }
    }

    private static AudioBookModel MultiTrackBook()
    {
        var book = new AudioBookModel
        {
            Id = Guid.NewGuid(),
            Title = "Multi-track test",
            Duration = "00:05:00",
        };
        book.FileDetails.HasFile = true;
        book.FileDetails.TracksJson = BookTrackList.Serialize(
        [
            new BookTrack(1, "track-0001.mp3", "audio/mpeg", 300_000, 1_000_000, 0, "Part 1"),
        ]);
        return book;
    }

    private sealed class StubAssetStorage : IBookAssetStorage
    {
        public Task<string> SaveBookFileAsync(Guid bookId, Stream content, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string> AdoptBookFileAsync(Guid bookId, string sourcePath, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<StoredAssetInfo?> GetBookFileInfoAsync(Guid bookId, CancellationToken ct = default) =>
            Task.FromResult<StoredAssetInfo?>(null);
        public Task<StoredAssetRead?> OpenBookFileAsync(Guid bookId, StorageByteRange? range = null, CancellationToken ct = default) =>
            Task.FromResult<StoredAssetRead?>(null);
        public Task<bool> DeleteBookFileAsync(Guid bookId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task DeleteBookFilesAsync(Guid bookId, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<string> SaveBookCoverAsync(Guid bookId, Stream content, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<StoredAssetInfo?> GetBookCoverInfoAsync(Guid bookId, CancellationToken ct = default) =>
            Task.FromResult<StoredAssetInfo?>(null);
        public Task<StoredAssetRead?> OpenBookCoverAsync(Guid bookId, CancellationToken ct = default) =>
            Task.FromResult<StoredAssetRead?>(null);
        public Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(Guid bookId, int width, CancellationToken ct = default) =>
            Task.FromResult<StoredAssetInfo?>(null);
        public Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(Guid bookId, int width, CancellationToken ct = default) =>
            Task.FromResult<StoredAssetRead?>(null);
        public Task<bool> DeleteCoverAsync(Guid bookId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class StubTrackStorage : IBookTrackStorage
    {
        public Guid? RequestedBookId { get; private set; }
        public int? RequestedTrackNumber { get; private set; }

        public Task<string> SaveTrackAsync(Guid bookId, int number, Stream content, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string> AdoptTrackAsync(Guid bookId, int number, string sourcePath, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<StoredAssetInfo?> GetTrackInfoAsync(Guid bookId, int number, CancellationToken ct = default)
        {
            RequestedBookId = bookId;
            RequestedTrackNumber = number;
            return Task.FromResult<StoredAssetInfo?>(null);
        }
        public Task<StoredAssetRead?> OpenTrackAsync(Guid bookId, int number, StorageByteRange? range = null, CancellationToken ct = default) =>
            Task.FromResult<StoredAssetRead?>(null);
        public Task DeleteTracksAsync(Guid bookId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
