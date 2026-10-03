using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Nostos.Backend.Providers.Acquisition.Media;

/// <summary>
/// Configuration for the external media tooling that audiobook normalization
/// needs.
/// </summary>
public sealed class MediaToolOptions
{
    public const string SectionName = "Media";

    /// <summary>Absolute path to ffmpeg. Defaults to resolving it on PATH.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Absolute path to ffprobe. Defaults to resolving it on PATH.</summary>
    public string? FfprobePath { get; set; }

    /// <summary>Ceiling for a single encode. A 13-hour audiobook takes 8-10 minutes on a modest CPU.</summary>
    public int EncodeTimeoutMinutes { get; set; } = 180;
}

/// <summary>
/// Whether the media tooling is present, and if not, why not.
///
/// Exposed so the prerequisite can be *detected* rather than assumed: a missing
/// ffmpeg has to produce a clear, actionable message at the point of use instead
/// of an obscure process-start failure (or, worse, a silently truncated file).
/// </summary>
public sealed record MediaToolAvailability(bool Available, string? FfmpegPath, string? FfprobePath, string? Problem)
{
    public static MediaToolAvailability Missing(string problem) => new(false, null, null, problem);
}

public interface IMediaProcessRunner
{
    MediaToolAvailability Availability { get; }

    /// <summary>
    /// The real duration of a media file, as reported by ffprobe. Null when the
    /// file cannot be probed.
    /// </summary>
    Task<TimeSpan?> ProbeDurationAsync(string path, CancellationToken ct);

    /// <summary>
    /// Number of chapters ffprobe can see in a file. Null when it cannot be
    /// determined — used to verify that chapter metadata actually landed in the
    /// file we produced rather than only in our intentions.
    /// </summary>
    Task<int?> ProbeChapterCountAsync(string path, CancellationToken ct);

    /// <summary>
    /// Runs ffmpeg. Arguments are passed as a list, never as a command line, so
    /// nothing in a filename or a chapter title can become a shell token.
    /// Throws <see cref="MediaToolException"/> on a non-zero exit.
    /// </summary>
    Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}

public sealed class MediaToolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public const string NotInstalled = "media_tool_missing";
    public const string TimedOut = "media_tool_timeout";
    public const string Failed = "media_tool_failed";
}

public sealed class MediaProcessRunner : IMediaProcessRunner
{
    private readonly MediaToolOptions _options;
    private readonly ILogger<MediaProcessRunner> _logger;

    public MediaProcessRunner(IOptions<MediaToolOptions> options, ILogger<MediaProcessRunner> logger)
    {
        _options = options.Value;
        _logger = logger;

        var ffmpeg = Resolve(_options.FfmpegPath, "ffmpeg");
        var ffprobe = Resolve(_options.FfprobePath, "ffprobe");

        Availability = ffmpeg is null || ffprobe is null
            ? MediaToolAvailability.Missing(
                "Importing a LibriVox recording needs ffmpeg and ffprobe on the server's PATH " +
                "(or Media:FfmpegPath / Media:FfprobePath set to their full paths). " +
                $"ffmpeg={(ffmpeg ?? "not found")}, ffprobe={(ffprobe ?? "not found")}.")
            : new MediaToolAvailability(true, ffmpeg, ffprobe, null);
    }

    public MediaToolAvailability Availability { get; }

    public async Task<TimeSpan?> ProbeDurationAsync(string path, CancellationToken ct)
    {
        var output = await RunCaptureAsync(
            Require(Availability.FfprobePath),
            ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path],
            TimeSpan.FromMinutes(2),
            ct);

        var text = output.Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    public async Task<int?> ProbeChapterCountAsync(string path, CancellationToken ct)
    {
        var output = await RunCaptureAsync(
            Require(Availability.FfprobePath),
            ["-v", "error", "-show_chapters", "-print_format", "json", path],
            TimeSpan.FromMinutes(2),
            ct);

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(output);
            return document.RootElement.TryGetProperty("chapters", out var chapters)
                ? chapters.GetArrayLength()
                : 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var executable = Require(Availability.FfmpegPath);
        // -nostdin keeps ffmpeg from consuming the service's standard input.
        var full = new List<string> { "-hide_banner", "-nostdin", "-y" };
        full.AddRange(arguments);

        await RunCaptureAsync(executable, full, TimeSpan.FromMinutes(Math.Max(1, _options.EncodeTimeoutMinutes)), ct);
    }

    private async Task<string> RunCaptureAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // ArgumentList, never a concatenated command line: a chapter title or a
        // path can then never be interpreted as a shell token.
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
            throw new MediaToolException(MediaToolException.Failed, $"Could not start {executable}.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        var standardError = process.StandardError.ReadToEndAsync(deadline.Token);
        var standardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);

            if (ct.IsCancellationRequested)
                throw;

            throw new MediaToolException(
                MediaToolException.TimedOut,
                $"{Path.GetFileName(executable)} did not finish within {timeout.TotalMinutes:0} minutes.");
        }

        var error = await SafeAwait(standardError);
        var output = await SafeAwait(standardOutput);

        if (process.ExitCode != 0)
        {
            // Only a bounded tail of the log: ffmpeg's stderr for a long encode
            // is megabytes of per-frame progress, and none of it helps.
            var tail = Tail(error, 1200);
            _logger.LogError(
                "{Tool} failed with exit code {ExitCode}. stderr tail: {Tail}",
                Path.GetFileName(executable), process.ExitCode, tail);

            throw new MediaToolException(
                MediaToolException.Failed,
                $"{Path.GetFileName(executable)} failed (exit code {process.ExitCode}).");
        }

        return output;
    }

    /// <summary>
    /// Kills the whole process tree. Without this, cancelling an encode leaves
    /// ffmpeg's own children running on a long-lived server.
    /// </summary>
    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process is already gone, or cannot be signalled. Either way
            // there is nothing further to do here.
        }
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string Tail(string value, int length) =>
        value.Length <= length ? value : value[^length..];

    private static string Require(string? path) =>
        path ?? throw new MediaToolException(
            MediaToolException.NotInstalled,
            "ffmpeg/ffprobe are not available on this server.");

    /// <summary>
    /// Resolves one tool: an explicit path wins, otherwise the PATH is searched
    /// for a real executable file, so availability is a fact rather than an
    /// assumption.
    /// </summary>
    private static string? Resolve(string? configured, string name)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing startup over.
            }
        }

        return null;
    }
}
