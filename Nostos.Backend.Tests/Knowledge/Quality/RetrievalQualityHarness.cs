using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;

namespace Nostos.Backend.Tests.Knowledge.Quality;

/// <summary>
/// Test-only harness for the retrieval-quality fixtures (issue #566).
/// Mirrors the construction in <c>KnowledgeRetrievalServiceTests</c> without
/// touching it: a real temp-file SQLite database plus the production note,
/// concept, library and book-text services. The book-text options disable
/// neighbor expansion so passage metrics measure FTS hits only.
/// </summary>
internal sealed class RetrievalQualityHarness : IAsyncDisposable
{
    private RetrievalQualityHarness(
        NostosDbContext db,
        TestDbContextFactory factory,
        NoteRepository noteRepository,
        ConceptRepository conceptRepository,
        NoteService noteService,
        SqliteBookTextIndex bookTextIndex,
        BookTextSearchService bookTextSearch,
        KnowledgeRetrievalService knowledge)
    {
        Db = db;
        Factory = factory;
        NoteRepository = noteRepository;
        ConceptRepository = conceptRepository;
        NoteService = noteService;
        BookTextIndex = bookTextIndex;
        BookTextSearch = bookTextSearch;
        Knowledge = knowledge;
    }

    public NostosDbContext Db { get; }
    public TestDbContextFactory Factory { get; }
    public NoteRepository NoteRepository { get; }
    public ConceptRepository ConceptRepository { get; }
    public NoteService NoteService { get; }
    public SqliteBookTextIndex BookTextIndex { get; }
    public BookTextSearchService BookTextSearch { get; }
    public KnowledgeRetrievalService Knowledge { get; }

    public static async Task<RetrievalQualityHarness> CreateAsync(SqliteTestFixture fixture)
    {
        var path = fixture.CreateDatabasePath();
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
        var bookSearch = new BookTextSearchService(
            index,
            library,
            new BookTextOptions { NeighborRadius = 0 });
        var knowledge = new KnowledgeRetrievalService(
            noteService,
            noteRepository,
            concepts,
            library,
            bookSearch,
            index,
            Array.Empty<IKnowledgeRetrievalContributor>());

        return new RetrievalQualityHarness(
            db, factory, noteRepository, concepts, noteService, index, bookSearch, knowledge);
    }

    public ValueTask DisposeAsync()
    {
        Db.Dispose();
        return ValueTask.CompletedTask;
    }

    internal sealed class TestDbContextFactory(DbContextOptions<NostosDbContext> options)
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
}
