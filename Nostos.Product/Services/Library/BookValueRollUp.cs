using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// One party to a roll-up of what is said about a book: an edition (a legacy
/// archive, the migration that moved these values onto the work) or a whole
/// work (linking two works into one). <see cref="LastReadAt"/> is when the
/// party was last read, null when never.
/// </summary>
internal readonly record struct BookValueSide(
    Guid Id,
    DateTime? LastReadAt,
    DateTime CreatedAt,
    int Rating,
    string? Review,
    bool IsFavorite);

/// <summary>
/// What the book says after the parties are combined. A party listed in
/// <see cref="RatingLosers"/> / <see cref="ReviewLosers"/> had a value that
/// differs from the book's, so the caller keeps it as that party's edition
/// rating / review rather than dropping it.
/// </summary>
internal sealed record BookValueRollUpResult(
    int Rating,
    string? Review,
    bool IsFavorite,
    IReadOnlySet<Guid> RatingLosers,
    IReadOnlySet<Guid> ReviewLosers);

/// <summary>A side together with the editions that carry its values.</summary>
internal sealed record BookValueParty(BookValueSide Side, IReadOnlyList<BookModel> Editions);

/// <summary>
/// The one rule for combining rating, favourite and review when several
/// parties become one book, so nothing a person wrote is lost:
/// <list type="bullet">
/// <item>values agree, or only one party has one: it becomes the book's;</item>
/// <item>values differ: the party read most recently wins (last read first,
/// never read last, then newest, then lowest id, so it is deterministic), and
/// every other party keeps its own value as an edition exception;</item>
/// <item>favourite: on if any party has it on.</item>
/// </list>
/// The migration that created these columns (<c>MoveBookValuesToWork</c>) runs
/// the same rule in SQL, and a test replays both over the same rows.
/// </summary>
internal static class BookValueRollUp
{
    public static BookValueRollUpResult Combine(IEnumerable<BookValueSide> sides)
    {
        var all = sides.ToList();
        var ranked = all.OrderBy(s => s.LastReadAt.HasValue ? 0 : 1)
            .ThenByDescending(s => s.LastReadAt)
            .ThenByDescending(s => s.CreatedAt)
            .ThenBy(s => IdKey(s.Id), StringComparer.Ordinal)
            .ToList();

        var rating = ranked.FirstOrDefault(s => s.Rating > 0).Rating;
        var review = ranked.FirstOrDefault(s => !string.IsNullOrEmpty(s.Review)).Review;
        review = string.IsNullOrEmpty(review) ? null : review;

        return new BookValueRollUpResult(
            rating,
            review,
            all.Any(s => s.IsFavorite),
            all.Where(s => s.Rating > 0 && s.Rating != rating).Select(s => s.Id).ToHashSet(),
            all.Where(s => !string.IsNullOrEmpty(s.Review) && !string.Equals(s.Review, review, StringComparison.Ordinal))
                .Select(s => s.Id).ToHashSet());
    }

    /// <summary>
    /// An edition's own rating once the book holds <paramref name="bookRating"/>:
    /// kept when it differs (an exception), cleared when it says the same thing.
    /// </summary>
    public static int EditionRatingAfter(int editionRating, int bookRating) =>
        editionRating == bookRating ? 0 : editionRating;

    /// <inheritdoc cref="EditionRatingAfter"/>
    public static string? EditionReviewAfter(string? editionReview, string? bookReview) =>
        string.IsNullOrEmpty(editionReview) || string.Equals(editionReview, bookReview, StringComparison.Ordinal)
            ? null
            : editionReview;

    /// <summary>
    /// Merges <paramref name="parties"/> into <paramref name="survivor"/>: the
    /// work takes the combined values, a loser's value goes to the editions it
    /// owns unless they already hold their own, and every edition value equal
    /// to the book's is cleared. <paramref name="allEditions"/> is every
    /// edition of the surviving work, so a stale duplicate is cleared too.
    /// </summary>
    public static void Merge(
        WorkModel survivor,
        IReadOnlyList<BookValueParty> parties,
        IReadOnlyCollection<BookModel> allEditions)
    {
        var result = Combine(parties.Select(p => p.Side));
        survivor.Rating = result.Rating;
        survivor.PersonalReview = result.Review;
        survivor.IsFavorite = result.IsFavorite;

        foreach (var party in parties)
        {
            foreach (var edition in party.Editions)
            {
                if (result.RatingLosers.Contains(party.Side.Id) && edition.Progress.Rating == 0)
                    edition.Progress.Rating = party.Side.Rating;
                if (result.ReviewLosers.Contains(party.Side.Id) && string.IsNullOrEmpty(edition.Progress.PersonalReview))
                    edition.Progress.PersonalReview = party.Side.Review;
            }
        }

        foreach (var edition in allEditions)
        {
            edition.Progress.Rating = EditionRatingAfter(edition.Progress.Rating, survivor.Rating);
            edition.Progress.PersonalReview = EditionReviewAfter(edition.Progress.PersonalReview, survivor.PersonalReview);
        }
    }

    /// <summary>The side an edition makes when a book's values were stored per edition.</summary>
    public static BookValueSide EditionSide(BookModel edition, bool isFavorite = false) => new(
        edition.Id,
        edition.Progress.LastReadAt,
        edition.CreatedAt,
        edition.Progress.Rating,
        edition.Progress.PersonalReview,
        isFavorite);

    /// <summary>The side a whole work makes: its own values, read when its latest edition was.</summary>
    public static BookValueSide WorkSide(WorkModel work, IEnumerable<BookModel> editions) => new(
        work.Id,
        editions.Select(e => e.Progress.LastReadAt).Where(t => t.HasValue).DefaultIfEmpty().Max(),
        work.CreatedAt,
        work.Rating,
        work.PersonalReview,
        work.IsFavorite);

    /// <summary>Guid order that is the same on SQLite (upper-case text) and PostgreSQL (uuid).</summary>
    private static string IdKey(Guid id) => id.ToString("D").ToUpperInvariant();
}
