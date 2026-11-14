using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
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
/// <para><b>Files.</b> Each protocol file is one Studio document
/// (<see cref="CarrelDraftStore"/>). Its content is the document's Markdown,
/// byte for byte, so a <c>nostos:cite/</c> link a device writes stays exactly
/// as written. Models are not touched on save: before every request the
/// service reconciles the Studio tree against the mapping rows, so a draft
/// edited (or renamed, moved or deleted) in the Studio gets a new revision and
/// cursor without every writer having to know about the protocol.</para>
///
/// <para><b>Library.</b> Highlights, notes and passages are exported with
/// their book, topics and a ready-to-use citation by
/// <see cref="CarrelLibrarySnapshotBuilder"/>. The same reconciliation idea
/// gives deletions: a note that is gone (deleted alone, or with its book or
/// through an import's bulk delete) is reported as a tombstone instead of
/// silently disappearing from a device that cached it.</para>
///
/// <para><b>Cursors.</b> Each owner has one row with two counters
/// (<see cref="CarrelCursors"/>). Every change takes the row's write lock
/// first, then receives cursor values, then commits, so cursor order is commit
/// order: a device that stores a published cursor can never skip a change that
/// commits later. The cursor a request returns is never ahead of the changes
/// it actually returned.</para>
/// </summary>
public sealed class CarrelSyncService(ICarrelDeviceDirectory directory)
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
        await CarrelCursors.EnsureAsync(directory, ownerKey, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await CarrelCursors.LockAsync(db, ownerKey, ct);
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
        await CarrelCursors.EnsureAsync(directory, ownerKey, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await CarrelCursors.LockAsync(db, ownerKey, ct);
        await ReconcileFilesAsync(db, ownerKey, DateTime.UtcNow, ct);

        var file = await db.CarrelFiles.FirstOrDefaultAsync(
            f => f.FileId == fileId && f.OwnerKey == ownerKey, ct);
        if (file is null || file.Deleted)
        {
            await transaction.CommitAsync(ct);
            return null;
        }

        var content = (await CarrelDraftStore.ReadContentAsync(db, [file.DraftId], ct))
            .GetValueOrDefault(file.DraftId, string.Empty);
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
            file.Cursor = await CarrelCursors.AllocateAsync(
                db, ownerKey, 1, library: false, now, ct) + 1;
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
        await CarrelCursors.EnsureAsync(directory, ownerKey, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        await CarrelCursors.LockAsync(db, ownerKey, ct);
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

            var draft = await CarrelDraftStore.CreateAsync(db, request.Path, content, now, ct);
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
            file.Cursor = await CarrelCursors.AllocateAsync(
                db, ownerKey, 1, library: false, now, ct) + 1;
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

        var existing = await CarrelDraftStore.FindAsync(db, file.DraftId, ct);
        if (existing is null)
        {
            // The draft was deleted in the Studio and the device is restoring
            // the file it still has: materialise it again under the same id.
            existing = await CarrelDraftStore.CreateAsync(db, request.Path, content, now, ct);
            file.DraftId = existing.Id;
        }
        else
        {
            await CarrelDraftStore.MoveToPathAsync(db, existing, request.Path, now, ct);
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
        file.Cursor = await CarrelCursors.AllocateAsync(db, ownerKey, 1, library: false, now, ct) + 1;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CarrelPutOutcome.Success(file.Rev, file.Hash);
    }

    public async Task<CarrelDeleteOutcome> DeleteFileAsync(
        string ownerKey, Guid fileId, long? baseRev, CancellationToken ct = default)
    {
        await using var db = await directory.OpenOwnerAsync(ownerKey, ct);
        await CarrelCursors.EnsureAsync(directory, ownerKey, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        await CarrelCursors.LockAsync(db, ownerKey, ct);
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
        await CarrelDraftStore.DeleteAsync(db, file.DraftId, ct);

        file.Rev++;
        file.Deleted = true;
        file.ModifiedAtUtc = now;
        file.Cursor = await CarrelCursors.AllocateAsync(db, ownerKey, 1, library: false, now, ct) + 1;
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
        await CarrelCursors.EnsureAsync(directory, ownerKey, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        await CarrelCursors.LockAsync(db, ownerKey, ct);
        await CarrelLibrarySnapshotBuilder.ReconcileAsync(db, ownerKey, now, ct);
        await db.SaveChangesAsync(ct);

        var page = await CarrelLibrarySnapshotBuilder.PageAsync(
            db, ownerKey, sinceValue, baseUrl, ct);
        await transaction.CommitAsync(ct);
        return page;
    }

    // ------------------------------------------------------------------
    // Reconciliation (files)
    // ------------------------------------------------------------------

    /// <summary>
    /// Brings the file rows in line with the Studio tree: a new document becomes
    /// a file, a renamed, moved or edited one gets the next revision, and a
    /// document that is gone leaves a tombstone.
    /// </summary>
    private static async Task ReconcileFilesAsync(
        NostosDbContext db, string ownerKey, DateTime now, CancellationToken ct)
    {
        var drafts = await CarrelDraftStore.ListAsync(db, ct);
        var files = await db.CarrelFiles
            .Where(f => f.OwnerKey == ownerKey)
            .ToListAsync(ct);
        var byDraft = files.ToDictionary(f => f.DraftId);

        // Only drafts that are new or edited since the last pass need their
        // text read; a path-only change keeps the hash it already had.
        var needContent = new HashSet<Guid>();
        foreach (var draft in drafts)
        {
            if (!byDraft.TryGetValue(draft.Id, out var file)
                || file.Deleted
                || file.DraftUpdatedAtUtc != draft.UpdatedAt)
            {
                needContent.Add(draft.Id);
            }
        }

        var contents = await CarrelDraftStore.ReadContentAsync(db, needContent, ct);
        var touched = new List<CarrelFileModel>();

        foreach (var draft in drafts)
        {
            if (!byDraft.TryGetValue(draft.Id, out var file))
            {
                file = new CarrelFileModel
                {
                    FileId = Guid.CreateVersion7(),
                    OwnerKey = ownerKey,
                    DraftId = draft.Id,
                    Path = draft.Path,
                    Hash = CarrelTokens.ContentHash(contents.GetValueOrDefault(draft.Id, string.Empty)),
                    Rev = 1,
                    Deleted = false,
                    ModifiedAtUtc = now,
                    DraftUpdatedAtUtc = draft.UpdatedAt,
                };
                db.CarrelFiles.Add(file);
                touched.Add(file);
                continue;
            }

            var pathChanged = file.Path != draft.Path;
            var draftChanged = file.DraftUpdatedAtUtc != draft.UpdatedAt;
            if (!file.Deleted && !pathChanged && !draftChanged)
                continue;

            if (draftChanged || file.Deleted)
            {
                file.Hash = CarrelTokens.ContentHash(contents.GetValueOrDefault(draft.Id, string.Empty));
            }

            file.Rev++;
            file.Path = draft.Path;
            file.Deleted = false;
            file.ModifiedAtUtc = now;
            file.DraftUpdatedAtUtc = draft.UpdatedAt;
            touched.Add(file);
        }

        var live = drafts.Select(d => d.Id).ToHashSet();
        foreach (var file in files.Where(f => !f.Deleted && !live.Contains(f.DraftId)))
        {
            file.Rev++;
            file.Deleted = true;
            file.ModifiedAtUtc = now;
            touched.Add(file);
        }

        await CarrelCursors.StampFilesAsync(db, ownerKey, touched, now, ct);
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
