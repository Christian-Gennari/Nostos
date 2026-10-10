using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services;
using Xunit;

namespace Nostos.Backend.Tests.Tracks;

/// <summary>
/// The download archive of a multi-track audiobook is never written anywhere:
/// its bytes are computed from sizes and checksums recorded at import. These
/// tests hold that computed layout to what a real ZIP reader accepts, and hold
/// every byte range of it to the same bytes the whole archive has.
/// </summary>
public sealed class StoredZipLayoutTests
{
    private static readonly DateTime Timestamp = new(2026, 10, 10, 9, 30, 44, DateTimeKind.Utc);

    [Fact]
    public async Task Archive_is_read_by_a_standard_zip_reader_with_every_member_intact()
    {
        var (layout, tracks, inline) = BuildSample();

        var bytes = await ReadAllAsync(layout, tracks, range: null);

        bytes.LongLength.Should().Be(layout.Length, "the advertised length is the real length");
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        archive.Entries.Select(entry => entry.FullName).Should().Equal(
            "manifest.json", "01 - Första kapitlet.mp3", "02 - Chapter two.mp3", "03.mp3");

        ReadEntry(archive, "manifest.json").Should().Equal(inline);
        ReadEntry(archive, "01 - Första kapitlet.mp3").Should().Equal(tracks[1]);
        ReadEntry(archive, "02 - Chapter two.mp3").Should().Equal(tracks[2]);
        ReadEntry(archive, "03.mp3").Should().Equal(tracks[3]);

        foreach (var entry in archive.Entries)
        {
            entry.CompressedLength.Should().Be(entry.Length, "members are stored, not deflated");
            entry.LastWriteTime.UtcDateTime.Year.Should().Be(2026);
        }
    }

    [Fact]
    public async Task Every_byte_range_equals_the_same_slice_of_the_whole_archive()
    {
        var (layout, tracks, _) = BuildSample();
        var whole = await ReadAllAsync(layout, tracks, range: null);
        var random = new Random(8350);

        var ranges = new List<(long Start, long End)>
        {
            (0, 0),
            (0, whole.Length - 1),
            (whole.Length - 1, whole.Length - 1),
            (whole.Length - 22, whole.Length - 1),
        };
        // Boundaries are where a slicing bug would live: one byte either side
        // of every place the source of the bytes changes.
        foreach (var piece in layout.Pieces)
        {
            foreach (var edge in new[] { piece.Offset - 1, piece.Offset, piece.Offset + piece.Length - 1 })
            {
                if (edge >= 0 && edge < whole.Length)
                    ranges.Add((edge, Math.Min(whole.Length - 1, edge + 3)));
            }
        }
        for (var i = 0; i < 300; i++)
        {
            var start = random.NextInt64(whole.Length);
            ranges.Add((start, start + random.NextInt64(whole.Length - start)));
        }

        foreach (var (start, end) in ranges)
        {
            var slice = await ReadAllAsync(layout, tracks, new StorageByteRange(start, end));
            slice.Should().Equal(
                whole.AsSpan((int)start, (int)(end - start + 1)).ToArray(),
                $"range {start}-{end} must match the whole archive");
        }
    }

    [Fact]
    public void Entity_tag_follows_the_archive_contents()
    {
        var first = BuildSample().Layout;
        var same = BuildSample().Layout;
        var changed = BuildSample(mutateTrackTwo: true).Layout;

        same.EntityTag.Should().Be(first.EntityTag);
        same.Length.Should().Be(first.Length);
        changed.EntityTag.Should().NotBe(first.EntityTag);
        first.EntityTag.Should().StartWith("\"").And.EndWith("\"");
    }

    [Fact]
    public void An_archive_beyond_four_gibibytes_uses_zip64_and_still_opens()
    {
        // Three members of 1.6 GiB put the last member, and the central
        // directory, past the 32-bit offset limit. Their bytes are never read
        // here, so they can be imaginary.
        const long big = 1_717_986_918;
        var tail = Encoding.UTF8.GetBytes("the last member, beyond 4 GiB");
        var entries = new List<StoredZipEntry>
        {
            new("01.mp3", big, 0x11111111, Inline: null, TrackNumber: 1),
            new("02.mp3", big, 0x22222222, Inline: null, TrackNumber: 2),
            new("03.mp3", big, 0x33333333, Inline: null, TrackNumber: 3),
            new("tail.txt", tail.LongLength, Crc32.Compute(tail), tail),
        };

        var layout = StoredZipLayout.Build(entries, Timestamp);
        layout.Length.Should().BeGreaterThan(uint.MaxValue);

        using var archive = new ZipArchive(new VirtualArchiveStream(layout), ZipArchiveMode.Read);
        archive.Entries.Should().HaveCount(4);
        archive.Entries.Select(entry => entry.Length).Should().Equal(big, big, big, tail.LongLength);
        ReadEntry(archive, "tail.txt").Should().Equal(tail);
    }

