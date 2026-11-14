using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Mapping;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Npgsql;
using Xunit;

namespace Nostos.Backend.Tests.Data;

/// <summary>
/// List, note and progress queries leave LocationsJson in the database (#1317).
/// Each scenario runs on SQLite here, and on real PostgreSQL in the
/// <c>PostgresSpike</c> job, against the SQL each provider actually sent.
/// </summary>
public sealed class BookReadShapesTests : IClassFixture<SqliteTestFixture>
{
    private const string Cache = "LOCATIONS-CACHE";
    private const string SpikeConnection = "NOSTOS_POSTGRES_SPIKE_CONNECTION";

    private readonly SqliteTestFixture _fixture;

    public BookReadShapesTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public Task List_and_note_reads_do_not_select_the_locations_on_sqlite() =>
        OnSqliteAsync(ReadsLeaveTheLocationsBehindAsync);

    [Fact]
    public Task A_position_save_keeps_every_side_effect_on_sqlite() =>
        OnSqliteAsync(PositionSaveKeepsItsSideEffectsAsync);

    [Fact]
    public Task The_list_shape_carries_every_column_but_the_locations_on_sqlite() =>
        OnSqliteAsync(ShapeCarriesEveryColumnAsync);

    [Fact]
    public Task A_shaped_book_maps_to_the_same_dto_as_an_entity_on_sqlite() =>
        OnSqliteAsync(ShapedDtoMatchesEntityDtoAsync);

    [Fact]
    [Trait("Category", "PostgresSpike")]
    public async Task Every_scenario_holds_on_postgresql()
    {
        var admin = Environment.GetEnvironmentVariable(SpikeConnection);
        if (string.IsNullOrWhiteSpace(admin))
        {
            // Zero-infrastructure by default, like the spike beside it.
            return;
        }

        await OnPostgresAsync(admin, ReadsLeaveTheLocationsBehindAsync);
        await OnPostgresAsync(admin, PositionSaveKeepsItsSideEffectsAsync);
        await OnPostgresAsync(admin, ShapeCarriesEveryColumnAsync);
        await OnPostgresAsync(admin, ShapedDtoMatchesEntityDtoAsync);
    }

    [Fact]
    public void Npgsql_translates_the_shapes_without_a_database()
    {
        using var db = new NostosDbContext(new DbContextOptionsBuilder<NostosDbContext>()
            .UseNpgsql("Host=localhost;Database=never-opened")
            .Options);

        var page = db.Books.AsNoTracking()
            .Where(b => b.Status != BookStatus.UploadPending)
            .OrderByDescending(b => b.CreatedAt)
            .Skip(20)
            .Take(10)
            .Select(BookReadShapes.WithoutLocations)
            .ToQueryString();
        var notes = db.Notes.AsNoTracking()
            .Include(n => n.NoteTopics).ThenInclude(nc => nc.Topic)
            .OrderBy(n => n.CreatedAt)
            .Select(BookReadShapes.NoteWithBookHeadline)
            .ToQueryString();

        db.Books.AsNoTracking().ToQueryString().Should().Contain(
            "FileDetails_LocationsJson", "this is the column these shapes exist to leave out");
        page.Should().NotContain("LocationsJson").And.Contain("FileDetails_ChaptersJson");
        notes.Should().NotContain("LocationsJson").And.NotContain("ChaptersJson").And.Contain("\"Title\"");
    }

    // ------------------------------------------------------------------
    // Scenarios
    // ------------------------------------------------------------------

