using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// How many editions each note's book has, in one query for a page (issue
/// #1619, extracted from NoteService).
/// </summary>
public sealed class NoteEditionCounterTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public NoteEditionCounterTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Editions_are_counted_once_per_work()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var work = Guid.NewGuid();
        var otherWork = Guid.NewGuid();
        var first = await h.SeedBookAsync<EBookModel>("First edition", work);
        var second = await h.SeedBookAsync<EBookModel>("Second edition", work);
        var other = await h.SeedBookAsync<EBookModel>("Another book", otherWork);

        var counts = await h.Editions.CountsAsync(
            [Note(first), Note(first), Note(second), Note(other)]);

        counts.Should().HaveCount(2);
        counts[work].Should().Be(2);
        counts[otherWork].Should().Be(1);
    }

    [Fact]
    public async Task A_note_with_no_book_asks_no_query()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());

        var counts = await h.Editions.CountsAsync([Note(book: null)]);

        counts.Should().BeEmpty();
    }

    private static NoteModel Note(BookModel? book) => new()
    {
        Id = Guid.NewGuid(),
        BookId = book?.Id ?? Guid.NewGuid(),
        Book = book,
        Content = "a note",
    };
}
