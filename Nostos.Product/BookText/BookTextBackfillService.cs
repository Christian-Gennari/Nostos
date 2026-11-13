using Microsoft.Extensions.Logging;
using Nostos.Backend.Services.Library;

namespace Nostos.Product.BookText;

public sealed class BookTextBackfillService(
    ILibraryService library,
    IBookTextIndex index,
    IBookTextIngestionScheduler scheduler,
    ILogger<BookTextBackfillService> logger)
{
    public async Task<int> ScheduleMissingAsync(CancellationToken ct = default)
    {
        var scheduled = 0;

        // One narrow read of the whole library, then one state read per 100
        // candidates, instead of a full book DTO page plus a state query per book.
        var books = await library.ListBookScopeAsync(
            bookIds: null,
            collectionId: null,
            limit: int.MaxValue,
            ct);

        var candidates = books
            .Where(book => book.HasFile
                && !string.IsNullOrWhiteSpace(book.FileName)
                && BookTextFormatResolver.TryResolve(book.FileName, out _))
            .ToList();

        foreach (var group in candidates.Chunk(100))
        {
            var states = (await index.GetStatesAsync(group.Select(book => book.Id).ToList(), ct))
                .ToDictionary(state => state.BookId);

            foreach (var book in group)
            {
                if (states.TryGetValue(book.Id, out var state)
                    && string.Equals(
                        state.ExtractorVersion,
                        BookTextArtifactSchema.CurrentExtractorVersion,
                        StringComparison.Ordinal))
                    continue;

                await scheduler.ScheduleAsync(book.Id, book.FileName!, ct);
                scheduled++;
            }
        }

        if (scheduled > 0)
        {
            logger.LogInformation(
                "Scheduled {Count} existing imported publication(s) for book-text indexing.",
                scheduled);
        }

        return scheduled;
    }
}