    private static async Task ReadsLeaveTheLocationsBehindAsync(Store store)
    {
        var seed = await SeedAsync(store);
        var library = store.Library;

        await using var db = await store.Factory.CreateDbContextAsync();
        var notes = new NoteRepository(db);
        var topics = new TopicRepository(db);
        var noteService = NoteServiceGraph.Create(db, new FakeThoughtProcessor());

        store.Commands.Clear();

        var grouped = (PaginatedResponse<BookDto>)(await library.ListBooksAsync(
            BookFilter.All, BookSort.Title, null, 1, 50, null, groupByWork: true)).Data!;
        grouped.Items.Should().ContainSingle(b => b.WorkId == seed.WorkId);
        var groupedRow = grouped.Items.Single(b => b.WorkId == seed.WorkId);
        groupedRow.EditionCount.Should().Be(3);
        groupedRow.OtherEditions.Should().HaveCount(2);
        groupedRow.Rating.Should().Be(4, "the rating is the work's");

        var flat = (PaginatedResponse<BookDto>)(await library.ListBooksAsync(
            BookFilter.All, BookSort.Title, null, 1, 50, null, groupByWork: false)).Data!;
        flat.Items.Should().HaveCount(3);
        flat.Items.Single(b => b.Id == seed.EBookId).CollectionIds.Should().Equal(seed.CollectionId);
        flat.Items.Single(b => b.Id == seed.EBookId).Source!.ProviderId.Should().Be("provider");

        var book = (BookDto)(await library.GetBookAsync(seed.EBookId)).Data!;
        book.Isbn.Should().Be("9780140455113");
        book.EditionCount.Should().Be(3);
        (await library.ResolveBookAsync(new LibraryResolveBookRequest(Isbn: "9780140455113")))
            .MatchedBook!.Id.Should().Be(seed.EBookId);
        (await library.ResolveBookAsync(new LibraryResolveBookRequest(Asin: "B000REPUBL")))
            .MatchedBook!.Id.Should().Be(seed.AudioId);
        (await library.ResolveBookAsync(new LibraryResolveBookRequest(Title: "The Republic", Author: "Plato")))
            .Candidates.Should().HaveCount(3);
        (await new BookRepository(db).GetBooksWithFilesPageAsync(0, 10))
            .Should().ContainSingle(b => b.Id == seed.EBookId);

        var byBook = await noteService.GetByBookAsync(seed.EBookId);
        byBook.Should().OnlyContain(n => n.BookTitle == "The Republic" && n.BookAuthor == "Plato");
        byBook.Should().HaveCount(3);

        var hit = await noteService.GetAsync(seed.NoteId);
        hit!.BookAuthor.Should().Be("Plato");
        hit.TopicNames.Should().Equal("justice");
        hit.WorkId.Should().Be(seed.WorkId);
        hit.EditionCount.Should().Be(3);
        hit.BookFormat.Should().Be("ebook");
        (await noteService.GetAsync(seed.AudioNoteId))!.BookFormat.Should().Be("audiobook");
        (await noteService.GetAsync(seed.PhysicalNoteId))!.BookFormat.Should().Be("physical");

        (await noteService.GetManyAsync([seed.NoteId, seed.AudioNoteId]))
            .Select(n => n.BookTitle).Should().Equal("The Republic", "The Republic");
        (await noteService.GetUnlinkedAsync(50, 0)).Items.Should().OnlyContain(n => n.BookTitle == "The Republic");
        (await noteService.BrowseAsync(new NoteBrowseQuery(BookId: seed.EBookId, Order: NoteBrowseOrder.Book), 50, 0))
            .Items.Should().HaveCount(3).And.OnlyContain(n => n.BookAuthor == "Plato");
        (await noteService.SearchAsync("plato cave", 10)).Should().NotBeEmpty();
        (await noteService.GetForReviewAsync(seed.NoteId))!.BookTitle.Should().Be("The Republic");

        var read = await notes.GetForReadingAsync(seed.NoteId);
        read!.NoteTopics.Select(link => link.Topic?.Topic).Should().Equal("justice");

        var topic = await topics.GetByIdWithNotesAsync(seed.TopicId);
        topic!.NoteTopics.Should().ContainSingle();
        topic.NoteTopics.Single().Note.Book!.Author.Should().Be("Plato");

        var writings = new WritingRepository(db);
        var kept = await writings.AddKeptNoteAsync(seed.WritingId, seed.NoteId);
        kept.Status.Should().Be(AddKeptNoteStatus.Success);
        kept.WritingNote!.Note.Book!.Title.Should().Be("The Republic");
        (await writings.AddKeptNoteAsync(seed.WritingId, seed.NoteId)).Status.Should().Be(AddKeptNoteStatus.AlreadyExists);
        (await writings.GetKeptNotesAsync(seed.WritingId))!.Single().Note.Book!.Author.Should().Be("Plato");

        // Edits read the book back for their reply, and must not write it.
        var updated = await noteService.UpdateAsync(seed.NoteId, new UpdateNoteDto("about the cave #justice"));
        updated.Value!.BookTitle.Should().Be("The Republic");
        updated.Value.BookAuthor.Should().Be("Plato");
        var linked = await noteService.LinkTopicsAsync(["virtue"], [seed.AudioNoteId, seed.NoteId]);
        linked.Value!.Notes.Select(n => n.Id).Should().Equal(seed.AudioNoteId, seed.NoteId);
        linked.Value.Notes.Should().OnlyContain(n => n.BookAuthor == "Plato");
        linked.Value.Notes[0].TopicNames.Should().Contain("virtue");

        store.Commands.Sent.Should().HaveCountGreaterThan(20);
        store.Commands.Sent.Should().NotContain(sql => sql.Contains("LocationsJson"));

        await using var check = await store.Factory.CreateDbContextAsync();
        (await check.Books.AsNoTracking().CountAsync()).Should().Be(3, "a note edit must not add a book");
        (await check.Books.AsNoTracking().Where(b => b.FileDetails.LocationsJson == Cache).CountAsync()).Should().Be(1);
    }

