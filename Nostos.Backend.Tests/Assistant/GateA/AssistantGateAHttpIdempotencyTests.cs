using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant.GateA;

/// <summary>
/// Gate A cases 1–4 and 15 at the real HTTP boundary (issue #566).
///
/// A lost HTTP response is retried by the client with the SAME logical
/// TurnId; the server must replay execution truth instead of writing again.
/// These tests drive the full host (<see cref="LibraryEndpointFactory"/>)
/// with the LLM replaced by <see cref="FakeLlmProvider"/>: the singleton
/// plan and continuation stores survive across requests exactly as they do
/// in production, so a retry that arrives as a brand-new HTTP request still
/// hits the same canonical receipts.
/// </summary>
public sealed class AssistantGateAHttpIdempotencyTests : IDisposable
{
    private const string TokenVariable = "NOSTOS_GATE_A_HTTP_TEST_TOKEN";
    private const string TokenValue = "gate-a-sentinel-key-value";

    public void Dispose() =>
        Environment.SetEnvironmentVariable(TokenVariable, null);

    [Fact]
    public async Task Lost_response_retry_reuses_the_same_TurnId_for_capture()
    {
        var provider = new FakeLlmProvider()
            .CallsTool("notes_capture", """{"content":"Gate A thought"}""")
            .Returns("Saved.")
            .CallsTool("notes_capture", """{"content":"Gate A thought"}""")
            .Returns("Saved.");

        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider);
        using var client = host.CreateClient();
        var book = await CreateBookAsync(client);

        using var _ = KeepToken();

        // The first attempt succeeds; its HTTP response is "lost" on the way
        // back, so the client retries the exact logical turn.
        var first = await PostTurnAsync(client, Turn(
            "Remember this thought.",
            book,
            conversationId: "gate-a-conversation-1",
            turnId: "gate-a-turn-capture",
            idem: "delivery-a"));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = (await first.Content.ReadFromJsonAsync<AssistantTurnResponse>())!;
        firstBody.CapturedNoteId.Should().NotBeNullOrWhiteSpace();

        var retry = await PostTurnAsync(client, Turn(
            "Remember this thought.",
            book,
            conversationId: "gate-a-conversation-1",
            turnId: "gate-a-turn-capture",
            idem: "delivery-b"));

        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var retryBody = (await retry.Content.ReadFromJsonAsync<AssistantTurnResponse>())!;

