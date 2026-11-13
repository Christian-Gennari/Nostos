using FluentAssertions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Data;

public sealed class RepositorySqlLikePatternHelperTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public RepositorySqlLikePatternHelperTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("100%", @"100\%")]
    [InlineData("snake_case", @"snake\_case")]
    [InlineData(@"a\b", @"a\\b")]
    [InlineData(@"100%_a\b", @"100\%\_a\\b")]
    [InlineData("", "")]
    public void Escape_escapes_wildcards_and_escape_character(string input, string expected)
    {
        SqlLikePatternHelper.Escape(input).Should().Be(expected);
    }

    [Fact]
    public void Escape_throws_when_value_is_null()
    {
        var act = () => SqlLikePatternHelper.Escape(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Contains_wraps_escaped_value_in_wildcards_and_uses_backslash_escape_char()
    {
        SqlLikePatternHelper.EscapeChar.Should().Be("\\");
        SqlLikePatternHelper.Contains(@"50%_off\now").Should().Be(@"%50\%\_off\\now%");
    }

    [Fact]
    public async Task Note_and_topic_searches_treat_like_metacharacters_as_literals()
    {
        var dbPath = _fixture.CreateDatabasePath();
        await using var db = _fixture.CreateContext(dbPath);

        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Escaping Guide" };
        db.Books.Add(book);

        var percentTopic = new TopicModel { Id = Guid.NewGuid(), Topic = "50% literal" };
        var underscoreTopic = new TopicModel { Id = Guid.NewGuid(), Topic = "a_b" };
        var backslashTopic = new TopicModel { Id = Guid.NewGuid(), Topic = @"x\y" };
        var noiseTopic = new TopicModel { Id = Guid.NewGuid(), Topic = "noise" };
        db.Topics.AddRange(percentTopic, underscoreTopic, backslashTopic, noiseTopic);

        var percentNote = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Content = "Discount 50% applies [[50% literal]]",
            CreatedAt = DateTime.UtcNow,
        };
        var underscoreNote = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Content = "Use a_b naming [[a_b]]",
            CreatedAt = DateTime.UtcNow.AddSeconds(-1),
        };
        var backslashNote = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Content = @"Windows x\y separator [[x\y]]",
            CreatedAt = DateTime.UtcNow.AddSeconds(-2),
        };
        var decoyNote = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Content = "Discount 500 applies and axb naming and xzy [[noise]]",
            CreatedAt = DateTime.UtcNow.AddSeconds(-3),
        };

        db.Notes.AddRange(percentNote, underscoreNote, backslashNote, decoyNote);
        db.NoteTopics.AddRange(
            new NoteTopicModel { NoteId = percentNote.Id, TopicId = percentTopic.Id },
            new NoteTopicModel { NoteId = underscoreNote.Id, TopicId = underscoreTopic.Id },
            new NoteTopicModel { NoteId = backslashNote.Id, TopicId = backslashTopic.Id },
            new NoteTopicModel { NoteId = decoyNote.Id, TopicId = noiseTopic.Id });
        await db.SaveChangesAsync();

        var noteRepo = new NoteRepository(db);
        var topicRepo = new TopicRepository(db);

        (await noteRepo.SearchByTextAsync("50%", 10))
            .Select(n => n.Id).Should().Equal(percentNote.Id);
        (await noteRepo.SearchByTextAsync("a_b", 10))
            .Select(n => n.Id).Should().Equal(underscoreNote.Id);
        (await noteRepo.SearchByTextAsync(@"x\y", 10))
            .Select(n => n.Id).Should().Equal(backslashNote.Id);

        (await topicRepo.SearchByNoteTextAsync("50%"))
            .Select(t => t.Id).Should().Equal(percentTopic.Id);
        (await topicRepo.SearchByNoteTextAsync("a_b"))
            .Select(t => t.Id).Should().Equal(underscoreTopic.Id);
        (await topicRepo.SearchByNoteTextAsync(@"x\y"))
            .Select(t => t.Id).Should().Equal(backslashTopic.Id);

        (await topicRepo.SearchByNoteTextAsync("%"))
            .Select(t => t.Id).Should().Equal(percentTopic.Id);
        (await topicRepo.SearchByNoteTextAsync("_"))
            .Select(t => t.Id).Should().Equal(underscoreTopic.Id);
    }
}
