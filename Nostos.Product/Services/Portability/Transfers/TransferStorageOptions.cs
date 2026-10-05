// Nostos.Product/Services/Portability/Transfers/TransferStorageOptions.cs

namespace Nostos.Backend.Services.Portability.Transfers;

/// <summary>
/// Local transfer storage configuration for SelfHosted migration jobs
/// (issue #679, plan section 2.1). Every transfer service resolves its paths
/// through <see cref="TransferPathResolver"/> against the single validated
/// root this class resolves, so no service can write outside it.
///
/// The root defaults to a <c>transfers</c> directory beside the resolved books
/// root — with the default books root that is exactly
/// <c>&lt;content-root&gt;/Storage/transfers</c>. A configured
/// <c>Storage:TransferPath</c> is absolute or relative to the content root.
/// Relative transfer roots deliberately follow the books volume, matching the
/// local backup layout: a self-hoster who puts the library on a larger volume
/// expects transfer scratch space on that same volume.
///
/// The section name matches <see cref="Nostos.Backend.Configuration.FileStorageOptions"/>
/// on purpose: the SelfHosted host binds both classes to the existing
/// <c>Storage</c> section.
/// </summary>
public sealed class TransferStorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Directory name used when no <see cref="TransferPath"/> is configured.</summary>
    public const string DefaultTransferDirectoryName = "transfers";

    /// <summary>
    /// Absolute path, or a path relative to the content root. Defaults to
    /// <c>transfers</c> beside the resolved books root.
    /// </summary>
    public string? TransferPath { get; set; }

    /// <summary>
    /// Accepted chunk size in bytes for new transfer sessions. Must remain
    /// inside the configured <see cref="MinChunkBytes"/>..<see cref="MaxChunkBytes"/>
    /// range and inside the shared contract limits.
    /// </summary>
    public int ChunkBytes { get; set; } = MigrationContractLimits.DefaultChunkBytes;

    public int MinChunkBytes { get; set; } = MigrationContractLimits.MinChunkBytes;

    public int MaxChunkBytes { get; set; } = MigrationContractLimits.MaxChunkBytes;

    /// <summary>Server-side processing concurrency. SelfHosted defaults to one.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>
    /// Per-installation ceiling on non-terminal migration jobs. A bounded
    /// number of outstanding jobs keeps a client from parking unbounded
    /// durable rows and reservations. One session per job is enforced
    /// structurally, so this also bounds outstanding sessions.
    /// </summary>
    public int MaxOutstandingJobs { get; set; } = 10;

    /// <summary>
    /// Global unallocatable safety margin kept free on the transfer volume.
    /// The effective margin is the larger of this byte floor and
    /// <see cref="DiskSafetyMarginPercent"/> of the physical volume.
    /// </summary>
    public long DiskSafetyMarginBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Percentage component of the global unallocatable safety margin.</summary>
    public double DiskSafetyMarginPercent { get; set; } = 5;

    /// <summary>Short preflight hold until a reservation is claimed by a job.</summary>
    public int PreflightReservationMinutes { get; set; } = 15;

    /// <summary>Download retention for a completed export artifact.</summary>
    public int ExportRetentionHours { get; set; } = 24;

    /// <summary>Retention for committed prepared-import staging before activation.</summary>
    public int PreparedImportRetentionHours { get; set; } = 24;

    /// <summary>Cleanup worker interval for expired transfer resources.</summary>
    public int CleanupIntervalMinutes { get; set; } = 15;

    public TimeSpan PreflightReservationTtl => TimeSpan.FromMinutes(PreflightReservationMinutes);

    public TimeSpan ExportRetentionTtl => TimeSpan.FromHours(ExportRetentionHours);

    public TimeSpan PreparedImportRetentionTtl => TimeSpan.FromHours(PreparedImportRetentionHours);

    public TimeSpan CleanupInterval => TimeSpan.FromMinutes(CleanupIntervalMinutes);

    /// <summary>
    /// Validates every configured value. Called from host composition before
    /// the transfer root is created so a misconfigured deployment fails
    /// startup instead of admitting transfers against impossible bounds.
    /// </summary>
    public static void Validate(TransferStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MinChunkBytes < MigrationContractLimits.MinChunkBytes
            || options.MinChunkBytes > MigrationContractLimits.MaxChunkBytes)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:MinChunkBytes' must be between " +
                $"{MigrationContractLimits.MinChunkBytes} and {MigrationContractLimits.MaxChunkBytes} bytes.");
        }

        if (options.MaxChunkBytes < MigrationContractLimits.MinChunkBytes
            || options.MaxChunkBytes > MigrationContractLimits.MaxChunkBytes)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:MaxChunkBytes' must be between " +
                $"{MigrationContractLimits.MinChunkBytes} and {MigrationContractLimits.MaxChunkBytes} bytes.");
        }

        if (options.MinChunkBytes > options.MaxChunkBytes)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:MinChunkBytes' cannot exceed '{SectionName}:MaxChunkBytes'.");
        }

        if (options.ChunkBytes < options.MinChunkBytes || options.ChunkBytes > options.MaxChunkBytes)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:ChunkBytes' ({options.ChunkBytes}) must be inside the configured " +
                $"{options.MinChunkBytes}-{options.MaxChunkBytes} range.");
        }

        if (options.MaxConcurrentJobs < 1)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:MaxConcurrentJobs' must be at least 1.");
        }

        if (options.MaxOutstandingJobs < 1)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:MaxOutstandingJobs' must be at least 1.");
        }

        if (options.DiskSafetyMarginBytes < 0)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:DiskSafetyMarginBytes' cannot be negative.");
        }

        if (options.DiskSafetyMarginPercent is < 0 or > 100)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:DiskSafetyMarginPercent' must be between 0 and 100.");
        }

        if (options.PreflightReservationMinutes < 1)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:PreflightReservationMinutes' must be at least 1.");
        }

        if (options.ExportRetentionHours < 1)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:ExportRetentionHours' must be at least 1.");
        }

        if (options.PreparedImportRetentionHours < 1)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:PreparedImportRetentionHours' must be at least 1.");
        }

        if (options.CleanupIntervalMinutes < 1)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:CleanupIntervalMinutes' must be at least 1.");
        }
    }

    /// <summary>
    /// Resolves the effective absolute transfer root and refuses unsafe roots
    /// (a filesystem root, the application content root, or an ancestor of it).
    /// </summary>
    public static string ResolveRoot(
        string contentRootPath,
        string booksRoot,
        TransferStorageOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(booksRoot);

        var configured = options?.TransferPath;
        string resolved;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            resolved = Path.GetFullPath(
                Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(contentRootPath, configured));
        }
        else
        {
            var fullBooksRoot = Path.GetFullPath(booksRoot);
            var parent = Path.GetDirectoryName(fullBooksRoot);
            resolved = Path.Combine(
                string.IsNullOrEmpty(parent) ? fullBooksRoot : parent,
                DefaultTransferDirectoryName);
        }

        if (IsUnsafeRoot(resolved, contentRootPath))
        {
            throw new InvalidOperationException(
                $"The transfer storage root '{resolved}' is unsafe: it must not be a filesystem root, " +
                "the application content root, or an ancestor of the application content root.");
        }

        return resolved;
    }

    /// <summary>
    /// True when <paramref name="resolvedRootPath"/> is a filesystem root, the
    /// content root, or an ancestor of the content root. Cleaning or deleting a
    /// transfer root with any of those shapes could destroy the application or
    /// unrelated host data.
    /// </summary>
    public static bool IsUnsafeRoot(string resolvedRootPath, string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var root = Path.GetFullPath(resolvedRootPath);
        var content = Path.GetFullPath(contentRootPath);
        var pathRoot = Path.GetPathRoot(root);
        if (string.IsNullOrEmpty(root)
            || string.Equals(root, pathRoot, PathComparison(root)))
        {
            return true;
        }

        return IsSameOrAncestor(root, content);
    }

    private static bool IsSameOrAncestor(string candidate, string descendant)
    {
        var comparison = PathComparison(candidate);
        if (string.Equals(candidate, descendant, comparison))
        {
            return true;
        }

        var prefix = candidate.EndsWith(Path.DirectorySeparatorChar)
            ? candidate
            : candidate + Path.DirectorySeparatorChar;
        return descendant.StartsWith(prefix, comparison);
    }

    private static StringComparison PathComparison(string path) =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
