using Nostos.Product.BookText;

namespace Nostos.Backend.Services.BookText;

/// <summary>Outcome of one strict derived-scheduling attempt.</summary>
internal enum DerivedScheduleOutcome
{
    /// <summary>The book was durably queued for ingestion.</summary>
    Scheduled,

    /// <summary>The source format is intentionally not indexable; nothing to queue.</summary>
    Unsupported,
}

/// <summary>
/// Strict, result-bearing scheduling seam for the post-activation derived
/// rebuild. Unlike <see cref="IBookTextIngestionScheduler"/>, which deliberately
/// treats derived indexing as non-authoritative and swallows failures for the
/// normal import path, this seam propagates a failure so the rebuild can keep
/// its success marker absent and retry the book on the next pass.
/// </summary>
internal interface IBookTextDerivedScheduler
{
    Task<DerivedScheduleOutcome> ScheduleAsync(
        Guid bookId,
        string fileName,
        CancellationToken ct = default);
}

/// <summary>
/// Invalidates a book's derived artifacts first and then durably queues it for
/// ingestion. The order is safe here because the rebuild runs after the derived
/// tables were wiped: no stale chunk can be served for a book that is about to
/// be rescheduled. If either step throws, the book is not durably scheduled and
/// the next rebuild pass retries it.
/// </summary>
internal sealed class StrictBookTextDerivedScheduler(
    IBookTextIndex index,
    IBookDerivedArtifactStorage artifacts) : IBookTextDerivedScheduler
{
    public async Task<DerivedScheduleOutcome> ScheduleAsync(
        Guid bookId,
        string fileName,
        CancellationToken ct = default)
    {
        if (!BookTextFormatResolver.TryResolve(fileName, out var format))
        {
            return DerivedScheduleOutcome.Unsupported;
        }

        await artifacts.DeleteBookArtifactsAsync(bookId, ct);
        await index.ScheduleAsync(bookId, fileName, format, ct);
        return DerivedScheduleOutcome.Scheduled;
    }
}
