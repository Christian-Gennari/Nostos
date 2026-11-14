using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// The identity a create-or-match request is resolved by, normalized once with
/// <see cref="BookIdentityNormalizer"/> so every check reads the same values.
/// </summary>
internal sealed record LibraryBookIdentity(
    string NormalizedTitle,
    string NormalizedAuthor,
    string? NormalizedIsbn,
    string? NormalizedAsin,
    string Type)
{
    /// <summary>False when the request cannot name a book at all.</summary>
    public static bool TryCreate(
        LibraryCreateBookRequest request,
        out LibraryBookIdentity identity,
        out string errorCode,
        out string errorMessage)
    {
        identity = null!;
        errorCode = string.Empty;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errorCode = "invalid_book_identity";
            errorMessage = "Title is required.";
            return false;
        }

        // No type means no evidence of a paper copy: it is a metadata-only
        // digital book, never the other way round. A physical book is a claim
        // someone makes, not a default (see BookLookupService, which no longer
        // supplies one).
        var type = string.IsNullOrWhiteSpace(request.Type)
            ? "ebook"
            : request.Type.Trim().ToLowerInvariant();
        if (type is not ("physical" or "ebook" or "audiobook"))
        {
            errorCode = "invalid_book_identity";
            errorMessage = $"Unknown book type: {request.Type}. Use physical, ebook, or audiobook.";
            return false;
        }

        identity = new LibraryBookIdentity(
            BookIdentityNormalizer.NormalizeTitle(request.Title),
            BookIdentityNormalizer.NormalizeAuthor(request.Author),
            BookIdentityNormalizer.NormalizeIsbn(request.Isbn),
            BookIdentityNormalizer.NormalizeAsin(request.Asin),
            type);
        return true;
    }
}

internal enum LibraryMatchKind
{
    /// <summary>An existing row is the answer; nothing is created.</summary>
    Matched,

    /// <summary>The caller has to choose among <see cref="LibraryMatchDecision.Candidates"/>.</summary>
    ConfirmationRequired,

    /// <summary>The request is invalid or conflicts with existing identity.</summary>
    Failure,

    /// <summary>No safe match: create, optionally as an edition of <see cref="LibraryMatchDecision.TargetWorkId"/>.</summary>
    Create,
}

internal sealed record LibraryMatchDecision(
    LibraryMatchKind Kind,
    BookModel? Matched = null,
    Guid? TargetWorkId = null,
    IReadOnlyList<LibraryCandidate>? Candidates = null,
    string? Reply = null,
    string? ErrorCode = null);

