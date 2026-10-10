using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Nostos.Backend.Services;

/// <summary>
/// One file of a stored (uncompressed) ZIP whose size and CRC-32 are already
/// known. <see cref="Inline"/> holds small generated content; a null value
/// means the bytes are the stored track <see cref="TrackNumber"/>.
/// </summary>
public sealed record StoredZipEntry(string Name, long Length, uint Crc32, byte[]? Inline, int TrackNumber = 0);

/// <summary>
/// The complete byte layout of a ZIP archive computed WITHOUT reading its
/// large members.
///
/// Every member is stored, not deflated, and its size and CRC-32 are known up
/// front, so each header can be written before any content and the archive's
/// total length is plain arithmetic. That is what allows an archive that only
/// exists while it is being sent to carry a Content-Length, an entity tag and
/// honour byte ranges, with no temporary file and no data descriptors (which
/// some extractors reject).
///
/// ZIP64 is used only for what can actually overflow here: member offsets and
/// the central directory position of an archive beyond 4 GiB. Individual
/// members are limited to below 4 GiB.
/// </summary>
public sealed class StoredZipLayout
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const ushort Utf8NamesFlag = 0x0800;
    private const ushort VersionDefault = 20;
    private const ushort VersionZip64 = 45;
    private const long Zip32Limit = 0xFFFFFFFFL;

    /// <summary>A contiguous run of archive bytes and where they come from.</summary>
    public sealed record Piece(long Offset, long Length, byte[]? Inline, int TrackNumber);

    private StoredZipLayout(IReadOnlyList<Piece> pieces, long length, string entityTag)
    {
        Pieces = pieces;
        Length = length;
        EntityTag = entityTag;
    }

    public IReadOnlyList<Piece> Pieces { get; }
    public long Length { get; }

    /// <summary>
    /// Strong validator derived from the central directory, which names every
    /// member with its size, checksum and position: any change to the archive's
    /// bytes changes it.
    /// </summary>
    public string EntityTag { get; }

    public static StoredZipLayout Build(IReadOnlyList<StoredZipEntry> entries, DateTime timestampUtc)
    {
        if (entries.Count is 0 or > ushort.MaxValue - 1)
            throw new ArgumentException("A stored ZIP needs between 1 and 65534 entries.", nameof(entries));

        var (dosTime, dosDate) = ToDosDateTime(timestampUtc);
        var pieces = new List<Piece>(entries.Count * 2 + 1);
        using var central = new MemoryStream();
        long offset = 0;

        foreach (var entry in entries)
        {
            if (entry.Length < 0 || entry.Length >= Zip32Limit)
                throw new ArgumentException($"ZIP member '{entry.Name}' must be smaller than 4 GiB.", nameof(entries));
            if (entry.Inline is not null && entry.Inline.LongLength != entry.Length)
                throw new ArgumentException($"ZIP member '{entry.Name}' length does not match its content.", nameof(entries));
            if (entry.Inline is null && entry.TrackNumber < 1)
                throw new ArgumentException($"ZIP member '{entry.Name}' has no content source.", nameof(entries));

            var name = Encoding.UTF8.GetBytes(entry.Name);
            if (name.Length is 0 or > ushort.MaxValue)
                throw new ArgumentException("ZIP member names must be 1 to 65535 UTF-8 bytes.", nameof(entries));

            var localOffset = offset;
            var local = LocalHeader(entry, name, dosTime, dosDate);
            pieces.Add(new Piece(offset, local.Length, local, 0));
            offset += local.Length;

            if (entry.Length > 0)
            {
                pieces.Add(new Piece(offset, entry.Length, entry.Inline, entry.Inline is null ? entry.TrackNumber : 0));
                offset += entry.Length;
            }

            central.Write(CentralHeader(entry, name, dosTime, dosDate, localOffset));
        }

        var centralOffset = offset;
        var centralBytes = central.ToArray();
        var trailer = EndRecords(entries.Count, centralBytes.LongLength, centralOffset);

        var tail = new byte[centralBytes.Length + trailer.Length];
        centralBytes.CopyTo(tail, 0);
        trailer.CopyTo(tail, centralBytes.Length);
        pieces.Add(new Piece(offset, tail.Length, tail, 0));
        offset += tail.Length;

        var tag = Convert.ToHexStringLower(SHA256.HashData(tail).AsSpan(0, 16));
        return new StoredZipLayout(pieces, offset, $"\"{tag}\"");
    }

    private static byte[] LocalHeader(StoredZipEntry entry, byte[] name, ushort dosTime, ushort dosDate)
    {
        var header = new byte[30 + name.Length];
        var span = header.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, LocalHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], VersionDefault);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], Utf8NamesFlag);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0); // stored
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], dosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], entry.Crc32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], (uint)entry.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[22..], (uint)entry.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], 0);
        name.CopyTo(span[30..]);
        return header;
    }

    private static byte[] CentralHeader(
        StoredZipEntry entry, byte[] name, ushort dosTime, ushort dosDate, long localOffset)
    {
        var zip64 = localOffset >= Zip32Limit;
        var extraLength = zip64 ? 12 : 0;
        var header = new byte[46 + name.Length + extraLength];
        var span = header.AsSpan();
        var version = zip64 ? VersionZip64 : VersionDefault;

        BinaryPrimitives.WriteUInt32LittleEndian(span, CentralHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], Utf8NamesFlag);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 0); // stored
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], dosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], entry.Crc32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)entry.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)entry.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)extraLength);
        // comment length, disk number start, internal and external attributes: all zero.
        BinaryPrimitives.WriteUInt32LittleEndian(span[42..], zip64 ? 0xFFFFFFFFu : (uint)localOffset);
        name.CopyTo(span[46..]);

        if (zip64)
        {
            var extra = span[(46 + name.Length)..];
            BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 8);
            BinaryPrimitives.WriteInt64LittleEndian(extra[4..], localOffset);
        }

        return header;
    }

    private static byte[] EndRecords(int entryCount, long centralLength, long centralOffset)
    {
        var zip64 = centralOffset >= Zip32Limit || centralLength >= Zip32Limit;
        var records = new byte[(zip64 ? 56 + 20 : 0) + 22];
        var span = records.AsSpan();

        if (zip64)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span, Zip64EndOfCentralDirectorySignature);
            BinaryPrimitives.WriteInt64LittleEndian(span[4..], 44);
            BinaryPrimitives.WriteUInt16LittleEndian(span[12..], VersionZip64);
            BinaryPrimitives.WriteUInt16LittleEndian(span[14..], VersionZip64);
            // disk numbers (two uint32) stay zero.
            BinaryPrimitives.WriteInt64LittleEndian(span[24..], entryCount);
            BinaryPrimitives.WriteInt64LittleEndian(span[32..], entryCount);
            BinaryPrimitives.WriteInt64LittleEndian(span[40..], centralLength);
            BinaryPrimitives.WriteInt64LittleEndian(span[48..], centralOffset);

            var locator = span[56..];
            BinaryPrimitives.WriteUInt32LittleEndian(locator, Zip64LocatorSignature);
            BinaryPrimitives.WriteInt64LittleEndian(locator[8..], centralOffset + centralLength);
            BinaryPrimitives.WriteUInt32LittleEndian(locator[16..], 1);
            span = span[76..];
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span, EndOfCentralDirectorySignature);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)entryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], zip64 ? 0xFFFFFFFFu : (uint)centralLength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], zip64 ? 0xFFFFFFFFu : (uint)centralOffset);
        return records;
    }

    private static (ushort Time, ushort Date) ToDosDateTime(DateTime utc)
    {
        // The DOS format cannot represent anything before 1980 or after 2107.
        var year = Math.Clamp(utc.Year, 1980, 2107);
        var date = (ushort)(((year - 1980) << 9) | (utc.Month << 5) | utc.Day);
        var time = (ushort)((utc.Hour << 11) | (utc.Minute << 5) | (utc.Second / 2));
        return (time, date);
    }

    /// <summary>
    /// The sources that make up <paramref name="range"/> (or the whole archive),
    /// each trimmed to the part of it the range covers.
    /// </summary>
    public IReadOnlyList<Slice> Slices(StorageByteRange? range)
    {
        var start = range?.Start ?? 0;
        var end = range?.EndInclusive ?? Length - 1;
        if (start < 0 || end >= Length || end < start)
            throw new ArgumentOutOfRangeException(nameof(range));

        var slices = new List<Slice>();
        foreach (var piece in Pieces)
        {
            var pieceEnd = piece.Offset + piece.Length - 1;
            if (pieceEnd < start)
                continue;
            if (piece.Offset > end)
                break;

            var from = Math.Max(start, piece.Offset) - piece.Offset;
            var to = Math.Min(end, pieceEnd) - piece.Offset;
            slices.Add(new Slice(piece.Inline, piece.TrackNumber, from, to - from + 1));
        }

        return slices;
    }

    /// <summary><paramref name="Length"/> bytes starting at <paramref name="Start"/> within one source.</summary>
    public sealed record Slice(byte[]? Inline, int TrackNumber, long Start, long Length);
}

