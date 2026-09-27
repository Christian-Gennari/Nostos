using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.Services.Knowledge;

public sealed class KnowledgeRetrievalServiceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public KnowledgeRetrievalServiceTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Low_overlap_query_recovers_relevant_note_and_ranks_it_above_single_token_distractor()
    {
        using var h = CreateHarness();
        var book = await SeedBookAsync(h, "A Book");

        var relevant = await SeedNoteAsync(
            h,
            book.Id,
            "Social pressure can produce conformity even when private judgment resists.");
        var distractor = await SeedNoteAsync(
            h,
            book.Id,
            "Social gatherings can be restorative after a long week.");

        var oldWholePhraseBaseline = await h.NoteRepository.SearchByTextAsync(
            "social conformity",
            20);
        oldWholePhraseBaseline.Should().BeEmpty(
            "the pre-#562 whole-query LIKE baseline cannot cross words between 'social' and 'conformity'");

        var improved = await h.NoteService.SearchAsync("social conformity", 20);

        improved.Should().NotBeEmpty();
        improved[0].Id.Should().Be(relevant.Id,
            "the relevant note matches both decomposed content words");
        improved.Select(hit => hit.Id).Should().Contain(distractor.Id,
            "single-token distractors are still eligible lexical evidence");
        improved.ToList().FindIndex(hit => hit.Id == relevant.Id)
            .Should().BeLessThan(improved.ToList().FindIndex(hit => hit.Id == distractor.Id));
    }

    [Fact]
    public async Task Concept_evidence_uses_the_same_multi_query_baseline_and_rewards_multi_token_support()
    {
        using var h = CreateHarness();
        var book = await SeedBookAsync(h, "A Book");
        var relevant = await SeedNoteAsync(
            h,
            book.Id,
            "Social pressure can produce conformity even when private judgment resists.");
        var distractor = await SeedNoteAsync(
            h,
            book.Id,
            "Social gatherings can be restorative after a long week.");

        var relevantConcept = await SeedConceptAsync(h, "Collective pressure");
        var distractorConcept = await SeedConceptAsync(h, "Sociability");
        h.Db.NoteConcepts.AddRange(
            new NoteConceptModel { NoteId = relevant.Id, ConceptId = relevantConcept.Id },
            new NoteConceptModel { NoteId = distractor.Id, ConceptId = distractorConcept.Id });
        await h.Db.SaveChangesAsync();

        var results = await h.Concepts.SearchByNoteTextAsync(
            "social conformity",
            bookIds: null,
            limit: 10);

        results.Should().NotBeEmpty();
        results[0].Id.Should().Be(relevantConcept.Id);
        results.Select(result => result.Id).Should().Contain(distractorConcept.Id);
    }

    [Fact]
    public async Task Unified_search_preserves_scope_and_returns_exact_reread_handles()
    {
        using var h = CreateHarness();
        var scoped = await SeedBookAsync(h, "Scoped Book");
        var outside = await SeedBookAsync(h, "Outside Book");

        var scopedNote = await SeedNoteAsync(
            h,
            scoped.Id,
            "Freedom is inseparable from responsibility in this passage.",
            sourceAnchorKind: "physical_page",
            sourceAnchorValue: "42",
            anchorVerified: false);
        await SeedNoteAsync(
            h,
            outside.Id,
            "Freedom and responsibility appear here too.");

        var concept = await SeedConceptAsync(h, "Responsibility");
        h.Db.NoteConcepts.Add(new NoteConceptModel
        {
            NoteId = scopedNote.Id,
            ConceptId = concept.Id,
        });
        await h.Db.SaveChangesAsync();

        var hash = new string('a', 64);
        var text = "Freedom requires accepting responsibility for one's choices.";
        var chunk = new BookTextIndexedChunk(
            Guid.NewGuid(),
            scoped.Id,
            hash,
            BookTextArtifactSchema.CurrentExtractorVersion,
            BookTextSourceFormat.Pdf,
            3,
            text,
            ["Part I", "Freedom"],
            [
                new BookTextSourceSegment(
                    0,
                    text.Length,
                    new PdfBookTextSourceLocator(
                        PageIndex: 7,
                        PageLabel: "8",
                        StartTextOffset: 0,
                        EndTextOffset: text.Length)),
            ]);
        h.BookTextIndex.AddReadyChunk(chunk);

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest(
                "freedom responsibility",
                BookIds: [scoped.Id],
                MaxPerSource: 6));

        result.EvidenceAvailable.Should().BeTrue();
        result.QueryVariants.Should().Contain("freedom");
        result.QueryVariants.Should().Contain("responsibility");

        result.Notes.Should().ContainSingle(note => note.NoteId == scopedNote.Id);
        result.Notes.Should().OnlyContain(note => note.BookId == scoped.Id);

        result.Concepts.Should().ContainSingle(item => item.ConceptId == concept.Id);
        result.Concepts.Single().SupportingNotes.Should()
            .OnlyContain(note => note.BookId == scoped.Id);

        result.BookPassages.Should().ContainSingle();
        var passage = result.BookPassages.Single();
        passage.BookId.Should().Be(scoped.Id);
        passage.SourceSha256.Should().Be(hash);
        passage.Ordinal.Should().Be(3);

        var noteReread = await h.Knowledge.ReadAsync(result.Notes.Single().Handle);
        noteReread.Should().NotBeNull();
        noteReread!.Note!.Content.Should().Be(scopedNote.Content);
        noteReread.Note.SourceAnchorKind.Should().Be("physical_page");
        noteReread.Note.SourceAnchorValue.Should().Be("42");

        var conceptReread = await h.Knowledge.ReadAsync(result.Concepts.Single().Handle);
        conceptReread.Should().NotBeNull();
        conceptReread!.Concept!.Name.Should().Be("Responsibility");
        conceptReread.Concept.Notes.Should().Contain(note => note.NoteId == scopedNote.Id);

        var bookReread = await h.Knowledge.ReadAsync(passage.Handle);
        bookReread.Should().NotBeNull();
        bookReread!.BookPassage!.Text.Should().Be(text);
        bookReread.BookPassage.SourceSha256.Should().Be(hash);
        bookReread.BookPassage.SourceSegments.Should().ContainSingle();

        var stale = passage.Handle with { SourceSha256 = new string('b', 64) };
        (await h.Knowledge.ReadAsync(stale)).Should().BeNull(
            "book evidence handles are bound to the exact source revision");
    }

    [Fact]
    public async Task Optional_contributor_candidates_are_re_resolved_and_cannot_escape_explicit_book_scope()
    {
        using var h = CreateHarness();
        var scoped = await SeedBookAsync(h, "Scoped");
        var outside = await SeedBookAsync(h, "Outside");
        var outsideNote = await SeedNoteAsync(
            h,
            outside.Id,
            "A semantic contributor should not smuggle this note into another book scope.");

        h.Contributor.Handles =
        [
            new KnowledgeEvidenceHandle(
                KnowledgeEvidenceKinds.Note,
                NoteId: outsideNote.Id),
        ];

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest(
                "phrase absent from canonical lexical material",
                BookIds: [scoped.Id]));

        result.Notes.Should().BeEmpty(
            "the unified retrieval layer, not the optional contributor, owns explicit scope");
    }

    [Fact]
    public async Task Knowledge_overview_is_structural_bounded_and_complete_on_counts()
    {
        using var h = CreateHarness();
        var first = await SeedBookAsync(h, "First Book");
        var second = await SeedBookAsync(h, "Second Book");

        var n1 = await SeedNoteAsync(h, first.Id, "one");
        var n2 = await SeedNoteAsync(h, first.Id, "two");
        await SeedNoteAsync(h, second.Id, "three");

        for (var i = 1; i <= 15; i++)
        {
            var concept = await SeedConceptAsync(h, $"Concept {i:00}");
            if (i <= 2)
            {
                h.Db.NoteConcepts.Add(new NoteConceptModel
                {
                    NoteId = i == 1 ? n1.Id : n2.Id,
                    ConceptId = concept.Id,
                });
            }
        }
        await h.Db.SaveChangesAsync();

        var overview = await h.Knowledge.OverviewAsync();

        overview.TotalNotes.Should().Be(3);
        overview.UnlinkedNotes.Should().Be(1);
        overview.TotalConcepts.Should().Be(15);
        overview.TotalConceptReferences.Should().Be(2);
        overview.TopConcepts.Should().HaveCount(12);
        overview.TopBooksByNoteCount.Should().HaveCount(2);
        overview.TopBooksByNoteCount[0].BookId.Should().Be(first.Id);
        overview.TopBooksByNoteCount[0].NoteCount.Should().Be(2);
    }

    private Harness CreateHarness()
    {
        var path = _fixture.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        var db = new NostosDbContext(options);
        db.Database.EnsureCreated();
        var factory = new TestContextFactory(options);

        var concepts = new ConceptRepository(db);
        var noteRepository = new NoteRepository(db);
        var noteService = new NoteService(
            noteRepository,
            new BookRepository(db),
            concepts,
            new NoteProcessorService(concepts),
            new FakeThoughtProcessor(),
            db,
            NullLogger<NoteService>.Instance);

        var library = new LibraryService(
            factory,
            new BookLookupService(
                new NoopHttpClientFactory(),
                NullLogger<BookLookupService>.Instance));

        var bookTextIndex = new FakeBookTextIndex();
        var bookText = new BookTextSearchService(
            bookTextIndex,
            library,
            new BookTextOptions());

        var contributor = new StaticKnowledgeContributor();
        var knowledge = new KnowledgeRetrievalService(
            noteService,
            noteRepository,
            concepts,
            library,
            bookText,
            bookTextIndex,
            [contributor]);

        return new Harness(
            db,
            noteRepository,
            noteService,
            concepts,
            bookTextIndex,
            contributor,
            knowledge);
    }

    private static async Task<PhysicalBookModel> SeedBookAsync(Harness h, string title)
    {
        var book = new PhysicalBookModel
        {
            Id = Guid.NewGuid(),
            Title = title,
            Author = "Author",
        };
        h.Db.Books.Add(book);
        await h.Db.SaveChangesAsync();
        return book;
    }

    private static async Task<NoteModel> SeedNoteAsync(
        Harness h,
        Guid bookId,
        string content,
        string sourceAnchorKind = "unknown",
        string? sourceAnchorValue = null,
        bool anchorVerified = false)
    {
        var note = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Content = content,
            CreatedAt = DateTime.UtcNow,
            SourceAnchorKind = sourceAnchorKind,
            SourceAnchorValue = sourceAnchorValue,
            AnchorVerified = anchorVerified,
        };
        h.Db.Notes.Add(note);
        await h.Db.SaveChangesAsync();
        return note;
    }

    private static async Task<ConceptModel> SeedConceptAsync(Harness h, string name)
    {
        var concept = new ConceptModel
        {
            Id = Guid.NewGuid(),
            Concept = name,
        };
        h.Db.Concepts.Add(concept);
        await h.Db.SaveChangesAsync();
        return concept;
    }

    private sealed record Harness(
        NostosDbContext Db,
        NoteRepository NoteRepository,
        NoteService NoteService,
        ConceptRepository Concepts,
        FakeBookTextIndex BookTextIndex,
        StaticKnowledgeContributor Contributor,
        KnowledgeRetrievalService Knowledge) : IDisposable
    {
        public void Dispose() => Db.Dispose();
    }

    private sealed class TestContextFactory(DbContextOptions<NostosDbContext> options)
        : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);

        public Task<NostosDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StaticKnowledgeContributor : IKnowledgeRetrievalContributor
    {
        public string Name => "test";
        public IReadOnlyList<KnowledgeEvidenceHandle> Handles { get; set; } = [];

        public Task<IReadOnlyList<KnowledgeEvidenceHandle>> SearchAsync(
            KnowledgeSearchRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(Handles);
    }

    private sealed class FakeBookTextIndex : IBookTextIndex
    {
        private readonly Dictionary<Guid, BookTextIngestionState> _states = [];
        private readonly List<BookTextSearchHit> _hits = [];
        private readonly List<BookTextIndexedChunk> _chunks = [];

        public void AddReadyChunk(BookTextIndexedChunk chunk, double score = 10)
        {
            _chunks.Add(chunk);
            _hits.Add(new BookTextSearchHit(chunk, score));
            _states[chunk.BookId] = new BookTextIngestionState(
                chunk.BookId,
                BookTextIngestionStatus.Ready,
                "source.pdf",
                chunk.Format,
                chunk.SourceSha256,
                chunk.ExtractorVersion,
                null,
                null,
                1,
                _chunks.Count(item => item.BookId == chunk.BookId),
                _chunks.Where(item => item.BookId == chunk.BookId).Sum(item => (long)item.Text.Length),
                DateTime.UtcNow);
        }

        public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ScheduleAsync(
            Guid bookId,
            string sourceFileName,
            BookTextSourceFormat format,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<BookTextIngestionWork?> TryClaimNextAsync(
            TimeSpan staleAfter,
            CancellationToken ct = default) =>
            Task.FromResult<BookTextIngestionWork?>(null);

        public Task<bool> ReplaceReadyAsync(
            BookTextSourceRevision revision,
            IReadOnlyList<BookTextIndexedChunk> chunks,
            long characterCount,
            int expectedAttempt,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<bool> MarkFailedAsync(
            Guid bookId,
            string errorCode,
            string errorMessage,
            bool unsupported,
            int expectedAttempt,
            CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task DeleteBookAsync(Guid bookId, CancellationToken ct = default)
        {
            _states.Remove(bookId);
            _hits.RemoveAll(hit => hit.Chunk.BookId == bookId);
            _chunks.RemoveAll(chunk => chunk.BookId == bookId);
            return Task.CompletedTask;
        }

        public Task<BookTextIngestionState?> GetStateAsync(
            Guid bookId,
            CancellationToken ct = default) =>
            Task.FromResult(_states.GetValueOrDefault(bookId));

        public Task<IReadOnlyList<BookTextSearchHit>> SearchAsync(
            string query,
            IReadOnlyList<Guid> bookIds,
            int maxCandidates,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BookTextSearchHit>>(
                _hits
                    .Where(hit => bookIds.Contains(hit.Chunk.BookId))
                    .OrderByDescending(hit => hit.Score)
                    .Take(maxCandidates)
                    .ToList());

        public Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsAsync(
            Guid bookId,
            string sourceSha256,
            string extractorVersion,
            int ordinal,
            int radius,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BookTextIndexedChunk>>(
                _chunks
                    .Where(chunk =>
                        chunk.BookId == bookId
                        && string.Equals(chunk.SourceSha256, sourceSha256, StringComparison.Ordinal)
                        && string.Equals(chunk.ExtractorVersion, extractorVersion, StringComparison.Ordinal)
                        && chunk.Ordinal >= ordinal - radius
                        && chunk.Ordinal <= ordinal + radius)
                    .OrderBy(chunk => chunk.Ordinal)
                    .ToList());
    }
}