/// <summary>
/// Decides whether a create-or-match request names a book Nostos already has
/// (issue #1611). This is the policy half of creating a book: it never writes,
/// so the decision order — explicit confirmation, then identifier, then type
/// consistency, then title/author — is testable on its own and stays identical
/// for the strict (Ask Nostos) and permissive (legacy REST) callers.
/// </summary>
internal static class LibraryBookMatcher
{
    public static async Task<LibraryMatchDecision> DecideAsync(
        NostosDbContext db,
        LibraryCreateBookRequest request,
        LibraryBookIdentity identity,
        bool strictConfirmation,
        CancellationToken ct)
    {
        // 1. Explicit confirmation: use the caller-selected existing row.
        if (request.ConfirmedBookId.HasValue)
        {
            var confirmed = await db.Books.AsNoTracking()
                .Where(b => b.Id == request.ConfirmedBookId.Value)
                .Select(BookReadShapes.WithoutLocations)
                .SingleOrDefaultAsync(ct);
            if (confirmed is null)
                return new LibraryMatchDecision(LibraryMatchKind.Failure,
                    Reply: LibraryReplyFormatter.BookNotFound, ErrorCode: "book_not_found");

            return new LibraryMatchDecision(LibraryMatchKind.Matched, Matched: confirmed);
        }

        // 2. Identifier resolution.
        BookModel? isbnMatch = null;
        BookModel? asinMatch = null;
        if (identity.NormalizedIsbn is not null)
            isbnMatch = await db.Books.AsNoTracking()
                .Where(b => b.NormalizedIsbn == identity.NormalizedIsbn)
                .Select(BookReadShapes.WithoutLocations)
                .SingleOrDefaultAsync(ct);
        if (identity.NormalizedAsin is not null)
            asinMatch = await db.Books.AsNoTracking()
                .Where(b => b.NormalizedAsin == identity.NormalizedAsin)
                .Select(BookReadShapes.WithoutLocations)
                .SingleOrDefaultAsync(ct);

        if (isbnMatch is not null && asinMatch is not null && isbnMatch.Id != asinMatch.Id)
            return new LibraryMatchDecision(LibraryMatchKind.Failure,
                Reply: LibraryReplyFormatter.IdentityConflict, ErrorCode: "identity_conflict");

        if (isbnMatch is not null)
            return new LibraryMatchDecision(LibraryMatchKind.Matched, Matched: isbnMatch);
        if (asinMatch is not null)
            return new LibraryMatchDecision(LibraryMatchKind.Matched, Matched: asinMatch);

        // Identifier/type consistency (after resolution, before creation): a
        // fresh audiobook with an ISBN, or a physical/ebook with an ASIN, is
        // rejected rather than silently dropping the identifier.
        if (identity.Type == "audiobook" && identity.NormalizedIsbn is not null)
            return new LibraryMatchDecision(LibraryMatchKind.Failure,
                Reply: "Audiobooks use ASIN, not ISBN.", ErrorCode: "invalid_book_identity");
        if (identity.Type != "audiobook" && identity.NormalizedAsin is not null)
            return new LibraryMatchDecision(LibraryMatchKind.Failure,
                Reply: "Only audiobooks use ASIN.", ErrorCode: "invalid_book_identity");

        // 3. Title/author resolution. forceCreate bypasses ambiguity; exact
        //    identifier matches above always win over forceCreate. Exact
        //    matching requires BOTH title and author: title-only requests
        //    yield candidates below, never an automatic match.
        Guid? targetWorkId = null;
        if (!request.ForceCreate && !string.IsNullOrEmpty(identity.NormalizedTitle))
        {
            var all = await db.Books.AsNoTracking()
                .Select(BookReadShapes.WithoutLocations)
                .ToListAsync(ct);
            var hasAuthor = !string.IsNullOrEmpty(identity.NormalizedAuthor);
            var exact = hasAuthor
                ? all
                    .Where(b => EditionMatcher.IsAutoMatch(b.Title, b.Author, request.Title, request.Author))
                    .ToList()
                : [];

            if (exact.Count > 0)
            {
                targetWorkId = exact[0].WorkId;

                var sameTypeEdition = exact.FirstOrDefault(b =>
                    (identity.Type == "audiobook" && b is AudioBookModel) ||
                    (identity.Type == "ebook" && b is EBookModel) ||
                    (identity.Type == "physical" && b is PhysicalBookModel));

                if (sameTypeEdition != null)
                {
                    var sameTypeCandidates = exact.Where(b => b.GetType() == sameTypeEdition.GetType()).ToList();
                    if (request.FileUploadExpected || request.FileAttached)
                    {
                        // A local file is on its way. It may only land on an
                        // edition that is still waiting for one (an interrupted
                        // add being resumed). An edition that already has its
                        // own file keeps it: the new file becomes another
                        // edition of the same work, never a replacement.
                        var awaitingFile = sameTypeCandidates.FirstOrDefault(b =>
                            !b.FileDetails.HasFile &&
                            b.Status is not (BookStatus.Downloading or BookStatus.Transcoding));
                        if (awaitingFile != null)
                            return new LibraryMatchDecision(LibraryMatchKind.Matched, Matched: awaitingFile);
                    }
                    else if (sameTypeCandidates.Count > 1)
                    {
                        if (strictConfirmation)
                        {
                            var candidates = sameTypeCandidates
                                .Select(b => LibraryCommandResults.ToCandidate(b,
                                    "exact title" + (string.IsNullOrEmpty(identity.NormalizedAuthor) ? "" : "+author match")))
                                .ToList();
                            return new LibraryMatchDecision(LibraryMatchKind.ConfirmationRequired,
                                Candidates: candidates,
                                Reply: LibraryReplyFormatter.ConfirmationRequired(candidates.Count));
                        }
                        // Non-strict (legacy REST path): multiple same-type editions exist; create anyway as before.
                    }
                    else
                    {
                        return new LibraryMatchDecision(LibraryMatchKind.Matched, Matched: sameTypeEdition);
                    }
                }

                // If same work exists but in different format/type:
                // We do not return matched! We proceed to create the new edition attached to targetWorkId.
            }
            else if (string.IsNullOrEmpty(identity.NormalizedAuthor) && strictConfirmation)
            {
                // Title-only with no exact match: fuzzy candidates before
                // guessing. Non-strict legacy path creates (old UI behavior).
                var normalizedTitle = identity.NormalizedTitle;
                var fuzzy = all
                    .Where(b =>
                    {
                        var bt = BookIdentityNormalizer.NormalizeTitle(b.Title);
                        return !string.IsNullOrEmpty(bt) && bt.Contains(normalizedTitle, StringComparison.Ordinal);
                    })
                    .Select(b => LibraryCommandResults.ToCandidate(b, "fuzzy title match"))
                    .ToList();
                if (fuzzy.Count > 0)
                    return new LibraryMatchDecision(LibraryMatchKind.ConfirmationRequired,
                        Candidates: fuzzy,
                        Reply: LibraryReplyFormatter.ConfirmationRequired(fuzzy.Count));
            }
        }

        // 4. No safe match: create (requires valid identifier, title+author,
        //    or explicit forceCreate; a bare title in strict mode needs more
        //    information).
        if (strictConfirmation
            && identity.NormalizedIsbn is null
            && identity.NormalizedAsin is null
            && string.IsNullOrEmpty(identity.NormalizedAuthor)
            && !request.ForceCreate)
        {
            return new LibraryMatchDecision(LibraryMatchKind.Failure,
                Reply: LibraryReplyFormatter.MoreInformationRequired, ErrorCode: "confirmation_required");
        }

        return new LibraryMatchDecision(LibraryMatchKind.Create, TargetWorkId: targetWorkId);
    }
}
