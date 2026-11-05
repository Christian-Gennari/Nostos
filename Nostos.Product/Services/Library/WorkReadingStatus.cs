using System.Linq.Expressions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Reading status of a book (a work), calculated from its editions and never
/// stored. A work has exactly one status:
/// <list type="bullet">
/// <item><b>Reading</b>: an unfinished edition has progress (a percentage or a
/// saved place) and either nothing in the work is finished or that edition was
/// read after the latest finish (a reread).</item>
/// <item><b>Finished</b>: some edition has a FinishedAt and nothing was read
/// after it (so it is not Reading).</item>
/// <item><b>Not started</b>: no edition has progress or a FinishedAt.</item>
/// </list>
/// The predicates below are the same rule as queries (they translate to SQL on
/// SQLite and PostgreSQL), so the filters and the sidebar counts agree with
/// <see cref="Of"/>. An edition with no LastReadAt cannot be shown to have been
/// read after a finish, so it does not undo one.
/// </summary>
internal static class WorkReadingStatus
{
    // The rules as expressions, so the filters and the one-query sidebar counts
    // are built from the same text.
    public static Expression<Func<BookModel, bool>> IsFinished(NostosDbContext db) =>
        b => db.Books.Any(e => e.WorkId == b.WorkId && e.Progress.FinishedAt != null)
            && !db.Books.Any(e =>
                e.WorkId == b.WorkId
                && e.Progress.FinishedAt == null
                && (e.Progress.ProgressPercent > 0 || e.Progress.LastLocation != null)
                && e.Progress.LastReadAt != null
                && !db.Books.Any(f =>
                    f.WorkId == b.WorkId
                    && f.Progress.FinishedAt != null
                    && f.Progress.FinishedAt >= e.Progress.LastReadAt));

    public static Expression<Func<BookModel, bool>> IsReading(NostosDbContext db) =>
        b => db.Books.Any(e =>
            e.WorkId == b.WorkId
            && e.Progress.FinishedAt == null
            && (e.Progress.ProgressPercent > 0 || e.Progress.LastLocation != null)
            && (!db.Books.Any(f => f.WorkId == b.WorkId && f.Progress.FinishedAt != null)
                || (e.Progress.LastReadAt != null
                    // Nothing in the work was finished at or after this edition's last read.
                    && !db.Books.Any(f =>
                        f.WorkId == b.WorkId
                        && f.Progress.FinishedAt != null
                        && f.Progress.FinishedAt >= e.Progress.LastReadAt))));

    public static Expression<Func<BookModel, bool>> IsNotStarted(NostosDbContext db) =>
        b => !db.Books.Any(e =>
            e.WorkId == b.WorkId
            && (e.Progress.ProgressPercent > 0 || e.Progress.LastLocation != null || e.Progress.FinishedAt != null));

    public static IQueryable<BookModel> WhereFinished(IQueryable<BookModel> books, NostosDbContext db) =>
        books.Where(IsFinished(db));

    public static IQueryable<BookModel> WhereReading(IQueryable<BookModel> books, NostosDbContext db) =>
        books.Where(IsReading(db));

    public static IQueryable<BookModel> WhereNotStarted(IQueryable<BookModel> books, NostosDbContext db) =>
        books.Where(IsNotStarted(db));

    /// <summary>The same rule in memory, for the editions of one work.</summary>
    public static (bool Finished, bool Reading, bool NotStarted) Of(IReadOnlyCollection<BookModel> editions)
    {
        var anyFinished = editions.Any(e => e.Progress.FinishedAt != null);
        var latestFinish = editions
            .Where(e => e.Progress.FinishedAt != null)
            .Select(e => e.Progress.FinishedAt!.Value)
            .DefaultIfEmpty()
            .Max();

        var reading = editions.Any(e =>
            e.Progress.FinishedAt == null
            && (e.Progress.ProgressPercent > 0 || e.Progress.LastLocation != null)
            && (!anyFinished
                || (e.Progress.LastReadAt is { } read && read > latestFinish)));
        var notStarted = !editions.Any(e =>
            e.Progress.ProgressPercent > 0 || e.Progress.LastLocation != null || e.Progress.FinishedAt != null);
        return (anyFinished && !reading, reading, notStarted);
    }
}
