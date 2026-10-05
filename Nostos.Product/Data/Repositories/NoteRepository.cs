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
        return await _db.Notes.Include(n => n.Book).Where(n => n.BookId == bookId).ToListAsync();
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
    public async Task<List<NoteModel>> SearchByTextAsync(
        string query,
        int limit,
        IReadOnlyCollection<Guid>? bookIds = null)
    {
        var term = query.Trim();
        if (term.Length == 0 || bookIds is { Count: 0 }) return [];

        // EF.Functions.Like keeps this a single query against the three text
        // columns a note really has. SQLite's LIKE is case-insensitive for ASCII,
        // which is the behaviour a search box should have.
        var pattern = $"%{Escape(term)}%";
        var notes = _db
            .Notes.Include(n => n.Book)
            .Include(n => n.NoteTopics)
            .ThenInclude(nc => nc.Topic)
            .AsQueryable();

        if (bookIds is { Count: > 0 })
            notes = notes.Where(n => bookIds.Contains(n.BookId));

        return await notes
            .Where(n =>
                EF.Functions.Like(n.Content, pattern, "\\")
                || (n.SelectedText != null && EF.Functions.Like(n.SelectedText, pattern, "\\"))
                || (n.Book != null && n.Book.Title != null && EF.Functions.Like(n.Book.Title, pattern, "\\"))
            )
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
        string? query, Guid? bookId, bool withoutTopics, bool oldestFirst, int limit, int offset)
    {
        var notes = _db.Notes.AsNoTracking().AsQueryable();
        if (bookId.HasValue) notes = notes.Where(n => n.BookId == bookId.Value);
        if (withoutTopics) notes = notes.Where(n => !n.NoteTopics.Any());
        var term = query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var pattern = $"%{Escape(term)}%";
            notes = notes.Where(n =>
                EF.Functions.Like(n.Content, pattern, "\\")
                || (n.SelectedText != null && EF.Functions.Like(n.SelectedText, pattern, "\\"))
                || (n.Book != null && EF.Functions.Like(n.Book.Title, pattern, "\\")));
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
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

}
