using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.ReadingList;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// Studio drafts, a topic's own page and the To read list as assistant
/// capabilities (issue #1468), plus the explicit book on the quote finder.
/// Each runs through the real repository or service the REST endpoints use.
/// </summary>
public sealed class AssistantDraftsTopicsReadingListTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public AssistantDraftsTopicsReadingListTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("topics_get", AssistantTrustClass.Suggest, AssistantCapabilityCategory.KnowledgeRetrieval)]
    [InlineData("writings_list", AssistantTrustClass.Suggest, AssistantCapabilityCategory.Writing)]
    [InlineData("writings_read", AssistantTrustClass.Suggest, AssistantCapabilityCategory.Writing)]
    [InlineData("writings_create", AssistantTrustClass.Act, AssistantCapabilityCategory.Writing)]
    [InlineData("reading_list_list", AssistantTrustClass.Suggest, AssistantCapabilityCategory.LibraryRead)]
    [InlineData("reading_list_add", AssistantTrustClass.Act, AssistantCapabilityCategory.OrdinaryAction)]
    public void Each_new_capability_has_its_trust_class_and_is_on_both_surfaces(
        string name, AssistantTrustClass trust, AssistantCapabilityCategory category)
    {
        using var h = CreateHarness();

        var capability = h.Registry.All.Single(c => c.Name == name);
        capability.Trust.Should().Be(trust);
        capability.Category.Should().Be(category);
        capability.Surfaces.Should().Be(AssistantSurface.AskNostos | AssistantSurface.Mcp);
    }

    // ------------------------------------------------------------------
    // topics_get
    // ------------------------------------------------------------------

    [Fact]
    public async Task Topics_get_reads_the_description_and_linked_notes_newest_first()
    {
        using var h = CreateHarness();
        var book = await SeedBookAsync(h, "Middlemarch", "George Eliot");
        var older = await SeedNoteAsync(h, book.Id, "Dorothea's idealism", DateTime.UtcNow.AddDays(-2));
        var newer = await SeedNoteAsync(h, book.Id, "Lydgate's ambition", DateTime.UtcNow);
        var topic = new TopicModel { Id = Guid.NewGuid(), Topic = "Vocation", Description = "What a life is for." };
        h.Db.Topics.Add(topic);
        h.Db.NoteTopics.AddRange(
            new NoteTopicModel { NoteId = older.Id, TopicId = topic.Id },
            new NoteTopicModel { NoteId = newer.Id, TopicId = topic.Id });
        await h.Db.SaveChangesAsync();

        var result = await Invoke(h, "topics_get", new { topicId = topic.Id });

        result.Success.Should().BeTrue(result.ErrorMessage);
        var data = result.Data!.Value;
        data.GetProperty("name").GetString().Should().Be("Vocation");
        data.GetProperty("description").GetString().Should().Be("What a life is for.");
        data.GetProperty("noteCount").GetInt32().Should().Be(2);
        var notes = data.GetProperty("notes").EnumerateArray().ToList();
        notes.Select(n => n.GetProperty("noteId").GetGuid()).Should().Equal(newer.Id, older.Id);
        notes[0].GetProperty("bookTitle").GetString().Should().Be("Middlemarch");

        var limited = await Invoke(h, "topics_get", new { topicId = topic.Id, limit = 1 });
        limited.Data!.Value.GetProperty("notes").GetArrayLength().Should().Be(1);
        limited.Data!.Value.GetProperty("noteCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Topics_get_is_not_found_or_invalid_without_a_real_topic()
    {
        using var h = CreateHarness();

        (await Invoke(h, "topics_get", new { topicId = Guid.NewGuid() }))
            .ErrorCode.Should().Be(AssistantErrorCodes.NotFound);
        (await Invoke(h, "topics_get", new { }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);
        (await Invoke(h, "topics_get", new { topicId = Guid.NewGuid(), limit = 0 }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);
    }

    // ------------------------------------------------------------------
    // writings_list / writings_read
    // ------------------------------------------------------------------

    [Fact]
    public async Task Writings_list_gives_paths_and_links_and_filters_by_name()
    {
        using var h = CreateHarness();
        var folder = await SeedWritingAsync(h, "Essays", WritingType.Folder);
        var draft = await SeedWritingAsync(h, "On memory", WritingType.Document, folder.Id, "Draft text");
        await SeedWritingAsync(h, "Shopping list", WritingType.Document, content: "Milk");

        var all = await Invoke(h, "writings_list", new { });
        all.Success.Should().BeTrue(all.ErrorMessage);
        all.Data!.Value.GetProperty("total").GetInt32().Should().Be(3);

        var found = await Invoke(h, "writings_list", new { query = "essays memory" });
        var item = found.Data!.Value.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("id").GetGuid().Should().Be(draft.Id);
        item.GetProperty("path").GetString().Should().Be("Essays / On memory");
        item.GetProperty("link").GetString().Should().Be($"/studio?writingId={draft.Id}");
        item.TryGetProperty("content", out _).Should().BeFalse("the list never carries a document's text");

        var folders = await Invoke(h, "writings_list", new { type = "Folder" });
        folders.Data!.Value.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).Should().Equal(folder.Id);
    }

    [Fact]
    public async Task Writings_list_refuses_invalid_arguments()
    {
        using var h = CreateHarness();

        (await Invoke(h, "writings_list", new { limit = 0 }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);
        (await Invoke(h, "writings_list", new { type = "Chapter" }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);
    }

    [Fact]
    public async Task Writings_read_returns_the_document_as_markdown()
    {
        using var h = CreateHarness();
        var folder = await SeedWritingAsync(h, "Essays", WritingType.Folder);
        const string markdown = "# On memory\n\nAs [Eliot says](nostos:cite/abc?p=4&as=quote), it is so.";
        var draft = await SeedWritingAsync(h, "On memory", WritingType.Document, folder.Id, markdown);

        var result = await Invoke(h, "writings_read", new { writingId = draft.Id });

        result.Success.Should().BeTrue(result.ErrorMessage);
        var data = result.Data!.Value;
        data.GetProperty("content").GetString().Should().Be(markdown);
        data.GetProperty("path").GetString().Should().Be("Essays / On memory");
        data.GetProperty("link").GetString().Should().Be($"/studio?writingId={draft.Id}");
        data.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Writings_read_is_not_found_invalid_or_refuses_a_folder()
    {
        using var h = CreateHarness();
        var folder = await SeedWritingAsync(h, "Essays", WritingType.Folder);

        (await Invoke(h, "writings_read", new { writingId = Guid.NewGuid() }))
            .ErrorCode.Should().Be(AssistantErrorCodes.NotFound);
        (await Invoke(h, "writings_read", new { writingId = "not-an-id" }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);
        (await Invoke(h, "writings_read", new { writingId = folder.Id }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);
    }

    // ------------------------------------------------------------------
    // writings_create
    // ------------------------------------------------------------------

    [Fact]
    public async Task Writings_create_makes_a_new_document_with_live_citations_like_a_studio_save()
    {
        using var h = CreateHarness();
        var book = await SeedBookAsync(h, "Middlemarch", "George Eliot");
        var note = await SeedNoteAsync(h, book.Id, "The growing good of the world", DateTime.UtcNow);
        var folder = await SeedWritingAsync(h, "Essays", WritingType.Folder);
        var markdown =
            $"Eliot ends on [the growing good of the world](nostos:cite/{book.Id}?p=838&as=quote&src={note.Id}).";

        var result = await Invoke(h, "writings_create", new { name = "Small acts", content = markdown, folderId = folder.Id });

        result.Success.Should().BeTrue(result.ErrorMessage);
        var data = result.Data!.Value;
        var id = data.GetProperty("id").GetGuid();
        data.GetProperty("link").GetString().Should().Be($"/studio?writingId={id}");
        data.GetProperty("path").GetString().Should().Be("Essays / Small acts");
        data.GetProperty("citations").GetInt32().Should().Be(1);

        await using var db = await h.Factory.CreateDbContextAsync();
        var stored = await db.Writings.AsNoTracking().SingleAsync(w => w.Id == id);
        stored.Type.Should().Be(WritingType.Document);
        stored.ParentId.Should().Be(folder.Id);
        stored.Content.Should().Be(markdown);
        // The same citation index a Studio save rebuilds (issue #1162).
        (await db.WritingCitations.AsNoTracking().Where(c => c.WritingId == id).Select(c => c.NoteId).ToListAsync())
            .Should().Equal(note.Id);
    }

    [Fact]
    public async Task Writings_create_never_overwrites_a_document_with_the_same_name()
    {
        using var h = CreateHarness();
        var existing = await SeedWritingAsync(h, "Small acts", WritingType.Document, content: "My own words");

        var result = await Invoke(h, "writings_create", new { name = "Small acts", content = "New words" });

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.Data!.Value.GetProperty("id").GetGuid().Should().NotBe(existing.Id);
        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Writings.AsNoTracking().SingleAsync(w => w.Id == existing.Id)).Content.Should().Be("My own words");
        (await db.Writings.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Writings_create_refuses_a_missing_name_or_folder_and_writes_nothing()
    {
        using var h = CreateHarness();
        var document = await SeedWritingAsync(h, "Not a folder", WritingType.Document, content: "x");

        (await Invoke(h, "writings_create", new { content = "No name" }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);
        (await Invoke(h, "writings_create", new { name = "Lost", folderId = Guid.NewGuid() }))
            .ErrorCode.Should().Be(AssistantErrorCodes.NotFound);
        (await Invoke(h, "writings_create", new { name = "Nested", folderId = document.Id }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);
        (await Invoke(h, "writings_create", new { name = "Bad", folderId = "nope" }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Writings.CountAsync()).Should().Be(1);
    }

    // ------------------------------------------------------------------
    // reading_list_list / reading_list_add
    // ------------------------------------------------------------------

    [Fact]
    public async Task Reading_list_list_puts_next_up_first_and_filters_by_shelf()
    {
        using var h = CreateHarness();
        h.Db.ReadingListEntries.AddRange(
            new ReadingListEntryModel { Title = "Someday Book", CreatedAt = DateTime.UtcNow },
            new ReadingListEntryModel { Title = "Second Up", NextPosition = 1, CreatedAt = DateTime.UtcNow.AddDays(-3) },
            new ReadingListEntryModel { Title = "First Up", NextPosition = 0, CreatedAt = DateTime.UtcNow.AddDays(-2), Why = "Everyone quotes it" });
        await h.Db.SaveChangesAsync();

        var all = await Invoke(h, "reading_list_list", new { });

        all.Success.Should().BeTrue(all.ErrorMessage);
        var data = all.Data!.Value;
        data.GetProperty("total").GetInt32().Should().Be(3);
        data.GetProperty("nextUpCount").GetInt32().Should().Be(2);
        var entries = data.GetProperty("entries").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("title").GetString()).Should().Equal("First Up", "Second Up", "Someday Book");
        entries[0].GetProperty("shelf").GetString().Should().Be("next");
        entries[0].GetProperty("why").GetString().Should().Be("Everyone quotes it");

        var someday = await Invoke(h, "reading_list_list", new { shelf = "someday" });
        someday.Data!.Value.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("title").GetString()).Should().Equal("Someday Book");

        (await Invoke(h, "reading_list_list", new { shelf = "wishlist" }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);
        (await Invoke(h, "reading_list_list", new { limit = 500 }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);
    }

    [Fact]
    public async Task Reading_list_add_adds_once_and_returns_the_existing_entry_after_that()
    {
        using var h = CreateHarness();

        var first = await Invoke(h, "reading_list_add",
            new { title = "The Waves", author = "Virginia Woolf", why = "For the essay on time", readNext = true });
        var again = await Invoke(h, "reading_list_add", new { title = "the waves", author = "virginia woolf" });

        first.Success.Should().BeTrue(first.ErrorMessage);
        first.Data!.Value.GetProperty("duplicate").GetBoolean().Should().BeFalse();
        first.Data!.Value.GetProperty("entry").GetProperty("shelf").GetString().Should().Be("next");
        again.Success.Should().BeTrue(again.ErrorMessage);
        again.Data!.Value.GetProperty("duplicate").GetBoolean().Should().BeTrue();
        again.Data!.Value.GetProperty("entry").GetProperty("id").GetGuid()
            .Should().Be(first.Data!.Value.GetProperty("entry").GetProperty("id").GetGuid());

        await using var db = await h.Factory.CreateDbContextAsync();
        var entry = await db.ReadingListEntries.AsNoTracking().SingleAsync();
        entry.Why.Should().Be("For the essay on time");
    }

    [Fact]
    public async Task Reading_list_add_refuses_invalid_arguments_with_the_services_codes()
    {
        using var h = CreateHarness();

        (await Invoke(h, "reading_list_add", new { author = "Nobody" }))
            .ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);
        (await Invoke(h, "reading_list_add", new { title = "Bad ISBN", isbn = "123" }))
            .ErrorCode.Should().Be(ReadingListErrorCodes.InvalidIsbn);

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.ReadingListEntries.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // book_text_find_quote with an explicit book
    // ------------------------------------------------------------------

    [Fact]
    public async Task Find_quote_uses_an_explicit_book_id_and_needs_one_without_an_open_book()
    {
        using var h = CreateHarness();

        var missingBook = await Invoke(h, "book_text_find_quote", new { startsWith = "a", endsWith = "b" });
        missingBook.ErrorCode.Should().Be(AssistantErrorCodes.InvalidArguments);

        var unknown = await Invoke(h, "book_text_find_quote",
            new { bookId = Guid.NewGuid(), startsWith = "the time of", endsWith = "came back" });
        unknown.Success.Should().BeTrue(unknown.ErrorMessage);
        unknown.Data!.Value.GetProperty("status").GetString().Should().Be("book_not_found");
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static Task<AssistantToolResult> Invoke(Harness h, string name, object args) =>
        h.Registry.InvokeAsync(
            name,
            JsonSerializer.SerializeToElement(args),
            new AssistantToolContext("client", Guid.NewGuid().ToString()));

    private Harness CreateHarness()
    {
        var path = _fixture.CreateDatabasePath();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        using (var bootstrap = new NostosDbContext(options))
        {
            bootstrap.Database.EnsureCreated();
        }

        var factory = new TestContextFactory(options);
        var db = new NostosDbContext(options);
        var topics = new TopicRepository(db);
        var noteService = NoteServiceGraph.Create(db, new FakeThoughtProcessor());
        var libraryService = LibraryServiceGraph.Create(
            factory,
            new BookLookupService(new NoopHttpClientFactory(), new SilentLogger<BookLookupService>()));

        var registry = new AssistantCapabilityRegistry(
            AssistantCapabilities.Build(
                noteService,
                libraryService,
                topics,
                NoOpKnowledgeRetrievalService.Instance,
                bookText: null,
                quotes: new BookTextQuoteFinder(new SqliteBookTextIndex(factory), libraryService),
                writings: new WritingRepository(db),
                readingList: new ReadingListService(db, noteService)));

        return new Harness(db, factory, registry);
    }

    private static async Task<PhysicalBookModel> SeedBookAsync(Harness h, string title, string? author)
    {
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = title, Author = author };
        h.Db.Books.Add(book);
        await h.Db.SaveChangesAsync();
        return book;
    }

    private static async Task<NoteModel> SeedNoteAsync(Harness h, Guid bookId, string content, DateTime createdAt)
    {
        var note = new NoteModel { Id = Guid.NewGuid(), BookId = bookId, Content = content, CreatedAt = createdAt };
        h.Db.Notes.Add(note);
        await h.Db.SaveChangesAsync();
        return note;
    }

    private static async Task<WritingModel> SeedWritingAsync(
        Harness h, string name, WritingType type, Guid? parentId = null, string? content = null)
    {
        var writing = new WritingModel
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = type,
            ParentId = parentId,
            Content = content,
        };
        h.Db.Writings.Add(writing);
        await h.Db.SaveChangesAsync();
        return writing;
    }

    private sealed class Harness(
        NostosDbContext db,
        IDbContextFactory<NostosDbContext> factory,
        AssistantCapabilityRegistry registry) : IDisposable
    {
        public NostosDbContext Db { get; } = db;
        public IDbContextFactory<NostosDbContext> Factory { get; } = factory;
        public AssistantCapabilityRegistry Registry { get; } = registry;

        public void Dispose() => Db.Dispose();
    }

    private sealed class TestContextFactory(DbContextOptions<NostosDbContext> options)
        : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);

        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
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
