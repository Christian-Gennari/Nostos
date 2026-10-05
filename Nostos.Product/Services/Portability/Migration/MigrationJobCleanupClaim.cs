namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Fencing token for activation-area cleanup (issue #681, Slice 10). The orphan
/// sweep marks a terminal job with a cleanup claim in the existing lease fields
/// before deleting that job's candidate/recovery leftovers, and
/// <see cref="EfMigrationJobStore.RetryAsync"/> refuses to reactivate a job
/// while an unexpired claim is held. Normal worker leases are 64 lowercase hex
/// characters, so the <c>cleanup:</c> prefix can never collide with one; no
/// schema change is needed. The claim expires on its own if the sweep process
/// dies, after which a retry may proceed and rebuild any partially removed
/// candidate.
/// </summary>
public static class MigrationJobCleanupClaim
{
    public const string Prefix = "cleanup:";

    /// <summary>SQL LIKE pattern matching any cleanup claim token.</summary>
    public const string LikePattern = Prefix + "%";

    /// <summary>
    /// Generous upper bound for one job's leftover deletion. A claim that
    /// outlives a crashed sweep only delays a retry; it never blocks it forever.
    /// </summary>
    public static TimeSpan DefaultDuration => TimeSpan.FromMinutes(30);

    public static bool IsClaimToken(string? token) =>
        token is not null && token.StartsWith(Prefix, StringComparison.Ordinal);

    public static string NewToken() => Prefix + Guid.NewGuid().ToString("N");
}
