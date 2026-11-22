using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Integrations.Mcp;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Mcp;

/// <summary>
/// Topic kinds over MCP (issue #1771): reading them, classifying topics in a
/// batch, and the rule that a connected app never overwrites what the reader
/// set by hand.
/// </summary>
public sealed class McpTopicKindToolsTests : IAsyncLifetime
{
    private readonly McpEndpointFactory _factory = new();
    private HttpClient _http = null!;
    private McpClient _mcp = null!;

    public async Task InitializeAsync()
    {
        _http = _factory.CreateClient();
        (await _http.GetAsync("/health/live")).EnsureSuccessStatusCode();
        _mcp = await ConnectAsync(McpEndpointFactory.Token);
    }

    public async Task DisposeAsync()
    {
        await _mcp.DisposeAsync();
        _http.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Write_tools_refuse_a_read_only_connection_with_the_read_only_message()
    {
        var topics = await SeedTopicsAsync("Alpha");
        await using var readOnly = await ConnectAsync(McpEndpointFactory.ReadOnlyToken);

        var set = await readOnly.CallToolAsync("set_topic_kinds", new Dictionary<string, object?>
        {
            ["assignments"] = new[] { new { topicId = topics["Alpha"], kindId = TopicKindModel.PersonId } },
        });
        var create = await readOnly.CallToolAsync("create_topic_kind", new Dictionary<string, object?>
        {
            ["name"] = "Mood",
        });
        var kinds = await readOnly.CallToolAsync("list_topic_kinds", new Dictionary<string, object?>());

        set.IsError.Should().BeTrue();
        Text(set).Should().Be(McpScopes.ReadOnlyRefusal);
        create.IsError.Should().BeTrue();
        Text(create).Should().Be(McpScopes.ReadOnlyRefusal);
        kinds.IsError.Should().NotBe(true, Text(kinds));
    }

    [Fact]
    public async Task A_batch_sets_and_clears_kinds_and_list_topics_can_show_only_untyped_topics()
    {
        var topics = await SeedTopicsAsync("Alpha", "Beta", "Gamma");

        // Give Beta a kind first so the batch can clear a real one.
        await CallAsync("set_topic_kinds", new()
        {
            ["assignments"] = new[] { new { topicId = topics["Beta"], kindId = TopicKindModel.IdeaId } },
        });

        var result = await CallAsync("set_topic_kinds", new()
        {
            ["assignments"] = new object[]
            {
                new { topicId = topics["Alpha"], kindId = TopicKindModel.PersonId },
                new { topicId = topics["Beta"], kindId = (Guid?)null },
            },
        });

        result.GetProperty("set").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(topics["Alpha"]);
        result.GetProperty("cleared").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(topics["Beta"]);
        result.GetProperty("skippedReaderKinds").EnumerateArray().Should().BeEmpty();
        result.GetProperty("unknownTopicIds").EnumerateArray().Should().BeEmpty();

        var all = await CallAsync("list_topics", new());
        var alpha = all.GetProperty("topics").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Alpha");
        alpha.GetProperty("kind").GetProperty("name").GetString().Should().Be("Person");
        alpha.GetProperty("kindSource").GetString().Should().Be(TopicKindSources.ConnectedApp);
        var beta = all.GetProperty("topics").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Beta");
        beta.TryGetProperty("kind", out _).Should().BeFalse();
        beta.TryGetProperty("kindSource", out _).Should().BeFalse();

        var untyped = await CallAsync("list_topics", new() { ["withoutKind"] = true });
        untyped.GetProperty("topics").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().BeEquivalentTo("Beta", "Gamma");

        var detail = await CallAsync("get_topic", new() { ["topicId"] = $"topic:{topics["Alpha"]:D}" });
        detail.GetProperty("kind").GetProperty("name").GetString().Should().Be("Person");
        detail.GetProperty("kindSource").GetString().Should().Be(TopicKindSources.ConnectedApp);

        // The same batch again changes nothing.
        var again = await CallAsync("set_topic_kinds", new()
        {
            ["assignments"] = new object[]
            {
                new { topicId = topics["Alpha"], kindId = TopicKindModel.PersonId },
                new { topicId = topics["Beta"], kindId = (Guid?)null },
            },
        });
        again.GetProperty("set").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(topics["Alpha"]);
        again.GetProperty("cleared").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(topics["Beta"]);
    }

    [Fact]
    public async Task A_kind_the_reader_set_is_skipped_unless_the_app_asks_to_overwrite_it()
    {
        var topics = await SeedTopicsAsync("Alpha");
        var alpha = topics["Alpha"];
        await AssignAsync([alpha], TopicKindModel.PersonId);

        var skipped = await CallAsync("set_topic_kinds", new()
        {
            ["assignments"] = new[] { new { topicId = alpha, kindId = TopicKindModel.IdeaId } },
        });

        skipped.GetProperty("set").EnumerateArray().Should().BeEmpty();
        skipped.GetProperty("skippedReaderKinds").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(alpha);
        var unchanged = await TopicAsync(alpha);
        unchanged.KindId.Should().Be(TopicKindModel.PersonId);
        unchanged.KindSource.Should().Be(TopicKindSources.Reader);

        var overwritten = await CallAsync("set_topic_kinds", new()
        {
            ["assignments"] = new[] { new { topicId = alpha, kindId = TopicKindModel.IdeaId } },
            ["overwriteReaderKinds"] = true,
        });

        overwritten.GetProperty("set").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(alpha);
        overwritten.GetProperty("skippedReaderKinds").EnumerateArray().Should().BeEmpty();
        var changed = await TopicAsync(alpha);
        changed.KindId.Should().Be(TopicKindModel.IdeaId);
        changed.KindSource.Should().Be(TopicKindSources.ConnectedApp);
    }

    [Fact]
    public async Task Unknown_topic_ids_are_reported_and_the_known_ones_still_get_their_kind()
    {
        var topics = await SeedTopicsAsync("Alpha");
        var missing = Guid.NewGuid();

        var result = await CallAsync("set_topic_kinds", new()
        {
            ["assignments"] = new object[]
            {
                new { topicId = topics["Alpha"], kindId = TopicKindModel.WorkId },
                new { topicId = missing, kindId = TopicKindModel.WorkId },
            },
        });

        result.GetProperty("set").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(topics["Alpha"]);
        result.GetProperty("unknownTopicIds").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(missing);
        (await TopicAsync(topics["Alpha"])).KindId.Should().Be(TopicKindModel.WorkId);
    }

    [Fact]
    public async Task More_than_100_assignments_is_refused_before_anything_changes()
    {
        var topics = await SeedTopicsAsync("Alpha");
        var assignments = Enumerable.Range(0, 101)
            .Select(_ => new { topicId = Guid.NewGuid(), kindId = (Guid?)TopicKindModel.IdeaId })
            .ToArray();

        var refused = await ErrorAsync("set_topic_kinds", new() { ["assignments"] = assignments });

        refused.Should().Contain("At most 100");
        (await TopicAsync(topics["Alpha"])).KindId.Should().BeNull();
    }

    [Fact]
    public async Task List_topic_kinds_reports_colour_counts_and_free_slots()
    {
        var topics = await SeedTopicsAsync("Alpha", "Beta");
        await AssignAsync([topics["Alpha"], topics["Beta"]], TopicKindModel.PersonId);

        var listed = await CallAsync("list_topic_kinds", new());

        listed.GetProperty("max").GetInt32().Should().Be(TopicKindModel.MaxKinds);
        listed.GetProperty("freeSlots").GetInt32().Should().Be(TopicKindModel.MaxKinds - 6);
        listed.GetProperty("url").GetString().Should().EndWith("/brain/kinds");
        var kinds = listed.GetProperty("kinds").EnumerateArray().ToList();
        kinds.Should().HaveCount(6);
        var person = kinds.Single(k => k.GetProperty("id").GetGuid() == TopicKindModel.PersonId);
        person.GetProperty("name").GetString().Should().Be("Person");
        person.GetProperty("colorKey").GetString().Should().Be("kind-1");
        person.GetProperty("topicCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Create_topic_kind_takes_the_next_free_colour_and_is_refused_at_12_with_the_existing_kinds()
    {
        await SeedTopicsAsync("Alpha");

        var created = await CallAsync("create_topic_kind", new() { ["name"] = "Mood" });

        created.GetProperty("name").GetString().Should().Be("Mood");
        created.GetProperty("colorKey").GetString().Should().Be("kind-7");
        created.GetProperty("url").GetString().Should().EndWith("/brain/kinds");
        created.GetProperty("id").GetGuid().Should().NotBeEmpty();
        (await CallAsync("list_topic_kinds", new())).GetProperty("freeSlots").GetInt32().Should().Be(5);

        for (var i = 8; i <= 12; i++)
        {
            var response = await _http.PostAsJsonAsync("/api/topic-kinds", new CreateTopicKindDto($"Kind {i}", $"kind-{i}"));
            response.EnsureSuccessStatusCode();
        }

        var refused = await ErrorAsync("create_topic_kind", new() { ["name"] = "Another" });

        refused.Should().Contain("12").And.Contain("Person").And.Contain("Mood");
    }

    [Fact]
    public async Task Overview_counts_topic_kinds_and_topics_without_one()
    {
        var topics = await SeedTopicsAsync("Alpha", "Beta");
        await AssignAsync([topics["Alpha"]], TopicKindModel.PlaceId);

        var overview = await CallAsync("overview", new());

        var brain = overview.GetProperty("brain");
        brain.GetProperty("topicKinds").GetInt32().Should().Be(6);
        brain.GetProperty("topicsWithoutKind").GetInt32().Should().Be(1);
    }

    // ------------------------------------------------------------------

    private async Task<McpClient> ConnectAsync(string token)
    {
        var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(authed.BaseAddress!, NostosMcp.Path),
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                authed,
                ownsHttpClient: true),
            new McpClientOptions
            {
                InitializationTimeout = TimeSpan.FromMinutes(3),
                DiscoverProbeTimeout = TimeSpan.FromMinutes(1),
            });
    }

    private async Task<Dictionary<string, Guid>> SeedTopicsAsync(params string[] names)
    {
        var response = await _http.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = "Kinds " + Guid.NewGuid().ToString("N"),
            author = "Test Author",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var bookId = body.GetProperty("id").GetGuid();

        var note = await _http.PostAsJsonAsync($"/api/books/{bookId}/notes", new CreateNoteDto(
            Content: "A note about kinds.",
            Topics: names));
        note.EnsureSuccessStatusCode();

        var topics = (await _http.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;
        return names.ToDictionary(name => name, name => topics.Single(t => t.Name == name).Id);
    }

    private async Task AssignAsync(IReadOnlyList<Guid> topicIds, Guid kindId)
    {
        var response = await _http.PutAsJsonAsync(
            "/api/topics/kind",
            new SetTopicsKindDto(topicIds, kindId));
        response.EnsureSuccessStatusCode();
    }

    private async Task<TopicDto> TopicAsync(Guid id)
    {
        var topics = (await _http.GetFromJsonAsync<TopicDto[]>("/api/topics"))!;
        return topics.Single(t => t.Id == id);
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _mcp.CallToolAsync(tool, args);
        result.IsError.Should().NotBe(true, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    private async Task<string> ErrorAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _mcp.CallToolAsync(tool, args);
        result.IsError.Should().BeTrue(Text(result));
        return Text(result);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
