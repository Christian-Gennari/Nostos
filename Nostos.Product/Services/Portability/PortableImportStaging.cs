namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Identifies one prepared-import staging area without exposing its location.
/// </summary>
/// <remarks>
/// <see cref="Guid.Empty"/> is not a valid staging identifier. Implementations MUST
/// reject default and empty identifiers, and identifiers that were never created or
/// have already been deleted, with the same typed not-found outcome
/// (<see cref="PortableStagingException.NotFoundCode"/>). The value is an opaque
/// selector, not an authorization token: hosts must still bind a staging area to its
/// authenticated owner.
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
/// Opaque reference to the staged relational data payload or the archive manifest.
/// </summary>
/// <remarks>
/// The opacity and validation rules of <see cref="PortableStagedMediaReference"/>
/// apply unchanged.
/// </remarks>
public readonly record struct PortableStagedPayloadReference(string Value);

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
/// without publishing a prepared import.
/// </para>
/// <para>
/// Lifecycle. Every staged item — one media entry, the relational data payload, or
/// the manifest — is written, completed, and then read or discarded as follows:
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
/// disposed without completion is abandoned; its bytes are never visible to readers,
/// may be superseded by a new write for the same item, and are removed by
/// <see cref="DeleteAsync"/>.
/// </description></item>
/// <item><description>
/// Complete: completion MUST verify the stored bytes against the expected length and
/// SHA-256, and those expected values MUST equal the descriptor bound to the
/// reference when the write was opened. A mismatch is a typed
/// <see cref="PortableStagingException.IntegrityMismatchCode"/> failure and the item
/// is discarded. Completing an already completed item with identical values is
/// idempotent; completing it with different values is a typed
/// <see cref="PortableStagingException.ConflictCode"/> conflict.
/// </description></item>
/// <item><description>
/// Read: reading an item that is not completed, is unknown, or does not belong to the
/// supplied staging identifier MUST fail with the typed not-found outcome
/// (<see cref="PortableStagingException.NotFoundCode"/>). An unknown or deleted
/// staging identifier produces the identical not-found outcome. A reference whose
/// value contains path separators or parent-directory segments is rejected as
/// <see cref="PortableStagingException.InvalidReferenceCode"/>.
/// </description></item>
/// <item><description>
/// Delete: <see cref="DeleteAsync"/> is idempotent and MUST remove everything held
/// for the staging identifier, including committed payloads, the committed prepared
/// descriptor, and in-flight writes. An operation racing a delete MUST fail typed
/// rather than publish a partially visible item.
/// </description></item>
/// <item><description>
/// Cancellation: a cancelled operation MUST leave either the previous visible state
/// or nothing; it never leaves a partially visible item.
/// </description></item>
/// <item><description>
/// Bounds: the size bounds in <see cref="PortableArchiveLimits"/> are enforced while
/// a stream is written, not only at completion. Exceeding the bound MUST fail with a
/// typed <see cref="PortableStagingException.LimitExceededCode"/> failure and discard
/// the item.
/// </description></item>
/// </list>
/// <para>
/// Durability. An implementation used by restart-survivable migration jobs MUST
/// persist completed payloads, the media inventory, and the committed prepared
/// descriptor until <see cref="DeleteAsync"/>. After a process restart,
/// <see cref="RebuildPreparedImportAsync"/> MUST reconstruct the prepared import from
/// staged state alone; the in-process <see cref="PreparedPortableImportState"/> is
/// only a cache for the current invocation. Provider durability class is a host
/// choice; a process-local implementation is suitable only for immediate flows and
/// tests.
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
    /// to the returned reference and must equal the values supplied to
    /// <see cref="CompleteMediaAsync"/>.
    /// </summary>
    Task<PortableStagingWrite> OpenMediaWriteAsync(
        PortableStagingId stagingId,
        PortableArchiveMediaEntry descriptor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes one media item after verifying the staged bytes against
    /// <paramref name="expectedLength"/> and <paramref name="expectedSha256"/>.
    /// </summary>
    Task CompleteMediaAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        long expectedLength,
        string expectedSha256,
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
    /// deterministically. Incomplete and abandoned writes are never listed.
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
    /// Completes the relational data payload after verifying the staged bytes
    /// against <paramref name="expectedLength"/> and <paramref name="expectedSha256"/>.
    /// </summary>
    Task CompleteDataAsync(
        PortableStagingId stagingId,
        PortableStagedPayloadReference reference,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read stream over the completed relational data payload. The caller
    /// owns the stream.
    /// </summary>
    Task<Stream> OpenDataReadAsync(
        PortableStagingId stagingId,
        PortableStagedPayloadReference reference,
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
    /// Completes the archive manifest after verifying the staged bytes against
    /// <paramref name="expectedLength"/> and <paramref name="expectedSha256"/>.
    /// </summary>
    Task CompleteManifestAsync(
        PortableStagingId stagingId,
        PortableStagedPayloadReference reference,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read stream over the completed archive manifest. The caller owns the
    /// stream.
    /// </summary>
    Task<Stream> OpenManifestReadAsync(
        PortableStagingId stagingId,
        PortableStagedPayloadReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Durably records the prepared-import descriptor for the staging area.
    /// Preparation is a real commit boundary: the descriptor MUST agree with the
    /// staged relational payload and media inventory, and the commit MUST fail with a
    /// typed <see cref="PortableStagingException.ConflictCode"/> conflict while any
    /// write is still uncompleted (abandoned writes do not count). Disagreeing
    /// identities fail with a typed integrity failure. Repeating an identical commit
    /// is idempotent; committing a different descriptor is a typed
    /// <see cref="PortableStagingException.ConflictCode"/> conflict.
    /// </summary>
    Task CommitPreparedImportAsync(
        PortableStagingId stagingId,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the prepared import from staged state alone, as required after a
    /// process restart. The result MUST be identical to the original preparation.
    /// Fails with the typed not-found outcome when no prepared descriptor has been
    /// committed for the staging area.
    /// </summary>
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
/// succeeds is abandoned and never visible to readers.
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
    /// enforce the maximum descriptor length while it is written.
    /// </summary>
    public Stream Stream { get; }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// A single sequential write opened for the relational data payload or the manifest.
/// </summary>
/// <remarks>
/// The ownership and abandonment rules of <see cref="PortableStagingWrite"/> apply
/// unchanged.
/// </remarks>
public sealed class PortableStagingPayloadWrite : IAsyncDisposable
{
    public PortableStagingPayloadWrite(
        PortableStagedPayloadReference reference,
        Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Reference = reference;
        Stream = stream;
    }

    public PortableStagedPayloadReference Reference { get; }

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
/// <see cref="IPortableImportStaging.RebuildPreparedImportAsync"/>.
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

    public MigrationArchiveCounts Counts => Metadata.Counts;

    public int MediaFiles => Metadata.MediaFiles;

    public long MediaBytes => Metadata.MediaBytes;

    public long ArchiveBytes => Metadata.ArchiveBytes;

    public DateTime PreparedAtUtc => Metadata.PreparedAtUtc;

    public bool IntegrityVerified => Metadata.IntegrityVerified;
}

/// <summary>
/// Converts the archive-engine count inventory into the migration count inventory
/// used by prepared imports.
/// </summary>
/// <remarks>
/// The archive count type predates several portable kinds. Kinds it cannot express
/// (writing notes, assistant settings, and note import book links) map to zero, and
/// the caller supplies the media entry count from the manifest inventory. The
/// portable-format completeness test fails when either count type gains a property
/// this conversion does not handle.
/// </remarks>
public static class PortableArchiveCountConversion
{
    public static MigrationArchiveCounts ToMigrationArchiveCounts(
        this PortableArchiveCounts counts,
        long mediaEntries)
    {
        ArgumentNullException.ThrowIfNull(counts);

        return new MigrationArchiveCounts(
            Works: counts.Works,
            Books: counts.Books,
            Notes: counts.Notes,
            Topics: counts.Topics,
            NoteTopics: counts.NoteTopics,
            Writings: counts.Writings,
            WritingNotes: 0L,
            Collections: counts.Collections,
            CollectionMemberships: counts.BookCollections,
            Acquisitions: counts.BookAcquisitions,
            AssistantSettings: 0L,
            NoteImportBookLinks: 0L,
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
