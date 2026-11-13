using FluentAssertions;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// The note reads (issue #1619): ordering by the ids asked for, a bounded
/// page, and the same kind/facet rules the Brain has always had.
/// </summary>
public sealed class NoteReadServiceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public NoteReadServiceTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Many_notes_keep_the_order_of_the_ids_asked_for()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var first = await h.SeedNoteAsync(book.Id, "first");
        var second = await h.SeedNoteAsync(book.Id, "second");
        var third = await h.SeedNoteAsync(book.Id, "third");

        var hits = await h.Reads.GetManyAsync([third.Id, first.Id, second.Id]);

        hits.Select(hit => hit.Id).Should().Equal(third.Id, first.Id, second.Id);
    }

    [Fact]
    public async Task Many_notes_leaves_out_ids_that_name_no_note()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, "only");

        var hits = await h.Reads.GetManyAsync([note.Id, Guid.NewGuid()]);

        hits.Should().ContainSingle().Which.Id.Should().Be(note.Id);
    }

    [Fact]
    public async Task Many_notes_with_no_ids_asks_no_query()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());

        (await h.Reads.GetManyAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unlinked_page_is_clamped_and_reports_the_total()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        await h.SeedNoteAsync(book.Id, "one");
        await h.SeedNoteAsync(book.Id, "two");
        await h.SeedNoteAsync(book.Id, "three");

        var page = await h.Reads.GetUnlinkedAsync(limit: 0, offset: 0);

        page.Limit.Should().Be(1);
        page.Items.Should().ContainSingle();
        page.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task An_unknown_kind_matches_nothing()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        await h.SeedNoteAsync(book.Id, "a note");

        var page = await h.Reads.BrowseAsync(new NoteBrowseQuery(Kind: "poem"), limit: 25, offset: 0);

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task A_read_reports_how_many_editions_the_book_has()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var work = Guid.NewGuid();
        var edition = await h.SeedBookAsync<EBookModel>("First edition", work);
        await h.SeedBookAsync<EBookModel>("Second edition", work);
        var note = await h.SeedNoteAsync(edition.Id, "a note");

        var hit = await h.Reads.GetAsync(note.Id);

        hit.Should().NotBeNull();
        hit!.EditionCount.Should().Be(2);
    }

    [Fact]
    public async Task Review_of_an_unknown_note_is_null()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());

        (await h.Reads.GetForReviewAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Review_reports_the_note_and_its_sorted_topic_names()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, "a note");
        h.Db.NoteTopics.Add(new NoteTopicModel
        {
            NoteId = note.Id,
            Topic = new TopicModel { Topic = "Virtue" },
        });
        h.Db.NoteTopics.Add(new NoteTopicModel
        {
            NoteId = note.Id,
            Topic = new TopicModel { Topic = "Courage" },
        });
        await h.Db.SaveChangesAsync();

        var review = await h.Reads.GetForReviewAsync(note.Id);

        review.Should().NotBeNull();
        review!.BookTitle.Should().Be(book.Title);
        review.Content.Should().Be("a note");
        review.TopicNames.Should().Equal("Courage", "Virtue");
    }
}