        // Gate A case 1 (same TurnId identity) and case 2 (no duplicate
        // capture): the retry replays the canonical receipt.
        retryBody.CapturedNoteId.Should().Be(firstBody.CapturedNoteId);
        (await NoteCountAsync(factory)).Should().Be(1);
    }

    [Fact]
    public async Task Lost_response_retry_reuses_the_same_TurnId_for_ordinary_act()
    {
        var provider = new FakeLlmProvider()
            .CallsTool("library_create_collection", """{"name":"Gate A shelf"}""")
            .Returns("Created.")
            .CallsTool("library_create_collection", """{"name":"Gate A shelf"}""")
            .Returns("Created.");

        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider);
        using var client = host.CreateClient();

        using var _ = KeepToken();

        AssistantTurnRequest Act(string delivery) => new(
            "gate-a-act-client",
            delivery,
            "Create a Gate A shelf collection.",
            new AssistantContextDto("library", "/library"),
            ConversationId: "gate-a-conversation-3",
            TurnId: "gate-a-turn-act");

        var first = await client.PostAsJsonAsync(AssistantEndpoints.TurnRoute, Act("delivery-a"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var retry = await client.PostAsJsonAsync(AssistantEndpoints.TurnRoute, Act("delivery-b"));
        retry.StatusCode.Should().Be(HttpStatusCode.OK);

        // Gate A case 3: one logical Act turn is one mutation, however many
        // deliveries arrive.
        var retryBody = (await retry.Content.ReadFromJsonAsync<AssistantTurnResponse>())!;
        retryBody.ExecutedCapabilities.Should().Contain("library_create_collection");
        (await CollectionCountAsync(factory, "Gate A shelf")).Should().Be(1);
    }

    [Fact]
    public async Task Lost_approval_response_replays_the_destructive_result_without_double_execution()
    {
        var target = Guid.NewGuid();
        var provider = new FakeLlmProvider()
            .CallsTool(
                "library_delete_collection",
                JsonSerializer.Serialize(new { collectionId = target }));

        using var factory = new LibraryEndpointFactory();
        await SeedCollectionAsync(factory, target, "Gate A doomed");
        using var host = CreateHost(factory, provider);
        using var client = host.CreateClient();

        using var _ = KeepToken();

        var turn = await client.PostAsJsonAsync(
            AssistantEndpoints.TurnRoute,
            new AssistantTurnRequest(
                "gate-a-approval-client",
                "delivery-a",
                "Delete the Gate A collection.",
                new AssistantContextDto("library", "/library"),
                ConversationId: "gate-a-conversation-4",
                TurnId: "gate-a-turn-plan"));

        turn.StatusCode.Should().Be(HttpStatusCode.OK);
        var plan = (await turn.Content.ReadFromJsonAsync<AssistantTurnResponse>())!.PendingPlan;
        plan.Should().NotBeNull();

        var approved = await client.PostAsJsonAsync(
            AssistantEndpoints.ApproveRoute,
            new AssistantPlanApproveRequest(plan!.PlanId, plan.ApprovalToken));
        approved.StatusCode.Should().Be(HttpStatusCode.OK);
        (await approved.Content.ReadFromJsonAsync<AssistantPlanApproveResponse>())!.Success
            .Should().BeTrue();

        // Gate A case 4: the approval response is lost, so the client presents
        // the exact plan id + token again. The terminal receipt replays
        // execution truth; the plan never becomes executable a second time.
        var replay = await client.PostAsJsonAsync(
            AssistantEndpoints.ApproveRoute,
            new AssistantPlanApproveRequest(plan.PlanId, plan.ApprovalToken));
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayBody = (await replay.Content.ReadFromJsonAsync<AssistantPlanApproveResponse>())!;
        replayBody.Success.Should().BeTrue();

        (await CollectionCountAsync(factory, "Gate A doomed")).Should().Be(0);
    }

    [Fact]
    public async Task Stale_superseded_approval_is_refused_over_http_and_mutates_nothing()
    {
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        var provider = new FakeLlmProvider()
            .CallsTool(
                "library_delete_collection",
                JsonSerializer.Serialize(new { collectionId = firstTarget }))
            .CallsTool(
                "library_delete_collection",
                JsonSerializer.Serialize(new { collectionId = secondTarget }));

        using var factory = new LibraryEndpointFactory();
        await SeedCollectionAsync(factory, firstTarget, "Gate A first");
        await SeedCollectionAsync(factory, secondTarget, "Gate A second");
        using var host = CreateHost(factory, provider);
        using var client = host.CreateClient();

        using var _ = KeepToken();

        async Task<AssistantPendingPlanDto> ProposeAsync(string message, string turnId)
        {
            var response = await client.PostAsJsonAsync(
                AssistantEndpoints.TurnRoute,
                new AssistantTurnRequest(
                    "gate-a-stale-client",
                    $"delivery-{turnId}",
                    message,
                    new AssistantContextDto("library", "/library"),
                    ConversationId: "gate-a-conversation-15",
                    TurnId: turnId));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await response.Content.ReadFromJsonAsync<AssistantTurnResponse>())!.PendingPlan!;
        }

        var stale = await ProposeAsync("Delete the first collection.", "gate-a-turn-stale-a");
        var current = await ProposeAsync("Actually delete the second instead.", "gate-a-turn-stale-b");
        stale.PlanId.Should().NotBe(current.PlanId);

        // Gate A case 15: the superseded approval is non-executable. The
        // refusal is typed (409 conflict or 404 gone) and nothing is deleted.
        var refused = await client.PostAsJsonAsync(
            AssistantEndpoints.ApproveRoute,
            new AssistantPlanApproveRequest(stale.PlanId, stale.ApprovalToken));
        refused.StatusCode.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.NotFound);
        (await ProblemTitleAsync(refused)).Should().BeOneOf(
            AssistantErrorCodes.ApprovalPlanMismatch,
            AssistantErrorCodes.NotFound);
        (await CollectionCountAsync(factory, "Gate A first")).Should().Be(1);
        (await CollectionCountAsync(factory, "Gate A second")).Should().Be(1);

        var executed = await client.PostAsJsonAsync(
            AssistantEndpoints.ApproveRoute,
            new AssistantPlanApproveRequest(current.PlanId, current.ApprovalToken));
        executed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await executed.Content.ReadFromJsonAsync<AssistantPlanApproveResponse>())!.Success
            .Should().BeTrue();
        (await CollectionCountAsync(factory, "Gate A first")).Should().Be(1);
        (await CollectionCountAsync(factory, "Gate A second")).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AssistantTurnRequest Turn(
        string message,
        BookDto book,
        string conversationId,
        string turnId,
        string idem) =>
        new(
            "gate-a-capture-client",
            idem,
            message,
            new AssistantContextDto(
                "second-brain",
                "/second-brain",
                book.Id.ToString(),
                book.Title,
                BookFormat: "ebook",
                ReaderType: "epub",
                EpubCfi: "epubcfi(/6/4[chap01]!/4/2/2)"),
            ConversationId: conversationId,
            TurnId: turnId);

    private static async Task<HttpResponseMessage> PostTurnAsync(
        HttpClient client,
        AssistantTurnRequest request) =>
        await client.PostAsJsonAsync(AssistantEndpoints.TurnRoute, request);

    private static async Task<BookDto> CreateBookAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Gate A Book {Guid.NewGuid():N}",
            author = "An Author",
            forceCreate = true,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private static async Task SeedCollectionAsync(
        LibraryEndpointFactory factory,
        Guid id,
        string name)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={factory.DatabasePath}",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;
        await using var db = new NostosDbContext(options);
        db.Collections.Add(new CollectionModel { Id = id, Name = name });
        await db.SaveChangesAsync();
    }

    private static async Task<int> NoteCountAsync(LibraryEndpointFactory factory)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={factory.DatabasePath}",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;
        await using var db = new NostosDbContext(options);
        return await db.Notes.CountAsync();
    }

    private static async Task<int> CollectionCountAsync(LibraryEndpointFactory factory, string name)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={factory.DatabasePath}",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;
        await using var db = new NostosDbContext(options);
        return await db.Collections.CountAsync(c => c.Name == name);
    }

    private static async Task<string?> ProblemTitleAsync(HttpResponseMessage response)
    {
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        return document.GetProperty("title").GetString();
    }

    private static IDisposable KeepToken()
    {
        Environment.SetEnvironmentVariable(TokenVariable, TokenValue);
        return new TokenScope();
    }

    private sealed class TokenScope : IDisposable
    {
        public void Dispose() =>
            Environment.SetEnvironmentVariable(TokenVariable, null);
    }

    private static WebApplicationFactory<Program> CreateHost(
        LibraryEndpointFactory factory,
        FakeLlmProvider provider)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Assistant:Enabled", "true");
            builder.UseSetting("Assistant:BaseUrl", "http://assistant.invalid/v1");
            builder.UseSetting("Assistant:ApiKeyEnvironmentVariable", TokenVariable);
            builder.UseSetting("Assistant:MaxToolIterations", "6");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmProvider>();
                services.AddSingleton<ILlmProvider>(provider);
            });
        });
    }
}
