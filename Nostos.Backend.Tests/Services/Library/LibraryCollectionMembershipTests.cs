using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// Membership is <c>BookCollections</c> only, and the legacy singular
/// collection replaces every membership (issue #1611, AGENTS.md traps): both
/// shapes go through the same replacement.
/// </summary>
public sealed class LibraryCollectionMembershipTests
{
    [Fact]
    public async Task Replace_removes_what_is_not_wanted_and_adds_what_is_missing()
    {
        using var graph = new LibraryTestGraph();
        var (bookId, collections) = await SeedAsync(graph, ["Fiction", "History"], owned: ["Fiction"]);

        var replaced = await ReplaceAsync(graph, bookId, [collections["Fiction"], collections["Poetry"]]);

        replaced.Should().BeTrue();
        (await MembershipAsync(graph, bookId)).Should()
            .BeEquivalentTo([collections["Fiction"], collections["Poetry"]]);
    }

    [Fact]
    public async Task Replace_with_a_single_collection_drops_every_other_membership()
    {
        using var graph = new LibraryTestGraph();
        var (bookId, collections) = await SeedAsync(graph, ["Fiction", "History"], owned: ["Fiction", "History"]);

        await ReplaceAsync(graph, bookId, [collections["Poetry"]]);

        (await MembershipAsync(graph, bookId)).Should().BeEquivalentTo([collections["Poetry"]]);
    }

    [Fact]
    public async Task Replace_with_nothing_clears_membership_and_keeps_the_book()
    {
        using var graph = new LibraryTestGraph();
        var (bookId, _) = await SeedAsync(graph, ["Fiction"], owned: ["Fiction"]);

        await ReplaceAsync(graph, bookId, []);

        (await MembershipAsync(graph, bookId)).Should().BeEmpty();
        await using var db = await graph.Contexts.CreateDbContextAsync();
        (await db.Books.AnyAsync(b => b.Id == bookId)).Should().BeTrue();
    }

    [Fact]
    public async Task Replace_with_an_unknown_collection_changes_nothing()
    {
        using var graph = new LibraryTestGraph();
        var (bookId, collections) = await SeedAsync(graph, ["Fiction"], owned: ["Fiction"]);

        var replaced = await ReplaceAsync(graph, bookId, [collections["Fiction"], Guid.NewGuid()]);

        replaced.Should().BeFalse();
        (await MembershipAsync(graph, bookId)).Should().BeEquivalentTo([collections["Fiction"]]);
    }

    private static async Task<bool> ReplaceAsync(
        LibraryTestGraph graph,
        Guid bookId,
        IReadOnlyList<Guid> wanted)
    {
        await using var db = await graph.Contexts.CreateDbContextAsync();
        var result = await LibraryCollectionMembership.ReplaceAsync(db, bookId, wanted, CancellationToken.None);
        await db.SaveChangesAsync();
        return result;
    }

    private static async Task<HashSet<Guid>> MembershipAsync(LibraryTestGraph graph, Guid bookId)
    {
        await using var db = await graph.Contexts.CreateDbContextAsync();
        return (await db.BookCollections.AsNoTracking()
                .Where(bc => bc.BookId == bookId)
                .Select(bc => bc.CollectionId)
                .ToListAsync())
            .ToHashSet();
    }

    private static async Task<(Guid BookId, Dictionary<string, Guid> Collections)> SeedAsync(
        LibraryTestGraph graph,
        IReadOnlyList<string> collections,
        IReadOnlyList<string> owned)
    {
        await using var db = await graph.Contexts.CreateDbContextAsync();
        var work = new WorkModel
        {
            Title = "A Book",
            NormalizedTitle = "A BOOK",
        };
        var book = new PhysicalBookModel
        {
            Title = "A Book",
            WorkId = work.Id,
            Work = work,
        };
        db.Works.Add(work);
        db.Books.Add(book);

        var ids = new Dictionary<string, Guid>();
        foreach (var name in collections.Append("Poetry").Distinct())
        {
            var collection = new CollectionModel { Name = name };
            ids[name] = collection.Id;
            db.Collections.Add(collection);
        }

        await db.SaveChangesAsync();

        foreach (var name in owned)
            db.BookCollections.Add(new BookCollectionModel { BookId = book.Id, CollectionId = ids[name] });
        await db.SaveChangesAsync();

        return (book.Id, ids);
    }
}
