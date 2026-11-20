using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// Server-side consistency between an assistant reply's claims of staged/pending
/// review and the real changeset state of the turn (issue #1746).
/// </summary>
public sealed class AssistantReviewClaimConsistencyTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantReviewClaimConsistencyTests(SqliteTestFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------
    // End-to-end orchestrator scenarios
    // ------------------------------------------------------------------

    [Fact]
    public async Task No_op_regroup_reports_no_change_and_replaces_false_staged_for_review_reply()
    {
        using var h = CreateHarness();
        var fiction = await SeedCollectionAsync(h, "Fiction");
        var essays = await SeedCollectionAsync(h, "Essays");

        var fictionBooks = new[]
        {
            await SeedBookAsync(h, "Dune"),
            await SeedBookAsync(h, "Left Hand of Darkness"),
            await SeedBookAsync(h, "Earthsea"),
            await SeedBookAsync(h, "Dracula"),
        };
        var essayBooks = new[]
        {
            await SeedBookAsync(h, "On Boat Repair"),
            await SeedBookAsync(h, "On Attention"),
            await SeedBookAsync(h, "On Solitude"),
            await SeedBookAsync(h, "On Craft"),
        };
        await AddToCollectionAsync(h, fiction, fictionBooks);
        await AddToCollectionAsync(h, essays, essayBooks);

        var updates = fictionBooks
            .Select(b => new { bookId = b.Id, collections = new[] { fiction.Id.ToString() } })
            .Concat(essayBooks.Select(b => new { bookId = b.Id, collections = new[] { essays.Id.ToString() } }))
            .ToArray();
        var setArgs = JsonSerializer.Serialize(new { updates });

        h.Llm
            .CallsTool("library_overview")
            .CallsTool("library_list_collections")
            .CallsTool("library_set_book_collections", setArgs)
            .Returns("I've grouped all eight books into Fiction and Essays, replacing their previous collection memberships. The changes are staged together for review.");

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "Regroup everything into Fiction and Essays.",
            Context(surface: "library", route: "/library")));

        var toolResult = ToolResultFor(h, "library_set_book_collections");
        toolResult.GetProperty("status").GetString().Should().Be("no_change");

        response.Changeset.Should().BeNull();
        response.Error.Should().BeNull();
        response.Reply.Should().Be(AssistantCompletionClaimGuard.NoChangeLibraryMatchesReply);
        response.Reply.Should().NotContainEquivalentOf("staged");
        response.Reply.Should().NotContainEquivalentOf("review");
    }

    [Fact]
    public async Task No_op_regroup_keeps_honest_model_reply_when_it_does_not_claim_a_review()
    {
        using var h = CreateHarness();
        var fiction = await SeedCollectionAsync(h, "Fiction");
        var book = await SeedBookAsync(h, "Dune");
        await AddToCollectionAsync(h, fiction, book);

        var setArgs = JsonSerializer.Serialize(new
        {
            updates = new[] { new { bookId = book.Id, collections = new[] { fiction.Id.ToString() } } },
        });

        h.Llm
            .CallsTool("library_set_book_collections", setArgs)
            .Returns("Nothing needed to change — Dune is already in Fiction.");

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "Put Dune in Fiction only.",
            Context(surface: "library", route: "/library")));

        ToolResultFor(h, "library_set_book_collections")
            .GetProperty("status").GetString().Should().Be("no_change");
        response.Changeset.Should().BeNull();
        response.Error.Should().BeNull();
        response.Reply.Should().Be("Nothing needed to change — Dune is already in Fiction.");
    }

    [Fact]
    public async Task Toolless_staged_for_review_claim_is_replaced_with_unverified_claim_failure()
    {
        using var h = CreateHarness();
        h.Llm.Returns("I've grouped all eight books into Fiction and Essays. The changes are staged together for review.");

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "Regroup everything into Fiction and Essays.",
            Context(surface: "library", route: "/library")));

        response.Changeset.Should().BeNull();
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(AssistantErrorCodes.UnverifiedCompletionClaim);
        response.Reply.Should().Be(AssistantCompletionClaimGuard.UnverifiedReviewReply);
        response.Reply.Should().NotContainEquivalentOf("staged for review");
    }

    [Fact]
    public async Task Refused_staging_followed_by_staged_for_review_claim_is_replaced()
    {
        using var h = CreateHarness();
        var book = await SeedBookAsync(h, "Dune");

        h.Llm
            .CallsTool("library_add_books_to_collection", $$"""{"collection":"Nonexistent","bookIds":["{{book.Id}}"]}""")
            .Returns("I've staged the changes for your review.");

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "Put Dune in Nonexistent.",
            Context(surface: "library", route: "/library")));

        response.Changeset.Should().BeNull();
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(AssistantErrorCodes.UnverifiedCompletionClaim);
        response.Reply.Should().Be(AssistantCompletionClaimGuard.UnverifiedReviewReply);
    }

    [Fact]
    public async Task Applied_changeset_replaces_explicit_false_pending_review_claim()
    {
        using var h = CreateHarness();
        h.Llm
            .CallsTool("library_create_collection", """{"name":"Science fiction"}""")
            .Returns("I created Science fiction. The changes are staged together for review.");

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "Create a Science fiction collection.",
            Context(surface: "library", route: "/library")));

        response.Changeset.Should().NotBeNull();
        response.Changeset!.Status.Should().Be("applied");
        response.Error.Should().BeNull();
        response.Reply.Should().Be(AssistantCompletionClaimGuard.AppliedChangesFallbackReply);
        response.Reply.Should().NotContainEquivalentOf("staged for review");
    }

    [Fact]
    public async Task Real_review_changeset_still_returns_review_card_and_server_review_reply()
    {
        using var h = CreateHarness();
        var fiction = await SeedCollectionAsync(h, "Fiction");
        var book = await SeedBookAsync(h, "Dune");
        await AddToCollectionAsync(h, fiction, book);

        h.Llm
            .CallsTool("library_delete_collection", $$"""{"collection":"{{fiction.Id}}"}""")
            .Returns("The changes are staged for review.");

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "Delete the Fiction collection.",
            Context(surface: "library", route: "/library")));

        response.Changeset.Should().NotBeNull();
        response.Changeset!.Status.Should().Be("review");
        response.Error.Should().BeNull();
        response.Reply.Should().Be(AssistantChangesetCoordinator.ReviewReply);
    }

    [Theory]
    [InlineData("I can also organise collections and update book details. Those changes are staged for review or applied with undo.")]
    [InlineData("Changes to books and collections are staged for review or can be undone.")]
    [InlineData("Library changes are staged for review or applied with undo.")]
    [InlineData("Those changes are staged for you to review or apply, or can be undone when applied.")]
    [InlineData("Nostos applies everyday changes with undo, and shows larger changes for your review before they take effect.")]
    public async Task Capability_disclosure_replies_are_not_mistaken_for_false_review_claims(string disclosureReply)
    {
        using var h = CreateHarness();
        h.Llm.Returns(disclosureReply);

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "What can you do?",
            Context()));

        response.Changeset.Should().BeNull();
        response.Error.Should().BeNull();
        response.Reply.Should().Be(disclosureReply);
    }

    // ------------------------------------------------------------------
    // Guard unit tests
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("The changes are staged together for review.")]
    [InlineData("The changes are staged for review.")]
    [InlineData("I've staged the changes for your review.")]
    [InlineData("I’ve staged the regrouping for you to review.")]
    [InlineData("Changes are staged and ready for review.")]
    [InlineData("Your changes are waiting for review.")]
    [InlineData("The updates are awaiting your review.")]
    [InlineData("These changes are ready to review.")]
    [InlineData("Apply the staged changes from the review card.")]
    [InlineData("You can review and apply the changes below.")]
    [InlineData("I've prepared a review card for the regrouping.")]
    [InlineData("Staged a new Fiction collection.")]
    [InlineData("I've staged the collection updates.")]
    [InlineData("The changes are staged.")]
    public void Guard_flags_staged_or_review_claims_when_no_changeset_exists(string reply)
    {
        var violation = AssistantCompletionClaimGuard.Evaluate(
            reply,
            new HashSet<string>(StringComparer.Ordinal),
            AssistantChangesetOutcome.None,
            draftHadEmptyDiff: true);

        violation.Should().NotBeNull();
        violation!.Kind.Should().Be("review_no_change");
        violation.IsError.Should().BeFalse();
        violation.ReplacementReply.Should().Be(AssistantCompletionClaimGuard.NoChangeLibraryMatchesReply);
    }

    [Theory]
    [InlineData("Nothing was staged for review because the books are already in those collections.")]
    [InlineData("No changes were staged for review.")]
    [InlineData("No changes are waiting for review.")]
    [InlineData("I did not stage any changes because that collection was not found.")]
    [InlineData("Nothing was changed — your library already matches that.")]
    [InlineData("There is no review card because nothing changed.")]
    public void Guard_allows_honest_negated_or_no_change_replies(string reply)
    {
        var violation = AssistantCompletionClaimGuard.Evaluate(
            reply,
            new HashSet<string>(StringComparer.Ordinal),
            AssistantChangesetOutcome.None,
            draftHadEmptyDiff: true);

        violation.Should().BeNull();
    }

    [Fact]
    public void Guard_does_not_replace_bare_staged_wording_on_applied_changesets_without_review_claim()
    {
        var applied = new AssistantChangesetOutcome(
            new AssistantChangesetDto(
                "cs-1",
                "applied",
                [],
                new LibraryChangesetDiffDto([], [], [], new LibraryChangesetCountsDto(1, 0, 0, 0, 0, 0, 0, 0)),
                "hash",
                "tok-1",
                [],
                CanUndo: true),
            null);

        var violation = AssistantCompletionClaimGuard.Evaluate(
            "I’ve staged a new “Sea Stories” collection and added the books.",
            new HashSet<string>(StringComparer.Ordinal),
            applied,
            draftHadEmptyDiff: false);

        violation.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private Harness CreateHarness()
    {
        var dbName = $"review_claim_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"DataSource={dbName};Mode=Memory;Cache=Shared")
            .Options;

        var keepAlive = new NostosDbContext(options);
        keepAlive.Database.OpenConnection();
        keepAlive.Database.EnsureCreated();

        var factory = new TestContextFactory(options);
        var topics = new TopicRepository(keepAlive);
        var noteService = NoteServiceGraph.Create(keepAlive, new FakeThoughtProcessor());
        var libraryService = new LibraryService(
            factory,
            new BookLookupService(new NoopHttpClientFactory(), new SilentLogger<BookLookupService>()));

        var registry = new AssistantCapabilityRegistry(
            AssistantCapabilities.Build(noteService, libraryService, topics));

        var llm = new FakeLlmProvider();
        var assistantOptions = new AssistantOptions { Enabled = true };
        var changesets = new AssistantChangesetStore();
        var coordinator = new AssistantChangesetCoordinator(changesets, libraryService, assistantOptions);
        var continuations = new AssistantContinuationStore();

        var orchestrator = new AssistantOrchestrator(
            registry,
            llm,
            coordinator,
            libraryService,
            continuations,
            libraryService,
            assistantOptions,
            NullLogger<AssistantOrchestrator>.Instance);

        return new Harness(keepAlive, factory, llm, orchestrator);
    }

    private static AssistantTurnRequest Turn(string message, AssistantContextDto context) =>
        new("client-1", $"idem-{Guid.NewGuid():N}", message, context);

    private static AssistantContextDto Context(
        string surface = "second-brain",
        string route = "/second-brain") =>
        new(surface, route, null, null, null, null);

    private static async Task<PhysicalBookModel> SeedBookAsync(Harness h, string title)
    {
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = title, Author = "Author" };
        h.Db.Books.Add(book);
        await h.Db.SaveChangesAsync();
        return book;
    }

    private static async Task<CollectionModel> SeedCollectionAsync(Harness h, string name)
    {
        var collection = new CollectionModel { Id = Guid.NewGuid(), Name = name };
        h.Db.Collections.Add(collection);
        await h.Db.SaveChangesAsync();
        return collection;
    }

    private static async Task AddToCollectionAsync(
        Harness h,
        CollectionModel collection,
        params BookModel[] books)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        foreach (var book in books)
            db.BookCollections.Add(new BookCollectionModel { BookId = book.Id, CollectionId = collection.Id });
        await db.SaveChangesAsync();
    }

    private static JsonElement ToolResultFor(Harness h, string tool)
    {
        foreach (var request in Enumerable.Reverse(h.Llm.Requests))
        {
            var messages = request.Messages.ToList();
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].Role != "tool" || messages[i].ToolCallId is not { } id)
                    continue;
                var call = messages.SelectMany(message => message.ToolCalls ?? []).FirstOrDefault(c => c.Id == id);
                if (call?.Name == tool)
                {
                    using var doc = JsonDocument.Parse(messages[i].Content!);
                    var root = doc.RootElement;
                    return (root.TryGetProperty("data", out var data) ? data : root).Clone();
                }
            }
        }

        throw new InvalidOperationException($"No result for {tool}.");
    }

    private sealed record Harness(
        NostosDbContext Db,
        IDbContextFactory<NostosDbContext> Factory,
        FakeLlmProvider Llm,
        AssistantOrchestrator Orchestrator) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }

    private sealed class TestContextFactory(DbContextOptions<NostosDbContext> options)
        : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);

        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class SilentLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
