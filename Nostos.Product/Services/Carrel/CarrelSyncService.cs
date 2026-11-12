using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Portability.Markdown;
using Nostos.Backend.Services.Writings;

namespace Nostos.Backend.Services.Carrel;

/// <summary>The outcome of a <c>PUT</c>.</summary>
public abstract record CarrelPutOutcome
{
    public sealed record Success(long Rev, string Hash) : CarrelPutOutcome;

    public sealed record Conflict(CarrelConflictInfo Info) : CarrelPutOutcome;

    /// <summary>A path or body the protocol refuses (400).</summary>
    public sealed record Invalid(string Error) : CarrelPutOutcome;
}

/// <summary>The outcome of a <c>DELETE</c>.</summary>
public abstract record CarrelDeleteOutcome
{
    public sealed record Deleted(long Rev) : CarrelDeleteOutcome;

    public sealed record Conflict(CarrelConflictInfo Info) : CarrelDeleteOutcome;
}

/// <summary>
/// The Carrel Sync Protocol v1 file and library feeds (WP8), mapped onto
/// Writing Studio drafts and the note library.
///
/// <para><b>Files.</b> Each protocol file is one Studio document. Its path is
/// the document's folder chain plus its name and <c>.md</c>; its content is
/// the document's Markdown, byte for byte, so a <c>nostos:cite/</c> link a
/// device writes stays exactly as written. Models are not touched on save:
/// before every request the service reconciles the Studio tree against the
/// mapping rows, so a draft edited (or renamed, moved or deleted) in the
/// Studio gets a new revision and cursor without every writer having to know
/// about the protocol.</para>
///
/// <para><b>Library.</b> Highlights, notes and passages are exported with
/// their book, topics and a ready-to-use citation. The same reconciliation
/// idea gives deletions: a note that is gone (deleted alone, or with its book
/// or through an import's bulk delete) is reported as a tombstone instead of
/// silently disappearing from a device that cached it.</para>
///
/// <para><b>Cursors.</b> Each owner has one row with two counters. Every
/// change takes the row's write lock first, then receives cursor values, then
/// commits, so cursor order is commit order: a device that stores a published
/// cursor can never skip a change that commits later. The cursor a request
/// returns is never ahead of the changes it actually returned.</para>
/// </summary>
public sealed partial class CarrelSyncService(ICarrelDeviceDirectory directory)
{
    /// <summary>Most a device may send; the protocol's own limit.</summary>
    public const int MaxFileBytes = CarrelPaths.MaxFileBytes;

    public const string InvalidCursor = "invalid cursor";

    private static readonly DateTime Epoch = DateTime.UnixEpoch;

    // ------------------------------------------------------------------
    // Files
    // ------------------------------------------------------------------

