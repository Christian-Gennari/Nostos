using System.Collections.Concurrent;
using Nostos.Backend.Services;

namespace Nostos.Product.Alignment;

/// <summary>
/// Decides whether an MP3 is constant bitrate, from a few small reads.
///
/// An MP3 has no index. To reach a time exactly, ffmpeg reads every frame
/// from the first byte, which on a long single-file audiobook is hundreds of
/// megabytes per clip. In a constant-bitrate file a time is a byte offset by
/// arithmetic, and ffmpeg's fast seek is exact. In a variable-bitrate file the
/// same seek is a guess that can be minutes off, so the answer here must never
/// be "constant" for a file that is not: every doubt is answered "no", and the
/// caller keeps the exact seek.
/// </summary>
public static class Mp3ConstantBitrate
{
    public const int Windows = 8;
    public const int WindowBytes = 8192;

    /// <summary>A window must hold this many frames in an unbroken chain.</summary>
    public const int MinimumFramesPerWindow = 4;

    private const int MaximumCachedFiles = 256;

    private static readonly ConcurrentDictionary<string, bool> Known =
        new(StringComparer.Ordinal);

    private static readonly int[] Version1Kbps =
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];

    private static readonly int[] Version2Kbps =
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    public readonly record struct Frame(
        int Version,
        int SampleRate,
        int Kbps,
        int Length);

    public static bool IsMp3(string? fileName) =>
        fileName is not null &&
        fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True only when frames sampled across the whole file all carry one
    /// bitrate, the file declares no variable-bitrate header, and that bitrate
    /// accounts for the stored duration. The answer is remembered per stored
    /// revision of the file.
    /// </summary>
    public static async Task<bool> IsConstantAsync(
        StoredAssetInfo info,
        double? durationSeconds,
        Func<StorageByteRange, CancellationToken, Task<StoredAssetRead?>> open,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(open);

        if (durationSeconds is not { } duration ||
            !double.IsFinite(duration) ||
            duration <= 0 ||
            info.Length < (long)Windows * WindowBytes * 2)
            return false;

        var key = string.IsNullOrWhiteSpace(info.EntityTag)
            ? null
            : $"{info.EntityTag}:{info.Length}:{Math.Round(duration)}";

        if (key is not null && Known.TryGetValue(key, out var known))
            return known;

        bool constant;

        try
        {
            constant = await ProbeAsync(info.Length, duration, open, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // An unreadable sample proves nothing. Not remembered: storage may
            // answer the next time.
            return false;
        }

        if (key is not null)
        {
            if (Known.Count >= MaximumCachedFiles)
                Known.Clear();

            Known[key] = constant;
        }

        return constant;
    }

    private static async Task<bool> ProbeAsync(
        long length,
        double duration,
        Func<StorageByteRange, CancellationToken, Task<StoredAssetRead?>> open,
        CancellationToken ct)
    {
        var head = await ReadAsync(0, length, open, ct);

        if (head is null)
            return false;

        var audioStart = Id3v2Length(head);

        if (audioStart < 0 ||
            length - audioStart < (long)Windows * WindowBytes * 2)
            return false;

        // The last window stops short of the end: an ID3v1 or APE tag there is
        // not audio.
        var span = length - audioStart - 2L * WindowBytes;
        var reads = new Task<byte[]?>[Windows];

        for (var i = 0; i < Windows; i++)
        {
            var offset = audioStart + span * i / (Windows - 1);
            reads[i] = ReadAsync(offset, length, open, ct);
        }

        var windows = await Task.WhenAll(reads);
        Frame? first = null;

        for (var i = 0; i < Windows; i++)
        {
            if (windows[i] is not { } window)
                return false;

            // The first frame of a variable-bitrate file announces itself.
            if (i == 0 && DeclaresVariableBitrate(window))
                return false;

            var frames = Chain(window, out var chainStart);

            // An encoder's "Info" frame holds no audio and is often written at
            // another bitrate than the audio that follows it.
            if (i == 0 && frames.Count > 0 &&
                window.AsSpan(chainStart, Math.Min(frames[0].Length, window.Length - chainStart))
                    .IndexOf("Info"u8) >= 0)
            {
                audioStart += chainStart + frames[0].Length;
                frames = frames.Skip(1).ToList();
            }

            if (frames.Count < MinimumFramesPerWindow)
                return false;

            first ??= frames[0];

            foreach (var frame in frames)
            {
                if (frame.Version != first.Value.Version ||
                    frame.SampleRate != first.Value.SampleRate ||
                    frame.Kbps != first.Value.Kbps)
                    return false;
            }
        }

        // Eight windows of equal frames could still be a variable-bitrate file
        // sampled where it happens to agree. Then its length would not be what
        // this bitrate makes of the duration.
        var expected = (length - audioStart) * 8d / (first!.Value.Kbps * 1000d);

        return Math.Abs(expected - duration) <= Math.Max(2, duration * 0.002);
    }

    /// <summary>
    /// The bytes an ID3v2 tag occupies at the start of the file, 0 when there
    /// is none, -1 when the tag is malformed.
    /// </summary>
    public static long Id3v2Length(ReadOnlySpan<byte> head)
    {
        if (head.Length < 10 ||
            head[0] != (byte)'I' || head[1] != (byte)'D' || head[2] != (byte)'3')
            return 0;

        if ((head[6] | head[7] | head[8] | head[9]) >= 0x80)
            return -1;

        var size = (head[6] << 21) | (head[7] << 14) | (head[8] << 7) | head[9];
        var footer = (head[5] & 0x10) != 0 ? 10 : 0;

        return 10L + size + footer;
    }

    /// <summary>A Layer III frame header at the start of the span, or null.</summary>
    public static Frame? ParseFrame(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 ||
            bytes[0] != 0xFF ||
            (bytes[1] & 0xE0) != 0xE0)
            return null;

        var version = (bytes[1] >> 3) & 3;   // 3: MPEG 1, 2: MPEG 2, 0: MPEG 2.5
        var layer = (bytes[1] >> 1) & 3;     // 1: Layer III
        var bitrateIndex = bytes[2] >> 4;
        var rateIndex = (bytes[2] >> 2) & 3;
        var padding = (bytes[2] >> 1) & 1;

        // Index 0 is "free format": the bitrate is not stated in the frame.
        if (version == 1 || layer != 1 ||
            bitrateIndex is 0 or 15 || rateIndex == 3)
            return null;

        var kbps = (version == 3 ? Version1Kbps : Version2Kbps)[bitrateIndex];

        var sampleRate = version switch
        {
            3 => new[] { 44100, 48000, 32000 }[rateIndex],
            2 => new[] { 22050, 24000, 16000 }[rateIndex],
            _ => new[] { 11025, 12000, 8000 }[rateIndex],
        };

        var frameLength =
            (version == 3 ? 144 : 72) * kbps * 1000 / sampleRate + padding;

        return new Frame(version, sampleRate, kbps, frameLength);
    }

    /// <summary>
    /// The longest run of frames in the window where each frame ends exactly
    /// where the next begins. Bytes that only look like a header do not chain.
    /// </summary>
    public static IReadOnlyList<Frame> Chain(ReadOnlySpan<byte> window) =>
        Chain(window, out _);

    public static IReadOnlyList<Frame> Chain(
        ReadOnlySpan<byte> window,
        out int start)
    {
        for (start = 0; start + 4 <= window.Length; start++)
        {
            if (ParseFrame(window[start..]) is null)
                continue;

            var frames = new List<Frame>();
            var at = start;

            while (at + 4 <= window.Length &&
                   ParseFrame(window[at..]) is { } frame)
            {
                frames.Add(frame);
                at += frame.Length;
            }

            // The chain must run to the end of the window: a break in the
            // middle is not a stream of frames.
            if (frames.Count >= MinimumFramesPerWindow &&
                at + 4 > window.Length)
                return frames;
        }

        start = 0;
        return [];
    }

    private static bool DeclaresVariableBitrate(ReadOnlySpan<byte> window)
    {
        // "Xing" and "VBRI" sit inside the first frame. "Info" is the same
        // header on a constant-bitrate file.
        var first = window[..Math.Min(window.Length, 2048)];

        return first.IndexOf("Xing"u8) >= 0 || first.IndexOf("VBRI"u8) >= 0;
    }

    private static async Task<byte[]?> ReadAsync(
        long offset,
        long length,
        Func<StorageByteRange, CancellationToken, Task<StoredAssetRead?>> open,
        CancellationToken ct)
    {
        var end = Math.Min(length, offset + WindowBytes) - 1;

        if (end < offset)
            return null;

        await using var read = await open(
            StorageByteRange.Create(offset, end, length), ct);

        if (read is null)
            return null;

        var buffer = new byte[end - offset + 1];
        var filled = 0;

        while (filled < buffer.Length)
        {
            var count = await read.Content.ReadAsync(
                buffer.AsMemory(filled), ct);

            if (count == 0)
                return null;

            filled += count;
        }

        return buffer;
    }
}
