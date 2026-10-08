namespace Nostos.Backend.Providers.Gutenberg;

/// <summary>
/// Bounds retries for Gutenberg's bulk snapshot transfer. A snapshot is about
/// 177 MB, so the defaults allow multiple resumable recoveries from upstream
/// drops while bounding attempts, idle reads, and total wall-clock time.
/// </summary>
public sealed record GutenbergSnapshotDownloadOptions
{
    /// <summary>Total HTTP GET attempts, including the initial request.</summary>
    public int MaxAttempts { get; init; } = 12;

    /// <summary>Consecutive attempts below the progress threshold before giving up.</summary>
    public int MaxConsecutiveNoProgressAttempts { get; init; } = 3;

    /// <summary>Bytes that must be added by an attempt to count as forward progress.</summary>
    public long MinimumProgressBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum time to wait for one body read before retrying the transfer.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMinutes(2);

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
        if (MaxAttempts is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "Snapshot attempts must be between 1 and 12.");

        if (MaxConsecutiveNoProgressAttempts is < 1 or > 12)
            throw new ArgumentOutOfRangeException(
                nameof(MaxConsecutiveNoProgressAttempts),
                "Consecutive no-progress attempts must be between 1 and 12.");

        if (MinimumProgressBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MinimumProgressBytes));

        if (ReadTimeout <= TimeSpan.Zero || ReadTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(ReadTimeout), "Snapshot read timeout must be positive and no longer than 5 minutes.");

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
