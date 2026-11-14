using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// Edition grouping (issue #1611): linking merges whole groups, unlinking gives
/// one edition its own work.
/// </summary>
public sealed class LibraryWorkServiceTests
{
    [Fact]
    public async Task Linking_moves_every_member_of_the_source_group_and_removes_its_work()
    {
        using var graph = new LibraryTestGraph();
        var fiction = await CreateAsync(graph, "Fictions", "Borges", "physical");
        var fictionEbook = await CreateAsync(graph, "Fictions", "Borges", "ebook");
        var other = await CreateAsync(graph, "The Aleph", "Borges", "physical");
        var before = await graph.CommittedVersionAsync();

        var result = await graph.Works.LinkAsync(
            new LibraryLinkWorkRequest("c", Key(), other.BookId, fiction.BookId));

        var membership = (LibraryWorkMembershipResultDto)result.Data!;
        membership.WorkId.Should().Be(fiction.WorkId);
        membership.EditionCount.Should().Be(3);
        membership.RemovedWorkId.Should().Be(other.WorkId);
        result.StateVersion.Should().Be((await graph.CommittedVersionAsync()).ToString());
        (await graph.CommittedVersionAsync()).Should().BeGreaterThan(before);

        await using var db = await graph.Contexts.CreateDbContextAsync();
        (await db.Books.CountAsync(b => b.WorkId == fiction.WorkId)).Should().Be(3);
        (await db.Works.AnyAsync(w => w.Id == other.WorkId)).Should().BeFalse();
    }

    [Fact]
    public async Task Unlinking_gives_the_edition_its_own_work_and_leaves_the_other_alone()
    {
        using var graph = new LibraryTestGraph();
        var physical = await CreateAsync(graph, "Fictions", "Borges", "physical");
        var ebook = await CreateAsync(graph, "Fictions", "Borges", "ebook");
        ebook.WorkId.Should().Be(physical.WorkId);

        var result = await graph.Works.UnlinkAsync(
            new LibraryUnlinkWorkRequest("c", Key(), ebook.BookId));

        var membership = (LibraryWorkMembershipResultDto)result.Data!;
        membership.WorkId.Should().NotBe(physical.WorkId);
        membership.EditionCount.Should().Be(1);

        await using var db = await graph.Contexts.CreateDbContextAsync();
        (await db.Books.SingleAsync(b => b.Id == physical.BookId)).WorkId.Should().Be(physical.WorkId);
    }

    private static string Key() => $"k-{Guid.NewGuid():N}";

    private static async Task<CreatedBook> CreateAsync(
        LibraryTestGraph graph,
        string title,
        string author,
        string type)
    {
        var result = await graph.Books.CreateOrMatchAsync(
            new LibraryCreateBookRequest("c", Key(), type, title, Author: author),
            strictConfirmation: false);
        var created = (LibraryCreateOrMatchResultDto)result.Data!;
        await using var db = await graph.Contexts.CreateDbContextAsync();
        var workId = await db.Books.AsNoTracking()
            .Where(b => b.Id == created.BookId!.Value)
            .Select(b => b.WorkId)
            .SingleAsync();
        return new CreatedBook(created.BookId!.Value, workId);
    }

    private sealed record CreatedBook(Guid BookId, Guid WorkId);
}
