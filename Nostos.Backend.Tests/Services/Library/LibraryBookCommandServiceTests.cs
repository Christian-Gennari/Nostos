using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// The book row's commands (issue #1611): create-time membership, the
/// full-replacement update (the singular collection replaces every membership),
/// and attaching provenance with the file in one save.
/// </summary>
public sealed class LibraryBookCommandServiceTests
{
    [Fact]
    public async Task Create_records_every_requested_collection()
    {
        using var graph = new LibraryTestGraph();
        var (fiction, history) = await CreateCollectionsAsync(graph);

        var created = await CreateAsync(graph, collectionIds: [fiction, history]);

        (await MembershipAsync(graph, created)).Should().BeEquivalentTo([fiction, history]);
    }

    [Fact]
    public async Task Create_with_an_unknown_collection_is_refused_and_writes_nothing()
    {
        using var graph = new LibraryTestGraph();

        var result = await graph.Books.CreateOrMatchAsync(
            new LibraryCreateBookRequest("c", Key(), "physical", "Nowhere", Author: "A",
                CollectionIds: [Guid.NewGuid()]),
            strictConfirmation: false);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("collection_not_found");
        await using var db = await graph.Contexts.CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Update_replaces_membership_with_the_given_set()
    {
        using var graph = new LibraryTestGraph();
        var (fiction, history) = await CreateCollectionsAsync(graph);
        var bookId = await CreateAsync(graph, collectionIds: [fiction]);

        var updated = await graph.Books.UpdateAsync(
            new LibraryUpdateBookRequest("c", Key(), bookId, CollectionIds: [history]));

        updated.Data.Should().BeOfType<BookDto>();
        (await MembershipAsync(graph, bookId)).Should().BeEquivalentTo([history]);
    }

    [Fact]
    public async Task Update_with_the_singular_collection_replaces_all_memberships()
    {
        using var graph = new LibraryTestGraph();
        var (fiction, history) = await CreateCollectionsAsync(graph);
        var bookId = await CreateAsync(graph, collectionIds: [fiction, history]);

        await graph.Books.UpdateAsync(
            new LibraryUpdateBookRequest("c", Key(), bookId, CollectionId: history));

        (await MembershipAsync(graph, bookId)).Should().BeEquivalentTo([history]);
    }

    [Fact]
    public async Task Update_clearing_the_collection_drops_every_membership()
    {
        using var graph = new LibraryTestGraph();
        var (fiction, history) = await CreateCollectionsAsync(graph);
        var bookId = await CreateAsync(graph, collectionIds: [fiction, history]);

        await graph.Books.UpdateAsync(
            new LibraryUpdateBookRequest("c", Key(), bookId, ClearCollection: true));

        (await MembershipAsync(graph, bookId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Attaching_an_acquired_asset_sets_the_file_provenance_and_ready_state()
    {
        using var graph = new LibraryTestGraph();
        var bookId = await CreateAsync(graph, collectionIds: [], type: "ebook");

        var result = await graph.Books.AttachAcquiredAssetAsync(new LibraryAttachAcquiredAssetRequest(
            "c", Key(), bookId, "book.epub",
            ProviderId: "gutenberg", ProviderDisplayName: "Project Gutenberg",
            ExternalId: "1342", AssetId: "epub-1342"));

        result.Data.Should().BeOfType<LibraryAttachAcquiredAssetResultDto>();
        await using var db = await graph.Contexts.CreateDbContextAsync();
        var book = await db.Books.Include(b => b.Acquisition).SingleAsync(b => b.Id == bookId);
        book.FileDetails.HasFile.Should().BeTrue();
        book.FileDetails.FileName.Should().Be("book.epub");
        book.Status.Should().Be(BookStatus.Ready);
        book.Acquisition!.ProviderId.Should().Be("gutenberg");
        book.Acquisition.ExternalId.Should().Be("1342");
    }

    [Fact]
    public async Task Creating_the_same_title_and_author_twice_reuses_the_work()
    {
        using var graph = new LibraryTestGraph();
        var first = await CreateAsync(graph, collectionIds: [], type: "physical");
        var second = await CreateAsync(graph, collectionIds: [], type: "ebook");

        await using var db = await graph.Contexts.CreateDbContextAsync();
        var workIds = await db.Books.AsNoTracking()
            .Where(b => b.Id == first || b.Id == second)
            .Select(b => b.WorkId)
            .ToListAsync();

        workIds.Distinct().Should().HaveCount(1, "a second edition of the same title and author joins the work");
        workIds.Should().HaveCount(2);
    }

    private static string Key() => $"k-{Guid.NewGuid():N}";

    private static async Task<(Guid Fiction, Guid History)> CreateCollectionsAsync(LibraryTestGraph graph)
    {
        var fiction = (CollectionDto)(await graph.Collections.CreateAsync(
            new LibraryCreateCollectionRequest("c", Key(), "Fiction"))).Data!;
        var history = (CollectionDto)(await graph.Collections.CreateAsync(
            new LibraryCreateCollectionRequest("c", Key(), "History"))).Data!;
        return (fiction.Id, history.Id);
    }

    private static async Task<Guid> CreateAsync(
        LibraryTestGraph graph,
        IReadOnlyList<Guid> collectionIds,
        string type = "physical")
    {
        var result = await graph.Books.CreateOrMatchAsync(
            new LibraryCreateBookRequest("c", Key(), type, "A Book", Author: "An Author",
                CollectionIds: collectionIds),
            strictConfirmation: false);
        return ((LibraryCreateOrMatchResultDto)result.Data!).BookId!.Value;
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
}
