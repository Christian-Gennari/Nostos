using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;

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

        var items = await _db.WritingNotes
            .AsNoTracking()
            .Where(wn => wn.WritingId == writingId)
            .Include(wn => wn.Note)
                .ThenInclude(n => n.Book)
            .OrderBy(wn => wn.AddedAt)
            .ThenBy(wn => wn.NoteId)
            .ToListAsync();

        // Defensive read filter: a membership row whose note is missing must never 500
        return items.Where(wn => wn.Note is not null).ToList();
    }

    public async Task<AddKeptNoteResult> AddKeptNoteAsync(Guid writingId, Guid noteId)
    {
        var writing = await _db.Writings.FirstOrDefaultAsync(w => w.Id == writingId);
        if (writing is null)
            return new AddKeptNoteResult(AddKeptNoteStatus.WritingNotFound, null);

        if (writing.Type == WritingType.Folder)
            return new AddKeptNoteResult(AddKeptNoteStatus.WritingIsFolder, null);

        var note = await _db.Notes
            .Include(n => n.Book)
            .FirstOrDefaultAsync(n => n.Id == noteId);
        if (note is null)
            return new AddKeptNoteResult(AddKeptNoteStatus.NoteNotFound, null);

        // Idempotent. The join is keyed (WritingId, NoteId); inserting a second
        // row would be a duplicate key. The check runs against the database, not
        // the tracked navigation, so a repeated call sees the committed link.
        var existing = await _db.WritingNotes
            .AsNoTracking()
            .Include(wn => wn.Note)
                .ThenInclude(n => n.Book)
            .FirstOrDefaultAsync(wn => wn.WritingId == writingId && wn.NoteId == noteId);

        if (existing is not null)
            return new AddKeptNoteResult(AddKeptNoteStatus.AlreadyExists, existing);

        var writingNote = new WritingNoteModel
        {
            WritingId = writingId,
            Writing = writing,
            NoteId = noteId,
            Note = note,
            AddedAt = DateTime.UtcNow,
        };

        _db.WritingNotes.Add(writingNote);
        await _db.SaveChangesAsync();

        return new AddKeptNoteResult(AddKeptNoteStatus.Success, writingNote);
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
