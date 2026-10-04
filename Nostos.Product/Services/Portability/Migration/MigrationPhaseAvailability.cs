namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Reports whether this host has a real phase handler for a direction. Until
/// Slices 9/10 wire archive preparation, the SelfHosted API refuses to create a
/// job whose handler is missing instead of creating a job that predictably
/// fails; the deployment-capabilities endpoint reports the same fact.
/// </summary>
public interface IMigrationPhaseAvailability
{
    bool IsAvailable(MigrationDirection direction);
}

/// <summary>Default for hosts that have not wired a migration phase handler yet.</summary>
public sealed class MigrationPhaseAvailabilityNone : IMigrationPhaseAvailability
{
    public static MigrationPhaseAvailabilityNone Instance { get; } = new();

    public bool IsAvailable(MigrationDirection direction) => false;
}
