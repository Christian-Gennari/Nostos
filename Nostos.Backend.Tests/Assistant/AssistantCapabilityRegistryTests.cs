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
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// The safe assistant action surface (issue #260 §5, §6). These tests pin the
/// exact capability set, prove the PlanAndAct approval guard refuses before any
/// store is touched, prove Suggest never mutates, and prove capture is
/// immediate and exactly-once.
/// </summary>
public sealed class AssistantCapabilityRegistryTests : IClassFixture<SqliteTestFixture>
{
    // The whole surface, in build order. A future addition must change this
    // assertion deliberately — that is the point of pinning it.
    private static readonly string[] ExpectedSurface =
    [
        "library_resolve_book",
        "library_list_books",
        "library_get_book",
        "library_overview",
        "library_create_or_match_book",
        "knowledge_search",
        "knowledge_overview",
        "knowledge_read_evidence",
        "notes_list_for_book",
        "notes_search",
        "notes_list_unlinked",
        "notes_read_for_review",
        "topics_list",
        "topics_search",
        "topics_propose_links",
        "library_list_collections",
        "library_get_collection",
        "notes_capture",
        "notes_link_existing_topic",
        "library_create_collection",
        "library_rename_collection",
        "library_move_collection",
        "library_delete_collection",
        "library_add_books_to_collection",
        "library_remove_books_from_collection",
        "library_set_book_collections",
        "library_update_book",
    ];

    private readonly SqliteTestFixture _fixture;

