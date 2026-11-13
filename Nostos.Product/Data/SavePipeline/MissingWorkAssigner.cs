using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Data.SavePipeline;

/// <summary>
/// A few legacy import/repository paths still add a <see cref="BookModel"/>
/// directly without assigning <see cref="BookModel.WorkId"/>. Keep those writes
/// valid now that <c>WorkId</c> is a required foreign key; the library service
/// always assigns the work explicitly, so this is only a compatibility guard
/// for rows that arrive with the old default value.
/// </summary>
internal static class MissingWorkAssigner
{
    public static void Assign(NostosDbContext db)
    {
        var pending = MissingWorkBooks(db);
        if (pending.Count == 0)
            return;

        Assign(db, pending, KnownWorks(db));
    }

    public static async Task AssignAsync(NostosDbContext db, CancellationToken cancellationToken)
    {
        var pending = MissingWorkBooks(db);
        if (pending.Count == 0)
            return;

        Assign(db, pending, await KnownWorksAsync(db, cancellationToken));
    }

    private static void Assign(NostosDbContext db, List<BookModel> pending, List<WorkModel> works)
    {
        foreach (var book in pending)
        {
            var normalizedTitle = BookIdentityNormalizer.NormalizeTitle(book.Title);
            var normalizedAuthor = BookIdentityNormalizer.NormalizeAuthor(book.Author);
            var work = works.FirstOrDefault(w =>
                w.NormalizedTitle == normalizedTitle &&
                w.NormalizedAuthor == normalizedAuthor);

            if (work is null)
            {
                work = new WorkModel
                {
                    Title = book.Title,
                    Author = book.Author,
                    NormalizedTitle = normalizedTitle,
                    NormalizedAuthor = normalizedAuthor,
                    CreatedAt = book.CreatedAt,
                };
                db.Works.Add(work);
                works.Add(work);
            }

            book.Work = work;
            book.WorkId = work.Id;
        }
    }

    private static List<WorkModel> KnownWorks(NostosDbContext db)
    {
        var works = TrackedWorks(db);
        AddMissingWorks(works, db.Works.ToList());
        return works;
    }

    private static async Task<List<WorkModel>> KnownWorksAsync(NostosDbContext db, CancellationToken cancellationToken)
    {
        var works = TrackedWorks(db);
        AddMissingWorks(works, await db.Works.ToListAsync(cancellationToken));
        return works;
    }

    private static List<WorkModel> TrackedWorks(NostosDbContext db) =>
        db.ChangeTracker.Entries<WorkModel>()
            .Where(entry => entry.State != EntityState.Deleted)
            .Select(entry => entry.Entity)
            .ToList();

    private static void AddMissingWorks(List<WorkModel> target, IEnumerable<WorkModel> source)
    {
        var knownIds = target.Select(work => work.Id).ToHashSet();
        foreach (var work in source)
        {
            if (knownIds.Add(work.Id))
                target.Add(work);
        }
    }

    private static List<BookModel> MissingWorkBooks(NostosDbContext db) =>
        db.ChangeTracker.Entries<BookModel>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.WorkId == Guid.Empty)
            .Select(entry => entry.Entity)
            .ToList();
}
