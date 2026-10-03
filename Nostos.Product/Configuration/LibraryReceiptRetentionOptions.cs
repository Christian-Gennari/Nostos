namespace Nostos.Backend.Configuration;

// Library receipt retention configuration (issue #51). Library command
// receipts are kept for RetentionDays and additionally count-bounded at
// MaximumReceipts; the hosted worker cleans up once every
// CleanupIntervalHours. All three values are clamped to safe ranges during
// normalization so a misconfigured section can never disable retention,
// turn a cleanup scan into a hot loop, or configure absurd bounds.
public sealed class LibraryReceiptRetentionOptions
{
    // A receipt must be retained at least 1 day and at most 10 years.
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;

    // The count cap must stay within 100..1,000,000 rows so the cap remains
    // meaningful and the oldest-above-cap query stays cheap.
    public const int MinMaximumReceipts = 100;
    public const int MaxMaximumReceipts = 1_000_000;

    // Cleanup may run at most hourly and at least once a month, keeping the
    // worker calm under any configuration.
    public const int MinCleanupIntervalHours = 1;
    public const int MaxCleanupIntervalHours = 720;

    public int RetentionDays { get; set; } = 90;

    public int MaximumReceipts { get; set; } = 10_000;

    public int CleanupIntervalHours { get; set; } = 24;

    /// <summary>
    /// Clamps every value into its safe range in place and returns the same
    /// instance. Called once at registration time so the singleton options
    /// the service and worker receive are always normalized.
    /// </summary>
    public static LibraryReceiptRetentionOptions Normalize(LibraryReceiptRetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.RetentionDays = Math.Clamp(options.RetentionDays, MinRetentionDays, MaxRetentionDays);
        options.MaximumReceipts = Math.Clamp(options.MaximumReceipts, MinMaximumReceipts, MaxMaximumReceipts);
        options.CleanupIntervalHours = Math.Clamp(options.CleanupIntervalHours, MinCleanupIntervalHours, MaxCleanupIntervalHours);

        return options;
    }
}
