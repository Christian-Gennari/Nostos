namespace Nostos.Backend.Providers.Contracts;

/// <summary>
/// A conditional request for one provider's bulk catalog snapshot.
///
/// The validators are opaque to the caller: a snapshot consumer persists
/// whatever the source last returned and sends it back unchanged. A request
/// with none of them is a full read.
/// </summary>
/// <param name="Cursor">
/// Provider-defined checkpoint from the previous read (for example a "since"
/// watermark), or <see langword="null"/> for the start of the catalog. The
/// caller never interprets it.
/// </param>
/// <param name="ETag">Entity tag from the previous read, when the source supplied one.</param>
/// <param name="IfModifiedSince">Last-Modified value from the previous read, when the source supplied one.</param>
public sealed record ProviderSnapshotRequest(
    string? Cursor = null,
    string? ETag = null,
    DateTimeOffset? IfModifiedSince = null);

/// <summary>Whether a snapshot read produced items or the source still matches the supplied validators.</summary>
public enum ProviderSnapshotStatus
{
    /// <summary>The source returned a body; <see cref="ProviderSnapshot.Items"/> is the catalog.</summary>
    Updated,

    /// <summary>The source still matches the request validators; there is nothing to read.</summary>
    NotModified,
}

/// <summary>
/// One bulk read of a provider's catalog, normalized to <see cref="ProviderItem"/>
/// exactly like a search page: items carry discovery metadata and a cover, and
/// no assets — acquisition re-resolves an item against the live provider.
/// </summary>
/// <param name="Status">Whether this read carries items or is a not-modified response.</param>
/// <param name="Items">
/// The provider's items, in source order. The stream is single-use and may be
/// backed by a live response, so a consumer must enumerate it once, within the
/// operation, before it is done with the snapshot.
/// </param>
/// <param name="ETag">Entity tag to persist and send back on the next read, when the source supplies one.</param>
/// <param name="LastModified">Last-Modified value to persist and send back on the next read, when the source supplies one.</param>
/// <param name="NextCursor">
/// Checkpoint to persist and send back on the next read when the source pages
/// its catalog; <see langword="null"/> means the read reached the end (or the
/// source has no cursor).
/// </param>
public sealed record ProviderSnapshot(
    ProviderSnapshotStatus Status,
    IAsyncEnumerable<ProviderItem> Items,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    string? NextCursor = null)
{
    /// <summary>A read whose validators still match: no items, validators unchanged.</summary>
    public static ProviderSnapshot NotModified(string? etag = null, DateTimeOffset? lastModified = null) =>
        new(
            ProviderSnapshotStatus.NotModified,
            AsyncEnumerable.Empty<ProviderItem>(),
            etag,
            lastModified);
}

/// <summary>
/// Bulk, conditional read of a provider's whole catalog, for a host that
/// maintains its own synchronized discovery index.
///
/// This is deliberately not part of the SelfHosted search path: the live
/// provider fan-out never calls it and no default configuration schedules it.
/// A host that uses it owns the cadence and the source etiquette; a source
/// failure surfaces as <see cref="ProviderException"/> with a stable code so a
/// sync can record it and back off.
/// </summary>
public interface IProviderSnapshotSource
{
    Task<ProviderSnapshot> ReadAsync(ProviderSnapshotRequest request, CancellationToken ct);
}
