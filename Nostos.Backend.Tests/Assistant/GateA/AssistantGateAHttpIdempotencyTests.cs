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

        // Gate A case 3: one logical turn applies its changes once, however
        // many deliveries arrive, and the retry reports the same changeset.
        var firstBody = (await first.Content.ReadFromJsonAsync<AssistantTurnResponse>())!;
        var retryBody = (await retry.Content.ReadFromJsonAsync<AssistantTurnResponse>())!;
        firstBody.Changeset!.Status.Should().Be("applied");
        retryBody.Changeset!.ChangesetId.Should().Be(firstBody.Changeset.ChangesetId);
        (await CollectionCountAsync(factory, "Gate A shelf")).Should().Be(1);
    }

    [Fact]
    public async Task Lost_apply_response_reports_the_applied_changeset_without_applying_twice()
    {
        var target = Guid.NewGuid();
        var provider = new FakeLlmProvider()
            .CallsTool("library_delete_collection", JsonSerializer.Serialize(new { collection = target }));

        using var factory = new LibraryEndpointFactory();
        await SeedCollectionAsync(factory, target, "Gate A doomed", withBook: true);
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
        var review = (await turn.Content.ReadFromJsonAsync<AssistantTurnResponse>())!.Changeset;
        review!.Status.Should().Be("review");
        (await CollectionCountAsync(factory, "Gate A doomed")).Should().Be(1, "a review applies nothing");

        var apply = new AssistantChangesetApplyRequest(review.ActionToken, null, review.DiffHash);
        var applied = await client.PostAsJsonAsync(ChangesetRoute(review, "apply"), apply);
        applied.StatusCode.Should().Be(HttpStatusCode.OK);
        (await applied.Content.ReadFromJsonAsync<AssistantChangesetActionResponse>())!.Success.Should().BeTrue();

        // Gate A case 4: the apply response is lost and the client presents
        // the same changeset and token again. It reports the applied
        // changeset; the changes never run a second time.
        var replay = await client.PostAsJsonAsync(ChangesetRoute(review, "apply"), apply);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayBody = (await replay.Content.ReadFromJsonAsync<AssistantChangesetActionResponse>())!;
        replayBody.Success.Should().BeTrue();
        replayBody.Changeset!.Status.Should().Be("applied");
        (await CollectionCountAsync(factory, "Gate A doomed")).Should().Be(0);

        var undone = await client.PostAsJsonAsync(
            ChangesetRoute(review, "undo"), new AssistantChangesetTokenRequest(review.ActionToken));
        undone.StatusCode.Should().Be(HttpStatusCode.OK);
        (await undone.Content.ReadFromJsonAsync<AssistantChangesetActionResponse>())!.Changeset!.Status
            .Should().Be("undone");
        (await CollectionCountAsync(factory, "Gate A doomed")).Should().Be(1);
    }

    [Fact]
    public async Task A_superseded_review_is_refused_over_http_and_changes_nothing()
    {
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        var provider = new FakeLlmProvider()
            .CallsTool("library_delete_collection", JsonSerializer.Serialize(new { collection = firstTarget }))
            .Returns("Prepared.")
            .CallsTool("library_delete_collection", JsonSerializer.Serialize(new { collection = secondTarget }))
            .Returns("Prepared.");

        using var factory = new LibraryEndpointFactory();
        await SeedCollectionAsync(factory, firstTarget, "Gate A first", withBook: true);
        await SeedCollectionAsync(factory, secondTarget, "Gate A second", withBook: true);
        using var host = CreateHost(factory, provider);
        using var client = host.CreateClient();

        using var _ = KeepToken();

        async Task<AssistantChangesetDto> ProposeAsync(string message, string turnId)
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
            return (await response.Content.ReadFromJsonAsync<AssistantTurnResponse>())!.Changeset!;
        }

        var stale = await ProposeAsync("Delete the first collection.", "gate-a-turn-stale-a");
        var current = await ProposeAsync("Actually delete the second instead.", "gate-a-turn-stale-b");
        stale.ChangesetId.Should().NotBe(current.ChangesetId);

        // Gate A case 15: the superseded review is not applicable. The refusal
        // is typed and nothing is deleted.
        var refused = await client.PostAsJsonAsync(ChangesetRoute(stale, "apply"),
            new AssistantChangesetApplyRequest(stale.ActionToken, null, stale.DiffHash));
        refused.StatusCode.Should().Be(HttpStatusCode.OK);
        var refusedBody = (await refused.Content.ReadFromJsonAsync<AssistantChangesetActionResponse>())!;
        refusedBody.Success.Should().BeFalse();
        refusedBody.ErrorCode.Should().Be(AssistantErrorCodes.ChangesetNotReviewable);
        (await CollectionCountAsync(factory, "Gate A first")).Should().Be(1);
        (await CollectionCountAsync(factory, "Gate A second")).Should().Be(1);

        var wrongToken = await client.PostAsJsonAsync(ChangesetRoute(current, "apply"),
            new AssistantChangesetApplyRequest(stale.ActionToken, null, null));
        wrongToken.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var executed = await client.PostAsJsonAsync(ChangesetRoute(current, "apply"),
            new AssistantChangesetApplyRequest(current.ActionToken, null, current.DiffHash));
        executed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await executed.Content.ReadFromJsonAsync<AssistantChangesetActionResponse>())!.Success.Should().BeTrue();
        (await CollectionCountAsync(factory, "Gate A first")).Should().Be(1);
        (await CollectionCountAsync(factory, "Gate A second")).Should().Be(0);
    }

    private static string ChangesetRoute(AssistantChangesetDto changeset, string action) =>
        $"/api/assistant/changesets/{changeset.ChangesetId}/{action}";

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
        string name,
        bool withBook = false)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={factory.DatabasePath}",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;
        await using var db = new NostosDbContext(options);
        db.Collections.Add(new CollectionModel { Id = id, Name = name });
        if (withBook)
        {
            var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = $"Held by {name}" };
            db.Books.Add(book);
            db.BookCollections.Add(new BookCollectionModel { BookId = book.Id, CollectionId = id });
        }
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
