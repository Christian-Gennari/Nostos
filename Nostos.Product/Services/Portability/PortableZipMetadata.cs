using System.Buffers.Binary;
using System.Text;

namespace Nostos.Backend.Services.Portability;

internal sealed record PortableZipTailLayout(
    long CentralDirectoryOffset, long CentralDirectorySize, long EntryCount,
    long EocdOffset, bool IsZip64, int PrefetchLength);

internal static class PortableZipMetadata
{
    internal static PortableArchiveException Invalid(string detail) => new("invalid_zip", detail);
    internal static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    internal static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    internal static long U64(ReadOnlySpan<byte> bytes, int offset)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
        if (value > long.MaxValue)
            throw Invalid("ZIP64 value exceeds the signed archive offset range.");
        return (long)value;
    }
    internal static void RequireRange(long offset, long length, long end)
    {
        if (offset < 0 || length < 0 || offset > end || length > end - offset)
            throw Invalid("ZIP range lies outside its containing region.");
    }
    internal static async Task ReadExactlyAsync(IPortableArchiveSource source, long offset, Memory<byte> bytes, CancellationToken ct)
    {
        RequireRange(offset, bytes.Length, source.Length);
        var read = 0;
        while (read < bytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            var count = await source.ReadAtAsync(offset + read, bytes[read..], ct).ConfigureAwait(false);
            if (count < 0 || count > bytes.Length - read)
                throw Invalid("Archive source returned an invalid read count.");
            if (count == 0)
                throw FilePortableArchiveSource.CreateUnexpectedEndException(offset + read, source.Length);
            read += count;
        }
    }
}

internal static class PortableZipTailLocator
{
    public static async Task<PortableZipTailLayout> LocateAsync(
        IPortableArchiveSource source, PortableArchiveBufferBudget budget, CancellationToken ct = default)
    {
        PortableArchiveValidation.ValidateArchiveSize(source.Length);
        if (source.Length < 22)
            throw PortableZipMetadata.Invalid("Archive has no complete EOCD.");
        var searchLength = (int)Math.Min(source.Length, PortableArchiveLimits.MaxEocdSearchBytes);
        using var search = await budget.RentAsync(searchLength, ct).ConfigureAwait(false);
        var searchOffset = source.Length - searchLength;
        await PortableZipMetadata.ReadExactlyAsync(source, searchOffset, search.Memory, ct).ConfigureAwait(false);
        var index = FindEocd(search.Memory.Span);
        if (index < 0)
            throw PortableZipMetadata.Invalid("No EOCD with a valid bounded comment was found.");
        var eocdOffset = searchOffset + index;
        var classic = ParseClassic(search.Memory.Span[index..]);
        long count = classic.Count, size = classic.Size, offset = classic.Offset;
        var metadataStart = eocdOffset;
        var zip64 = classic.NeedsZip64;
        using var scratch = await budget.RentAsync(56, ct).ConfigureAwait(false);
        if (eocdOffset >= 20)
        {
            await PortableZipMetadata.ReadExactlyAsync(source, eocdOffset - 20, scratch.Memory[..20], ct).ConfigureAwait(false);
            zip64 |= PortableZipMetadata.U32(scratch.Memory.Span, 0) == 0x07064b50;
        }
        if (zip64)
        {
            if (eocdOffset < 20 || PortableZipMetadata.U32(scratch.Memory.Span, 0) != 0x07064b50)
                throw PortableZipMetadata.Invalid("ZIP64 locator is missing.");
            if (PortableZipMetadata.U32(scratch.Memory.Span, 4) != 0 || PortableZipMetadata.U32(scratch.Memory.Span, 16) != 1)
                throw PortableZipMetadata.Invalid("Multi-disk ZIP64 archives are not supported.");
            var recordOffset = PortableZipMetadata.U64(scratch.Memory.Span, 8);
            PortableZipMetadata.RequireRange(recordOffset, 56, eocdOffset - 20);
            await PortableZipMetadata.ReadExactlyAsync(source, recordOffset, scratch.Memory, ct).ConfigureAwait(false);
            (count, size, offset) = ParseZip64(scratch.Memory.Span, recordOffset, eocdOffset - 20);
            RequireClassicAgreement(classic, count, size, offset);
            metadataStart = recordOffset;
        }
        PortableArchiveValidation.ValidateArchiveEntryCount(count > int.MaxValue ? int.MaxValue : (int)count);
        if (size > PortableArchiveLimits.MaxCentralDirectoryBytes)
            throw PortableZipMetadata.Invalid("ZIP central directory exceeds its bounded cache limit.");
        PortableZipMetadata.RequireRange(offset, size, metadataStart);
        if (offset + size != metadataStart)
            throw PortableZipMetadata.Invalid("ZIP central directory must end exactly at its end-record region.");
        if (offset >= source.Length)
            throw PortableZipMetadata.Invalid("ZIP central directory starts outside the archive.");
        var tailLength = source.Length - offset;
        if (tailLength > PortableArchiveLimits.MaxPrefetchedZipTailBytes)
            throw PortableZipMetadata.Invalid("ZIP tail exceeds its bounded prefetch limit.");
        return new(offset, size, count, eocdOffset, zip64, (int)tailLength);
    }

