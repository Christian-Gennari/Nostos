using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Writings;

namespace Nostos.Backend.Data.Repositories;

public class WritingRepository : IWritingRepository
{
    private readonly NostosDbContext _db;

    public WritingRepository(NostosDbContext db)
    {
        _db = db;
    }

    public async Task<List<WritingModel>> GetAllAsync()
    {
        return await _db.Writings.ToListAsync();
    }

    public async Task<WritingModel?> GetByIdAsync(Guid id)
    {
        return await _db.Writings.FindAsync(id);
    }

    public async Task AddAsync(WritingModel writing)
    {
        _db.Writings.Add(writing);
        await _db.SaveChangesAsync();
    }

    public async Task UpdateAsync(WritingModel writing)
    {
        _db.Writings.Update(writing);
        await _db.SaveChangesAsync();
    }

    public async Task SaveContentAsync(WritingModel writing, CancellationToken ct = default)
    {
        // One transaction: the content save takes the writing's row lock (and
        // the library revision's), so a second tab saving the same writing
        // waits, then rebuilds from its own content. The last save wins and
        // its citation rows match the content it saved (issue #1162).
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        _db.Writings.Update(writing);
        await _db.SaveChangesAsync(ct);
        await WritingCitationIndex.SyncAsync(
            _db,
            writing.Id,
            writing.Type == WritingType.Document ? writing.Content : null,
            ct);
        await transaction.CommitAsync(ct);
    }

    public async Task DeleteAsync(WritingModel writing)
    {
        // Cascade delete is configured in DbContext, so this deletes all children too.
        _db.Writings.Remove(writing);
        await _db.SaveChangesAsync();
    }

    public async Task<Guid?> GetParentIdAsync(Guid id)
    {
        var result = await _db
            .Writings.Where(w => w.Id == id)
            .Select(w => new { w.ParentId })
            .FirstOrDefaultAsync();

        return result?.ParentId;
    }

    public async Task<List<WritingNoteModel>?> GetKeptNotesAsync(Guid writingId)
    {
        var writingExists = await _db.Writings.AnyAsync(w => w.Id == writingId);
        if (!writingExists)
            return null;

        return await WithNotesAsync(_db.WritingNotes
            .Where(wn => wn.WritingId == writingId)
            .OrderBy(wn => wn.AddedAt)
            .ThenBy(wn => wn.NoteId));
    }

    /// <summary>Kept-note rows with their notes, each note with its book's headline.</summary>
    private async Task<List<WritingNoteModel>> WithNotesAsync(IQueryable<WritingNoteModel> links)
    {
        var items = await links.AsNoTracking().ToListAsync();
        var noteIds = items.Select(wn => wn.NoteId).ToList();
        var notes = (await _db.Notes.Where(n => noteIds.Contains(n.Id)).ToListWithBookHeadlineAsync())
            .ToDictionary(n => n.Id);

        // Defensive read filter: a membership row whose note is missing must never 500
        foreach (var item in items)
            item.Note = notes.GetValueOrDefault(item.NoteId)!;
        return items.Where(wn => wn.Note is not null).ToList();
    }

    public async Task<AddKeptNoteResult> AddKeptNoteAsync(Guid writingId, Guid noteId)
    {
        var writing = await _db.Writings.FirstOrDefaultAsync(w => w.Id == writingId);
        if (writing is null)
            return new AddKeptNoteResult(AddKeptNoteStatus.WritingNotFound, null);

        if (writing.Type == WritingType.Folder)
            return new AddKeptNoteResult(AddKeptNoteStatus.WritingIsFolder, null);

        if (!await _db.Notes.AnyAsync(n => n.Id == noteId))
            return new AddKeptNoteResult(AddKeptNoteStatus.NoteNotFound, null);

        // Idempotent. The join is keyed (WritingId, NoteId); inserting a second
        // row would be a duplicate key. The check runs against the database, not
        // the tracked navigation, so a repeated call sees the committed link.
        var link = _db.WritingNotes.Where(wn => wn.WritingId == writingId && wn.NoteId == noteId);
        var existing = (await WithNotesAsync(link)).FirstOrDefault();

        if (existing is not null)
            return new AddKeptNoteResult(AddKeptNoteStatus.AlreadyExists, existing);

        _db.WritingNotes.Add(new WritingNoteModel
        {
            WritingId = writingId,
            Writing = writing,
            NoteId = noteId,
            AddedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        return new AddKeptNoteResult(AddKeptNoteStatus.Success, (await WithNotesAsync(link)).First());
    }

    public async Task<bool> RemoveKeptNoteAsync(Guid writingId, Guid noteId)
    {
        var writingExists = await _db.Writings.AnyAsync(w => w.Id == writingId);
        if (!writingExists)
            return false;

        var existing = await _db.WritingNotes
            .FirstOrDefaultAsync(wn => wn.WritingId == writingId && wn.NoteId == noteId);

        if (existing is not null)
        {
            _db.WritingNotes.Remove(existing);
            await _db.SaveChangesAsync();
        }

        return true;
    }
}
