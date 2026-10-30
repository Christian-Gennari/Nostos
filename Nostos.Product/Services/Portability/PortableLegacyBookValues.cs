using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Reads the rating, favourite and review of an archive written before they
/// moved onto the work (data version 5 and older), where each edition carried
/// its own copy. They go through <see cref="BookValueRollUp"/>, the same rule
/// the migration and "link as one book" use, so an old archive loses nothing:
/// the book takes the agreed or most recently read value and an edition that
/// disagreed keeps its own as an edition rating / review.
/// </summary>
internal static class PortableLegacyBookValues
{
    /// <summary>The first data version whose works carry rating, favourite and review.</summary>
    public const int FirstVersionWithWorkValues = 6;

    /// <summary>
    /// The archive as the current model reads it. A current archive is returned
    /// as it is. Call it once, on the freshly deserialised payload: the result
    /// keeps the old version number so manifest checks still agree, and rolling
    /// it up a second time would not be a no-op.
    /// </summary>
    public static PortableLibraryData ToCurrent(PortableLibraryData data)
    {
        if (data.Version >= FirstVersionWithWorkValues)
            return data;

        var editionsByWork = data.Books.ToLookup(b => b.WorkId);
        var books = data.Books.ToDictionary(b => b.Id);
        var works = new List<PortableWork>(data.Works.Count);

        foreach (var work in data.Works)
        {
            var editions = editionsByWork[work.Id].ToList();
            var result = BookValueRollUp.Combine(editions.Select(e => new BookValueSide(
                e.Id,
                e.Progress.LastReadAt,
                e.CreatedAt,
                e.Progress.Rating,
                e.Progress.PersonalReview,
                e.Progress.IsFavorite)));

            works.Add(work with
            {
                Rating = result.Rating,
                IsFavorite = result.IsFavorite,
                PersonalReview = result.Review,
            });

            foreach (var edition in editions)
            {
                books[edition.Id] = edition with
                {
                    Progress = edition.Progress with
                    {
                        Rating = BookValueRollUp.EditionRatingAfter(edition.Progress.Rating, result.Rating),
                        PersonalReview = BookValueRollUp.EditionReviewAfter(edition.Progress.PersonalReview, result.Review),
                        IsFavorite = false,
                    },
                };
            }
        }

        return data with
        {
            Works = works,
            Books = data.Books.Select(b => books[b.Id]).ToList(),
        };
    }
}
