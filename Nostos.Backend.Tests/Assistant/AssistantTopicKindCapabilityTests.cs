using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// Reading and setting topic kinds through Ask Nostos (issue #1770): the read
/// capabilities, staging through the changeset, the 12-kind and duplicate-name
/// refusals, and the rule that a kind the reader set is only replaced when the
/// user named that topic.
/// </summary>
public sealed class AssistantTopicKindCapabilityTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantTopicKindCapabilityTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Topics_list_kinds_reports_every_kind_with_its_count()
    {
        using var h = CreateHarness();
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.Topics.Add(new TopicModel { Topic = "Freire", KindId = TopicKindModel.PersonId, KindSource = TopicKindSources.Reader });
            await db.SaveChangesAsync();
        }

        var result = await h.Registry.InvokeAsync("topics_list_kinds", Args("{}"), new AssistantToolContext());

        result.Success.Should().BeTrue(result.ErrorMessage);
        var kinds = result.Data!.Value.EnumerateArray().ToList();
        kinds.Should().HaveCount(6);
        kinds.Single(kind => kind.GetProperty("name").GetString() == "Person")
            .GetProperty("topicCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Topics_list_pages_and_filters_topics_with_no_kind()
    {
        using var h = CreateHarness();
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            db.Topics.AddRange(
                new TopicModel { Topic = "Freire", KindId = TopicKindModel.PersonId, KindSource = TopicKindSources.Reader },
                new TopicModel { Topic = "Montessori" },
                new TopicModel { Topic = "Dewey" });
            await db.SaveChangesAsync();
        }

        var untyped = await h.Registry.InvokeAsync(
            "topics_list",
            Args("""{"untyped":true,"pageSize":1}"""),
            new AssistantToolContext());

        untyped.Success.Should().BeTrue(untyped.ErrorMessage);
        var data = untyped.Data!.Value;
        data.GetProperty("total").GetInt32().Should().Be(2);
        data.GetProperty("unkindedTotal").GetInt32().Should().Be(2);
        data.GetProperty("items").GetArrayLength().Should().Be(1);

        var all = await h.Registry.InvokeAsync(
            "topics_list",
            Args("""{"pageSize":2}"""),
            new AssistantToolContext());
        all.Data!.Value.GetProperty("total").GetInt32().Should().Be(3);
        all.Data!.Value.GetProperty("totalPages").GetInt32().Should().Be(2);
        all.Data!.Value.GetProperty("items").EnumerateArray()
            .Select(topic => topic.GetProperty("kindSource").GetString())
            .Should().Contain("reader");
    }

    [Fact]
    public async Task Topics_get_includes_the_kind_and_who_set_it()
    {
        using var h = CreateHarness();
        TopicModel topic;
        await using (var db = await h.Factory.CreateDbContextAsync())
        {
            topic = new TopicModel
            {
                Topic = "Freire",
                KindId = TopicKindModel.PersonId,
                KindSource = TopicKindSources.Reader,
            };
            db.Topics.Add(topic);
            await db.SaveChangesAsync();
        }

        var result = await h.Registry.InvokeAsync(
            "topics_get",
            Args($$"""{"topicId":"{{topic.Id}}"}"""),
            new AssistantToolContext());

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Data!.Value.GetProperty("kindId").GetGuid().Should().Be(TopicKindModel.PersonId);
        result.Data!.Value.GetProperty("kindName").GetString().Should().Be("Person");
        result.Data!.Value.GetProperty("kindSource").GetString().Should().Be(TopicKindSources.Reader);
    }

    [Fact]
    public async Task Topics_create_kind_stages_a_new_kind_with_the_first_free_colour()
    {
        using var h = CreateHarness();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_create_kind",
            Args("""{"name":"School of thought"}"""),
            new AssistantToolContext("client", "create-kind", draft));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        draft.Diff!.TopicKinds.Should().ContainSingle()
            .Which.ColorKey.Should().Be("kind-7");
        draft.Steps.Should().ContainSingle()
            .Which.Summary.Should().Be("Create the topic kind “School of thought”");
    }

    [Fact]
    public async Task Topics_create_kind_is_refused_at_the_twelfth_kind_and_for_a_duplicate_name()
    {
        using (var full = CreateHarness(extraKinds: 6))
        {
            var draft = new AssistantChangesetDraft(full.Library, full.Library);
            var refused = await full.Registry.InvokeAsync(
                "topics_create_kind",
                Args("""{"name":"Extra kind"}"""),
                new AssistantToolContext("client", "full", draft));
            refused.Success.Should().BeFalse();
            refused.ErrorCode.Should().Be(LibraryChangesetErrorCodes.TopicKindLimit);
        }

        using var h = CreateHarness(extraKinds: 5);
        var duplicateDraft = new AssistantChangesetDraft(h.Library, h.Library);
        var duplicate = await h.Registry.InvokeAsync(
            "topics_create_kind",
            Args("""{"name":"Person"}"""),
            new AssistantToolContext("client", "duplicate", duplicateDraft));
        duplicate.Success.Should().BeFalse();
        duplicate.ErrorCode.Should().Be(LibraryChangesetErrorCodes.TopicKindDuplicate);
    }

    [Fact]
    public async Task Topics_set_kind_stages_and_applies_one_kind_for_many_topics()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire");
        var montessori = await SeedTopicAsync(h, "Montessori");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { freire.Id, montessori.Id },
                kind = "Idea",
            })),
            new AssistantToolContext("client", "set-kind", draft));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        draft.Diff!.Counts.TopicKindChanges.Should().Be(2);

        var applied = await h.Library.ApplyAsync(draft.Steps.Select(step => step.Op).ToList(), draft.DiffHash);
        applied.Success.Should().BeTrue(applied.ErrorMessage);

        await using var db = await h.Factory.CreateDbContextAsync();
        var topics = await db.Topics.AsNoTracking().Where(t => t.Id == freire.Id || t.Id == montessori.Id).ToListAsync();
        topics.Should().OnlyContain(topic =>
            topic.KindId == TopicKindModel.IdeaId && topic.KindSource == TopicKindSources.Assistant);
    }

    [Fact]
    public async Task A_kind_the_reader_set_is_skipped_and_the_model_is_told_how_many_were_kept()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire", TopicKindModel.PersonId, TopicKindSources.Reader);
        var montessori = await SeedTopicAsync(h, "Montessori");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { freire.Id, montessori.Id },
                kind = "Idea",
            })),
            new AssistantToolContext("client", "skip-reader", draft, "Give the topics that have no kind a kind."));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        staged.Data!.Value.GetProperty("keptReaderKinds").GetInt32().Should().Be(1);
        staged.Data!.Value.GetProperty("note").GetString().Should().Contain("Kept 1 kind you set yourself");
        draft.Diff!.TopicKindAssignments.Should().ContainSingle()
            .Which.TopicName.Should().Be("Montessori");
    }

    [Fact]
    public async Task Naming_the_topic_lets_the_reader_kind_be_replaced()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire", TopicKindModel.PersonId, TopicKindSources.Reader);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new { topicIds = new[] { freire.Id }, kind = "Idea" })),
            new AssistantToolContext("client", "name-reader", draft, "Freire is an Idea, not a Person."));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        draft.Diff!.TopicKindAssignments.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unknown_topic_is_refused_when_staging()
    {
        using var h = CreateHarness();
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new { topicIds = new[] { Guid.NewGuid() }, kind = "Idea" })),
            new AssistantToolContext("client", "unknown", draft));

        staged.Success.Should().BeFalse();
        staged.ErrorCode.Should().Be(LibraryChangesetErrorCodes.TopicNotFound);
        draft.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Clearing_a_kind_stages_a_removal()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire", TopicKindModel.PersonId, TopicKindSources.Assistant);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new { topicIds = new[] { freire.Id }, clear = true })),
            new AssistantToolContext("client", "clear", draft));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        draft.Diff!.TopicKindAssignments.Should().ContainSingle()
            .Which.KindId.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private Harness CreateHarness(int extraKinds = 0)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_fixture.CreateDatabasePath()}")
            .Options;
        using (var bootstrap = new NostosDbContext(options))
            bootstrap.Database.EnsureCreated();

        var factory = new TestContextFactory(options);
        var db = new NostosDbContext(options);
        for (var index = 0; index < extraKinds; index++)
        {
            db.TopicKinds.Add(new TopicKindModel
            {
                Name = $"Extra {index}",
                ColorKey = TopicKindModel.PaletteColorKeys[6 + index],
                SortOrder = 7 + index,
            });
        }
        db.SaveChanges();

        var topics = new TopicRepository(db);
        var library = LibraryServiceGraph.Create(
            factory,
            new BookLookupService(new NoopHttpClientFactory(), NullLogger<BookLookupService>.Instance));
        var registry = new AssistantCapabilityRegistry(AssistantCapabilities.Build(
            notes: null!,
            library,
            topics));

        return new Harness(db, factory, topics, library, registry);
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<TopicModel> SeedTopicAsync(
        Harness h,
        string name,
        Guid? kindId = null,
        string? source = null)
    {
        var topic = new TopicModel { Topic = name, KindId = kindId, KindSource = source };
        h.Db.Topics.Add(topic);
        await h.Db.SaveChangesAsync();
        return topic;
    }

    private sealed record Harness(
        NostosDbContext Db,
        IDbContextFactory<NostosDbContext> Factory,
        TopicRepository Topics,
        LibraryService Library,
        AssistantCapabilityRegistry Registry) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }

    private sealed class TestContextFactory(DbContextOptions<NostosDbContext> options) : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);
        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
