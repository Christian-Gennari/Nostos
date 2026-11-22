using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class TopicKindEndpointTests : IClassFixture<LibraryEndpointFactory>, IAsyncLifetime
{
    private readonly LibraryEndpointFactory _factory;

    public TopicKindEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    public Task InitializeAsync() => _factory.ResetStateAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task List_returns_six_builtin_kinds_in_sort_order_with_usage_counts()
    {
        var book = await CreateBookWithNotesAsync("[[Aristotle]] wrote about [[Virtue]] and [[Athens]]");
        book.Should().NotBeNull();

        var topics = (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;
        var aristotle = topics.Single(t => t.Name == "Aristotle");
        var virtue = topics.Single(t => t.Name == "Virtue");

        var assign = await Client.PutAsJsonAsync(
            "/api/topics/kind",
            new SetTopicsKindDto([aristotle.Id], TopicKindModel.PersonId));
        assign.StatusCode.Should().Be(HttpStatusCode.OK);

        var assignIdea = await Client.PutAsJsonAsync(
            "/api/topics/kind",
            new SetTopicsKindDto([virtue.Id], TopicKindModel.IdeaId));
        assignIdea.StatusCode.Should().Be(HttpStatusCode.OK);

        var kinds = (await Client.GetFromJsonAsync<TopicKindDto[]>("/api/topic-kinds"))!;
        kinds.Should().HaveCount(6);
        kinds.Select(k => (k.Id, k.Name, k.ColorKey, k.SortOrder, k.TopicCount))
            .Should()
            .Equal(
                (TopicKindModel.PersonId, "Person", "kind-1", 1, 1),
                (TopicKindModel.IdeaId, "Idea", "kind-2", 2, 1),
                (TopicKindModel.PlaceId, "Place", "kind-3", 3, 0),
                (TopicKindModel.WorkId, "Work", "kind-4", 4, 0),
                (TopicKindModel.EventId, "Event", "kind-5", 5, 0),
                (TopicKindModel.FieldId, "Field", "kind-6", 6, 0));
    }

    [Fact]
    public async Task Topic_list_search_and_graph_include_kind_fields()
    {
        var token = $"phenomenology{Guid.NewGuid():N}";
        await CreateBookWithNotesAsync($"[[Edmund Husserl]] founded {token}.");

        var topics = (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;
        var husserl = topics.Single(t => t.Name == "Edmund Husserl");
        husserl.KindId.Should().BeNull();
        husserl.KindSource.Should().BeNull();

        var set = await Client.PutAsJsonAsync(
            "/api/topics/kind",
            new SetTopicsKindDto([husserl.Id], TopicKindModel.PersonId));
        set.StatusCode.Should().Be(HttpStatusCode.OK);

        var listed = (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!
            .Single(t => t.Id == husserl.Id);
        listed.KindId.Should().Be(TopicKindModel.PersonId);
        listed.KindSource.Should().Be(TopicKindSources.Reader);

        var searched = (await Client.GetFromJsonAsync<TopicDto[]>($"/api/topics?search={token}"))!
            .Single(t => t.Id == husserl.Id);
        searched.KindId.Should().Be(TopicKindModel.PersonId);
        searched.KindSource.Should().Be(TopicKindSources.Reader);

        var graph = (await Client.GetFromJsonAsync<TopicGraphDto>("/api/topics/graph"))!;
        graph.Nodes.Single(n => n.Id == husserl.Id).KindId.Should().Be(TopicKindModel.PersonId);
    }

    [Fact]
    public async Task Create_adds_custom_kind_and_rejects_invalid_duplicate_and_limit()
    {
        var created = await Client.PostAsJsonAsync(
            "/api/topic-kinds",
            new CreateTopicKindDto("  Quote  ", "kind-7"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var kind = (await created.Content.ReadFromJsonAsync<TopicKindDto>())!;
        kind.Name.Should().Be("Quote");
        kind.ColorKey.Should().Be("kind-7");
        kind.SortOrder.Should().Be(7);
        kind.TopicCount.Should().Be(0);

        // Validation errors -> 400
        (await Client.PostAsJsonAsync("/api/topic-kinds", new CreateTopicKindDto("   ", "kind-8")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Client.PostAsJsonAsync("/api/topic-kinds", new CreateTopicKindDto(new string('x', 101), "kind-8")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Client.PostAsJsonAsync("/api/topic-kinds", new CreateTopicKindDto("Method", "kind-99")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Duplicate name (case-insensitive) -> 409
        (await Client.PostAsJsonAsync("/api/topic-kinds", new CreateTopicKindDto("person", "kind-8")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Duplicate color -> 409
        (await Client.PostAsJsonAsync("/api/topic-kinds", new CreateTopicKindDto("Method", "kind-1")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Fill up to 12 kinds, then the 13th returns 422
        for (var i = 8; i <= 12; i++)
        {
            var resp = await Client.PostAsJsonAsync(
                "/api/topic-kinds",
                new CreateTopicKindDto($"Custom {i}", $"kind-{i}"));
            resp.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var thirteenth = await Client.PostAsJsonAsync(
            "/api/topic-kinds",
            new CreateTopicKindDto("Thirteenth", "kind-1"));
        thirteenth.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Update_edits_name_color_sortOrder_and_swaps_color_atomically()
    {
        // Rename + move to unused color + custom SortOrder
        var update = await Client.PutAsJsonAsync(
            $"/api/topic-kinds/{TopicKindModel.PersonId}",
            new UpdateTopicKindDto(Name: "  Thinker ", ColorKey: "kind-12", SortOrder: 10));
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await update.Content.ReadFromJsonAsync<TopicKindDto>())!;
        updated.Name.Should().Be("Thinker");
        updated.ColorKey.Should().Be("kind-12");
        updated.SortOrder.Should().Be(10);

        // Unknown id -> 404
        (await Client.PutAsJsonAsync($"/api/topic-kinds/{Guid.NewGuid()}", new UpdateTopicKindDto(Name: "Missing")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Invalid input -> 400
        (await Client.PutAsJsonAsync($"/api/topic-kinds/{TopicKindModel.IdeaId}", new UpdateTopicKindDto(Name: "  ")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Client.PutAsJsonAsync($"/api/topic-kinds/{TopicKindModel.IdeaId}", new UpdateTopicKindDto(ColorKey: "blue")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Duplicate folded name -> 409
        (await Client.PutAsJsonAsync($"/api/topic-kinds/{TopicKindModel.IdeaId}", new UpdateTopicKindDto(Name: "THINKER")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Color held by another kind without swapColor -> 409
        (await Client.PutAsJsonAsync($"/api/topic-kinds/{TopicKindModel.IdeaId}", new UpdateTopicKindDto(ColorKey: "kind-12")))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // swapColor: true exchanges the colors cleanly
        var swapped = await Client.PutAsJsonAsync(
            $"/api/topic-kinds/{TopicKindModel.IdeaId}",
            new UpdateTopicKindDto(ColorKey: "kind-12", SwapColor: true));
        swapped.StatusCode.Should().Be(HttpStatusCode.OK);
        (await swapped.Content.ReadFromJsonAsync<TopicKindDto>())!.ColorKey.Should().Be("kind-12");

        var allKinds = (await Client.GetFromJsonAsync<TopicKindDto[]>("/api/topic-kinds"))!;
        allKinds.Single(k => k.Id == TopicKindModel.PersonId).ColorKey.Should().Be("kind-2");
        allKinds.Single(k => k.Id == TopicKindModel.IdeaId).ColorKey.Should().Be("kind-12");
    }

    [Fact]
    public async Task Delete_removes_kind_and_clears_topic_kind_assignments()
    {
        await CreateBookWithNotesAsync("[[Plato]] and [[Athens]]");
        var topics = (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;
        var plato = topics.Single(t => t.Name == "Plato");

        await Client.PutAsJsonAsync(
            "/api/topics/kind",
            new SetTopicsKindDto([plato.Id], TopicKindModel.PersonId));

        var delete = await Client.DeleteAsync($"/api/topic-kinds/{TopicKindModel.PersonId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var kinds = (await Client.GetFromJsonAsync<TopicKindDto[]>("/api/topic-kinds"))!;
        kinds.Should().NotContain(k => k.Id == TopicKindModel.PersonId);

        var afterTopics = (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;
        var reloaded = afterTopics.Single(t => t.Id == plato.Id);
        reloaded.KindId.Should().BeNull();
        reloaded.KindSource.Should().BeNull();

        (await Client.DeleteAsync($"/api/topic-kinds/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Bulk_set_and_clear_topic_kinds_validates_inputs_and_updates_topics()
    {
        await CreateBookWithNotesAsync("[[Alpha]] [[Beta]]");
        var topics = (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;
        var alpha = topics.Single(t => t.Name == "Alpha");
        var beta = topics.Single(t => t.Name == "Beta");

        // Set for multiple topics (including duplicate topicId)
        var set = await Client.PutAsJsonAsync(
            "/api/topics/kind",
            new SetTopicsKindDto([alpha.Id, beta.Id, alpha.Id], TopicKindModel.IdeaId));
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        var setResult = (await set.Content.ReadFromJsonAsync<SetTopicsKindResultDto>())!;
        setResult.UpdatedTopicIds.Should().BeEquivalentTo([alpha.Id, beta.Id]);
        setResult.KindId.Should().Be(TopicKindModel.IdeaId);
        setResult.KindSource.Should().Be(TopicKindSources.Reader);

        // Clear one topic
        var clear = await Client.PutAsJsonAsync(
            "/api/topics/kind",
            new SetTopicsKindDto([alpha.Id], null));
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        var clearResult = (await clear.Content.ReadFromJsonAsync<SetTopicsKindResultDto>())!;
        clearResult.UpdatedTopicIds.Should().Equal([alpha.Id]);
        clearResult.KindId.Should().BeNull();
        clearResult.KindSource.Should().BeNull();

        var after = (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;
        after.Single(t => t.Id == alpha.Id).KindId.Should().BeNull();
        after.Single(t => t.Id == alpha.Id).KindSource.Should().BeNull();
        after.Single(t => t.Id == beta.Id).KindId.Should().Be(TopicKindModel.IdeaId);
        after.Single(t => t.Id == beta.Id).KindSource.Should().Be(TopicKindSources.Reader);

        // Validation / 404 cases
        (await Client.PutAsJsonAsync("/api/topics/kind", new SetTopicsKindDto([], TopicKindModel.IdeaId)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Client.PutAsJsonAsync("/api/topics/kind", new SetTopicsKindDto(Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList(), TopicKindModel.IdeaId)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Client.PutAsJsonAsync("/api/topics/kind", new SetTopicsKindDto([alpha.Id], Guid.NewGuid())))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.PutAsJsonAsync("/api/topics/kind", new SetTopicsKindDto([alpha.Id, Guid.NewGuid()], TopicKindModel.IdeaId)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Merge_and_rename_follow_kind_carry_over_rule()
    {
        await CreateBookWithNotesAsync(
            "[[Socrates]] [[Philosopher Socrates]]",
            "[[Plato]] [[Athenian Plato]]",
            "[[Aristotle]] [[Stagira]]");
        var topics = (await Client.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;

        var socratesTarget = topics.Single(t => t.Name == "Socrates");
        var socratesSource = topics.Single(t => t.Name == "Philosopher Socrates");
        var platoTarget = topics.Single(t => t.Name == "Plato");
        var platoSource = topics.Single(t => t.Name == "Athenian Plato");
        var aristotleTarget = topics.Single(t => t.Name == "Aristotle");
        var stagiraSource = topics.Single(t => t.Name == "Stagira");

        // Assign initial kinds and non-reader sources directly to test source preservation
        await using (var db = await OpenDbAsync())
        {
            var sSrc = await db.Topics.SingleAsync(t => t.Id == socratesSource.Id);
            sSrc.KindId = TopicKindModel.PersonId;
            sSrc.KindSource = TopicKindSources.Assistant;

            var pTgt = await db.Topics.SingleAsync(t => t.Id == platoTarget.Id);
            pTgt.KindId = TopicKindModel.PersonId;
            pTgt.KindSource = TopicKindSources.Reader;

            var pSrc = await db.Topics.SingleAsync(t => t.Id == platoSource.Id);
            pSrc.KindId = TopicKindModel.IdeaId;
            pSrc.KindSource = TopicKindSources.ConnectedApp;

            var aTgt = await db.Topics.SingleAsync(t => t.Id == aristotleTarget.Id);
            aTgt.KindId = TopicKindModel.PersonId;
            aTgt.KindSource = TopicKindSources.ConnectedApp;

            await db.SaveChangesAsync();
        }

        // Case 1: target has no kind, source has Person + assistant -> target takes Person + assistant
        var merge1 = await Client.PostAsJsonAsync(
            $"/api/topics/{socratesSource.Id}/merge",
            new MergeTopicDto(socratesTarget.Id));
        merge1.StatusCode.Should().Be(HttpStatusCode.OK);
        var merged1 = (await merge1.Content.ReadFromJsonAsync<TopicDto>())!;
        merged1.KindId.Should().Be(TopicKindModel.PersonId);
        merged1.KindSource.Should().Be(TopicKindSources.Assistant);

        // Case 2: both have kinds (via rename auto-merge) -> target keeps its own Person + reader
        var renameMerge = await Client.PutAsJsonAsync(
            $"/api/topics/{platoSource.Id}",
            new UpdateTopicDto(platoTarget.Name));
        renameMerge.StatusCode.Should().Be(HttpStatusCode.OK);
        var merged2 = (await renameMerge.Content.ReadFromJsonAsync<TopicDto>())!;
        merged2.Id.Should().Be(platoTarget.Id);
        merged2.KindId.Should().Be(TopicKindModel.PersonId);
        merged2.KindSource.Should().Be(TopicKindSources.Reader);

        // Case 3: target has kind, source has none -> target keeps its own Person + connected-app
        var merge3 = await Client.PostAsJsonAsync(
            $"/api/topics/{stagiraSource.Id}/merge",
            new MergeTopicDto(aristotleTarget.Id));
        merge3.StatusCode.Should().Be(HttpStatusCode.OK);
        var merged3 = (await merge3.Content.ReadFromJsonAsync<TopicDto>())!;
        merged3.KindId.Should().Be(TopicKindModel.PersonId);
        merged3.KindSource.Should().Be(TopicKindSources.ConnectedApp);
    }

    private async Task<BookDto> CreateBookWithNotesAsync(params string[] noteContents)
    {
        var bookResponse = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Topic kind endpoint test {Guid.NewGuid():N}",
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