    public async Task<CarrelFileList> ListFilesAsync(
        string ownerKey, string? since, CancellationToken ct = default)
    {
        if (!TryParseCursor(since, out var sinceValue, out var cursorError))
            throw new CarrelCursorException(cursorError);

        await using var db = await directory.OpenOwnerAsync(ownerKey, ct);
        await EnsureStateAsync(ownerKey, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockStateAsync(db, ownerKey, ct);
        await ReconcileFilesAsync(db, ownerKey, DateTime.UtcNow, ct);

        var rows = await db.CarrelFiles
            .AsNoTracking()
            .Where(f => f.OwnerKey == ownerKey && f.Cursor > sinceValue)
            .OrderBy(f => f.Cursor)
            .ToListAsync(ct);
        await transaction.CommitAsync(ct);

        var entries = rows.Select(ToEntry).ToList();
        var cursor = rows.Count == 0 ? sinceValue : Math.Max(sinceValue, rows[^1].Cursor);
        return new CarrelFileList(entries, cursor.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>One live file's content, with the hash of exactly these bytes.</summary>
    public async Task<CarrelFileContent?> GetFileAsync(
        string ownerKey, Guid fileId, CancellationToken ct = default)
    {
        await using var db = await directory.OpenOwnerAsync(ownerKey, ct);
        await EnsureStateAsync(ownerKey, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockStateAsync(db, ownerKey, ct);
        await ReconcileFilesAsync(db, ownerKey, DateTime.UtcNow, ct);

        var file = await db.CarrelFiles.FirstOrDefaultAsync(
            f => f.FileId == fileId && f.OwnerKey == ownerKey, ct);
        if (file is null || file.Deleted)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        var draft = await db.Writings
            .AsNoTracking()
            .Where(w => w.Id == file.DraftId && w.Type == WritingType.Document)
            .Select(w => new { w.Content })
            .FirstOrDefaultAsync(ct);
        var content = draft?.Content ?? string.Empty;
        var hash = CarrelTokens.ContentHash(content);

        // A Studio edit that landed between the reconciliation and this read:
        // report the content and the revision that belong to each other, so no
        // device is ever handed a hash of different bytes.
        if (!CarrelTokens.HashEquals(file.Hash, hash))
        {
            var now = DateTime.UtcNow;
            await db.SaveChangesAsync(ct); // whatever reconciliation changed
            file.Rev++;
            file.Hash = hash;
            file.ModifiedAtUtc = now;
            file.Cursor = await AllocateAsync(db, ownerKey, 1, library: false, now, ct) + 1;
            await db.SaveChangesAsync(ct);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new CarrelFileContent(
            file.FileId.ToString("D"), file.Path, hash, file.Rev, content);
    }

    public async Task<CarrelPutOutcome> PutFileAsync(
        string ownerKey, Guid fileId, CarrelPutFileRequest request, CancellationToken ct = default)
    {
        var pathError = CarrelPaths.Validate(request.Path);
        if (pathError is not null)
            return new CarrelPutOutcome.Invalid(pathError);
        if (Encoding.UTF8.GetByteCount(request.Content ?? string.Empty) > MaxFileBytes)
            return new CarrelPutOutcome.Invalid("file is larger than 16777216 bytes");

        var content = request.Content ?? string.Empty;
        var hash = CarrelTokens.ContentHash(content);

        await using var db = await directory.OpenOwnerAsync(ownerKey, ct);
        await EnsureStateAsync(ownerKey, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        await LockStateAsync(db, ownerKey, ct);
        await ReconcileFilesAsync(db, ownerKey, now, ct);
        await db.SaveChangesAsync(ct);

        var file = await db.CarrelFiles.FirstOrDefaultAsync(
            f => f.FileId == fileId && f.OwnerKey == ownerKey, ct);

        // A different live file already owns this path. The device moves it to
        // a conflict copy and retries, which is why the answer names it.
        var occupant = await db.CarrelFiles
            .AsNoTracking()
            .Where(f => f.OwnerKey == ownerKey && f.Path == request.Path && !f.Deleted && f.FileId != fileId)
            .OrderBy(f => f.FileId)
            .FirstOrDefaultAsync(ct);
        if (occupant is not null)
        {
            await transaction.CommitAsync(ct);
            return new CarrelPutOutcome.Conflict(new CarrelConflictInfo(
                occupant.Rev, occupant.Hash, occupant.FileId.ToString("D")));
        }

        if (file is null)
        {
            // `base_rev` non-null means the device believes the file exists;
            // rev 0 with no file id is the crate's "server does not know it".
            if (request.BaseRev is not null)
            {
                await transaction.CommitAsync(ct);
                return new CarrelPutOutcome.Conflict(new CarrelConflictInfo(0, string.Empty, null));
            }

            var draft = await CreateDraftAsync(db, request.Path, content, now, ct);
            file = new CarrelFileModel
            {
                FileId = fileId,
                OwnerKey = ownerKey,
                DraftId = draft.Id,
                Path = request.Path,
                Hash = hash,
                Rev = 1,
                Deleted = false,
                ModifiedAtUtc = now,
                DraftUpdatedAtUtc = draft.UpdatedAt,
            };
            db.CarrelFiles.Add(file);
            await db.SaveChangesAsync(ct);
            await WritingCitationIndex.SyncAsync(db, draft.Id, content, ct);
            file.Cursor = await AllocateAsync(db, ownerKey, 1, library: false, now, ct) + 1;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new CarrelPutOutcome.Success(file.Rev, file.Hash);
        }

        if (file.Rev != request.BaseRev)
        {
            await transaction.CommitAsync(ct);
            return new CarrelPutOutcome.Conflict(new CarrelConflictInfo(file.Rev, file.Hash, null));
        }

        // A retried PUT of the same bytes at the same path is the same save:
        // the answer is the current revision, not a spurious new one.
        if (!file.Deleted && file.Path == request.Path && CarrelTokens.HashEquals(file.Hash, hash))
        {
            await transaction.CommitAsync(ct);
            return new CarrelPutOutcome.Success(file.Rev, file.Hash);
        }

        var existing = await db.Writings.FirstOrDefaultAsync(
            w => w.Id == file.DraftId && w.Type == WritingType.Document, ct);
        if (existing is null)
        {
            // The draft was deleted in the Studio and the device is restoring
            // the file it still has: materialise it again under the same id.
            existing = await CreateDraftAsync(db, request.Path, content, now, ct);
            file.DraftId = existing.Id;
        }
        else
        {
            await ApplyPathAsync(db, existing, request.Path, now, ct);
            existing.Content = content;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await WritingCitationIndex.SyncAsync(db, existing.Id, content, ct);
        }

        file.Rev++;
        file.Deleted = false;
        file.Path = request.Path;
        file.Hash = hash;
        file.ModifiedAtUtc = now;
        file.DraftUpdatedAtUtc = existing.UpdatedAt;
        file.Cursor = await AllocateAsync(db, ownerKey, 1, library: false, now, ct) + 1;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CarrelPutOutcome.Success(file.Rev, file.Hash);
    }

    public async Task<CarrelDeleteOutcome> DeleteFileAsync(
        string ownerKey, Guid fileId, long? baseRev, CancellationToken ct = default)
    {
        await using var db = await directory.OpenOwnerAsync(ownerKey, ct);
        await EnsureStateAsync(ownerKey, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        await LockStateAsync(db, ownerKey, ct);
        await ReconcileFilesAsync(db, ownerKey, now, ct);
        await db.SaveChangesAsync(ct);

        var file = await db.CarrelFiles.FirstOrDefaultAsync(
            f => f.FileId == fileId && f.OwnerKey == ownerKey, ct);

        // Deleting a file the server never had is a no-op, so a retried
        // delete stays idempotent (the crate's reference server answers 0).
        if (file is null)
        {
            await transaction.CommitAsync(ct);
            return new CarrelDeleteOutcome.Deleted(0);
        }

        if (file.Rev != baseRev)
        {
            await transaction.CommitAsync(ct);
            return new CarrelDeleteOutcome.Conflict(new CarrelConflictInfo(file.Rev, file.Hash, null));
        }

        if (file.Deleted)
        {
            await transaction.CommitAsync(ct);
            return new CarrelDeleteOutcome.Deleted(file.Rev);
        }

        // The draft goes with the file: leaving it behind would make the next
        // reconciliation resurrect the file the person just deleted.
        var draft = await db.Writings.FirstOrDefaultAsync(w => w.Id == file.DraftId, ct);
        if (draft is not null)
            db.Writings.Remove(draft);

        file.Rev++;
        file.Deleted = true;
        file.ModifiedAtUtc = now;
        file.Cursor = await AllocateAsync(db, ownerKey, 1, library: false, now, ct) + 1;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CarrelDeleteOutcome.Deleted(file.Rev);
    }

    // ------------------------------------------------------------------
    // Library
    // ------------------------------------------------------------------

    public async Task<CarrelLibraryPage> ListLibraryAsync(
        string ownerKey, string? since, string baseUrl, CancellationToken ct = default)
    {
        if (!TryParseCursor(since, out var sinceValue, out var cursorError))
            throw new CarrelCursorException(cursorError);

        await using var db = await directory.OpenOwnerAsync(ownerKey, ct);
        await EnsureStateAsync(ownerKey, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        await LockStateAsync(db, ownerKey, ct);
        await ReconcileLibraryAsync(db, ownerKey, now, ct);
        await db.SaveChangesAsync(ct);

        var mappings = await db.CarrelLibraryItems
            .AsNoTracking()
            .Where(i => i.OwnerKey == ownerKey)
            .ToListAsync(ct);
        var changed = mappings.Where(i => i.Cursor > sinceValue).ToList();

        // Only the items this sync returns need their full text; the rest of
        // the library was compared on its version, book and topics alone.
        var wanted = changed.Where(i => !i.Deleted).Select(i => i.NoteId).ToList();
        var notes = await LoadNotesAsync(db, wanted, ct);
        var topics = await LoadTopicsAsync(db, wanted, ct);

        var items = new List<CarrelLibraryItem>(wanted.Count);
        foreach (var note in notes.Values)
        {
            items.Add(ToLibraryItem(note, topics.GetValueOrDefault(note.Id, []), baseUrl));
        }

        // Every book a live item names, so a device can refresh its shelves.
        var books = await db.Notes
            .AsNoTracking()
            .Where(n => db.CarrelLibraryItems.Any(
                i => i.OwnerKey == ownerKey && !i.Deleted && i.NoteId == n.Id))
            .Select(n => new { n.BookId, n.Book!.Title, n.Book.Author })
            .ToListAsync(ct);

        await transaction.CommitAsync(ct);

        var deleted = changed.Where(i => i.Deleted).Select(i => i.NoteId.ToString("D")).ToList();
        var cursor = changed.Count == 0
            ? sinceValue
            : Math.Max(sinceValue, changed.Max(i => i.Cursor));
        return new CarrelLibraryPage(
            books
                .GroupBy(b => b.BookId)
                .Select(g => new CarrelLibraryBook(g.Key.ToString("D"), g.First().Title, g.First().Author))
                .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            items.OrderBy(i => i.Id, StringComparer.Ordinal).ToList(),
            deleted.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            cursor.ToString(CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------
    // Reconciliation (files)
    // ------------------------------------------------------------------

    private sealed record WritingRow(
        Guid Id, string Name, WritingType Type, Guid? ParentId, DateTime UpdatedAt);

    /// <summary>
    /// Brings the file rows in line with the Studio tree: a new document becomes
    /// a file, a renamed, moved or edited one gets the next revision, and a
    /// document that is gone leaves a tombstone.
    /// </summary>
    private static async Task ReconcileFilesAsync(
        NostosDbContext db, string ownerKey, DateTime now, CancellationToken ct)
    {
        var writings = await db.Writings
            .AsNoTracking()
            .Select(w => new WritingRow(w.Id, w.Name, w.Type, w.ParentId, w.UpdatedAt))
            .ToListAsync(ct);
        var files = await db.CarrelFiles
            .Where(f => f.OwnerKey == ownerKey)
            .ToListAsync(ct);
        var byDraft = files.ToDictionary(f => f.DraftId);
        var byId = writings.ToDictionary(w => w.Id);

        var documents = writings
            .Where(w => w.Type == WritingType.Document)
            .OrderBy(w => w.Id)
            .ToList();

        var paths = new Dictionary<Guid, string>(documents.Count);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            paths[document.Id] = CarrelPaths.Unique(
                CarrelPaths.ForDocument(FolderChain(byId, document), document.Name), taken);
        }

        // Only drafts that are new or edited since the last pass need their
        // text read; a path-only change keeps the hash it already had.
        var needContent = new HashSet<Guid>();
        foreach (var document in documents)
        {
            if (!byDraft.TryGetValue(document.Id, out var file)
                || file.Deleted
                || file.DraftUpdatedAtUtc != document.UpdatedAt)
            {
                needContent.Add(document.Id);
            }
        }

        var contents = await LoadContentAsync(db, needContent, ct);
        var touched = new List<CarrelFileModel>();

        foreach (var document in documents)
        {
            var path = paths[document.Id];
            if (!byDraft.TryGetValue(document.Id, out var file))
            {
                file = new CarrelFileModel
                {
                    FileId = Guid.CreateVersion7(),
                    OwnerKey = ownerKey,
                    DraftId = document.Id,
                    Path = path,
                    Hash = CarrelTokens.ContentHash(contents.GetValueOrDefault(document.Id, string.Empty)),
                    Rev = 1,
                    Deleted = false,
                    ModifiedAtUtc = now,
                    DraftUpdatedAtUtc = document.UpdatedAt,
                };
                db.CarrelFiles.Add(file);
                touched.Add(file);
                continue;
            }

            var pathChanged = file.Path != path;
            var draftChanged = file.DraftUpdatedAtUtc != document.UpdatedAt;
            if (!file.Deleted && !pathChanged && !draftChanged)
                continue;

            if (draftChanged || file.Deleted)
            {
                file.Hash = CarrelTokens.ContentHash(contents.GetValueOrDefault(document.Id, string.Empty));
            }

            file.Rev++;
            file.Path = path;
            file.Deleted = false;
            file.ModifiedAtUtc = now;
            file.DraftUpdatedAtUtc = document.UpdatedAt;
            touched.Add(file);
        }

        var live = documents.Select(d => d.Id).ToHashSet();
        foreach (var file in files.Where(f => !f.Deleted && !live.Contains(f.DraftId)))
        {
            file.Rev++;
            file.Deleted = true;
            file.ModifiedAtUtc = now;
            touched.Add(file);
        }

        await StampFilesAsync(db, ownerKey, touched, now, ct);
    }

    /// <summary>The folder names above a document, outermost first.</summary>
    private static List<string> FolderChain(
        IReadOnlyDictionary<Guid, WritingRow> byId, WritingRow document)
    {
        var names = new List<string>();
        var seen = new HashSet<Guid>();
        var parentId = document.ParentId;
        while (parentId is { } id && seen.Add(id) && byId.TryGetValue(id, out var parent))
        {
            if (parent.Type == WritingType.Folder)
                names.Add(parent.Name);
            parentId = parent.ParentId;
        }

        names.Reverse();
        return names;
    }

    private static async Task<Dictionary<Guid, string>> LoadContentAsync(
        NostosDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var chunk in ids.Chunk(400))
        {
            var batch = chunk.ToList();
            var rows = await db.Writings
                .AsNoTracking()
                .Where(w => batch.Contains(w.Id))
                .Select(w => new { w.Id, w.Content })
                .ToListAsync(ct);
            foreach (var row in rows)
                result[row.Id] = row.Content ?? string.Empty;
        }

        return result;
    }

    private sealed record LibraryRow(
        Guid Id,
        Guid BookId,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        string Kind,
        string? PassageTitle,
        string? BookTitle,
        string? BookAuthor);

    /// <summary>
    /// Brings the exported-note rows in line with the library. A note is new or
    /// changed when its version, book headline, topics or kind differ; a note
    /// that is gone (deleted alone, with its book, or by an import's bulk
    /// delete) is a tombstone, because a deleted row cannot describe itself.
    /// </summary>
    private static async Task ReconcileLibraryAsync(
        NostosDbContext db, string ownerKey, DateTime now, CancellationToken ct)
    {
        var notes = await db.Notes
            .AsNoTracking()
            .Where(n => n.Book != null)
            .Select(n => new LibraryRow(
                n.Id,
                n.BookId,
                n.CreatedAt,
                n.UpdatedAt,
                n.Kind,
                n.PassageTitle,
                n.Book!.Title,
                n.Book.Author))
            .ToListAsync(ct);

        var topicRows = await db.NoteTopics
            .AsNoTracking()
            .Select(nt => new { nt.NoteId, Name = nt.Topic.Topic })
            .ToListAsync(ct);
        var topicsByNote = topicRows
            .GroupBy(t => t.NoteId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());

        var items = await db.CarrelLibraryItems
            .Where(i => i.OwnerKey == ownerKey)
            .ToListAsync(ct);
        var byNote = items.ToDictionary(i => i.NoteId);
        var touched = new List<CarrelLibraryItemModel>();

        foreach (var note in notes)
        {
            var signature = LibrarySignature(note, topicsByNote.GetValueOrDefault(note.Id, []));
            if (!byNote.TryGetValue(note.Id, out var item))
            {
                item = new CarrelLibraryItemModel
                {
                    OwnerKey = ownerKey,
                    NoteId = note.Id,
                    Signature = signature,
                    Deleted = false,
                    ModifiedAtUtc = now,
                };
                db.CarrelLibraryItems.Add(item);
                touched.Add(item);
                continue;
            }

            if (!item.Deleted && item.Signature == signature)
                continue;

            item.Signature = signature;
            item.Deleted = false;
            item.ModifiedAtUtc = now;
            touched.Add(item);
        }

        var live = notes.Select(n => n.Id).ToHashSet();
        foreach (var item in items.Where(i => !i.Deleted && !live.Contains(i.NoteId)))
        {
            item.Deleted = true;
            item.ModifiedAtUtc = now;
            touched.Add(item);
        }

        await StampLibraryAsync(db, ownerKey, touched, now, ct);
    }

    /// <summary>
    /// The version that decides whether a device is told about an item: the
    /// note's own version plus the book headline and topics, none of which
    /// moves the note's <c>UpdatedAt</c>. The text itself is not hashed again;
    /// every edit to it stamps <c>UpdatedAt</c> in the context's save pipeline.
    /// </summary>
    private static string LibrarySignature(LibraryRow note, IReadOnlyList<string> topics)
    {
        var canonical = string.Join(
            '\u001f',
            note.Id.ToString("D"),
            (note.UpdatedAt ?? note.CreatedAt).Ticks.ToString(CultureInfo.InvariantCulture),
            note.Kind ?? string.Empty,
            note.PassageTitle ?? string.Empty,
            note.BookId.ToString("D"),
            note.BookTitle ?? string.Empty,
            note.BookAuthor ?? string.Empty,
            string.Join('\u001e', topics));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record FullNote(
        Guid Id,
        Guid BookId,
        string? Content,
        string? SelectedText,
        string? CfiRange,
        DateTime CreatedAt,
        string Kind,
        string? PassageTitle,
        string? PassageEnd,
        string SourceAnchorKind,
        string? SourceAnchorValue,
        bool AnchorVerified,
        string? BookTitle,
        string? BookAuthor);

    private static async Task<Dictionary<Guid, FullNote>> LoadNotesAsync(
        NostosDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, FullNote>();
        foreach (var chunk in ids.Chunk(400))
        {
            var batch = chunk.ToList();
            var rows = await db.Notes
                .AsNoTracking()
                .Where(n => batch.Contains(n.Id) && n.Book != null)
                .Select(n => new FullNote(
                    n.Id,
                    n.BookId,
                    n.Content,
                    n.SelectedText,
                    n.CfiRange,
                    n.CreatedAt,
                    n.Kind,
                    n.PassageTitle,
                    n.PassageEnd,
                    n.SourceAnchorKind,
                    n.SourceAnchorValue,
                    n.AnchorVerified,
                    n.Book!.Title,
                    n.Book.Author))
                .ToListAsync(ct);
            foreach (var row in rows)
                result[row.Id] = row;
        }

        return result;
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadTopicsAsync(
        NostosDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var chunk in ids.Chunk(400))
        {
            var batch = chunk.ToList();
            var rows = await db.NoteTopics
                .AsNoTracking()
                .Where(nt => batch.Contains(nt.NoteId))
                .Select(nt => new { nt.NoteId, Name = nt.Topic.Topic })
                .ToListAsync(ct);
            foreach (var group in rows.GroupBy(r => r.NoteId))
            {
                result[group.Key] = group
                    .Select(r => r.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        return result;
    }

    private static CarrelLibraryItem ToLibraryItem(
        FullNote note, IReadOnlyList<string> topics, string baseUrl)
    {
        var kind = note.Kind == NoteKinds.Passage
            ? "passage"
            : string.IsNullOrWhiteSpace(note.SelectedText) ? "note" : "highlight";
        var thought = WikiLinksAsWords(MarkdownNotesRenderer.Thought(note.Content));
        var text = kind == "note"
            ? thought ?? WikiLinksAsWords(Clean(note.Content)) ?? string.Empty
            : (note.SelectedText ?? string.Empty).Trim();

        var located = MarkdownNotesRenderer.Locate(new MarkdownNote(
            note.Id,
            note.Content ?? string.Empty,
            note.CfiRange,
            note.SelectedText,
            note.CreatedAt,
            note.Kind,
            note.SourceAnchorKind,
            note.SourceAnchorValue,
            note.AnchorVerified,
            note.PassageTitle,
            note.PassageEnd,
            [])).Words;
        var passageTitle = kind == "passage" ? Clean(note.PassageTitle) : null;
        var location = located ?? (passageTitle is null ? null : $"“{passageTitle}”");

        return new CarrelLibraryItem(
            note.Id.ToString("D"),
            kind,
            note.BookId.ToString("D"),
            text,
            kind == "note" ? null : thought,
            location,
            topics,
            new CarrelLibraryCitation(
                CitationMarkdown(note.BookAuthor, note.BookTitle, located, passageTitle),
                $"{baseUrl.TrimEnd('/')}/brain/notes/{note.Id:D}"));
    }

    /// <summary>
    /// The footnote text for one quote: <c>Author, *Title*, p. 42</c>. A passage
    /// is named rather than paged, as the Studio's house citation style does.
    /// </summary>
    public static string CitationMarkdown(
        string? author, string? title, string? location, string? passageTitle)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(author))
            parts.Add(EscapeMarkdown(Clean(author)!));
        var cleanTitle = EscapeMarkdown(Clean(title) ?? "Untitled");
        parts.Add($"*{cleanTitle}*");
        if (!string.IsNullOrWhiteSpace(passageTitle))
            parts.Add($"“{EscapeMarkdown(Clean(passageTitle)!)}”");
        else if (CitationLocator(location) is { } locator)
            parts.Add(locator);
        return string.Join(", ", parts);
    }

    /// <summary><c>Page 42</c> → <c>p. 42</c>, in the Studio's house style. Anything else is kept.</summary>
    private static string? CitationLocator(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;
        var value = location.Trim();
        if (value.StartsWith("Pages ", StringComparison.Ordinal))
            return "pp. " + value["Pages ".Length..];
        if (value.StartsWith("Page ", StringComparison.Ordinal))
            return "p. " + value["Page ".Length..];
        if (value.StartsWith("At ", StringComparison.Ordinal))
            return "at " + value["At ".Length..];
        if (value.StartsWith("From ", StringComparison.Ordinal) && value.Contains(" to ", StringComparison.Ordinal))
            return "at " + value["From ".Length..].Replace(" to ", "–", StringComparison.Ordinal);
        if (value.StartsWith("In ", StringComparison.Ordinal))
            return value["In ".Length..];
        return value;
    }

    private static string? Clean(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }

    // The app shows [[Target]] or [[Target|shown words]] as the words, so a
    // device quoting the note sees what the person sees, not the link syntax.
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\[\[((?:(?!\[\[|\]\])[^\r\n|])*)(?:\|([^\[\]\r\n]*))?\]\]")]
    private static partial System.Text.RegularExpressions.Regex TopicLinkRegex();

    private static string? WikiLinksAsWords(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("[[", StringComparison.Ordinal))
            return text;
        return TopicLinkRegex().Replace(text, match =>
        {
            var shown = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[1].Value;
            return shown.Trim();
        });
    }

    /// <summary>Escape the Markdown the citation is written in, as the Studio does.</summary>
    private static string EscapeMarkdown(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']')
                builder.Append('\\');
            builder.Append(c);
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------
    // Drafts and paths
    // ------------------------------------------------------------------

    /// <summary>Make (or find) a document at <paramref name="path"/> with <paramref name="content"/>.</summary>
    private static async Task<WritingModel> CreateDraftAsync(
        NostosDbContext db, string path, string content, DateTime now, CancellationToken ct)
    {
        var (folders, name) = CarrelPaths.Split(path);
        var parentId = await ResolveFoldersAsync(db, folders, ct);
        var draft = new WritingModel
        {
            Name = name,
            Type = WritingType.Document,
            Content = content,
            ParentId = parentId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Writings.Add(draft);
        await db.SaveChangesAsync(ct);
        return draft;
    }

    /// <summary>Rename and move a draft to the folder chain the device's path names.</summary>
    private static async Task ApplyPathAsync(
        NostosDbContext db, WritingModel draft, string path, DateTime now, CancellationToken ct)
    {
        var (folders, name) = CarrelPaths.Split(path);
        var parentId = await ResolveFoldersAsync(db, folders, ct);
        if (draft.Name != name)
            draft.Name = name;
        if (draft.ParentId != parentId)
            draft.ParentId = parentId;
        draft.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The id of the folder chain <paramref name="folders"/> names, creating the
    /// folders that do not exist. A folder is matched by parent and exact name,
    /// so two devices writing the same path find the same folder.
    /// </summary>
    private static async Task<Guid?> ResolveFoldersAsync(
        NostosDbContext db, IReadOnlyList<string> folders, CancellationToken ct)
    {
        Guid? parentId = null;
        foreach (var name in folders)
        {
            var currentParent = parentId;
            var existing = await db.Writings
                .Where(w => w.Type == WritingType.Folder
                    && w.ParentId == currentParent
                    && w.Name == name)
                .OrderBy(w => w.Id)
                .Select(w => (Guid?)w.Id)
                .FirstOrDefaultAsync(ct);
            if (existing is { } id)
            {
                parentId = id;
                continue;
            }

            var folder = new WritingModel
            {
                Name = name,
                Type = WritingType.Folder,
                ParentId = currentParent,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Writings.Add(folder);
            await db.SaveChangesAsync(ct);
            parentId = folder.Id;
        }

        return parentId;
    }

    // ------------------------------------------------------------------
    // Cursors
    // ------------------------------------------------------------------

    /// <summary>Creates this owner's cursor row once, outside the request's transaction.</summary>
    private async Task EnsureStateAsync(string ownerKey, CancellationToken ct)
    {
        await using var db = await directory.OpenOwnerAsync(ownerKey, ct);
        if (await db.CarrelSyncStates.AsNoTracking().AnyAsync(s => s.OwnerKey == ownerKey, ct))
            return;

        db.CarrelSyncStates.Add(new CarrelSyncStateModel { OwnerKey = ownerKey });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request created it first; the row is all that matters.
        }
    }

    /// <summary>
    /// Takes the owner's cursor row for the rest of the transaction. An update
    /// of the row locks it (SQLite serialises writers; PostgreSQL locks the
    /// row), so two changes cannot be handed cursors in one order and commit
    /// in another.
    /// </summary>
    private static async Task LockStateAsync(
        NostosDbContext db, string ownerKey, CancellationToken ct)
    {
        await db.CarrelSyncStates
            .Where(s => s.OwnerKey == ownerKey)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAtUtc, x => x.UpdatedAtUtc), ct);
    }

    private static async Task<long> AllocateAsync(
        NostosDbContext db, string ownerKey, int count, bool library, DateTime now, CancellationToken ct)
    {
        if (count <= 0)
            return 0;

        var row = db.CarrelSyncStates.Where(s => s.OwnerKey == ownerKey);
        if (library)
        {
            await row.ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LibraryCursor, x => x.LibraryCursor + count)
                    .SetProperty(x => x.UpdatedAtUtc, now),
                ct);
            var end = await db.CarrelSyncStates
                .AsNoTracking()
                .Where(s => s.OwnerKey == ownerKey)
                .Select(s => s.LibraryCursor)
                .SingleAsync(ct);
            return end - count;
        }

        await row.ExecuteUpdateAsync(
            s => s.SetProperty(x => x.FilesCursor, x => x.FilesCursor + count)
                .SetProperty(x => x.UpdatedAtUtc, now),
            ct);
        var filesEnd = await db.CarrelSyncStates
            .AsNoTracking()
            .Where(s => s.OwnerKey == ownerKey)
            .Select(s => s.FilesCursor)
            .SingleAsync(ct);
        return filesEnd - count;
    }

    /// <summary>The cursor of the last change for every touched file row, in one allocation.</summary>
    private static async Task StampFilesAsync(
        NostosDbContext db,
        string ownerKey,
        IReadOnlyList<CarrelFileModel> touched,
        DateTime now,
        CancellationToken ct)
    {
        if (touched.Count == 0)
            return;

        var baseCursor = await AllocateAsync(db, ownerKey, touched.Count, library: false, now, ct);
        var next = baseCursor;
        foreach (var file in touched)
            file.Cursor = ++next;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The cursor of the last change for every touched library row, in one allocation.</summary>
    private static async Task StampLibraryAsync(
        NostosDbContext db,
        string ownerKey,
        IReadOnlyList<CarrelLibraryItemModel> touched,
        DateTime now,
        CancellationToken ct)
    {
        if (touched.Count == 0)
            return;

        var baseCursor = await AllocateAsync(db, ownerKey, touched.Count, library: true, now, ct);
        var next = baseCursor;
        foreach (var item in touched)
            item.Cursor = ++next;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------
    // Wire helpers
    // ------------------------------------------------------------------

    public static bool TryParseCursor(string? since, out long value, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(since))
        {
            value = 0;
            return true;
        }

        if (long.TryParse(since, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0)
            return true;

        value = 0;
        error = InvalidCursor;
        return false;
    }

    public static long UnixSeconds(DateTime value) =>
        (long)(value.ToUniversalTime() - Epoch).TotalSeconds;

    private static CarrelFileEntry ToEntry(CarrelFileModel file) =>
        new(
            file.FileId.ToString("D"),
            file.Path,
            file.Hash,
            file.Rev,
            file.Deleted,
            UnixSeconds(file.ModifiedAtUtc));
}

/// <summary>A <c>since</c> value the protocol refuses.</summary>
public sealed class CarrelCursorException(string message) : Exception(message);