    public AssistantCapabilityRegistryTests(SqliteTestFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------
    // The surface itself
    // ------------------------------------------------------------------

    [Fact]
    public void Registry_exposes_exactly_the_documented_surface_and_a_trust_class()
    {
        var h = CreateHarness();

        h.Registry.All.Select(c => c.Name).Should().Equal(ExpectedSurface);
        h.Registry.All.Should().OnlyContain(c => Enum.IsDefined(c.Trust));
        h.Registry.All.Select(c => c.Trust).Should().Contain([
            AssistantTrustClass.Capture,
            AssistantTrustClass.Suggest,
            AssistantTrustClass.Act,
            AssistantTrustClass.Stage,
        ]);
    }

    [Fact]
    public void Every_library_organisation_capability_is_staged_and_none_deletes_a_book()
    {
        var h = CreateHarness();

        h.Registry.All
            .Where(c => c.Name is "library_create_collection" or "library_rename_collection"
                or "library_move_collection" or "library_delete_collection"
                or "library_add_books_to_collection" or "library_remove_books_from_collection"
                or "library_set_book_collections" or "library_update_book")
            .Should().HaveCount(8).And.OnlyContain(c => c.Trust == AssistantTrustClass.Stage);
        h.Registry.All.Select(c => c.Name).Should().NotContain(["library_delete_book", "notes_delete"]);
    }

    [Fact]
    public async Task Unknown_capability_is_a_typed_failure()
    {
        var h = CreateHarness();

        // Even the exact name a deletion route would have is simply absent.
        var result = await h.Registry.InvokeAsync(
            "library_delete_book", Args("{}"), new AssistantToolContext());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(AssistantErrorCodes.UnknownCapability);
    }

    // ------------------------------------------------------------------
    // Staging
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_staged_capability_is_refused_outside_a_turn_and_writes_nothing()
    {
        var h = CreateHarness();
        var before = await StoreSnapshotAsync(h);

        foreach (var capability in h.Registry.All.Where(c => c.Trust == AssistantTrustClass.Stage))
        {
            var result = await h.Registry.InvokeAsync(
                capability.Name, Args("""{"name":"X","collection":"X"}"""), new AssistantToolContext("client", "key"));

            result.Success.Should().BeFalse(capability.Name);
            result.ErrorCode.Should().Be(AssistantErrorCodes.ChangesetUnavailable, capability.Name);
        }

        (await StoreSnapshotAsync(h)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task A_staged_capability_previews_and_never_writes()
    {
        var h = CreateHarness();
        var before = await CollectionCountAsync(h);
        var draft = new AssistantChangesetDraft(h.Library, h.Library);

        var result = await h.Registry.InvokeAsync(
            "library_create_collection",
            Args("""{"name":"Staged Collection"}"""),
            new AssistantToolContext("client", "stage-key", draft));

        result.Success.Should().BeTrue(result.ErrorMessage);
        draft.Steps.Should().ContainSingle().Which.Summary.Should().Be("Create collection “Staged Collection”");
        draft.Diff!.Counts.CollectionsCreated.Should().Be(1);
        (await CollectionCountAsync(h)).Should().Be(before);
    }

    [Fact]
    public async Task Capture_executes_immediately_and_the_note_exists()
    {
        var h = CreateHarness();
        var book = await SeedBookAsync(h);

        var result = await h.Registry.InvokeAsync(
            "notes_capture",
            Args($$"""{"bookId":"{{book.Id}}","content":"A captured thought","captureSource":"voice"}"""),
            new AssistantToolContext("client", "capture-1"));

        result.Success.Should().BeTrue();

        await using var db = await h.Factory.CreateDbContextAsync();
        var note = await db.Notes.AsNoTracking().SingleAsync();
        note.BookId.Should().Be(book.Id);
        note.Content.Should().Be("A captured thought");
        note.CaptureSource.Should().Be("voice");
    }

    [Fact]
    public async Task Capture_is_exactly_once_for_the_same_client_and_idempotency_key()
    {
        var h = CreateHarness();
        var book = await SeedBookAsync(h);
        var context = new AssistantToolContext("client-1", "idempotent-1");
        var args = Args($$"""{"bookId":"{{book.Id}}","content":"Captured once"}""");

        var first = await h.Registry.InvokeAsync("notes_capture", args, context);
        var second = await h.Registry.InvokeAsync("notes_capture", args, context);

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Notes.CountAsync()).Should().Be(1);
    }

    // ------------------------------------------------------------------
    // Suggest never mutates
    // ------------------------------------------------------------------

    [Fact]
    public async Task Suggest_capabilities_never_mutate_notes_collections_or_topics()
    {
        var h = CreateHarness();
        var book = await SeedBookAsync(h, "Seeded Book");
        var note = await SeedNoteAsync(h, book.Id, "seeded note text");
        var topic = await SeedTopicAsync(h, "seeded topic");
        var collection = await SeedCollectionAsync(h, "Seeded Collection");

        var before = await StoreSnapshotAsync(h);

        var calls = new (string Name, string Args)[]
        {
            ("library_resolve_book", """{"title":"Seeded Book","author":"Author","includeExternalMetadata":false}"""),
            ("library_list_books", "{}"),
            ("library_get_book", $$"""{"bookId":"{{book.Id}}"}"""),
            ("library_overview", "{}"),
            ("knowledge_search", """{"query":"seeded"}"""),
            ("knowledge_overview", "{}"),
            ("knowledge_read_evidence", JsonSerializer.Serialize(new { kind = "note", noteId = note.Id })),
            ("notes_list_for_book", JsonSerializer.Serialize(new { bookId = book.Id })),
            ("notes_search", """{"query":"seeded"}"""),
            ("notes_list_unlinked", "{}"),
            ("notes_read_for_review", $$"""{"noteId":"{{note.Id}}"}"""),
            ("topics_list", "{}"),
            ("topics_search", """{"term":"seeded"}"""),
            ("library_list_collections", "{}"),
            ("library_get_collection", $$"""{"collectionId":"{{collection.Id}}"}"""),
        };

        foreach (var (name, args) in calls)
        {
            var result = await h.Registry.InvokeAsync(name, Args(args), new AssistantToolContext());
            result.Success.Should().BeTrue($"{name} is a read and must succeed");
        }

        (await StoreSnapshotAsync(h)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Book_actions_can_create_then_replace_collection_membership()
    {
        var h = CreateHarness();
        var collection = await SeedCollectionAsync(h, "German Literature");

        var created = await h.Registry.InvokeAsync(
            "library_create_or_match_book",
            Args($$"""
            {
              "type":"physical",
              "title":"The Magic Mountain",
              "author":"Thomas Mann",
              "collectionIds":["{{collection.Id}}"]
            }
            """),
            new AssistantToolContext("client", "book-create"));

        created.Success.Should().BeTrue();

        await using var db = await h.Factory.CreateDbContextAsync();
        var book = await db.Books.AsNoTracking().SingleAsync(b => b.Title == "The Magic Mountain");
        var membership = await db.BookCollections.AsNoTracking()
            .Where(link => link.BookId == book.Id)
            .Select(link => link.CollectionId)
            .ToListAsync();
        membership.Should().Equal(collection.Id);

        var updated = await StageAndApplyAsync(h,
            "library_set_book_collections",
            $$"""{"updates":[{"bookId":"{{book.Id}}","collections":[]}]}""");

        updated.Success.Should().BeTrue(updated.ErrorMessage);

        var after = await db.BookCollections.AsNoTracking()
            .Where(link => link.BookId == book.Id)
            .CountAsync();
        after.Should().Be(0);
    }

    [Fact]
    public async Task Add_book_matches_on_a_second_request_instead_of_duplicating()
    {
        var h = CreateHarness();
        var args = Args(JsonSerializer.Serialize(new
        {
            type = "physical",
            title = "The Magic Mountain",
            author = "Thomas Mann",
        }));

        var first = await h.Registry.InvokeAsync(
            "library_create_or_match_book",
            args,
            new AssistantToolContext("client", "book-add-1"));
        var second = await h.Registry.InvokeAsync(
            "library_create_or_match_book",
            args,
            new AssistantToolContext("client", "book-add-2"));

        first.Success.Should().BeTrue();
        second.Success.Should().BeTrue();

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Books.CountAsync(b => b.Title == "The Magic Mountain")).Should().Be(1);

        var payload = second.Data!.Value.GetProperty("data");
        payload.GetProperty("outcome").GetString().Should().Be("matched");
    }

    [Fact]
    public async Task Update_book_sets_replaces_and_clears_the_description()
    {
        var h = CreateHarness();
        var book = await SeedBookAsync(h, "Essays and Aphorisms", "Schopenhauer");

        async Task<string?> UpdateAndReadAsync(object args, string key)
        {
            var result = await StageAndApplyAsync(h, "library_update_book", JsonSerializer.Serialize(args));
            result.Success.Should().BeTrue(result.ErrorMessage);
            await using var db = await h.Factory.CreateDbContextAsync();
            return (await db.Books.AsNoTracking().SingleAsync(b => b.Id == book.Id)).Metadata.Description;
        }

        (await UpdateAndReadAsync(new { bookId = book.Id, description = "A selection from Parerga." }, "d1"))
            .Should().Be("A selection from Parerga.", "a book without a description can be given one");
        (await UpdateAndReadAsync(new { bookId = book.Id, description = "Short essays." }, "d2"))
            .Should().Be("Short essays.", "an existing description can be replaced");
        (await UpdateAndReadAsync(new { bookId = book.Id, rating = 4 }, "d3"))
            .Should().Be("Short essays.", "omitting description leaves it unchanged");
        (await UpdateAndReadAsync(new { bookId = book.Id, description = "" }, "d4"))
            .Should().BeNull("an empty string clears the description");
    }

    [Fact]
    public async Task Update_book_description_preserves_other_metadata_and_memberships()
    {
        var h = CreateHarness();
        var collection = await SeedCollectionAsync(h, "Philosophy");
        var book = await SeedBookAsync(h, "Essays and Aphorisms", "Schopenhauer");

        (await StageAndApplyAsync(h, "library_add_books_to_collection",
            JsonSerializer.Serialize(new { collection = collection.Id, bookIds = new[] { book.Id } }))).Success.Should().BeTrue();
        (await StageAndApplyAsync(h, "library_update_book",
            JsonSerializer.Serialize(new { bookId = book.Id, rating = 5 }))).Success.Should().BeTrue();
        (await StageAndApplyAsync(h, "library_update_book",
            JsonSerializer.Serialize(new { bookId = book.Id, description = "Short essays." }))).Success.Should().BeTrue();

        await using var db = await h.Factory.CreateDbContextAsync();
        var stored = await db.Books.AsNoTracking().SingleAsync(b => b.Id == book.Id);
        stored.Metadata.Description.Should().Be("Short essays.");
        stored.Title.Should().Be("Essays and Aphorisms");
        stored.Author.Should().Be("Schopenhauer");
        stored.Progress.Rating.Should().Be(5);
        (await db.BookCollections.AsNoTracking().Where(link => link.BookId == book.Id)
            .Select(link => link.CollectionId).ToListAsync())
            .Should().Equal(collection.Id);
    }

    [Fact]
    public async Task Bulk_collection_membership_routes_each_book_through_the_canonical_service()
    {
        var h = CreateHarness();
        var collection = await SeedCollectionAsync(h, "Russian Literature");
        var first = await SeedBookAsync(h, "The Devils");
        var second = await SeedBookAsync(h, "The Brothers Karamazov");

        var result = await StageAndApplyAsync(h,
            "library_set_book_collections",
            JsonSerializer.Serialize(new
            {
                updates = new[]
                {
                    new { bookId = first.Id, collections = new[] { collection.Id } },
                    new { bookId = second.Id, collections = new[] { collection.Id } },
                },
            }));

        result.Success.Should().BeTrue(result.ErrorMessage);

        await using var db = await h.Factory.CreateDbContextAsync();
        var memberships = await db.BookCollections.AsNoTracking()
            .Where(link => link.CollectionId == collection.Id)
            .Select(link => link.BookId)
            .OrderBy(id => id)
            .ToListAsync();

        memberships.Should().BeEquivalentTo([first.Id, second.Id]);
    }

    /// <summary>Stages one capability call into a fresh draft and applies what it staged.</summary>
    private static async Task<LibraryChangesetResult> StageAndApplyAsync(Harness h, string capability, string argsJson)
    {
        var draft = new AssistantChangesetDraft(h.Library, h.Library);
        var staged = await h.Registry.InvokeAsync(capability, Args(argsJson), new AssistantToolContext("client", "stage", draft));
        if (!staged.Success)
            return LibraryChangesetResult.Fail(staged.ErrorCode ?? "not_staged", staged.ErrorMessage ?? string.Empty, "0");
        return await h.Library.ApplyAsync(draft.Steps.Select(step => step.Op).ToList(), draft.DiffHash);
    }

    [Fact]
    public async Task Library_overview_reads_the_complete_library_not_only_the_first_page()
    {
        var h = CreateHarness();
        for (var i = 1; i <= 105; i++)
        {
            await SeedBookAsync(h, $"Book {i:000}", $"Author {i:000}");
        }
        await SeedCollectionAsync(h, "Philosophy");

        var result = await h.Registry.InvokeAsync(
            "library_overview",
            Args("{}"),
            new AssistantToolContext());

        result.Success.Should().BeTrue();
        var data = result.Data!.Value;
        data.GetProperty("totalBooks").GetInt32().Should().Be(105);
        data.GetProperty("books").GetArrayLength().Should().Be(105);
        data.GetProperty("collections").GetArrayLength().Should().Be(1);
    }

    // ------------------------------------------------------------------
    // Linking never creates a topic
    // ------------------------------------------------------------------

    [Fact]
    public async Task Linking_a_chosen_existing_topic_is_an_immediate_action()
    {
        var h = CreateHarness();
        var book = await SeedBookAsync(h);
        var note = await SeedNoteAsync(h, book.Id, "a note");
        var topic = await SeedTopicAsync(h, "Alienation");

        var result = await h.Registry.InvokeAsync(
            "notes_link_existing_topic",
            Args(JsonSerializer.Serialize(new
            {
                noteId = note.Id,
                topicId = topic.Id,
            })),
            new AssistantToolContext("client", "link-existing"));

        result.Success.Should().BeTrue();

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.NoteTopics.CountAsync(link =>
            link.NoteId == note.Id && link.TopicId == topic.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Linking_an_unknown_topic_is_a_typed_failure_and_creates_no_topic()
    {
        var h = CreateHarness();
        var book = await SeedBookAsync(h);
        var note = await SeedNoteAsync(h, book.Id, "a note");

        var result = await h.Registry.InvokeAsync(
            "notes_link_existing_topic",
            Args($$"""{"noteId":"{{note.Id}}","topicId":"{{Guid.NewGuid()}}"}"""),
            new AssistantToolContext("client", "link-key"));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("topic_not_found");

        await using var db = await h.Factory.CreateDbContextAsync();
        (await db.Topics.CountAsync()).Should().Be(0);
        (await db.NoteTopics.CountAsync()).Should().Be(0);
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

        using (var bootstrap = new NostosDbContext(options))
        {
            bootstrap.Database.EnsureCreated();
        }

        var factory = new TestContextFactory(options);
        var db = new NostosDbContext(options);

        // The same scoped context backs every note repository, exactly as the
        // web host wires it.
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

        var libraryService = new LibraryService(
            factory,
            new BookLookupService(new NoopHttpClientFactory(), new SilentLogger<BookLookupService>()));

        var bookTextIndex = new NoOpBookTextIndex();
        var bookText = new BookTextSearchService(
            bookTextIndex,
            libraryService,
            new BookTextOptions());
        var knowledge = new KnowledgeRetrievalService(
            noteService,
            noteRepository,
            topics,
            libraryService,
            bookText,
            bookTextIndex,
            []);

        var registry = new AssistantCapabilityRegistry(
            AssistantCapabilities.Build(noteService, libraryService, topics, knowledge));

        return new Harness(db, factory, registry, libraryService);
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<PhysicalBookModel> SeedBookAsync(
        Harness h, string title = "Seeded Book", string? author = "Author")
    {
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = title, Author = author };
        h.Db.Books.Add(book);
        await h.Db.SaveChangesAsync();
        return book;
    }

    private static async Task<NoteModel> SeedNoteAsync(Harness h, Guid bookId, string content)
    {
        var note = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Content = content,
            CreatedAt = DateTime.UtcNow,
        };
        h.Db.Notes.Add(note);
        await h.Db.SaveChangesAsync();
        return note;
    }

    private static async Task<TopicModel> SeedTopicAsync(Harness h, string name)
    {
        var topic = new TopicModel { Id = Guid.NewGuid(), Topic = name };
        h.Db.Topics.Add(topic);
        await h.Db.SaveChangesAsync();
        return topic;
    }

    private static async Task<CollectionModel> SeedCollectionAsync(Harness h, string name)
    {
        var collection = new CollectionModel { Id = Guid.NewGuid(), Name = name };
        h.Db.Collections.Add(collection);
        await h.Db.SaveChangesAsync();
        return collection;
    }

    private static async Task<int> CollectionCountAsync(Harness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();
        return await db.Collections.CountAsync();
    }

    /// <summary>Count plus content for every row in the three stores the assistant can reach.</summary>
    private static async Task<StoreSnapshot> StoreSnapshotAsync(Harness h)
    {
        await using var db = await h.Factory.CreateDbContextAsync();

        var notes = await db.Notes.AsNoTracking()
            .OrderBy(n => n.Id)
            .Select(n => new { n.Id, n.Content })
            .ToListAsync();
        var topics = await db.Topics.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.Topic })
            .ToListAsync();
        var collections = await db.Collections.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.ParentId })
            .ToListAsync();

        return new StoreSnapshot(
            notes.Select(n => $"{n.Id}:{n.Content}").ToList(),
            topics.Select(c => $"{c.Id}:{c.Topic}").ToList(),
            collections.Select(c => $"{c.Id}:{c.Name}:{c.ParentId}").ToList());
    }

    private sealed record StoreSnapshot(
        IReadOnlyList<string> Notes,
        IReadOnlyList<string> Topics,
        IReadOnlyList<string> Collections);

    private sealed class Harness(
        NostosDbContext db,
        IDbContextFactory<NostosDbContext> factory,
        AssistantCapabilityRegistry registry,
        LibraryService library) : IDisposable
    {
        public NostosDbContext Db { get; } = db;
        public IDbContextFactory<NostosDbContext> Factory { get; } = factory;
        public AssistantCapabilityRegistry Registry { get; } = registry;
        public LibraryService Library { get; } = library;

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
