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
