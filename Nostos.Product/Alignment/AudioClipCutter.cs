using System.Buffers.Binary;
using System.Diagnostics;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;

namespace Nostos.Product.Alignment;

public sealed class AudioClipException(string code, string message) : Exception(message)
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
/// Decodes only the requested portions of an audiobook through ffmpeg.
/// Input comes from the storage abstraction; no absolute library paths
/// are passed to child processes.
///
/// Each segment produces raw PCM, not a standalone WAV. The segments
/// are concatenated and given exactly one WAV header.
/// </summary>
public sealed class FfmpegAudioClipCutter(
    IBookAssetStorage assets,
    IServiceProvider services) : IAudioClipCutter
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async Task<AudioClip> CutAsync(
        AudioBookModel book,
        double positionSeconds,
        double clipSeconds,
        CancellationToken ct = default)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0 ||
            !double.IsFinite(clipSeconds) || clipSeconds <= 0 ||
            clipSeconds > 60)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.InvalidPosition,
                "The requested audio position or clip duration is invalid.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);

        try
        {
            var tracks = BookTrackList.Parse(book.FileDetails.TracksJson);

            if (tracks.Count > 0)
                return await CutTracksAsync(
                    book.Id, tracks, positionSeconds, clipSeconds, deadline.Token);

            return await CutSingleAsync(
                book.Id, positionSeconds, clipSeconds, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.Timeout,
                "Audio clip extraction exceeded its timeout.");
        }
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
            plan = AudioClipPlanner.Plan(tracks, positionSeconds, clipSeconds);
        }
        catch (ArgumentException ex)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.InvalidPosition, ex.Message);
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

            await using var source = await storage.OpenTrackAsync(
                bookId, segment.TrackNumber, ct: ct);

            if (source is null)
            {
                throw new AudioClipException(
                    AudioClipErrorCodes.MediaMissing,
                    $"Audiobook track {segment.TrackNumber} is missing.");
            }

            await DecodeAsync(
                source.Content,
                pcm,
                segment.StartSeconds,
                segment.DurationSeconds,
                ct);
        }

        return CreateWav(pcm);
    }

    private async Task<AudioClip> CutSingleAsync(
        Guid bookId,
        double positionSeconds,
        double clipSeconds,
        CancellationToken ct)
    {
        var duration = Math.Min(positionSeconds, clipSeconds);

        if (duration <= 0)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.InvalidPosition,
                "The requested position contains no preceding audio.");
        }

        var start = positionSeconds - duration;

        await using var source = await assets.OpenBookFileAsync(
            bookId, ct: ct);

        if (source is null)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.MediaMissing,
                "The audiobook media is missing.");
        }

        using var pcm = new MemoryStream();

        await DecodeAsync(
            source.Content, pcm, start, duration, ct);

        return CreateWav(pcm);
    }

    private static AudioClip CreateWav(MemoryStream pcm)
    {
        if (pcm.Length == 0 || pcm.Length > int.MaxValue - 44)
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
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[28..], SampleRate * Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header[32..], Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], BitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[40..], checked((uint)pcm.Length));

        output.Write(header);
        pcm.Position = 0;
        pcm.CopyTo(output);
        output.Position = 0;

        return new AudioClip(
            output,
            pcm.Length / (double)(SampleRate * Channels * BitsPerSample / 8));
    }

    private static async Task DecodeAsync(
        Stream input,
        Stream output,
        double offsetSeconds,
        double durationSeconds,
        CancellationToken ct)
    {
        var args = new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-i", "pipe:0",
            "-ss", offsetSeconds.ToString("0.######",
                System.Globalization.CultureInfo.InvariantCulture),
            "-t", durationSeconds.ToString("0.######",
                System.Globalization.CultureInfo.InvariantCulture),
            "-vn",
            "-ac", "1",
            "-ar", "16000",
            "-f", "s16le",
            "-acodec", "pcm_s16le",
            "pipe:1",
        };

        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.Unavailable,
                "ffmpeg is not installed or cannot be started.");
        }

        using var cancellation = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });

        // All three pipes must be serviced concurrently. In particular,
        // waiting for stdin before draining stdout can deadlock ffmpeg.
        var writer = Task.Run(async () =>
        {
            try
            {
                await input.CopyToAsync(
                    process.StandardInput.BaseStream, ct);
            }
            catch (IOException) when (process.HasExited)
            {
                // ffmpeg may stop reading after -t.
            }
            finally
            {
                process.StandardInput.Close();
            }
        }, ct);

        var reader = process.StandardOutput.BaseStream.CopyToAsync(
            output, ct);

        var errors = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await Task.WhenAll(writer, reader, errors);
            await process.WaitForExitAsync(ct);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new AudioClipException(
                AudioClipErrorCodes.DecodeFailed,
                "ffmpeg could not decode the requested audiobook segment.");
        }
    }
}
