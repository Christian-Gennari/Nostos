using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Reading status of a book (a work), calculated from its editions and never
/// stored:
/// <list type="bullet">
/// <item><b>Finished</b>: any edition has a FinishedAt.</item>
/// <item><b>Reading</b>: some unfinished edition has progress above 0 and
/// either nothing in the work is finished or that edition was read after the
/// latest finish (a reread). A book can be Reading and Finished at once.</item>
/// <item><b>Not started</b>: no edition has progress or a FinishedAt.</item>
/// </list>
/// The predicates below are the same rule as queries (they translate to SQL on
/// SQLite and PostgreSQL), so the filters and the sidebar counts agree with
/// <see cref="Of"/>.
/// </summary>
internal static class WorkReadingStatus
{
    public static IQueryable<BookModel> WhereFinished(IQueryable<BookModel> books, NostosDbContext db) =>
        books.Where(b => db.Books.Any(e => e.WorkId == b.WorkId && e.Progress.FinishedAt != null));

    public static IQueryable<BookModel> WhereReading(IQueryable<BookModel> books, NostosDbContext db) =>
        books.Where(b => db.Books.Any(e =>
            e.WorkId == b.WorkId
            && e.Progress.FinishedAt == null
            && e.Progress.ProgressPercent > 0
            // Nothing in the work was finished at or after this edition's last read.
            && !db.Books.Any(f =>
                f.WorkId == b.WorkId
                && f.Progress.FinishedAt != null
                && (e.Progress.LastReadAt == null || f.Progress.FinishedAt >= e.Progress.LastReadAt))));

    public static IQueryable<BookModel> WhereNotStarted(IQueryable<BookModel> books, NostosDbContext db) =>
        books.Where(b => !db.Books.Any(e =>
            e.WorkId == b.WorkId
            && (e.Progress.ProgressPercent > 0 || e.Progress.FinishedAt != null)));

    /// <summary>The same rule in memory, for the editions of one work.</summary>
    public static (bool Finished, bool Reading, bool NotStarted) Of(IReadOnlyCollection<BookModel> editions)
    {
        var latestFinish = editions
            .Where(e => e.Progress.FinishedAt != null)
            .Select(e => e.Progress.FinishedAt!.Value)
            .DefaultIfEmpty()
            .Max();
        var anyFinished = editions.Any(e => e.Progress.FinishedAt != null);

        var reading = editions.Any(e =>
            e.Progress.FinishedAt == null
            && e.Progress.ProgressPercent > 0
            && (!anyFinished
                || (e.Progress.LastReadAt is { } read && read > latestFinish)));
        var notStarted = !editions.Any(e => e.Progress.ProgressPercent > 0 || e.Progress.FinishedAt != null);
        return (anyFinished, reading, notStarted);
    }
}
