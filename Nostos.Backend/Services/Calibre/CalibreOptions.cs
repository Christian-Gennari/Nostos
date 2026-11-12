namespace Nostos.Backend.Services.Calibre;

/// <summary>
/// The one Calibre library this server may read (issue #1527).
///
/// The administrator makes the library folder visible to the server (for a
/// container, a read-only bind mount such as
/// <c>/mnt/calibre:/external/calibre:ro</c>) and names it here. Nothing in the
/// web interface can choose a path, and nothing in the import writes to it.
/// Unset disables the feature; the Settings card then explains how to set one
/// up, exactly as External media does.
/// </summary>
public sealed class CalibreOptions
{
    public const string SectionName = "Calibre";

    /// <summary>Absolute path of the Calibre library folder. Unset disables the import.</summary>
    public string? LibraryRoot { get; set; }
}
