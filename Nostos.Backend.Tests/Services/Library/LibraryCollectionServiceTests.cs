using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// Collection commands (issue #1611): hierarchy rules, duplicate reuse, and the
/// delete order that protects books while removing the membership rows whose
/// FK is RESTRICT.
/// </summary>
public sealed class LibraryCollectionServiceTests
{
    [Fact]
    public async Task Creating_a_duplicate_sibling_returns_the_existing_collection()
    {
        using var graph = new LibraryTestGraph();
        var created = await CreateAsync(graph, "Fiction");
        var before = await graph.CommittedVersionAsync();

        var duplicate = await graph.Collections.CreateAsync(Request("fiction"));

        ((CollectionDto)duplicate.Data!).Id.Should().Be(created.Id);
        duplicate.Reply.Should().Be(LibraryReplyFormatter.CollectionExists("Fiction"));
        (await graph.CommittedVersionAsync()).Should().Be(before, "reusing a collection writes nothing");
    }

    [Fact]
    public async Task Moving_a_collection_under_its_own_descendant_is_a_cycle()
    {
        using var graph = new LibraryTestGraph();
        var root = await CreateAsync(graph, "Root");
        var child = await CreateAsync(graph, "Child", root.Id);

        var result = await graph.Collections.MoveAsync(
            new LibraryMoveCollectionRequest("c", Key(), root.Id, child.Id));

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_cycle");
        await using var db = await graph.Contexts.CreateDbContextAsync();
        (await db.Collections.SingleAsync(c => c.Id == root.Id)).ParentId.Should().BeNull();
    }

    [Fact]
    public async Task Deleting_a_collection_removes_membership_rows_and_keeps_the_books()
    {
        using var graph = new LibraryTestGraph();
        var collection = await CreateAsync(graph, "Fiction");
        var bookId = await SeedBookInAsync(graph, collection.Id);

        var result = await graph.Collections.DeleteAsync(
            new LibraryDeleteCollectionRequest("c", Key(), collection.Id, Confirm: true));

        ((LibraryDeleteCollectionResultDto)result.Data!).BooksUnlinked.Should().Be(1);
        await using var db = await graph.Contexts.CreateDbContextAsync();
        (await db.Collections.AnyAsync(c => c.Id == collection.Id)).Should().BeFalse();
        (await db.BookCollections.AnyAsync(bc => bc.CollectionId == collection.Id)).Should().BeFalse();
        (await db.Books.AnyAsync(b => b.Id == bookId)).Should().BeTrue("books survive a collection delete");
    }

    [Fact]
    public async Task Deleting_a_collection_with_children_is_refused()
    {
        using var graph = new LibraryTestGraph();
        var root = await CreateAsync(graph, "Root");
        await CreateAsync(graph, "Child", root.Id);

        var result = await graph.Collections.DeleteAsync(
            new LibraryDeleteCollectionRequest("c", Key(), root.Id, Confirm: true));

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_has_children");
    }

    [Fact]
    public async Task An_unchanged_combined_update_is_a_noop_without_a_version_bump()
    {
        using var graph = new LibraryTestGraph();
        var created = await CreateAsync(graph, "Fiction");
        var before = await graph.CommittedVersionAsync();

        var result = await graph.Collections.UpdateAsync("c", Key(), created.Id, "Fiction", null);

        result.Data.Should().BeEquivalentTo(new CollectionDto(created.Id, "Fiction", null));
        (await graph.CommittedVersionAsync()).Should().Be(before);
    }

    [Fact]
    public async Task A_rename_advances_the_state_version()
    {
        using var graph = new LibraryTestGraph();
        var created = await CreateAsync(graph, "Fiction");
        var before = await graph.CommittedVersionAsync();

        var result = await graph.Collections.UpdateAsync("c", Key(), created.Id, "Novels", null);

        ((CollectionDto)result.Data!).Name.Should().Be("Novels");
        (await graph.CommittedVersionAsync()).Should().BeGreaterThan(before);
    }

    private static LibraryCreateCollectionRequest Request(string name, Guid? parentId = null) =>
        new("c", Key(), name, parentId);

    private static string Key() => $"k-{Guid.NewGuid():N}";

    private static async Task<CollectionDto> CreateAsync(
        LibraryTestGraph graph,
        string name,
        Guid? parentId = null)
    {
        var result = await graph.Collections.CreateAsync(Request(name, parentId));
        return (CollectionDto)result.Data!;
    }

    private static async Task<Guid> SeedBookInAsync(LibraryTestGraph graph, Guid collectionId)
    {
        await using var db = await graph.Contexts.CreateDbContextAsync();
        var work = new WorkModel { Title = "A Book", NormalizedTitle = "A BOOK" };
        var book = new PhysicalBookModel { Title = "A Book", WorkId = work.Id, Work = work };
        db.Works.Add(work);
        db.Books.Add(book);
        db.BookCollections.Add(new BookCollectionModel { BookId = book.Id, CollectionId = collectionId });
        await db.SaveChangesAsync();
        return book.Id;
    }
}
