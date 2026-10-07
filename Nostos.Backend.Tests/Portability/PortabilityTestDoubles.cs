using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Tests.Portability;

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
}

internal sealed class FakeTransferVolume : ITransferVolume
{
    public long AvailableFreeSpaceBytes { get; set; }

    public long TotalSizeBytes { get; set; }
}

internal abstract class DelegatingBookAssetStorage : IBookAssetStorage
{
    protected DelegatingBookAssetStorage()
    {
    }

    protected DelegatingBookAssetStorage(IBookAssetStorage inner) => Inner = inner;

    protected IBookAssetStorage Inner { get; set; } = null!;

    public virtual Task<string> SaveBookFileAsync(
        Guid bookId,
        Stream content,
        string fileName,
        CancellationToken ct = default) =>
        Inner.SaveBookFileAsync(bookId, content, fileName, ct);

    public virtual Task<string> AdoptBookFileAsync(
        Guid bookId,
        string sourcePath,
        string fileName,
        CancellationToken ct = default) =>
        Inner.AdoptBookFileAsync(bookId, sourcePath, fileName, ct);

    public virtual Task<StoredAssetInfo?> GetBookFileInfoAsync(
        Guid bookId,
        CancellationToken ct = default) =>
        Inner.GetBookFileInfoAsync(bookId, ct);

    public virtual Task<StoredAssetRead?> OpenBookFileAsync(
        Guid bookId,
        StorageByteRange? range = null,
        CancellationToken ct = default) =>
        Inner.OpenBookFileAsync(bookId, range, ct);

    public virtual Task<bool> DeleteBookFileAsync(
        Guid bookId,
        CancellationToken ct = default) =>
        Inner.DeleteBookFileAsync(bookId, ct);

    public virtual Task DeleteBookFilesAsync(
        Guid bookId,
        CancellationToken ct = default) =>
        Inner.DeleteBookFilesAsync(bookId, ct);

    public virtual Task<string> SaveBookCoverAsync(
        Guid bookId,
        Stream content,
        string fileName,
        CancellationToken ct = default) =>
        Inner.SaveBookCoverAsync(bookId, content, fileName, ct);

    public virtual Task<StoredAssetInfo?> GetBookCoverInfoAsync(
        Guid bookId,
        CancellationToken ct = default) =>
        Inner.GetBookCoverInfoAsync(bookId, ct);

    public virtual Task<StoredAssetRead?> OpenBookCoverAsync(
        Guid bookId,
        CancellationToken ct = default) =>
        Inner.OpenBookCoverAsync(bookId, ct);

    public virtual Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(
        Guid bookId,
        int width,
        CancellationToken ct = default) =>
        Inner.GetBookCoverThumbnailInfoAsync(bookId, width, ct);

    public virtual Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(
        Guid bookId,
        int width,
        CancellationToken ct = default) =>
        Inner.OpenBookCoverThumbnailAsync(bookId, width, ct);

    public virtual Task<bool> DeleteCoverAsync(
        Guid bookId,
        CancellationToken ct = default) =>
        Inner.DeleteCoverAsync(bookId, ct);
}

internal readonly record struct PollResult<T>(
    bool Matched,
    T? Value,
    string LastObservation);

internal static class PortabilityTestPolling
{
    internal static async Task<PollResult<T>> PollUntilAsync<T>(
        Func<Task<T>> poll,
        Func<T, bool> isMatch,
        Func<T, string> describe,
        TimeSpan timeout,
        TimeSpan interval,
        Action<T>? disposeUnmatched = null,
        Action? afterUnmatchedPoll = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastObservation = "<none>";

        while (DateTime.UtcNow < deadline)
        {
            var value = await poll();
            lastObservation = describe(value);
            if (isMatch(value))
                return new PollResult<T>(true, value, lastObservation);

            disposeUnmatched?.Invoke(value);
            afterUnmatchedPoll?.Invoke();
            await Task.Delay(interval);
        }

        return new PollResult<T>(false, default, lastObservation);
    }
}
