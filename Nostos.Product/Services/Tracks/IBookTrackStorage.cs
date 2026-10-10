namespace Nostos.Backend.Services;

/// <summary>
/// Storage boundary for the tracks of a multi-track audiobook.
///
/// Deliberately separate from <see cref="IBookAssetStorage"/>: a host adopts
/// multi-track storage by registering this, without every existing asset
/// storage implementation having to grow new members first. A host that does
/// not register it simply cannot hold multi-track books, and the features that
/// need it say so.
///
/// Tracks are addressed by book and 1-based number only; the stored name is
/// <see cref="BookTrackFormats.CanonicalFileName"/>, never anything a caller
/// or a remote source chose.
///
/// Removing a whole book's media, tracks included, remains
/// <see cref="IBookAssetStorage.DeleteBookFilesAsync"/>.
/// </summary>
public interface IBookTrackStorage
{
    /// <summary>Stores one track from a stream and returns its bare stored file name.</summary>
    Task<string> SaveTrackAsync(
        Guid bookId,
        int number,
        Stream content,
        string fileName,
        CancellationToken ct = default);

    /// <summary>
    /// Stores a caller-owned staged file as a track, removing the source only
    /// after the destination commit succeeds. Returns the bare stored file name.
    /// </summary>
    Task<string> AdoptTrackAsync(
        Guid bookId,
        int number,
        string sourcePath,
        string fileName,
        CancellationToken ct = default);

    Task<StoredAssetInfo?> GetTrackInfoAsync(
        Guid bookId,
        int number,
        CancellationToken ct = default);

    Task<StoredAssetRead?> OpenTrackAsync(
        Guid bookId,
        int number,
        StorageByteRange? range = null,
        CancellationToken ct = default);

    /// <summary>Removes every track of the book and nothing else.</summary>
    Task DeleteTracksAsync(
        Guid bookId,
        CancellationToken ct = default);
}
