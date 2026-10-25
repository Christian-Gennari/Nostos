namespace Nostos.Backend.Services.BookUpload;

/// <summary>
/// Owns staging and publication of one book file across resumable parts
/// (#1266 Phase A). The SelfHosted implementation stages under the transfer
/// root; a hosted implementation can put the same contract in front of
/// multipart object storage without changing the session state machine.
///
/// The token returned by <see cref="ReceivePartAsync"/> is opaque to callers:
/// it identifies bytes that were fully received and hashed but not yet placed.
/// </summary>
public interface IBookFileUploadStore
{
    /// <summary>Creates the staging target, pre-sized to <paramref name="totalBytes"/>.</summary>
    Task PrepareAsync(Guid sessionId, long totalBytes, CancellationToken ct);

    /// <summary>
    /// Reads exactly <paramref name="lengthBytes"/> from <paramref name="content"/>,
    /// verifies the SHA-256 and stores the bytes in a pending part. Nothing is
    /// written into the file's final range until <see cref="PlacePartAsync"/>.
    /// </summary>
    Task<string> ReceivePartAsync(
        Guid sessionId,
        int partIndex,
        int lengthBytes,
        string expectedSha256,
        Stream content,
        CancellationToken ct);

    /// <summary>Durably writes a pending part at its offset, then removes the pending copy.</summary>
    Task PlacePartAsync(
        Guid sessionId,
        string pendingToken,
        long offsetBytes,
        CancellationToken ct);

    /// <summary>Whole-file SHA-256 over the staged bytes.</summary>
    Task<bool> VerifyAsync(
        Guid sessionId,
        long totalBytes,
        string expectedSha256,
        CancellationToken ct);

    /// <summary>Marks the verified staging file as ready for publication.</summary>
    Task SealAsync(Guid sessionId, CancellationToken ct);

    /// <summary>True when either the staged or the sealed file is present.</summary>
    Task<bool> HasStagingAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Opens the sealed upload for metadata extraction.</summary>
    Task<Stream> OpenSealedAsync(Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Publishes the sealed upload through the host's asset storage. The
    /// implementation removes its own staging copy only after the destination
    /// commit succeeds.
    /// </summary>
    Task AdoptAsync(
        Guid bookId,
        Guid sessionId,
        string fileName,
        IBookAssetStorage storage,
        CancellationToken ct);

    /// <summary>Deletes every staged, sealed or pending byte for the session.</summary>
    Task DiscardAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Deletes a pending token that could not be placed.</summary>
    void DiscardPending(string pendingToken);
}
