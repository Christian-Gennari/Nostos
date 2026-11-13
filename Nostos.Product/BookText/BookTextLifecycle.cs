using Microsoft.Extensions.Logging;

namespace Nostos.Product.BookText;

public sealed class BookTextIngestionScheduler(
    IBookTextIndex index,
    IBookDerivedArtifactStorage artifacts,
    ILogger<BookTextIngestionScheduler> logger,
    BookTextWorkSignal? workSignal = null) : IBookTextIngestionScheduler
{
    public async Task ScheduleAsync(Guid bookId, string sourceFileName, CancellationToken ct = default)
    {
        if (!BookTextFormatResolver.TryResolve(sourceFileName, out var format))
            return;

        try
        {
            // Invalidate retrieval first. If derived-object cleanup is slower or
            // temporarily unavailable, a replaced source must still stop serving
            // chunks from the previous revision immediately.
            await index.ScheduleAsync(bookId, sourceFileName, format, ct);
            workSignal?.Signal();
            await artifacts.DeleteBookArtifactsAsync(bookId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Could not schedule book-text ingestion for {BookId}; exception type {ExceptionType}. The source import remains valid.",
                bookId,
                exception.GetType().Name);
        }
    }
}

public sealed class BookTextLifecycle(
    IBookTextIndex index,
    IBookDerivedArtifactStorage artifacts) : IBookTextLifecycle
{
    public async Task DeleteAsync(Guid bookId, CancellationToken ct = default)
    {
        await index.DeleteBookAsync(bookId, ct);
        await artifacts.DeleteBookArtifactsAsync(bookId, ct);
    }
}