    // Recheck the owned snapshot, not just the earlier source reads. This also closes a
    // discovery/prefetch change of view: native sees only the checked metadata in this lease.
    internal static void ValidatePrefetchedTail(ReadOnlySpan<byte> tail, PortableZipTailLayout layout)
    {
        if (tail.Length != layout.PrefetchLength)
            throw PortableZipMetadata.Invalid("ZIP prefetched tail length changed.");
        var searchLength = Math.Min(tail.Length, PortableArchiveLimits.MaxEocdSearchBytes);
        var searchStart = tail.Length - searchLength;
        var index = FindEocd(tail[searchStart..]);
        if (index < 0 || layout.CentralDirectoryOffset + searchStart + index != layout.EocdOffset)
            throw PortableZipMetadata.Invalid("ZIP EOCD view changed between discovery and prefetch.");
        var eocdIndex = searchStart + index;
        var classic = ParseClassic(tail[eocdIndex..]);
        RequireClassicAgreement(classic, layout.EntryCount, layout.CentralDirectorySize, layout.CentralDirectoryOffset);
        var hasLocator = eocdIndex >= 20 && PortableZipMetadata.U32(tail, eocdIndex - 20) == 0x07064b50;
        if (layout.IsZip64 != (classic.NeedsZip64 || hasLocator))
            throw PortableZipMetadata.Invalid("ZIP64 view changed between discovery and prefetch.");
        if (!layout.IsZip64)
        {
            if (layout.CentralDirectorySize != eocdIndex)
                throw PortableZipMetadata.Invalid("ZIP central directory is not contiguous with EOCD.");
            return;
        }
        if (!hasLocator)
            throw PortableZipMetadata.Invalid("ZIP64 locator is missing from the prefetched snapshot.");
        var locator = tail.Slice(eocdIndex - 20, 20);
        if (PortableZipMetadata.U32(locator, 4) != 0 || PortableZipMetadata.U32(locator, 16) != 1)
            throw PortableZipMetadata.Invalid("Multi-disk ZIP64 archives are not supported.");
        var recordOffset = PortableZipMetadata.U64(locator, 8);
        if (recordOffset != layout.CentralDirectoryOffset + layout.CentralDirectorySize)
            throw PortableZipMetadata.Invalid("ZIP64 record changed the validated directory boundary.");
        PortableZipMetadata.RequireRange(layout.CentralDirectorySize, 56, tail.Length);
        var (count, size, offset) = ParseZip64(tail.Slice((int)layout.CentralDirectorySize, 56), recordOffset, layout.EocdOffset - 20);
        if (count != layout.EntryCount || size != layout.CentralDirectorySize || offset != layout.CentralDirectoryOffset)
            throw PortableZipMetadata.Invalid("ZIP64 directory view changed between discovery and prefetch.");
    }

