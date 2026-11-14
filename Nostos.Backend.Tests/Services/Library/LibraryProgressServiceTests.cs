using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// Issue #1611: progress, reset, status and delete used to hand-roll their own
/// transaction and revision advance. They now run through
/// <see cref="Nostos.Backend.Services.Library.LibraryMutationExecutor"/>'s gate,
/// so each of them still advances the committed state version exactly once, and
/// a command that wrote nothing still advances nothing.
/// </summary>
public sealed class LibraryProgressServiceTests
{
    [Fact]
    public async Task Progress_update_advances_the_committed_version_and_returns_it()
    {
        using var graph = new LibraryTestGraph();
        var bookId = await CreateBookAsync(graph);
        var before = await graph.CommittedVersionAsync();

        var result = await graph.Progress.UpdateProgressAsync(bookId, "cfi(/6/4)", 40);

        var after = await graph.CommittedVersionAsync();
        long.Parse(result.StateVersion).Should().Be(after).And.BeGreaterThan(before);
        after.Should().Be(before + 1, "one progress save is one revision advance");
    }

    [Fact]
    public async Task Progress_update_for_a_missing_book_advances_nothing()
    {
        using var graph = new LibraryTestGraph();
        var before = await graph.CommittedVersionAsync();

        var result = await graph.Progress.UpdateProgressAsync(Guid.NewGuid(), "cfi", 10);

        ((LibraryErrorDto)result.Data!).Code.Should().Be("book_not_found");
        (await graph.CommittedVersionAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Progress_reset_advances_the_committed_version()
    {
        using var graph = new LibraryTestGraph();
        var bookId = await CreateBookAsync(graph);
        await graph.Progress.UpdateProgressAsync(bookId, "cfi", 100);
        var before = await graph.CommittedVersionAsync();

        var result = await graph.Progress.ResetProgressAsync(bookId);

        var after = await graph.CommittedVersionAsync();
        result.Data.Should().BeEquivalentTo(new { updated = true });
        long.Parse(result.StateVersion).Should().Be(after).And.Be(before + 1);
    }

    [Fact]
    public async Task Progress_reset_of_an_already_reset_book_advances_nothing()
    {
        using var graph = new LibraryTestGraph();
        var bookId = await CreateBookAsync(graph);
        var before = await graph.CommittedVersionAsync();

        var result = await graph.Progress.ResetProgressAsync(bookId);

        result.Data.Should().BeEquivalentTo(new { updated = false });
        result.StateVersion.Should().Be(before.ToString());
        (await graph.CommittedVersionAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Book_status_update_advances_the_committed_version()
    {
        using var graph = new LibraryTestGraph();
        var bookId = await CreateBookAsync(graph);
        var before = await graph.CommittedVersionAsync();

        var result = await graph.Progress.SetBookStatusAsync(bookId, BookStatus.Failed, "testing");

        var after = await graph.CommittedVersionAsync();
        long.Parse(result.StateVersion).Should().Be(after).And.Be(before + 1);

        await using var db = await graph.Contexts.CreateDbContextAsync();
        var stored = await db.Books.SingleAsync(b => b.Id == bookId);
        stored.Status.Should().Be(BookStatus.Failed);
        stored.StatusMessage.Should().Be("testing");
    }

    [Fact]
    public async Task Book_delete_advances_the_committed_version_and_removes_the_empty_work()
    {
        using var graph = new LibraryTestGraph();
        var bookId = await CreateBookAsync(graph);
        var before = await graph.CommittedVersionAsync();

        var result = await graph.Books.DeleteAsync(bookId);

        var after = await graph.CommittedVersionAsync();
        long.Parse(result.StateVersion).Should().Be(after).And.BeGreaterThan(before);

        await using var db = await graph.Contexts.CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(0);
        (await db.Works.CountAsync()).Should().Be(0, "a work with no editions left is removed with its last book");
    }

    [Fact]
    public async Task Book_delete_for_a_missing_book_advances_nothing()
    {
        using var graph = new LibraryTestGraph();
        var before = await graph.CommittedVersionAsync();

        var result = await graph.Books.DeleteAsync(Guid.NewGuid());

        ((LibraryErrorDto)result.Data!).Code.Should().Be("book_not_found");
        (await graph.CommittedVersionAsync()).Should().Be(before);
    }

    private static async Task<Guid> CreateBookAsync(LibraryTestGraph graph)
    {
        var created = await graph.Books.CreateOrMatchAsync(
            new LibraryCreateBookRequest(
                "test-client",
                $"k-{Guid.NewGuid():N}",
                "physical",
                "Progress Book",
                Author: "Author A"),
            strictConfirmation: false);
        return ((LibraryCreateOrMatchResultDto)created.Data!).BookId!.Value;
    }
}
