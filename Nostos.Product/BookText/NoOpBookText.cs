namespace Nostos.Product.BookText;

public sealed class NoOpBookTextIndex : IBookTextIndex
{
    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ScheduleAsync(Guid bookId, string sourceFileName, BookTextSourceFormat format, CancellationToken ct = default) => Task.CompletedTask;
    public Task<BookTextIngestionWork?> TryClaimNextAsync(TimeSpan staleAfter, CancellationToken ct = default) => Task.FromResult<BookTextIngestionWork?>(null);
    public Task<bool> ReplaceReadyAsync(BookTextSourceRevision revision, IReadOnlyList<BookTextIndexedChunk> chunks, long characterCount, int expectedAttempt, CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> MarkFailedAsync(Guid bookId, string errorCode, string errorMessage, bool unsupported, int expectedAttempt, CancellationToken ct = default) => Task.FromResult(false);
    public Task DeleteBookAsync(Guid bookId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<BookTextIngestionState?> GetStateAsync(Guid bookId, CancellationToken ct = default) => Task.FromResult<BookTextIngestionState?>(null);
    public Task<IReadOnlyList<BookTextSearchHit>> SearchAsync(string query, IReadOnlyList<Guid> bookIds, int maxCandidates, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BookTextSearchHit>>([]);
    public Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsAsync(Guid bookId, string sourceSha256, string extractorVersion, int ordinal, int radius, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BookTextIndexedChunk>>([]);
    public Task<IReadOnlyList<BookTextIngestionState>> GetStatesAsync(IReadOnlyList<Guid> bookIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BookTextIngestionState>>([]);
    public Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsManyAsync(IReadOnlyList<BookTextNeighborQuery> queries, int radius, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BookTextIndexedChunk>>([]);
}

public sealed class NoOpBookTextArtifactStorage : IBookDerivedArtifactStorage
{
    public Task WriteAsync(BookTextSourceRevision revision, Func<Stream, CancellationToken, Task> writer, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task DeleteBookArtifactsAsync(Guid bookId, CancellationToken ct = default) => Task.CompletedTask;
    public Task PruneBookArtifactsAsync(Guid bookId, BookTextSourceRevision keep, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class NoOpBookTextIngestionScheduler : IBookTextIngestionScheduler
{
    public Task ScheduleAsync(Guid bookId, string sourceFileName, CancellationToken ct = default) => Task.CompletedTask;
}
