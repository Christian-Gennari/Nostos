namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>
/// Whether this host can read media in place from an administrator-mounted
/// folder. Registered only by a host that implements external media; a host
/// that does not register it has no such feature, and the capability the
/// frontend reads is false.
/// </summary>
public interface IExternalMediaAvailability
{
    /// <summary>True when an external media root is configured for this server.</summary>
    bool IsConfigured { get; }
}
