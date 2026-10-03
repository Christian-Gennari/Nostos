using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Knowledge;

public sealed class KnowledgeRetrievalServiceTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public KnowledgeRetrievalServiceTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Low_overlap_multi_query_recovers_relevant_note_and_ranks_it_over_a_distractor()
    {
        await using var h = await CreateHarnessAsync();
        // Keep the book title neutral: note search intentionally includes book
        // titles, and naming this book "Responsibility" would give the distractor
        // an artificial second match unrelated to its note text.
        var book = await h.SeedBookAsync("Neutral Reading");
        var relevant = await h.SeedNoteAsync(
            book.Id,
            "Freedom becomes concrete when a person accepts responsibility for a choice.");
        var distractor = await h.SeedNoteAsync(
            book.Id,
            "Decisions about furniture arrangement belong in another notebook.");

        const string query = "freedom requires accepting responsibility for decisions";

        // Measured baseline: the pre-#562 whole-phrase repository query has no
        // hit because that exact wording never occurs in either note.
        var baseline = await h.NoteRepository.SearchByTextAsync(query, 20);
        baseline.Should().BeEmpty();

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest(query, MaxPerSource: 6));

        var retrieved = result.Notes.Select(note => note.NoteId).ToList();
        var recall = retrieved.Contains(relevant.Id) ? 1d : 0d;
        var precisionAtOne = result.Notes.FirstOrDefault()?.NoteId == relevant.Id ? 1d : 0d;

        recall.Should().Be(1d);
        precisionAtOne.Should().Be(1d);
        retrieved.Should().Contain(distractor.Id);
        result.QueryVariants.Should().Contain("freedom");
        result.QueryVariants.Should().Contain("responsibility");
    }

    [Fact]
    public async Task Concept_search_uses_linked_note_evidence_across_multiple_lexical_variants()
    {
        await using var h = await CreateHarnessAsync();
        var book = await h.SeedBookAsync("Agency");
        var relevant = await h.SeedNoteAsync(
            book.Id,
            "Freedom becomes concrete when a person accepts responsibility for a choice.");
        var distractor = await h.SeedNoteAsync(
            book.Id,
            "Decisions about furniture arrangement belong in another notebook.");
        var agency = await h.SeedConceptAsync("Agency", relevant.Id);
        await h.SeedConceptAsync("Arrangement", distractor.Id);

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest(
                "freedom requires accepting responsibility for decisions",
                MaxPerSource: 6));

        result.Concepts.Should().NotBeEmpty();
        result.Concepts[0].ConceptId.Should().Be(agency.Id);
        result.Concepts[0].SupportingNotes.Should().Contain(note => note.NoteId == relevant.Id);
        result.Concepts[0].Handle.Should().Be(
            new KnowledgeEvidenceHandle(KnowledgeEvidenceKinds.Concept, ConceptId: agency.Id));
    }

    [Fact]
    public async Task Explicit_book_scope_applies_to_notes_and_concept_evidence()
    {
        await using var h = await CreateHarnessAsync();
        var bookA = await h.SeedBookAsync("Book A");
        var bookB = await h.SeedBookAsync("Book B");
        var noteA = await h.SeedNoteAsync(bookA.Id, "Freedom and responsibility belong together.");
        var noteB = await h.SeedNoteAsync(bookB.Id, "Freedom responsibility decisions consequences.");
        var concept = await h.SeedConceptAsync("Agency", noteA.Id, noteB.Id);

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest(
                "freedom responsibility",
                BookIds: [bookA.Id],
                MaxPerSource: 6));

        result.Notes.Should().ContainSingle();
        result.Notes[0].NoteId.Should().Be(noteA.Id);
        result.Notes.Should().NotContain(note => note.NoteId == noteB.Id);

        var conceptEvidence = result.Concepts.Single(item => item.ConceptId == concept.Id);
        conceptEvidence.SupportingNotes.Should().OnlyContain(note => note.BookId == bookA.Id);
    }

    [Fact]
    public async Task Overview_is_structural_and_bounded()
    {
        await using var h = await CreateHarnessAsync();
        var first = await h.SeedBookAsync("First");
        var second = await h.SeedBookAsync("Second");

        var n1 = await h.SeedNoteAsync(first.Id, "one");
        var n2 = await h.SeedNoteAsync(first.Id, "two");
        await h.SeedNoteAsync(second.Id, "three");
        await h.SeedConceptAsync("Recurring", n1.Id, n2.Id);

        var overview = await h.Knowledge.OverviewAsync();

        overview.TotalNotes.Should().Be(3);
        overview.UnlinkedNotes.Should().Be(1);
        overview.TotalConcepts.Should().Be(1);
        overview.TotalConceptReferences.Should().Be(2);
        overview.TopConcepts.Should().ContainSingle(concept => concept.Name == "Recurring");
        overview.TopBooksByNoteCount[0].Should().Be(
            new KnowledgeBookNoteCount(first.Id, "First", 2));
        overview.TopBooksByNoteCount.Count.Should().BeLessThanOrEqualTo(12);
        overview.TopConcepts.Count.Should().BeLessThanOrEqualTo(12);
    }

    [Fact]
    public async Task Note_handle_rereads_exact_canonical_note_and_carries_provenance()
    {
        await using var h = await CreateHarnessAsync();
        var book = await h.SeedBookAsync("Physical Book");
        var note = await h.SeedNoteAsync(
            book.Id,
            "A thought worth keeping.",
            selectedText: "A quoted sentence.",
            sourceAnchorKind: "physical_page",
            sourceAnchorValue: "247",
            anchorVerified: false);
        await h.SeedConceptAsync("Attention", note.Id);

        var handle = new KnowledgeEvidenceHandle(
            KnowledgeEvidenceKinds.Note,
            NoteId: note.Id);

        var read = await h.Knowledge.ReadAsync(handle);

        read.Should().NotBeNull();
        read!.Handle.Should().Be(handle);
        read.Note!.NoteId.Should().Be(note.Id);
        read.Note.Content.Should().Be("A thought worth keeping.");
        read.Note.SelectedText.Should().Be("A quoted sentence.");
        read.Note.SourceAnchorKind.Should().Be("physical_page");
        read.Note.SourceAnchorValue.Should().Be("247");
        read.Note.AnchorVerified.Should().BeFalse();
        read.Note.ConceptNames.Should().Contain("Attention");
    }

    [Fact]
    public async Task Book_text_handle_rereads_exact_revision_and_stale_revision_is_refused()
    {
        await using var h = await CreateHarnessAsync();
        var book = await h.SeedBookAsync("Imported PDF");
        var hash = new string('a', 64);
        var revision = new BookTextSourceRevision(
            book.Id,
            hash,
            BookTextArtifactSchema.CurrentExtractorVersion,
            BookTextSourceFormat.Pdf);

        await h.BookTextIndex.ScheduleAsync(book.Id, "book.pdf", BookTextSourceFormat.Pdf);
        var work = await h.BookTextIndex.TryClaimNextAsync(TimeSpan.FromMinutes(15));
        work.Should().NotBeNull();

        var chunk = new BookTextIndexedChunk(
            BookTextIdentity.ChunkId(revision, 0),
            book.Id,
            hash,
            revision.ExtractorVersion,
            BookTextSourceFormat.Pdf,
            0,
            "Freedom requires responsibility in every choice.",
            ["Chapter 1"],
            [
                new BookTextSourceSegment(
                    0,
                    47,
                    new PdfBookTextSourceLocator(12, "13", 0, 47)),
            ]);

        (await h.BookTextIndex.ReplaceReadyAsync(
            revision,
            [chunk],
            chunk.Text.Length,
            work!.Attempt)).Should().BeTrue();

        var search = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest(
                "responsibility",
                BookIds: [book.Id],
                MaxPerSource: 4));

        search.BookPassages.Should().ContainSingle();
        var evidence = search.BookPassages[0];
        evidence.Handle.SourceSha256.Should().Be(hash);
        evidence.Handle.Ordinal.Should().Be(0);

        var reread = await h.Knowledge.ReadAsync(evidence.Handle);
        reread!.BookPassage!.Text.Should().Be(chunk.Text);
        reread.BookPassage.SourceSegments.Should().BeEquivalentTo(chunk.SourceSegments);

        // Replacing/scheduling the source invalidates the old revision
        // immediately; a historical handle cannot resurrect stale text.
        await h.BookTextIndex.ScheduleAsync(book.Id, "replacement.pdf", BookTextSourceFormat.Pdf);
        (await h.Knowledge.ReadAsync(evidence.Handle)).Should().BeNull();
    }

    [Fact]
    public async Task Optional_contributor_candidates_cannot_escape_explicit_book_scope()
    {
        await using var h = await CreateHarnessAsync();
        var scoped = await h.SeedBookAsync("Scoped");
        var outside = await h.SeedBookAsync("Outside");
        var outsideNote = await h.SeedNoteAsync(
            outside.Id,
            "A semantic contributor should not bypass the explicit book scope.");

        h.Contributor.Handles =
        [
            new KnowledgeEvidenceHandle(
                KnowledgeEvidenceKinds.Note,
                NoteId: outsideNote.Id),
        ];

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest(
                "phrase absent from the scoped book",
                BookIds: [scoped.Id],
                MaxPerSource: 4));

        result.Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task Optional_contributor_candidates_are_resolved_through_canonical_data()
    {
        await using var h = await CreateHarnessAsync();
        var book = await h.SeedBookAsync("Semantic seam");
        var note = await h.SeedNoteAsync(
            book.Id,
            "Vocabulary with no lexical overlap to the search phrase.");

        h.Contributor.Handles =
        [
            new KnowledgeEvidenceHandle(KnowledgeEvidenceKinds.Note, NoteId: note.Id),
            new KnowledgeEvidenceHandle(KnowledgeEvidenceKinds.Note, NoteId: Guid.NewGuid()),
        ];

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest("completely unrelated words", MaxPerSource: 4));

        result.Notes.Should().ContainSingle(item => item.NoteId == note.Id);
        // The missing handle returned by the contributor is discarded because
        // it cannot be resolved back to canonical Nostos data.
        result.Notes.Should().HaveCount(1);
    }

    // The hybrid corpus (issue #683). For the query "freedom":
    //   lexical (bm25) ranks  Dense (ordinal 0) > Sparse (ordinal 3); Synonym has no match
    //   vector (cosine) ranks Sparse (1.0) > Synonym (0.9) > Dense (0.5) > fillers (0)
    // so RRF, k=60, gives Sparse 1/62+1/61 > Dense 1/61+1/63 > Synonym 1/62.
    private const string EmbeddingModel = "test-embedding-model";
    private const string Dense = "Freedom freedom freedom.";
    private const string Sparse =
        "Freedom is discussed here once, in a long passage that is mostly about harbours, tides, weather and the slow repair of wooden boats.";
    private const string Synonym = "Liberty is the condition of ruling oneself without a master.";

    private static readonly string[] HybridCorpus =
    [
        Dense,
        "Filler about bread and ovens.",
        "Filler about mountain paths.",
        Sparse,
        "Filler about river stones.",
        "Filler about winter coats.",
        Synonym,
    ];

    private static async Task<(PhysicalBookModel Book, IReadOnlyList<BookTextIndexedChunk> Chunks)> SeedHybridCorpusAsync(
        Harness h)
    {
        var book = await h.SeedBookAsync("Hybrid");
        var chunks = await h.IndexBookAsync(book.Id, HybridCorpus);
        await h.BookTextIndex.StoreAsync(
            EmbeddingModel,
            chunks
                .Select(chunk => new BookTextChunkEmbedding(chunk.Id, chunk.Text switch
                {
                    Sparse => [1f, 0f, 0f],
                    Synonym => [0.9f, 0.43589f, 0f],
                    Dense => [0.5f, 0.86603f, 0f],
                    _ => [0f, 0f, 1f],
                }))
                .ToList());
        return (book, chunks);
    }

    [Fact]
    public async Task Disabled_embeddings_keep_book_retrieval_purely_lexical()
    {
        var provider = new FakeEmbeddingProvider(model: null);
        await using var h = await CreateHarnessAsync(provider);
        var (book, _) = await SeedHybridCorpusAsync(h);

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest("freedom", BookIds: [book.Id], MaxPerSource: 3));

        // bm25 order, each hit followed by its neighbours: exactly what the
        // lexical pipeline returned before the vector channel existed.
        result.BookPassages.Select(passage => passage.Ordinal).Should().Equal(0, 1, 3);
        result.BookPassages.Should().NotContain(passage => passage.Text == Synonym);
        provider.EmbedCalls.Should().Be(0);

        await using var withoutEmbeddings = await CreateHarnessAsync();
        var (lexicalBook, _) = await SeedHybridCorpusAsync(withoutEmbeddings);
        var lexical = await withoutEmbeddings.Knowledge.SearchAsync(
            new KnowledgeSearchRequest("freedom", BookIds: [lexicalBook.Id], MaxPerSource: 3));
        result.BookPassages.Select(passage => passage.Text)
            .Should().Equal(lexical.BookPassages.Select(passage => passage.Text));
    }

    [Fact]
    public async Task Enabled_embeddings_fuse_lexical_and_vector_rankings_by_rrf()
    {
        var provider = new FakeEmbeddingProvider(EmbeddingModel) { QueryVector = [1f, 0f, 0f] };
        await using var h = await CreateHarnessAsync(provider);
        var (book, chunks) = await SeedHybridCorpusAsync(h);

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest("freedom", BookIds: [book.Id], MaxPerSource: 3));

        // Sparse is second lexically and first by vector, so fusion lifts it
        // over the lexical leader; Synonym has no lexical match at all and is
        // only reachable through the vector channel.
        result.BookPassages.Select(passage => passage.Text).Should().Equal(Sparse, Dense, Synonym);
        result.EvidenceAvailable.Should().BeTrue();
        provider.EmbedCalls.Should().Be(1);
        provider.LastInputs.Should().Equal("freedom");

        // A vector-only passage is still canonical evidence: same handle,
        // heading path and source anchors as the indexed chunk, and it rereads.
        var synonymChunk = chunks.Single(chunk => chunk.Text == Synonym);
        var synonym = result.BookPassages[2];
        synonym.Handle.Should().Be(new KnowledgeEvidenceHandle(
            KnowledgeEvidenceKinds.BookText,
            BookId: book.Id,
            SourceSha256: synonymChunk.SourceSha256,
            ExtractorVersion: synonymChunk.ExtractorVersion,
            Ordinal: synonymChunk.Ordinal));
        synonym.BookTitle.Should().Be("Hybrid");
        synonym.BookAuthor.Should().Be("Author");
        synonym.HeadingPath.Should().Equal("Chapter One");
        synonym.SourceSegments.Should().BeEquivalentTo(synonymChunk.SourceSegments);

        var reread = await h.Knowledge.ReadAsync(synonym.Handle);
        reread!.BookPassage!.Text.Should().Be(Synonym);
    }

    [Fact]
    public async Task Vector_channel_recovers_passages_when_the_lexical_channel_finds_nothing()
    {
        var provider = new FakeEmbeddingProvider(EmbeddingModel) { QueryVector = [1f, 0f, 0f] };
        await using var h = await CreateHarnessAsync(provider);
        var (book, _) = await SeedHybridCorpusAsync(h);

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest("emancipation", BookIds: [book.Id], MaxPerSource: 3));

        result.BookPassages.Select(passage => passage.Text).Should().Equal(Sparse, Synonym, Dense);
        result.EvidenceAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Vector_channel_cannot_escape_explicit_book_scope()
    {
        var provider = new FakeEmbeddingProvider(EmbeddingModel) { QueryVector = [1f, 0f, 0f] };
        await using var h = await CreateHarnessAsync(provider);
        await SeedHybridCorpusAsync(h);
        var scoped = await h.SeedBookAsync("Scoped");
        await h.IndexBookAsync(scoped.Id, "Unrelated passage about clockwork.");

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest("freedom", BookIds: [scoped.Id], MaxPerSource: 3));

        result.BookPassages.Should().BeEmpty();
    }

    [Fact]
    public async Task Enabled_embeddings_without_stored_vectors_stay_lexical()
    {
        // A model with no vectors yet (the embedding pass has not caught up).
        var provider = new FakeEmbeddingProvider("another-model") { QueryVector = [1f, 0f, 0f] };
        await using var h = await CreateHarnessAsync(provider);
        var (book, _) = await SeedHybridCorpusAsync(h);

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest("freedom", BookIds: [book.Id], MaxPerSource: 3));

        result.BookPassages.Select(passage => passage.Ordinal).Should().Equal(0, 1, 3);
    }

    public static TheoryData<Exception> EmbeddingFailures => new()
    {
        EmbeddingException.TimedOut(),
        EmbeddingException.ProviderFailure("HTTP 500"),
        new InvalidOperationException("unexpected"),
        new TaskCanceledException("provider-side timeout"),
    };

    [Theory]
    [MemberData(nameof(EmbeddingFailures))]
    public async Task Failing_embedding_provider_falls_back_to_lexical_without_failing_the_turn(
        Exception failure)
    {
        var provider = new FakeEmbeddingProvider(EmbeddingModel) { Failure = failure };
        await using var h = await CreateHarnessAsync(provider);
        var (book, _) = await SeedHybridCorpusAsync(h);

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest("freedom", BookIds: [book.Id], MaxPerSource: 3));

        provider.EmbedCalls.Should().Be(1);
        result.BookPassages.Select(passage => passage.Ordinal).Should().Equal(0, 1, 3);
        result.EvidenceAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Failing_active_model_lookup_falls_back_to_lexical()
    {
        var provider = new FakeEmbeddingProvider(EmbeddingModel)
        {
            ModelFailure = new InvalidOperationException("settings unavailable"),
        };
        await using var h = await CreateHarnessAsync(provider);
        var (book, _) = await SeedHybridCorpusAsync(h);

        var result = await h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest("freedom", BookIds: [book.Id], MaxPerSource: 3));

        result.BookPassages.Select(passage => passage.Ordinal).Should().Equal(0, 1, 3);
    }

    [Fact]
    public async Task Caller_cancellation_during_embedding_is_not_swallowed_as_a_fallback()
    {
        using var cts = new CancellationTokenSource();
        var provider = new FakeEmbeddingProvider(EmbeddingModel) { OnEmbed = cts.Cancel };
        await using var h = await CreateHarnessAsync(provider);
        var (book, _) = await SeedHybridCorpusAsync(h);

        var act = () => h.Knowledge.SearchAsync(
            new KnowledgeSearchRequest("freedom", BookIds: [book.Id], MaxPerSource: 3),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<Harness> CreateHarnessAsync(IEmbeddingProvider? embeddings = null)
    {
        var path = _fixture.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        await using (var bootstrap = new NostosDbContext(options))
            await bootstrap.Database.EnsureCreatedAsync();

        var factory = new TestDbContextFactory(options);
        var db = new NostosDbContext(options);
        var concepts = new ConceptRepository(db);
        var noteRepository = new NoteRepository(db);
        var noteService = new NoteService(
            noteRepository,
            new BookRepository(db),
            concepts,
            new NoteProcessorService(concepts),
            new PassThroughThoughtProcessor(),
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
        var contributor = new StubContributor();
        var knowledge = new KnowledgeRetrievalService(
            noteService,
            noteRepository,
            concepts,
            library,
            bookSearch,
            index,
            [contributor],
            embeddings,
            embeddings is null ? null : index);

        return new Harness(
            db,
            factory,
            noteRepository,
            index,
            knowledge,
            contributor);
    }

    private sealed class Harness(
        NostosDbContext db,
        IDbContextFactory<NostosDbContext> factory,
        NoteRepository noteRepository,
        SqliteBookTextIndex bookTextIndex,
        KnowledgeRetrievalService knowledge,
        StubContributor contributor) : IAsyncDisposable
    {
        public NostosDbContext Db { get; } = db;
        public IDbContextFactory<NostosDbContext> Factory { get; } = factory;
        public NoteRepository NoteRepository { get; } = noteRepository;
        public SqliteBookTextIndex BookTextIndex { get; } = bookTextIndex;
        public KnowledgeRetrievalService Knowledge { get; } = knowledge;
        public StubContributor Contributor { get; } = contributor;

        public async Task<PhysicalBookModel> SeedBookAsync(string title)
        {
            var book = new PhysicalBookModel
            {
                Id = Guid.NewGuid(),
                Title = title,
                Author = "Author",
            };
            Db.Books.Add(book);
            await Db.SaveChangesAsync();
            return book;
        }

        public async Task<IReadOnlyList<BookTextIndexedChunk>> IndexBookAsync(
            Guid bookId,
            params string[] texts)
        {
            await BookTextIndex.ScheduleAsync(bookId, "book.pdf", BookTextSourceFormat.Pdf);
            BookTextIngestionWork? work;
            do
            {
                work = await BookTextIndex.TryClaimNextAsync(TimeSpan.FromMinutes(15));
                work.Should().NotBeNull();
            }
            while (work!.BookId != bookId);

            var revision = new BookTextSourceRevision(
                bookId,
                new string('a', 64),
                BookTextArtifactSchema.CurrentExtractorVersion,
                BookTextSourceFormat.Pdf);

            var chunks = texts
                .Select((text, ordinal) => new BookTextIndexedChunk(
                    BookTextIdentity.ChunkId(revision, ordinal),
                    bookId,
                    revision.SourceSha256,
                    revision.ExtractorVersion,
                    revision.Format,
                    ordinal,
                    text,
                    ["Chapter One"],
                    [new BookTextSourceSegment(0, text.Length, new PdfBookTextSourceLocator(ordinal, null, 0, text.Length))]))
                .ToList();

            (await BookTextIndex.ReplaceReadyAsync(
                revision,
                chunks,
                chunks.Sum(chunk => chunk.Text.Length),
                work.Attempt)).Should().BeTrue();
            return chunks;
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
            Db.Notes.Add(note);
            await Db.SaveChangesAsync();
            return note;
        }

        public async Task<ConceptModel> SeedConceptAsync(
            string name,
            params Guid[] noteIds)
        {
            var concept = new ConceptModel
            {
                Id = Guid.NewGuid(),
                Concept = name,
            };
            Db.Concepts.Add(concept);
            foreach (var noteId in noteIds)
            {
                Db.NoteConcepts.Add(new NoteConceptModel
                {
                    NoteId = noteId,
                    ConceptId = concept.Id,
                });
            }
            await Db.SaveChangesAsync();
            return concept;
        }

        public ValueTask DisposeAsync()
        {
            Db.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<NostosDbContext> options)
        : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);

        public Task<NostosDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new NostosDbContext(options));
    }

    private sealed class PassThroughThoughtProcessor : IThoughtProcessor
    {
        public Task<ThoughtProcessingResult> ProcessAsync(
            string rawText,
            string mode,
            CancellationToken ct = default) =>
            Task.FromResult(new ThoughtProcessingResult(rawText, mode, ProviderCalled: false));
    }

    private sealed class FakeEmbeddingProvider(string? model) : IEmbeddingProvider
    {
        public float[] QueryVector { get; init; } = [1f, 0f, 0f];
        public Exception? Failure { get; init; }
        public Exception? ModelFailure { get; init; }
        public Action? OnEmbed { get; init; }
        public int EmbedCalls { get; private set; }
        public IReadOnlyList<string> LastInputs { get; private set; } = [];

        public Task<string?> GetActiveModelAsync(CancellationToken ct = default) =>
            ModelFailure is null ? Task.FromResult(model) : throw ModelFailure;

        public Task<EmbeddingBatch> EmbedAsync(
            IReadOnlyList<string> inputs,
            CancellationToken ct = default)
        {
            EmbedCalls++;
            LastInputs = inputs;
            if (Failure is not null)
                throw Failure;

            OnEmbed?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new EmbeddingBatch(model!, [QueryVector.ToArray()], TotalTokens: 1));
        }
    }

    private sealed class StubContributor : IKnowledgeRetrievalContributor
    {
        public string Name => "stub-semantic";
        public IReadOnlyList<KnowledgeEvidenceHandle> Handles { get; set; } = [];

        public Task<IReadOnlyList<KnowledgeEvidenceHandle>> SearchAsync(
            KnowledgeSearchRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(Handles);
    }
}
