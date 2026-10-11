namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Turns one archive-engine checkpoint into the job progress a customer sees.
/// Every host's progress pump goes through here so they cannot drift.
/// </summary>
public static class MigrationArchiveProgressMapping
{
    /// <summary>
    /// The engine measures each of its phases against that phase's own total.
    /// Validating the relational data is a few kilobytes against a total of a
    /// few kilobytes, so forwarding it made the import's progress bar run to
    /// 100% and then restart at 0% for the media that is the real work. Data
    /// validation is therefore reported without a total, which the UI renders
    /// as an indeterminate bar.
    /// </summary>
    public static MigrationProgress ToJobProgress(
        MigrationProgressPhase phase,
        PortableArchiveProgress value,
        bool includeMessage = false)
    {
        var measured = value.Phase != PortableArchiveProgressPhase.ValidatingData;
        return new MigrationProgress(
            phase,
            measured ? value.BytesProcessed : 0,
            measured ? value.TotalBytes : null,
            value.TotalItems is null ? null : value.ItemsProcessed,
            value.TotalItems,
            includeMessage ? value.Message : null);
    }
}
