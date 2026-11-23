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

    /// <summary>The composer's prefilled sort request, word for word.</summary>
    private const string SortRequest =
        "Give a kind to my topics that have none. Use the kinds I have; propose a new kind only if many topics need it.";

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
        data.GetProperty("nextPage").GetInt32().Should().Be(2);

        // Classifying only needs a name: an untyped page projects {id, name},
        // which measured 71 bytes against 172 for the whole row.
        var compact = data.GetProperty("items")[0].EnumerateObject().Select(property => property.Name).ToList();
        compact.Should().BeEquivalentTo("id", "name");

        var all = await h.Registry.InvokeAsync(
            "topics_list",
            Args("""{"pageSize":2}"""),
            new AssistantToolContext());
        all.Data!.Value.GetProperty("total").GetInt32().Should().Be(3);
        all.Data!.Value.GetProperty("totalPages").GetInt32().Should().Be(2);
        all.Data!.Value.GetProperty("nextPage").GetInt32().Should().Be(2);
        all.Data!.Value.GetProperty("items").EnumerateArray()
            .Select(topic => topic.GetProperty("kindSource").GetString())
            .Should().Contain("reader");

        var last = await h.Registry.InvokeAsync(
            "topics_list",
            Args("""{"pageSize":2,"page":2}"""),
            new AssistantToolContext());
        last.Data!.Value.GetProperty("nextPage").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Topics_list_accepts_a_page_of_250_but_no_larger()
    {
        using var h = CreateHarness();
        await SeedTopicAsync(h, "Freire");

        var largest = await h.Registry.InvokeAsync(
            "topics_list",
            Args("""{"pageSize":250}"""),
            new AssistantToolContext());
        largest.Success.Should().BeTrue(largest.ErrorMessage);

        var refused = await h.Registry.InvokeAsync(
            "topics_list",
            Args("""{"pageSize":251}"""),
            new AssistantToolContext());
        refused.Success.Should().BeFalse();
        refused.ErrorMessage.Should().Contain("250");
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
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { freire.Id },
                kind = "Idea",
                namedTopicIds = new[] { freire.Id },
            })),
            new AssistantToolContext("client", "name-reader", draft, "Freire is an Idea, not a Person."));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        draft.Diff!.TopicKindAssignments.Should().ContainSingle();
    }

    [Fact]
    public async Task An_untyped_page_spends_about_70_bytes_a_topic()
    {
        using var h = CreateHarness();
        for (var index = 1; index <= 20; index += 1)
            await SeedTopicAsync(h, $"Topic number {index:00}");

        var result = await h.Registry.InvokeAsync(
            "topics_list",
            Args("""{"untyped":true}"""),
            new AssistantToolContext());

        result.Success.Should().BeTrue(result.ErrorMessage);
        var items = result.Data!.Value.GetProperty("items");
        items.GetArrayLength().Should().Be(20);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(items.GetRawText());
        // The full row measured 172 bytes on the 149-topic seed; the compact
        // projection must stay well under 100 so a classification page fits in
        // the turn's budget.
        (bytes / 20.0).Should().BeLessThan(100, "each item is only {id, name}");
    }

    [Fact]
    public async Task A_staged_result_reports_the_count_and_latest_step_not_every_summary()
    {
        using var h = CreateHarness();
        var ids = new List<Guid>();
        for (var index = 1; index <= 3; index += 1)
            ids.Add((await SeedTopicAsync(h, $"Topic {index}")).Id);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new { topicIds = ids, kind = "Idea" })),
            new AssistantToolContext("client", "shape", draft));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        var data = staged.Data!.Value;
        data.GetProperty("status").GetString().Should().Be("staged");
        data.GetProperty("stepCount").GetInt32().Should().Be(3);
        data.GetProperty("latestStep").GetString().Should().Contain("Topic 3");
        // A classification stages hundreds of steps across a few calls; echoing
        // every summary after each call grew the prompt quadratically.
        data.TryGetProperty("steps", out _).Should().BeFalse();
        data.GetRawText().Should().NotContain("Topic 1");
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
    public async Task The_prefilled_sort_request_keeps_the_reader_kind_of_every_topic_it_does_not_name()
    {
        using var h = CreateHarness();
        // Four names the prefilled request contains inside ordinary words
        // ("many", "topics", "kinds", "Use"), each with a kind the reader set.
        var man = await SeedTopicAsync(h, "Man", TopicKindModel.PersonId, TopicKindSources.Reader);
        var topic = await SeedTopicAsync(h, "Topic", TopicKindModel.IdeaId, TopicKindSources.Reader);
        var kind = await SeedTopicAsync(h, "Kind", TopicKindModel.PlaceId, TopicKindSources.Reader);
        var us = await SeedTopicAsync(h, "US", TopicKindModel.WorkId, TopicKindSources.Reader);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { man.Id, topic.Id, kind.Id, us.Id },
                kind = "Event",
            })),
            new AssistantToolContext(
                "client",
                "prefilled",
                draft,
                SortRequest));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        staged.Data!.Value.GetProperty("kept").GetInt32().Should().Be(4);
        staged.Data!.Value.GetProperty("note").GetString().Should().Contain("Kept 4 kinds you set yourself");
        draft.IsEmpty.Should().BeTrue("the prefilled request names none of these topics");
    }

    [Fact]
    public async Task Clearing_a_kind_on_a_reader_topic_the_request_names_reaches_the_diff()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire", TopicKindModel.PersonId, TopicKindSources.Reader);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { freire.Id },
                clear = true,
                namedTopicIds = new[] { freire.Id },
            })),
            new AssistantToolContext("client", "clear-named", draft, "Freire has no kind, actually."));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        draft.Diff.Should().NotBeNull("a clear on a named topic is a change, not a protected no-op");
        var assignment = draft.Diff!.TopicKindAssignments.Should().ContainSingle().Which;
        assignment.TopicName.Should().Be("Freire");
        assignment.KindId.Should().BeNull();
        assignment.PreviousKindName.Should().Be("Person");

        var applied = await h.Library.ApplyAsync(draft.Steps.Select(step => step.Op).ToList(), draft.DiffHash);
        applied.Success.Should().BeTrue(applied.ErrorMessage);

        await using var db = await h.Factory.CreateDbContextAsync();
        var topic = await db.Topics.AsNoTracking().SingleAsync(t => t.Id == freire.Id);
        topic.KindId.Should().BeNull();
        topic.KindSource.Should().BeNull();
    }

    [Fact]
    public async Task The_prefilled_sort_request_keeps_reader_kinds_of_topics_called_Need_None_and_Give()
    {
        using var h = CreateHarness();
        // These are ordinary words in the prefilled request, not names. The
        // model omits namedTopicIds because the reader named no topic, and an
        // omitted list must protect every reader kind rather than fall back to
        // matching the request's prose against a stoplist.
        var need = await SeedTopicAsync(h, "Need", TopicKindModel.IdeaId, TopicKindSources.Reader);
        var none = await SeedTopicAsync(h, "None", TopicKindModel.PlaceId, TopicKindSources.Reader);
        var give = await SeedTopicAsync(h, "Give", TopicKindModel.WorkId, TopicKindSources.Reader);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { need.Id, none.Id, give.Id },
                kind = "Event",
            })),
            new AssistantToolContext("client", "prefilled-scaffolding", draft, SortRequest));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        staged.Data!.Value.GetProperty("kept").GetInt32().Should().Be(3);
        draft.IsEmpty.Should().BeTrue("the prefilled request names none of these topics");
    }

    [Fact]
    public async Task A_named_topic_with_two_letters_is_verified_when_the_model_passes_its_id()
    {
        using var h = CreateHarness();
        var ai = await SeedTopicAsync(h, "AI", TopicKindModel.IdeaId, TopicKindSources.Reader);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { ai.Id },
                kind = "Person",
                namedTopicIds = new[] { ai.Id },
            })),
            new AssistantToolContext("client", "short-name", draft, "AI should be a Person."));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        draft.Diff!.TopicKindAssignments.Should().ContainSingle()
            .Which.TopicName.Should().Be("AI");
    }

    [Fact]
    public async Task A_model_passed_id_whose_name_is_not_in_the_request_is_refused()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire", TopicKindModel.PersonId, TopicKindSources.Reader);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { freire.Id },
                kind = "Idea",
                namedTopicIds = new[] { freire.Id },
            })),
            new AssistantToolContext("client", "not-in-text", draft, "Give the unkinded topics a kind."));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        staged.Data!.Value.GetProperty("kept").GetInt32().Should().Be(1);
        draft.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task An_omitted_named_list_does_not_fall_back_to_matching_the_prose()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire", TopicKindModel.PersonId, TopicKindSources.Reader);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        // The text contains "Freire", but the model did not say the reader
        // named it. An omitted list means no topic was named.
        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new { topicIds = new[] { freire.Id }, kind = "Idea" })),
            new AssistantToolContext("client", "prose-only", draft, "Freire is an Idea."));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        staged.Data!.Value.GetProperty("kept").GetInt32().Should().Be(1);
        draft.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task A_topic_the_model_lists_as_named_but_the_request_does_not_name_still_keeps_its_reader_kind()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire", TopicKindModel.PersonId, TopicKindSources.Reader);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var staged = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { freire.Id },
                kind = "Idea",
                namedTopicIds = new[] { freire.Id },
            })),
            new AssistantToolContext("client", "not-named", draft, "Give the unkinded topics a kind."));

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        staged.Data!.Value.GetProperty("kept").GetInt32().Should().Be(1);
        staged.Data!.Value.GetProperty("note").GetString().Should().Contain("Kept 1 kind you set yourself");
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

    [Fact]
    public async Task A_collection_name_swap_still_stages_one_kind_step_per_topic()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire");
        var montessori = await SeedTopicAsync(h, "Montessori");
        var alpha = new CollectionModel { Name = "Alpha" };
        var beta = new CollectionModel { Name = "Beta" };
        h.Db.Collections.AddRange(alpha, beta);
        await h.Db.SaveChangesAsync();

        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        var staged = await draft.StageAsync(
        [
            // A genuine name swap takes the non-canonical collection path...
            (new RenameCollectionOp(LibraryCollectionRef.Existing(alpha.Id), "Temp"), (IReadOnlyList<int>)[]),
            (new RenameCollectionOp(LibraryCollectionRef.Existing(beta.Id), "Alpha"), (IReadOnlyList<int>)[]),
            (new RenameCollectionOp(LibraryCollectionRef.Existing(alpha.Id), "Beta"), (IReadOnlyList<int>)[]),
            // ...but the kind operations must stay canonical: one per topic, so
            // the card can tick each topic and canApply matches the server.
            (new SetTopicKindOp(
                [freire.Id, montessori.Id],
                TopicKindRef.Existing(TopicKindModel.IdeaId),
                NamedTopicIds: []), (IReadOnlyList<int>)[]),
        ], default);

        staged.Success.Should().BeTrue(staged.ErrorMessage);
        var kindSteps = draft.Steps.Select(step => step.Op).OfType<SetTopicKindOp>().ToList();
        kindSteps.Should().HaveCount(2);
        kindSteps.Should().OnlyContain(step => step.TopicIds.Count == 1);
    }

    [Fact]
    public async Task Refusing_a_set_for_a_reader_kind_reports_replan_counts_and_dropped_created_kind()
    {
        using var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire", TopicKindModel.PersonId, TopicKindSources.Reader);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var collection = await h.Registry.InvokeAsync(
            "library_create_collection",
            Args("""{"name":"Reading"}"""),
            new AssistantToolContext("client", "replan-reader", draft, "Give my unkinded topics a kind."));
        collection.Success.Should().BeTrue(collection.ErrorMessage);

        var created = await h.Registry.InvokeAsync(
            "topics_create_kind",
            Args("""{"name":"Movement"}"""),
            new AssistantToolContext("client", "replan-reader", draft, "Give my unkinded topics a kind."));
        created.Success.Should().BeTrue(created.ErrorMessage);
        created.Data!.Value.GetProperty("changesetSoFar").GetProperty("topicKindsCreated").GetInt32().Should().Be(1);

        var set = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { freire.Id },
                kind = "Movement",
            })),
            new AssistantToolContext("client", "replan-reader", draft, "Give my unkinded topics a kind."));

        set.Success.Should().BeTrue(set.ErrorMessage);
        var data = set.Data!.Value;
        data.GetProperty("status").GetString().Should().Be("kept_reader_kinds");
        data.GetProperty("kept").GetInt32().Should().Be(1);
        data.GetProperty("note").GetString().Should().Contain("Kept 1 kind you set yourself.")
            .And.Contain("“Movement” will not be created: no topic uses it.");
        var counts = data.GetProperty("changesetSoFar");
        counts.GetProperty("collectionsCreated").GetInt32().Should().Be(1);
        counts.GetProperty("topicKindsCreated").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Abandoning_a_created_kind_reports_updated_counts_and_tells_the_model()
    {
        using var h = CreateHarness();
        var dewey = await SeedTopicAsync(h, "Dewey");
        var freire = await SeedTopicAsync(h, "Freire", TopicKindModel.PersonId, TopicKindSources.Reader);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        (await h.Registry.InvokeAsync(
            "topics_create_kind",
            Args("""{"name":"Movement"}"""),
            new AssistantToolContext("client", "abandon", draft, "Give my unkinded topics a kind.")))
            .Success.Should().BeTrue();
        (await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new { topicIds = new[] { dewey.Id }, kind = "Movement" })),
            new AssistantToolContext("client", "abandon", draft, "Give my unkinded topics a kind.")))
            .Success.Should().BeTrue();

        // Moving its only topic to another kind (while a reader kind is kept in
        // the same call) abandons "Movement": counts drop to 0 created kinds
        // and the result says so.
        var reassigned = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new
            {
                topicIds = new[] { dewey.Id, freire.Id },
                kind = "Idea",
            })),
            new AssistantToolContext("client", "abandon", draft, "Give my unkinded topics a kind."));

        reassigned.Success.Should().BeTrue(reassigned.ErrorMessage);
        var data = reassigned.Data!.Value;
        data.GetProperty("keptReaderKinds").GetInt32().Should().Be(1);
        data.GetProperty("note").GetString().Should().Contain("Kept 1 kind you set yourself.")
            .And.Contain("“Movement” will not be created: no topic uses it.");
        var counts = data.GetProperty("changesetSoFar");
        counts.GetProperty("topicKindsCreated").GetInt32().Should().Be(0);
        counts.GetProperty("topicKindChanges").GetInt32().Should().Be(1);

        // Using the abandoned kind later in the turn brings it back cleanly.
        var montessori = await SeedTopicAsync(h, "Montessori");
        var reused = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new { topicIds = new[] { montessori.Id }, kind = "Movement" })),
            new AssistantToolContext("client", "abandon", draft, "Give my unkinded topics a kind."));
        reused.Success.Should().BeTrue(reused.ErrorMessage);
        reused.Data!.Value.GetProperty("changesetSoFar").GetProperty("topicKindsCreated").GetInt32().Should().Be(1);
        reused.Data!.Value.GetProperty("note").GetString().Should().NotContain("will not be created");

        // Clearing its only topic abandons it again (down to no_change).
        var cleared = await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args(JsonSerializer.Serialize(new { topicIds = new[] { montessori.Id }, clear = true })),
            new AssistantToolContext("client", "abandon", draft, "Give my unkinded topics a kind."));
        cleared.Success.Should().BeTrue(cleared.ErrorMessage);
        cleared.Data!.Value.GetProperty("changesetSoFar").GetProperty("topicKindsCreated").GetInt32().Should().Be(0);
        cleared.Data!.Value.GetProperty("note").GetString().Should().Contain("“Movement” will not be created: no topic uses it.");
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
