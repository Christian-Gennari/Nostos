using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// The host cost budget seam (<see cref="AiUsageLease.LlmBudget"/>, Nostos-Cloud
/// issue 654): a host whose LLM reservation is a fixed amount limits every
/// provider call of a turn, and a reply cut by a limit says so.
/// </summary>
public sealed class AssistantCallBudgetTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantCallBudgetTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Without_a_host_budget_every_call_keeps_the_product_output_limit()
    {
        using var h = CreateHarness(budget: null);
        h.Llm.CallsTool("library_list_collections", "{}").Returns("You have no collections.");

        var response = await h.Orchestrator.HandleTurnAsync(Turn("Which collections do I have?"));

        response.Reply.Should().Be("You have no collections.");
        h.Llm.Requests.Should().HaveCount(2);
        h.Llm.Requests.Should().OnlyContain(r => r.MaxTokens == AssistantOrchestrator.MaxResponseTokens);
    }

    [Fact]
    public async Task Each_call_is_sent_with_the_limit_the_host_budget_allows_and_reports_back()
    {
        var budget = new ScriptedBudget(10_000, 900);
        using var h = CreateHarness(budget);
        h.Llm.Enqueue(
            new LlmCompletion(null, "tool_calls",
                [new LlmToolCall("call-1", "library_list_collections", "{}")], 1_000, 40),
            new LlmCompletion("You have no collections.", "stop", [], 1_100, 12));

        var response = await h.Orchestrator.HandleTurnAsync(Turn("Which collections do I have?"));

        response.Reply.Should().Be("You have no collections.");
        // A budget above the product limit never raises it; a lower one is sent.
        h.Llm.Requests.Select(r => r.MaxTokens).Should().Equal(
            AssistantOrchestrator.MaxResponseTokens,
            900);
        budget.Asked.Should().Be(2);
        budget.Recorded.Select(c => c.PromptTokens).Should().Equal(1_000, 1_100);
        h.Usage.Completed.Should().ContainSingle().Which.Should().Be(
            new LlmProviderUsage(2, 2_100, 52, null, 2_152, 1, "Completed", "stop"));
    }

    [Fact]
    public async Task A_budget_that_cannot_pay_for_the_next_call_ends_the_turn_at_its_execution_limit()
    {
        var budget = new ScriptedBudget(2_000, AssistantOrchestrator.MinBudgetedResponseTokens - 1);
        using var h = CreateHarness(budget);
        h.Llm.Enqueue(
            new LlmCompletion("Let me look.", "tool_calls",
                [new LlmToolCall("call-1", "library_list_collections", "{}")], 1_000, 40),
            new LlmCompletion("never sent", "stop", []));

        var response = await h.Orchestrator.HandleTurnAsync(Turn("Which collections do I have?"));

        h.Llm.CallCount.Should().Be(1, "the call the budget cannot pay for is never sent");
        response.Reply.Should().Be(AssistantOrchestrator.IncompleteTurnReply);
        response.Error!.Code.Should().Be(AssistantErrorCodes.ExecutionBudgetExhausted);
        h.Usage.Completed.Should().ContainSingle().Which.Should().Be(
            new LlmProviderUsage(1, 1_000, 40, null, 1_040, 1, "SafetyCeiling", "tool_calls"));
    }

    [Fact]
    public async Task A_budget_that_refuses_the_first_call_spends_nothing_and_settles_an_empty_turn()
    {
        var budget = new ScriptedBudget(0);
        using var h = CreateHarness(budget);

        var response = await h.Orchestrator.HandleTurnAsync(Turn("Summarise my library."));

        h.Llm.CallCount.Should().Be(0);
        response.Reply.Should().Be(AssistantOrchestrator.IncompleteTurnReply);
        response.Error!.Code.Should().Be(AssistantErrorCodes.ExecutionBudgetExhausted);
        h.Usage.Completed.Should().ContainSingle().Which.Should().Be(
            new LlmProviderUsage(0, 0, 0, 0, 0, 0, "SafetyCeiling", null));
    }

    [Fact]
    public async Task A_reply_the_provider_ended_at_its_limit_tells_the_person_it_was_cut_short()
    {
        var budget = new ScriptedBudget(300);
        using var h = CreateHarness(budget);
        h.Llm.Enqueue(new LlmCompletion("The first part of a long answer", "length", [], 900, 300));

        var response = await h.Orchestrator.HandleTurnAsync(Turn("Explain everything."));

        h.Llm.LastRequest.MaxTokens.Should().Be(300);
        response.Reply.Should().Be(
            "The first part of a long answer\n\n" + AssistantOrchestrator.CutShortNotice);
        response.Error.Should().BeNull("a cut reply is still the assistant's answer, not a failure");
    }

    [Fact]
    public async Task A_reply_cut_at_the_product_limit_is_marked_too_without_a_host_budget()
    {
        using var h = CreateHarness(budget: null);
        h.Llm.Enqueue(new LlmCompletion("A long answer that stops", "length", []));

        var response = await h.Orchestrator.HandleTurnAsync(Turn("Explain everything."));

        response.Reply.Should().EndWith(AssistantOrchestrator.CutShortNotice);
    }

    [Fact]
    public async Task A_complete_reply_is_never_marked_as_cut_short()
    {
        using var h = CreateHarness(new ScriptedBudget(300));
        h.Llm.Returns("A short answer.");

        var response = await h.Orchestrator.HandleTurnAsync(Turn("Explain briefly."));

        response.Reply.Should().Be("A short answer.");
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private Harness CreateHarness(ScriptedBudget? budget)
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
        var noteService = NoteServiceGraph.Create(db, new FakeThoughtProcessor());
        var libraryService = new LibraryService(
            factory,
            new BookLookupService(new NoopHttpClientFactory(), NullLogger<BookLookupService>.Instance));
        var registry = new AssistantCapabilityRegistry(
            AssistantCapabilities.Build(
                noteService,
                libraryService,
                new TopicRepository(db),
                NoOpKnowledgeRetrievalService.Instance,
                bookText: null,
                new BookTextQuoteFinder(new SqliteBookTextIndex(factory), libraryService)));

        var llm = new FakeLlmProvider();
        var assistantOptions = new AssistantOptions { Enabled = true, MaxToolIterations = 6 };
        var usage = new BudgetedUsageAccounting(budget);
        var orchestrator = new AssistantOrchestrator(
            registry,
            llm,
            new AssistantChangesetCoordinator(new AssistantChangesetStore(), libraryService, assistantOptions),
            libraryService,
            new AssistantContinuationStore(),
            libraryService,
            assistantOptions,
            NullLogger<AssistantOrchestrator>.Instance,
            usageAccounting: usage);

        return new Harness(db, llm, usage, orchestrator);
    }

    private static AssistantTurnRequest Turn(string message) =>
        new("client-1", "key-1", message, new AssistantContextDto("second-brain", "/second-brain", null, null, null, null));

    private sealed record Harness(
        NostosDbContext Db,
        FakeLlmProvider Llm,
        BudgetedUsageAccounting Usage,
        AssistantOrchestrator Orchestrator) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }

    /// <summary>Answers each call with the next scripted limit, repeating the last one.</summary>
    private sealed class ScriptedBudget(params int[] limits) : ILlmCallBudget
    {
        public int Asked { get; private set; }

        public List<LlmCompletion> Recorded { get; } = [];

        public int MaxOutputTokens(LlmCompletionRequest request)
        {
            var limit = limits[Math.Min(Asked, limits.Length - 1)];
            Asked++;
            return limit;
        }

        public void Record(LlmCompletion completion) => Recorded.Add(completion);
    }

    /// <summary>A host that attaches a budget to each LLM lease, as Cloud does.</summary>
    private sealed class BudgetedUsageAccounting(ILlmCallBudget? budget) : IAiUsageAccountingService
    {
        public List<LlmProviderUsage> Completed { get; } = [];

        public Task<AiUsageLease?> BeginLlmTurnAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AiUsageLease?>(
                new AiUsageLease(Guid.NewGuid(), AiUsageKind.Llm) { LlmBudget = budget });

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
}
