namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>
/// The one folder of existing media this server may read in place.
///
/// The administrator makes the folder visible to the server (for a container,
/// a read-only bind mount such as <c>/mnt/audiobooks:/external/audiobooks:ro</c>)
/// and names it here. Nothing in the web UI can choose a path: a request can
/// only ever name a file by its path relative to this root.
/// </summary>
public sealed class ExternalMediaOptions
{
    public const string SectionName = "ExternalMedia";
    public const string DefaultRootId = "external";

    /// <summary>Absolute path of the mounted folder. Unset disables the feature.</summary>
    public string? Root { get; set; }

    /// <summary>
    /// Stable identity stored with every reference. It is what a reference
    /// follows, so the folder can be mounted at another path later without
    /// breaking the books linked from it.
    /// </summary>
    public string RootId { get; set; } = DefaultRootId;
}
