using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortableZipAsyncApiTests
{
    private const string EntryName = "payload.txt";

    [Fact]
    [Trait("Category", "KnownLimitation")]
    public async Task Net10_zip_writer_exposes_sync_data_descriptor_limitation_on_non_seekable_sink()
    {
        var payload = Encoding.UTF8.GetBytes("async ZIP payload");
        var sink = new AsyncOnlyNonSeekableWriteStream();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WriteArchiveAsync(sink, payload));

        exception.Message.Should().Be("Synchronous write forbidden.");
    }

    [Fact]
    [Trait("Category", "KnownLimitation")]
    public async Task Net10_zip_reader_entries_expose_sync_read_limitation_on_seekable_source()
    {
        var archiveBytes = CreateArchiveFixture(Encoding.UTF8.GetBytes("range read"));
        await using var source = new AsyncOnlySeekableReadStream(archiveBytes);
        await using var archive = await ZipArchive.CreateAsync(
            source,
            ZipArchiveMode.Read,
            leaveOpen: true,
            entryNameEncoding: null,
            CancellationToken.None);

        var exception = Assert.Throws<InvalidOperationException>(() => _ = archive.Entries);
        exception.Message.Should().Be("Synchronous read forbidden.");
    }

    [Fact]
    public async Task Net10_zip_reader_opens_entries_without_sync_source_reads()
    {
        var expected = Encoding.UTF8.GetBytes("entry OpenAsync must stay asynchronous");
        var archiveBytes = CreateArchiveFixture(expected);
        await using var source = new AsyncOnlySeekableReadStream(
            archiveBytes,
            forbidSynchronousReads: false);

        await using var archive = await ZipArchive.CreateAsync(
            source,
            ZipArchiveMode.Read,
            leaveOpen: true,
            entryNameEncoding: null,
            CancellationToken.None);

        var entry = archive.GetEntry(EntryName);
        entry.Should().NotBeNull();
        source.ForbidSynchronousReads = true;

        await using var entryStream = await entry!.OpenAsync(CancellationToken.None);
        using var actual = new MemoryStream();
        await entryStream.CopyToAsync(actual, CancellationToken.None);

        actual.ToArray().Should().Equal(expected);
    }

    [Fact]
    public async Task Net10_zip_non_seekable_writer_emits_data_descriptor()
    {
        var payload = Encoding.UTF8.GetBytes("data descriptor");
        var sink = new AsyncOnlyNonSeekableWriteStream(forbidSynchronousWrites: false);
        await WriteArchiveAsync(sink, payload);

        var archiveBytes = sink.ToArray();
        var localHeaderFlags = BinaryPrimitives.ReadUInt16LittleEndian(archiveBytes.AsSpan(6, 2));
        (localHeaderFlags & 0x0008).Should().NotBe(0);

        var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(archiveBytes.AsSpan(26, 2));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(archiveBytes.AsSpan(28, 2));
        var descriptorOffset = 30 + fileNameLength + extraLength + payload.Length;
        BinaryPrimitives.ReadUInt32LittleEndian(archiveBytes.AsSpan(descriptorOffset, 4))
            .Should().Be(0x08074b50);
        BinaryPrimitives.ReadUInt32LittleEndian(archiveBytes.AsSpan(descriptorOffset + 4, 4))
            .Should().Be(Crc32(payload));
        BinaryPrimitives.ReadUInt32LittleEndian(archiveBytes.AsSpan(descriptorOffset + 8, 4))
            .Should().Be((uint)payload.Length);
        BinaryPrimitives.ReadUInt32LittleEndian(archiveBytes.AsSpan(descriptorOffset + 12, 4))
            .Should().Be((uint)payload.Length);
        sink.SynchronousWriteCalls.Should().BeGreaterThan(0,
            "the data descriptor is currently emitted with synchronous writes");
    }

    [Fact]
    public async Task Net10_zip_roundtrip_supports_zip64_offsets()
    {
        var expected = Encoding.UTF8.GetBytes("ZIP64 sparse-offset payload");
        var centralDirectoryOffset = (long)uint.MaxValue + 4096;
        var (localRegion, tail) = CreateZip64OffsetFixture(
            EntryName,
            expected,
            centralDirectoryOffset);
        await using var source = new SparseZip64ArchiveStream(
            localRegion,
            centralDirectoryOffset,
            tail);

        await using var archive = await ZipArchive.CreateAsync(
            source,
            ZipArchiveMode.Read,
            leaveOpen: true,
            entryNameEncoding: null,
            CancellationToken.None);

        archive.Entries.Should().ContainSingle();
        var entry = archive.GetEntry(EntryName);
        entry.Should().NotBeNull();
        source.ForbidSynchronousReads = true;
        await using var entryStream = await entry!.OpenAsync(CancellationToken.None);
        using var actual = new MemoryStream();
        await entryStream.CopyToAsync(actual, CancellationToken.None);

        actual.ToArray().Should().Equal(expected);
        source.Length.Should().BeGreaterThan(uint.MaxValue,
            "the synthetic archive places its central directory above the 32-bit offset limit");
    }

    private static byte[] CreateArchiveFixture(byte[] payload)
    {
        using var sink = new MemoryStream();
        using (var archive = new ZipArchive(sink, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(EntryName, CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            entryStream.Write(payload);
        }

        return sink.ToArray();
    }

    private static async Task WriteArchiveAsync(
        AsyncOnlyNonSeekableWriteStream sink,
        byte[] payload)
    {
        await using (var archive = await ZipArchive.CreateAsync(
            sink,
            ZipArchiveMode.Create,
            leaveOpen: true,
            entryNameEncoding: null,
            CancellationToken.None))
        {
            var entry = archive.CreateEntry(EntryName, CompressionLevel.NoCompression);
            await using var entryStream = await entry.OpenAsync(CancellationToken.None);
            await entryStream.WriteAsync(payload, CancellationToken.None);
        }
    }

    private static (byte[] LocalRegion, byte[] Tail) CreateZip64OffsetFixture(
        string entryName,
        byte[] payload,
        long centralDirectoryOffset)
    {
        var name = Encoding.UTF8.GetBytes(entryName);
        var crc = Crc32(payload);

        using var localBuffer = new MemoryStream();
        using (var writer = new BinaryWriter(localBuffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x04034b50u);
            writer.Write((ushort)45);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0x0021);
            writer.Write(crc);
            writer.Write((uint)payload.Length);
            writer.Write((uint)payload.Length);
            writer.Write((ushort)name.Length);
            writer.Write((ushort)0);
            writer.Write(name);
            writer.Write(payload);
        }

        using var centralBuffer = new MemoryStream();
        using (var writer = new BinaryWriter(centralBuffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x02014b50u);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0x0021);
            writer.Write(crc);
            writer.Write((uint)payload.Length);
            writer.Write((uint)payload.Length);
            writer.Write((ushort)name.Length);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(name);
        }

        var centralDirectory = centralBuffer.ToArray();
        var zip64EndOffset = checked((ulong)centralDirectoryOffset + (ulong)centralDirectory.Length);

        using var tailBuffer = new MemoryStream();
        using (var writer = new BinaryWriter(tailBuffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(centralDirectory);

            writer.Write(0x06064b50u);
            writer.Write(44UL);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(1UL);
            writer.Write(1UL);
            writer.Write((ulong)centralDirectory.Length);
            writer.Write((ulong)centralDirectoryOffset);

            writer.Write(0x07064b50u);
            writer.Write(0u);
            writer.Write(zip64EndOffset);
            writer.Write(1u);

            writer.Write(0x06054b50u);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(ushort.MaxValue);
            writer.Write(ushort.MaxValue);
            writer.Write(uint.MaxValue);
            writer.Write(uint.MaxValue);
            writer.Write((ushort)0);
        }

        return (localBuffer.ToArray(), tailBuffer.ToArray());
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }

        return ~crc;
    }

    private sealed class AsyncOnlyNonSeekableWriteStream(
        bool forbidSynchronousWrites = true) : Stream
    {
        private readonly MemoryStream _buffer = new();
        public int SynchronousWriteCalls { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _buffer.ToArray();

        public override void Flush()
        {
            if (forbidSynchronousWrites)
                throw new InvalidOperationException("Synchronous flush forbidden.");
            _buffer.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _buffer.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            RecordSynchronousWrite();
            _buffer.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RecordSynchronousWrite();
            _buffer.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            RecordSynchronousWrite();
            _buffer.WriteByte(value);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _buffer.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            _buffer.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            // Keep the backing buffer available to the test after ZIP finalization.
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void RecordSynchronousWrite()
        {
            SynchronousWriteCalls++;
            if (forbidSynchronousWrites)
                throw new InvalidOperationException("Synchronous write forbidden.");
        }
    }

    private sealed class AsyncOnlySeekableReadStream(
        byte[] bytes,
        bool forbidSynchronousReads = true) : Stream
    {
        private readonly MemoryStream _source = new(bytes, writable: false);

        public bool ForbidSynchronousReads { get; set; } = forbidSynchronousReads;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _source.Length;
        public override long Position
        {
            get => _source.Position;
            set => _source.Position = value;
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (ForbidSynchronousReads)
                throw new InvalidOperationException("Synchronous read forbidden.");
            return _source.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (ForbidSynchronousReads)
                throw new InvalidOperationException("Synchronous read forbidden.");
            return _source.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _source.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            _source.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            _source.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _source.Dispose();
        }

        public override ValueTask DisposeAsync() => _source.DisposeAsync();
    }

    private sealed class SparseZip64ArchiveStream(
        byte[] localRegion,
        long centralDirectoryOffset,
        byte[] tail) : Stream
    {
        private long _position;
        private readonly long _length = checked(centralDirectoryOffset + tail.Length);

        public bool ForbidSynchronousReads { get; set; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (ForbidSynchronousReads)
                throw new InvalidOperationException("Synchronous read forbidden.");
            return ReadCore(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (ForbidSynchronousReads)
                throw new InvalidOperationException("Synchronous read forbidden.");
            return ReadCore(buffer);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (next < 0)
                throw new IOException("Attempted to seek before the start of the ZIP source.");

            _position = next;
            return _position;
        }

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private int ReadCore(Span<byte> buffer)
        {
            if (buffer.Length == 0 || _position >= _length)
                return 0;

            var count = (int)Math.Min(buffer.Length, _length - _position);
            var readBuffer = buffer[..count];
            readBuffer.Clear();
            CopyOverlap(localRegion, 0, _position, readBuffer);
            CopyOverlap(tail, centralDirectoryOffset, _position, readBuffer);
            _position += count;
            return count;
        }

        private static void CopyOverlap(
            byte[] source,
            long sourceOffset,
            long readOffset,
            Span<byte> destination)
        {
            var overlapStart = Math.Max(sourceOffset, readOffset);
            var overlapEnd = Math.Min(
                sourceOffset + source.Length,
                readOffset + destination.Length);
            if (overlapStart >= overlapEnd)
                return;

            var sourceIndex = checked((int)(overlapStart - sourceOffset));
            var destinationIndex = checked((int)(overlapStart - readOffset));
            var count = checked((int)(overlapEnd - overlapStart));
            source.AsSpan(sourceIndex, count).CopyTo(destination[destinationIndex..]);
        }
    }
}