    [Fact]
    public void A_member_of_four_gibibytes_or_more_is_refused()
    {
        var act = () => StoredZipLayout.Build(
            [new StoredZipEntry("huge.mp3", uint.MaxValue, 0, Inline: null, TrackNumber: 1)],
            Timestamp);

        act.Should().Throw<ArgumentException>().WithMessage("*smaller than 4 GiB*");
    }

    [Fact]
    public async Task A_track_shorter_than_recorded_fails_instead_of_padding_the_archive()
    {
        var (layout, tracks, _) = BuildSample();
        tracks[3] = tracks[3][..^10];

        var act = () => ReadAllAsync(layout, tracks, range: null);

        await act.Should().ThrowAsync<EndOfStreamException>();
    }

    [Fact]
    public void Crc32_matches_the_reference_check_value()
    {
        // The standard CRC-32 check: the ASCII digits 1 to 9.
        Crc32.Compute("123456789"u8).Should().Be(0xCBF43926u);

        var incremental = new Crc32();
        incremental.Append("1234"u8);
        incremental.Append("56789"u8);
        incremental.Value.Should().Be(0xCBF43926u);
    }

    private static (StoredZipLayout Layout, Dictionary<int, byte[]> Tracks, byte[] Inline) BuildSample(
        bool mutateTrackTwo = false)
    {
        var random = new Random(4242);
        var tracks = new Dictionary<int, byte[]>
        {
            [1] = RandomBytes(random, 70_001),
            [2] = RandomBytes(random, 3),
            [3] = RandomBytes(random, 131_072),
        };
        if (mutateTrackTwo)
            tracks[2][0] ^= 0xFF;

        var inline = Encoding.UTF8.GetBytes("{\"metadata\":{\"title\":\"Sample\"}}");
        var entries = new List<StoredZipEntry>
        {
            new("manifest.json", inline.LongLength, Crc32.Compute(inline), inline),
            new("01 - Första kapitlet.mp3", tracks[1].LongLength, Crc32.Compute(tracks[1]), null, 1),
            new("02 - Chapter two.mp3", tracks[2].LongLength, Crc32.Compute(tracks[2]), null, 2),
            new("03.mp3", tracks[3].LongLength, Crc32.Compute(tracks[3]), null, 3),
        };

        return (StoredZipLayout.Build(entries, Timestamp), tracks, inline);
    }

    private static async Task<byte[]> ReadAllAsync(
        StoredZipLayout layout,
        Dictionary<int, byte[]> tracks,
        StorageByteRange? range)
    {
        await using var stream = new StoredZipReadStream(
            layout.Slices(range),
            (number, trackRange, _) => Task.FromResult<StoredAssetRead?>(OpenTrack(tracks[number], trackRange)));
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static StoredAssetRead OpenTrack(byte[] bytes, StorageByteRange range)
    {
        // Mirrors a storage provider: the stream starts at the range start and
        // simply ends when the stored bytes do.
        var start = (int)Math.Min(range.Start, bytes.Length);
        var info = new StoredAssetInfo("track", "audio/mpeg", bytes.Length, "\"t\"", DateTimeOffset.UnixEpoch);
        return new StoredAssetRead(info, new MemoryStream(bytes, start, bytes.Length - start, writable: false), range);
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// A seekable view of a layout whose track bytes are all zero, so a ZIP
    /// reader can walk a multi-gigabyte archive that occupies no memory.
    /// </summary>
    private sealed class VirtualArchiveStream(StoredZipLayout layout) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => layout.Length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= layout.Length || count == 0)
                return 0;

            var end = Math.Min(layout.Length - 1, Position + count - 1);
            var written = 0;
            foreach (var slice in layout.Slices(new StorageByteRange(Position, end)))
            {
                if (slice.Inline is not null)
                    Array.Copy(slice.Inline, slice.Start, buffer, offset + written, slice.Length);
                else
                    Array.Clear(buffer, offset + written, (int)slice.Length);
                written += (int)slice.Length;
            }

            Position += written;
            return written;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                _ => layout.Length + offset,
            };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
