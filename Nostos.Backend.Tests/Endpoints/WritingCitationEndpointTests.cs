using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

// Issue #1162: a writing's topics come from what it cites, never from tags.
// Every save rebuilds the derived WritingCitations index from the content;
// topics are joined at read time (writing -> cited notes -> NoteTopics).
public sealed class WritingCitationEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public WritingCitationEndpointTests(LibraryEndpointFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client => _factory.CreateClient();

    private sealed record Library(Guid BookId, Guid[] NoteIds, Guid OptimismId, Guid TheodicyId);

    /// <summary>
    /// One book with three notes: note 0 has Optimism, note 1 has Optimism
    /// and Theodicy, note 2 has no topic. Topic names are unique per test.
    /// </summary>
    private async Task<Library> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var book = new PhysicalBookModel
        {
            Id = Guid.NewGuid(),
            Title = "Candide " + suffix,
            Author = "Voltaire",
            CreatedAt = DateTime.UtcNow,
        };
        var optimism = new TopicModel { Id = Guid.NewGuid(), Topic = "Optimism " + suffix };
        optimism.NormalizedName = optimism.Topic.ToUpperInvariant();
        var theodicy = new TopicModel { Id = Guid.NewGuid(), Topic = "Theodicy " + suffix };
        theodicy.NormalizedName = theodicy.Topic.ToUpperInvariant();

        var notes = new[]
        {
            new NoteModel { Id = Guid.NewGuid(), BookId = book.Id, Book = book, Content = $"All is for the best [[{optimism.Topic}]]", CreatedAt = DateTime.UtcNow },
            new NoteModel { Id = Guid.NewGuid(), BookId = book.Id, Book = book, Content = $"The earthquake [[{optimism.Topic}]] [[{theodicy.Topic}]]", CreatedAt = DateTime.UtcNow },
            new NoteModel { Id = Guid.NewGuid(), BookId = book.Id, Book = book, Content = "Cultivate the garden", CreatedAt = DateTime.UtcNow },
        };

        db.Books.Add(book);
        db.Notes.AddRange(notes);
        db.Topics.AddRange(optimism, theodicy);
        db.NoteTopics.AddRange(
            new NoteTopicModel { NoteId = notes[0].Id, TopicId = optimism.Id },
            new NoteTopicModel { NoteId = notes[1].Id, TopicId = optimism.Id },
            new NoteTopicModel { NoteId = notes[1].Id, TopicId = theodicy.Id });
        await db.SaveChangesAsync();

        return new Library(book.Id, notes.Select(n => n.Id).ToArray(), optimism.Id, theodicy.Id);
    }

    private static string Cite(Guid bookId, Guid? noteId, string extra = "as=quote") =>
        $"[(Voltaire, 1759)](nostos:cite/{bookId}?{extra}{(noteId is { } id ? $"&src={id}" : string.Empty)})";

    private async Task<Guid> CreateAsync(string type = "Document", Guid? parentId = null, string name = "Essay")
    {
        var response = await Client.PostAsJsonAsync("/api/writings", new CreateWritingDto(name, type, parentId));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<WritingDto>())!.Id;
    }

    private async Task<WritingContentDto> SaveAsync(Guid writingId, string content, string name = "Essay")
    {
        var response = await Client.PutAsJsonAsync($"/api/writings/{writingId}", new UpdateWritingDto(name, content));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<WritingContentDto>())!;
    }

    private async Task<List<Guid>> RowsAsync(Guid writingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        return await db.WritingCitations
            .Where(c => c.WritingId == writingId)
            .Select(c => c.NoteId)
            .ToListAsync();
    }

    private async Task<TopicWritingUsageDto> UsageAsync(Guid topicId, int? limit = null)
    {
        var url = $"/api/topics/{topicId}/writings" + (limit is { } l ? $"?limit={l}" : string.Empty);
        var response = await Client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TopicWritingUsageDto>())!;
    }

    // --- Broken or odd content ---

    [Fact]
    public async Task Malformed_links_and_invalid_src_are_ignored_and_the_text_is_kept_as_written()
    {
        var library = await SeedAsync();
        var writingId = await CreateAsync();
        var content = string.Join("\n\n",
            $"Half deleted [(Voltaire, 1759](nostos:cite/{library.BookId}?src={library.NoteIds[0]}",
            $"[(Odd)](nostos:cite/{library.BookId}?src=not-a-guid)",
            $"[(Odd)](nostos:cite/??src={library.NoteIds[0]})",
            Cite(library.BookId, library.NoteIds[1]));

        var saved = await SaveAsync(writingId, content);

        saved.Content.Should().Be(content);
        (await RowsAsync(writingId)).Should().Equal(library.NoteIds[1]);
    }

    [Fact]
    public async Task A_book_only_citation_and_a_bibliography_entry_add_no_topic()
    {
        var library = await SeedAsync();
        var writingId = await CreateAsync();

        await SaveAsync(writingId, string.Join("\n\n",
            Cite(library.BookId, null, "p=3"),
            "[^1]: " + Cite(library.BookId, library.NoteIds[0], "as=bib")));

        (await RowsAsync(writingId)).Should().BeEmpty();
        (await UsageAsync(library.OptimismId)).Total.Should().Be(0);
    }

    [Fact]
    public async Task A_src_from_another_library_is_ignored_not_an_error()
    {
        var library = await SeedAsync();
        var writingId = await CreateAsync();

        await SaveAsync(writingId, Cite(library.BookId, Guid.NewGuid()) + " " + Cite(library.BookId, library.NoteIds[0]));

        (await RowsAsync(writingId)).Should().Equal(library.NoteIds[0]);
    }

    [Fact]
    public async Task The_same_note_cited_several_times_counts_once()
    {
        var library = await SeedAsync();
        var writingId = await CreateAsync();

        await SaveAsync(writingId, string.Join(" ",
            Cite(library.BookId, library.NoteIds[1], "p=1&as=quote"),
            Cite(library.BookId, library.NoteIds[1], "p=2&as=note"),
            Cite(library.BookId, library.NoteIds[1], "as=reference")));

        (await RowsAsync(writingId)).Should().Equal(library.NoteIds[1]);
        var usage = await UsageAsync(library.TheodicyId);
        usage.Total.Should().Be(1);
        var only = usage.NoteCitations.Should().ContainSingle().Subject;
        only.NoteId.Should().Be(library.NoteIds[1]);
        only.Writings.Should().Be(1);
        only.WritingIds.Should().Equal(writingId);
    }

    [Fact]
    public async Task Citing_only_notes_without_topics_touches_no_topic()
    {
        var library = await SeedAsync();
        var writingId = await CreateAsync();

        await SaveAsync(writingId, Cite(library.BookId, library.NoteIds[2]));

        (await RowsAsync(writingId)).Should().Equal(library.NoteIds[2]);
        var sources = await Client.GetFromJsonAsync<List<NoteSearchHitDto>>(
            $"/api/notes/by-ids?ids={library.NoteIds[2]}");
        sources!.Single().TopicNames.Should().BeEmpty("the Studio then shows no Touches line");
    }

    // --- Things deleted underneath ---

    [Fact]
    public async Task Deleting_a_note_removes_its_rows_keeps_the_text_and_drops_the_count()
    {
        var library = await SeedAsync();
        var writingId = await CreateAsync();
        var content = Cite(library.BookId, library.NoteIds[0]) + " " + Cite(library.BookId, library.NoteIds[1]);
        await SaveAsync(writingId, content);
        (await UsageAsync(library.TheodicyId)).Total.Should().Be(1);

        (await Client.DeleteAsync($"/api/notes/{library.NoteIds[1]}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RowsAsync(writingId)).Should().Equal(library.NoteIds[0]);
        (await Client.GetFromJsonAsync<WritingContentDto>($"/api/writings/{writingId}"))!.Content.Should().Be(content);
        (await UsageAsync(library.TheodicyId)).Total.Should().Be(0);
        var sources = await Client.GetFromJsonAsync<List<NoteSearchHitDto>>(
            $"/api/notes/by-ids?ids={library.NoteIds[0]},{library.NoteIds[1]}");
        sources!.Select(s => s.Id).Should().Equal([library.NoteIds[0]], "a removed source is simply absent");
    }

    [Fact]
    public async Task Deleting_the_book_cascades_through_its_notes_to_the_rows()
    {
        var library = await SeedAsync();
        var writingId = await CreateAsync();
        await SaveAsync(writingId, Cite(library.BookId, library.NoteIds[0]));

        (await Client.DeleteAsync($"/api/books/{library.BookId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RowsAsync(writingId)).Should().BeEmpty();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        (await db.WritingCitations.AnyAsync(c => library.NoteIds.Contains(c.NoteId))).Should().BeFalse("no orphan row is left");
    }

    [Fact]
    public async Task Deleting_a_writing_or_its_folder_removes_its_rows_and_updates_used_in()
    {
        var library = await SeedAsync();
        var folderId = await CreateAsync("Folder", name: "Drafts");
        var inFolder = await CreateAsync(parentId: folderId, name: "In folder");
        var alone = await CreateAsync(name: "Alone");
        await SaveAsync(inFolder, Cite(library.BookId, library.NoteIds[0]), "In folder");
        await SaveAsync(alone, Cite(library.BookId, library.NoteIds[0]), "Alone");
        (await UsageAsync(library.OptimismId)).Total.Should().Be(2);

        (await Client.DeleteAsync($"/api/writings/{alone}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await RowsAsync(alone)).Should().BeEmpty();
        (await UsageAsync(library.OptimismId)).Writings.Select(w => w.Id).Should().Equal(inFolder);

        (await Client.DeleteAsync($"/api/writings/{folderId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await RowsAsync(inFolder)).Should().BeEmpty();
        (await UsageAsync(library.OptimismId)).Total.Should().Be(0);
    }

    [Fact]
    public async Task Renaming_merging_and_unlinking_a_topic_show_at_once_without_touching_the_index()
    {
        var library = await SeedAsync();
        var writingId = await CreateAsync();
        await SaveAsync(writingId, Cite(library.BookId, library.NoteIds[1]));
        var rowsBefore = await RowsAsync(writingId);

        // Rename: same topic id, same writings.
        (await Client.PutAsJsonAsync($"/api/topics/{library.TheodicyId}", new UpdateTopicDto("Evil " + Guid.NewGuid().ToString("N")[..6])))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await UsageAsync(library.TheodicyId)).Total.Should().Be(1);
        var named = await Client.GetFromJsonAsync<List<NoteSearchHitDto>>($"/api/notes/by-ids?ids={library.NoteIds[1]}");
        named!.Single().TopicNames.Should().Contain(name => name.StartsWith("Evil "));

        // Merge Theodicy into Optimism: the writing now reaches Optimism only.
        (await Client.PostAsJsonAsync($"/api/topics/{library.TheodicyId}/merge", new MergeTopicDto(library.OptimismId)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client.GetAsync($"/api/topics/{library.TheodicyId}/writings")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await UsageAsync(library.OptimismId)).Total.Should().Be(1);

        // Unlink Optimism from the cited note: no remaining use.
        var optimism = (await Client.GetFromJsonAsync<TopicDetailDto>($"/api/topics/{library.OptimismId}"))!.Name;
        (await Client.PostAsJsonAsync("/api/topics/unlink", new TopicLinkRequestDto([optimism], [library.NoteIds[1]])))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var usage = await UsageAsync(library.OptimismId);
        usage.Total.Should().Be(0, "a topic with no remaining uses hides its Used in section");
        usage.Writings.Should().BeEmpty();
        usage.NoteCitations.Should().BeEmpty();

        (await RowsAsync(writingId)).Should().Equal(rowsBefore, "topics are joined at read time, never written to the index");
    }

    // --- Saving and syncing ---

    [Fact]
    public async Task The_last_save_wins_and_its_rows_match_its_content()
    {
        var library = await SeedAsync();
        var writingId = await CreateAsync();

        // Two tabs, one after another, then both at once.
        await SaveAsync(writingId, Cite(library.BookId, library.NoteIds[0]));
        await SaveAsync(writingId, Cite(library.BookId, library.NoteIds[1]));
        (await RowsAsync(writingId)).Should().Equal(library.NoteIds[1]);

        var contents = Enumerable.Range(0, 6)
            .Select(i => Cite(library.BookId, library.NoteIds[i % 3]))
            .ToArray();
        var responses = await Task.WhenAll(contents.Select(content =>
            Client.PutAsJsonAsync($"/api/writings/{writingId}", new UpdateWritingDto("Essay", content))));
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);

        var final = (await Client.GetFromJsonAsync<WritingContentDto>($"/api/writings/{writingId}"))!.Content;
        var expected = contents.Select((c, i) => (c, i)).First(x => x.c == final).i % 3;
        (await RowsAsync(writingId)).Should().Equal(library.NoteIds[expected]);
    }

    [Fact]
    public async Task Copying_a_citation_into_another_writing_gives_it_its_own_rows()
    {
        var library = await SeedAsync();
        var first = await CreateAsync(name: "First");
        var second = await CreateAsync(name: "Second");
        var citation = Cite(library.BookId, library.NoteIds[0]);

        await SaveAsync(first, citation, "First");
        await SaveAsync(second, "Pasted: " + citation, "Second");

        (await RowsAsync(first)).Should().Equal(library.NoteIds[0]);
        (await RowsAsync(second)).Should().Equal(library.NoteIds[0]);
        var usage = await UsageAsync(library.OptimismId);
        usage.Total.Should().Be(2);
        var note = usage.NoteCitations.Should().ContainSingle().Subject;
        note.NoteId.Should().Be(library.NoteIds[0]);
        note.Writings.Should().Be(2);
        note.WritingIds.Should().Equal([second, first], "the note's writings are listed newest edited first");
    }

    [Fact]
    public async Task A_kept_source_that_is_not_cited_does_not_count()
    {
        var library = await SeedAsync();
        var writingId = await CreateAsync();
        (await Client.PostAsJsonAsync($"/api/writings/{writingId}/notes", new AddWritingSourceDto(library.NoteIds[0])))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        await SaveAsync(writingId, "Prose that cites nothing yet.");

        (await RowsAsync(writingId)).Should().BeEmpty();
        (await UsageAsync(library.OptimismId)).Total.Should().Be(0);
    }

    // --- Scale ---

    [Fact]
    public async Task Used_in_lists_the_newest_five_and_counts_the_rest()
    {
        var library = await SeedAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            var id = await CreateAsync(name: $"Essay {i}");
            await SaveAsync(id, Cite(library.BookId, library.NoteIds[0]), $"Essay {i}");
            ids.Add(id);
            await Task.Delay(5);
        }

        var usage = await UsageAsync(library.OptimismId);

        usage.Total.Should().Be(7);
        usage.Writings.Select(w => w.Id).Should().Equal(Enumerable.Reverse(ids).Take(5), "newest first");
        (await UsageAsync(library.OptimismId, limit: 2)).Writings.Should().HaveCount(2);
    }

    [Fact]
    public async Task Usage_of_a_missing_topic_is_not_found()
    {
        (await Client.GetAsync($"/api/topics/{Guid.NewGuid()}/writings")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- Library ---

    [Fact]
    public async Task The_topic_map_never_shows_writings_as_nodes()
    {
        var library = await SeedAsync();
        var writingId = await CreateAsync(name: "Map essay");
        await SaveAsync(writingId, Cite(library.BookId, library.NoteIds[0]) + Cite(library.BookId, library.NoteIds[1]), "Map essay");

        var graph = (await Client.GetFromJsonAsync<TopicGraphDto>("/api/topics/graph"))!;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var topicIds = await db.Topics.Select(t => t.Id).ToListAsync();
        var writingIds = await db.Writings.Select(w => w.Id).ToListAsync();
        graph.Nodes.Select(n => n.Id).Should().BeSubsetOf(topicIds);
        graph.Nodes.Select(n => n.Id).Should().NotIntersectWith(writingIds);
        graph.Nodes.Should().NotContain(n => n.Name == "Map essay");
        graph.Edges.SelectMany(e => new[] { e.SourceId, e.TargetId }).Should().NotIntersectWith(writingIds);
    }

    [Fact]
    public async Task Notes_by_ids_rejects_too_many_and_skips_junk()
    {
        var library = await SeedAsync();

        var tooMany = string.Join(',', Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()));
        (await Client.GetAsync($"/api/notes/by-ids?ids={tooMany}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var hits = await Client.GetFromJsonAsync<List<NoteSearchHitDto>>(
            $"/api/notes/by-ids?ids=junk,{library.NoteIds[1]},,{library.NoteIds[0]}");
        hits!.Select(h => h.Id).Should().Equal(library.NoteIds[1], library.NoteIds[0]);
        hits[0].TopicNames.Should().HaveCount(2);
        (await Client.GetFromJsonAsync<List<NoteSearchHitDto>>("/api/notes/by-ids"))!.Should().BeEmpty();
    }
}
