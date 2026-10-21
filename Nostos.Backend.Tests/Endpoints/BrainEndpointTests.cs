using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// The routes the rebuilt Brain lists, counts and links notes through, and the
/// rule that a topic's rename, merge and delete keep the notes' text in step
/// with their links.
/// </summary>
public sealed class BrainEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public BrainEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Browse_lists_a_topic_and_narrows_to_notes_shared_with_another()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var virtue = $"Browse Virtue {suffix}";
        var wisdom = $"Browse Wisdom {suffix}";
        var (_, notes) = await CreateBookWithNotesAsync(
            $"One [[{virtue}]] [[{wisdom}]]",
            $"Two [[{virtue}]]",
            $"Three [[{wisdom}]]");
        var topics = await GetTopicsAsync();
        var virtueId = topics.Single(t => t.Name == virtue).Id;
        var wisdomId = topics.Single(t => t.Name == wisdom).Id;

        var inTopic = await BrowseAsync($"topicId={virtueId}");
        inTopic.TotalCount.Should().Be(2);
        inTopic.Items.Select(n => n.Id).Should().BeEquivalentTo([notes[0].Id, notes[1].Id]);

        var shared = await BrowseAsync($"topicId={virtueId}&withTopicId={wisdomId}");
        shared.TotalCount.Should().Be(1);
        shared.Items.Single().Id.Should().Be(notes[0].Id);
        shared.Items.Single().TopicNames.Should().BeEquivalentTo([virtue, wisdom]);
    }

    [Fact]
    public async Task Browse_orders_newest_oldest_and_by_book()
    {
        var marker = $"Order {Guid.NewGuid():N}";
        var (zeta, zetaNotes) = await CreateBookWithNotesAsync($"Zeta {marker} early", $"Zeta {marker} late");
        var (alpha, alphaNotes) = await CreateBookWithNotesAsync($"Alpha {marker}");
        await RetitleAsync(zeta.Id, $"Zeta book {marker}");
        await RetitleAsync(alpha.Id, $"Alpha book {marker}");
        await SetCreatedAsync(zetaNotes[0].Id, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SetCreatedAsync(zetaNotes[1].Id, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await SetCreatedAsync(alphaNotes[0].Id, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var newest = await BrowseAsync($"query={Uri.EscapeDataString(marker)}");
        var oldest = await BrowseAsync($"query={Uri.EscapeDataString(marker)}&order=oldest");
        var legacyOldest = await BrowseAsync($"query={Uri.EscapeDataString(marker)}&oldestFirst=true");
        var byBook = await BrowseAsync($"query={Uri.EscapeDataString(marker)}&order=book");

        newest.Items.Select(n => n.Id).Should().Equal(zetaNotes[1].Id, alphaNotes[0].Id, zetaNotes[0].Id);
        oldest.Items.Select(n => n.Id).Should().Equal(zetaNotes[0].Id, alphaNotes[0].Id, zetaNotes[1].Id);
        legacyOldest.Items.Select(n => n.Id).Should().Equal(oldest.Items.Select(n => n.Id));
        byBook.Items.Select(n => n.Id).Should().Equal(alphaNotes[0].Id, zetaNotes[0].Id, zetaNotes[1].Id);

        var invalid = await Client.GetAsync("/api/notes?order=sideways");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Books_lists_each_book_in_a_place_with_its_count_ignoring_the_book_filter()
    {
        var topic = $"Books Topic {Guid.NewGuid():N}";
        var (first, _) = await CreateBookWithNotesAsync($"[[{topic}]] a", $"[[{topic}]] b", "unlinked");
        var (second, _) = await CreateBookWithNotesAsync($"[[{topic}]] c");
        var topicId = (await GetTopicsAsync()).Single(t => t.Name == topic).Id;

        var facets = (await Client.GetFromJsonAsync<NoteBookFacetDto[]>(
            $"/api/notes/books?topicId={topicId}"))!;

        facets.Should().HaveCount(2);
        facets.Single(f => f.BookId == first.Id).NoteCount.Should().Be(2);
        facets.Single(f => f.BookId == second.Id).NoteCount.Should().Be(1);
        facets.Should().BeInAscendingOrder(f => f.BookTitle, StringComparer.CurrentCultureIgnoreCase);
    }

    [Fact]
    public async Task Summary_counts_all_notes_passages_and_unsorted()
    {
        var before = (await Client.GetFromJsonAsync<NoteSummaryDto>("/api/notes/summary"))!;
        await CreateBookWithNotesAsync($"[[Summary {Guid.NewGuid():N}]]", "No topic here");

        var after = (await Client.GetFromJsonAsync<NoteSummaryDto>("/api/notes/summary"))!;

        after.Total.Should().Be(before.Total + 2);
        after.Unsorted.Should().Be(before.Unsorted + 1);
        after.Passages.Should().Be(before.Passages);
    }

    [Fact]
    public async Task Link_adds_a_new_topic_to_many_notes_once_and_is_idempotent()
    {
        var topic = $"Linked {Guid.NewGuid():N}";
        var (_, notes) = await CreateBookWithNotesAsync("First thought.", "Second thought.", $"Has it [[{topic}]]");
        var ids = notes.Select(n => n.Id).ToArray();

        var response = await Client.PostAsJsonAsync("/api/topics/link", new TopicLinkRequestDto([topic], ids));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<TopicLinkResultDto>())!;
        result.ChangedNoteIds.Should().Equal(ids[0], ids[1]);
        result.Notes.Select(n => n.Id).Should().Equal(ids);
        result.Notes.Should().OnlyContain(n => n.TopicNames.Contains(topic));
        result.Notes[0].Content.Should().Be($"First thought.\n\n[[{topic}]]");

        (await GetTopicsAsync()).Where(t => t.Name == topic).Should().ContainSingle()
            .Which.UsageCount.Should().Be(3);

        var again = (await (await Client.PostAsJsonAsync("/api/topics/link",
            new TopicLinkRequestDto([topic], ids))).Content.ReadFromJsonAsync<TopicLinkResultDto>())!;
        again.ChangedNoteIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Unlink_undoes_a_link_and_keeps_words_written_in_prose()
    {
        var topic = $"Unlinked {Guid.NewGuid():N}";
        var (_, notes) = await CreateBookWithNotesAsync("Plain thought.", $"Writes about [[{topic}]] inline.");
        var ids = notes.Select(n => n.Id).ToArray();
        await Client.PostAsJsonAsync("/api/topics/link", new TopicLinkRequestDto([topic], ids));

        var response = await Client.PostAsJsonAsync("/api/topics/unlink", new TopicLinkRequestDto([topic], ids));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<TopicLinkResultDto>())!;
        result.Notes[0].Content.Should().Be("Plain thought.");
        result.Notes[1].Content.Should().Be($"Writes about {topic} inline.");
        result.Notes.Should().OnlyContain(n => n.TopicNames.Count == 0);
    }

    [Fact]
    public async Task Link_with_an_unknown_note_changes_nothing()
    {
        var topic = $"Atomic {Guid.NewGuid():N}";
        var (_, notes) = await CreateBookWithNotesAsync("Untouched.");

        var response = await Client.PostAsJsonAsync("/api/topics/link",
            new TopicLinkRequestDto([topic], [notes[0].Id, Guid.NewGuid()]));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using var db = await OpenDbAsync();
        (await db.Notes.SingleAsync(n => n.Id == notes[0].Id)).Content.Should().Be("Untouched.");
        (await db.Topics.AnyAsync(t => t.Topic == topic)).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("has ]] brackets")]
    [InlineData("two\nlines")]
    [InlineData("foo]")]
    public async Task Link_rejects_a_name_that_cannot_stand_inside_brackets(string name)
    {
        var (_, notes) = await CreateBookWithNotesAsync("Text.");

        var response = await Client.PostAsJsonAsync("/api/topics/link",
            new TopicLinkRequestDto([name], [notes[0].Id]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Link_and_unlink_keep_a_link_the_text_never_declared()
    {
        var kept = $"Join only {Guid.NewGuid():N}";
        var added = $"Added {Guid.NewGuid():N}";
        var (_, notes) = await CreateBookWithNotesAsync($"Text [[{kept}]]");
        var keptId = (await GetTopicsAsync()).Single(t => t.Name == kept).Id;

        // Older data and imports can hold a link whose name is not in the body.
        await using (var db = await OpenDbAsync())
        {
            var note = await db.Notes.SingleAsync(n => n.Id == notes[0].Id);
            note.Content = "Text without the reference";
            await db.SaveChangesAsync();
        }

        var linked = (await (await Client.PostAsJsonAsync("/api/topics/link",
            new TopicLinkRequestDto([added], [notes[0].Id]))).Content.ReadFromJsonAsync<TopicLinkResultDto>())!;
        linked.Notes.Single().TopicNames.Should().BeEquivalentTo([kept, added]);

        var unlinked = (await (await Client.PostAsJsonAsync("/api/topics/unlink",
            new TopicLinkRequestDto([kept], [notes[0].Id]))).Content.ReadFromJsonAsync<TopicLinkResultDto>())!;
        unlinked.ChangedNoteIds.Should().Equal(notes[0].Id);
        unlinked.Notes.Single().TopicNames.Should().Equal(added);
        (await GetTopicsAsync()).Single(t => t.Id == keptId).UsageCount.Should().Be(0);
    }

    [Fact]
    public async Task Editing_a_note_keeps_a_link_its_text_never_declared_and_unlink_still_removes_it()
    {
        var kept = $"Edit keeps {Guid.NewGuid():N}";
        var (_, notes) = await CreateBookWithNotesAsync($"Text [[{kept}]]");
        await using (var db = await OpenDbAsync())
        {
            var note = await db.Notes.SingleAsync(n => n.Id == notes[0].Id);
            note.Content = "Text without the reference";
            await db.SaveChangesAsync();
        }

        (await Client.PutAsJsonAsync($"/api/notes/{notes[0].Id}", new { content = "Edited text" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var edited = await GetNoteAsync(notes[0].Id);
        edited.TopicNames.Should().Equal(kept);
        edited.Content.Should().Be($"Edited text\n\n[[{kept}]]");

        await using (var db = await OpenDbAsync())
        {
            var note = await db.Notes.SingleAsync(n => n.Id == notes[0].Id);
            note.Content = "Link only again";
            await db.SaveChangesAsync();
        }
        var unlinked = (await (await Client.PostAsJsonAsync("/api/topics/unlink",
            new TopicLinkRequestDto([kept], [notes[0].Id]))).Content.ReadFromJsonAsync<TopicLinkResultDto>())!;
        unlinked.ChangedNoteIds.Should().Equal(notes[0].Id);
        unlinked.Notes.Single().TopicNames.Should().BeEmpty();
        unlinked.Notes.Single().Content.Should().Be("Link only again");
    }

    [Theory]
    [InlineData("a]]b")]
    [InlineData("ends with]")]
    [InlineData("alias|shown")]
    public async Task Rename_refuses_a_name_that_would_break_the_notes_references(string name)
    {
        var original = $"Rename Guard {Guid.NewGuid():N}";
        await CreateBookWithNotesAsync($"[[{original}]]");
        var topic = (await GetTopicsAsync()).Single(t => t.Name == original);

        var response = await Client.PutAsJsonAsync($"/api/topics/{topic.Id}", new UpdateTopicDto(name));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rename_rewrites_the_notes_so_the_old_name_does_not_return_on_the_next_edit()
    {
        var oldName = $"Before {Guid.NewGuid():N}";
        var newName = $"After {Guid.NewGuid():N}";
        var (_, notes) = await CreateBookWithNotesAsync($"About [[{oldName}]].");
        var topic = (await GetTopicsAsync()).Single(t => t.Name == oldName);

        (await Client.PutAsJsonAsync($"/api/topics/{topic.Id}", new UpdateTopicDto(newName)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var note = await GetNoteAsync(notes[0].Id);
        note.Content.Should().Be($"About [[{newName}]].");

        // Editing the note re-derives its links from the text: still the renamed topic.
        await Client.PutAsJsonAsync($"/api/notes/{note.Id}", new { content = note.Content + " More." });
        var topics = await GetTopicsAsync();
        topics.Should().NotContain(t => t.Name == oldName);
        topics.Single(t => t.Name == newName).Id.Should().Be(topic.Id);
    }

    [Fact]
    public async Task Merge_points_the_source_references_at_the_target()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var source = $"Merge From {suffix}";
        var target = $"Merge Into {suffix}";
        var (_, notes) = await CreateBookWithNotesAsync($"A [[{source}]] b.\n\n[[{source}]] [[{target}]]");
        var topics = await GetTopicsAsync();

        await Client.PostAsJsonAsync($"/api/topics/{topics.Single(t => t.Name == source).Id}/merge",
            new MergeTopicDto(topics.Single(t => t.Name == target).Id));

        (await GetNoteAsync(notes[0].Id)).Content.Should().Be($"A [[{target}]] b.\n\n[[{target}]]");
    }

    [Fact]
    public async Task Delete_turns_references_into_words_so_the_topic_stays_deleted()
    {
        var name = $"Deleted {Guid.NewGuid():N}";
        var (_, notes) = await CreateBookWithNotesAsync($"On [[{name}]].\n\n[[{name}]]");
        var topic = (await GetTopicsAsync()).Single(t => t.Name == name);

        (await Client.DeleteAsync($"/api/topics/{topic.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var note = await GetNoteAsync(notes[0].Id);
        note.Content.Should().Be($"On {name}.");
        await Client.PutAsJsonAsync($"/api/notes/{note.Id}", new { content = note.Content + " Edited." });
        (await GetTopicsAsync()).Should().NotContain(t => t.Name == name);
    }

    [Fact]
    public async Task A_topic_takes_the_readers_own_words_and_an_empty_value_clears_them()
    {
        var name = $"Described {Guid.NewGuid():N}";
        await CreateBookWithNotesAsync($"A note [[{name}]]");
        var id = (await GetTopicsAsync()).Single(t => t.Name == name).Id;

        var set = await Client.PutAsJsonAsync($"/api/topics/{id}/description",
            new UpdateTopicDescriptionDto("  Living well, as I understand it.  "));
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        (await set.Content.ReadFromJsonAsync<TopicDto>())!.Description.Should().Be("Living well, as I understand it.");
        (await GetTopicsAsync()).Single(t => t.Id == id).Description.Should().Be("Living well, as I understand it.");

        var cleared = await Client.PutAsJsonAsync($"/api/topics/{id}/description", new UpdateTopicDescriptionDto("   "));
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetTopicsAsync()).Single(t => t.Id == id).Description.Should().BeNull();

        var tooLong = await Client.PutAsJsonAsync($"/api/topics/{id}/description",
            new UpdateTopicDescriptionDto(new string('a', 4001)));
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var missing = await Client.PutAsJsonAsync($"/api/topics/{Guid.NewGuid()}/description",
            new UpdateTopicDescriptionDto("Words"));
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Merging_topics_keeps_both_sets_of_words()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var source = $"Merge Source {suffix}";
        var target = $"Merge Target {suffix}";
        await CreateBookWithNotesAsync($"One [[{source}]]", $"Two [[{target}]]");
        var topics = await GetTopicsAsync();
        var sourceId = topics.Single(t => t.Name == source).Id;
        var targetId = topics.Single(t => t.Name == target).Id;
        await Client.PutAsJsonAsync($"/api/topics/{targetId}/description", new UpdateTopicDescriptionDto("Kept first."));
        await Client.PutAsJsonAsync($"/api/topics/{sourceId}/description", new UpdateTopicDescriptionDto("Added after."));

        var merged = await Client.PostAsJsonAsync($"/api/topics/{sourceId}/merge", new MergeTopicDto(targetId));
        merged.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetTopicsAsync()).Single(t => t.Id == targetId).Description.Should().Be("Kept first.\n\nAdded after.");
    }

    [Fact]
    public async Task The_cleanup_keeps_a_described_topic_that_no_note_carries()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var described = $"Kept {suffix}";
        var bare = $"Swept {suffix}";
        await using (var db = await OpenDbAsync())
        {
            db.Topics.Add(new Nostos.Backend.Data.Models.TopicModel { Topic = described, Description = "Mine." });
            db.Topics.Add(new Nostos.Backend.Data.Models.TopicModel { Topic = bare });
            await db.SaveChangesAsync();

            await new Nostos.Backend.Data.Repositories.TopicRepository(db).DeleteOrphanedAsync();
        }

        var names = (await GetTopicsAsync()).Select(t => t.Name).ToList();
        names.Should().Contain(described);
        names.Should().NotContain(bare);
    }

    [Fact]
    public async Task A_name_in_another_case_is_the_same_topic()
    {
        var name = $"Stoicism {Guid.NewGuid():N}";
        var lower = name.ToLowerInvariant();
        var (_, notes) = await CreateBookWithNotesAsync($"On [[{name}]].", $"Also [[{lower}]].");

        var topic = (await GetTopicsAsync())
            .Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle().Which;
        topic.Name.Should().Be(name);
        topic.UsageCount.Should().Be(2);

        // Linking by name in yet another case reuses it too.
        var upper = name.ToUpperInvariant();
        var (_, more) = await CreateBookWithNotesAsync("Unlinked thought.");
        await Client.PostAsJsonAsync("/api/topics/link", new TopicLinkRequestDto([upper], [more[0].Id]));
        (await GetTopicsAsync())
            .Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle().Which.UsageCount.Should().Be(3);
    }

    [Fact]
    public async Task Renaming_onto_another_topics_name_in_another_case_merges_them()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var source = $"Ethics {suffix}";
        var target = $"Virtue {suffix}";
        await CreateBookWithNotesAsync($"[[{source}]]", $"[[{target}]]");
        var topics = await GetTopicsAsync();
        var sourceId = topics.Single(t => t.Name == source).Id;
        var targetId = topics.Single(t => t.Name == target).Id;

        var response = await Client.PutAsJsonAsync($"/api/topics/{sourceId}", new UpdateTopicDto(target.ToUpperInvariant()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TopicDto>())!.Id.Should().Be(targetId);
        (await GetTopicsAsync()).Should().NotContain(t => t.Id == sourceId);
    }

    [Fact]
    public async Task Renaming_a_topic_to_its_own_name_in_another_case_renames_it()
    {
        var name = $"logic {Guid.NewGuid():N}";
        var (_, notes) = await CreateBookWithNotesAsync($"On [[{name}]].");
        var topic = (await GetTopicsAsync()).Single(t => t.Name == name);
        var capitalised = char.ToUpperInvariant(name[0]) + name[1..];

        (await Client.PutAsJsonAsync($"/api/topics/{topic.Id}", new UpdateTopicDto(capitalised)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetTopicsAsync()).Single(t => t.Id == topic.Id).Name.Should().Be(capitalised);
        (await GetNoteAsync(notes[0].Id)).Content.Should().Be($"On [[{capitalised}]].");
    }

    [Fact]
    public async Task The_cleanup_merges_topics_that_differ_only_in_case()
    {
        var name = $"Ärende {Guid.NewGuid():N}";
        var (_, notes) = await CreateBookWithNotesAsync($"On [[{name}]].", $"Again [[{name}]].");
        var survivorId = (await GetTopicsAsync()).Single(t => t.Name == name).Id;

        // A variant made before names compared in any case, on a third note.
        var variant = name.ToLowerInvariant();
        var (_, more) = await CreateBookWithNotesAsync("Plain.");
        await using (var db = await OpenDbAsync())
        {
            var topic = new Nostos.Backend.Data.Models.TopicModel { Topic = variant, Description = "Mine." };
            db.Topics.Add(topic);
            var note = await db.Notes.SingleAsync(n => n.Id == more[0].Id);
            note.Content = $"Plain. [[{variant}]]";
            db.NoteTopics.Add(new Nostos.Backend.Data.Models.NoteTopicModel { NoteId = note.Id, TopicId = topic.Id });
            await db.SaveChangesAsync();

            (await new Nostos.Backend.Data.Repositories.TopicRepository(db).MergeCaseDuplicatesAsync()).Should().Be(1);
        }

        var survivor = (await GetTopicsAsync())
            .Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            .Should().ContainSingle().Which;
        survivor.Id.Should().Be(survivorId);
        survivor.UsageCount.Should().Be(3);
        survivor.Description.Should().Be("Mine.");
        (await GetNoteAsync(more[0].Id)).Content.Should().Be($"Plain. [[{name}]]");
    }

    private async Task<NoteSearchPageDto> BrowseAsync(string query) =>
        (await Client.GetFromJsonAsync<NoteSearchPageDto>($"/api/notes?limit=50&{query}"))!;

    private async Task<NoteSearchHitDto> GetNoteAsync(Guid id) =>
        (await Client.GetFromJsonAsync<NoteSearchHitDto>($"/api/notes/{id}"))!;

    private async Task<TopicDto[]> GetTopicsAsync() =>
        (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;

    private async Task<(BookDto Book, NoteDto[] Notes)> CreateBookWithNotesAsync(params string[] noteContents)
    {
        var bookResponse = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Brain endpoint test {Guid.NewGuid():N}",
        });
        bookResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var book = (await bookResponse.Content.ReadFromJsonAsync<BookDto>())!;

        var notes = new List<NoteDto>();
        foreach (var content in noteContents)
        {
            var noteResponse = await Client.PostAsJsonAsync($"/api/books/{book.Id}/notes", new { content });
            noteResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            notes.Add((await noteResponse.Content.ReadFromJsonAsync<NoteDto>())!);
        }

        return (book, notes.ToArray());
    }

    private async Task RetitleAsync(Guid bookId, string title)
    {
        await using var db = await OpenDbAsync();
        var book = await db.Books.SingleAsync(b => b.Id == bookId);
        book.Title = title;
        await db.SaveChangesAsync();
    }

    private async Task SetCreatedAsync(Guid noteId, DateTime createdAt)
    {
        await using var db = await OpenDbAsync();
        var note = await db.Notes.SingleAsync(n => n.Id == noteId);
        note.CreatedAt = createdAt;
        await db.SaveChangesAsync();
    }

    private async Task<NostosDbContext> OpenDbAsync()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_factory.DatabasePath}")
            .Options;
        var db = new NostosDbContext(options);
        await db.Database.OpenConnectionAsync();
        return db;
    }
}