    private static void RequireClassicAgreement(
        (ushort DiskCount, ushort Count, uint Size, uint Offset, bool NeedsZip64) classic,
        long count, long size, long offset)
    {
        if ((classic.Count != ushort.MaxValue && classic.Count != count)
            || (classic.Size != uint.MaxValue && classic.Size != size)
            || (classic.Offset != uint.MaxValue && classic.Offset != offset)
            || (classic.DiskCount != ushort.MaxValue && classic.DiskCount != count))
            throw PortableZipMetadata.Invalid("Classic and ZIP64 EOCD declarations disagree.");
    }

    private static int FindEocd(ReadOnlySpan<byte> bytes)
    {
        // Select the last complete-record start native can reach from EOF-18. Earlier
        // signatures can belong to stored EPUBs, payloads, names or directory fields.
        // Validate only this candidate: never fall back to an earlier valid EOCD.
        for (var i = bytes.Length - 22; i >= 0; i--)
        {
            if (PortableZipMetadata.U32(bytes, i) != 0x06054b50)
                continue;
            if (PortableZipMetadata.U16(bytes, i + 20) != bytes.Length - i - 22)
                throw PortableZipMetadata.Invalid("ZIP EOCD comment must end exactly at EOF.");
            // Signature-free comments remain supported. Native's short-source buffered
            // probe can over-read into the final bytes, so reject comment signatures even
            // when they cannot start a complete record; do not treat them as candidates.
            for (var comment = i + 22; comment <= bytes.Length - 4; comment++)
                if (PortableZipMetadata.U32(bytes, comment) == 0x06054b50)
                    throw PortableZipMetadata.Invalid("ZIP EOCD comment contains an EOCD signature.");
            return i;
        }
        return -1;
    }
    private static (ushort DiskCount, ushort Count, uint Size, uint Offset, bool NeedsZip64) ParseClassic(ReadOnlySpan<byte> bytes)
    {
        if (PortableZipMetadata.U16(bytes, 4) != 0 || PortableZipMetadata.U16(bytes, 6) != 0)
            throw PortableZipMetadata.Invalid("Multi-disk archives are not supported.");
        var diskCount = PortableZipMetadata.U16(bytes, 8);
        var count = PortableZipMetadata.U16(bytes, 10);
        if (diskCount != count && diskCount != ushort.MaxValue && count != ushort.MaxValue)
            throw PortableZipMetadata.Invalid("EOCD entry counts disagree.");
        var size = PortableZipMetadata.U32(bytes, 12);
        var offset = PortableZipMetadata.U32(bytes, 16);
        return (diskCount, count, size, offset,
            count == ushort.MaxValue || diskCount == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue);
    }
    private static (long Count, long Size, long Offset) ParseZip64(ReadOnlySpan<byte> bytes, long offset, long locator)
    {
        if (PortableZipMetadata.U32(bytes, 0) != 0x06064b50)
            throw PortableZipMetadata.Invalid("ZIP64 end record signature is invalid.");
        var size = PortableZipMetadata.U64(bytes, 4);
        if (size < 44 || size > PortableArchiveLimits.MaxZip64EndRecordBytes - 12)
            throw PortableZipMetadata.Invalid("ZIP64 end record length is invalid.");
        PortableZipMetadata.RequireRange(offset, size + 12, locator);
        if (offset + size + 12 != locator)
            throw PortableZipMetadata.Invalid("ZIP64 end record is not contiguous with its locator.");
        if (PortableZipMetadata.U32(bytes, 16) != 0 || PortableZipMetadata.U32(bytes, 20) != 0)
            throw PortableZipMetadata.Invalid("Multi-disk ZIP64 archives are not supported.");
        var count = PortableZipMetadata.U64(bytes, 32);
        if (count != PortableZipMetadata.U64(bytes, 24))
            throw PortableZipMetadata.Invalid("ZIP64 entry counts disagree.");
        return (count, PortableZipMetadata.U64(bytes, 40), PortableZipMetadata.U64(bytes, 48));
    }
}