    private static async Task PositionSaveKeepsItsSideEffectsAsync(Store store)
    {
        var seed = await SeedAsync(store);
        var library = store.Library;
        store.Commands.Clear();

        // Reading on: the revision moves once, the feed says position only, and
        // the row changes in the four columns the save owns and no others.
        var before = await RevisionAsync(store);
        var (result, change) = await SaveAsync(store, seed.EBookId, "  epubcfi(/6/4)  ", 40);
        result.Data.Should().BeEquivalentTo(new { updated = true });
        result.Reply.Should().Contain("The Republic");
        long.Parse(result.StateVersion).Should().Be(before + 1);
        (await RevisionAsync(store)).Should().Be(before + 1);
        change.ProgressOnly.Should().BeTrue("a moved position may be picked up lazily");
        store.Commands.Sent.Should().Contain(sql => sql.Contains("Progress_LastLocation"));
        store.Commands.Sent.Should().NotContain(sql => sql.Contains("LocationsJson"));

        var book = await BookAsync(store, seed.EBookId);
        book.Progress.LastLocation.Should().Be("  epubcfi(/6/4)  ");
        book.Progress.ProgressPercent.Should().Be(40);
        book.Progress.LastReadAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        book.Progress.LastReadAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        book.Progress.FinishedAt.Should().BeNull();
        book.FileDetails.LocationsJson.Should().Be(Cache, "the save must leave the cache alone");
        book.Title.Should().Be("The Republic");

        // Finishing changes the book's status, so it is not position-only.
        (_, change) = await SaveAsync(store, seed.EBookId, "end", 140);
        change.ProgressOnly.Should().BeFalse();
        var finished = await BookAsync(store, seed.EBookId);
        finished.Progress.ProgressPercent.Should().Be(100, "the percentage is clamped");
        finished.Progress.FinishedAt.Should().NotBeNull();

        // Reading a finished book's last page again keeps the original date.
        (_, change) = await SaveAsync(store, seed.EBookId, "end", 100);
        change.ProgressOnly.Should().BeTrue();
        (await BookAsync(store, seed.EBookId)).Progress.FinishedAt.Should().Be(finished.Progress.FinishedAt);

        // Going back unfinishes it.
        (_, change) = await SaveAsync(store, seed.EBookId, "   ", -5);
        change.ProgressOnly.Should().BeFalse();
        var reopened = await BookAsync(store, seed.EBookId);
        reopened.Progress.FinishedAt.Should().BeNull();
        reopened.Progress.ProgressPercent.Should().Be(0);
        reopened.Progress.LastLocation.Should().BeNull("a blank location clears it");

        // The work's status is worked out from its editions, so it follows.
        await SaveAsync(store, seed.AudioId, "60", 100);
        var finishedList = (PaginatedResponse<BookDto>)(await library.ListBooksAsync(
            BookFilter.Finished, BookSort.Title, null, 1, 50, null, groupByWork: true)).Data!;
        finishedList.Items.Should().ContainSingle(b => b.WorkId == seed.WorkId);

        // A book that is not there changes nothing.
        var revision = await RevisionAsync(store);
        var missing = await library.UpdateProgressAsync(Guid.NewGuid(), "x", 10);
        missing.Data.Should().BeEquivalentTo(new LibraryErrorDto("book_not_found"));
        (await RevisionAsync(store)).Should().Be(revision);
    }

    /// <summary>
    /// A column added to a book and forgotten in the shape would silently read as
    /// empty on every list. This walks the EF model, so it fails for that column.
    /// </summary>
    private static async Task ShapeCarriesEveryColumnAsync(Store store)
    {
        var seed = await SeedAsync(store);
        await using var db = await store.Factory.CreateDbContextAsync();

        foreach (var id in new[] { seed.EBookId, seed.AudioId, seed.PhysicalId })
        {
            var entity = await db.Books.AsNoTracking().SingleAsync(b => b.Id == id);
            var shaped = await db.Books.AsNoTracking()
                .Where(b => b.Id == id)
                .Select(BookReadShapes.WithoutLocations)
                .SingleAsync();

            shaped.GetType().Should().Be(entity.GetType());
            AssertSameColumns(db.Model.FindEntityType(entity.GetType())!, entity, shaped);
        }
    }

