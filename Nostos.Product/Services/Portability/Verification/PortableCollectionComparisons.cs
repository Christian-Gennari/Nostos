using Nostos.Backend.Data.Models;
using static Nostos.Backend.Services.Portability.Verification.PortableVerificationPrimitives;

namespace Nostos.Backend.Services.Portability.Verification;

/// <summary>Collections and the book-to-collection membership pairs, including
/// each membership's own added-at value.</summary>
internal static class PortableCollectionComparisons
{
    internal static readonly IReadOnlyList<ComparisonEntry<CollectionContext>> CollectionComparisons =
    [
        Entry<CollectionContext>(
            nameof(PortableCollection.Id),
            Ef(("CollectionModel", nameof(CollectionModel.Id))),
            (context, failures) => CompareField(failures, "collection", context.Row.Id, nameof(PortableCollection.Id), context.Row.Id, context.Expected.Id)),
        Entry<CollectionContext>(
            nameof(PortableCollection.Name),
            Ef(("CollectionModel", nameof(CollectionModel.Name))),
            (context, failures) => CompareField(failures, "collection", context.Row.Id, nameof(PortableCollection.Name), context.Row.Name, context.Expected.Name)),
        Entry<CollectionContext>(
            nameof(PortableCollection.ParentId),
            Ef(("CollectionModel", nameof(CollectionModel.ParentId))),
            (context, failures) => CompareField(failures, "collection", context.Row.Id, nameof(PortableCollection.ParentId), context.Row.ParentId, context.Expected.ParentId)),
    ];

    internal static readonly IReadOnlyList<ComparisonEntry<MembershipContext>> BookCollectionComparisons =
    [
        PairEntry<MembershipContext>(
            nameof(PortableBookCollection.BookId),
            nameof(PortableBookCollection.CollectionId),
            Ef(
                ("BookCollectionModel", nameof(BookCollectionModel.BookId)),
                ("BookCollectionModel", nameof(BookCollectionModel.CollectionId))),
            (context, failures) => CompareJoinPairs(
                failures,
                "bookCollection",
                $"{nameof(PortableBookCollection.BookId)},{nameof(PortableBookCollection.CollectionId)}",
                context.Expected.Select(item => (item.BookId, item.CollectionId)),
                context.Rows.Select(row => (row.BookId, row.CollectionId)))),
        Entry<MembershipContext>(
            nameof(PortableBookCollection.AddedAt),
            Ef(("BookCollectionModel", nameof(BookCollectionModel.AddedAt))),
            (context, failures) =>
            {
                var expected = context.Expected
                    .GroupBy(item => (item.BookId, item.CollectionId))
                    .ToDictionary(group => group.Key, group => group.First().AddedAt);
                CompareAddedAt(
                    failures,
                    "bookCollection",
                    nameof(PortableBookCollection.AddedAt),
                    expected,
                    context.Rows.Select(row => ((row.BookId, row.CollectionId), row.AddedAt)));
            }),
    ];

    internal static void CompareCollections(
        PortableLibraryData data,
        List<CollectionModel> collections,
        FailureList failures)
    {
        var expected = data.Collections.ToDictionary(item => item.Id);
        var actual = collections.ToDictionary(item => item.Id);
        CompareIdSets("collection", expected.Keys, actual.Keys, failures);
        foreach (var (id, item) in expected)
        {
            if (actual.TryGetValue(id, out var row))
            {
                Run(CollectionComparisons, new CollectionContext(row, item), failures);
            }
        }
    }

    internal static void CompareBookCollections(
        PortableLibraryData data,
        List<BookCollectionModel> memberships,
        FailureList failures) =>
        Run(
            BookCollectionComparisons,
            new MembershipContext(memberships, data.BookCollections),
            failures);
}