internal sealed record PortableZipDirectoryEntry(
    string Path, ushort Flags, ushort CompressionMethod, uint Crc32,
    long CompressedLength, long Length, long LocalHeaderOffset, ReadOnlyMemory<byte> NameBytes);

internal static class PortableZipDirectoryParser
{
    public static IReadOnlyList<PortableZipDirectoryEntry> Parse(
        ReadOnlyMemory<byte> directory, long expectedEntryCount, long centralDirectoryOffset)
    {
        PortableArchiveValidation.ValidateArchiveEntryCount(expectedEntryCount > int.MaxValue ? int.MaxValue : (int)expectedEntryCount);
        if (expectedEntryCount < 0 || directory.Length > PortableArchiveLimits.MaxCentralDirectoryBytes)
            throw PortableZipMetadata.Invalid("Invalid bounded directory declaration.");
        var entries = new List<PortableZipDirectoryEntry>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offsets = new HashSet<long>();
        var position = 0;
        long total = 0;
        while (position < directory.Length)
        {
            var bytes = directory.Span[position..];
            if (bytes.Length < 46 || PortableZipMetadata.U32(bytes, 0) != 0x02014b50)
                throw PortableZipMetadata.Invalid("Truncated or invalid central directory entry.");
            var nameLength = PortableZipMetadata.U16(bytes, 28);
            var extraLength = PortableZipMetadata.U16(bytes, 30);
            var commentLength = PortableZipMetadata.U16(bytes, 32);
            var recordLength = 46 + nameLength + extraLength + commentLength;
            if (recordLength > bytes.Length)
                throw PortableZipMetadata.Invalid("Central directory variable fields overrun the directory.");
            var flags = PortableZipMetadata.U16(bytes, 8);
            var method = PortableZipMetadata.U16(bytes, 10);
            ValidateFlagsAndMethod(flags, method);
            long length = PortableZipMetadata.U32(bytes, 24), compressed = PortableZipMetadata.U32(bytes, 20);
            long offset = PortableZipMetadata.U32(bytes, 42), disk = PortableZipMetadata.U16(bytes, 34);
            ApplyZip64(bytes.Slice(46 + nameLength, extraLength), ref length, ref compressed, ref offset, ref disk);
            if (disk != 0)
                throw PortableZipMetadata.Invalid("Multi-disk entry is not supported.");
            if (offset >= centralDirectoryOffset || offset < 0 || !offsets.Add(offset))
                throw PortableZipMetadata.Invalid("Invalid or duplicate local header offset.");
            var nameBytes = directory.Slice(position + 46, nameLength);
            // Native .NET defaults to UTF-8, also when the UTF-8 flag is absent.
            var path = PortableArchiveValidation.ValidateArchivePath(Encoding.UTF8.GetString(nameBytes.Span));
            PortableArchiveValidation.ValidateUniqueArchivePath(path, paths.Add(path));
            total = PortableArchiveValidation.ValidateDeclaredEntry(path, length, compressed, total);
            entries.Add(new(path, flags, method, PortableZipMetadata.U32(bytes, 16), compressed, length, offset, nameBytes));
            PortableArchiveValidation.ValidateArchiveEntryCount(entries.Count);
            position += recordLength;
        }
        if (entries.Count != expectedEntryCount)
            throw PortableZipMetadata.Invalid("Central directory count differs from the EOCD.");
        return entries;
    }

    internal static void ValidateFlagsAndMethod(ushort flags, ushort method)
    {
        if ((flags & (1 | 0x40 | 0x2000)) != 0)
            throw PortableZipMetadata.Invalid("Encrypted entries are not supported.");
        if ((flags & ~0x080e) != 0 || method is not (0 or 8))
            throw PortableZipMetadata.Invalid("Unsupported ZIP flags or compression method.");
    }

