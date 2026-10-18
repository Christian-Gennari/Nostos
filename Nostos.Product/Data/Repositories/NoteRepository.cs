using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data.Repositories;

public class NoteRepository : INoteRepository
{
    private readonly NostosDbContext _db;

    public NoteRepository(NostosDbContext db)
    {
        _db = db;
    }

    public async Task<List<NoteModel>> GetByBookIdAsync(Guid bookId)
    {
        // Oldest first, with Id as a tiebreak: the book page shows the latest notes
        // at the end of a collapsed feed, which needs a defined order (#1062).
        return await _db.Notes.Include(n => n.Book).Where(n => n.BookId == bookId)
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToListAsync();
    }

    public async Task<NoteModel?> GetByIdAsync(Guid id)
    {
        return await _db.Notes.FindAsync(id);
    }

    public async Task<NoteModel?> GetByIdWithTopicsAsync(Guid id)
    {
        return await _db
            .Notes.Include(n => n.NoteTopics)
            .Include(n => n.Book)
            .FirstOrDefaultAsync(n => n.Id == id);
    }

    public async Task<NoteModel?> GetByIdWithBookAsync(Guid id)
    {
        return await _db
            .Notes.AsNoTracking()
            .Include(n => n.Book)
            .FirstOrDefaultAsync(n => n.Id == id);
    }

    public Task AddAsync(NoteModel note)
    {
        _db.Notes.Add(note);
        // Don't save here — the endpoint calls ProcessNoteAsync first, then saves
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync()
    {
        await _db.SaveChangesAsync();
    }

    public async Task<NoteCommandReceipt?> GetReceiptAsync(string clientId, string idempotencyKey)
    {
        // AsNoTracking keeps the replay read out of the change tracker; the
        // stored result is returned as-is and never mutated.
        return await _db
            .NoteCommandReceipts.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ClientId == clientId && r.IdempotencyKey == idempotencyKey);
    }

