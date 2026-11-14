using Nostos.Backend.Data.Models;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>The reading list.</summary>
internal static class PortableReadingListComparisons
{
    internal static readonly IReadOnlyList<ComparisonEntry<ReadingListContext>> ReadingListComparisons =
    [
        Entry<ReadingListContext>(
            nameof(PortableReadingListEntry.Id),
            Ef(("ReadingListEntryModel", nameof(ReadingListEntryModel.Id))),
            (context, failures) => CompareField(failures, "readingListEntry", context.Row.Id, nameof(PortableReadingListEntry.Id), context.Row.Id, context.Expected.Id)),
        Entry<ReadingListContext>(
            nameof(PortableReadingListEntry.Title),
            Ef(("ReadingListEntryModel", nameof(ReadingListEntryModel.Title))),
            (context, failures) => CompareField(failures, "readingListEntry", context.Row.Id, nameof(PortableReadingListEntry.Title), context.Row.Title, context.Expected.Title)),
        Entry<ReadingListContext>(
            nameof(PortableReadingListEntry.Author),
            Ef(("ReadingListEntryModel", nameof(ReadingListEntryModel.Author))),
            (context, failures) => CompareField(failures, "readingListEntry", context.Row.Id, nameof(PortableReadingListEntry.Author), context.Row.Author, context.Expected.Author)),
        Entry<ReadingListContext>(
            nameof(PortableReadingListEntry.Isbn),
            Ef(("ReadingListEntryModel", nameof(ReadingListEntryModel.Isbn))),
            (context, failures) => CompareField(failures, "readingListEntry", context.Row.Id, nameof(PortableReadingListEntry.Isbn), context.Row.Isbn, context.Expected.Isbn)),
        Entry<ReadingListContext>(
            nameof(PortableReadingListEntry.PublishedDate),
            Ef(("ReadingListEntryModel", nameof(ReadingListEntryModel.PublishedDate))),
            (context, failures) => CompareField(failures, "readingListEntry", context.Row.Id, nameof(PortableReadingListEntry.PublishedDate), context.Row.PublishedDate, context.Expected.PublishedDate)),
        Entry<ReadingListContext>(
            nameof(PortableReadingListEntry.Why),
            Ef(("ReadingListEntryModel", nameof(ReadingListEntryModel.Why))),
            (context, failures) => CompareField(failures, "readingListEntry", context.Row.Id, nameof(PortableReadingListEntry.Why), context.Row.Why, context.Expected.Why)),
        Entry<ReadingListContext>(
            nameof(PortableReadingListEntry.RecommendedBy),
            Ef(("ReadingListEntryModel", nameof(ReadingListEntryModel.RecommendedBy))),
            (context, failures) => CompareField(failures, "readingListEntry", context.Row.Id, nameof(PortableReadingListEntry.RecommendedBy), context.Row.RecommendedBy, context.Expected.RecommendedBy)),
        Entry<ReadingListContext>(
            nameof(PortableReadingListEntry.NextPosition),
            Ef(("ReadingListEntryModel", nameof(ReadingListEntryModel.NextPosition))),
            (context, failures) => CompareField(failures, "readingListEntry", context.Row.Id, nameof(PortableReadingListEntry.NextPosition), context.Row.NextPosition, context.Expected.NextPosition)),
        Entry<ReadingListContext>(
            nameof(PortableReadingListEntry.CreatedAt),
            Ef(("ReadingListEntryModel", nameof(ReadingListEntryModel.CreatedAt))),
            (context, failures) => CompareField(failures, "readingListEntry", context.Row.Id, nameof(PortableReadingListEntry.CreatedAt), context.Row.CreatedAt, context.Expected.CreatedAt)),
    ];

    internal static void CompareReadingListEntries(
        PortableLibraryData data,
        List<ReadingListEntryModel> readingList,
        FailureList failures)
    {
        var expected = (data.ReadingListEntries ?? []).ToDictionary(item => item.Id);
        var actual = readingList.ToDictionary(item => item.Id);
        CompareIdSets("readingListEntry", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(ReadingListComparisons, new ReadingListContext(row, item), failures);
            }
        }
    }
}
