using System.Security.Cryptography;
using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// <c>VerifyMediaAsync</c> against the provider-neutral asset-storage abstraction:
/// presence, length and SHA-256 of every expected entry, streamed with the bounded
/// copy buffer. The abstraction exposes no inventory enumeration, so unexpected
/// objects are the caller's responsibility (documented on the interface).
/// </summary>
[Collection(PortableLibraryVerificationCollection.Name)]
public sealed class PortableLibraryVerifierAssetStorageTests
{
    private readonly PortableLibraryVerifier _verifier = new();

    [Fact]
    public async Task Missing_stored_object_is_reported()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var descriptor = BookDescriptor(Guid.NewGuid(), length: 12, sha256: new string('0', 64));

        var report = await _verifier.VerifyMediaAsync(library.Storage, [descriptor]);

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MediaMissing
            && failure.Entity == "media"
            && failure.EntityId == descriptor.BookId.ToString("D"));
        report.MediaFilesVerified.Should().Be(0);
    }

    [Fact]
    public async Task Wrong_stored_length_is_reported()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var bookId = Guid.NewGuid();
        var payload = "stored-payload"u8.ToArray();
        await library.Storage.SaveBookFileAsync(bookId, new MemoryStream(payload), "book.epub");

        var report = await _verifier.VerifyMediaAsync(
            library.Storage,
            [BookDescriptor(bookId, payload.LongLength + 1, Sha256Hex(payload))]);

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MediaLengthMismatch);
        report.MediaFilesVerified.Should().Be(0);
    }

    [Fact]
    public async Task Wrong_stored_hash_is_reported()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var bookId = Guid.NewGuid();
        var payload = "stored-payload"u8.ToArray();
        await library.Storage.SaveBookFileAsync(bookId, new MemoryStream(payload), "book.epub");

        var report = await _verifier.VerifyMediaAsync(
            library.Storage,
            [BookDescriptor(bookId, payload.LongLength, new string('0', 64))]);

        report.Passed.Should().BeFalse();
        report.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MediaHashMismatch);
        report.MediaFilesVerified.Should().Be(0);
    }

    [Fact]
    public async Task Matching_book_and_cover_objects_pass()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        var bookId = Guid.NewGuid();
        var bookBytes = "book-payload"u8.ToArray();
        var coverBytes = "cover-payload"u8.ToArray();
        await library.Storage.SaveBookFileAsync(bookId, new MemoryStream(bookBytes), "book.epub");
        await library.Storage.SaveBookCoverAsync(bookId, new MemoryStream(coverBytes), "cover.png");

        var report = await _verifier.VerifyMediaAsync(
            library.Storage,
            [
                BookDescriptor(bookId, bookBytes.LongLength, Sha256Hex(bookBytes)),
                CoverDescriptor(bookId, coverBytes.LongLength, Sha256Hex(coverBytes)),
            ]);

        report.Passed.Should().BeTrue(
            string.Join("; ", report.Failures.Select(failure => $"{failure.Code}:{failure.Field}")));
        report.Failures.Should().BeEmpty();
        report.MediaFilesVerified.Should().Be(2);
        report.MediaBytesVerified.Should().Be(bookBytes.LongLength + coverBytes.LongLength);
        report.VerifiedKinds.Should().ContainSingle()
            .Which.Should().Be(nameof(PortableArchiveMediaEntry));
    }

    [Fact]
    public async Task Large_object_is_streamed_with_the_bounded_copy_buffer()
    {
        const long length = 48L * 1024 * 1024;
        var sha256 = HashGenerated(length);
        var storage = new SingleBookFileStorage(length);

        var report = await _verifier.VerifyMediaAsync(
            storage,
            [BookDescriptor(storage.BookId, length, sha256)]);

        report.Passed.Should().BeTrue(
            string.Join("; ", report.Failures.Select(failure => $"{failure.Code}:{failure.Field}")));
        report.MediaFilesVerified.Should().Be(1);
        report.MediaBytesVerified.Should().Be(length);
        storage.Stream.MaxRequestedBufferBytes.Should().BeGreaterThan(0);
        storage.Stream.MaxRequestedBufferBytes.Should().BeLessThanOrEqualTo(
            PortableArchiveLimits.CopyBufferBytes);
        storage.Stream.Disposed.Should().BeTrue();
    }

    private static PortableArchiveMediaEntry BookDescriptor(Guid bookId, long length, string sha256) =>
        new(
            BookId: bookId,
            Kind: PortableArchiveFormat.BookMediaKind,
            Path: $"media/books/{bookId:N}/book.epub",
            FileName: "book.epub",
            ContentType: "application/epub+zip",
            Length: length,
            Sha256: sha256);

    private static PortableArchiveMediaEntry CoverDescriptor(Guid bookId, long length, string sha256) =>
        new(
            BookId: bookId,
            Kind: PortableArchiveFormat.CoverMediaKind,
            Path: $"media/books/{bookId:N}/cover.png",
            FileName: "cover.png",
            ContentType: "image/png",
            Length: length,
            Sha256: sha256);

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string HashGenerated(long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4096];
        long offset = 0;
        while (offset < length)
        {
            var count = (int)Math.Min(buffer.Length, length - offset);
            for (var index = 0; index < count; index++)
            {
                buffer[index] = GenerateByte(offset + index);
            }

            hash.AppendData(buffer, 0, count);
            offset += count;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static byte GenerateByte(long offset) => (byte)(offset % 251);

    private sealed class GeneratedAssetStream(long length) : Stream
    {
        private long _offset;

        internal int MaxRequestedBufferBytes { get; private set; }

        internal bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Fill(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => Fill(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaxRequestedBufferBytes = Math.Max(MaxRequestedBufferBytes, buffer.Length);
            return ValueTask.FromResult(Fill(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        private int Fill(Span<byte> buffer)
        {
            var remaining = length - _offset;
            if (remaining <= 0)
            {
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, remaining);
            for (var index = 0; index < count; index++)
            {
                buffer[index] = GenerateByte(_offset + index);
            }

            _offset += count;
            return count;
        }
    }

    private sealed class SingleBookFileStorage : IBookAssetStorage
    {
        private readonly long _length;

        internal SingleBookFileStorage(long length)
        {
            _length = length;
            Stream = new GeneratedAssetStream(length);
        }

        internal Guid BookId { get; } = Guid.NewGuid();

        internal GeneratedAssetStream Stream { get; }

        public Task<StoredAssetInfo?> GetBookFileInfoAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Task.FromResult<StoredAssetInfo?>(Info());

        public Task<StoredAssetRead?> OpenBookFileAsync(
            Guid bookId,
            StorageByteRange? range = null,
            CancellationToken ct = default) =>
            Task.FromResult<StoredAssetRead?>(new StoredAssetRead(Info(), Stream));

        private StoredAssetInfo Info() =>
            new("book.epub", "application/epub+zip", _length, "etag", DateTimeOffset.UnixEpoch);

        public Task<string> SaveBookFileAsync(Guid bookId, Stream content, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> AdoptBookFileAsync(Guid bookId, string sourcePath, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteBookFileAsync(Guid bookId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteBookFilesAsync(Guid bookId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> SaveBookCoverAsync(Guid bookId, Stream content, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredAssetInfo?> GetBookCoverInfoAsync(Guid bookId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredAssetRead?> OpenBookCoverAsync(Guid bookId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(Guid bookId, int width, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(Guid bookId, int width, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteCoverAsync(Guid bookId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
