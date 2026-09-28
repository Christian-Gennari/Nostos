using System.Text.Json;
using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Xunit;
using static Nostos.Backend.Tests.Assistant.GateA.GateATurns;

namespace Nostos.Backend.Tests.Assistant.GateA;

/// <summary>
/// Gate A case 12: the server-side typed failure contract (issue #566), plus
/// case 13 (cancellation before a write starts produces no write) on the
/// continuation path.
///
/// Every deterministic refusal carries a stable code the surface maps to UI
/// state (retryable prompt, re-ask, typed terminal, stop acknowledgement).
/// The Angular mapping itself lives in the frontend and is out of scope for
/// this project; what this file pins is that the server emits exactly the
/// codes the mapping was written against, so a renamed or swallowed code
/// fails here instead of silently degrading the UI. No model call runs in
/// any of these tests beyond the scripted fake.
/// </summary>
public sealed class AssistantGateAFailureContractTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantGateAFailureContractTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Unknown_continuation_is_ContinuationNotFound()
    {
        using var h = GateAHost.Create(_fixture);

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "Page 12.",
            Context(bookFormat: "physical"),
            conversationId: "gate-a-contract",
            turnId: "turn-unknown",
            continuationId: "does-not-exist"));

        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(AssistantErrorCodes.ContinuationNotFound);
        response.CapturedNoteId.Should().BeNull();
        (await NoteCountAsync(h)).Should().Be(0);
        h.Llm.CallCount.Should().Be(0, "a stale continuation never reaches the model");
    }

    [Fact]
    public async Task Empty_continuation_answer_is_ContinuationAnswerRequired()
    {
        using var h = GateAHost.Create(_fixture);
        var book = await SeedBookAsync(h);

        h.Llm.CallsTool(
            "notes_capture",
            $$"""{"bookId":"{{book.Id}}","content":"A thought"}""");

        var first = await h.Orchestrator.HandleTurnAsync(Turn(
            "Remember this.",
            Context(bookId: book.Id.ToString(), bookFormat: "physical"),
            conversationId: "gate-a-contract-empty",
            turnId: "turn-original"));

        var refused = await h.Orchestrator.HandleTurnAsync(Turn(
            "   ",
            Context(bookId: book.Id.ToString(), bookFormat: "physical"),
            conversationId: "gate-a-contract-empty",
            turnId: "turn-empty",
            continuationId: first.AnchorPrompt!.ContinuationId));

        refused.Error.Should().NotBeNull();
        refused.Error!.Code.Should().Be(AssistantErrorCodes.ContinuationAnswerRequired);
        (await NoteCountAsync(h)).Should().Be(0);

        // The continuation stays alive: the real answer still completes.
        var completed = await h.Orchestrator.HandleTurnAsync(Turn(
            "Page 3.",
            Context(bookId: book.Id.ToString(), bookFormat: "physical"),
            conversationId: "gate-a-contract-empty",
            turnId: "turn-real-answer",
            continuationId: first.AnchorPrompt.ContinuationId));

        completed.Error.Should().BeNull();
        (await NoteCountAsync(h)).Should().Be(1);
    }

    [Fact]
    public async Task Approval_refusals_keep_their_typed_codes()
    {
        using var h = GateAHost.Create(_fixture);
        var collection = await SeedCollectionAsync(h, "Contract collection");

        h.Llm.CallsTool(
            "library_delete_collection",
            JsonSerializer.Serialize(new { collectionId = collection.Id }));

        var plan = (await h.Orchestrator.HandleTurnAsync(Turn(
            "Delete the contract collection.",
            Context(surface: "library", route: "/library")))).PendingPlan;
        plan.Should().NotBeNull();

        (await h.Orchestrator.ApproveAsync("not-this-plan", plan!.ApprovalToken))
            .ErrorCode.Should().Be(AssistantErrorCodes.NotFound);
        (await h.Orchestrator.ApproveAsync(plan.PlanId, "garbage-token"))
            .ErrorCode.Should().Be(AssistantErrorCodes.ApprovalPlanMismatch);
        (await h.Orchestrator.ApproveAsync(plan.PlanId, null))
            .ErrorCode.Should().Be(AssistantErrorCodes.ApprovalRequired);

        (await CollectionCountAsync(h)).Should().Be(1, "every refusal precedes any store access");
    }

    [Fact]
    public async Task Pending_source_is_SourceIndexingPending()
    {
        var search = new PendingBookTextSearchService();
        using var h = GateAHost.Create(_fixture, bookText: search);

        h.Llm
            .CallsTool("book_text_search", """{"query":"needle"}""")
            .Returns("It is still indexing.");

        var response = await h.Orchestrator.HandleTurnAsync(
            Turn("Find needle.", Context(surface: "library", route: "/library")));

        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(AssistantErrorCodes.SourceIndexingPending);
        response.Sources.Should().BeEmpty("a pending source is never cited");
    }

    [Fact]
    public async Task Pre_cancelled_turn_is_TurnCancelled_without_a_write()
    {
        using var h = GateAHost.Create(_fixture);
        var book = await SeedBookAsync(h);
        h.Llm.CallsTool("notes_capture", """{"content":"must not be saved"}""");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var response = await h.Orchestrator.HandleTurnAsync(
            Turn(
                "Save this.",
                Context(
                    bookId: book.Id.ToString(),
                    bookTitle: book.Title,
                    bookFormat: "ebook",
                    readerType: "epub",
                    epubCfi: "epubcfi(/6/2)")),
            cts.Token);

        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(AssistantErrorCodes.TurnCancelled);
        h.Llm.CallCount.Should().Be(0);
        (await NoteCountAsync(h)).Should().Be(0);
    }

    [Fact]
    public async Task Cancelled_continuation_answer_writes_nothing()
    {
        using var h = GateAHost.Create(_fixture);
        var book = await SeedBookAsync(h, "Cancelled Continuation");

        h.Llm.CallsTool(
            "notes_capture",
            $$"""{"bookId":"{{book.Id}}","content":"A thought"}""");

        var first = await h.Orchestrator.HandleTurnAsync(Turn(
            "Remember this.",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "physical"),
            conversationId: "gate-a-contract-cancel",
            turnId: "turn-original"));

        // Gate A case 13 on the continuation path: the stop arrives before
        // the resumed write begins, so nothing is written and the turn is
        // typed as cancelled rather than failed.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var stopped = await h.Orchestrator.HandleTurnAsync(
            Turn(
                "Page 9.",
                Context(
                    bookId: book.Id.ToString(),
                    bookTitle: book.Title,
                    bookFormat: "physical"),
                conversationId: "gate-a-contract-cancel",
                turnId: "turn-cancelled-answer",
                continuationId: first.AnchorPrompt!.ContinuationId),
            cts.Token);

        stopped.Error.Should().NotBeNull();
        stopped.Error!.Code.Should().Be(AssistantErrorCodes.TurnCancelled);
        stopped.CapturedNoteId.Should().BeNull();
        (await NoteCountAsync(h)).Should().Be(0);
    }

    [Fact]
    public async Task Stale_evidence_handle_is_NotFound_through_the_tool()
    {
        using var h = GateAHost.Create(_fixture);

        using var document = JsonDocument.Parse(
            $$"""{"kind":"note","noteId":"{{Guid.NewGuid()}}"}""");
        var result = await h.Registry.InvokeAsync(
            "knowledge_read_evidence",
            document.RootElement,
            new AssistantToolContext(ClientId: "gate-a", IdempotencyKey: "gate-a-stale"));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(AssistantErrorCodes.NotFound);
        result.ErrorMessage.Should().Contain("stale");
    }

    private sealed class PendingBookTextSearchService : IBookTextSearchService
    {
        public Task<BookTextSearchResponse> SearchAsync(
            BookTextSearchRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new BookTextSearchResponse(
                [],
                [
                    new BookTextIngestionState(
                        Guid.NewGuid(),
                        BookTextIngestionStatus.Pending,
                        "book.epub",
                        BookTextSourceFormat.Epub,
                        null,
                        BookTextArtifactSchema.CurrentExtractorVersion,
                        null,
                        null,
                        0,
                        0,
                        0,
                        DateTime.UtcNow),
                ],
                false));
    }
}
