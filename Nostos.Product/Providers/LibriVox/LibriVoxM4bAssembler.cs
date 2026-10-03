using System.Text;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Acquisition.Media;
using Nostos.Backend.Providers.Contracts;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Providers.LibriVox;

/// <summary>
/// Combines LibriVox's ordered MP3 sections into ONE chaptered M4B.
///
/// Nostos models a book as a single primary media file and its audio reader
/// plays one stream, so the provider-specific multi-track representation is
/// absorbed here — once, at acquisition — instead of becoming a playlist
/// concept that storage, playback, progress, downloads and backups would all
/// have to learn.
///
/// Three details are load-bearing and each was verified against ffmpeg 6.1
/// before being written down:
///
/// 1. <c>-ar 44100</c> is REQUIRED. LibriVox sections are recorded by different
///    volunteers over years, so the first section may be 22.05 kHz; without an
///    explicit rate the AAC encoder adopts the first input's rate and the whole
///    thirteen hours is silently downsampled.
/// 2. Chapter spans are measured with ffprobe from the files that were actually
///    downloaded. The API's per-section playtime is rounded and drifts; across
///    forty sections that accumulates into tens of seconds of chapter misalignment.
/// 3. The ffmetadata chapter key is <c>title</c> with <c>TIMEBASE=1/1000</c> and
///    millisecond bounds. The tempting <c>CHAPTERTITLE</c> is not a tag ffmpeg
///    recognises, and it fails silently — chapters arrive with blank names.
/// </summary>
public sealed class LibriVoxM4bAssembler(
    IMediaProcessRunner media,
    ILogger<LibriVoxM4bAssembler> logger)
{
    public const string ConcatListFileName = "concat.txt";
    public const string ChaptersFileName = "chapters.txt";
    public const string OutputFileName = "result.m4b";

    private const string OutputContentType = "audio/mp4";

    /// <summary>
    /// Speech in mono AAC at 64 kbps is close to transparent, and the sources are
    /// 64 kbps MP3 — a higher bitrate cannot restore detail that was never there,
    /// it only makes the file bigger.
    /// </summary>
    private const string AudioBitrate = "64k";

    /// <summary>
    /// The media-tool vocabulary is translated into the acquisition one here.
    /// A missing ffmpeg then keeps its own code and its actionable message
    /// instead of collapsing into the pipeline's generic "unexpected failure",
    /// which would tell an operator nothing about the prerequisite the server is
    /// actually missing.
    /// </summary>
    public async Task<AcquisitionArtifact> AssembleAsync(
        AcquisitionAssemblyContext context,
        CancellationToken ct)
    {
        try
        {
            return await AssembleCoreAsync(context, ct);
        }
        catch (MediaToolException ex)
        {
            throw new AcquisitionException(ex.Code, ex.Message);
        }
    }

    private async Task<AcquisitionArtifact> AssembleCoreAsync(
        AcquisitionAssemblyContext context,
        CancellationToken ct)
    {
        if (!media.Availability.Available)
            throw new MediaToolException(
                MediaToolException.NotInstalled,
                media.Availability.Problem ?? "ffmpeg/ffprobe are not available on this server.");

        var parts = context.Parts;
        if (parts.Count == 0)
            throw new MediaToolException(MediaToolException.Failed, "There are no tracks to combine.");

        // 1. Measure the real length of every track.
        var durations = new List<TimeSpan>(parts.Count);
        foreach (var part in parts)
        {
            var duration = await media.ProbeDurationAsync(part.FilePath, ct)
                ?? throw new MediaToolException(
                    MediaToolException.Failed,
                    $"The length of track {Path.GetFileName(part.FilePath)} could not be read.");

            durations.Add(duration);
        }

        var chapters = BuildChapters(context, durations);
        var total = durations.Aggregate(TimeSpan.Zero, (sum, value) => sum + value);

        // 2. The two inputs the encoder reads: the ordered track list, and the
        //    chapter table.
        var listPath = Path.Combine(context.WorkingDirectory, ConcatListFileName);
        await File.WriteAllTextAsync(listPath, BuildConcatList(parts.Select(p => p.FilePath)), ct);

        var chaptersPath = Path.Combine(context.WorkingDirectory, ChaptersFileName);
        await File.WriteAllTextAsync(chaptersPath, BuildChapterMetadata(chapters), ct);

        // 3. One pass: decode every track, resample to a uniform rate, re-encode
        //    to AAC, and take chapter metadata from the ffmetadata file.
        var outputPath = Path.Combine(context.WorkingDirectory, OutputFileName);

        await media.RunFfmpegAsync(
        [
            "-f", "concat", "-safe", "0", "-i", listPath,
            "-i", chaptersPath,
            "-map", "0:a",
            // Both mappings are needed. -map_metadata takes the chapter table as
            // the metadata source, and -map_chapters stops ffmpeg from copying
            // chapters off the first track (which quietly wins by default).
            "-map_metadata", "1",
            "-map_chapters", "1",
            "-c:a", "aac",
            "-b:a", AudioBitrate,
            "-ac", "1",
            "-ar", "44100",
            // Without faststart the index atom sits at the end of a several-hundred
            // megabyte file, so a browser cannot seek until it has fetched the tail.
            "-movflags", "+faststart",
            // The ipod muxer is the one that writes M4A/M4B; -f mp4 would produce
            // something players treat differently.
            "-f", "ipod",
            outputPath,
        ], ct);

        // 4. Verify the file that came out, rather than trusting that ffmpeg did
        //    what it was told. A chapter table that silently failed to apply is
        //    exactly the failure this step exists to catch.
        var producedChapters = await media.ProbeChapterCountAsync(outputPath, ct);
        if (producedChapters != chapters.Count)
            throw new MediaToolException(
                MediaToolException.Failed,
                $"The finished audiobook has {producedChapters?.ToString() ?? "no readable"} chapters where {chapters.Count} were expected.");

        var producedDuration = await media.ProbeDurationAsync(outputPath, ct);
        var tolerance = Math.Max(2.0, total.TotalSeconds * 0.005);
        if (producedDuration is null || Math.Abs(producedDuration.Value.TotalSeconds - total.TotalSeconds) > tolerance)
            throw new MediaToolException(
                MediaToolException.Failed,
                $"The finished audiobook is {producedDuration?.ToString() ?? "of unknown length"} where about {total} was expected.");

        logger.LogInformation(
            "Assembled {Sections} LibriVox sections into {Output} ({Duration}, {Chapters} chapters).",
            parts.Count, Path.GetFileName(outputPath), total, chapters.Count);

        return new AcquisitionArtifact(
            FilePath: outputPath,
            FileExtension: ".m4b",
            ContentType: OutputContentType,
            Chapters: chapters.Select(c => new BookChapterDto(c.Title, c.StartMs / 1000.0)).ToList(),
            Duration: FormatDuration(producedDuration.Value));
    }

    private static List<ChapterSpan> BuildChapters(
        AcquisitionAssemblyContext context,
        IReadOnlyList<TimeSpan> durations)
    {
        var chapters = new List<ChapterSpan>(durations.Count);
        long cursorMs = 0;

        for (var index = 0; index < durations.Count; index++)
        {
            // The source's own name for the section ("Chapters 1-3") is the
            // chapter title; there is nothing to invent.
            var label = context.Plan.Parts.Count > index ? context.Plan.Parts[index].Label : null;
            var title = string.IsNullOrWhiteSpace(label) ? $"Section {index + 1}" : label.Trim();

            var lengthMs = (long)Math.Round(durations[index].TotalMilliseconds);
            chapters.Add(new ChapterSpan(title, cursorMs, cursorMs + lengthMs));
            cursorMs += lengthMs;
        }

        return chapters;
    }

    private static string BuildConcatList(IEnumerable<string> paths)
    {
        var builder = new StringBuilder();
        foreach (var path in paths)
        {
            // The concat demuxer's own quoting: a single quote inside the path is
            // written as '\''.
            builder.Append("file '").Append(path.Replace("'", @"'\''")).Append('\'').Append('\n');
        }

        return builder.ToString();
    }

    private static string BuildChapterMetadata(IReadOnlyList<ChapterSpan> chapters)
    {
        var builder = new StringBuilder();
        builder.Append(";FFMETADATA1\n");

        foreach (var chapter in chapters)
        {
            builder.Append("[CHAPTER]\n");
            builder.Append("TIMEBASE=1/1000\n");
            builder.Append("START=").Append(chapter.StartMs).Append('\n');
            builder.Append("END=").Append(chapter.EndMs).Append('\n');
            builder.Append("title=").Append(EscapeMetadata(chapter.Title)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// The ffmetadata parser treats '=', ';', '#' and '\' as syntax, so an
    /// unescaped one truncates or corrupts the value — and chapter titles come
    /// from volunteers, who write things like "Chapter 1; Part 2".
    /// </summary>
    private static string EscapeMetadata(string value)
    {
        var flattened = value.Replace('\n', ' ').Replace('\r', ' ');
        var builder = new StringBuilder(flattened.Length + 8);

        foreach (var character in flattened)
        {
            if (character is '=' or ';' or '#' or '\\')
                builder.Append('\\');

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Matches the "hh:mm:ss" the rest of the app stores in
    /// <c>AudioBookModel.Duration</c>, but without TimeSpan's 24-hour wrap — a
    /// very long recording must not come back as "02:00:00".
    /// </summary>
    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";

    private readonly record struct ChapterSpan(string Title, long StartMs, long EndMs);
}
