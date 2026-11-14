using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Carrel;

/// <summary>
/// The Writing Studio side of the Carrel file feed: which documents exist, the
/// protocol path each one is served at, and the writes a device's <c>PUT</c> or
/// <c>DELETE</c> makes to the Studio tree.
///
/// <para>A protocol file is one Studio document. Its path is the document's
/// folder chain plus its name and <c>.md</c>, made unique across the tree so
/// two documents with one name never share a file. Its content is the
/// document's Markdown byte for byte, so a <c>nostos:cite/</c> link a device
/// writes stays exactly as written.</para>
/// </summary>
public static class CarrelDraftStore
{
    /// <summary>One Studio document and the protocol path it is served at.</summary>
    public sealed record Draft(Guid Id, string Path, DateTime UpdatedAt);

    private sealed record Row(
        Guid Id, string Name, WritingType Type, Guid? ParentId, DateTime UpdatedAt);

    /// <summary>Every Studio document, in id order, each with its unique path.</summary>
    public static async Task<IReadOnlyList<Draft>> ListAsync(
        NostosDbContext db, CancellationToken ct)
    {
        var writings = await db.Writings
            .AsNoTracking()
            .Select(w => new Row(w.Id, w.Name, w.Type, w.ParentId, w.UpdatedAt))
            .ToListAsync(ct);
        var byId = writings.ToDictionary(w => w.Id);

        var drafts = new List<Draft>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in writings
                     .Where(w => w.Type == WritingType.Document)
                     .OrderBy(w => w.Id))
        {
            drafts.Add(new Draft(
                document.Id,
                CarrelPaths.Unique(
                    CarrelPaths.ForDocument(FolderChain(byId, document), document.Name), taken),
                document.UpdatedAt));
        }

        return drafts;
    }

    /// <summary>The Markdown of the named documents, read in chunks; a missing one is absent.</summary>
    public static async Task<Dictionary<Guid, string>> ReadContentAsync(
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

    /// <summary>The document a file maps to, tracked, or null when the Studio no longer has it.</summary>
    public static Task<WritingModel?> FindAsync(
        NostosDbContext db, Guid draftId, CancellationToken ct) =>
        db.Writings.FirstOrDefaultAsync(
            w => w.Id == draftId && w.Type == WritingType.Document, ct);

    /// <summary>Make a document at <paramref name="path"/> with <paramref name="content"/>.</summary>
    public static async Task<WritingModel> CreateAsync(
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
    public static async Task MoveToPathAsync(
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
    /// Deletes the Studio draft a file maps to, if it is still there. Leaving it
    /// behind would make the next reconciliation resurrect the deleted file.
    /// </summary>
    public static async Task DeleteAsync(NostosDbContext db, Guid draftId, CancellationToken ct)
    {
        var draft = await db.Writings.FirstOrDefaultAsync(w => w.Id == draftId, ct);
        if (draft is not null)
            db.Writings.Remove(draft);
    }

    /// <summary>The folder names above a document, outermost first.</summary>
    private static List<string> FolderChain(IReadOnlyDictionary<Guid, Row> byId, Row document)
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
}
