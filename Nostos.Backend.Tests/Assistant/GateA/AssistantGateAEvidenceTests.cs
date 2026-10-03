using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.Assistant.GateA;

/// <summary>
/// Gate A cases 11, 16 and 17: exact evidence handles (issue #566).
///
/// A #565 evidence handle re-reads canonical Nostos data; a stale handle
/// (mismatched revision hash, extractor version or ordinal) fails closed —
/// it never silently returns mismatched data. These tests drive the REAL
/// <see cref="KnowledgeRetrievalService"/> over a REAL book-text index, both
/// directly and through the <c>knowledge_read_evidence</c> tool, with no
/// model involved.
/// </summary>
public sealed class AssistantGateAEvidenceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantGateAEvidenceTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Exact_pdf_handle_rereads_canonical_text_with_grounded_locators()
    {
        await using var h = await CreateHarnessAsync();
        var book = await h.SeedBookAsync("Imported PDF");
        var hash = new string('d', 64);
        var chunk = await h.IndexReadyChunkAsync(
            book.Id,
            hash,
            BookTextSourceFormat.Pdf,
            ordinal: 5,
            text: "Unified retrieval keeps exact source provenance.",
            segments:
            [
                new BookTextSourceSegment(
                    0,
                    47,
                    new PdfBookTextSourceLocator(14, "15", 200, 247)),
            ]);

        var handle = new KnowledgeEvidenceHandle(
            KnowledgeEvidenceKinds.BookText,
            BookId: book.Id,
            SourceSha256: hash,
            ExtractorVersion: BookTextArtifactSchema.CurrentExtractorVersion,
            Ordinal: 5);

        // Gate A case 11: the exact handle re-reads canonical data.
        var reread = await h.Knowledge.ReadAsync(handle);
        reread.Should().NotBeNull();
        reread!.Handle.Should().Be(handle);
        reread.BookPassage!.Text.Should().Be(chunk.Text);
        reread.BookPassage.SourceSegments.Should().BeEquivalentTo(chunk.SourceSegments);

        // Gate A case 16: the same handle through the tool preserves the
        // exact PDF page index/label provenance for the turn.
        var tool = await h.ReadThroughToolAsync(
            $$"""{"kind":"book_text","bookId":"{{book.Id}}","sourceSha256":"{{hash}}","extractorVersion":"{{BookTextArtifactSchema.CurrentExtractorVersion}}","ordinal":5}""");

        tool.Success.Should().BeTrue();
        var raw = tool.Data!.Value.GetRawText();
        raw.Should().Contain("Unified retrieval keeps exact source provenance");
        raw.Should().Contain("\"type\":\"pdf\"");
        raw.Should().Contain("\"pageIndex\":14");
        raw.Should().Contain("\"pageLabel\":\"15\"");
    }

    [Fact]
    public async Task Exact_epub_handle_reread_preserves_cfi_provenance()
    {
        await using var h = await CreateHarnessAsync();
        var book = await h.SeedBookAsync("Imported EPUB");
        var hash = new string('e', 64);
        var chunk = await h.IndexReadyChunkAsync(
            book.Id,
            hash,
            BookTextSourceFormat.Epub,
            ordinal: 2,
            text: "The shipped chapter argues for attention.",
            segments:
            [
                new BookTextSourceSegment(
                    0,
                    39,
                    new EpubBookTextSourceLocator(2, "chapter-2.xhtml", "epubcfi(/6/8!/4/2:0)", 0, 39)),
            ]);

        var handle = new KnowledgeEvidenceHandle(
            KnowledgeEvidenceKinds.BookText,
            BookId: book.Id,
            SourceSha256: hash,
            ExtractorVersion: BookTextArtifactSchema.CurrentExtractorVersion,
            Ordinal: 2);
        var reread = await h.Knowledge.ReadAsync(handle);
        reread.Should().NotBeNull();
        reread!.BookPassage!.SourceSegments.Should().BeEquivalentTo(chunk.SourceSegments);

        var tool = await h.ReadThroughToolAsync(
            $$"""{"kind":"book_text","bookId":"{{book.Id}}","sourceSha256":"{{hash}}","extractorVersion":"{{BookTextArtifactSchema.CurrentExtractorVersion}}","ordinal":2}""");

        tool.Success.Should().BeTrue();
        var raw = tool.Data!.Value.GetRawText();
        raw.Should().Contain("\"type\":\"epub\"");
        raw.Should().Contain("\"cfi\":\"epubcfi(/6/8!/4/2:0)\"");
        raw.Should().Contain("\"resourceHref\":\"chapter-2.xhtml\"");
    }

    [Fact]
    public async Task Extractor_version_mismatch_fails_closed()
    {
        await using var h = await CreateHarnessAsync();
        var book = await h.SeedBookAsync("Versioned PDF");
        var hash = new string('f', 64);
        _ = await h.IndexReadyChunkAsync(
            book.Id,
            hash,
            BookTextSourceFormat.Pdf,
            ordinal: 0,
            text: "Extractor-bound text.",
            segments:
            [
                new BookTextSourceSegment(
                    0,
                    19,
                    new PdfBookTextSourceLocator(3, "4", 0, 19)),
            ]);

        // Gate A case 17: the revision hash matches but the extractor that
        // produced the indexed text is not the one the handle names.
        var stale = new KnowledgeEvidenceHandle(
            KnowledgeEvidenceKinds.BookText,
            BookId: book.Id,
            SourceSha256: hash,
            ExtractorVersion: "nostos-book-text-v1",
            Ordinal: 0);
        (await h.Knowledge.ReadAsync(stale)).Should().BeNull();

        var tool = await h.ReadThroughToolAsync(
            $$"""{"kind":"book_text","bookId":"{{book.Id}}","sourceSha256":"{{hash}}","extractorVersion":"nostos-book-text-v1","ordinal":0}""");

        tool.Success.Should().BeFalse();
        tool.ErrorCode.Should().Be(AssistantErrorCodes.NotFound);
        tool.Data.Should().BeNull("a stale handle never returns mismatched data");
    }

    [Fact]
    public async Task Ordinal_mismatch_fails_closed()
    {
        await using var h = await CreateHarnessAsync();
        var book = await h.SeedBookAsync("Ordinal PDF");
        var hash = new string('a', 64);
        _ = await h.IndexReadyChunkAsync(
            book.Id,
            hash,
            BookTextSourceFormat.Pdf,
            ordinal: 0,
            text: "First chunk text.",
            segments:
            [
                new BookTextSourceSegment(
                    0,
                    17,
                    new PdfBookTextSourceLocator(3, "4", 0, 17)),
            ]);

        // Gate A case 17: revision and extractor match, but the indexed
        // ordinal no longer exists.
        var stale = new KnowledgeEvidenceHandle(
            KnowledgeEvidenceKinds.BookText,
            BookId: book.Id,
            SourceSha256: hash,
            ExtractorVersion: BookTextArtifactSchema.CurrentExtractorVersion,
            Ordinal: 7);
        (await h.Knowledge.ReadAsync(stale)).Should().BeNull();

        var tool = await h.ReadThroughToolAsync(
            $$"""{"kind":"book_text","bookId":"{{book.Id}}","sourceSha256":"{{hash}}","extractorVersion":"{{BookTextArtifactSchema.CurrentExtractorVersion}}","ordinal":7}""");

        tool.Success.Should().BeFalse();
        tool.ErrorCode.Should().Be(AssistantErrorCodes.NotFound);
        tool.Data.Should().BeNull();
    }

    [Fact]
    public async Task Replaced_revision_fails_closed_through_the_tool()
    {
        await using var h = await CreateHarnessAsync();
        var book = await h.SeedBookAsync("Replaced PDF");
        var hash = new string('b', 64);
        await h.IndexReadyChunkAsync(
            book.Id,
            hash,
            BookTextSourceFormat.Pdf,
            ordinal: 0,
            text: "Original revision text.",
            segments:
            [
                new BookTextSourceSegment(
                    0,
                    23,
                    new PdfBookTextSourceLocator(8, "9", 0, 23)),
            ]);

        var args =
            $$"""{"kind":"book_text","bookId":"{{book.Id}}","sourceSha256":"{{hash}}","extractorVersion":"{{BookTextArtifactSchema.CurrentExtractorVersion}}","ordinal":0}""";
        (await h.ReadThroughToolAsync(args)).Success.Should().BeTrue();

        // Replacing/scheduling the source invalidates the old revision
        // immediately; the historical handle cannot resurrect stale text.
        await h.BookTextIndex.ScheduleAsync(book.Id, "replacement.pdf", BookTextSourceFormat.Pdf);

        var stale = await h.ReadThroughToolAsync(args);
        stale.Success.Should().BeFalse();
        stale.ErrorCode.Should().Be(AssistantErrorCodes.NotFound);
        stale.ErrorMessage.Should().Contain("stale");
        stale.Data.Should().BeNull();
    }

    [Fact]
    public async Task Note_handle_reread_returns_canonical_note_through_the_tool()
    {
        await using var h = await CreateHarnessAsync();
        var book = await h.SeedBookAsync("Note Provenance");
        var note = await h.SeedNoteAsync(
            book.Id,
            "A thought worth keeping.",
            selectedText: "A quoted sentence.",
            sourceAnchorKind: "physical_page",
            sourceAnchorValue: "247",
            anchorVerified: false);

        // Gate A case 11 for notes: the handle re-reads the canonical row,
        // including its provenance fields.
        var read = await h.Knowledge.ReadAsync(
            new KnowledgeEvidenceHandle(KnowledgeEvidenceKinds.Note, NoteId: note.Id));
        read.Should().NotBeNull();
        read!.Note!.Content.Should().Be("A thought worth keeping.");
        read.Note.SourceAnchorKind.Should().Be("physical_page");
        read.Note.SourceAnchorValue.Should().Be("247");

        var tool = await h.ReadThroughToolAsync(
            $$"""{"kind":"note","noteId":"{{note.Id}}"}""");
        tool.Success.Should().BeTrue();
        tool.Data!.Value.GetRawText().Should().Contain("A thought worth keeping.");

        var missing = await h.ReadThroughToolAsync(
            $$"""{"kind":"note","noteId":"{{Guid.NewGuid()}}"}""");
        missing.Success.Should().BeFalse();
        missing.ErrorCode.Should().Be(AssistantErrorCodes.NotFound);
    }

    // ------------------------------------------------------------------
    // Harness: real retrieval service + real index + real tool registry
    // ------------------------------------------------------------------

    private async Task<EvidenceHarness> CreateHarnessAsync()
    {
        var path = _fixture.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        await using (var bootstrap = new NostosDbContext(options))
            await bootstrap.Database.EnsureCreatedAsync();

        var factory = new EvidenceContextFactory(options);
        var db = new NostosDbContext(options);
        var topics = new TopicRepository(db);
        var noteRepository = new NoteRepository(db);
        var noteService = new NoteService(
            noteRepository,
            new BookRepository(db),
            topics,
            new NoteProcessorService(topics),
            new FakeThoughtProcessor(),
            db,
            NullLogger<NoteService>.Instance);

        var library = new LibraryService(
            factory,
            new BookLookupService(
                new StubHttpClientFactory(),
                NullLogger<BookLookupService>.Instance));

        var index = new SqliteBookTextIndex(factory);
        await index.EnsureSchemaAsync();
        var bookSearch = new BookTextSearchService(index, library, new BookTextOptions());
        var knowledge = new KnowledgeRetrievalService(
            noteService,
            noteRepository,
            topics,
            library,
            bookSearch,
            index,
            []);
        var registry = new AssistantCapabilityRegistry(
            AssistantCapabilities.Build(noteService, library, topics, knowledge, bookSearch));

        return new EvidenceHarness(db, factory, index, knowledge, registry);
    }

    private sealed class EvidenceHarness(
        NostosDbContext db,
        IDbContextFactory<NostosDbContext> factory,
        SqliteBookTextIndex bookTextIndex,
        KnowledgeRetrievalService knowledge,
        AssistantCapabilityRegistry registry) : IAsyncDisposable
    {
        public SqliteBookTextIndex BookTextIndex { get; } = bookTextIndex;
        public KnowledgeRetrievalService Knowledge { get; } = knowledge;

        public async Task<PhysicalBookModel> SeedBookAsync(string title)
        {
            var book = new PhysicalBookModel
            {
                Id = Guid.NewGuid(),
                Title = title,
                Author = "Author",
            };
            db.Books.Add(book);
            await db.SaveChangesAsync();
            return book;
        }

        public async Task<NoteModel> SeedNoteAsync(
            Guid bookId,
            string content,
            string? selectedText = null,
            string sourceAnchorKind = "unknown",
            string? sourceAnchorValue = null,
            bool anchorVerified = false)
        {
            var note = new NoteModel
            {
                Id = Guid.NewGuid(),
                BookId = bookId,
                Content = content,
                SelectedText = selectedText,
                SourceAnchorKind = sourceAnchorKind,
                SourceAnchorValue = sourceAnchorValue,
                AnchorVerified = anchorVerified,
                CreatedAt = DateTime.UtcNow,
            };
            db.Notes.Add(note);
            await db.SaveChangesAsync();
            return note;
        }

        public async Task<BookTextIndexedChunk> IndexReadyChunkAsync(
            Guid bookId,
            string hash,
            BookTextSourceFormat format,
            int ordinal,
            string text,
            IReadOnlyList<BookTextSourceSegment> segments)
        {
            var revision = new BookTextSourceRevision(
                bookId,
                hash,
                BookTextArtifactSchema.CurrentExtractorVersion,
                format);

            await bookTextIndex.ScheduleAsync(bookId, "source.pdf", format);
            var work = await bookTextIndex.TryClaimNextAsync(TimeSpan.FromMinutes(15));
            work.Should().NotBeNull();

            var chunk = new BookTextIndexedChunk(
                BookTextIdentity.ChunkId(revision, ordinal),
                bookId,
                hash,
                revision.ExtractorVersion,
                format,
                ordinal,
                text,
                ["Chapter 1"],
                segments);

            (await bookTextIndex.ReplaceReadyAsync(
                revision,
                [chunk],
                chunk.Text.Length,
                work!.Attempt)).Should().BeTrue();

            return chunk;
        }

        public async Task<AssistantToolResult> ReadThroughToolAsync(string argumentsJson)
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return await registry.InvokeAsync(
                "knowledge_read_evidence",
                document.RootElement,
                new AssistantToolContext(ClientId: "gate-a", IdempotencyKey: "gate-a-evidence"));
        }

        public async ValueTask DisposeAsync()
        {
            db.Dispose();
            await ValueTask.CompletedTask;
        }
    }

    private sealed class EvidenceContextFactory(DbContextOptions<NostosDbContext> options)
        : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);

        public Task<NostosDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new NostosDbContext(options));
    }
}
