namespace Nostos.Backend.Services.Portability;

/// <summary>
/// One consistent relational export revision plus the media references it
/// contains. <see cref="Data"/> is materialized inside a single read
/// transaction; media storage is only observed after that transaction closes.
/// </summary>
internal sealed record PortableExportSnapshot(
    DateTime SnapshotAtUtc,
    PortableLibraryData Data,
    PortableArchiveCounts Counts,
    IReadOnlyList<PortableSourceMedia> Media);

/// <summary>
/// A media item referenced by the relational snapshot. Storage metadata is
/// deliberately not captured here because media is only read after the
/// relational transaction has ended.
/// </summary>
internal sealed record PortableSourceMedia(
    Guid BookId,
    string Kind,
    // The canonical stored name; set for tracks only, where a book has many.
    string? FileName = null);

/// <summary>
/// A source media revision pinned by length, last-modified, entity tag and
/// SHA-256. The archive is only finalized when the bytes copied from storage
/// still match this pin and the post-copy storage observation is unchanged.
/// </summary>
internal sealed record PinnedPortableSourceMedia(
    Guid BookId,
    string Kind,
    string ArchivePath,
    string FileName,
    string ContentType,
    long Length,
    DateTimeOffset LastModified,
    string EntityTag,
    string Sha256);
