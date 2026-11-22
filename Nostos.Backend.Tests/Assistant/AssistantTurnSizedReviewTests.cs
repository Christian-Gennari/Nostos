using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// A classification turn stopped by the token (or step) ceiling still reaches
/// the reader as a review card; the card's reply says what the server's diff did
/// and what a second run would continue (issue #1770 review).
/// </summary>
public sealed class AssistantTurnSizedReviewTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantTurnSizedReviewTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_turn_stopped_at_its_ceiling_says_how_many_it_sorted_and_what_is_left()
    {
        var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire");
        var montessori = await SeedTopicAsync(h, "Montessori");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        await h.Registry.InvokeAsync(
            "topics_create_kind",
            Args("""{"name":"School of thought"}"""),
            new AssistantToolContext("client", "sized", draft, "Give my unkinded topics a kind."));
        await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args($$"""{"topicIds":["{{freire.Id}}","{{montessori.Id}}"],"kind":"School of thought"}"""),
            new AssistantToolContext("client", "sized", draft, "Give my unkinded topics a kind."));

        var finalizer = new AssistantTurnFinalizer(
            new AssistantChangesetCoordinator(new AssistantChangesetStore(), h.Library, new AssistantOptions()),
            NullLogger.Instance);

        var final = await finalizer.CompleteAsync(
            new AssistantTurnFinalInput(
                ConversationKey: "conversation",
                TurnId: "turn-1",
                UserMessage: "Give my unkinded topics a kind.",
                Draft: draft,
                Retrieval: new AssistantRetrievalTurnState(new AssistantContextDto("ask", "/brain")),
                StopReason: AssistantTurnStopReason.SafetyCeiling,
                ReadUntrustedContent: false,
                MutationCompleted: false,
                MutationFailed: false,
                LastRoundOnlySucceededMutations: false,
                Acknowledgement: null,
                CapturedNoteId: null,
                LastAssistantContent: null,
                ReplyCutShort: false,
                AnchorPrompt: null,
                TerminalError: null,
                SuccessfulCapabilities: new HashSet<string>(StringComparer.Ordinal),
                EvidenceCount: 0,
                Execution: null,
                UnkindedTopicCount: 5),
            default);

        final.Changeset.Should().NotBeNull();
        final.Changeset!.Status.Should().Be("review");
        final.Error.Should().BeNull("a review is the turn's ending, not a failure beside it");
        final.Reply.Should().StartWith(
            "I sorted 2 topics before this turn's limit; 3 still have no kind. Apply these, then ask again.");
        final.Reply.Should().Contain(AssistantChangesetCoordinator.ReviewReply);
    }

    [Fact]
    public async Task Without_a_library_count_the_turn_says_what_it_sorted_and_no_more()
    {
        var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        await h.Registry.InvokeAsync(
            "topics_create_kind",
            Args("""{"name":"School of thought"}"""),
            new AssistantToolContext("client", "no-count", draft, "Give my unkinded topics a kind."));
        await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args($$"""{"topicIds":["{{freire.Id}}"],"kind":"School of thought"}"""),
            new AssistantToolContext("client", "no-count", draft, "Give my unkinded topics a kind."));

        var finalizer = new AssistantTurnFinalizer(
            new AssistantChangesetCoordinator(new AssistantChangesetStore(), h.Library, new AssistantOptions()),
            NullLogger.Instance);

        var final = await finalizer.CompleteAsync(
            new AssistantTurnFinalInput(
                ConversationKey: "conversation",
                TurnId: "turn-2",
                UserMessage: "Give my unkinded topics a kind.",
                Draft: draft,
                Retrieval: new AssistantRetrievalTurnState(new AssistantContextDto("ask", "/brain")),
                StopReason: AssistantTurnStopReason.SafetyCeiling,
                ReadUntrustedContent: false,
                MutationCompleted: false,
                MutationFailed: false,
                LastRoundOnlySucceededMutations: false,
                Acknowledgement: null,
                CapturedNoteId: null,
                LastAssistantContent: null,
                ReplyCutShort: false,
                AnchorPrompt: null,
                TerminalError: null,
                SuccessfulCapabilities: new HashSet<string>(StringComparer.Ordinal),
                EvidenceCount: 0,
                Execution: null),
            default);

        final.Reply.Should().StartWith(
            "I sorted 1 topic before this turn's limit. Apply these, then ask again.");
    }

    [Fact]
    public async Task One_topic_still_unkinded_reads_in_the_singular()
    {
        var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire");
        var montessori = await SeedTopicAsync(h, "Montessori");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        await h.Registry.InvokeAsync(
            "topics_create_kind",
            Args("""{"name":"School of thought"}"""),
            new AssistantToolContext("client", "sized-one", draft, "Give my unkinded topics a kind."));
        await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args($$"""{"topicIds":["{{freire.Id}}","{{montessori.Id}}"],"kind":"School of thought"}"""),
            new AssistantToolContext("client", "sized-one", draft, "Give my unkinded topics a kind."));

        var final = await FinishAsync(h, draft, "conversation-one", "turn-one", unkindedTopicCount: 3);

        final.Reply.Should().StartWith(
            "I sorted 2 topics before this turn's limit; 1 still has no kind. Apply these, then ask again.");
    }

    [Fact]
    public async Task Nothing_left_to_sort_does_not_ask_again()
    {
        var h = CreateHarness();
        var freire = await SeedTopicAsync(h, "Freire");
        var montessori = await SeedTopicAsync(h, "Montessori");
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        await h.Registry.InvokeAsync(
            "topics_create_kind",
            Args("""{"name":"School of thought"}"""),
            new AssistantToolContext("client", "sized-none", draft, "Give my unkinded topics a kind."));
        await h.Registry.InvokeAsync(
            "topics_set_kind",
            Args($$"""{"topicIds":["{{freire.Id}}","{{montessori.Id}}"],"kind":"School of thought"}"""),
            new AssistantToolContext("client", "sized-none", draft, "Give my unkinded topics a kind."));

        var final = await FinishAsync(h, draft, "conversation-none", "turn-none", unkindedTopicCount: 2);

        final.Reply.Should().StartWith("I sorted 2 topics before this turn's limit. Apply these.");
        final.Reply.Should().NotContain("ask again");
    }

    private static async Task<AssistantTurnFinal> FinishAsync(
        Harness h,
        AssistantChangesetDraft draft,
        string conversation,
        string turn,
        int? unkindedTopicCount)
    {
        var finalizer = new AssistantTurnFinalizer(
            new AssistantChangesetCoordinator(new AssistantChangesetStore(), h.Library, new AssistantOptions()),
            NullLogger.Instance);

        return await finalizer.CompleteAsync(
            new AssistantTurnFinalInput(
                ConversationKey: conversation,
                TurnId: turn,
                UserMessage: "Give my unkinded topics a kind.",
                Draft: draft,
                Retrieval: new AssistantRetrievalTurnState(new AssistantContextDto("ask", "/brain")),
                StopReason: AssistantTurnStopReason.SafetyCeiling,
                ReadUntrustedContent: false,
                MutationCompleted: false,
                MutationFailed: false,
                LastRoundOnlySucceededMutations: false,
                Acknowledgement: null,
                CapturedNoteId: null,
                LastAssistantContent: null,
                ReplyCutShort: false,
                AnchorPrompt: null,
                TerminalError: null,
                SuccessfulCapabilities: new HashSet<string>(StringComparer.Ordinal),
                EvidenceCount: 0,
                Execution: null,
                UnkindedTopicCount: unkindedTopicCount),
            default);
    }

    private Harness CreateHarness()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_fixture.CreateDatabasePath()}")
            .Options;
        using (var bootstrap = new NostosDbContext(options))
            bootstrap.Database.EnsureCreated();

        var factory = new TestContextFactory(options);
        var db = new NostosDbContext(options);
        var topics = new TopicRepository(db);
        var library = LibraryServiceGraph.Create(
            factory,
            new BookLookupService(new NoopHttpClientFactory(), NullLogger<BookLookupService>.Instance));
        var registry = new AssistantCapabilityRegistry(AssistantCapabilities.Build(
            notes: null!,
            library,
            topics));

        return new Harness(db, topics, library, registry);
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<TopicModel> SeedTopicAsync(Harness h, string name)
    {
        var topic = new TopicModel { Topic = name };
        h.Db.Topics.Add(topic);
        await h.Db.SaveChangesAsync();
        return topic;
    }

    private sealed record Harness(
        NostosDbContext Db,
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
