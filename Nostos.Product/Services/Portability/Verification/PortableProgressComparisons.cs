using Nostos.Backend.Data.Models;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>Per-edition reading progress, including the edition-only rating and
/// review exception. The favourite lives on the work.</summary>
internal static class PortableProgressComparisons
{
    internal static readonly IReadOnlyList<ComparisonEntry<BookProgressContext>> BookProgressComparisons =
    [
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.LastLocation),
            Ef(("ReadingProgress", nameof(ReadingProgress.LastLocation))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.LastLocation), context.Row.LastLocation, context.Expected.LastLocation)),
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.ProgressPercent),
            Ef(("ReadingProgress", nameof(ReadingProgress.ProgressPercent))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.ProgressPercent), context.Row.ProgressPercent, context.Expected.ProgressPercent)),
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.Rating),
            Ef(("ReadingProgress", nameof(ReadingProgress.Rating))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.Rating), context.Row.Rating, context.Expected.Rating)),
        Entry<BookProgressContext>(
            // Legacy: the favourite is the book's and is compared on the work. An
            // edition no longer has one, and a payload (rolled up on read) carries none.
            nameof(PortableReadingProgress.IsFavorite),
            NoFields,
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(PortableReadingProgress.IsFavorite), false, context.Expected.IsFavorite)),
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.PersonalReview),
            Ef(("ReadingProgress", nameof(ReadingProgress.PersonalReview))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.PersonalReview), context.Row.PersonalReview, context.Expected.PersonalReview)),
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.LastReadAt),
            Ef(("ReadingProgress", nameof(ReadingProgress.LastReadAt))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.LastReadAt), context.Row.LastReadAt, context.Expected.LastReadAt)),
        Entry<BookProgressContext>(
            nameof(PortableReadingProgress.FinishedAt),
            Ef(("ReadingProgress", nameof(ReadingProgress.FinishedAt))),
            (context, failures) => CompareField(failures, "book", context.BookId, nameof(ReadingProgress.FinishedAt), context.Row.FinishedAt, context.Expected.FinishedAt)),
    ];
}
