using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Nostos.Backend.Tests.Services;

public sealed class FileStorageServiceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly FileStorageService _sut;

    public FileStorageServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "nostos-test-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        var env = new FakeWebHostEnvironment { ContentRootPath = _tempDirectory };
        var options = Options.Create(new FileStorageOptions { BooksRoot = _tempDirectory });
        _sut = new FileStorageService(env, options, NullLogger<FileStorageService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp dir cleanup
        }
    }

    [Fact]
    public void Constructor_InTestingWithoutExplicitBooksRoot_RefusesDefaultStorage()
    {
        var contentRoot = Path.Combine(
            Path.GetTempPath(),
            "nostos-test-storage-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        try
        {
            var env = new FakeWebHostEnvironment { ContentRootPath = contentRoot };
            var defaultRoot = FileStorageOptions.ResolveBooksRoot(
                contentRoot,
                new FileStorageOptions());

            var act = () => new FileStorageService(
                env,
                Options.Create(new FileStorageOptions()),
                NullLogger<FileStorageService>.Instance);

            act.Should()
                .Throw<InvalidOperationException>()
                .WithMessage("*Storage:BooksRoot*disposable directory*");
            Directory.Exists(defaultRoot).Should().BeFalse(
                "a Testing host must fail before the production/default storage tree is created");
        }
        finally
        {
            if (Directory.Exists(contentRoot))
                Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SaveBookFileAsync_FromStream_WritesWholePayloadToStorageRootBookExt_AndReturnsPath()
    {
        var bookId = Guid.NewGuid();
        var payload = new byte[] { 1, 2, 3, 4, 5, 42, 99 };
        using var stream = new MemoryStream(payload);

        var savedPath = await _sut.SaveBookFileAsync(bookId, stream, "sample.epub");

        var expectedPath = Path.Combine(_tempDirectory, bookId.ToString(), "book.epub");
        savedPath.Should().Be(expectedPath);
        File.Exists(savedPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(savedPath)).Should().Equal(payload);
    }

    [Theory]
    [InlineData("malicious.exe")]
    [InlineData("archive.zip")]
    [InlineData("script.sh")]
    public async Task SaveBookFileAsync_WithUnsupportedExtension_ThrowsInvalidOperationException_AndWritesNothing(string fileName)
    {
        var bookId = Guid.NewGuid();
        var payload = new byte[] { 1, 2, 3 };
        using var stream = new MemoryStream(payload);

        var act = async () => await _sut.SaveBookFileAsync(bookId, stream, fileName);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*Unsupported file type: {Path.GetExtension(fileName)}*");

        var bookFolder = Path.Combine(_tempDirectory, bookId.ToString());
        if (Directory.Exists(bookFolder))
        {
            Directory.GetFiles(bookFolder).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task SaveBookFileAsync_SavingSecondFileWithDifferentExtension_LeavesExactlyOneBookFileInFolder()
    {
        var bookId = Guid.NewGuid();

        using (var stream1 = new MemoryStream(new byte[] { 1, 2, 3 }))
        {
            await _sut.SaveBookFileAsync(bookId, stream1, "first.epub");
        }

        using (var stream2 = new MemoryStream(new byte[] { 4, 5, 6, 7 }))
        {
            await _sut.SaveBookFileAsync(bookId, stream2, "second.pdf");
        }

        var bookFolder = Path.Combine(_tempDirectory, bookId.ToString());
        var bookFiles = Directory.GetFiles(bookFolder, "book.*");

        bookFiles.Should().ContainSingle();
        Path.GetFileName(bookFiles[0]).Should().Be("book.pdf");
        (await File.ReadAllBytesAsync(bookFiles[0])).Should().Equal(new byte[] { 4, 5, 6, 7 });
    }

    [Fact]
    public async Task ReplacingBookFileChangesEntityTag()
    {
        var bookId = Guid.NewGuid();

        await _sut.SaveBookFileAsync(bookId, new MemoryStream([1, 2, 3]), "reader.pdf");
        var original = await _sut.GetBookFileInfoAsync(bookId);

        await _sut.SaveBookFileAsync(bookId, new MemoryStream([4, 5, 6, 7, 8]), "reader.pdf");
        var replacement = await _sut.GetBookFileInfoAsync(bookId);

        original.Should().NotBeNull();
        replacement.Should().NotBeNull();
        replacement!.EntityTag.Should().NotBe(original!.EntityTag);
    }

    [Fact]
    public async Task GetBookFileName_ReturnsSavedPath()
    {
        var bookId = Guid.NewGuid();
        using var stream = new MemoryStream(new byte[] { 1, 2 });
        var savedPath = await _sut.SaveBookFileAsync(bookId, stream, "my-book.txt");

        var retrievedPath = _sut.GetBookFileName(bookId);

        retrievedPath.Should().Be(savedPath);
    }

    [Fact]
    public async Task GetBookFile_ReturnsReadableStreamWithSameBytes()
    {
        var bookId = Guid.NewGuid();
        var payload = new byte[] { 10, 20, 30, 40, 50 };
        using (var inStream = new MemoryStream(payload))
        {
            await _sut.SaveBookFileAsync(bookId, inStream, "audio.mp3");
        }

        using var outStream = _sut.GetBookFile(bookId);

        outStream.Should().NotBeNull();
        using var ms = new MemoryStream();
        await outStream!.CopyToAsync(ms);
        ms.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task DeleteBookFile_RemovesOnlyBookFile_CoverSavedBeforehandStillExists()
    {
        var bookId = Guid.NewGuid();

        // Save cover first
        var coverBytes = CreatePngBytes();
        using (var coverStream = new MemoryStream(coverBytes))
        {
            await _sut.SaveBookCoverAsync(bookId, coverStream, "artwork.png");
        }

        // Save book file
        using (var bookStream = new MemoryStream(new byte[] { 1, 2, 3 }))
        {
            await _sut.SaveBookFileAsync(bookId, bookStream, "story.epub");
        }

        var deleted = _sut.DeleteBookFile(bookId);

        deleted.Should().BeTrue();
        _sut.GetBookFileName(bookId).Should().BeNull();
        _sut.GetBookCoverPath(bookId).Should().NotBeNull();
        File.Exists(_sut.GetBookCoverPath(bookId)!).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteBookFiles_RemovesWholeFolder()
    {
        var bookId = Guid.NewGuid();
        using (var stream = new MemoryStream(new byte[] { 1, 2, 3 }))
        {
            await _sut.SaveBookFileAsync(bookId, stream, "book.epub");
        }

        var bookFolder = Path.Combine(_tempDirectory, bookId.ToString());
        Directory.Exists(bookFolder).Should().BeTrue();

        _sut.DeleteBookFiles(bookId);

        Directory.Exists(bookFolder).Should().BeFalse();
    }

    [Fact]
    public async Task SaveBookCoverAsync_FromStream_WritesCoverExt()
    {
        var bookId = Guid.NewGuid();
        var coverBytes = CreatePngBytes();
        using var stream = new MemoryStream(coverBytes);

        var savedPath = await _sut.SaveBookCoverAsync(bookId, stream, "original.jpg");

        var expectedPath = Path.Combine(_tempDirectory, bookId.ToString(), "cover.jpg");
        savedPath.Should().Be(expectedPath);
        File.Exists(savedPath).Should().BeTrue();
    }

    [Fact]
    public async Task SaveBookCoverAsync_WithGifExtension_ThrowsInvalidOperationException()
    {
        var bookId = Guid.NewGuid();
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var act = async () => await _sut.SaveBookCoverAsync(bookId, stream, "animation.gif");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Only PNG, JPG, or JPEG allowed.*");
    }

    [Fact]
    public async Task SaveBookCoverAsync_ReplacingCover_DeletesPreviouslyGeneratedThumbnailAndOldCover()
    {
        var bookId = Guid.NewGuid();

        // 1. Save initial PNG cover
        var initialPngBytes = CreatePngBytes();
        using (var stream1 = new MemoryStream(initialPngBytes))
        {
            await _sut.SaveBookCoverAsync(bookId, stream1, "cover.png");
        }

        // 2. Generate a thumbnail
        var thumbPath = await _sut.GetBookCoverThumbnailPathAsync(bookId, 200);
        thumbPath.Should().NotBeNull();
        File.Exists(thumbPath!).Should().BeTrue();

        var bookFolder = Path.Combine(_tempDirectory, bookId.ToString());
        var oldCoverPath = Path.Combine(bookFolder, "cover.png");
        File.Exists(oldCoverPath).Should().BeTrue();

        // 3. Replace cover with JPG
        var newCoverBytes = CreateJpgBytes();
        using (var stream2 = new MemoryStream(newCoverBytes))
        {
            await _sut.SaveBookCoverAsync(bookId, stream2, "cover.jpg");
        }

        // Old cover.png and thumbnail cover-thumb-*.webp should now be gone
        File.Exists(oldCoverPath).Should().BeFalse();
        File.Exists(thumbPath!).Should().BeFalse();
        Directory.GetFiles(bookFolder, "cover-thumb-*.webp").Should().BeEmpty();

        var newCoverPath = Path.Combine(bookFolder, "cover.jpg");
        File.Exists(newCoverPath).Should().BeTrue();
    }

    [Fact]
    public async Task AdoptBookFileAsync_MovesStagedFileIntoFolder_AndSourceFileNoLongerExists()
    {
        var bookId = Guid.NewGuid();
        var stagedFile = Path.Combine(_tempDirectory, "staged-download.epub");
        var payload = new byte[] { 9, 8, 7, 6, 5 };
        await File.WriteAllBytesAsync(stagedFile, payload);

        var adoptedPath = await _sut.AdoptBookFileAsync(bookId, stagedFile, "final.epub");

        var expectedPath = Path.Combine(_tempDirectory, bookId.ToString(), "book.epub");
        adoptedPath.Should().Be(expectedPath);
        File.Exists(adoptedPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(adoptedPath)).Should().Equal(payload);
        File.Exists(stagedFile).Should().BeFalse("adopted source file should have been moved/deleted");
    }

    [Fact]
    public async Task AdoptBookFileAsync_WithDisallowedExtension_ThrowsInvalidOperationException()
    {
        var bookId = Guid.NewGuid();
        var stagedFile = Path.Combine(_tempDirectory, "staged.exe");
        await File.WriteAllBytesAsync(stagedFile, new byte[] { 1, 2, 3 });

        var act = async () => await _sut.AdoptBookFileAsync(bookId, stagedFile, "staged.exe");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Unsupported file type: .exe*");
        File.Exists(stagedFile).Should().BeTrue("source file must not be modified or moved when rejected");
    }

    [Fact]
    public async Task AdoptBookFileAsync_WithNonExistentSource_ThrowsFileNotFoundException()
    {
        var bookId = Guid.NewGuid();
        var missingPath = Path.Combine(_tempDirectory, "non-existent-source.epub");

        var act = async () => await _sut.AdoptBookFileAsync(bookId, missingPath, "final.epub");

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task SaveBookFileAsync_StreamThatThrowsMidCopy_LeavesNoBookAndNoPartialFileBehind()
    {
        var bookId = Guid.NewGuid();
        using var failingStream = new ThrowingStream(failAfterBytes: 50);

        var act = async () => await _sut.SaveBookFileAsync(bookId, failingStream, "broken.epub");

        await act.Should().ThrowAsync<IOException>()
            .WithMessage("*Simulated stream failure mid-copy*");

        var bookFolder = Path.Combine(_tempDirectory, bookId.ToString());
        if (Directory.Exists(bookFolder))
        {
            Directory.GetFiles(bookFolder, "book.*").Should().BeEmpty();
            Directory.GetFiles(bookFolder, "*.partial").Should().BeEmpty();
        }
    }

    // A directory at the final name makes the commit rename fail
    // deterministically after staging succeeded, with an older book of a
    // different format already in the folder.
    private string ArrangeCommitFailure(Guid bookId, byte[] existingEpub)
    {
        var bookFolder = Path.Combine(_tempDirectory, bookId.ToString());
        Directory.CreateDirectory(bookFolder);
        File.WriteAllBytes(Path.Combine(bookFolder, "book.epub"), existingEpub);
        Directory.CreateDirectory(Path.Combine(bookFolder, "book.pdf"));
        return bookFolder;
    }

    [Fact]
    public async Task SaveBookFileAsync_CrossFormatCommitFails_KeepsThePreviousBookAndNoPartial()
    {
        var bookId = Guid.NewGuid();
        var original = new byte[] { 1, 2, 3 };
        var bookFolder = ArrangeCommitFailure(bookId, original);

        using var replacement = new MemoryStream(new byte[] { 4, 5, 6, 7 });
        var act = async () => await _sut.SaveBookFileAsync(bookId, replacement, "replacement.pdf");

        await act.Should().ThrowAsync<Exception>();
        var epub = Path.Combine(bookFolder, "book.epub");
        File.Exists(epub).Should().BeTrue("a failed replacement must not delete the previous book");
        (await File.ReadAllBytesAsync(epub)).Should().Equal(original);
        Directory.GetFiles(bookFolder, "*.partial").Should().BeEmpty();
    }

    [Fact]
    public async Task AdoptBookFileAsync_CrossFormatCommitFails_KeepsThePreviousBookAndNoPartial()
    {
        var bookId = Guid.NewGuid();
        var original = new byte[] { 1, 2, 3 };
        var bookFolder = ArrangeCommitFailure(bookId, original);
        var staged = Path.Combine(_tempDirectory, "staged-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(staged, new byte[] { 4, 5, 6, 7 });

        var act = async () => await _sut.AdoptBookFileAsync(bookId, staged, "replacement.pdf");

        await act.Should().ThrowAsync<Exception>();
        var epub = Path.Combine(bookFolder, "book.epub");
        File.Exists(epub).Should().BeTrue("a failed replacement must not delete the previous book");
        (await File.ReadAllBytesAsync(epub)).Should().Equal(original);
        Directory.GetFiles(bookFolder, "*.partial").Should().BeEmpty();
    }

    [Fact]
    public async Task AdoptBookFileAsync_AdoptingDifferentExtension_LeavesExactlyOneBookFileInFolder()
    {
        var bookId = Guid.NewGuid();
        using (var epub = new MemoryStream(new byte[] { 1, 2, 3 }))
            await _sut.SaveBookFileAsync(bookId, epub, "first.epub");
        var staged = Path.Combine(_tempDirectory, "staged-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(staged, new byte[] { 4, 5, 6, 7 });

        await _sut.AdoptBookFileAsync(bookId, staged, "second.pdf");

        var bookFiles = Directory.GetFiles(Path.Combine(_tempDirectory, bookId.ToString()), "book.*");
        bookFiles.Should().ContainSingle();
        Path.GetFileName(bookFiles[0]).Should().Be("book.pdf");
    }

    [Fact]
    public async Task SaveBookFileAsync_FormFileOverMemoryStream_ProducesSameResultAsStreamOverload()
    {
        var payload = new byte[] { 101, 102, 103, 104, 105 };
        var fileName = "manual-upload.epub";

        var bookIdFormFile = Guid.NewGuid();
        using (var ms = new MemoryStream(payload))
        {
            var formFile = new FormFile(ms, 0, payload.Length, "file", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/epub+zip"
            };

            var pathFromFormFile = await _sut.SaveBookFileAsync(bookIdFormFile, formFile);
            File.Exists(pathFromFormFile).Should().BeTrue();
            (await File.ReadAllBytesAsync(pathFromFormFile)).Should().Equal(payload);
            Path.GetFileName(pathFromFormFile).Should().Be("book.epub");
        }

        var bookIdStream = Guid.NewGuid();
        using (var ms = new MemoryStream(payload))
        {
            var pathFromStream = await _sut.SaveBookFileAsync(bookIdStream, ms, fileName);
            File.Exists(pathFromStream).Should().BeTrue();
            (await File.ReadAllBytesAsync(pathFromStream)).Should().Equal(payload);
            Path.GetFileName(pathFromStream).Should().Be("book.epub");
        }
    }

    private static byte[] CreatePngBytes()
    {
        using var image = new Image<Rgba32>(10, 10);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] CreateJpgBytes()
    {
        using var image = new Image<Rgba32>(10, 10);
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    private sealed class ThrowingStream : Stream
    {
        private readonly int _failAfterBytes;
        private int _bytesRead;

        public ThrowingStream(int failAfterBytes)
        {
            _failAfterBytes = failAfterBytes;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1000;
        public override long Position { get => _bytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_bytesRead >= _failAfterBytes)
                throw new IOException("Simulated stream failure mid-copy");

            var toRead = Math.Min(count, _failAfterBytes - _bytesRead);
            for (var i = 0; i < toRead; i++)
                buffer[offset + i] = 0xAA;

            _bytesRead += toRead;
            return toRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_bytesRead >= _failAfterBytes)
                throw new IOException("Simulated stream failure mid-copy");

            var toRead = Math.Min(buffer.Length, _failAfterBytes - _bytesRead);
            buffer.Span[..toRead].Fill(0xAA);
            _bytesRead += toRead;
            return toRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------
    // Tracks (multi-track audiobooks)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Track_is_stored_flat_under_its_canonical_name_and_reads_back()
    {
        var bookId = Guid.NewGuid();
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();

        var stored = await _sut.SaveTrackAsync(bookId, 3, new MemoryStream(bytes), "whatever.MP3");

        stored.Should().Be("track-0003.mp3");
        File.Exists(Path.Combine(_tempDirectory, bookId.ToString(), "track-0003.mp3")).Should().BeTrue();
        Directory.GetDirectories(Path.Combine(_tempDirectory, bookId.ToString()))
            .Should().BeEmpty("backup, restore and library-switch retention refuse subfolders in a book folder");

        var info = await _sut.GetTrackInfoAsync(bookId, 3);
        info!.Length.Should().Be(bytes.Length);
        info.ContentType.Should().Be("audio/mpeg");

        await using var whole = await _sut.OpenTrackAsync(bookId, 3);
        (await ReadAllAsync(whole!.Content)).Should().Equal(bytes);

        await using var ranged = await _sut.OpenTrackAsync(bookId, 3, new StorageByteRange(100, 199));
        var slice = new byte[100];
        await ranged!.Content.ReadExactlyAsync(slice);
        slice.Should().Equal(bytes[100..200]);
    }

    [Fact]
    public async Task Adopted_track_moves_the_staged_file_into_place()
    {
        var bookId = Guid.NewGuid();
        var staged = Path.Combine(_tempDirectory, "staged-part.mp3");
        await File.WriteAllBytesAsync(staged, [1, 2, 3, 4]);

        var stored = await _sut.AdoptTrackAsync(bookId, 1, staged, "track.mp3");

        stored.Should().Be("track-0001.mp3");
        File.Exists(staged).Should().BeFalse("adoption consumes the staged file");
        (await _sut.GetTrackInfoAsync(bookId, 1))!.Length.Should().Be(4);
    }

    [Fact]
    public async Task A_track_is_never_mistaken_for_the_primary_book_file()
    {
        // Tracks share an extension with a single-file audiobook, and the
        // primary-file lookup scans the folder by extension.
        var bookId = Guid.NewGuid();
        await _sut.SaveTrackAsync(bookId, 1, new MemoryStream([1, 2, 3]), "a.mp3");
        await _sut.SaveTrackAsync(bookId, 2, new MemoryStream([4, 5, 6]), "b.mp3");

        _sut.GetBookFileName(bookId).Should().BeNull();
        (await _sut.GetBookFileInfoAsync(bookId)).Should().BeNull();
        (await _sut.DeleteBookFileAsync(bookId)).Should().BeFalse();
        (await _sut.GetTrackInfoAsync(bookId, 1)).Should().NotBeNull("deleting the primary file leaves tracks alone");
    }

    [Fact]
    public async Task Storing_a_primary_file_does_not_delete_tracks()
    {
        // Replacing a book file removes other book.* files in the folder. That
        // sweep works by extension and must skip tracks; the caller decides
        // when a book stops being multi-track.
        var bookId = Guid.NewGuid();
        await _sut.SaveTrackAsync(bookId, 1, new MemoryStream([1, 2, 3]), "a.mp3");

        await _sut.SaveBookFileAsync(bookId, new MemoryStream([9, 9]), "replacement.m4b");

        (await _sut.GetTrackInfoAsync(bookId, 1)).Should().NotBeNull();
        Path.GetFileName(_sut.GetBookFileName(bookId)).Should().Be("book.m4b");
    }

    [Fact]
    public async Task Deleting_tracks_removes_every_track_and_nothing_else()
    {
        var bookId = Guid.NewGuid();
        await _sut.SaveBookCoverAsync(bookId, new MemoryStream([1]), "cover.jpg");
        await _sut.SaveTrackAsync(bookId, 1, new MemoryStream([1, 2, 3]), "a.mp3");
        await _sut.SaveTrackAsync(bookId, 2, new MemoryStream([4, 5, 6]), "b.m4a");

        await _sut.DeleteTracksAsync(bookId);

        (await _sut.GetTrackInfoAsync(bookId, 1)).Should().BeNull();
        (await _sut.GetTrackInfoAsync(bookId, 2)).Should().BeNull();
        _sut.GetBookCoverPath(bookId).Should().NotBeNull();
    }

    [Fact]
    public async Task Restoring_a_track_in_another_format_leaves_one_file_per_number()
    {
        var bookId = Guid.NewGuid();
        await _sut.SaveTrackAsync(bookId, 1, new MemoryStream([1, 2, 3]), "a.mp3");

        await _sut.SaveTrackAsync(bookId, 1, new MemoryStream([7, 7]), "a.m4a");

        Directory.GetFiles(Path.Combine(_tempDirectory, bookId.ToString()), "track-0001.*")
            .Select(Path.GetFileName)
            .Should().Equal("track-0001.m4a");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2001)]
    public async Task Out_of_range_track_numbers_find_nothing_and_cannot_be_stored(int number)
    {
        var bookId = Guid.NewGuid();

        (await _sut.GetTrackInfoAsync(bookId, number)).Should().BeNull();
        (await _sut.OpenTrackAsync(bookId, number)).Should().BeNull();
        var act = () => _sut.SaveTrackAsync(bookId, number, new MemoryStream([1]), "a.mp3");
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_non_audio_file_cannot_be_stored_as_a_track()
    {
        var act = () => _sut.SaveTrackAsync(Guid.NewGuid(), 1, new MemoryStream([1]), "chapter.epub");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Nostos.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(Path.GetTempPath());
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(Path.GetTempPath());
    }
}