    private static void AssertSameColumns(IEntityType type, object entity, object shaped)
    {
        foreach (var property in type.GetProperties().Where(p => p.PropertyInfo is not null))
        {
            var expected = property.PropertyInfo!.GetValue(entity);
            var actual = property.PropertyInfo!.GetValue(shaped);
            if (property.Name == nameof(FileInfoDetails.LocationsJson))
            {
                actual.Should().BeNull();
                continue;
            }

            actual.Should().Be(expected, $"{type.ShortName()}.{property.Name} has to be in the shape");
        }

        foreach (var navigation in type.GetNavigations().Where(n => n.ForeignKey.IsOwnership))
        {
            AssertSameColumns(
                navigation.TargetEntityType,
                navigation.PropertyInfo!.GetValue(entity)!,
                navigation.PropertyInfo!.GetValue(shaped)!);
        }
    }

    private static async Task ShapedDtoMatchesEntityDtoAsync(Store store)
    {
        var seed = await SeedAsync(store);
        await using var db = await store.Factory.CreateDbContextAsync();

        foreach (var id in new[] { seed.EBookId, seed.AudioId, seed.PhysicalId })
        {
            var entity = await db.Books.AsNoTracking()
                .Include(b => b.BookCollections)
                .Include(b => b.Acquisition)
                .SingleAsync(b => b.Id == id);
            var shaped = await db.Books.AsNoTracking()
                .Where(b => b.Id == id)
                .Select(BookReadShapes.WithoutLocations)
                .SingleAsync();

            JsonSerializer.Serialize(shaped.ToDto()).Should().Be(JsonSerializer.Serialize(entity.ToDto()));
        }
    }

    // ------------------------------------------------------------------
    // Support
    // ------------------------------------------------------------------

    private static async Task<(LibraryCommandResultDto Result, LibraryChange Change)> SaveAsync(
        Store store, Guid bookId, string location, int percentage)
    {
        await using var db = await store.Factory.CreateDbContextAsync();
        var feed = LibraryChangeSignal.NextAsync(LibraryChangeSignal.KeyFor(db));
        var result = await store.Library.UpdateProgressAsync(bookId, location, percentage);
        return (result, await feed.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<long> RevisionAsync(Store store)
    {
        await using var db = await store.Factory.CreateDbContextAsync();
        var state = await db.LibraryStates.AsNoTracking().SingleOrDefaultAsync();
        return long.Parse(state?.StateVersion ?? "0");
    }

    private static async Task<BookModel> BookAsync(Store store, Guid id)
    {
        await using var db = await store.Factory.CreateDbContextAsync();
        return await db.Books.AsNoTracking().SingleAsync(b => b.Id == id);
    }

    private sealed record Seed(
        Guid WorkId,
        Guid EBookId,
        Guid AudioId,
        Guid PhysicalId,
        Guid CollectionId,
        Guid NoteId,
        Guid AudioNoteId,
        Guid PhysicalNoteId,
        Guid TopicId,
        Guid WritingId);

    private static async Task<Seed> SeedAsync(Store store)
    {
        await using var db = await store.Factory.CreateDbContextAsync();

        var work = new WorkModel
        {
            Title = "The Republic",
            Author = "Plato",
            NormalizedTitle = BookIdentityNormalizer.NormalizeTitle("The Republic"),
            NormalizedAuthor = BookIdentityNormalizer.NormalizeAuthor("Plato"),
            Rating = 4,
            IsFavorite = true,
            PersonalReview = "the work's review",
        };

        var ebook = Filled(new EBookModel { Isbn = "9780140455113", PageCount = 416 }, work);
        ebook.NormalizedIsbn = "9780140455113";
        ebook.FileDetails.LocationsJson = Cache;
        var audio = Filled(new AudioBookModel { Asin = "B000REPUBL", Duration = "13:42:00", Narrator = "A Reader" }, work);
        audio.NormalizedAsin = "B000REPUBL";
        audio.FileDetails.HasFile = false;
        audio.FileDetails.FileName = null;
        // A row with no publication details at all: its owned metadata is every
        // column null, which reads back as no row.
        var physical = new PhysicalBookModel { Title = "The Republic", Author = "Plato", Work = work, Isbn = "9780000000002", PageCount = 300 };

        var collection = new CollectionModel { Name = "Philosophy" };
        ebook.BookCollections.Add(new BookCollectionModel { Collection = collection });
        ebook.Acquisition = new BookAcquisitionModel
        {
            ProviderId = "provider",
            ProviderDisplayName = "A Provider",
            ExternalId = "external",
            AssetId = "asset",
            AssetFormat = "epub",
            SourceUrl = "https://example.test/republic",
            RightsStatement = "public domain",
        };

        var topic = new TopicModel { Topic = "justice" };
        var created = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
        var note = new NoteModel { Book = ebook, Content = "about the cave #justice", CreatedAt = created };
        note.NoteTopics.Add(new NoteTopicModel { Topic = topic });
        var second = new NoteModel { Book = ebook, Content = "second", CreatedAt = created.AddMinutes(1) };
        var third = new NoteModel { Book = ebook, Content = "third", CreatedAt = created.AddMinutes(2) };
        var audioNote = new NoteModel { Book = audio, Content = "heard", CreatedAt = created.AddMinutes(3) };
        var physicalNote = new NoteModel { Book = physical, Content = "read", CreatedAt = created.AddMinutes(4) };

        var writing = new WritingModel { Name = "Essay", Type = WritingType.Document, Content = "text" };

        db.AddRange(work, ebook, audio, physical, note, second, third, audioNote, physicalNote, writing);
        await db.SaveChangesAsync();

        return new Seed(
            work.Id, ebook.Id, audio.Id, physical.Id, collection.Id,
            note.Id, audioNote.Id, physicalNote.Id, topic.Id, writing.Id);
    }

    private static T Filled<T>(T book, WorkModel work)
        where T : BookModel
    {
        book.Work = work;
        book.Status = BookStatus.Ready;
        book.StatusMessage = "a status";
        book.Title = "The Republic";
        book.Author = "Plato";
        book.CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        book.Metadata = new BookMetadata
        {
            Subtitle = "subtitle",
            Description = "description",
            Editor = "editor",
            Translator = "translator",
            Publisher = "publisher",
            PlaceOfPublication = "place",
            PublishedDate = "1955",
            Language = "en",
            Categories = "philosophy",
            Edition = "second",
            Series = "classics",
            VolumeNumber = "1",
        };
        book.Progress = new ReadingProgress
        {
            LastLocation = "start",
            ProgressPercent = 12,
            Rating = 3,
            PersonalReview = "this edition's review",
            LastReadAt = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc),
        };
        book.FileDetails = new FileInfoDetails
        {
            HasFile = true,
            FileName = "republic.epub",
            CoverFileName = "cover.jpg",
            CoverVersion = "abc",
            ChaptersJson = """[{"title":"One","start":0}]""",
            TracksJson = null,
        };
        return book;
    }

    private async Task OnSqliteAsync(Func<Store, Task> scenario)
    {
        var commands = new SentCommands();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_fixture.CreateDatabasePath()}")
            .AddInterceptors(commands)
            .Options;
        await using (var bootstrap = new NostosDbContext(options))
        {
            await bootstrap.Database.EnsureCreatedAsync();
        }

        await scenario(new Store(options, commands));
    }