    internal static void ApplyZip64(ReadOnlySpan<byte> extras, ref long length, ref long compressed, ref long offset, ref long disk)
    {
        var needsLength = length == uint.MaxValue;
        var needsCompressed = compressed == uint.MaxValue;
        var needsOffset = offset == uint.MaxValue;
        var needsDisk = disk == ushort.MaxValue;
        var found = false;
        while (!extras.IsEmpty)
        {
            if (extras.Length < 4)
                throw PortableZipMetadata.Invalid("Truncated ZIP extra field.");
            var id = PortableZipMetadata.U16(extras, 0);
            var size = PortableZipMetadata.U16(extras, 2);
            if (size > extras.Length - 4)
                throw PortableZipMetadata.Invalid("ZIP extra field overruns its record.");
            if (id == 1)
            {
                if (found)
                    throw PortableZipMetadata.Invalid("Duplicate ZIP64 extra field.");
                found = true;
                // Native has a permissive >=28-byte "all fields" path that can skip surplus
                // sizes before an offset. Only accept the canonical sentinel-requested form,
                // so neither parser can substitute a different local header offset.
                var requiredSize = (needsLength ? 8 : 0) + (needsCompressed ? 8 : 0)
                    + (needsOffset ? 8 : 0) + (needsDisk ? 4 : 0);
                if (requiredSize == 0 || size != requiredSize)
                    throw PortableZipMetadata.Invalid("ZIP64 extra must contain exactly its sentinel replacement fields.");
                var values = extras.Slice(4, size);
                if (needsLength) length = Take64(ref values);
                if (needsCompressed) compressed = Take64(ref values);
                if (needsOffset) offset = Take64(ref values);
                if (needsDisk)
                {
                    if (values.Length < 4)
                        throw PortableZipMetadata.Invalid("Missing ZIP64 disk replacement.");
                    disk = PortableZipMetadata.U32(values, 0);
                }
            }
            extras = extras[(4 + size)..];
        }
        if (!found && (needsLength || needsCompressed || needsOffset || needsDisk))
            throw PortableZipMetadata.Invalid("Missing required ZIP64 extra field.");
    }
    private static long Take64(ref ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            throw PortableZipMetadata.Invalid("Truncated ZIP64 replacement value.");
        var value = PortableZipMetadata.U64(bytes, 0);
        bytes = bytes[8..];
        return value;
    }
}

internal sealed record PortableZipEntryLayout(string Path, long LocalHeaderOffset, long DataOffset, long CompressedDataEnd, long PhysicalEnd);

