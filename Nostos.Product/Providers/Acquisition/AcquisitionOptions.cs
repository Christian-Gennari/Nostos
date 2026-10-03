namespace Nostos.Backend.Providers.Acquisition;

/// <summary>
/// Resource bounds and location for the acquisition pipeline.
///
/// Every value here exists because acquiring real content is not like serving a
/// request: a Project Gutenberg EPUB is a few megabytes, but a LibriVox
/// audiobook is dozens of tracks and a multi-hour encode, all on the same
/// self-hosted box that serves the rest of the app.
/// </summary>
public sealed class AcquisitionOptions
{
    public const string SectionName = "Acquisition";

    /// <summary>
    /// Where per-acquisition staging directories are created. Absolute, or
    /// relative to the content root. Defaults to a sibling of the books
    /// directory (see <see cref="ResolveWorkingRoot"/>).
    /// </summary>
    public string? WorkingRoot { get; set; }

    /// <summary>
    /// Parts fetched at once. Kept low deliberately: the catalogs behind these
    /// sources throttle aggressive clients (archive.org answers a wide fan-out
    /// with 429/503), so more parallelism mostly means more failures.
    /// </summary>
    public int DownloadConcurrency { get; set; } = 3;

    /// <summary>Attempts per part, including the first. Retries 429/5xx and transient IO only.</summary>
    public int DownloadAttempts { get; set; } = 3;

    /// <summary>Deadline for a single part.</summary>
    public int DownloadTimeoutSeconds { get; set; } = 900;

    /// <summary>Ceiling for one whole acquisition.</summary>
    public int AcquisitionTimeoutMinutes { get; set; } = 240;

    public int MaxRedirects { get; set; } = 5;

    /// <summary>
    /// Transcodes running at once. One by default: encoding a feature-length
    /// audiobook already saturates a self-hosted machine, and a second
    /// concurrent encode makes the whole server unresponsive rather than merely
    /// slow.
    /// </summary>
    public int MaxConcurrentTranscodes { get; set; } = 1;

    /// <summary>
    /// Free space required before starting, as a multiple of the bytes the job
    /// expects to write (all parts plus the assembled result). Downloading and
    /// then encoding needs both sets on disk at once.
    /// </summary>
    public double FreeSpaceFactor { get; set; } = 2.5;

    public long MinimumFreeSpaceBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>How long a finished or failed job stays visible to the UI.</summary>
    public int JobRetentionMinutes { get; set; } = 180;

    /// <summary>Upper bound on retained jobs, so the manager cannot grow without limit.</summary>
    public int MaxRetainedJobs { get; set; } = 50;

    public int ClampDownloadConcurrency() => Math.Clamp(DownloadConcurrency, 1, 8);

    public int ClampDownloadAttempts() => Math.Clamp(DownloadAttempts, 1, 6);

    public int ClampTranscodeConcurrency() => Math.Clamp(MaxConcurrentTranscodes, 1, 4);

    /// <summary>
    /// The effective staging root.
    ///
    /// Deliberately a sibling of the books directory rather than
    /// <c>Path.GetTempPath()</c>: on Linux <c>/tmp</c> is frequently a
    /// RAM-backed tmpfs, and a few gigabytes of audiobook staging there would
    /// exhaust host memory and get the app OOM-killed. Keeping it beside the
    /// books directory also means the finished artifact is committed by a
    /// rename on the same volume instead of being copied again.
    /// </summary>
    public static string ResolveWorkingRoot(
        string contentRootPath,
        string? localBooksRoot,
        AcquisitionOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.WorkingRoot))
        {
            var configured = options.WorkingRoot!;
            return Path.GetFullPath(
                Path.IsPathRooted(configured) ? configured : Path.Combine(contentRootPath, configured));
        }

        if (!string.IsNullOrWhiteSpace(localBooksRoot))
        {
            return Path.GetFullPath(
                Path.Combine(localBooksRoot, "..", "tmp", "acquisitions"));
        }

        // Remote asset storage may be durable elsewhere, but acquisition and
        // transcoding still need bounded scratch disk. Keep that scratch
        // ephemeral and separate from the durable storage abstraction.
        return Path.GetFullPath(
            Path.Combine(contentRootPath, "Storage", "tmp", "acquisitions"));
    }
}
