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
    public async Task Link_rejects_a_name_that_cannot_stand_inside_brackets(string name)
    {
        var (_, notes) = await CreateBookWithNotesAsync("Text.");

        var response = await Client.PostAsJsonAsync("/api/topics/link",
            new TopicLinkRequestDto([name], [notes[0].Id]));

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
