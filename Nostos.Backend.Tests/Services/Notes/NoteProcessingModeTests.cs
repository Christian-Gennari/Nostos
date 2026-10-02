using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// The post-processing pipeline end to end at the service level (issue #262 §7,
/// §8), over the REAL <see cref="ThoughtProcessor"/> and a scripted
/// <see cref="FakeLlmProvider"/>. No test here reaches the network, so the free
/// pool is never spent.
///
/// Each hard invariant the brief names has a test:
/// raw capture preserved; reprocess from RawContent never from processed prose;
/// quotes untouched; server-prepared service mode honoured; raw readable and
/// restorable. The assistant's global setting selects that service mode.
/// </summary>
public sealed class NoteProcessingModeTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public NoteProcessingModeTests(SqliteTestFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------
    // Verbatim / light polish / clarify
    // ------------------------------------------------------------------

    [Fact]
    public async Task Verbatim_capture_makes_no_provider_call_and_keeps_the_raw_words()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        // A provider that would throw if touched: verbatim must never reach it.
        h.Llm.Failure = LlmException.ProviderFailure("the processor must not be called for verbatim");

        var result = await h.Service.CaptureAsync(
            new CaptureNoteRequest(book.Id, "so anyway i was thinking", ProcessingMode: "verbatim"));

        result.Success.Should().BeTrue();
        h.Llm.CallCount.Should().Be(0);

        var dto = result.Value!;
        dto.Content.Should().Be("so anyway i was thinking");
        dto.RawContent.Should().BeNull();
        dto.ProcessingMode.Should().Be("verbatim");
    }

    [Fact]
    public async Task Light_polish_processes_the_raw_transcript_and_keeps_it()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        h.Llm.Returns("I was thinking.");

        var result = await h.Service.CaptureAsync(
            new CaptureNoteRequest(book.Id, "so anyway i was like thinking", ProcessingMode: "light_polish"));

        result.Success.Should().BeTrue();
        h.Llm.CallCount.Should().Be(1);
        h.Llm.LastRequest.Messages[1].Content.Should().Be("so anyway i was like thinking");

        var dto = result.Value!;
        dto.Content.Should().Be("I was thinking.");
        dto.RawContent.Should().Be("so anyway i was like thinking");
        dto.ProcessingMode.Should().Be("light_polish");
    }

    [Fact]
    public async Task Processing_uses_RawContent_when_the_caller_supplies_one()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        h.Llm.Returns("Clarified.");

        var result = await h.Service.CaptureAsync(
            new CaptureNoteRequest(
                book.Id,
                Content: "an already tidy sentence",
                RawContent: "the actual raw words the user spoke",
                ProcessingMode: "clarify"));

        result.Success.Should().BeTrue();
        h.Llm.LastRequest.Messages[1].Content.Should().Be("the actual raw words the user spoke");

        var dto = result.Value!;
        dto.Content.Should().Be("Clarified.");
        dto.RawContent.Should().Be("the actual raw words the user spoke");
        dto.ProcessingMode.Should().Be("clarify");
    }

    // ------------------------------------------------------------------
    // Reprocess always starts from RawContent
    // ------------------------------------------------------------------

    [Fact]
    public async Task Reprocessing_always_starts_from_RawContent_and_is_idempotent()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        h.Llm.Returns("POLISHED");
        var created = await h.Service.CaptureAsync(
            new CaptureNoteRequest(book.Id, "raw words", ProcessingMode: "light_polish"));
        var id = created.Value!.Id;
        created.Value!.Content.Should().Be("POLISHED");

        // Second pass, a DIFFERENT mode: it must read the raw transcript, never
        // the "POLISHED" prose the first pass produced.
        h.Llm.Returns("CLARIFIED");
        var clarified = await h.Service.ReprocessAsync(id, "clarify");
        clarified.Success.Should().BeTrue();
        h.Llm.LastRequest.Messages[1].Content.Should().Be("raw words");
        clarified.Value!.Content.Should().Be("CLARIFIED");
        clarified.Value!.RawContent.Should().Be("raw words");

        // Third pass, back to the first mode: again from the raw transcript. A
        // transform-of-a-transform would have sent "CLARIFIED".
        h.Llm.Returns("POLISHED AGAIN");
        var again = await h.Service.ReprocessAsync(id, "light_polish");
        h.Llm.LastRequest.Messages[1].Content.Should().Be("raw words");
        again.Value!.Content.Should().Be("POLISHED AGAIN");
        again.Value!.RawContent.Should().Be("raw words");
    }

    [Fact]
    public async Task A_verbatim_reprocess_restores_from_the_raw_transcript_without_a_provider_call()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        h.Llm.Returns("POLISHED");
        var created = await h.Service.CaptureAsync(
            new CaptureNoteRequest(book.Id, "raw words", ProcessingMode: "light_polish"));
        var callsAfterCapture = h.Llm.CallCount;

        var verbatim = await h.Service.ReprocessAsync(created.Value!.Id, "verbatim");

        verbatim.Success.Should().BeTrue();
        verbatim.Value!.Content.Should().Be("raw words");
        verbatim.Value!.ProcessingMode.Should().Be("verbatim");
        h.Llm.CallCount.Should().Be(callsAfterCapture, "verbatim is a no-op that never calls the provider");
    }

    [Fact]
    public async Task An_unknown_reprocess_mode_is_refused_rather_than_silently_downgraded()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        var created = await h.Service.CaptureAsync(new CaptureNoteRequest(book.Id, "raw words"));

        var result = await h.Service.ReprocessAsync(created.Value!.Id, "make_it_fancy");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(NoteErrorCodes.InvalidProcessingMode);
        h.Llm.CallCount.Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Quotes are never rewritten
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_quote_only_capture_is_a_no_op_for_every_mode()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        h.Llm.Failure = LlmException.ProviderFailure("a quote-only capture must never call the processor");

        const string quote = "The snow was general all over Ireland.";

        foreach (var mode in new[] { "light_polish", "clarify" })
        {
            var result = await h.Service.CaptureAsync(
                new CaptureNoteRequest(
                    book.Id,
                    Content: string.Empty,
                    SelectedText: quote,
                    ProcessingMode: mode));

            result.Success.Should().BeTrue();
            var dto = result.Value!;
            dto.SelectedText.Should().Be(quote, "no mode may rewrite the quoted passage");
            dto.Content.Should().BeEmpty();
            dto.ProcessingMode.Should().Be("verbatim", "nothing was processed, so the honest mode is verbatim");
        }

        h.Llm.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Only_the_users_thought_reaches_the_provider_never_the_quote()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        h.Llm.Returns("I think this matters.");

        const string quote = "The snow was general all over Ireland.";
        var result = await h.Service.CaptureAsync(
            new CaptureNoteRequest(
                book.Id,
                Content: "i think this matters",
                SelectedText: quote,
                ProcessingMode: "light_polish"));

        result.Success.Should().BeTrue();
        h.Llm.CallCount.Should().Be(1);

        var sentToProvider = h.Llm.LastRequest.Messages[1].Content!;
        sentToProvider.Should().Be("i think this matters");
        sentToProvider.Should().NotContain("snow", "the quote is never part of the prompt");

        result.Value!.SelectedText.Should().Be(quote);
        result.Value!.Content.Should().Be("I think this matters.");
    }

    // ------------------------------------------------------------------
    // Raw readable / restorable
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_raw_transcript_is_retrievable_and_restorable()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        h.Llm.Returns("POLISHED");
        var created = await h.Service.CaptureAsync(
            new CaptureNoteRequest(book.Id, "raw words", ProcessingMode: "light_polish"));
        var id = created.Value!.Id;

        var raw = await h.Service.GetRawAsync(id);
        raw.Success.Should().BeTrue();
        raw.Value!.RawContent.Should().Be("raw words");
        raw.Value!.Content.Should().Be("POLISHED");
        raw.Value!.ProcessingMode.Should().Be("light_polish");

        var restored = await h.Service.RestoreRawAsync(id);
        restored.Success.Should().BeTrue();
        restored.Value!.Content.Should().Be("raw words");
        restored.Value!.ProcessingMode.Should().Be("verbatim");
        restored.Value!.RawContent.Should().Be("raw words", "restoring never erases the stored raw transcript");

        var after = await h.Service.GetRawAsync(id);
        after.Value!.Content.Should().Be("raw words");
        after.Value!.ProcessingMode.Should().Be("verbatim");
    }

    [Fact]
    public async Task Restoring_a_note_that_kept_no_raw_transcript_is_a_typed_refusal()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        var created = await h.Service.CaptureAsync(new CaptureNoteRequest(book.Id, "a plain typed note"));

        var result = await h.Service.RestoreRawAsync(created.Value!.Id);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(NoteErrorCodes.NoRawTranscript);
    }

    [Fact]
    public async Task A_provider_failure_during_capture_never_loses_the_thought()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        h.Llm.Failure = LlmException.ProviderFailure("the pool is down");

        var result = await h.Service.CaptureAsync(
            new CaptureNoteRequest(book.Id, "the words that must survive", ProcessingMode: "clarify"));

        result.Success.Should().BeTrue();
        result.Value!.Content.Should().Be("the words that must survive");
        result.Value!.RawContent.Should().Be("the words that must survive");
        result.Value!.ProcessingMode.Should().Be("verbatim", "nothing was processed, so the honest mode is verbatim");
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private Harness CreateHarness()
    {
        var path = _fixture.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        var db = new NostosDbContext(options);
        db.Database.EnsureCreated();

        var concepts = new ConceptRepository(db);
        var llm = new FakeLlmProvider();
        var service = new NoteService(
            new NoteRepository(db),
            new BookRepository(db),
            concepts,
            new NoteProcessorService(concepts),
            new ThoughtProcessor(llm),
            db,
            NullLogger<NoteService>.Instance);

        return new Harness(db, service, llm);
    }

    private static async Task<PhysicalBookModel> SeedBookAsync(Harness h)
    {
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "A Book" };
        h.Db.Books.Add(book);
        await h.Db.SaveChangesAsync();
        return book;
    }

    private sealed class Harness(
        NostosDbContext db,
        NoteService service,
        FakeLlmProvider llm) : IDisposable
    {
        public NostosDbContext Db { get; } = db;
        public NoteService Service { get; } = service;
        public FakeLlmProvider Llm { get; } = llm;
        public void Dispose() => Db.Dispose();
    }
}
