using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Reading progress and life-cycle status (issue #1611): the high-frequency,
/// last-write-wins book writes. None of them is receipt-guarded, but they all run
/// through <see cref="LibraryMutationExecutor.RunInTransactionAsync"/>, so they
/// share the process-wide library gate, the single revision advance and the
/// change-feed tagging with every receipt-guarded command instead of hand-rolling
/// their own transaction and <see cref="LibraryRevision"/> call.
/// </summary>
public sealed class LibraryProgressService(LibraryMutationExecutor mutations)
{
    private DateTime Now => DateTime.UtcNow;

    /// <summary>
    /// High-frequency progress update: validates 0..100 and keeps FinishedAt
    /// aligned with the percentage. A <paramref name="playbackRate"/> is the
    /// audiobook's saved speed (clamped to 0.5..3); null leaves it as it is.
    /// </summary>
    public Task<LibraryCommandResultDto> UpdateProgressAsync(
        Guid bookId,
        string location,
        int percentage,
        double? playbackRate = null,
        CancellationToken ct = default) =>
        mutations.RunInTransactionAsync<LibraryCommandResultDto>(async (db, token) =>
        {
            var state = await LibraryStateGuard.EnsureAsync(db, token);

            // Only what the update decides on, not the row: a position save runs every
            // few seconds of reading and a book row can carry a large locations cache.
            var book = await db.Books.AsNoTracking()
                .Where(b => b.Id == bookId)
                .Select(b => new { b.Title, b.Progress.FinishedAt })
                .SingleOrDefaultAsync(token);
            if (book is null)
                return (false, LibraryCommandResults.Failure(
                    "book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

            var clamped = Math.Clamp(percentage, 0, 100);
            var lastLocation = string.IsNullOrWhiteSpace(location) ? null : location;
            var now = Now;
            double? rate = playbackRate is { } requested && double.IsFinite(requested)
                ? Math.Round(Math.Clamp(requested, 0.5, 3), 2)
                : null;

            // Keep FinishedAt aligned with the percentage (regression fix: a
            // finished book that is read again becomes unfinished).
            DateTime? finishedAt = clamped >= 100 ? book.FinishedAt ?? now : null;
            var finishedChanged = finishedAt != book.FinishedAt;

            // The bulk write bypasses the tracker, so what SaveChanges works out for
            // itself is said here: the revision goes first, and the change feed is told
            // whether this moved the reading position alone. Crossing into or out of
            // finished changes the book's status, so open screens need that at once.
            var version = await LibraryRevision.AdvanceAndGetAsync(db, token);
            db.NoteLibraryChangeKind(positionOnly: !finishedChanged);

            var updated = await db.Books
                .Where(b => b.Id == bookId)
                .ExecuteUpdateAsync(
                    setters =>
                    {
                        setters.SetProperty(b => b.Progress.LastLocation, lastLocation);
                        setters.SetProperty(b => b.Progress.ProgressPercent, clamped);
                        setters.SetProperty(b => b.Progress.LastReadAt, now);
                        if (rate is not null)
                            setters.SetProperty(b => b.Progress.PlaybackRate, rate);
                        if (finishedChanged)
                            setters.SetProperty(b => b.Progress.FinishedAt, finishedAt);
                    },
                    token);

            // Deleted since the read above: nothing was written, so the gate rolls
            // the revision back with it.
            if (updated == 0)
                return (false, LibraryCommandResults.Failure(
                    "book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

            return (true, LibraryCommandResults.Result(
                LibraryReplyFormatter.ProgressUpdated(book.Title), new { updated = true }, version));
        }, ct);

    /// <summary>
    /// Resets a book's reading progress to the canonical "not started" state.
    /// An already-reset book succeeds as a no-op with no state-version change.
    /// </summary>
    public Task<LibraryCommandResultDto> ResetProgressAsync(
        Guid bookId,
        bool allEditions = false,
        CancellationToken ct = default) =>
        mutations.RunInTransactionAsync<LibraryCommandResultDto>(async (db, token) =>
        {
            var state = await LibraryStateGuard.EnsureAsync(db, token);

            var book = await db.Books.SingleOrDefaultAsync(b => b.Id == bookId, token);
            if (book is null)
                return (false, LibraryCommandResults.Failure(
                    "book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

            // One edition, or every edition of the book (the work) so that "start over"
            // on a book read in several formats leaves nothing half-read behind.
            var targets = allEditions
                ? await db.Books.Where(b => b.WorkId == book.WorkId).ToListAsync(token)
                : [book];

            // Canonical reset state; an already-reset book is a successful no-op
            // (no state-version bump).
            static bool AlreadyReset(ReadingProgress progress) =>
                progress.LastLocation is null
                && progress.ProgressPercent == 0
                && progress.FinishedAt is null
                && progress.LastReadAt is null;

            var toReset = targets.Where(b => !AlreadyReset(b.Progress)).ToList();
            if (toReset.Count == 0)
                return (false, LibraryCommandResults.Result(
                    LibraryReplyFormatter.ProgressReset(book.Title), new { updated = false }, state.StateVersion));

            foreach (var target in toReset)
            {
                target.Progress.LastLocation = null;
                target.Progress.ProgressPercent = 0;
                target.Progress.FinishedAt = null;
                target.Progress.LastReadAt = null;
            }

            var version = await LibraryRevision.AdvanceAndGetAsync(db, token);
            await db.SaveChangesAsync(token);

            return (true, LibraryCommandResults.Result(
                LibraryReplyFormatter.ProgressReset(book.Title), new { updated = true }, version));
        }, ct);

    /// <summary>
    /// Updates the life-cycle status of an in-flight or completed book import.
    /// </summary>
    public Task<LibraryCommandResultDto> SetBookStatusAsync(
        Guid bookId,
        BookStatus status,
        string? statusMessage = null,
        CancellationToken ct = default) =>
        mutations.RunInTransactionAsync<LibraryCommandResultDto>(async (db, token) =>
        {
            var state = await LibraryStateGuard.EnsureAsync(db, token);

            var book = await db.Books.SingleOrDefaultAsync(b => b.Id == bookId, token);
            if (book is null)
                return (false, LibraryCommandResults.Failure(
                    "book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

            book.Status = status;
            book.StatusMessage = string.IsNullOrWhiteSpace(statusMessage) ? null : statusMessage;

            var version = await LibraryRevision.AdvanceAndGetAsync(db, token);
            await db.SaveChangesAsync(token);

            return (true, LibraryCommandResults.Result(
                "Book status updated.", new { updated = true }, version));
        }, ct);
}
