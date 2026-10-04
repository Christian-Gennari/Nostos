namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Identifies one prepared-import staging area without exposing its location.
/// </summary>
public readonly record struct PortableStagingId(Guid Value);

/// <summary>
/// Opaque reference to one media item held by a staging provider.
/// </summary>
/// <remarks>
/// Consumers must treat <see cref="Value"/> as an uninterpreted token. It must not
/// be used as a filesystem path or interpreted as a provider object key.
/// </remarks>
public readonly record struct PortableStagedMediaReference(string Value);

/// <summary>
/// Host-controlled storage for media copied from a portable archive before activation.
/// </summary>
/// <remarks>
/// Implementations choose the storage provider. A media reference is not readable as
/// prepared media until <see cref="CompleteMediaAsync"/> succeeds. Deletion is
/// idempotent; disposal releases provider resources but does not activate the import.
/// </remarks>
public interface IPortableImportStaging : IAsyncDisposable
{
    Task<PortableStagingId> CreateAsync(
        CancellationToken cancellationToken = default);

    Task<PortableStagingWrite> OpenMediaWriteAsync(
        PortableStagingId stagingId,
        PortableArchiveMediaEntry descriptor,
        CancellationToken cancellationToken = default);

    Task CompleteMediaAsync(
        PortableStagedMediaReference reference,
        long verifiedLength,
        string verifiedSha256,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenMediaReadAsync(
        PortableStagedMediaReference reference,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A single sequential write opened for one archive media entry.
/// </summary>
public sealed class PortableStagingWrite : IAsyncDisposable
{
    public PortableStagingWrite(
        PortableStagedMediaReference reference,
        Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Reference = reference;
        Stream = stream;
    }

    public PortableStagedMediaReference Reference { get; }

    public Stream Stream { get; }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// Verified archive facts for an import whose media is held by a staging provider.
/// </summary>
public sealed record PreparedPortableImportMetadata(
    PortableStagingId StagingId,
    int FormatVersion,
    int DataVersion,
    PortableArchiveCounts Counts,
    int MediaFiles,
    long MediaBytes,
    long ArchiveBytes,
    DateTime PreparedAtUtc,
    bool IntegrityVerified);

/// <summary>
/// Provider-neutral summary and staged media references for a fully prepared archive.
/// </summary>
public interface IPreparedPortableImport
{
    PreparedPortableImportMetadata Metadata { get; }

    IReadOnlyList<PortablePreparedMedia> Media { get; }
}

/// <summary>
/// A media descriptor paired with its opaque, verified staging reference.
/// </summary>
public sealed record PortablePreparedMedia(
    PortableArchiveMediaEntry Descriptor,
    PortableStagedMediaReference Reference);

/// <summary>
/// Public value returned when a portable archive has been prepared successfully.
/// </summary>
public sealed record PortablePreparedImport(
    PreparedPortableImportMetadata Metadata,
    IReadOnlyList<PortablePreparedMedia> Media) : IPreparedPortableImport
{
    public PortableStagingId StagingId => Metadata.StagingId;

    public int FormatVersion => Metadata.FormatVersion;

    public int DataVersion => Metadata.DataVersion;

    public PortableArchiveCounts Counts => Metadata.Counts;

    public int MediaFiles => Metadata.MediaFiles;

    public long MediaBytes => Metadata.MediaBytes;

    public long ArchiveBytes => Metadata.ArchiveBytes;

    public DateTime PreparedAtUtc => Metadata.PreparedAtUtc;

    public bool IntegrityVerified => Metadata.IntegrityVerified;
}

/// <summary>
/// Relational data remains internal to the archive-processing invocation.
/// </summary>
internal sealed record PreparedPortableImportState(
    PortablePreparedImport Summary,
    PortableArchiveManifest Manifest,
    PortableLibraryData Data);
