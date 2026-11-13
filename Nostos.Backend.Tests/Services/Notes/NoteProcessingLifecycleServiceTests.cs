using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// The raw transcript and its lifecycle (issue #1619, split out of NoteService):
/// a re-run starts from the original words, restoring never erases the
/// transcript, and a provider failure keeps the raw words.
/// </summary>
public sealed class NoteProcessingLifecycleServiceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public NoteProcessingLifecycleServiceTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Restoring_the_raw_transcript_restores_the_words_and_the_honest_mode()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, content: "polished words", raw: "raw words", mode: "clarify");

        var result = await h.Lifecycle.RestoreRawAsync(note.Id);

        result.Success.Should().BeTrue();
        result.Value!.Content.Should().Be("raw words");
        result.Value.ProcessingMode.Should().Be(ThoughtProcessingModes.Verbatim);
        var stored = await h.Db.Notes.AsNoTracking().SingleAsync(n => n.Id == note.Id);
        stored.RawContent.Should().Be("raw words");
    }

    [Fact]
    public async Task Restoring_a_note_that_kept_no_transcript_is_a_typed_refusal()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, content: "typed", raw: null, mode: "verbatim");

        var result = await h.Lifecycle.RestoreRawAsync(note.Id);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(NoteErrorCodes.NoRawTranscript);
    }

    [Fact]
    public async Task An_unknown_processing_mode_is_a_typed_refusal()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, content: "words", raw: "words", mode: "verbatim");

        var result = await h.Lifecycle.ReprocessAsync(note.Id, "rewrite-everything");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(NoteErrorCodes.InvalidProcessingMode);
    }

    [Fact]
    public async Task A_verbatim_rerun_uses_the_raw_words_and_never_calls_the_provider()
    {
        var thoughts = new FakeThoughtProcessor();
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath(), thoughts);
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, content: "polished words", raw: "raw words", mode: "clarify");

        var result = await h.Lifecycle.ReprocessAsync(note.Id, ThoughtProcessingModes.Verbatim);

        result.Success.Should().BeTrue();
        result.Value!.Content.Should().Be("raw words");
        result.Value.ProcessingMode.Should().Be(ThoughtProcessingModes.Verbatim);
        thoughts.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rerun_starts_from_the_raw_transcript_not_the_polished_prose()
    {
        var thoughts = new FakeThoughtProcessor
        {
            Responder = (text, mode) => new ThoughtProcessingResult($"[{mode}] {text}", mode, ProviderCalled: true),
        };
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath(), thoughts);
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, content: "polished words", raw: "raw words", mode: "light_polish");

        var result = await h.Lifecycle.ReprocessAsync(note.Id, ThoughtProcessingModes.Clarify);

        result.Success.Should().BeTrue();
        thoughts.LastText.Should().Be("raw words");
        result.Value!.Content.Should().Be("[clarify] raw words");
    }

    [Fact]
    public async Task Rewriting_a_capture_another_conversation_made_is_refused()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, content: "words", raw: "words", mode: "verbatim");

        var result = await h.Lifecycle.RewriteCaptureAsync(note.Id, "another-conversation");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(NoteErrorCodes.NoteNotFound);
    }

    [Fact]
    public async Task The_raw_transcript_is_readable_with_the_mode_the_text_reflects()
    {
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath());
        var book = await h.SeedBookAsync<EBookModel>("A Book");
        var note = await h.SeedNoteAsync(book.Id, content: "polished", raw: "raw words", mode: "clarify");

        var result = await h.Lifecycle.GetRawAsync(note.Id);

        result.Success.Should().BeTrue();
        result.Value.Should().Be(new NoteRawTranscriptDto(note.Id, "raw words", "polished", "clarify"));
    }

    [Fact]
    public async Task A_verbatim_capture_is_a_no_op_that_calls_no_provider()
    {
        var thoughts = new FakeThoughtProcessor();
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath(), thoughts);
        var model = new NoteModel { Content = "as I said it" };

        await h.Lifecycle.ApplyCaptureProcessingAsync(model, ThoughtProcessingModes.Verbatim);

        model.Content.Should().Be("as I said it");
        model.RawContent.Should().BeNull();
        model.ProcessingMode.Should().Be(ThoughtProcessingModes.Verbatim);
        thoughts.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_capture_with_no_thought_text_is_stored_verbatim()
    {
        var thoughts = new FakeThoughtProcessor();
        using var h = new NoteServiceTestContext(_fixture.CreateDatabasePath(), thoughts);
        var model = new NoteModel { Content = "  " };

        await h.Lifecycle.ApplyCaptureProcessingAsync(model, ThoughtProcessingModes.Clarify);

        model.ProcessingMode.Should().Be(ThoughtProcessingModes.Verbatim);
        thoughts.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_failure_keeps_the_raw_words()
    {
        using var h = new NoteServiceTestContext(
            _fixture.CreateDatabasePath(), new ThrowingThoughtProcessor());
        var model = new NoteModel { Content = "the words I said" };

        await h.Lifecycle.ApplyCaptureProcessingAsync(model, ThoughtProcessingModes.Clarify);

        model.Content.Should().Be("the words I said");
        model.RawContent.Should().Be("the words I said");
        model.ProcessingMode.Should().Be(ThoughtProcessingModes.Verbatim);
    }

    private sealed class ThrowingThoughtProcessor : IThoughtProcessor
    {
        public Task<ThoughtProcessingResult> ProcessAsync(
            string text,
            string mode,
            CancellationToken ct = default) =>
            throw LlmException.ProviderFailure("the provider is down");
    }
}
