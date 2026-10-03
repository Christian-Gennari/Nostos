using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Product.BookText;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Tests.Assistant.GateA;

/// <summary>
/// Shared deterministic harness for the issue #566 Gate A suite. Every test
/// drives the REAL product path (orchestrator, registry, canonical services,
/// real SQLite) with the LLM replaced by <see cref="FakeLlmProvider"/>; no
/// live model or network call is possible here.
/// </summary>
internal sealed class GateAHost : IDisposable
{
    public NostosDbContext Db { get; }
    public IDbContextFactory<NostosDbContext> Factory { get; }
    public AssistantCapabilityRegistry Registry { get; }
    public FakeLlmProvider Llm { get; }
    public AssistantPlanStore Plans { get; }
    public AssistantContinuationStore Continuations { get; }
    public AssistantSettingsService Settings { get; }
    public AssistantOrchestrator Orchestrator { get; }

    private GateAHost(
        NostosDbContext db,
        IDbContextFactory<NostosDbContext> factory,
        AssistantCapabilityRegistry registry,
        FakeLlmProvider llm,
        AssistantPlanStore plans,
        AssistantContinuationStore continuations,
        AssistantSettingsService settings,
        AssistantOrchestrator orchestrator)
    {
        Db = db;
        Factory = factory;
        Registry = registry;
        Llm = llm;
        Plans = plans;
        Continuations = continuations;
        Settings = settings;
        Orchestrator = orchestrator;
    }

    public static GateAHost Create(
        SqliteTestFixture fixture,
        int maxToolIterations = 6,
        Action<AssistantOptions>? configure = null,
        IBookTextSearchService? bookText = null,
        IKnowledgeRetrievalService? knowledge = null)
    {
        var path = fixture.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        using (var bootstrap = new NostosDbContext(options))
        {
            bootstrap.Database.EnsureCreated();
        }

        var factory = new GateAContextFactory(options);
        var db = new NostosDbContext(options);

        var topics = new TopicRepository(db);
        var noteService = new NoteService(
            new NoteRepository(db),
            new BookRepository(db),
            topics,
            new NoteProcessorService(topics),
            new FakeThoughtProcessor(),
            db,
            NullLogger<NoteService>.Instance);

        var libraryService = new LibraryService(
            factory,
            new BookLookupService(new NoopClientFactory(), new SilentLogger<BookLookupService>()));

        var registry = new AssistantCapabilityRegistry(
            AssistantCapabilities.Build(
                noteService,
                libraryService,
                topics,
                knowledge ?? NoOpKnowledgeRetrievalService.Instance,
                bookText));

        var llm = new FakeLlmProvider();
        var assistantOptions = new AssistantOptions
        {
            Enabled = true,
            MaxToolIterations = maxToolIterations,
        };
        configure?.Invoke(assistantOptions);
        var plans = new AssistantPlanStore();
        var continuations = new AssistantContinuationStore();
        var settings = new AssistantSettingsService(factory);

        var orchestrator = new AssistantOrchestrator(
            registry,
            llm,
            plans,
            continuations,
            settings,
            libraryService,
            assistantOptions,
            NullLogger<AssistantOrchestrator>.Instance);

        return new GateAHost(db, factory, registry, llm, plans, continuations, settings, orchestrator);
    }

    public void Dispose() => Db.Dispose();

    private sealed class GateAContextFactory(DbContextOptions<NostosDbContext> options)
        : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);

        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoopClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class SilentLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}

/// <summary>
/// Turn/context factories and store probes shared by the Gate A suite. They
/// mirror the long-lived orchestrator suite so a Gate A failure reads the
/// same way as the behaviour it pins.
/// </summary>
internal static class GateATurns
{
    public static AssistantTurnRequest Turn(
        string message,
        AssistantContextDto context,
        string clientId = "client-1",
        string idem = "key-1",
        string? processingMode = null,
        IReadOnlyList<AssistantHistoryMessageDto>? history = null,
        string? conversationId = null,
        string? turnId = null,
        string? continuationId = null,
        bool continuationSkipped = false) =>
        new(
            clientId,
            idem,
            message,
            context,
            processingMode,
            history,
            conversationId,
            turnId,
            continuationId,
            continuationSkipped);

    public static AssistantContextDto Context(
        string surface = "second-brain",
        string route = "/second-brain",
        string? bookId = null,
        string? bookTitle = null,
        string? bookFormat = null,
        string? readerType = null,
        string? epubCfi = null,
        int? pdfPage = null,
        double? audioTimestamp = null,
        string? selectedText = null,
        AssistantAnchorDto? anchor = null,
        string? captureBookTitle = null) =>
        new(
            surface,
            route,
            bookId,
            bookTitle,
            bookFormat,
            readerType,
            epubCfi,
            pdfPage,
            audioTimestamp,
            AudioChapter: null,
            selectedText,
            BrainReviewNoteId: null,
            Topic: null,
            CollectionId: null,
            anchor,
            CaptureBookTitle: captureBookTitle);

    public static async Task<PhysicalBookModel> SeedBookAsync(GateAHost h, string title = "Seeded Book")
    {
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = title, Author = "Author" };
        h.Db.Books.Add(book);
        await h.Db.SaveChangesAsync();
        return book;
    }

    public static async Task<CollectionModel> SeedCollectionAsync(GateAHost h, string name)
    {
        var collection = new CollectionModel { Id = Guid.NewGuid(), Name = name };
        h.Db.Collections.Add(collection);
        await h.Db.SaveChangesAsync();
        return collection;
    }

    public static async Task<int> NoteCountAsync(GateAHost h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.Notes.CountAsync();
    }

    public static async Task<int> CollectionCountAsync(GateAHost h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.Collections.CountAsync();
    }
}