    /// <summary>In a database of its own: the spike beside this expects an empty one.</summary>
    private static async Task OnPostgresAsync(string admin, Func<Store, Task> scenario)
    {
        var name = $"book_read_shapes_{Guid.NewGuid():N}";
        await using var server = new NpgsqlConnection(admin);
        await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", server))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var commands = new SentCommands();
            var connection = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
            var options = new DbContextOptionsBuilder<NostosDbContext>()
                .UseNpgsql(connection, npgsql => npgsql.MigrationsAssembly(typeof(Program).Assembly.FullName))
                .AddInterceptors(commands)
                .Options;
            await using (var bootstrap = new NostosDbContext(options))
            {
                await new DatabaseBootstrapService(bootstrap).EnsureReadyAsync();
            }

            await scenario(new Store(options, commands));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class Store
    {
        public Store(DbContextOptions<NostosDbContext> options, SentCommands commands)
        {
            Factory = new ContextFactory(options);
            Commands = commands;
            Library = LibraryServiceGraph.Create(
                Factory,
                new BookLookupService(new NoHttp(), NullLogger<BookLookupService>.Instance));
        }

        public IDbContextFactory<NostosDbContext> Factory { get; }

        public SentCommands Commands { get; }

        public ILibraryService Library { get; }
    }

    private sealed class ContextFactory(DbContextOptions<NostosDbContext> options) : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);

        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>The SQL a provider was really handed, reads and writes alike.</summary>
    private sealed class SentCommands : DbCommandInterceptor
    {
        private readonly List<string> _sent = [];

        public IReadOnlyList<string> Sent
        {
            get
            {
                lock (_sent)
                {
                    return _sent.ToList();
                }
            }
        }

        public void Clear()
        {
            lock (_sent)
            {
                _sent.Clear();
            }
        }

        private void Record(DbCommand command)
        {
            lock (_sent)
            {
                _sent.Add(command.CommandText);
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
