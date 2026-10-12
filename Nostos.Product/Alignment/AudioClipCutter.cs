using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;

namespace Nostos.Product.Alignment;

public sealed class AudioClipException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}

public static class AudioClipErrorCodes
{
    public const string Unavailable = "alignment_ffmpeg_unavailable";
    public const string InvalidPosition = "alignment_invalid_position";
    public const string MediaMissing = "alignment_media_missing";
    public const string DecodeFailed = "alignment_audio_decode_failed";
    public const string Timeout = "alignment_audio_timeout";
}

public sealed record AudioClip(Stream Audio, double DurationSeconds)
    : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Audio.DisposeAsync();
}

public interface IAudioClipCutter
{
    Task<AudioClip> CutAsync(
        AudioBookModel book,
        double positionSeconds,
        double clipSeconds,
        CancellationToken ct = default);
}

/// <summary>
/// Extracts a short PCM clip through ffmpeg using seekable HTTP ranges
/// backed exclusively by the host's storage interfaces.
/// </summary>
public sealed class FfmpegAudioClipCutter(
    IBookAssetStorage assets,
    IServiceProvider services) : IAudioClipCutter
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    private const int BytesPerSecond =
        SampleRate * Channels * BitsPerSample / 8;

    public const int MaxPcmBytes = 4 * 1024 * 1024;
    public const int MaxStderrBytes = 8 * 1024;

    private static readonly TimeSpan Timeout =
        TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ExitGrace =
        TimeSpan.FromSeconds(3);

    public async Task<AudioClip> CutAsync(
        AudioBookModel book,
        double positionSeconds,
        double clipSeconds,
        CancellationToken ct = default)
    {
        if (!double.IsFinite(positionSeconds) ||
            positionSeconds <= 0 ||
            !double.IsFinite(clipSeconds) ||
            clipSeconds <= 0 ||
            clipSeconds > 60)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.InvalidPosition,
                "The requested audio position or clip duration is invalid.");
        }

        var tracks = BookTrackList.Parse(book.FileDetails.TracksJson);

        if (tracks.Count == 0)
        {
            ValidateSingleFilePosition(
                book.Duration,
                positionSeconds);
        }

        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(ct);

        deadline.CancelAfter(Timeout);

        var started = Stopwatch.GetTimestamp();

        try
        {
            var clip = tracks.Count > 0
                ? await CutTracksAsync(
                    book.Id,
                    tracks,
                    positionSeconds,
                    clipSeconds,
                    deadline.Token)
                : await CutSingleAsync(
                    book.Id,
                    TryParseDurationSeconds(book.Duration, out var bookSeconds)
                        ? bookSeconds
                        : null,
                    positionSeconds,
                    clipSeconds,
                    deadline.Token);

            // Where a switch between editions spends its time is otherwise
            // invisible: only the transcription call is logged by its client.
            (services.GetService(typeof(ILogger<FfmpegAudioClipCutter>))
                as ILogger)?.LogInformation(
                "Alignment clip for audio book {AudioBookId} ending at " +
                "{PositionSeconds} s was cut in {ElapsedMs} ms.",
                book.Id,
                Math.Round(positionSeconds),
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            return clip;
        }
        catch (OperationCanceledException)
            when (!ct.IsCancellationRequested)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.Timeout,
                "Audio clip extraction exceeded its timeout.");
        }
    }

    public static void ValidateSingleFilePosition(
        string? storedDuration,
        double positionSeconds)
    {
        if (!double.IsFinite(positionSeconds) ||
            positionSeconds <= 0)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.InvalidPosition,
                "A positive, finite playback position is required.");
        }

        if (TryParseDurationSeconds(storedDuration, out var durationSeconds) &&
            positionSeconds > durationSeconds)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.InvalidPosition,
                "The playback position exceeds the audiobook duration.");
        }
    }

    // Stored durations are "h:mm:ss" (or "m:ss") and can exceed 24 hours, which
    // TimeSpan.Parse reads as days. An unparseable duration means "unknown".
    private static bool TryParseDurationSeconds(string? stored, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(stored))
            return false;

        var parts = stored.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
            return false;

        foreach (var part in parts)
        {
            if (!double.TryParse(
                    part,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return false;
            }

            seconds = seconds * 60 + value;
        }

        return seconds > 0;
    }

    private async Task<AudioClip> CutTracksAsync(
        Guid bookId,
        IReadOnlyList<BookTrack> tracks,
        double positionSeconds,
        double clipSeconds,
        CancellationToken ct)
    {
        IReadOnlyList<AudioClipSegment> plan;

        try
        {
            plan = AudioClipPlanner.Plan(
                tracks, positionSeconds, clipSeconds);
        }
        catch (ArgumentException)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.InvalidPosition,
                "The audiobook position or track layout is invalid.");
        }

        if (plan.Count == 0)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.InvalidPosition,
                "The requested position contains no audio.");
        }

        var storage = services.GetService(typeof(IBookTrackStorage))
            as IBookTrackStorage;

        if (storage is null)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.MediaMissing,
                "Multi-track storage is not available.");
        }

        using var pcm = new MemoryStream();

        foreach (var segment in plan)
        {
            ct.ThrowIfCancellationRequested();

            var info = await storage.GetTrackInfoAsync(
                bookId,
                segment.TrackNumber,
                ct);

            if (info is null || info.Length <= 0)
            {
                throw new AudioClipException(
                    AudioClipErrorCodes.MediaMissing,
                    "The requested audiobook track is unavailable.");
            }

            Task<StoredAssetRead?> Open(
                StorageByteRange range, CancellationToken token) =>
                storage.OpenTrackAsync(
                    bookId,
                    segment.TrackNumber,
                    range,
                    token);

            var track = tracks.FirstOrDefault(
                candidate => candidate.Number == segment.TrackNumber);

            var fastSeek = await CanSeekFastAsync(
                info,
                track is null ? null : track.DurationMs / 1000d,
                segment.StartSeconds,
                Open,
                ct);

            await using var server = await LoopbackRangeServer.StartAsync(
                info.Length,
                Open,
                ct);

            await DecodeAsync(
                server.Url,
                pcm,
                segment.StartSeconds,
                segment.DurationSeconds,
                fastSeek,
                ct);
        }

        return CreateWav(pcm);
    }

    private async Task<AudioClip> CutSingleAsync(
        Guid bookId,
        double? bookSeconds,
        double positionSeconds,
        double clipSeconds,
        CancellationToken ct)
    {
        var duration = Math.Min(positionSeconds, clipSeconds);
        var start = positionSeconds - duration;

        var info = await assets.GetBookFileInfoAsync(bookId, ct);

        if (info is null || info.Length <= 0)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.MediaMissing,
                "The audiobook media is missing.");
        }

        Task<StoredAssetRead?> Open(
            StorageByteRange range, CancellationToken token) =>
            assets.OpenBookFileAsync(bookId, range, token);

        var fastSeek = await CanSeekFastAsync(
            info, bookSeconds, start, Open, ct);

        await using var server = await LoopbackRangeServer.StartAsync(
            info.Length,
            Open,
            ct);

        using var pcm = new MemoryStream();

        await DecodeAsync(
            server.Url,
            pcm,
            start,
            duration,
            fastSeek,
            ct);

        return CreateWav(pcm);
    }

    public static void ValidatePcmLength(long length)
    {
        if (length < 0 || length > MaxPcmBytes)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.DecodeFailed,
                "The decoded audio clip exceeds the permitted size.");
        }
    }

    private static AudioClip CreateWav(MemoryStream pcm)
    {
        ValidatePcmLength(pcm.Length);

        if (pcm.Length == 0)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.DecodeFailed,
                "ffmpeg did not produce a usable audio clip.");
        }

        var output = new MemoryStream(
            checked((int)pcm.Length + 44));

        Span<byte> header = stackalloc byte[44];

        "RIFF"u8.CopyTo(header);

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[4..], checked((uint)(pcm.Length + 36)));

        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);

        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], BytesPerSecond);

        BinaryPrimitives.WriteUInt16LittleEndian(
            header[32..], Channels * BitsPerSample / 8);

        BinaryPrimitives.WriteUInt16LittleEndian(
            header[34..], BitsPerSample);

        "data"u8.CopyTo(header[36..]);

        BinaryPrimitives.WriteUInt32LittleEndian(
            header[40..], checked((uint)pcm.Length));

        output.Write(header);

        pcm.Position = 0;
        pcm.CopyTo(output);

        output.Position = 0;

        return new AudioClip(
            output,
            pcm.Length / (double)BytesPerSecond);
    }

    /// <summary>
    /// ffmpeg reaches a time in an MP3 by reading every frame before it. A
    /// constant-bitrate file needs none of that: the time is a byte offset.
    /// Anything else, and any MP3 not proven constant, keeps the exact seek.
    /// </summary>
    private static async Task<bool> CanSeekFastAsync(
        StoredAssetInfo info,
        double? durationSeconds,
        double offsetSeconds,
        Func<StorageByteRange, CancellationToken, Task<StoredAssetRead?>> open,
        CancellationToken ct) =>
        offsetSeconds > 0 &&
        Mp3ConstantBitrate.IsMp3(info.FileName) &&
        await Mp3ConstantBitrate.IsConstantAsync(
            info, durationSeconds, open, ct);

    public static string[] DecodeArguments(
        Uri input,
        double offsetSeconds,
        double durationSeconds,
        bool fastSeek)
    {
        static string Seconds(double value) =>
            value.ToString("0.######", CultureInfo.InvariantCulture);

        string[] seek = fastSeek ? ["-fflags", "+fastseek"] : [];

        return
        [
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-protocol_whitelist", "file,http,tcp",
            .. seek,
            "-ss", Seconds(offsetSeconds),
            "-i", input.AbsoluteUri,
            "-t", Seconds(durationSeconds),
            "-vn",
            "-ac", "1",
            "-ar", "16000",
            "-f", "s16le",
            "-acodec", "pcm_s16le",
            "pipe:1",
        ];
    }

    private static async Task DecodeAsync(
        Uri input,
        MemoryStream output,
        double offsetSeconds,
        double durationSeconds,
        bool fastSeek,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in DecodeArguments(
                     input, offsetSeconds, durationSeconds, fastSeek))
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new AudioClipException(
                    AudioClipErrorCodes.Unavailable,
                    "ffmpeg could not be started.");
            }
        }
        catch (Win32Exception)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.Unavailable,
                "ffmpeg is not installed or cannot be started.");
        }

        using var cancellation = ct.Register(
            () => KillProcessTree(process));

        var readOutput = ReadOutputAsync(
            process.StandardOutput.BaseStream,
            output,
            ct);

        var readErrors = DrainStderrAsync(
            process.StandardError.BaseStream,
            ct);

        try
        {
            await Task.WhenAll(
                readOutput,
                readErrors,
                process.WaitForExitAsync(ct));

            if (process.ExitCode != 0)
            {
                throw new AudioClipException(
                    AudioClipErrorCodes.DecodeFailed,
                    "ffmpeg could not decode the requested audiobook segment.");
            }
        }
        catch
        {
            KillProcessTree(process);

            // Process exit is deliberately bounded even when the original
            // request was cancelled. Never wait indefinitely in cleanup.
            try
            {
                await process.WaitForExitAsync(
                    CancellationToken.None).WaitAsync(ExitGrace);
            }
            catch (TimeoutException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            // Both readers were already started. Observe completed faults,
            // but do not await a permanently blocked pipe.
            _ = readOutput.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            _ = readErrors.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            throw;
        }
    }

    private static async Task ReadOutputAsync(
        Stream source,
        MemoryStream destination,
        CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);

            if (read == 0)
                break;

            ValidatePcmLength(destination.Length + read);

            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// Drains stderr concurrently without accumulating arbitrary provider
    /// output. Only the final 8 KiB are retained in process memory.
    /// The retained text is deliberately never sent to API clients.
    /// </summary>
    private static async Task<byte[]> DrainStderrAsync(
        Stream source,
        CancellationToken ct)
    {
        var tail = new byte[MaxStderrBytes];
        var buffer = new byte[4096];
        var written = 0;

        while (true)
        {
            var count = await source.ReadAsync(buffer, ct);

            if (count == 0)
                break;

            if (count >= tail.Length)
            {
                buffer.AsSpan(count - tail.Length, tail.Length)
                    .CopyTo(tail);
                written = tail.Length;
                continue;
            }

            if (written + count > tail.Length)
            {
                var discard = written + count - tail.Length;

                tail.AsSpan(discard, written - discard)
                    .CopyTo(tail);

                written -= discard;
            }

            buffer.AsSpan(0, count)
                .CopyTo(tail.AsSpan(written));

            written += count;
        }

        return tail[..written];
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
