namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>
/// Registered only by a host that can read media in place from an
/// administrator-mounted folder. Its presence is what turns the capability
/// the frontend reads on; a host that does not register it has no such
/// feature.
/// </summary>
public interface IExternalMediaAvailability
{
    /// <summary>True when an external media root is configured for this server.</summary>
    bool IsConfigured { get; }
}
