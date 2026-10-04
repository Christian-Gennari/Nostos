using Nostos.Product.BookText;
using Nostos.Backend.Services;

namespace Nostos.Backend.Workers;

public sealed class BookTextIngestionWorker(
    IServiceScopeFactory scopes,
    BookTextOptions options,
    ILogger<BookTextIngestionWorker> logger,
    ILibraryMaintenanceCoordinator? maintenance = null) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);

    // Embedding is the low-priority second stage: it is only attempted when no
    // extraction work is waiting, and after an idle or failed pass it is left
    // alone for a while so a fully embedded library is not rescanned every
    // cycle and a broken provider is not called in a tight loop.
    private DateTime _nextEmbeddingPassUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Book-text ingestion worker started.");

        try
        {
            await using var operation = maintenance is null ? null : await maintenance.EnterOperationAsync(stoppingToken);
            using var initialScope = scopes.CreateScope();
            await initialScope.ServiceProvider
                .GetRequiredService<BookTextBackfillService>()
                .ScheduleMissingAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Book-text startup backfill failed with {ExceptionType}; the worker will continue processing already queued books.",
                exception.GetType().Name);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var didWork = await ProcessOneAsync(stoppingToken)
                    || await ProcessEmbeddingsWhenDueAsync(stoppingToken);
                if (!didWork)
                    await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Book-text worker cycle failed with {ExceptionType}; publication text is not logged.",
                    exception.GetType().Name);
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }

    public async Task<bool> ProcessOneAsync(CancellationToken ct = default)
    {
        await using var operation = maintenance is null ? null : await maintenance.EnterOperationAsync(ct);
        using var scope = scopes.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<IBookTextIndex>();
        var work = await index.TryClaimNextAsync(
            TimeSpan.FromMinutes(Math.Max(1, options.StaleProcessingMinutes)),
            ct);
        if (work is null) return false;

        var engine = scope.ServiceProvider.GetRequiredService<BookTextIngestionEngine>();
        await engine.ProcessAsync(work, ct);
        return true;
    }

    /// <summary>
    /// Embeds one batch of chunks that have no vector for the active model.
    /// Never throws for a provider problem: a failure is a reported outcome and
    /// leaves lexical search untouched.
    /// </summary>
    public async Task<BookTextEmbeddingPassResult> ProcessEmbeddingsAsync(CancellationToken ct = default)
    {
        await using var operation = maintenance is null ? null : await maintenance.EnterOperationAsync(ct);
        using var scope = scopes.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<BookTextEmbeddingEngine>();
        return await engine.ProcessBatchAsync(ct);
    }

    private async Task<bool> ProcessEmbeddingsWhenDueAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _nextEmbeddingPassUtc)
            return false;

        var result = await ProcessEmbeddingsAsync(ct);
        if (result.Status == BookTextEmbeddingPassStatus.Embedded)
            return true;

        var wait = result.Status == BookTextEmbeddingPassStatus.Failed
            ? options.EmbeddingFailureBackoffSeconds
            : options.EmbeddingIdlePollSeconds;
        _nextEmbeddingPassUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, wait));
        return false;
    }
}