    public Task AddReceiptAsync(NoteCommandReceipt receipt)
    {
        // Don't save here — the service writes the note and its receipt inside
        // one transaction, then commits.
        _db.NoteCommandReceipts.Add(receipt);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(NoteModel note)
    {
        _db.Notes.Remove(note);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteTopicLinksAsync(Guid noteId)
    {
        // ExecuteDelete bypasses the change tracker. The revision row is
        // advanced FIRST, before the portable rows, so every portable writer
        // takes the singleton lock in the same order (issue #679 Slice 11).
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await LibraryRevision.AdvanceAndGetAsync(_db);
        await _db.NoteTopics.Where(nc => nc.NoteId == noteId).ExecuteDeleteAsync();
        await transaction.CommitAsync();
    }
    public Task<List<NoteModel>> SearchByTextAsync(
        string query,
        int limit,
        IReadOnlyCollection<Guid>? bookIds = null) =>
        SearchByAllTermsAsync([query], limit, bookIds);

    /// <summary>
    /// Notes where EVERY term appears in at least one of the fields a person
    /// would describe a note by: its text, its quotation, its passage title, or
    /// its book's title or author (issue #1038). One term is the plain phrase
    /// search; several let "schopenhauer will" find his notes about the will
    /// although no single field holds both words.
    /// </summary>
    public async Task<List<NoteModel>> SearchByAllTermsAsync(
        IReadOnlyList<string> terms,
        int limit,
        IReadOnlyCollection<Guid>? bookIds = null)
    {
        var cleaned = terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        if (cleaned.Count == 0 || bookIds is { Count: 0 }) return [];

        var notes = _db
            .Notes.Include(n => n.Book)
            .Include(n => n.NoteTopics)
            .ThenInclude(nc => nc.Topic)
            .AsQueryable();

        if (bookIds is { Count: > 0 })
            notes = notes.Where(n => bookIds.Contains(n.BookId));

        foreach (var term in cleaned)
            notes = WhereAnyFieldContains(notes, term);

        return await notes
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<NoteModel>> GetWithoutTopicsAsync(int limit, int offset)
    {
        return await _db
            .Notes.Include(n => n.Book)
            .Where(n => !n.NoteTopics.Any())
            // `Id` breaks CreatedAt ties. Without it two notes saved in the same
            // tick can swap places between two page requests, which makes an
            // offset page skip one row and show another twice.
            .OrderByDescending(n => n.CreatedAt)
            .ThenBy(n => n.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<(List<NoteModel> Items, int Total)> BrowseAsync(
        string? query, Guid? bookId, bool withoutTopics, bool oldestFirst, int limit, int offset,
        string? kind = null)
    {
        var notes = _db.Notes.AsNoTracking().AsQueryable();
        if (bookId.HasValue) notes = notes.Where(n => n.BookId == bookId.Value);
        if (!string.IsNullOrEmpty(kind)) notes = notes.Where(n => n.Kind == kind);
        if (withoutTopics) notes = notes.Where(n => !n.NoteTopics.Any());
        var term = query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            notes = WhereAnyFieldContains(notes, term);
        }

        var total = await notes.CountAsync();
        var ordered = oldestFirst
            ? notes.OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
            : notes.OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id);
        var items = await ordered.Skip(offset).Take(limit)
            .Include(n => n.Book)
            .Include(n => n.NoteTopics).ThenInclude(nc => nc.Topic)
            .ToListAsync();
        return (items, total);
    }

    public async Task<int> CountWithoutTopicsAsync()
    {
        return await _db.Notes.CountAsync(n => !n.NoteTopics.Any());
    }

    public Task<int> CountAsync() => _db.Notes.CountAsync();

    public async Task<IReadOnlyList<NoteBookCount>> GetBookCountsAsync(int limit)
    {
        var take = Math.Clamp(limit, 1, 50);
        var rows = await _db.Notes
            .AsNoTracking()
            .GroupBy(note => new
            {
                note.BookId,
                BookTitle = note.Book != null ? note.Book.Title : string.Empty,
            })
            .Select(group => new
            {
                group.Key.BookId,
                group.Key.BookTitle,
                NoteCount = group.Count(),
            })
            .OrderByDescending(item => item.NoteCount)
            .ThenBy(item => item.BookTitle)
            .ThenBy(item => item.BookId)
            .Take(take)
            .ToListAsync();

        return rows
            .Select(item => new NoteBookCount(
                item.BookId,
                item.BookTitle,
                item.NoteCount))
            .ToList();
    }

    /// <summary>
    /// `%` and `_` are wildcards inside LIKE, and `\` is the escape character
    /// passed above, so a user searching for "100%" must not match everything.
    /// </summary>
    /// <summary>
    /// Notes where <paramref name="term"/> appears in a field a person would
    /// describe the note by: its text, its quotation, its passage title, or its
    /// book's title or author (issue #1038). Search and Browse share this so the
    /// two boxes never disagree.
    ///
    /// Each field is tried as typed and lower-cased. SQLite's LIKE ignores ASCII
    /// case but its lower() leaves "Å" alone, so the as-typed form keeps "Åsa"
    /// finding "Åsa"; PostgreSQL's LIKE is case-sensitive, so the lower-cased
    /// form is what lets "schopenhauer" find "Schopenhauer" on Cloud.
    /// </summary>
    private static IQueryable<NoteModel> WhereAnyFieldContains(IQueryable<NoteModel> notes, string term)
    {
        var typed = $"%{Escape(term)}%";
        var lower = $"%{Escape(term.ToLowerInvariant())}%";
        return notes.Where(n =>
            EF.Functions.Like(n.Content, typed, "\\")
            || EF.Functions.Like(n.Content.ToLower(), lower, "\\")
            || (n.SelectedText != null
                && (EF.Functions.Like(n.SelectedText, typed, "\\")
                    || EF.Functions.Like(n.SelectedText.ToLower(), lower, "\\")))
            || (n.PassageTitle != null
                && (EF.Functions.Like(n.PassageTitle, typed, "\\")
                    || EF.Functions.Like(n.PassageTitle.ToLower(), lower, "\\")))
            || (n.Book != null && n.Book.Title != null
                && (EF.Functions.Like(n.Book.Title, typed, "\\")
                    || EF.Functions.Like(n.Book.Title.ToLower(), lower, "\\")))
            || (n.Book != null && n.Book.Author != null
                && (EF.Functions.Like(n.Book.Author, typed, "\\")
                    || EF.Functions.Like(n.Book.Author.ToLower(), lower, "\\"))));
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

}
