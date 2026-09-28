using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Tests.Support;
using Xunit;
using static Nostos.Backend.Tests.Assistant.GateA.GateATurns;

namespace Nostos.Backend.Tests.Assistant.GateA;

/// <summary>
/// Gate A cases 9–10: the server-side conversation contract (issue #566).
///
/// The Angular shell persists <c>conversationId</c> in sessionStorage and
/// restores it on refresh (covered by
/// <c>assistant.service.spec.ts</c>: "restores the same conversation id and
/// transcript after a page-reload-style service recreation"); a New
/// conversation mints a fresh id and clears the ledger (same spec:
/// "persists a New conversation as a clean replacement"). What the server
/// owns is the other half: deterministic state (continuations, plans) is
/// keyed by conversation, so the same id resumes and a fresh id starts
/// clean. These tests pin that contract against the real orchestrator.
/// </summary>
public sealed class AssistantGateAConversationTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantGateAConversationTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Session_refresh_restores_the_active_conversation_continuation()
    {
        using var h = GateAHost.Create(_fixture);
        var book = await SeedBookAsync(h, "Refreshed Book");

        h.Llm
            .CallsTool(
                "notes_capture",
                $$"""{"bookId":"{{book.Id}}","content":"A thought across a refresh"}""")
            .Returns("I can capture thoughts and search your library.");

        // Turn 1 opens the deterministic continuation under conversation C.
        var first = await h.Orchestrator.HandleTurnAsync(Turn(
            "Remember this.",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "physical"),
            conversationId: "gate-a-refresh",
            turnId: "turn-original"));

        first.AnchorPrompt.Should().NotBeNull();
        var continuationId = first.AnchorPrompt!.ContinuationId!;

        // The page reloads. The restored client re-sends the same
        // conversation id; an unrelated turn in between must not disturb the
        // pending deterministic state.
        var between = await h.Orchestrator.HandleTurnAsync(Turn(
            "What can you do?",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "physical"),
            conversationId: "gate-a-refresh",
            turnId: "turn-between"));
        between.Error.Should().BeNull();
        between.AnchorPrompt.Should().BeNull();
        (await NoteCountAsync(h)).Should().Be(0);

        // The restored continuation answers with the real page and completes
        // from the ORIGINAL capture arguments — nothing was lost or guessed.
        var completed = await h.Orchestrator.HandleTurnAsync(Turn(
            "Page 12.",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "physical"),
            conversationId: "gate-a-refresh",
            turnId: "turn-answer",
            continuationId: continuationId));

        completed.Error.Should().BeNull();
        completed.CapturedNoteId.Should().NotBeNullOrWhiteSpace();
        h.Llm.CallCount.Should().Be(2, "the resume path never re-consults the model");

        await using var db = await h.Factory.CreateDbContextAsync();
        var note = await db.Notes.AsNoTracking().SingleAsync();
        note.Content.Should().Be("A thought across a refresh");
        note.SourceAnchorKind.Should().Be("physical_page");
        note.SourceAnchorValue.Should().Be("12");
    }

    [Fact]
    public async Task A_new_conversation_starts_clean_and_leaves_the_old_one_intact()
    {
        using var h = GateAHost.Create(_fixture);
        var book = await SeedBookAsync(h, "Two Conversations");

        h.Llm
            .CallsTool(
                "notes_capture",
                $$"""{"bookId":"{{book.Id}}","content":"First conversation thought"}""")
            .Returns("Just a plain answer.");

        var first = await h.Orchestrator.HandleTurnAsync(Turn(
            "Remember this.",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "physical"),
            conversationId: "gate-a-conversation-old",
            turnId: "turn-original"));

        var continuationId = first.AnchorPrompt!.ContinuationId!;

        // A New conversation answers with the old continuation id: it belongs
        // to a different conversation, so it is refused and mutates nothing.
        var foreign = await h.Orchestrator.HandleTurnAsync(Turn(
            "Page 12.",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "physical"),
            conversationId: "gate-a-conversation-new",
            turnId: "turn-foreign-answer",
            continuationId: continuationId));

        foreign.Error.Should().NotBeNull();
        foreign.Error!.Code.Should().Be(AssistantErrorCodes.ContinuationMismatch);
        foreign.CapturedNoteId.Should().BeNull();
        (await NoteCountAsync(h)).Should().Be(0);

        // The new conversation is otherwise clean: a fresh turn runs as a
        // fresh turn, with no anchor prompt carried over from the old one.
        var fresh = await h.Orchestrator.HandleTurnAsync(Turn(
            "What can you do?",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "physical"),
            conversationId: "gate-a-conversation-new",
            turnId: "turn-fresh"));
        fresh.Error.Should().BeNull();
        fresh.AnchorPrompt.Should().BeNull();

        // And the old conversation is untouched: its continuation still
        // completes exactly once.
        var completed = await h.Orchestrator.HandleTurnAsync(Turn(
            "Page 12.",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "physical"),
            conversationId: "gate-a-conversation-old",
            turnId: "turn-answer",
            continuationId: continuationId));

        completed.Error.Should().BeNull();
        completed.CapturedNoteId.Should().NotBeNullOrWhiteSpace();
        (await NoteCountAsync(h)).Should().Be(1);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Notes.AsNoTracking().SingleAsync()).Content
            .Should().Be("First conversation thought");
    }
}
