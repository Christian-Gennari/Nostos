namespace Nostos.Backend.Services;

/// <summary>
/// Provider-neutral storage boundary for book media.
///
/// SelfHosted maps this to the local filesystem; other hosts can map it to
/// durable object storage. Callers never need an absolute storage path.
/// </summary>
public interface IBookAssetStorage
{
    Task<string> SaveBookFileAsync(
        Guid bookId,
        Stream content,
        string fileName,
        CancellationToken ct = default);

    /// <summary>
    /// Stores a caller-owned staged file, then removes the source only after
    /// the destination commit succeeds. The local provider can optimize this
    /// to a rename; object storage streams the file directly to the provider.
    /// </summary>
    Task<string> AdoptBookFileAsync(
        Guid bookId,
        string sourcePath,
        string fileName,
        CancellationToken ct = default);

    Task<StoredAssetInfo?> GetBookFileInfoAsync(
        Guid bookId,
        CancellationToken ct = default);

    Task<StoredAssetRead?> OpenBookFileAsync(
        Guid bookId,
        StorageByteRange? range = null,
        CancellationToken ct = default);

    Task<bool> DeleteBookFileAsync(
        Guid bookId,
        CancellationToken ct = default);

    Task DeleteBookFilesAsync(
        Guid bookId,
        CancellationToken ct = default);

    Task<string> SaveBookCoverAsync(
        Guid bookId,
        Stream content,
        string fileName,
        CancellationToken ct = default);

    Task<StoredAssetInfo?> GetBookCoverInfoAsync(
        Guid bookId,
        CancellationToken ct = default);

    Task<StoredAssetRead?> OpenBookCoverAsync(
        Guid bookId,
        CancellationToken ct = default);

    Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(
        Guid bookId,
        int width,
        CancellationToken ct = default);

    Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(
        Guid bookId,
        int width,
        CancellationToken ct = default);

    Task<bool> DeleteCoverAsync(
        Guid bookId,
        CancellationToken ct = default);
}

public sealed record StoredAssetInfo(
    string FileName,
    string ContentType,
    long Length,
    string EntityTag,
    DateTimeOffset LastModified);

public readonly record struct StorageByteRange(long Start, long EndInclusive)
{
    public long Length => checked(EndInclusive - Start + 1);

    public static StorageByteRange Create(long start, long endInclusive, long totalLength)
    {
        if (totalLength < 0)
            throw new ArgumentOutOfRangeException(nameof(totalLength));

        if (start < 0 || endInclusive < start || endInclusive >= totalLength)
            throw new ArgumentOutOfRangeException(nameof(start), "Invalid storage byte range.");

        return new StorageByteRange(start, endInclusive);
    }
}

/// <summary>
/// Open storage response. Disposing it releases both the response stream and
/// any provider-specific response owner (for example an S3 GetObjectResponse).
/// </summary>
public sealed class StoredAssetRead(
    StoredAssetInfo info,
    Stream content,
    StorageByteRange? range = null,
    IDisposable? owner = null)
    : IAsyncDisposable
{
    public StoredAssetInfo Info { get; } = info;
    public Stream Content { get; } = content;
    public StorageByteRange? Range { get; } = range;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        owner?.Dispose();
    }
}
