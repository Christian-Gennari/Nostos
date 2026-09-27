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
/// Canonical note service (issue #260 §1, §5): concept processing on create,
/// capture provenance, and the two capabilities #261 needs — linking a note to
/// an existing concept and reading it back for review.
/// </summary>
public sealed class NoteServiceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public NoteServiceTests(SqliteTestFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------
    // Create: concept processing + provenance
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_still_processes_wikilinks_into_concepts()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h, "A Book");

        var result = await h.Service.CreateAsync(
            book.Id, new CreateNoteDto("Reading about [[Virtue]] and [[Courage]]."));

        result.Success.Should().BeTrue();
        result.Value!.BookTitle.Should().Be("A Book");
        (await h.Db.Concepts.Select(c => c.Concept).OrderBy(name => name).ToListAsync())
            .Should().Equal("Courage", "Virtue");
        (await h.Db.NoteConcepts.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Create_for_unknown_book_returns_book_not_found()
    {
        var h = CreateHarness();
        using var _ = h;
        var bookId = Guid.NewGuid();

        var result = await h.Service.CreateAsync(bookId, new CreateNoteDto("a note"));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("book_not_found");
        result.ErrorMessage.Should().Be($"Book {bookId} not found.");
        (await h.Db.Notes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_without_content_or_selected_text_returns_empty_note()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        var result = await h.Service.CreateAsync(book.Id, new CreateNoteDto("   "));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("empty_note");
        (await h.Db.Notes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_captured_note_keeps_raw_content_while_content_differs()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        // The provider returns the polished text; the assertions below then pin
        // that Content is the processed result while RawContent keeps the words.
        h.Thoughts.Responder = (_, mode) =>
            new ThoughtProcessingResult("Virtue is a settled disposition.", mode, ProviderCalled: true);

        var result = await h.Service.CreateAsync(
            book.Id,
            new CreateNoteDto(
                Content: "Virtue is a settled disposition.",
                RawContent: "so virtue is like a hexis, a settled disposition...",
                CaptureSource: "voice",
                ProcessingMode: "light_polish",
                SourceAnchorKind: "epub_cfi",
                SourceAnchorValue: "epubcfi(/6/4!/4/2/2)",
                AnchorVerified: true));

        result.Success.Should().BeTrue();

        // The processor was handed the RAW transcript, never the incoming
        // (already-processed-looking) Content.
        h.Thoughts.LastText.Should().Be("so virtue is like a hexis, a settled disposition...");
        h.Thoughts.Calls[^1].Mode.Should().Be("light_polish");

        var dto = result.Value!;
        dto.Content.Should().Be("Virtue is a settled disposition.");
        dto.RawContent.Should().Be("so virtue is like a hexis, a settled disposition...");
        dto.CaptureSource.Should().Be("voice");
        dto.ProcessingMode.Should().Be("light_polish");
        dto.SourceAnchorKind.Should().Be("epub_cfi");
        dto.SourceAnchorValue.Should().Be("epubcfi(/6/4!/4/2/2)");
        dto.AnchorVerified.Should().BeTrue();

        // The raw capture is a second column, never an overwrite of Content.
        var stored = await h.Db.Notes.AsNoTracking().SingleAsync(n => n.Id == dto.Id);
        stored.Content.Should().Be("Virtue is a settled disposition.");
        stored.RawContent.Should().Be("so virtue is like a hexis, a settled disposition...");
    }

    [Fact]
    public async Task A_plain_note_defaults_to_text_verbatim_unknown()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);

        var result = await h.Service.CreateAsync(book.Id, new CreateNoteDto("plain typed note"));

        var dto = result.Value!;
        dto.CaptureSource.Should().Be("text");
        dto.ProcessingMode.Should().Be("verbatim");
        dto.SourceAnchorKind.Should().Be("unknown");
        dto.SourceAnchorValue.Should().BeNull();
        dto.AnchorVerified.Should().BeFalse();
        dto.RawContent.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Link to an existing concept
    // ------------------------------------------------------------------

    [Fact]
    public async Task Link_to_existing_concept_is_idempotent_and_creates_no_concept()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);
        var note = await SeedNoteAsync(h, book.Id, "a note");
        var concept = await SeedConceptAsync(h, "virtue");

        var first = await h.Service.LinkToExistingConceptAsync(note.Id, concept.Id);
        first.Success.Should().BeTrue();
        first.Value!.Id.Should().Be(note.Id);

        var second = await h.Service.LinkToExistingConceptAsync(note.Id, concept.Id);
        second.Success.Should().BeTrue();
        second.Value!.Content.Should().Be("a note\n\n[[virtue]]");

        (await h.Db.NoteConcepts.CountAsync()).Should().Be(1,
            "the join is keyed (NoteId, ConceptId); a repeat link is a no-op");
        (await h.Db.Concepts.CountAsync()).Should().Be(1,
            "linking never creates a concept to satisfy the link");

        // An ordinary edit reprocesses membership from the note body. The
        // accepted assistant link must remain there after that reprocessing.
        var edited = await h.Service.UpdateAsync(note.Id, new UpdateNoteDto(
            second.Value.Content.Replace("a note", "an edited note"), null));
        edited.Success.Should().BeTrue();
        (await h.Db.NoteConcepts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Link_unknown_concept_returns_concept_not_found()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);
        var note = await SeedNoteAsync(h, book.Id, "a note");

        var result = await h.Service.LinkToExistingConceptAsync(note.Id, Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("concept_not_found");
        (await h.Db.NoteConcepts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Link_unknown_note_returns_note_not_found()
    {
        var h = CreateHarness();
        using var _ = h;
        var concept = await SeedConceptAsync(h, "virtue");

        var result = await h.Service.LinkToExistingConceptAsync(Guid.NewGuid(), concept.Id);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("note_not_found");
    }

    [Fact]
    public async Task Link_keeps_the_notes_other_concept_links()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h);
        var note = await SeedNoteAsync(h, book.Id, "a note");
        var first = await SeedConceptAsync(h, "courage");
        var second = await SeedConceptAsync(h, "virtue");
        h.Db.NoteConcepts.Add(new NoteConceptModel { NoteId = note.Id, ConceptId = first.Id });
        await h.Db.SaveChangesAsync();

        var result = await h.Service.LinkToExistingConceptAsync(note.Id, second.Id);

        result.Success.Should().BeTrue();
        result.Value!.Content.Should().Contain("[[courage]]").And.Contain("[[virtue]]");
        var linked = await h.Db.NoteConcepts.Where(nc => nc.NoteId == note.Id)
            .Select(nc => nc.ConceptId).ToListAsync();
        linked.Should().BeEquivalentTo(new[] { first.Id, second.Id });

        await h.Service.UpdateAsync(note.Id, new UpdateNoteDto(result.Value.Content + "\nAn edit"));
        (await h.Db.NoteConcepts.Where(nc => nc.NoteId == note.Id)
            .Select(nc => nc.ConceptId).ToListAsync()).Should().BeEquivalentTo(new[] { first.Id, second.Id });
    }

    // ------------------------------------------------------------------
    // Review read
    // ------------------------------------------------------------------

    [Fact]
    public async Task Review_read_returns_text_book_title_and_linked_concept_names()
    {
        var h = CreateHarness();
        using var _ = h;
        var book = await SeedBookAsync(h, "A Book");
        var note = await SeedNoteAsync(h, book.Id, "Some content", "a quoted passage");
        var alpha = await SeedConceptAsync(h, "alpha");
        var beta = await SeedConceptAsync(h, "beta");
        h.Db.NoteConcepts.AddRange(
            new NoteConceptModel { NoteId = note.Id, ConceptId = alpha.Id },
            new NoteConceptModel { NoteId = note.Id, ConceptId = beta.Id });
        await h.Db.SaveChangesAsync();

        var review = await h.Service.GetForReviewAsync(note.Id);

        review.Should().NotBeNull();
        review!.NoteId.Should().Be(note.Id);
        review.BookId.Should().Be(book.Id);
        review.BookTitle.Should().Be("A Book");
        review.Content.Should().Be("Some content");
        review.SelectedText.Should().Be("a quoted passage");
        review.ConceptNames.Should().Equal("alpha", "beta");
    }

    [Fact]
    public async Task Review_read_returns_null_for_an_unknown_note()
    {
        var h = CreateHarness();
        using var _ = h;

        (await h.Service.GetForReviewAsync(Guid.NewGuid())).Should().BeNull();
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

        // The same scoped context backs every repository and the processor, so
        // create/update stay one unit of work exactly as in the web host.
        var concepts = new ConceptRepository(db);
        var thoughts = new FakeThoughtProcessor();
        var service = new NoteService(
            new NoteRepository(db),
            new BookRepository(db),
            concepts,
            new NoteProcessorService(concepts),
            thoughts,
            db,
            NullLogger<NoteService>.Instance);

        return new Harness(db, service, thoughts);
    }

    private static async Task<PhysicalBookModel> SeedBookAsync(Harness h, string title = "A Book")
    {
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = title };
        h.Db.Books.Add(book);
        await h.Db.SaveChangesAsync();
        return book;
    }

    private static async Task<NoteModel> SeedNoteAsync(
        Harness h, Guid bookId, string content, string? selectedText = null)
    {
        var note = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Content = content,
            SelectedText = selectedText,
            CreatedAt = DateTime.UtcNow,
        };
        h.Db.Notes.Add(note);
        await h.Db.SaveChangesAsync();
        return note;
    }

    private static async Task<ConceptModel> SeedConceptAsync(Harness h, string name)
    {
        var concept = new ConceptModel { Id = Guid.NewGuid(), Concept = name };
        h.Db.Concepts.Add(concept);
        await h.Db.SaveChangesAsync();
        return concept;
    }

    private sealed class Harness(
        NostosDbContext db,
        NoteService service,
        FakeThoughtProcessor thoughts) : IDisposable
    {
        public NostosDbContext Db { get; } = db;
        public NoteService Service { get; } = service;
        public FakeThoughtProcessor Thoughts { get; } = thoughts;
        public void Dispose() => Db.Dispose();
    }
}
