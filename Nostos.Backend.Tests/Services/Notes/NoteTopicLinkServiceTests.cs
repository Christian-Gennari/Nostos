using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// Topic linking (issue #1619): the note body stays the canonical source of
/// membership, a batch change is all-or-nothing, and linking to an existing
/// topic is idempotent.
/// </summary>
public sealed class NoteTopicLinkServiceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public NoteTopicLinkServiceTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Linking_appends_the_reference_and_links_the_topic()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, "a thought");

        var result = await h.Links.LinkTopicsAsync(["Virtue"], [note.Id]);

        result.Success.Should().BeTrue();
        result.Value!.ChangedNoteIds.Should().Equal(note.Id);
        result.Value.Notes.Should().ContainSingle().Which.TopicNames.Should().Equal("Virtue");

        var stored = await h.Db.Notes.AsNoTracking().SingleAsync(n => n.Id == note.Id);
        stored.Content.Should().Contain("[[Virtue]]");
        (await h.Db.NoteTopics.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Linking_a_topic_the_text_already_declares_changes_nothing()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, "a thought");

        await h.Links.LinkTopicsAsync(["Virtue"], [note.Id]);
        var second = await h.Links.LinkTopicsAsync(["Virtue"], [note.Id]);

        second.Success.Should().BeTrue();
        second.Value!.ChangedNoteIds.Should().BeEmpty();
        (await h.Db.NoteTopics.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Unlinking_removes_the_reference_and_the_link()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, "a thought about [[Virtue]]");
        h.Db.NoteTopics.Add(new NoteTopicModel
        {
            NoteId = note.Id,
            Topic = new TopicModel { Topic = "Virtue" },
        });
        await h.Db.SaveChangesAsync();

        var result = await h.Links.UnlinkTopicsAsync(["Virtue"], [note.Id]);

        result.Success.Should().BeTrue();
        result.Value!.ChangedNoteIds.Should().Equal(note.Id);
        var stored = await h.Db.Notes.AsNoTracking().SingleAsync(n => n.Id == note.Id);
        stored.Content.Should().NotContain("[[Virtue]]");
        (await h.Db.NoteTopics.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Linking_an_unknown_note_is_a_typed_refusal()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());

        var result = await h.Links.LinkTopicsAsync(["Virtue"], [Guid.NewGuid()]);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(NoteErrorCodes.NoteNotFound);
    }

    [Fact]
    public async Task A_batch_needs_both_a_topic_and_a_note()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());

        var result = await h.Links.LinkTopicsAsync([], [Guid.NewGuid()]);

        result.ErrorCode.Should().Be(NoteErrorCodes.InvalidTopicLink);
    }

    [Fact]
    public async Task Linking_to_an_existing_topic_is_idempotent()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, "a thought");
        var topic = new TopicModel { Topic = "Virtue" };
        h.Db.Topics.Add(topic);
        await h.Db.SaveChangesAsync();

        var first = await h.Links.LinkToExistingTopicAsync(note.Id, topic.Id);
        var second = await h.Links.LinkToExistingTopicAsync(note.Id, topic.Id);

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();
        second.Value!.Content.Should().Contain("[[Virtue]]");
        (await h.Db.NoteTopics.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Linking_to_an_unknown_topic_is_a_typed_refusal()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, "a thought");

        var result = await h.Links.LinkToExistingTopicAsync(note.Id, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(NoteErrorCodes.TopicNotFound);
    }
}
