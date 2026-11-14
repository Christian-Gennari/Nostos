using System.Text.Json;
using System.Text.RegularExpressions;
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
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// Characterization tests for one Ask Nostos turn (issue #1598).
///
/// <see cref="AssistantOrchestrator"/> is split into focused collaborators and
/// its capability payloads become typed. These tests pin what a turn actually
/// produces before that split, so the split can be judged against them:
///
/// <list type="bullet">
/// <item>the final <see cref="AssistantTurnResponse"/> JSON the frontend and
/// Cloud read (the review body, the continuation and the typed error included);</item>
/// <item>the tool-result JSON the model reads;</item>
/// <item>the <see cref="LlmProviderUsage"/> a host settles for the turn;</item>
/// <item>the order of the streamed activity events.</item>
/// </list>
///
/// They were written against the pre-split orchestrator and must stay green
/// without edits afterwards. Random identities (GUIDs, changeset ids, action
/// tokens, content hashes) are normalized to placeholders so the snapshots are
/// about shape and value, not about a run's identifiers.
/// </summary>
public sealed class AssistantOrchestratorCharacterizationTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantOrchestratorCharacterizationTests(SqliteTestFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------
    // A plain turn
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_plain_turn_keeps_its_response_json_and_its_settled_usage()
    {
        var h = CreateHarness();
        h.Llm.Returns("Nothing in your notes on homecoming yet.");

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "What do I have on homecoming?",
            Context()));

        Snapshot(response).Should().Be(
            """
            {"reply":"Nothing in your notes on homecoming yet.","acknowledgement":null,"anchorPrompt":null,"suggestions":[],"changeset":null,"capturedNoteId":null,"executedCapabilities":[],"sources":[],"error":null,"evidence":[],"resolvedBook":null}
            """);

        h.Llm.CallCount.Should().Be(1);
        h.Usage.Completed.Should().ContainSingle().Which.Should().Be(
            new LlmProviderUsage(
                UpstreamRequestCount: 1,
                InputTokens: null,
                OutputTokens: null,
                ThinkingTokens: null,
                ReportedTotalTokens: null,
                ToolLoopIterations: 0,
                ResultCategory: "Completed",
                ProviderFinishReason: "stop"));
    }

    // ------------------------------------------------------------------
    // A tool-using turn
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_tool_using_turn_keeps_the_tool_result_json_and_the_response_json()
    {
        var h = CreateHarness();
        await SeedTopicAsync(h, "Homecoming");
        await SeedTopicAsync(h, "Memory");

        h.Llm
            .CallsTool("topics_list")
            .Returns("You have two topics: Homecoming and Memory.");

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "Which topics do I have?",
            Context()));

        Snapshot(ToolResultFor(h, "topics_list")).Should().Be(
            """
            [{"id":"{id}","name":"Homecoming","usageCount":0,"noteMatchCount":0,"noteMatchSnippet":null,"description":null},{"id":"{id}","name":"Memory","usageCount":0,"noteMatchCount":0,"noteMatchSnippet":null,"description":null}]
            """);

        Snapshot(response).Should().Be(
            """
            {"reply":"You have two topics: Homecoming and Memory.","acknowledgement":null,"anchorPrompt":null,"suggestions":[],"changeset":null,"capturedNoteId":null,"executedCapabilities":[],"sources":[],"error":null,"evidence":[],"resolvedBook":null}
            """);

        h.Llm.CallCount.Should().Be(2);
        h.Usage.Completed.Should().ContainSingle().Which.Should().Be(
            new LlmProviderUsage(2, null, null, null, null, 1, "Completed", "stop"));
    }

    // ------------------------------------------------------------------
    // Budget exhaustion across iterations
    // ------------------------------------------------------------------

    [Fact]
    public async Task Tool_iteration_budget_exhaustion_keeps_its_reply_json_and_usage()
    {
        var h = CreateHarness(maxToolIterations: 2);
        await SeedTopicAsync(h, "Homecoming");

        // Two different read tools: an identical repeat in the next round is a
        // repeated-tool loop, which is a different stop reason.
        h.Llm
            .CallsTool("topics_list")
            .CallsTool("library_list_books");

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "List every topic, then list every book.",
            Context()));

        response.Reply.Should().Be(AssistantOrchestrator.IncompleteTurnReply);
        Snapshot(response).Should().Be(
            """
            {"reply":"I reached this turn\u0027s execution limit before I could finish. Send another message to continue.","acknowledgement":null,"anchorPrompt":null,"suggestions":[],"changeset":null,"capturedNoteId":null,"executedCapabilities":[],"sources":[],"error":{"code":"assistant_execution_budget_exhausted","message":"This turn reached its execution limit before it could finish."},"evidence":[],"resolvedBook":null}
            """);

        h.Llm.CallCount.Should().Be(2, "the iteration budget is the ceiling it stopped at");
        h.Usage.Completed.Should().ContainSingle().Which.Should().Be(
            new LlmProviderUsage(2, null, null, null, null, 2, "SafetyCeiling", "tool_calls"));
    }

    // ------------------------------------------------------------------
    // Streamed turn event order
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_streamed_turn_keeps_its_activity_order()
    {
        // The streamed transport writes "started", then one "activity" event per
        // callback in exactly this order, then the terminal event. The endpoint
        // is a pass-through, so the order is the orchestrator's to keep.
        var h = CreateHarness();
        var book = await SeedBookAsync(h, "The Magic Mountain");
        await SeedTopicAsync(h, "Homecoming");

        h.Llm
            .CallsTool("library_get_book", $$"""{"bookId":"{{book.Id}}"}""")
            .CallsTool("topics_list")
            .CallsTool("notes_capture", """{"content":"A thought kept while reading"}""")
            .Returns("Saved, and here are your topics.");

        var activities = new List<string>();
        var response = await h.Orchestrator.HandleTurnAsync(
            Turn(
                "Remember this thought while I read.",
                Context(
                    surface: "reader",
                    route: $"/read/{book.Id}",
                    bookId: book.Id.ToString(),
                    bookTitle: book.Title,
                    bookFormat: "ebook",
                    readerType: "epub")),
            activity =>
            {
                activities.Add($"{activity.Code}: {activity.Message}");
                return ValueTask.CompletedTask;
            });

        var streamOrder = new List<string> { AssistantTurnEventKinds.Started };
        streamOrder.AddRange(activities);

        streamOrder.Should().Equal(
            AssistantTurnEventKinds.Started,
            "checking_library: Checking your library…",
            "searching_notes: Searching your notes…",
            "saving_note: Saving your note…");

        response.Error.Should().BeNull("the terminal event is 'completed'");
        response.Acknowledgement.Should().Contain(book.Title);
        Snapshot(response).Should().Be(
            """
            {"reply":"Saved, and here are your topics.","acknowledgement":"Saved to The Magic Mountain. You can undo this from your notes.","anchorPrompt":null,"suggestions":[],"changeset":null,"capturedNoteId":"{id}","executedCapabilities":[],"sources":[],"error":null,"evidence":[],"resolvedBook":{"bookId":"{id}","bookTitle":"The Magic Mountain"}}
            """);
    }

    // ------------------------------------------------------------------
    // A write that needs review
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_write_needing_review_keeps_its_changeset_json()
    {
        var h = CreateHarness();
        var fiction = await SeedCollectionAsync(h, "Fiction");
        var book = await SeedBookAsync(h, "Dune");
        await AddToCollectionAsync(h, fiction, book);

        h.Llm
            .CallsTool("library_delete_collection", $$"""{"collection":"{{fiction.Id}}"}""")
            .Returns("I prepared that for you.");

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "Delete my Fiction collection.",
            Context(surface: "library", route: "/library")));

        var changeset = response.Changeset!;
        changeset.Status.Should().Be("review");
        changeset.DiffHash.Should().NotBeNullOrWhiteSpace();

        Snapshot(response).Should().Be(
            """
            {"reply":"Here\u0027s what I\u0027d change. Nothing changes until you apply it.","acknowledgement":null,"anchorPrompt":null,"suggestions":[],"changeset":{"changesetId":"{id}","status":"review","steps":[{"index":0,"summary":"Delete collection \u201CFiction\u201D","included":true,"dependsOn":[]}],"diff":{"collections":[{"id":"{id}","kind":"deleted","name":"Fiction","previousName":null,"parentName":null,"previousParentName":null,"parentChanged":false,"bookCount":1}],"memberships":[{"bookId":"{id}","title":"Dune","author":"Author","added":[],"removed":["Fiction"]}],"bookEdits":[],"counts":{"collectionsCreated":0,"collectionsDeleted":1,"collectionsUpdated":0,"booksTouched":1,"membershipsAdded":0,"membershipsRemoved":1,"booksLosingMembership":1,"bookEdits":0},"isEmpty":false},"diffHash":"{hash}","actionToken":"{hash}","reviewReasons":["Deletes \u201CFiction\u201D, which holds 1 book."],"canUndo":false},"capturedNoteId":null,"executedCapabilities":[],"sources":[],"error":null,"evidence":[],"resolvedBook":null}
            """);

        h.Usage.Completed.Should().ContainSingle().Which.ResultCategory.Should().Be("Completed");
    }

    // ------------------------------------------------------------------
    // A continuation
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_capture_continuation_keeps_both_turns_json()
    {
        var h = CreateHarness();
        var paper = await SeedBookAsync(h, "Only On Paper");

        h.Llm
            .CallsTool("notes_capture", """{"content":"The ending is sad."}""")
            .Returns("What page are you on?");

        var asked = await h.Orchestrator.HandleTurnAsync(Turn(
            "The ending is sad.",
            Context(bookId: paper.Id.ToString(), bookTitle: paper.Title, bookFormat: "physical"),
            conversationId: "conversation-continuation",
            turnId: "turn-1"));

        asked.AnchorPrompt.Should().NotBeNull();
        Snapshot(asked).Should().Be(
            """
            {"reply":"What page are you on?","acknowledgement":null,"anchorPrompt":{"kind":"physical_page","question":"What page are you on?","continuationId":"{id}"},"suggestions":[],"changeset":null,"capturedNoteId":null,"executedCapabilities":[],"sources":[],"error":null,"evidence":[],"resolvedBook":null}
            """);

        h.Llm.Returns("Saved on page 42.");

        var resumed = await h.Orchestrator.HandleTurnAsync(Turn(
            "42",
            Context(bookId: paper.Id.ToString(), bookTitle: paper.Title, bookFormat: "physical"),
            conversationId: "conversation-continuation",
            turnId: "turn-2",
            continuationId: asked.AnchorPrompt!.ContinuationId));

        resumed.AnchorPrompt.Should().BeNull();
        Snapshot(resumed).Should().Be(
            """
            {"reply":"","acknowledgement":"Saved to Only On Paper. You can undo this from your notes.","anchorPrompt":null,"suggestions":[],"changeset":null,"capturedNoteId":"{id}","executedCapabilities":[],"sources":[],"error":null,"evidence":null,"resolvedBook":null}
            """);

        await using var db = await h.Factory.CreateDbContextAsync();
        var note = await db.Notes.AsNoTracking().SingleAsync();
        note.SourceAnchorValue.Should().Be("42");
        note.Content.Should().Be("The ending is sad.");
        h.Llm.CallCount.Should().Be(1, "the continuation is deterministic, not another model turn");
    }

    // ------------------------------------------------------------------
    // A provider failure
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_provider_failure_keeps_its_typed_exception_and_settles_the_turn()
    {
        var h = CreateHarness();
        h.Llm.Failure = LlmException.RateLimited();

        var act = () => h.Orchestrator.HandleTurnAsync(Turn("What do I have?", Context()));

        var failure = (await act.Should().ThrowAsync<LlmException>()).Which;
        failure.Code.Should().Be(LlmErrorCodes.RateLimited);
        failure.Message.Should().Be("Ask Nostos is temporarily busy. Try again shortly.");

        h.Llm.CallCount.Should().Be(1);
        h.Usage.Completed.Should().ContainSingle().Which.Should().Be(
            new LlmProviderUsage(1, null, null, null, null, 0, "ProviderError", null));
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private Harness CreateHarness(int maxToolIterations = 6)
    {
        var path = _fixture.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        using (var bootstrap = new NostosDbContext(options))
        {
            bootstrap.Database.EnsureCreated();
        }

        var factory = new TestContextFactory(options);
        var db = new NostosDbContext(options);

        var topics = new TopicRepository(db);
        var noteService = NoteServiceGraph.Create(db, new FakeThoughtProcessor());

        var libraryService = new LibraryService(
            factory,
            new BookLookupService(new NoopHttpClientFactory(), new SilentLogger<BookLookupService>()));

        var registry = new AssistantCapabilityRegistry(
            AssistantCapabilities.Build(
                noteService,
                libraryService,
                topics,
                NoOpKnowledgeRetrievalService.Instance,
                bookText: null,
                new BookTextQuoteFinder(new SqliteBookTextIndex(factory), libraryService)));

        var llm = new FakeLlmProvider();
        var assistantOptions = new AssistantOptions
        {
            Enabled = true,
            MaxToolIterations = maxToolIterations,
        };
        var changesets = new AssistantChangesetStore();
        var coordinator = new AssistantChangesetCoordinator(changesets, libraryService, assistantOptions);
        var continuations = new AssistantContinuationStore();
        var usage = new RecordingUsageAccounting();

        var orchestrator = new AssistantOrchestrator(
            registry,
            llm,
            coordinator,
            libraryService,
            continuations,
            libraryService,
            assistantOptions,
            NullLogger<AssistantOrchestrator>.Instance,
            usageAccounting: usage);

        return new Harness(db, factory, llm, usage, orchestrator);
    }

    private static AssistantTurnRequest Turn(
        string message,
        AssistantContextDto context,
        string clientId = "client-1",
        string idem = "key-1",
        string? conversationId = null,
        string? turnId = null,
        string? continuationId = null) =>
        new(
            clientId,
            idem,
            message,
            context,
            ConversationId: conversationId,
            TurnId: turnId,
            ContinuationId: continuationId);

    private static AssistantContextDto Context(
        string surface = "second-brain",
        string route = "/second-brain",
        string? bookId = null,
        string? bookTitle = null,
        string? bookFormat = null,
        string? readerType = null) =>
        new(
            surface,
            route,
            bookId,
            bookTitle,
            bookFormat,
            readerType);

    private static async Task<PhysicalBookModel> SeedBookAsync(
        Harness h,
        string title = "Seeded Book",
        string author = "Author")
    {
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = title, Author = author };
        h.Db.Books.Add(book);
        await h.Db.SaveChangesAsync();
        return book;
    }

    private static async Task<TopicModel> SeedTopicAsync(Harness h, string name)
    {
        var topic = new TopicModel { Id = Guid.NewGuid(), Topic = name };
        h.Db.Topics.Add(topic);
        await h.Db.SaveChangesAsync();
        return topic;
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

    /// <summary>The last result the model was shown for <paramref name="tool"/>, its data half.</summary>
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

    private static string Snapshot(object value) =>
        Normalize(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static string Snapshot(JsonElement value) =>
        Normalize(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    /// <summary>
    /// Replaces a run's identities with placeholders: GUIDs (dashed or "N"),
    /// changeset ids (32 hex), action tokens (48 hex) and content hashes (64 hex).
    /// </summary>
    private static string Normalize(string json) => GuidPattern.Replace(
        HexIdPattern.Replace(
            HashPattern.Replace(json, "{hash}"),
            "{id}"),
        "{id}");

    private static readonly Regex GuidPattern = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Content hashes (64 hex) and action tokens (48 hex).</summary>
    private static readonly Regex HashPattern = new(
        "\\b[0-9a-fA-F]{48}\\b|\\b[0-9a-fA-F]{64}\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>"N"-format GUIDs (32 hex), used by changeset and continuation ids.</summary>
    private static readonly Regex HexIdPattern = new(
        "\\b[0-9a-fA-F]{32}\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed record Harness(
        NostosDbContext Db,
        IDbContextFactory<NostosDbContext> Factory,
        FakeLlmProvider Llm,
        RecordingUsageAccounting Usage,
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

    /// <summary>Records what a host would settle for the turn, as Cloud does.</summary>
    private sealed class RecordingUsageAccounting : IAiUsageAccountingService
    {
        public List<LlmProviderUsage> Completed { get; } = [];

        public Task<AiUsageLease?> BeginLlmTurnAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AiUsageLease?>(new AiUsageLease(Guid.NewGuid(), AiUsageKind.Llm));

        public Task CompleteLlmTurnAsync(
            AiUsageLease? lease,
            LlmProviderUsage usage,
            CancellationToken cancellationToken = default)
        {
            Completed.Add(usage);
            return Task.CompletedTask;
        }

        public Task<AiUsageLease?> BeginSttAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AiUsageLease?>(null);

        public Task CompleteSttAsync(
            AiUsageLease? lease,
            TranscriptionUsage usage,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
