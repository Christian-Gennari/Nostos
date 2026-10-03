using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class TopicEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public TopicEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Stats_returns_reference_counts_and_most_used_topic()
    {
        var before = (await Client.GetFromJsonAsync<TopicStatsDto>("/api/topics/stats"))!;
        var suffix = Guid.NewGuid().ToString("N");
        var alpha = $"Stats Alpha {suffix}";
        var shared = $"Stats Shared {suffix}";
        var single = $"Stats Single {suffix}";

        var noteContents = new[]
        {
            $"[[{alpha}]] [[{shared}]] [[{single}]]",
            $"[[{alpha}]] [[{shared}]]",
        }
        .Concat(Enumerable.Repeat($"[[{alpha}]]", 8))
        .ToArray();
        await CreateBookWithNotesAsync(noteContents);

        var after = (await Client.GetFromJsonAsync<TopicStatsDto>("/api/topics/stats"))!;

        after.TotalTopics.Should().Be(before.TotalTopics + 3);
        after.TotalReferences.Should().Be(before.TotalReferences + 13);
        after.SingleNoteTopics.Should().Be(before.SingleNoteTopics + 1);
        after.MostUsedName.Should().Be(alpha);
        after.MostUsedCount.Should().Be(10);
    }

    [Fact]
    public async Task Legacy_concepts_route_aliases_the_topics_handlers()
    {
        var name = $"Alias {Guid.NewGuid():N}";
        await CreateBookWithNotesAsync($"[[{name}]] aliased");

        var viaTopics = (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;
        var viaConcepts = (await Client.GetFromJsonAsync<TopicDto[]>("/api/concepts"))!;

        viaConcepts.Select(t => t.Id).Should().BeEquivalentTo(viaTopics.Select(t => t.Id));
        var created = viaConcepts.Single(t => t.Name == name);
        var detail = await Client.GetAsync($"/api/concepts/{created.Id}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Related_returns_shared_topics_in_usage_order_and_unknown_is_empty()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var anchorName = $"Related Anchor {suffix}";
        var sharedName = $"Related Shared {suffix}";
        var occasionalName = $"Related Occasional {suffix}";

        var book = await CreateBookWithNotesAsync(
            $"[[{anchorName}]] [[{sharedName}]] [[{occasionalName}]]",
            $"[[{anchorName}]] [[{sharedName}]]",
            $"[[{anchorName}]]");

        var notes = (await Client.GetFromJsonAsync<NoteDto[]>($"/api/books/{book.Id}/notes"))!;
        var bothShared = notes
            .Where(note => note.Content.Contains($"[[{sharedName}]]", StringComparison.Ordinal))
            .Select(note => note.Id)
            .OrderBy(id => id)
            .ToList();
        var occasionalShared = notes
            .Where(note => note.Content.Contains($"[[{occasionalName}]]", StringComparison.Ordinal))
            .Select(note => note.Id)
            .OrderBy(id => id)
            .ToList();

        var topics = await GetTopicsAsync();
        var anchor = topics.Single(c => c.Name == anchorName);

        var related = await Client.GetFromJsonAsync<RelatedTopicDto[]>(
            $"/api/topics/{anchor.Id}/related");

        related.Should().HaveCount(2);
        related![0].Id.Should().Be(topics.Single(c => c.Name == sharedName).Id);
        related[0].Name.Should().Be(sharedName);
        related[0].SharedNotes.Should().Be(2);
        related[0].SharedNoteIds.Should().Equal(bothShared);

        related[1].Id.Should().Be(topics.Single(c => c.Name == occasionalName).Id);
        related[1].Name.Should().Be(occasionalName);
        related[1].SharedNotes.Should().Be(1);
        related[1].SharedNoteIds.Should().Equal(occasionalShared);

        var unknown = await Client.GetAsync($"/api/topics/{Guid.NewGuid()}/related");
        unknown.StatusCode.Should().Be(HttpStatusCode.OK);
        (await unknown.Content.ReadFromJsonAsync<RelatedTopicDto[]>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Detail_includes_note_created_at()
    {
        var topicName = $"Created At {Guid.NewGuid():N}";
        var book = await CreateBookWithNotesAsync($"[[{topicName}]]");
        var note = (await Client.GetFromJsonAsync<NoteDto[]>($"/api/books/{book.Id}/notes"))!.Single();
        var expected = new DateTime(2025, 4, 26, 15, 30, 0, DateTimeKind.Utc);

        await using (var db = await OpenDbAsync())
        {
            var stored = await db.Notes.SingleAsync(n => n.Id == note.Id);
            stored.CreatedAt = expected;
            await db.SaveChangesAsync();
        }

        var topic = (await GetTopicsAsync()).Single(c => c.Name == topicName);
        var detail = (await Client.GetFromJsonAsync<TopicDetailDto>(
            $"/api/topics/{topic.Id}"))!;

        detail.Notes.Single().CreatedAt.Should().Be(expected);
    }

    [Fact]
    public async Task Rename_trims_name_and_unknown_id_returns_404()
    {
        var oldName = $"Rename Old {Guid.NewGuid():N}";
        var book = await CreateBookWithNotesAsync($"[[{oldName}]]");
        var oldTopic = (await GetTopicsAsync()).Single(c => c.Name == oldName);
        var newName = $"Rename New {Guid.NewGuid():N}";

        var response = await Client.PutAsJsonAsync(
            $"/api/topics/{oldTopic.Id}",
            new UpdateTopicDto($"  {newName}  "));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<TopicDto>())!.Name.Should().Be(newName);

        var unknown = await Client.PutAsJsonAsync(
            $"/api/topics/{Guid.NewGuid()}",
            new UpdateTopicDto("Anything"));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var notes = await Client.GetFromJsonAsync<NoteDto[]>($"/api/books/{book.Id}/notes");
        notes.Should().ContainSingle();
    }

    [Fact]
    public async Task Rename_to_existing_name_merges_and_deduplicates_note_links()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sourceName = $"Rename Source {suffix}";
        var targetName = $"Rename Target {suffix}";
        await CreateBookWithNotesAsync(
            $"[[{sourceName}]] [[{targetName}]]",
            $"[[{sourceName}]]");

        var topics = await GetTopicsAsync();
        var source = topics.Single(c => c.Name == sourceName);
        var target = topics.Single(c => c.Name == targetName);

        var response = await Client.PutAsJsonAsync(
            $"/api/topics/{source.Id}",
            new UpdateTopicDto($" {targetName} "));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var survivor = (await response.Content.ReadFromJsonAsync<TopicDto>())!;
        survivor.Id.Should().Be(target.Id);
        survivor.Name.Should().Be(targetName);
        survivor.UsageCount.Should().Be(2);

        await using var db = await OpenDbAsync();
        (await db.Topics.FindAsync(source.Id)).Should().BeNull();
        (await db.NoteTopics.CountAsync(nc => nc.TopicId == target.Id)).Should().Be(2);
    }

    [Fact]
    public async Task Empty_rename_returns_400_without_changing_the_topic()
    {
        var name = $"Rename Validation {Guid.NewGuid():N}";
        await CreateBookWithNotesAsync($"[[{name}]]");
        var topic = (await GetTopicsAsync()).Single(c => c.Name == name);

        var response = await Client.PutAsJsonAsync(
            $"/api/topics/{topic.Id}",
            new UpdateTopicDto("  "));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetTopicsAsync()).Should().Contain(c => c.Id == topic.Id && c.Name == name);
    }

    [Fact]
    public async Task Merge_moves_links_once_and_unknown_ids_return_404()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sourceName = $"Merge Source {suffix}";
        var targetName = $"Merge Target {suffix}";
        await CreateBookWithNotesAsync(
            $"[[{sourceName}]] [[{targetName}]]",
            $"[[{sourceName}]]",
            $"[[{targetName}]]");

        var topics = await GetTopicsAsync();
        var source = topics.Single(c => c.Name == sourceName);
        var target = topics.Single(c => c.Name == targetName);

        var response = await Client.PostAsJsonAsync(
            $"/api/topics/{source.Id}/merge",
            new MergeTopicDto(target.Id));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var survivor = (await response.Content.ReadFromJsonAsync<TopicDto>())!;
        survivor.Id.Should().Be(target.Id);
        survivor.UsageCount.Should().Be(3);

        await using (var db = await OpenDbAsync())
        {
            (await db.Topics.FindAsync(source.Id)).Should().BeNull();
            (await db.NoteTopics.CountAsync(nc => nc.TopicId == target.Id)).Should().Be(3);
        }

        var unknownSource = await Client.PostAsJsonAsync(
            $"/api/topics/{Guid.NewGuid()}/merge",
            new MergeTopicDto(target.Id));
        unknownSource.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var unknownTarget = await Client.PostAsJsonAsync(
            $"/api/topics/{target.Id}/merge",
            new MergeTopicDto(Guid.NewGuid()));
        unknownTarget.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Self_merge_returns_400()
    {
        var name = $"Merge Self {Guid.NewGuid():N}";
        await CreateBookWithNotesAsync($"[[{name}]]");
        var topic = (await GetTopicsAsync()).Single(c => c.Name == name);

        var response = await Client.PostAsJsonAsync(
            $"/api/topics/{topic.Id}/merge",
            new MergeTopicDto(topic.Id));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_removes_topic_links_but_keeps_note_and_unknown_is_404()
    {
        var name = $"Delete Topic {Guid.NewGuid():N}";
        var book = await CreateBookWithNotesAsync($"Keep [[{name}]] text");
        var topic = (await GetTopicsAsync()).Single(c => c.Name == name);
        var notesBefore = await Client.GetFromJsonAsync<NoteDto[]>($"/api/books/{book.Id}/notes");

        var response = await Client.DeleteAsync($"/api/topics/{topic.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var db = await OpenDbAsync())
        {
            (await db.Topics.FindAsync(topic.Id)).Should().BeNull();
            (await db.NoteTopics.CountAsync(nc => nc.TopicId == topic.Id)).Should().Be(0);
            (await db.Notes.FindAsync(notesBefore!.Single().Id)).Should().NotBeNull();
        }

        var unknown = await Client.DeleteAsync($"/api/topics/{Guid.NewGuid()}");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Graph_includes_isolated_nodes()
    {
        var isolatedName = $"Graph Isolated {Guid.NewGuid():N}";
        // Create a topic via a single-topic note (no co-occurrence).
        await CreateBookWithNotesAsync($"[[{isolatedName}]]");

        var graph = (await Client.GetFromJsonAsync<TopicGraphDto>("/api/topics/graph"))!;

        graph.Nodes.Should().Contain(n => n.Name == isolatedName);
        // An isolated topic must have no edge touching it.
        var node = graph.Nodes.Single(n => n.Name == isolatedName);
        graph.Edges.Should().NotContain(e => e.SourceId == node.Id || e.TargetId == node.Id);
    }

    [Fact]
    public async Task Graph_creates_one_edge_per_co_occurring_pair_with_correct_shared_notes()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var alpha = $"Graph Alpha {suffix}";
        var bravo = $"Graph Bravo {suffix}";
        var charlie = $"Graph Charlie {suffix}";

        // Alpha and Bravo co-occur in 2 notes, Alpha and Charlie in 1.
        await CreateBookWithNotesAsync(
            $"[[{alpha}]] [[{bravo}]] [[{charlie}]]",
            $"[[{alpha}]] [[{bravo}]]");

        var graph = (await Client.GetFromJsonAsync<TopicGraphDto>("/api/topics/graph"))!;
        var nodes = graph.Nodes;
        var alphaNode = nodes.Single(n => n.Name == alpha);
        var bravoNode = nodes.Single(n => n.Name == bravo);
        var charlieNode = nodes.Single(n => n.Name == charlie);

        // One undirected edge per pair.
        var abEdge = graph.Edges.SingleOrDefault(e =>
            (e.SourceId == alphaNode.Id && e.TargetId == bravoNode.Id) ||
            (e.SourceId == bravoNode.Id && e.TargetId == alphaNode.Id));
        abEdge.Should().NotBeNull();
        abEdge!.SharedNotes.Should().Be(2);

        var acEdge = graph.Edges.SingleOrDefault(e =>
            (e.SourceId == alphaNode.Id && e.TargetId == charlieNode.Id) ||
            (e.SourceId == charlieNode.Id && e.TargetId == alphaNode.Id));
        acEdge.Should().NotBeNull();
        acEdge!.SharedNotes.Should().Be(1);

        var bcEdge = graph.Edges.SingleOrDefault(e =>
            (e.SourceId == bravoNode.Id && e.TargetId == charlieNode.Id) ||
            (e.SourceId == charlieNode.Id && e.TargetId == bravoNode.Id));
        bcEdge.Should().NotBeNull();
        bcEdge!.SharedNotes.Should().Be(1);
    }

    [Fact]
    public async Task Graph_has_no_duplicate_or_self_edges()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var a = $"Graph NoDup A {suffix}";
        var b = $"Graph NoDup B {suffix}";

        // Same pair in 3 notes should produce exactly one edge.
        await CreateBookWithNotesAsync(
            $"[[{a}]] [[{b}]]",
            $"[[{a}]] [[{b}]]",
            $"[[{a}]] [[{b}]]");

        var graph = (await Client.GetFromJsonAsync<TopicGraphDto>("/api/topics/graph"))!;
        var aNode = graph.Nodes.Single(n => n.Name == a);
        var bNode = graph.Nodes.Single(n => n.Name == b);

        // No self-edges.
        graph.Edges.Should().NotContain(e => e.SourceId == e.TargetId);

        // Exactly one edge for this pair.
        var pairEdges = graph.Edges.Where(e =>
            (e.SourceId == aNode.Id && e.TargetId == bNode.Id) ||
            (e.SourceId == bNode.Id && e.TargetId == aNode.Id)).ToList();
        pairEdges.Should().HaveCount(1);
        pairEdges[0].SharedNotes.Should().Be(3);
    }

    [Fact]
    public async Task Graph_includes_edges_for_topics_outside_top_30()
    {
        // Regression test: the old map only requested relationships for the
        // 30 most-used visible topics, starving topics #31+ of edges.
        // The graph endpoint must return edges for ALL topics, not just a top-N.
        var suffix = Guid.NewGuid().ToString("N");

        // Create 35 topics, each with one note. Two of them (ranked ~31 and ~32)
        // share a note — their edge must still appear.
        var topicNames = Enumerable.Range(1, 35)
            .Select(i => $"Graph R{i:D3} {suffix}")
            .ToList();

        // Give topics 1–30 extra notes so they rank higher.
        var noteContents = new List<string>();
        for (int i = 0; i < 30; i++)
        {
            // 3 separate notes for each of the top-30 topics.
            noteContents.Add($"[[{topicNames[i]}]]");
            noteContents.Add($"[[{topicNames[i]}]]");
            noteContents.Add($"[[{topicNames[i]}]]");
        }
        // Topics 31 and 32 share a note.
        noteContents.Add($"[[{topicNames[30]}]] [[{topicNames[31]}]]");
        // Add the remaining topics.
        for (int i = 32; i < 35; i++)
        {
            noteContents.Add($"[[{topicNames[i]}]]");
        }

        await CreateBookWithNotesAsync(noteContents.ToArray());

        var graph = (await Client.GetFromJsonAsync<TopicGraphDto>("/api/topics/graph"))!;
        var node31 = graph.Nodes.Single(n => n.Name == topicNames[30]);
        var node32 = graph.Nodes.Single(n => n.Name == topicNames[31]);

        // The edge between topics outside the old top-30 must exist.
        var edge = graph.Edges.SingleOrDefault(e =>
            (e.SourceId == node31.Id && e.TargetId == node32.Id) ||
            (e.SourceId == node32.Id && e.TargetId == node31.Id));
        edge.Should().NotBeNull("topics ranked 31 and 32 co-occur and the graph must include their edge");
        edge!.SharedNotes.Should().Be(1);
    }

    [Fact]
    public async Task Search_term_in_note_body_returns_topic_with_match_count_and_snippet()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var topicName = $"NoteBodyTopic {suffix}";
        var searchTerm = $"flabbergasted{suffix}";

        await CreateBookWithNotesAsync(
            $"[[{topicName}]] The professor was completely {searchTerm} by the unexpected test results."
        );

        var results = await Client.GetFromJsonAsync<TopicDto[]>($"/api/topics?search={searchTerm}");

        results.Should().NotBeNull();
        results.Should().ContainSingle(c => c.Name == topicName);
        var match = results!.Single(c => c.Name == topicName);
        match.NoteMatchCount.Should().Be(1);
        match.NoteMatchSnippet.Should().NotBeNullOrWhiteSpace();
        match.NoteMatchSnippet.Should().Contain(searchTerm);
    }

    [Fact]
    public async Task Search_same_term_in_several_notes_of_one_topic_returns_right_count()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var topicName = $"MultiNoteTopic {suffix}";
        var searchTerm = $"kaleidoscope{suffix}";

        await CreateBookWithNotesAsync(
            $"[[{topicName}]] First note containing {searchTerm} and observations.",
            $"[[{topicName}]] Second note with another mention of {searchTerm} here.",
            $"[[{topicName}]] Third note without the search term."
        );

        var results = await Client.GetFromJsonAsync<TopicDto[]>($"/api/topics?search={searchTerm}");

        results.Should().NotBeNull();
        var match = results!.Single(c => c.Name == topicName);
        match.UsageCount.Should().Be(3);
        match.NoteMatchCount.Should().Be(2);
        match.NoteMatchSnippet.Should().NotBeNullOrWhiteSpace();
        match.NoteMatchSnippet.Should().Contain(searchTerm);
    }

    [Fact]
    public async Task Search_finds_match_in_selected_text()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var topicName = $"SelectedTextTopic {suffix}";
        var searchTerm = $"chrysanthemum{suffix}";

        var bookResponse = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"SelectedText Book {suffix}",
        });
        bookResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var book = (await bookResponse.Content.ReadFromJsonAsync<BookDto>())!;

        var noteResponse = await Client.PostAsJsonAsync(
            $"/api/books/{book.Id}/notes",
            new
            {
                content = $"[[{topicName}]] Comment about the passage.",
                selectedText = $"The delicate {searchTerm} petals were preserved in the book."
            });
        noteResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var results = await Client.GetFromJsonAsync<TopicDto[]>($"/api/topics?search={searchTerm}");

        results.Should().NotBeNull();
        var match = results!.Single(c => c.Name == topicName);
        match.NoteMatchCount.Should().Be(1);
        match.NoteMatchSnippet.Should().NotBeNullOrWhiteSpace();
        match.NoteMatchSnippet.Should().Contain(searchTerm);
    }

    [Fact]
    public async Task Search_finds_match_in_book_title()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var topicName = $"BookTitleTopic {suffix}";
        var searchTerm = $"supernova{suffix}";

        var bookResponse = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Chronicles of the {searchTerm} Explosion",
        });
        bookResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var book = (await bookResponse.Content.ReadFromJsonAsync<BookDto>())!;

        var noteResponse = await Client.PostAsJsonAsync(
            $"/api/books/{book.Id}/notes",
            new { content = $"[[{topicName}]] Observations on stellar collapse." });
        noteResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var results = await Client.GetFromJsonAsync<TopicDto[]>($"/api/topics?search={searchTerm}");

        results.Should().NotBeNull();
        var match = results!.Single(c => c.Name == topicName);
        match.NoteMatchCount.Should().Be(1);
        match.NoteMatchSnippet.Should().NotBeNullOrWhiteSpace();
        match.NoteMatchSnippet.Should().Contain(searchTerm);
    }

    [Fact]
    public async Task Search_matches_case_insensitively()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var topicName = $"CaseInsensitiveTopic {suffix}";
        var baseTerm = $"luminescence{suffix}";

        await CreateBookWithNotesAsync(
            $"[[{topicName}]] Observed nocturnal {baseTerm.ToLowerInvariant()} in samples."
        );

        var results = await Client.GetFromJsonAsync<TopicDto[]>($"/api/topics?search={baseTerm.ToUpperInvariant()}");

        results.Should().NotBeNull();
        results.Should().ContainSingle(c => c.Name == topicName);
        var match = results!.Single(c => c.Name == topicName);
        match.NoteMatchCount.Should().Be(1);
        match.NoteMatchSnippet.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Search_with_no_matches_returns_empty_list()
    {
        var nonExistentTerm = $"nonexistent_{Guid.NewGuid():N}";

        var results = await Client.GetFromJsonAsync<TopicDto[]>($"/api/topics?search={nonExistentTerm}");

        results.Should().NotBeNull();
        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_omitted_returns_full_list()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var topicName = $"FullListTopic {suffix}";

        await CreateBookWithNotesAsync($"[[{topicName}]] A note for full list test.");

        var fullListWithoutParam = await Client.GetFromJsonAsync<TopicDto[]>("/api/topics");
        var fullListWithBlankParam = await Client.GetFromJsonAsync<TopicDto[]>("/api/topics?search=");

        fullListWithoutParam.Should().NotBeNull();
        fullListWithoutParam.Should().Contain(c => c.Name == topicName);

        fullListWithBlankParam.Should().NotBeNull();
        fullListWithBlankParam.Should().Contain(c => c.Name == topicName);
    }

    private async Task<BookDto> CreateBookWithNotesAsync(params string[] noteContents)
    {
        var bookResponse = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Topic endpoint test {Guid.NewGuid():N}",
        });
        bookResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var book = (await bookResponse.Content.ReadFromJsonAsync<BookDto>())!;

        foreach (var content in noteContents)
        {
            var noteResponse = await Client.PostAsJsonAsync(
                $"/api/books/{book.Id}/notes",
                new { content });
            noteResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        return book;
    }

    private async Task<TopicDto[]> GetTopicsAsync() =>
        (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;

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
