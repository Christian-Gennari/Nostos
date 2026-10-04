namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Describes a checkpoint in portable archive processing.
/// </summary>
public enum PortableArchiveProgressPhase
{
    Snapshotting = 0,
    IndexingMedia = 1,
    WritingArchive = 2,
    InspectingArchive = 3,
    ValidatingData = 4,
    StagingMedia = 5,
    Prepared = 6,
}

/// <summary>
/// Reports progress from the archive engine, independently of migration-job state.
/// </summary>
public sealed record PortableArchiveProgress(
    PortableArchiveProgressPhase Phase,
    long BytesProcessed,
    long? TotalBytes,
    int ItemsProcessed = 0,
    int? TotalItems = null,
    string? Message = null);
