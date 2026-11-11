using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Data;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant.GateA;

/// <summary>
/// Gate A case 12: a capture commits, then a later provider call in the same
/// turn fails (issue #1558). The failure response must carry the capture that
/// already exists — the panel has to say the note was saved — and a retry with
/// the same TurnId must replay the canonical receipt instead of writing a
/// second note.
///
/// These drive the real host (<see cref="LibraryEndpointFactory"/>) with only
/// the LLM replaced by <see cref="FakeLlmProvider"/>, so the HTTP boundary, the
/// failure contract and the persisted note receipt are all exercised. No model
/// is called.
/// </summary>
public sealed class AssistantGateAPartialWriteTests : IDisposable
{
    private const string TokenVariable = "NOSTOS_GATE_A_PARTIAL_WRITE_TOKEN";
    private const string TokenValue = "gate-a-partial-write-sentinel";
    private const string FailureMessage = "The AI service could not complete this turn. Try again.";

    public void Dispose() =>
        Environment.SetEnvironmentVariable(TokenVariable, null);

    /// <summary>
    /// Call 1 saves the note; call 2 is the provider failing after the write.
    /// The retry repeats the same tool call, so the canonical receipt answers
    /// it, and the next call is the model's closing line.
    /// </summary>
    private static FakeLlmProvider Provider() => new()
    {
        Responder = call =>
        {
            if (call is 1 or 3)
            {
                return new LlmCompletion(
                    null,
                    "tool_calls",
                    [new LlmToolCall(
                        $"capture-{call}",
                        "notes_capture",
                        """{"content":"Ropes hold a knot better than smooth cord."}""")]);
            }

            if (call is 2)
                throw LlmException.ProviderFailure("upstream says no");

            return new LlmCompletion("Saved.", "stop", []);
        },
    };

    [Fact]
    public async Task A_capture_that_committed_before_the_provider_failed_is_in_the_turn_failure_body()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, Provider());
        using var client = host.CreateClient();
        var book = await CreateBookAsync(client);

        using var _token = KeepToken();

        var failed = await client.PostAsJsonAsync(
            AssistantEndpoints.TurnRoute,
            Turn("Remember this phrase.", book, "gate-a-partial-conversation", "gate-a-partial-turn"));

        // The failure keeps the status and plain words it always had...
        ((int)failed.StatusCode).Should().Be(502);
        var problem = await failed.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be(LlmErrorCodes.Provider);
        problem.GetProperty("detail").GetString().Should().Be(FailureMessage);
        problem.GetRawText().Should().NotContain("upstream says no");

        // ...and now also the capture that committed before the provider failed.
        var committed = problem.GetProperty("committed");
        var noteId = committed.GetProperty("capturedNoteId").GetString();
        noteId.Should().NotBeNullOrWhiteSpace();
        committed.GetProperty("acknowledgement").GetString().Should().NotBeNullOrWhiteSpace();
        (await NoteCountAsync(factory)).Should().Be(1);

        // The client retries the same logical turn. The model repeats its call,
        // the receipt replays, and the second delivery reports the same note.
        var retry = await client.PostAsJsonAsync(
            AssistantEndpoints.TurnRoute,
            Turn("Remember this phrase.", book, "gate-a-partial-conversation", "gate-a-partial-turn"));

        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var retried = (await retry.Content.ReadFromJsonAsync<AssistantTurnResponse>())!;
        retried.CapturedNoteId.Should().Be(noteId);
        (await NoteCountAsync(factory)).Should().Be(1, "a retry of the same TurnId never writes a second note");
    }

    [Fact]
    public async Task The_streamed_failure_event_carries_the_capture_so_the_panel_can_report_it()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, Provider());
        using var client = host.CreateClient();
        var book = await CreateBookAsync(client);

        using var _token = KeepToken();

        var failed = await client.PostAsJsonAsync(
            AssistantEndpoints.StreamTurnRoute,
            Turn("Remember this phrase.", book, "gate-a-stream-partial", "gate-a-stream-turn"));
        failed.IsSuccessStatusCode.Should().BeTrue("a provider failure is an event, not an HTTP error");

        var events = await EventsAsync(failed);
        events.Count(item => item.Kind == AssistantTurnEventKinds.Failed).Should().Be(1);
        var terminal = events[^1];
        terminal.Kind.Should().Be(AssistantTurnEventKinds.Failed);
        terminal.Failure!.Code.Should().Be(LlmErrorCodes.Provider);
        terminal.Failure.Message.Should().Be(FailureMessage);
        terminal.Failure.Retryable.Should().BeTrue();

        // The failed event keeps the turn's committed truth: the note id, the
        // acknowledgement the reader sees above the failure, and no plan.
        var committed = terminal.Response!;
        committed.CapturedNoteId.Should().NotBeNullOrWhiteSpace();
        committed.Acknowledgement.Should().NotBeNullOrWhiteSpace();
        committed.Changeset.Should().BeNull("staged changes were never applied, so they are not reported");
        (await NoteCountAsync(factory)).Should().Be(1);

        var retry = await client.PostAsJsonAsync(
            AssistantEndpoints.StreamTurnRoute,
            Turn("Remember this phrase.", book, "gate-a-stream-partial", "gate-a-stream-turn"));
        var retried = await EventsAsync(retry);
        retried.Should().NotContain(item => item.Kind == AssistantTurnEventKinds.Failed);
        var completed = retried[^1];

        completed.Kind.Should().Be(AssistantTurnEventKinds.Completed);
        completed.Response!.CapturedNoteId.Should().Be(committed.CapturedNoteId);
        (await NoteCountAsync(factory)).Should().Be(1, "the retry replays the receipt, never a second write");
    }

    [Fact]
    public async Task A_failure_with_nothing_committed_keeps_the_empty_body_it_always_had()
    {
        var provider = new FakeLlmProvider
        {
            Responder = _ => throw LlmException.ProviderFailure("upstream says no"),
        };

        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider);
        using var client = host.CreateClient();
        var book = await CreateBookAsync(client);

        using var _token = KeepToken();

        var failed = await client.PostAsJsonAsync(
            AssistantEndpoints.TurnRoute,
            Turn("What did I note?", book, "gate-a-partial-none", "gate-a-partial-none-turn"));

        ((int)failed.StatusCode).Should().Be(502);
        var problem = await failed.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().Should().Be(LlmErrorCodes.Provider);
        problem.TryGetProperty("committed", out _).Should().BeFalse(
            "the optional field is only present when a write survived the failure");
        (await NoteCountAsync(factory)).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AssistantTurnRequest Turn(
        string message,
        BookDto book,
        string conversationId,
        string turnId) =>
        new(
            "gate-a-partial-client",
            $"{turnId}-delivery",
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

    private static async Task<IReadOnlyList<AssistantTurnEventDto>> EventsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<AssistantTurnEventDto>(
                line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToList();
    }

    private static async Task<BookDto> CreateBookAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/books", new
        {
            type = "physical",
            title = $"Gate A Partial Book {Guid.NewGuid():N}",
            author = "An Author",
            forceCreate = true,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
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
        FakeLlmProvider provider) =>
        factory.WithWebHostBuilder(builder =>
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
