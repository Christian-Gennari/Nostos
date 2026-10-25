using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.BookUpload;

/// <summary>
/// Resumable book-file uploads (#1266 Phase A). The session table is the only
/// record that an upload is in progress: the book row is neither hidden nor
/// given a new status. Accepted parts are receipted only after their bytes are
/// durably placed, and publication verifies the whole-file SHA-256 and adopts
/// the sealed file through the host's asset storage.
/// </summary>
public sealed class BookUploadService(
    NostosDbContext db,
    IBookFileUploadStore store,
    IBookRepository repo,
    IBookAssetStorage storage,
    IBookUploadOwnerProvider owner,
    BookUploadWriteGate writeGate,
    BookFileCommit commit,
    BookFileMutationGate fileMutationGate,
    ITransferVolume volume,
    ILogger<BookUploadService> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private string OwnerKey => owner.GetOwnerKey();

    public async Task<(BookUploadStatus Status, bool Created)> CreateOrResumeAsync(
        Guid bookId,
        BookUploadCreateRequest request,
        CancellationToken ct)
    {
        ValidateRequest(request);

        var book = await repo.GetByIdAsync(bookId)
            ?? throw BookUploadException.Error(BookUploadException.BookNotFound);
        EnsureBookAcceptsUpload(book);

        var sha256 = request.Sha256.Trim().ToLowerInvariant();
        var existing = await ActiveSessionAsync(bookId, ct, includeCompleting: true);
        if (existing is not null)
        {
            if (Matches(existing, request, sha256))
            {
                await EnsurePreparedAsync(existing, ct);
                return (await ToStatusAsync(existing, ct), false);
            }

            // A different file replaces an interrupted attempt, but never a
            // session whose verified bytes are already being published.
            if (existing.StateValue == BookUploadState.Completing)
                throw BookUploadException.Error(BookUploadException.InProgress);

            await DiscardSessionAsync(existing, BookUploadState.Discarded, ct);
        }

        if (volume.AvailableFreeSpaceBytes < request.TotalBytes)
            throw BookUploadException.Error(BookUploadException.StorageExhausted);

        var session = new BookUploadSessionRecord
        {
            BookId = bookId,
            OwnerKey = OwnerKey,
            StateValue = BookUploadState.Created,
            TotalBytes = request.TotalBytes,
            ChunkSize = BookUploadLimits.PartBytes,
            TotalChunks = BookUploadLimits.PartCountFor(request.TotalBytes),
            FileName = request.FileName,
            ClientContentType = request.ContentType,
            FileSha256 = sha256,
            ClientFingerprint = request.ClientFingerprint,
            ReceivedBytes = 0,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            ExpiresAtUtc = Now.Add(BookUploadLimits.SessionLifetime),
            ActiveSessionKey = ActiveKey(bookId),
        };

        db.BookUploadSessionRecords.Add(session);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two creators raced for the same (owner, book). The unique active
            // key made exactly one win; report the winner as a replay.
            db.Entry(session).State = EntityState.Detached;
            var winner = await ActiveSessionAsync(bookId, ct, includeCompleting: true)
                ?? throw BookUploadException.Error(BookUploadException.InProgress);
            if (!Matches(winner, request, sha256))
                throw BookUploadException.Error(BookUploadException.InProgress);
            await EnsurePreparedAsync(winner, ct);
            return (await ToStatusAsync(winner, ct), false);
        }

        await store.PrepareAsync(session.Id, session.TotalBytes, ct);
        return (await ToStatusAsync(session, ct), true);
    }

    public async Task<BookUploadStatus?> GetCurrentAsync(Guid bookId, CancellationToken ct)
    {
        var session = await ActiveSessionAsync(bookId, ct, includeCompleting: true);
        return session is null ? null : await ToStatusAsync(session, ct);
    }

    public async Task<BookUploadStatus> GetAsync(
        Guid bookId,
        Guid sessionId,
        CancellationToken ct)
    {
        var session = await FindAsync(bookId, sessionId, ct);
        return await ToStatusAsync(session, ct);
    }

    public async Task<BookUploadPartResult> UploadPartAsync(
        Guid bookId,
        Guid sessionId,
        int partIndex,
        long start,
        long end,
        long total,
        string sha256,
        Stream body,
        CancellationToken ct)
    {
        var session = await FindAsync(bookId, sessionId, ct);
        EnsureNotExpired(session);
        if (session.StateValue is not (BookUploadState.Created
            or BookUploadState.Receiving
            or BookUploadState.Paused))
        {
            throw BookUploadException.Error(BookUploadException.InvalidState);
        }

        if (partIndex < 0 || partIndex >= session.TotalChunks)
            throw BookUploadException.Error(BookUploadException.PartRangeInvalid);

        var offset = (long)partIndex * session.ChunkSize;
        var length = BookUploadLimits.PartLength(session.TotalBytes, partIndex);
        if (total != session.TotalBytes
            || start != offset
            || end != offset + length - 1)
        {
            throw BookUploadException.Error(BookUploadException.PartRangeInvalid);
        }

        var expected = sha256.Trim().ToLowerInvariant();
        var receipt = await db.BookUploadPartReceiptRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.SessionId == sessionId && r.PartIndex == partIndex, ct);
        if (receipt is not null)
        {
            if (receipt.LengthBytes == length && receipt.Sha256 == expected)
                return new BookUploadPartResult(sessionId, partIndex, true);
            throw BookUploadException.Error(BookUploadException.PartConflict);
        }

        var pending = await store.ReceivePartAsync(sessionId, partIndex, length, expected, body, ct);
        var placed = false;
        try
        {
            using var lease = await writeGate.EnterAsync(sessionId, ct);
            session = await FindAsync(bookId, sessionId, ct);
            EnsureNotExpired(session);
            if (session.StateValue is not (BookUploadState.Created
                or BookUploadState.Receiving
                or BookUploadState.Paused))
            {
                throw BookUploadException.Error(BookUploadException.InvalidState);
            }

            receipt = await db.BookUploadPartReceiptRecords.AsNoTracking()
                .SingleOrDefaultAsync(r => r.SessionId == sessionId && r.PartIndex == partIndex, ct);
            if (receipt is not null)
            {
                if (receipt.LengthBytes == length && receipt.Sha256 == expected)
                    return new BookUploadPartResult(sessionId, partIndex, true);
                throw BookUploadException.Error(BookUploadException.PartConflict);
            }

            await store.PlacePartAsync(sessionId, pending, offset, ct);
            placed = true;

            db.BookUploadPartReceiptRecords.Add(new BookUploadPartReceiptRecord
            {
                SessionId = sessionId,
                PartIndex = partIndex,
                OffsetBytes = offset,
                LengthBytes = length,
                Sha256 = expected,
                ReceivedAtUtc = Now,
            });
            session.ReceivedBytes += length;
            session.StateValue = BookUploadState.Receiving;
            session.ExpiresAtUtc = Now.Add(BookUploadLimits.SessionLifetime);
            session.UpdatedAtUtc = Now;
            session.Version += 1;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A duplicate receipt or a concurrent session update means
                // another request already recorded this part. Whatever won,
                // our placed bytes are identical (same offset and hash).
                db.ChangeTracker.Clear();
                var current = await db.BookUploadPartReceiptRecords.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.SessionId == sessionId && r.PartIndex == partIndex, ct);
                if (current is not null
                    && current.LengthBytes == length
                    && current.Sha256 == expected)
                {
                    return new BookUploadPartResult(sessionId, partIndex, true);
                }
                throw BookUploadException.Error(BookUploadException.PartConflict);
            }

            return new BookUploadPartResult(sessionId, partIndex, false);
        }
        finally
        {
            if (!placed) store.DiscardPending(pending);
        }
    }

    public async Task<BookUploadStatus> PauseAsync(
        Guid bookId,
        Guid sessionId,
        CancellationToken ct)
    {
        var session = await FindAsync(bookId, sessionId, ct);
        EnsureNotExpired(session);
        if (session.StateValue == BookUploadState.Paused)
            return await ToStatusAsync(session, ct);
        if (session.StateValue == BookUploadState.Complete)
            return await ToStatusAsync(session, ct);
        if (session.StateValue is not (BookUploadState.Created or BookUploadState.Receiving))
            throw BookUploadException.Error(BookUploadException.InvalidState);

        session.StateValue = BookUploadState.Paused;
        session.UpdatedAtUtc = Now;
        session.Version += 1;
        await db.SaveChangesAsync(ct);
        return await ToStatusAsync(session, ct);
    }

    public async Task DiscardAsync(Guid bookId, Guid sessionId, CancellationToken ct)
    {
        var session = await FindAsync(bookId, sessionId, ct);
        if (session.StateValue == BookUploadState.Discarded) return;
        if (session.StateValue == BookUploadState.Complete)
            throw BookUploadException.Error(BookUploadException.InvalidState);

        await DiscardSessionAsync(session, BookUploadState.Discarded, ct);
    }

    /// <summary>Drops every unfinished session for a book that is being deleted.</summary>
    public async Task DiscardForBookAsync(Guid bookId, CancellationToken ct)
    {
        var sessions = await db.BookUploadSessionRecords.AsNoTracking()
            .Where(s => s.BookId == bookId && s.OwnerKey == OwnerKey
                && s.State != (int)BookUploadState.Discarded
                && s.State != (int)BookUploadState.Complete)
            .ToListAsync(ct);

        foreach (var session in sessions)
            await DiscardSessionAsync(session, BookUploadState.Discarded, ct);
    }

    public async Task<BookUploadStatus> CompleteAsync(
        Guid bookId,
        Guid sessionId,
        CancellationToken ct)
    {
        var session = await FindAsync(bookId, sessionId, ct);
        if (session.StateValue == BookUploadState.Complete)
            return await ToStatusAsync(session, ct);
        EnsureNotExpired(session);
        if (session.StateValue is not (BookUploadState.Created
            or BookUploadState.Receiving
            or BookUploadState.Paused
            or BookUploadState.Completing))
        {
            throw BookUploadException.Error(BookUploadException.InvalidState);
        }

        var receipts = await db.BookUploadPartReceiptRecords.AsNoTracking()
            .Where(r => r.SessionId == sessionId)
            .OrderBy(r => r.PartIndex)
            .ToListAsync(ct);
        ValidateReceipts(session, receipts);

        // Publication resumes from the sealed file after a crash. When neither
        // staging name exists, the bytes may already have been adopted into the
        // library before the row commit was recorded.
        var hasStaging = await store.HasStagingAsync(sessionId, ct);
        if (!hasStaging)
        {
            if (await AdoptedFileMatchesAsync(bookId, session, ct))
                return await MarkCompleteAsync(session, ct);
            throw BookUploadException.Error(BookUploadException.InvalidState);
        }

        if (session.StateValue != BookUploadState.Completing)
        {
            if (!await store.VerifyAsync(sessionId, session.TotalBytes, session.FileSha256, ct))
            {
                logger.LogWarning(
                    "Book upload {SessionId} for book {BookId} failed whole-file verification.",
                    sessionId,
                    bookId);
                await DiscardSessionAsync(session, BookUploadState.Failed, ct,
                    BookUploadException.FileHashMismatch);
                throw BookUploadException.Error(BookUploadException.FileHashMismatch);
            }

            await store.SealAsync(sessionId, ct);
            session.StateValue = BookUploadState.Completing;
            session.UpdatedAtUtc = Now;
            session.Version += 1;
            await db.SaveChangesAsync(ct);
        }

        // Probe, never wait: a stale "complete" must not queue behind a
        // multi-hour acquisition and then overwrite its result.
        using var lease = fileMutationGate.TryEnter(bookId)
            ?? throw BookUploadException.Error(BookUploadException.InProgress);

        var book = await repo.GetByIdAsync(bookId)
            ?? throw BookUploadException.Error(BookUploadException.BookNotFound);
        EnsureBookAcceptsUpload(book);

        await using (var staged = await store.OpenSealedAsync(sessionId, ct))
        {
            commit.Enrich(book, staged);
        }

        try
        {
            await store.AdoptAsync(bookId, sessionId, session.FileName, storage, ct);
            await commit.FinishAsync(book, session.FileName, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The crash window this state exists for: adoption may have made
            // the bytes the book's file before the row update was recorded.
            if (await AdoptedFileMatchesAsync(bookId, session, ct))
                return await MarkCompleteAsync(session, ct);

            // No staged bytes and no adopted file means this attempt cannot be
            // resumed; surface a terminal failure instead of a stuck session.
            if (!await store.HasStagingAsync(sessionId, ct))
            {
                await DiscardSessionAsync(
                    session,
                    BookUploadState.Failed,
                    ct,
                    "book_upload_publish_failed");
            }

            throw;
        }

        await MarkCompleteAsync(session, ct);
        writeGate.Forget(sessionId);
        return await ToStatusAsync(session, ct);
    }

    /// <summary>Marks expired sessions and removes their staging bytes.</summary>
    public async Task<int> ExpireStaleAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        var stale = await db.BookUploadSessionRecords
            .Where(s => s.ActiveSessionKey != null
                && s.ExpiresAtUtc <= cutoffUtc
                && s.State != (int)BookUploadState.Complete
                && s.State != (int)BookUploadState.Failed
                && s.State != (int)BookUploadState.Discarded
                && s.State != (int)BookUploadState.Expired)
            .ToListAsync(ct);

        foreach (var session in stale)
            await DiscardSessionAsync(session, BookUploadState.Expired, ct);
        return stale.Count;
    }

    /// <summary>
    /// Removes finished session rows (and any leftover staging) after they have
    /// been idle for <paramref name="cutoffUtc"/>. Complete rows are kept for a
    /// while so a client that lost the completion response can still read it.
    /// </summary>
    public async Task<int> PurgeTerminalAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        var sessions = await db.BookUploadSessionRecords.AsNoTracking()
            .Where(s => s.ActiveSessionKey == null && s.UpdatedAtUtc <= cutoffUtc)
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (sessions.Count == 0) return 0;

        foreach (var sessionId in sessions)
        {
            await store.DiscardAsync(sessionId, ct);
            writeGate.Forget(sessionId);
        }

        return await db.BookUploadSessionRecords
            .Where(s => sessions.Contains(s.Id))
            .ExecuteDeleteAsync(ct);
    }

    private async Task<BookUploadSessionRecord> FindAsync(
        Guid bookId,
        Guid sessionId,
        CancellationToken ct)
    {
        var session = await db.BookUploadSessionRecords
            .SingleOrDefaultAsync(
                s => s.Id == sessionId && s.BookId == bookId && s.OwnerKey == OwnerKey,
                ct);
        return session ?? throw BookUploadException.Error(BookUploadException.NotFound);
    }

    private Task<BookUploadSessionRecord?> ActiveSessionAsync(
        Guid bookId,
        CancellationToken ct,
        bool includeCompleting) =>
        db.BookUploadSessionRecords
            .Where(s => s.BookId == bookId
                && s.OwnerKey == OwnerKey
                && s.ActiveSessionKey != null
                && (includeCompleting || s.State != (int)BookUploadState.Completing))
            .OrderByDescending(s => s.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);

    private async Task EnsurePreparedAsync(
        BookUploadSessionRecord session,
        CancellationToken ct)
    {
        if (await store.HasStagingAsync(session.Id, ct)) return;

        // The database survived but the staging volume did not (or an earlier
        // prepare failed). Receipts without bytes are meaningless: start the
        // file over rather than publishing a hole.
        if (session.ReceivedBytes > 0 || session.StateValue != BookUploadState.Created)
        {
            await db.BookUploadPartReceiptRecords
                .Where(r => r.SessionId == session.Id)
                .ExecuteDeleteAsync(ct);
            session.ReceivedBytes = 0;
            session.StateValue = BookUploadState.Created;
            session.UpdatedAtUtc = Now;
            session.Version += 1;
            await db.SaveChangesAsync(ct);
        }

        await store.PrepareAsync(session.Id, session.TotalBytes, ct);
    }

    private async Task DiscardSessionAsync(
        BookUploadSessionRecord session,
        BookUploadState terminalState,
        CancellationToken ct,
        string? failureCode = null)
    {
        await db.BookUploadPartReceiptRecords
            .Where(r => r.SessionId == session.Id)
            .ExecuteDeleteAsync(ct);

        session.StateValue = terminalState;
        session.FailureCode = failureCode;
        session.ActiveSessionKey = null;
        session.ReceivedBytes = 0;
        session.UpdatedAtUtc = Now;
        session.Version += 1;
        await db.SaveChangesAsync(ct);
        await store.DiscardAsync(session.Id, ct);
        writeGate.Forget(session.Id);
    }

    private async Task<BookUploadStatus> MarkCompleteAsync(
        BookUploadSessionRecord session,
        CancellationToken ct)
    {
        if (session.StateValue != BookUploadState.Complete)
        {
            session.StateValue = BookUploadState.Complete;
            session.ActiveSessionKey = null;
            session.CompletedAtUtc = Now;
            session.UpdatedAtUtc = Now;
            session.Version += 1;
            await db.SaveChangesAsync(ct);
        }
        return await ToStatusAsync(session, ct);
    }

    private async Task<bool> AdoptedFileMatchesAsync(
        Guid bookId,
        BookUploadSessionRecord session,
        CancellationToken ct)
    {
        var info = await storage.GetBookFileInfoAsync(bookId, ct);
        if (info is null || info.Length != session.TotalBytes) return false;
        var book = await repo.GetByIdAsync(bookId);
        return book is not null
            && book.FileDetails.HasFile
            && string.Equals(
                book.FileDetails.FileName,
                $"book{Path.GetExtension(session.FileName)}",
                StringComparison.OrdinalIgnoreCase);
    }

    private void ValidateReceipts(
        BookUploadSessionRecord session,
        IReadOnlyList<BookUploadPartReceiptRecord> receipts)
    {
        if (receipts.Count != session.TotalChunks)
            throw BookUploadException.Error(BookUploadException.InvalidState);

        long expectedOffset = 0;
        for (var index = 0; index < receipts.Count; index += 1)
        {
            var receipt = receipts[index];
            var expectedLength = BookUploadLimits.PartLength(session.TotalBytes, index);
            if (receipt.PartIndex != index
                || receipt.OffsetBytes != expectedOffset
                || receipt.LengthBytes != expectedLength)
            {
                throw BookUploadException.Error(BookUploadException.InvalidState);
            }
            expectedOffset += expectedLength;
        }

        if (expectedOffset != session.TotalBytes)
            throw BookUploadException.Error(BookUploadException.InvalidState);
    }

    private async Task<BookUploadStatus> ToStatusAsync(
        BookUploadSessionRecord session,
        CancellationToken ct)
    {
        var received = await db.BookUploadPartReceiptRecords.AsNoTracking()
            .Where(r => r.SessionId == session.Id)
            .OrderBy(r => r.PartIndex)
            .Select(r => r.PartIndex)
            .ToListAsync(ct);

        return new BookUploadStatus(
            session.Id,
            session.BookId,
            session.StateValue,
            session.TotalBytes,
            session.ChunkSize,
            session.TotalChunks,
            session.FileName,
            session.FileSha256,
            received,
            session.ReceivedBytes,
            AsUtc(session.CreatedAtUtc),
            AsUtc(session.ExpiresAtUtc),
            session.CompletedAtUtc is { } completed ? AsUtc(completed) : null,
            session.FailureCode);
    }

    private void EnsureNotExpired(BookUploadSessionRecord session)
    {
        if (session.StateValue is BookUploadState.Expired)
            throw BookUploadException.Error(BookUploadException.Expired);
        if (session.ActiveSessionKey is not null && session.ExpiresAtUtc <= Now)
            throw BookUploadException.Error(BookUploadException.Expired);
    }

    private static void EnsureBookAcceptsUpload(BookModel book)
    {
        if (book is PhysicalBookModel)
            throw BookUploadException.Error(BookUploadException.InvalidRequest);
        if (book.Status is BookStatus.Downloading or BookStatus.Transcoding)
            throw BookUploadException.Error(BookUploadException.InProgress);
    }

    private static void ValidateRequest(BookUploadCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TotalBytes <= 0 || request.TotalBytes > BookUploadLimits.MaxBookBytes)
            throw BookUploadException.Error(BookUploadException.InvalidRequest);
        if (string.IsNullOrWhiteSpace(request.FileName)
            || request.FileName.Length > 255
            || !BookAssetFormats.IsAllowedUpload(
                request.ContentType ?? "application/octet-stream",
                request.FileName))
        {
            throw BookUploadException.Error(BookUploadException.UnsupportedType);
        }
        if (!IsSha256(request.Sha256))
            throw BookUploadException.Error(BookUploadException.InvalidRequest);
        if (request.ClientFingerprint is { Length: > 256 })
            throw BookUploadException.Error(BookUploadException.InvalidRequest);
    }

    private static bool Matches(
        BookUploadSessionRecord session,
        BookUploadCreateRequest request,
        string sha256) =>
        session.TotalBytes == request.TotalBytes
        && session.FileSha256 == sha256
        && string.Equals(session.FileName, request.FileName, StringComparison.Ordinal);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private string ActiveKey(Guid bookId) => $"{OwnerKey}:{bookId:N}";

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
