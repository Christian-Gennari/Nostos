using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Scheduling and safety bounds for the activation maintenance workers (issue
/// #681, Slice 10): the recovery expiry cleanup sweep, the activation orphan
/// sweep and the post-activation derived rebuild. All workers run outside the
/// exclusive library maintenance window and take the shared operation lease per
/// pass, so a cutover never overlaps them.
/// </summary>
public sealed class SelfHostedActivationMaintenanceOptions
{
    public const string SectionName = "ActivationMaintenance";

    /// <summary>
    /// Delay before the first pass of each worker. Hosted services already start
    /// after the startup reconciler has finished; the delay keeps a freshly
    /// admitted host from competing with bootstrap and the first requests.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 30;

    /// <summary>Interval between recovery cleanup / orphan sweep passes.</summary>
    public int SweepIntervalMinutes { get; set; } = 15;

    /// <summary>Interval between derived rebuild passes.</summary>
    public int DerivedRebuildIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// An activation leftover with no owner is removed only after this age. The
    /// window matches the transfer session expiry so an in-flight preparation or
    /// activation can never be mistaken for an orphan by timestamp alone.
    /// </summary>
    public int OrphanSafetyAgeHours { get; set; } = MigrationContractLimits.SessionExpiryHours;

    public TimeSpan StartupDelay => TimeSpan.FromSeconds(StartupDelaySeconds);

    public TimeSpan SweepInterval => TimeSpan.FromMinutes(SweepIntervalMinutes);

    public TimeSpan DerivedRebuildInterval => TimeSpan.FromSeconds(DerivedRebuildIntervalSeconds);

    public TimeSpan OrphanSafetyAge => TimeSpan.FromHours(OrphanSafetyAgeHours);

    public static void Validate(SelfHostedActivationMaintenanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StartupDelaySeconds < 0)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:StartupDelaySeconds' cannot be negative.");
        }

        if (options.SweepIntervalMinutes < 1)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:SweepIntervalMinutes' must be at least 1.");
        }

        if (options.DerivedRebuildIntervalSeconds < 1)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:DerivedRebuildIntervalSeconds' must be at least 1.");
        }

        if (options.OrphanSafetyAgeHours < 1)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:OrphanSafetyAgeHours' must be at least 1.");
        }
    }
}
