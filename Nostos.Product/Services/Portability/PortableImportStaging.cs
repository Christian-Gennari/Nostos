namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Identifies one prepared-import staging area without exposing its location.
/// </summary>
/// <remarks>
/// <see cref="Guid.Empty"/> is not a valid staging identifier. Implementations MUST
/// reject default and empty identifiers, and identifiers that were never created or
/// have already been deleted, with the same typed not-found outcome
/// (<see cref="PortableStagingException.NotFoundCode"/>); the idempotent
/// <see cref="IPortableImportStaging.DeleteAsync"/> is the one operation that
/// succeeds for an unknown identifier. The value is an opaque selector, not an
/// authorization token: hosts must still bind a staging area to its authenticated
/// owner.
/// </remarks>
public readonly record struct PortableStagingId(Guid Value);

/// <summary>
/// Opaque reference to one media item held by a staging provider.
/// </summary>
/// <remarks>
/// Consumers must treat <see cref="Value"/> as an uninterpreted token. It must not
/// be used as a filesystem path or interpreted as a provider object key.
/// Implementations MUST reject an empty value and any value containing path
/// separators or parent-directory segments, and MUST NOT use the value as a path.
/// The value is not a secret: it may be logged and durably serialised, and it grants
/// no access by itself.
/// </remarks>
public readonly record struct PortableStagedMediaReference(string Value);

/// <summary>
/// Typed failure raised by a prepared-import staging provider.
/// </summary>
public sealed class PortableStagingException : Exception
{
    public const string NotFoundCode = "staging_not_found";
    public const string InvalidReferenceCode = "staging_invalid_reference";
    public const string IntegrityMismatchCode = "staging_integrity_mismatch";
    public const string ConflictCode = "staging_conflict";
    public const string LimitExceededCode = "staging_limit_exceeded";
    public const string AlreadyCommittedCode = "staging_already_committed";

    public PortableStagingException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public PortableStagingException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }

    public bool IsNotFound => string.Equals(Code, NotFoundCode, StringComparison.Ordinal);
}