internal static class PortableZipEntryLayoutValidator
{
    public static async Task<IReadOnlyList<PortableZipEntryLayout>> ValidateAsync(
        PortableArchiveRangeCache cache, PortableArchiveBufferBudget budget,
        IReadOnlyList<PortableZipDirectoryEntry> entries, long centralDirectoryOffset, CancellationToken ct)
    {
        using var scratch = await budget.RentAsync(30 + 2 * ushort.MaxValue, ct).ConfigureAwait(false);
        var layouts = new List<PortableZipEntryLayout>();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            PortableZipMetadata.RequireRange(entry.LocalHeaderOffset, 30, centralDirectoryOffset);
            await cache.ReadAsync(entry.LocalHeaderOffset, scratch.Memory[..30], ct).ConfigureAwait(false);
            var variableLength = ValidatePrefix(scratch.Memory.Span, entry);
            PortableZipMetadata.RequireRange(entry.LocalHeaderOffset, 30 + variableLength, centralDirectoryOffset);
            if (variableLength > 0)
                await cache.ReadAsync(entry.LocalHeaderOffset + 30, scratch.Memory.Slice(30, variableLength), ct).ConfigureAwait(false);
            ValidateLocal(scratch.Memory.Span, entry);
            var dataOffset = entry.LocalHeaderOffset + 30 + variableLength;
            PortableZipMetadata.RequireRange(dataOffset, entry.CompressedLength, centralDirectoryOffset);
            var end = dataOffset + entry.CompressedLength;
            var physicalEnd = end;
            if ((entry.Flags & 8) != 0)
            {
                var available = (int)Math.Min(24, centralDirectoryOffset - end);
                if (available < 12)
                    throw PortableZipMetadata.Invalid("Truncated ZIP data descriptor.");
                await cache.ReadAsync(end, scratch.Memory[..available], ct).ConfigureAwait(false);
                physicalEnd += DescriptorLength(scratch.Memory.Span[..available], entry);
            }
            layouts.Add(new(entry.Path, entry.LocalHeaderOffset, dataOffset, end, physicalEnd));
        }
        var ordered = layouts.OrderBy(x => x.LocalHeaderOffset).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var next = i + 1 < ordered.Length ? ordered[i + 1].LocalHeaderOffset : centralDirectoryOffset;
            if (ordered[i].PhysicalEnd > next)
                throw PortableZipMetadata.Invalid("ZIP physical entry ranges overlap.");
        }
        return layouts;
    }
    private static int ValidatePrefix(ReadOnlySpan<byte> bytes, PortableZipDirectoryEntry entry)
    {
        if (PortableZipMetadata.U32(bytes, 0) != 0x04034b50
            || PortableZipMetadata.U16(bytes, 6) != entry.Flags
            || PortableZipMetadata.U16(bytes, 8) != entry.CompressionMethod)
            throw PortableZipMetadata.Invalid("Local and central ZIP headers disagree.");
        return PortableZipMetadata.U16(bytes, 26) + PortableZipMetadata.U16(bytes, 28);
    }
    private static void ValidateLocal(ReadOnlySpan<byte> bytes, PortableZipDirectoryEntry entry)
    {
        var nameLength = PortableZipMetadata.U16(bytes, 26);
        var extraLength = PortableZipMetadata.U16(bytes, 28);
        if (!bytes.Slice(30, nameLength).SequenceEqual(entry.NameBytes.Span))
            throw PortableZipMetadata.Invalid("Local and central ZIP names disagree.");
        long length = PortableZipMetadata.U32(bytes, 22), compressed = PortableZipMetadata.U32(bytes, 18), offset = 0, disk = 0;
        PortableZipDirectoryParser.ApplyZip64(bytes.Slice(30 + nameLength, extraLength), ref length, ref compressed, ref offset, ref disk);
        if ((entry.Flags & 8) == 0 && (length != entry.Length || compressed != entry.CompressedLength
            || PortableZipMetadata.U32(bytes, 14) != entry.Crc32))
            throw PortableZipMetadata.Invalid("Local and central ZIP sizes or CRC disagree.");
    }
    private static int DescriptorLength(ReadOnlySpan<byte> bytes, PortableZipDirectoryEntry entry)
    {
        // Prefer the longest matching form (including zero-length ZIP64 entries). Try both
        // signature interpretations because a CRC may itself equal the optional signature.
        foreach (var prefix in new[] { 4, 0 })
        {
            if (prefix == 4 && PortableZipMetadata.U32(bytes, 0) != 0x08074b50)
                continue;
            if (bytes.Length < prefix + 12 || PortableZipMetadata.U32(bytes, prefix) != entry.Crc32)
                continue;
            // Read unsigned sizes here so a failed classic interpretation cannot throw first.
            if (bytes.Length >= prefix + 20
                && BinaryPrimitives.ReadUInt64LittleEndian(bytes[(prefix + 4)..]) == (ulong)entry.CompressedLength
                && BinaryPrimitives.ReadUInt64LittleEndian(bytes[(prefix + 12)..]) == (ulong)entry.Length)
                return prefix + 20;
            if (entry.Length < uint.MaxValue && entry.CompressedLength < uint.MaxValue
                && PortableZipMetadata.U32(bytes, prefix + 4) == entry.CompressedLength
                && PortableZipMetadata.U32(bytes, prefix + 8) == entry.Length)
                return prefix + 12;
        }
        throw PortableZipMetadata.Invalid("ZIP data descriptor disagrees with the central directory.");
    }
}