/// <summary>
/// Forward-only read stream over the slices of a <see cref="StoredZipLayout"/>.
/// A track is opened only when the read position reaches it and released as
/// soon as it is consumed, so at most one storage read is open at a time
/// however many tracks the archive spans.
/// </summary>
public sealed class StoredZipReadStream(
    IReadOnlyList<StoredZipLayout.Slice> slices,
    Func<int, StorageByteRange, CancellationToken, Task<StoredAssetRead?>> openTrack) : Stream
{
    private int _index;
    private long _consumed;
    private StoredAssetRead? _open;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_index < slices.Count && buffer.Length > 0)
        {
            var slice = slices[_index];
            var remaining = slice.Length - _consumed;
            if (remaining == 0)
            {
                await AdvanceAsync();
                continue;
            }

            var wanted = (int)Math.Min(buffer.Length, remaining);
            int read;
            if (slice.Inline is not null)
            {
                slice.Inline.AsMemory((int)(slice.Start + _consumed), wanted).CopyTo(buffer);
                read = wanted;
            }
            else
            {
                _open ??= await openTrack(
                        slice.TrackNumber,
                        new StorageByteRange(slice.Start, slice.Start + slice.Length - 1),
                        cancellationToken)
                    ?? throw new FileNotFoundException(
                        $"Track {slice.TrackNumber} is missing from storage.");

                read = await _open.Content.ReadAsync(buffer[..wanted], cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException(
                        $"Track {slice.TrackNumber} ended with {remaining} bytes still expected.");
            }

            _consumed += read;
            return read;
        }

        return 0;
    }

    private async ValueTask AdvanceAsync()
    {
        if (_open is not null)
        {
            await _open.DisposeAsync();
            _open = null;
        }

        _index++;
        _consumed = 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask DisposeAsync()
    {
        if (_open is not null)
        {
            await _open.DisposeAsync();
            _open = null;
        }

        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _open is not null)
        {
            _open.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _open = null;
        }

        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
