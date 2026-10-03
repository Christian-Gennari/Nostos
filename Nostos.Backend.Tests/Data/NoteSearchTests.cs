using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Data;

/// <summary>
/// Note-text search and the unlinked-note listing (issue #158).
///
/// The defect these close: the index could only match topic NAMES, so a word
/// living only in a note's body or quote was unreachable, and a note belonging to
/// no topic could not be reached at all.
/// </summary>
public sealed class NoteSearchTests
{
    private static async Task<(AcquisitionHarness Harness, Guid BookId, Guid LinkedNoteId, Guid UnlinkedNoteId)>
        SeedAsync()
    {
        var harness = AcquisitionHarness.Create();

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        // BookModel is abstract (the concrete type is what a reader owns); the
        // physical subtype needs no extra columns for this fixture.
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Nostos Test Book" };
        db.Books.Add(book);

        var topic = new TopicModel { Id = Guid.NewGuid(), Topic = "virtue" };
        db.Topics.Add(topic);

        // One note that belongs to a topic, one that belongs to none — the case
        // the index could never surface.
        var linked = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Content = "Sisyphus is condemned to a task that is boring, difficult and futile.",
            SelectedText = "the perpetual performance of a task that is boring",
            CreatedAt = DateTime.UtcNow,
        };
        var unlinked = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = book.Id,
            Content = "An unrelated remark about marginalia.",
            SelectedText = "objective-list smuggling",
            CreatedAt = DateTime.UtcNow.AddSeconds(-5),
        };
        db.Notes.AddRange(linked, unlinked);
        db.NoteTopics.Add(new NoteTopicModel { NoteId = linked.Id, TopicId = topic.Id });
        await db.SaveChangesAsync();

        return (harness, book.Id, linked.Id, unlinked.Id);
    }

    [Fact]
    public async Task Finds_a_note_by_words_in_its_body()
    {
        var (harness, _, linkedId, _) = await SeedAsync();
        using var _h = harness;

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var hits = await new NoteRepository(db).SearchByTextAsync("Sisyphus", 50);

        hits.Should().ContainSingle().Which.Id.Should().Be(linkedId);
    }

    [Fact]
    public async Task Finds_a_note_by_words_in_the_quotation_it_captured()
    {
        // The quoted passage is usually the only place the book's own words appear.
        var (harness, _, linkedId, _) = await SeedAsync();
        using var _h = harness;

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var hits = await new NoteRepository(db).SearchByTextAsync("perpetual performance", 50);

        hits.Should().ContainSingle().Which.Id.Should().Be(linkedId);
    }

    [Fact]
    public async Task Finds_the_notes_of_a_book_by_its_title()
    {
        var (harness, _, _, _) = await SeedAsync();
        using var _h = harness;

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var hits = await new NoteRepository(db).SearchByTextAsync("Nostos Test Book", 50);

        hits.Should().HaveCount(2);
    }

    [Fact]
    public async Task Searches_a_note_that_belongs_to_no_topic()
    {
        // The whole point of #158: this note has no topic row to be reached by.
        var (harness, _, _, unlinkedId) = await SeedAsync();
        using var _h = harness;

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var hits = await new NoteRepository(db).SearchByTextAsync("marginalia", 50);

        hits.Should().ContainSingle().Which.Id.Should().Be(unlinkedId);
    }

    [Fact]
    public async Task Treats_wildcards_as_text_instead_of_matching_everything()
    {
        var (harness, _, _, _) = await SeedAsync();
        using var _h = harness;

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var repo = new NoteRepository(db);

        // A bare `%` is a LIKE wildcard: unescaped, this would return every note.
        (await repo.SearchByTextAsync("%", 50)).Should().BeEmpty();
        (await repo.SearchByTextAsync("_", 50)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_query_returns_nothing_rather_than_everything()
    {
        var (harness, _, _, _) = await SeedAsync();
        using var _h = harness;

        await using var db = await harness.ContextFactory.CreateDbContextAsync();

        (await new NoteRepository(db).SearchByTextAsync("   ", 50)).Should().BeEmpty();
    }

    [Fact]
    public async Task Lists_only_the_notes_that_belong_to_no_topic()
    {
        var (harness, _, _, unlinkedId) = await SeedAsync();
        using var _h = harness;

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var unlinked = await new NoteRepository(db).GetWithoutTopicsAsync(50, 0);

        unlinked.Should().ContainSingle().Which.Id.Should().Be(unlinkedId);
    }

    /// <summary>
    /// The review queue (issue #256) walks the whole unlinked set in pages, so the
    /// page and the total have to agree with each other.
    /// </summary>
    private static async Task<AcquisitionHarness> SeedUnlinkedAsync(int count)
    {
        var harness = AcquisitionHarness.Create();

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "A Book With Marginalia" };
        db.Books.Add(book);

        // Distinct CreatedAt values in a stable order: page boundaries are only
        // meaningful against a deterministic sort.
        for (var i = 0; i < count; i++)
        {
            db.Notes.Add(
                new NoteModel
                {
                    Id = Guid.NewGuid(),
                    BookId = book.Id,
                    Content = $"unlinked note {i}",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-i),
                }
            );
        }

        await db.SaveChangesAsync();
        return harness;
    }

    [Fact]
    public async Task Counts_every_note_that_belongs_to_no_topic()
    {
        var harness = await SeedUnlinkedAsync(5);
        using var _h = harness;

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var repo = new NoteRepository(db);

        (await repo.CountWithoutTopicsAsync()).Should().Be(5);
    }

    [Fact]
    public async Task Pages_the_unlinked_set_without_repeating_or_skipping_a_note()
    {
        var harness = await SeedUnlinkedAsync(5);
        using var _h = harness;

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var repo = new NoteRepository(db);

        var first = await repo.GetWithoutTopicsAsync(2, 0);
        var second = await repo.GetWithoutTopicsAsync(2, 2);
        var third = await repo.GetWithoutTopicsAsync(2, 4);

        first.Should().HaveCount(2);
        second.Should().HaveCount(2);
        third.Should().HaveCount(1);

        var ids = first.Concat(second).Concat(third).Select(n => n.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().HaveCount(5);
    }

    [Fact]
    public async Task An_offset_past_the_end_returns_an_empty_page_rather_than_the_first_page()
    {
        // The review queue asks for the row after its last one. Clamping the
        // offset back to zero here would silently re-serve the whole queue.
        var harness = await SeedUnlinkedAsync(3);
        using var _h = harness;

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var repo = new NoteRepository(db);

        var page = await repo.GetWithoutTopicsAsync(25, 3);

        page.Should().BeEmpty();
        (await repo.CountWithoutTopicsAsync()).Should().Be(3);
    }

    [Fact]
    public async Task A_note_that_gains_a_topic_leaves_the_queue_and_the_count()
    {
        // Resolving a note in review mode is an ordinary note save that adds the
        // association; the queue and its total both have to follow.
        var harness = await SeedUnlinkedAsync(3);
        using var _h = harness;

        await using var db = await harness.ContextFactory.CreateDbContextAsync();
        var repo = new NoteRepository(db);
        var topic = new TopicModel { Id = Guid.NewGuid(), Topic = "Freedom" };
        db.Topics.Add(topic);
        await db.SaveChangesAsync();

        var target = (await repo.GetWithoutTopicsAsync(1, 0)).Single();
        db.NoteTopics.Add(new NoteTopicModel { NoteId = target.Id, TopicId = topic.Id });
        await db.SaveChangesAsync();

        (await repo.CountWithoutTopicsAsync()).Should().Be(2);
        (await repo.GetWithoutTopicsAsync(25, 0)).Select(n => n.Id).Should().NotContain(target.Id);
    }
}
