using FluentAssertions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// Lexical note search (issue #1619): what a phrase finds, which hit comes
/// first, and the page bound the search box shares with every other list.
/// </summary>
public sealed class NoteSearchServiceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public NoteSearchServiceTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_word_in_the_body_finds_the_note()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book", author: null);
        var note = await h.SeedNoteAsync(book.Id, "reading about the will");

        var hits = await h.Search.SearchAsync("will", 50);

        hits.Should().ContainSingle().Which.Id.Should().Be(note.Id);
    }

    [Fact]
    public async Task The_note_holding_every_word_comes_first()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var schopenhauer = await h.SeedBookAsync<EBookModel>("The World as Will and Representation", author: "Arthur Schopenhauer");
        var other = await h.SeedBookAsync<EBookModel>("Notebook", author: null);
        var both = await h.SeedNoteAsync(schopenhauer.Id, "the will is not free");
        await h.SeedNoteAsync(other.Id, "will power in a notebook");

        var hits = await h.Search.SearchAsync("schopenhauer will", 50);

        hits.Should().HaveCountGreaterThanOrEqualTo(2);
        hits[0].Id.Should().Be(both.Id);
    }

    [Fact]
    public async Task A_search_page_is_bounded_even_when_the_caller_asks_for_none()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book", author: null);
        await h.SeedNoteAsync(book.Id, "will one");
        await h.SeedNoteAsync(book.Id, "will two");
        await h.SeedNoteAsync(book.Id, "will three");

        var hits = await h.Search.SearchAsync("will", limit: 0);

        hits.Should().ContainSingle();
    }

    [Fact]
    public async Task An_empty_query_finds_nothing()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book", author: null);
        await h.SeedNoteAsync(book.Id, "a note");

        (await h.Search.SearchAsync("   ", 50)).Should().BeEmpty();
    }

    [Fact]
    public async Task Book_ids_narrow_the_search_to_those_editions()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var kept = await h.SeedBookAsync<EBookModel>("Kept", author: null);
        var other = await h.SeedBookAsync<EBookModel>("Other", author: null);
        var hit = await h.SeedNoteAsync(kept.Id, "about the will");
        await h.SeedNoteAsync(other.Id, "about the will too");

        var hits = await h.Search.SearchAsync("will", 50, [kept.Id]);

        hits.Should().ContainSingle().Which.Id.Should().Be(hit.Id);
    }

    [Fact]
    public async Task Notes_filed_under_a_matching_topic_are_included_without_text_match()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("The Salt Meridian", author: null);
        var topicTagged = await h.SeedNoteAsync(
            book.Id,
            "The keeper trims each wick at dusk and climbs to the gallery once at midnight.");
        var textMatched = await h.SeedNoteAsync(
            book.Id,
            "Repair philosophy: attention first, tools second.");
        var distractor = await h.SeedNoteAsync(
            book.Id,
            "A quick note on boat varnish and cedar planks.");

        var topic = new TopicModel { Id = Guid.NewGuid(), Topic = "Attention" };
        h.Db.Topics.Add(topic);
        h.Db.NoteTopics.Add(new NoteTopicModel { NoteId = topicTagged.Id, TopicId = topic.Id });
        await h.Db.SaveChangesAsync();

        var directHits = await h.Search.SearchAsync("attention", 50);
        directHits.Select(hit => hit.Id).Should().BeEquivalentTo([textMatched.Id, topicTagged.Id]);
        directHits.Select(hit => hit.Id).Should().NotContain(distractor.Id);

        var questionHits = await h.Search.SearchAsync("What did I note about attention?", 50);
        questionHits.Select(hit => hit.Id).Should().Contain([textMatched.Id, topicTagged.Id]);
    }
}
