namespace Nostos.Backend.Services;

/// <summary>
/// CRC-32 (IEEE 802.3, the ZIP polynomial), computed incrementally so a track
/// can be checksummed while it is being written rather than read back.
/// </summary>
public struct Crc32
{
    private static readonly uint[] Table = BuildTable();

    private uint _state;

    public Crc32() => _state = 0xFFFFFFFFu;

    public readonly uint Value => ~_state;

    public void Append(ReadOnlySpan<byte> data)
    {
        var state = _state;
        foreach (var value in data)
            state = Table[(state ^ value) & 0xFF] ^ (state >> 8);
        _state = state;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = new Crc32();
        crc.Append(data);
        return crc.Value;
    }

    public static async Task<(uint Crc32, long Bytes)> ComputeAsync(Stream content, CancellationToken ct = default)
    {
        var crc = new Crc32();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            crc.Append(buffer.AsSpan(0, read));
            total += read;
        }

        return (crc.Value, total);
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var entry = i;
            for (var bit = 0; bit < 8; bit++)
                entry = (entry & 1) != 0 ? 0xEDB88320u ^ (entry >> 1) : entry >> 1;
            table[i] = entry;
        }

        return table;
    }
}
