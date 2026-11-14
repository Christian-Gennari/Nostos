using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Writings;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Data.Repositories;

public class WritingRepository : IWritingRepository
{
    private readonly NostosDbContext _db;

    public WritingRepository(NostosDbContext db)
    {
        _db = db;
    }

    public async Task<List<WritingDto>> GetListAsync(CancellationToken cancellationToken = default)
    {
        // Projected so each document's full text (Content) never leaves the database.
        var rows = await _db.Writings
            .AsNoTracking()
            .Select(w => new { w.Id, w.Name, w.Type, w.ParentId, w.UpdatedAt })
            .ToListAsync(cancellationToken);
        return rows
            .Select(w => new WritingDto(w.Id, w.Name, w.Type.ToString(), w.ParentId, w.UpdatedAt))
            .ToList();
    }

    public async Task<WritingModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Writings.FindAsync([id], cancellationToken);
    }

    public async Task AddAsync(WritingModel writing, CancellationToken cancellationToken = default)
    {
        _db.Writings.Add(writing);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(WritingModel writing, CancellationToken cancellationToken = default)
    {
        _db.Writings.Update(writing);
        await _db.SaveChangesAsync(cancellationToken);
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

    public async Task DeleteAsync(WritingModel writing, CancellationToken cancellationToken = default)
    {
        // Cascade delete is configured in DbContext, so this deletes all children too.
        _db.Writings.Remove(writing);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, Guid?>> GetParentMapAsync(
        CancellationToken cancellationToken = default)
    {
        // Two GUID columns per writing: one query, walked in memory by
        // WritingTreeService instead of one parent read per folder level.
        return await _db
            .Writings.AsNoTracking()
            .Select(w => new { w.Id, w.ParentId })
            .ToDictionaryAsync(w => w.Id, w => w.ParentId, cancellationToken);
    }

    public async Task<List<WritingNoteModel>?> GetKeptNotesAsync(
        Guid writingId,
        CancellationToken cancellationToken = default)
    {
        var writingExists = await _db.Writings.AnyAsync(w => w.Id == writingId, cancellationToken);
        if (!writingExists)
            return null;

        return await WithNotesAsync(
            _db.WritingNotes
                .Where(wn => wn.WritingId == writingId)
                .OrderBy(wn => wn.AddedAt)
                .ThenBy(wn => wn.NoteId),
            cancellationToken);
    }

    /// <summary>Kept-note rows with their notes, each note with its book's headline.</summary>
    private async Task<List<WritingNoteModel>> WithNotesAsync(
        IQueryable<WritingNoteModel> links,
        CancellationToken cancellationToken)
    {
        var items = await links.AsNoTracking().ToListAsync(cancellationToken);
        var noteIds = items.Select(wn => wn.NoteId).ToList();
        var notes = (await _db.Notes.Where(n => noteIds.Contains(n.Id)).ToListWithBookHeadlineAsync(cancellationToken))
            .ToDictionary(n => n.Id);

        // Defensive read filter: a membership row whose note is missing must never 500
        foreach (var item in items)
            item.Note = notes.GetValueOrDefault(item.NoteId)!;
        return items.Where(wn => wn.Note is not null).ToList();
    }

    public async Task<AddKeptNoteResult> AddKeptNoteAsync(
        Guid writingId,
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        var writing = await _db.Writings.FirstOrDefaultAsync(w => w.Id == writingId, cancellationToken);
        if (writing is null)
            return new AddKeptNoteResult(AddKeptNoteStatus.WritingNotFound, null);

        if (writing.Type == WritingType.Folder)
            return new AddKeptNoteResult(AddKeptNoteStatus.WritingIsFolder, null);

        if (!await _db.Notes.AnyAsync(n => n.Id == noteId, cancellationToken))
            return new AddKeptNoteResult(AddKeptNoteStatus.NoteNotFound, null);

        // Idempotent. The join is keyed (WritingId, NoteId); inserting a second
        // row would be a duplicate key. The check runs against the database, not
        // the tracked navigation, so a repeated call sees the committed link.
        var link = _db.WritingNotes.Where(wn => wn.WritingId == writingId && wn.NoteId == noteId);
        var existing = (await WithNotesAsync(link, cancellationToken)).FirstOrDefault();

        if (existing is not null)
            return new AddKeptNoteResult(AddKeptNoteStatus.AlreadyExists, existing);

        _db.WritingNotes.Add(new WritingNoteModel
        {
            WritingId = writingId,
            Writing = writing,
            NoteId = noteId,
            AddedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(cancellationToken);

        return new AddKeptNoteResult(
            AddKeptNoteStatus.Success,
            (await WithNotesAsync(link, cancellationToken)).First());
    }

    public async Task<bool> RemoveKeptNoteAsync(
        Guid writingId,
        Guid noteId,
        CancellationToken cancellationToken = default)
    {
        var writingExists = await _db.Writings.AnyAsync(w => w.Id == writingId, cancellationToken);
        if (!writingExists)
            return false;

        var existing = await _db.WritingNotes
            .FirstOrDefaultAsync(wn => wn.WritingId == writingId && wn.NoteId == noteId, cancellationToken);

        if (existing is not null)
        {
            _db.WritingNotes.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}
