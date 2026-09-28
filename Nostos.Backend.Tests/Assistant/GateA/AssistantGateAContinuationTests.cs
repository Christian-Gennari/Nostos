using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;
using static Nostos.Backend.Tests.Assistant.GateA.GateATurns;

namespace Nostos.Backend.Tests.Assistant.GateA;

/// <summary>
/// Gate A cases 5–8: deterministic capture continuations (issue #566).
///
/// The user's REAL answer travels as the turn <c>Message</c> tied to the
/// server-held continuation — never hidden in request context — and the
/// resumed capture executes exactly once. These tests drive the real
/// orchestrator over real SQLite with a scripted provider.
/// </summary>
public sealed class AssistantGateAContinuationTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantGateAContinuationTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Physical_page_answer_wins_over_a_stale_context_anchor()
    {
        using var h = GateAHost.Create(_fixture);
        var book = await SeedBookAsync(h, "Physical Book");

        h.Llm.CallsTool(
            "notes_capture",
            $$"""{"bookId":"{{book.Id}}","content":"A thought"}""");

        var first = await h.Orchestrator.HandleTurnAsync(Turn(
            "Remember this.",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "physical"),
            conversationId: "gate-a-page-truth",
            turnId: "turn-original"));

        first.AnchorPrompt.Should().NotBeNull();
        var continuationId = first.AnchorPrompt!.ContinuationId!;

        // The answer request carries a stale decoy anchor in its context (for
        // example a lingering reader snapshot). The server-held continuation
        // is authoritative: the real Message answer wins, the context decoy
        // is ignored, and the original thought is what gets saved.
        var completed = await h.Orchestrator.HandleTurnAsync(Turn(
            "Page 247.",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "physical",
                anchor: new AssistantAnchorDto("physical_page", "999", true)),
            conversationId: "gate-a-page-truth",
            turnId: "turn-page-answer",
            continuationId: continuationId));

        completed.Error.Should().BeNull();
        completed.CapturedNoteId.Should().NotBeNullOrWhiteSpace();
        h.Llm.CallCount.Should().Be(1, "the continuation resume never re-consults the model");

        await using var db = await h.Factory.CreateDbContextAsync();
        var note = await db.Notes.AsNoTracking().SingleAsync();
        note.Content.Should().Be("A thought");
        note.SourceAnchorKind.Should().Be("physical_page");
        note.SourceAnchorValue.Should().Be("247", "the real answer, not the stale context decoy");
        note.AnchorVerified.Should().BeFalse("a typed page is never app-verified");
    }

    [Fact]
    public async Task External_audio_answer_replay_captures_exactly_once()
    {
        using var h = GateAHost.Create(_fixture);
        var book = await SeedBookAsync(h, "External Audio");

        h.Llm.CallsTool(
            "notes_capture",
            $$"""{"bookId":"{{book.Id}}","content":"Audio thought"}""");

        var first = await h.Orchestrator.HandleTurnAsync(Turn(
            "Save this thought.",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "audiobook",
                readerType: null),
            conversationId: "gate-a-audio-replay",
            turnId: "turn-original"));

        first.AnchorPrompt!.Kind.Should().Be("external_audio_timestamp");
        var continuationId = first.AnchorPrompt.ContinuationId!;

        var answer = Turn(
            "1:23",
            Context(
                bookId: book.Id.ToString(),
                bookTitle: book.Title,
                bookFormat: "audiobook",
                readerType: null),
            idem: "delivery-answer-a",
            conversationId: "gate-a-audio-replay",
            turnId: "turn-audio-answer",
            continuationId: continuationId);

        var completed = await h.Orchestrator.HandleTurnAsync(answer);
        completed.Error.Should().BeNull();
        completed.CapturedNoteId.Should().NotBeNullOrWhiteSpace();

        // A lost continuation response replays the same answer TurnId: the
        // bounded terminal receipt answers, and no second note exists.
        var replay = await h.Orchestrator.HandleTurnAsync(answer with
        {
            IdempotencyKey = "delivery-answer-b",
        });

        replay.CapturedNoteId.Should().Be(completed.CapturedNoteId);
        (await NoteCountAsync(h)).Should().Be(1);
        h.Llm.CallCount.Should().Be(1);

        await using var db = await h.Factory.CreateDbContextAsync();
        var note = await db.Notes.AsNoTracking().SingleAsync();
        note.Content.Should().Be("Audio thought");
        note.SourceAnchorKind.Should().Be("external_audio_timestamp");
        note.SourceAnchorValue.Should().Be("83");
        note.AnchorVerified.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_book_answer_replay_captures_exactly_once()
    {
        using var h = GateAHost.Create(_fixture);
        var intended = await SeedBookAsync(h, "Vita Contemplativa");

        h.Llm.CallsTool(
            "notes_capture",
            """{"content":"A thought with no open book"}""");

        var first = await h.Orchestrator.HandleTurnAsync(Turn(
            "Save this thought.",
            Context(surface: "library", route: "/library"),
            conversationId: "gate-a-book-replay",
            turnId: "turn-original"));

        first.AnchorPrompt!.Kind.Should().Be("book");
        var continuationId = first.AnchorPrompt.ContinuationId!;

        var answer = Turn(
            "Vita Contemplativa",
            Context(surface: "library", route: "/library"),
            idem: "delivery-answer-a",
            conversationId: "gate-a-book-replay",
            turnId: "turn-book-answer",
            continuationId: continuationId);

        var completed = await h.Orchestrator.HandleTurnAsync(answer);
        completed.Error.Should().BeNull();
        completed.Acknowledgement.Should().Contain("Vita Contemplativa");

        // The user answered once; a retried delivery must not resolve and
        // save a second time.
        var replay = await h.Orchestrator.HandleTurnAsync(answer with
        {
            IdempotencyKey = "delivery-answer-b",
        });

        replay.CapturedNoteId.Should().Be(completed.CapturedNoteId);
        (await NoteCountAsync(h)).Should().Be(1);
        h.Llm.CallCount.Should().Be(1);

        await using var db = await h.Factory.CreateDbContextAsync();
        var note = await db.Notes.AsNoTracking().SingleAsync();
        note.BookId.Should().Be(intended.Id);
        note.Content.Should().Be("A thought with no open book");
    }

    [Fact]
    public async Task Historical_book_context_never_redirects_a_capture_to_the_old_book()
    {
        using var h = GateAHost.Create(_fixture);
        var bookA = await SeedBookAsync(h, "Book A");
        var bookB = await SeedBookAsync(h, "Book B");

        // The model tries to file the new thought against the historical
        // book; the open book is the fact that must win.
        h.Llm.CallsTool(
            "notes_capture",
            $$"""{"bookId":"{{bookA.Id}}","content":"A new thought"}""");

        var history = new List<AssistantHistoryMessageDto>
        {
            new(
                "user",
                "Earlier I was reading Book A.",
                new AssistantHistoricalContextDto(
                    Surface: "reader",
                    BookId: bookA.Id.ToString(),
                    BookTitle: "Book A")),
            new(
                "assistant",
                "Saved to Book A.",
                CapturedNoteId: Guid.NewGuid().ToString("N")),
        };

        var response = await h.Orchestrator.HandleTurnAsync(Turn(
            "Remember this new thought.",
            Context(
                bookId: bookB.Id.ToString(),
                bookTitle: "Book B",
                bookFormat: "ebook",
                readerType: "epub",
                epubCfi: "epubcfi(/6/2)"),
            history: history));

        response.CapturedNoteId.Should().NotBeNullOrWhiteSpace();
        response.Acknowledgement.Should().Contain("Book B");

        // Both referents stay visible to the model: the historical turn keeps
        // Book A, the current context names Book B.
        h.Llm.LastRequest.Messages.Should().Contain(m =>
            m.Role == "user" && m.Content != null && m.Content.Contains("Book A"));
        h.Llm.LastRequest.Messages.Should().Contain(m =>
            m.Role == "system" && m.Content != null && m.Content.Contains("Book B"));

        await using var db = await h.Factory.CreateDbContextAsync();
        var note = await db.Notes.AsNoTracking().SingleAsync();
        note.BookId.Should().Be(bookB.Id, "history resolves references; it never authorizes the write");
        note.Content.Should().Be("A new thought");
    }
}
