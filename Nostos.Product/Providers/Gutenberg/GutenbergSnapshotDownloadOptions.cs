namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>
/// Bounds retries for Gutenberg's bulk snapshot transfer. A snapshot is about
/// 177 MB, so the defaults allow recovery from a short-lived upstream drop
/// without leaving a sync worker waiting indefinitely.
/// </summary>
public sealed record GutenbergSnapshotDownloadOptions
{
    /// <summary>Total HTTP GET attempts, including the initial request.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Maximum wall-clock time spent downloading and retrying one snapshot.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Base delay before the first retry; later retries grow exponentially.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound for exponential retry delays (Retry-After remains authoritative).</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Random variation applied to exponential delays, from zero to 0.5.</summary>
    public double RetryJitterRatio { get; init; } = 0.2;

    internal void Validate()
    {
        if (MaxAttempts is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "Snapshot attempts must be between 1 and 5.");

        if (MaxDuration <= TimeSpan.Zero || MaxDuration > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(MaxDuration), "Snapshot duration must be positive and no longer than 15 minutes.");

        if (InitialRetryDelay < TimeSpan.Zero || InitialRetryDelay > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(InitialRetryDelay));

        if (MaxRetryDelay < TimeSpan.Zero || MaxRetryDelay > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(MaxRetryDelay));

        if (RetryJitterRatio is < 0 or > 0.5)
            throw new ArgumentOutOfRangeException(nameof(RetryJitterRatio));
    }
}
