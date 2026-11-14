using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// The manual multi-edition override (issue #1611): merging two works and
/// splitting one book out of its work. Editions are never rewritten except for
/// <c>WorkId</c> (and an edition rating/review exception); what the book itself
/// says about a book is combined by <see cref="BookValueRollUp"/>.
/// </summary>
public sealed class LibraryWorkService(LibraryMutationExecutor mutations)
{
    private DateTime Now => DateTime.UtcNow;

    public Task<LibraryCommandResultDto> LinkAsync(
        LibraryLinkWorkRequest request,
        CancellationToken ct = default) =>
        mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "LinkWork",
            (db, token) => LinkWorkCoreAsync(db, request, token), ct);

    public Task<LibraryCommandResultDto> UnlinkAsync(
        LibraryUnlinkWorkRequest request,
        CancellationToken ct = default) =>
        mutations.ExecuteAsync(request.ClientId, request.IdempotencyKey, "UnlinkWork",
            (db, token) => UnlinkWorkCoreAsync(db, request, token), ct);

    /// <summary>
    /// Merge <c>BookId</c>'s work into <c>TargetBookId</c>'s work.
    ///
    /// Group semantics, deliberately: the target's work survives as the merged
    /// group's identity and EVERY book from the source group is moved onto it,
    /// rather than only the two named books. Moving just the named book would
    /// silently strip one edition out of an already-valid group — a side effect
    /// the user did not ask for.
    ///
    /// Two edition rows are never touched except for <c>WorkId</c> (and an edition
    /// rating / review exception, when the two works disagree about the book):
    /// files, progress, notes, metadata and collection membership all belong to
    /// the edition and stay exactly as they were. The book's rating, favourite
    /// and review are combined by <see cref="BookValueRollUp"/>.
    /// </summary>
    internal async Task<(bool DidChange, LibraryCommandResultDto Result)> LinkWorkCoreAsync(
        NostosDbContext db,
        LibraryLinkWorkRequest request,
        CancellationToken ct)
    {
        var state = await LibraryStateGuard.EnsureAsync(db, ct);

        if (request.BookId == request.TargetBookId)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("invalid_work_link",
                "A book cannot be linked to itself.", state.StateVersion));

        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == request.BookId, ct);
        if (book is null)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure(
                "book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

        var target = await db.Books.SingleOrDefaultAsync(b => b.Id == request.TargetBookId, ct);
        if (target is null)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure("target_book_not_found",
                LibraryReplyFormatter.BookNotFound, state.StateVersion));

        // Already grouped (in either direction, and including a replayed
        // request with the arguments swapped): a successful no-op, so the
        // operation is idempotent regardless of how the client orders the pair.
        if (book.WorkId == target.WorkId)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Result(
                LibraryReplyFormatter.WorkAlreadyLinked(book.Title, target.Title),
                await BuildWorkMembershipResultAsync(db, book, removedWorkId: null, ct),
                state.StateVersion));

        var sourceWorkId = book.WorkId;
        var survivorWorkId = target.WorkId;

        // Fix up every member of the source group, not just the named book.
        var sourceMembers = await db.Books
            .Where(b => b.WorkId == sourceWorkId)
            .ToListAsync(ct);
        var survivorMembers = await db.Books
            .Where(b => b.WorkId == survivorWorkId)
            .ToListAsync(ct);

        // What each side says about the book is combined by the shared rule, so
        // linking never drops a rating, favourite or review: agreeing or one-sided
        // values become the book's, and where they differ the side read most
        // recently wins and the other keeps its value on its own editions.
        var sourceWorkRow = book.Work!;
        var survivorWorkRow = target.Work!;
        BookValueRollUp.Merge(
            survivorWorkRow,
            [
                new BookValueParty(BookValueRollUp.WorkSide(survivorWorkRow, survivorMembers), survivorMembers),
                new BookValueParty(BookValueRollUp.WorkSide(sourceWorkRow, sourceMembers), sourceMembers),
            ],
            [.. survivorMembers, .. sourceMembers]);

        foreach (var member in sourceMembers)
        {
            member.WorkId = survivorWorkId;
            member.Work = survivorWorkRow;
        }

        // Remove the source work once nothing references it. Its rating, favourite
        // and review were rolled into the survivor above, so nothing is lost; the
        // group's identity is the survivor's own title/author. The books are updated first so the
        // FK is already re-pointed when the delete runs.
        var sourceWork = await db.Works.SingleOrDefaultAsync(w => w.Id == sourceWorkId, ct);
        Guid? removedWorkId = null;
        if (sourceWork is not null)
        {
            await db.SaveChangesAsync(ct);
            var stillReferenced = await db.Books.AnyAsync(b => b.WorkId == sourceWorkId, ct);
            if (!stillReferenced)
            {
                db.Works.Remove(sourceWork);
                removedWorkId = sourceWorkId;
            }
        }

        await db.SaveChangesAsync(ct);

        return LibraryCommandResults.Change(LibraryCommandResults.Result(
            LibraryReplyFormatter.WorkLinked(book.Title, target.Title),
            await BuildWorkMembershipResultAsync(db, book, removedWorkId, ct),
            state.StateVersion));
    }

    /// <summary>
    /// Split <c>BookId</c> out of its work into a fresh single-book work built
    /// from the book's OWN current title/author identity. The book therefore
    /// always ends up with a valid work id; an unlinked-to-nothing state is not
    /// representable.
    /// </summary>
    internal async Task<(bool DidChange, LibraryCommandResultDto Result)> UnlinkWorkCoreAsync(
        NostosDbContext db,
        LibraryUnlinkWorkRequest request,
        CancellationToken ct)
    {
        var state = await LibraryStateGuard.EnsureAsync(db, ct);

        var book = await db.Books.SingleOrDefaultAsync(b => b.Id == request.BookId, ct);
        if (book is null)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Failure(
                "book_not_found", LibraryReplyFormatter.BookNotFound, state.StateVersion));

        var previousWorkId = book.WorkId;
        var remaining = await db.Books.CountAsync(b => b.WorkId == previousWorkId && b.Id != book.Id, ct);

        // Nothing to split: the book is already the only member of its work.
        if (remaining == 0)
            return LibraryCommandResults.NoChange(LibraryCommandResults.Result(
                LibraryReplyFormatter.WorkAlreadyStandalone(book.Title),
                await BuildWorkMembershipResultAsync(db, book, removedWorkId: null, ct),
                state.StateVersion));

        // The book's own identity, NOT the old work's: that work exists to
        // describe the group, and copying its title onto a single book would
        // label the split-off edition with whichever sibling happened to be the
        // group's representative.
        var newWork = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = book.Title,
            Author = book.Author,
            NormalizedTitle = BookIdentityNormalizer.NormalizeTitle(book.Title),
            NormalizedAuthor = BookIdentityNormalizer.NormalizeAuthor(book.Author),
            CreatedAt = Now,
            // The split-off edition takes its own rating / review when it has one,
            // else the book's it was part of; the favourite is copied. Its own
            // values then stop being exceptions. The old work is left as it was.
            Rating = book.Progress.Rating > 0 ? book.Progress.Rating : book.Work!.Rating,
            PersonalReview = string.IsNullOrEmpty(book.Progress.PersonalReview)
                ? book.Work!.PersonalReview
                : book.Progress.PersonalReview,
            IsFavorite = book.Work!.IsFavorite,
        };
        db.Works.Add(newWork);
        book.Progress.Rating = 0;
        book.Progress.PersonalReview = null;

        book.WorkId = newWork.Id;
        book.Work = newWork;
        await db.SaveChangesAsync(ct);

        return LibraryCommandResults.Change(LibraryCommandResults.Result(
            LibraryReplyFormatter.WorkUnlinked(book.Title),
            await BuildWorkMembershipResultAsync(db, book, removedWorkId: null, ct),
            state.StateVersion));
    }

    /// <summary>
    /// Post-mutation membership snapshot for the command payload. Read after the
    /// write so the client can render the new group without a follow-up read,
    /// and so the count is the group's real size rather than a prediction.
    /// </summary>
    private static async Task<LibraryWorkMembershipResultDto> BuildWorkMembershipResultAsync(
        NostosDbContext db,
        BookModel book,
        Guid? removedWorkId,
        CancellationToken ct)
    {
        var workId = book.WorkId;
        var count = await db.Books.CountAsync(b => b.WorkId == workId, ct);
        return new LibraryWorkMembershipResultDto(book.Id, workId, count, removedWorkId);
    }
}