/// <summary>
/// Host-controlled durable storage for a prepared import: the validated relational
/// payload, the archive manifest, and every copied media object.
/// </summary>
/// <remarks>
/// <para>
/// Implementations choose the storage provider. Staging is not activation: writing
/// here never mutates the active library, and disposal releases provider resources
/// without publishing a prepared import. Provider durability class is a host choice;
/// a process-local implementation is suitable only for immediate flows and tests.
/// </para>
/// <para>
/// Lifecycle. A staging area moves through <c>Building</c>, <c>Committed</c>, and
/// <c>Deleted</c>, with no transition back to <c>Building</c>. Every staged item —
/// one media entry, the relational data payload, or the manifest — is written,
/// completed, and then read, discarded, or deleted:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Create: <see cref="CreateAsync"/> returns a new identifier distinct from every
/// other identifier the provider has returned.
/// </description></item>
/// <item><description>
/// Write: each open call starts exactly one writer for one item. Opening a second
/// writer for an item that is already being written or already completed MUST fail
/// with a typed <see cref="PortableStagingException.ConflictCode"/> conflict. A write
/// disposed without completion is abandoned and discarded; its bytes are never
/// visible to readers, and a later write for the same item may replace it.
/// </description></item>
/// <item><description>
/// Complete: completion takes the write handle and MUST verify the stored bytes
/// against the descriptor bound to the handle when the write was opened. A mismatch
/// is a typed <see cref="PortableStagingException.IntegrityMismatchCode"/> failure and
/// the item is discarded. Successful completion seals the handle: the stored bytes
/// are immutable, later writes through the handle's stream fail with a typed
/// <see cref="PortableStagingException.ConflictCode"/> conflict, and completing the
/// same handle again is idempotent while the staging area is still building.
/// Completing with a handle that was never opened for the supplied staging
/// identifier, or whose write was abandoned or discarded, MUST fail with the typed
/// not-found outcome.
/// </description></item>
/// <item><description>
/// Limits: a declared length beyond the bounds in <see cref="PortableArchiveLimits"/>
/// is rejected when the write is opened. Exceeding the declared length or the hard
/// bound while writing MUST fail with a typed
/// <see cref="PortableStagingException.LimitExceededCode"/> failure and discard the
/// item immediately; the handle becomes dead, the item is not readable, and a later
/// write for the same item may replace it.
/// </description></item>
/// <item><description>
/// Read: media items are read by reference; the relational data payload and the
/// manifest are singular per staging area and are read by staging identifier alone.
/// Reading an item that is not completed, is unknown, belongs to another staging
/// area, or has been deleted MUST fail with the typed not-found outcome. A media
/// reference whose value contains path separators or parent-directory segments is
/// rejected as <see cref="PortableStagingException.InvalidReferenceCode"/>.
/// </description></item>
/// <item><description>
/// Commit: <see cref="CommitPreparedImportAsync"/> is the boundary between building
/// and committed. It MUST fail with a typed
/// <see cref="PortableStagingException.ConflictCode"/> conflict unless the relational
/// payload and manifest are completed, no item is still being written, and the
/// descriptor agrees with the staged data identity, media count and bytes, and media
/// entry count. After it succeeds, every open-write and completion operation on the
/// staging area MUST fail with the typed
/// <see cref="PortableStagingException.AlreadyCommittedCode"/> failure. Identical
/// recommit is idempotent; committing a different descriptor is a typed conflict.
/// Reads, inventory listing, rebuild, and delete remain available.
/// </description></item>
/// <item><description>
/// Delete: <see cref="DeleteAsync"/> is idempotent and MUST remove everything held
/// for the staging identifier, including committed payloads, the committed prepared
/// descriptor, and in-flight writes. An operation racing a delete MUST fail typed
/// rather than publish a partially visible item, and writes through a stream whose
/// area was deleted MUST fail with the typed not-found outcome.
/// </description></item>
/// <item><description>
/// Cancellation: a cancelled operation MUST leave either the previous visible state
/// or nothing; it never leaves a partially visible item.
/// </description></item>
/// </list>
/// <para>
/// Durability. An implementation used by restart-survivable migration jobs MUST
/// persist completed payloads, the media inventory, and the committed prepared
/// descriptor until <see cref="DeleteAsync"/>. After a process restart,
/// <see cref="RebuildPreparedImportAsync"/> MUST reconstruct the prepared import —
/// metadata, media references, and access to the singular relational payload and
/// manifest — from staged state and the staging identifier alone. The in-process
/// <see cref="PreparedPortableImportState"/> is only a cache for the current
/// invocation.
/// </para>
/// </remarks>
public interface IPortableImportStaging : IAsyncDisposable
{
    /// <summary>
    /// Creates a new, empty staging area.
    /// </summary>
    Task<PortableStagingId> CreateAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the single writer for one archive media entry. The descriptor is bound
    /// to the returned handle and is the identity completion verifies.
    /// </summary>
    Task<PortableStagingWrite> OpenMediaWriteAsync(
        PortableStagingId stagingId,
        PortableArchiveMediaEntry descriptor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes and seals one media item after verifying the stored bytes against
    /// the descriptor bound when the write was opened.
    /// </summary>
    Task CompleteMediaAsync(
        PortableStagingId stagingId,
        PortableStagingWrite write,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read stream over a completed media item. The caller owns the stream.
    /// </summary>
    Task<Stream> OpenMediaReadAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the durable media inventory for the staging area, ordered
    /// deterministically. Incomplete and discarded writes are never listed.
    /// </summary>
    Task<IReadOnlyList<PortablePreparedMedia>> ListMediaAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the single writer for the validated relational data payload
    /// (<c>data/library.json</c>) of the staging area.
    /// </summary>
    Task<PortableStagingPayloadWrite> OpenDataWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes and seals the relational data payload after verifying the stored
    /// bytes against the descriptor bound when the write was opened.
    /// </summary>
    Task CompleteDataAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read stream over the completed relational data payload. The caller
    /// owns the stream.
    /// </summary>
    Task<Stream> OpenDataReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the single writer for the archive manifest (<c>manifest.json</c>) of the
    /// staging area.
    /// </summary>
    Task<PortableStagingPayloadWrite> OpenManifestWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes and seals the archive manifest after verifying the stored bytes
    /// against the descriptor bound when the write was opened.
    /// </summary>
    Task CompleteManifestAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read stream over the completed archive manifest. The caller owns the
    /// stream.
    /// </summary>
    Task<Stream> OpenManifestReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Durably records the prepared-import descriptor and closes the staging area to
    /// further writes. The descriptor MUST agree with the staged relational payload
    /// and media inventory, and the commit MUST fail typed while required items are
    /// incomplete or any write is still uncompleted.
    /// </summary>
    Task CommitPreparedImportAsync(
        PortableStagingId stagingId,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the prepared import from staged state and the staging identifier
    /// alone, as required after a process restart. Fails with the typed not-found
    /// outcome when no prepared descriptor has been committed for the staging area.
    /// </summary>
    /// <remarks>
    /// Reconstruction verifies the relational payload against <see cref="PreparedPortableImportMetadata.DataSha256"/>
    /// and verifies every staged media length, but it does not re-hash media
    /// contents. Activation (#681) MUST re-hash every staged media file against its
    /// <see cref="PortableArchiveMediaEntry.Sha256"/> before mutating the live
    /// library.
    /// </remarks>
    Task<IPreparedPortableImport> RebuildPreparedImportAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every staged item and the committed prepared descriptor for the
    /// staging area. Idempotent: deleting an unknown or already deleted identifier
    /// succeeds.
    /// </summary>
    Task DeleteAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A single sequential write opened for one archive media entry.
/// </summary>
/// <remarks>
/// Disposing the write closes the stream but does not complete or publish the item.
/// A write disposed before <see cref="IPortableImportStaging.CompleteMediaAsync"/>
/// succeeds is abandoned and discarded. After successful completion the handle is
/// sealed: later writes through <see cref="Stream"/> fail with a typed
/// <see cref="PortableStagingException"/> and the verified bytes cannot change.
/// </remarks>
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

    /// <summary>
    /// The sequential writer for this item. The stream must be writable; seeking is
    /// not required and must not be attempted. Ownership is transferred to this
    /// handle: callers must not dispose the stream themselves, and the provider may
    /// enforce the maximum item length while it is written.
    /// </summary>
    public Stream Stream { get; }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// A single sequential write opened for the relational data payload or the manifest.
/// </summary>
/// <remarks>
/// The ownership, abandonment, and sealing rules of <see cref="PortableStagingWrite"/>
/// apply unchanged.
/// </remarks>
public sealed class PortableStagingPayloadWrite : IAsyncDisposable
{
    public PortableStagingPayloadWrite(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Stream = stream;
    }

    /// <summary>
    /// The sequential writer for this payload. The stream must be writable; seeking
    /// is not required and must not be attempted. Ownership is transferred to this
    /// handle: callers must not dispose the stream themselves, and the provider may
    /// enforce the maximum payload length while it is written.
    /// </summary>
    public Stream Stream { get; }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// Durable verified facts for a prepared import whose payloads and media are held by
/// a staging provider.
/// </summary>
/// <remarks>
/// <see cref="Counts"/> MUST cover every portable kind and MUST be computed from the
/// validated relational payload rather than from the legacy archive count inventory.
/// <see cref="DataBytes"/> and <see cref="DataSha256"/> bind the prepared import to
/// the exact relational payload that was validated, so activation can revalidate the
/// staged bytes before mutating the library. <see cref="ArchiveBytes"/> is the number
/// of compressed archive source bytes consumed while preparing the import.
/// <see cref="IntegrityVerified"/> remains status metadata; it is not a substitute
/// for revalidating the staged payload against <see cref="DataSha256"/>.
/// </remarks>
public sealed record PreparedPortableImportMetadata(
    PortableStagingId StagingId,
    int FormatVersion,
    int DataVersion,
    long DataBytes,
    string DataSha256,
    MigrationArchiveCounts Counts,
    int MediaFiles,
    long MediaBytes,
    long ArchiveBytes,
    DateTime PreparedAtUtc,
    bool IntegrityVerified);

/// <summary>
/// Provider-neutral summary and staged media inventory for a fully prepared archive,
/// reconstructible from staging alone via
/// <see cref="IPortableImportStaging.RebuildPreparedImportAsync"/>. The singular
/// relational payload and manifest are read from the staging area by its identifier.
/// </summary>
public interface IPreparedPortableImport
{
    PreparedPortableImportMetadata Metadata { get; }

    IReadOnlyList<PortablePreparedMedia> Media { get; }
}

/// <summary>
/// The durable staged-media inventory descriptor: a media descriptor paired with its
/// opaque, verified staging reference. It associates each staged item with its book
/// id, media kind, archive entry path, length, and SHA-256.
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

    public long DataBytes => Metadata.DataBytes;

    public string DataSha256 => Metadata.DataSha256;

    public MigrationArchiveCounts Counts => Metadata.Counts;

    public int MediaFiles => Metadata.MediaFiles;

    public long MediaBytes => Metadata.MediaBytes;

    public long ArchiveBytes => Metadata.ArchiveBytes;

    public DateTime PreparedAtUtc => Metadata.PreparedAtUtc;

    public bool IntegrityVerified => Metadata.IntegrityVerified;
}

/// <summary>
/// Computes authoritative migration counts from the validated portable relational
/// payload and the staged media entry count.
/// </summary>
/// <remarks>
/// This replaces the legacy <see cref="PortableArchiveCounts"/> conversion, which
/// could not represent writing notes, assistant settings, or note import book links
/// and therefore produced known-false zeros for current DataVersion 3 archives. The
/// completeness test fails when <see cref="MigrationArchiveCounts"/> gains a property
/// this function does not set or <see cref="PortableLibraryData"/> gains a property
/// this function does not count.
/// </remarks>
internal static class PortableLibraryCounts
{
    internal static MigrationArchiveCounts ComputeCounts(
        PortableLibraryData data,
        long mediaEntries)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new MigrationArchiveCounts(
            Works: data.Works.Count,
            Books: data.Books.Count,
            Notes: data.Notes.Count,
            Topics: data.Topics.Count,
            NoteTopics: data.NoteTopics.Count,
            Writings: data.Writings.Count,
            WritingNotes: data.WritingNotes?.Count ?? 0,
            Collections: data.Collections.Count,
            CollectionMemberships: data.BookCollections.Count,
            Acquisitions: data.BookAcquisitions.Count,
            AssistantSettings: data.AssistantSettings is null ? 0 : 1,
            NoteImportBookLinks: data.NoteImportBookLinks?.Count ?? 0,
            MediaEntries: mediaEntries);
    }
}

/// <summary>
/// Relational data and manifest state cached for the current archive-processing
/// invocation only.
/// </summary>
/// <remarks>
/// The durable representation of a prepared import is the committed
/// <see cref="PreparedPortableImportMetadata"/> plus the staged payloads and media
/// inventory; use <see cref="IPortableImportStaging.RebuildPreparedImportAsync"/> to
/// recover it after a restart.
/// </remarks>
internal sealed record PreparedPortableImportState(
    PortablePreparedImport Summary,
    PortableArchiveManifest Manifest,
    PortableLibraryData Data);
