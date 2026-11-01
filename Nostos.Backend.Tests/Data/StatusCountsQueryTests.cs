using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Npgsql;
using Xunit;

namespace Nostos.Backend.Tests.Data;

/// <summary>
/// The sidebar's nine counts come from one conditional-aggregate query (#1318).
/// A seeded mixed library proves they equal the nine separate queries they
/// replaced, on SQLite and (in the <c>PostgresSpike</c> job) real PostgreSQL.
/// </summary>
public sealed class StatusCountsQueryTests : IClassFixture<SqliteTestFixture>
{
    private const string SpikeConnection = "NOSTOS_POSTGRES_SPIKE_CONNECTION";

    private readonly SqliteTestFixture _fixture;

    public StatusCountsQueryTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public Task Counts_match_the_nine_separate_queries_in_one_round_trip_on_sqlite() =>
        OnSqliteAsync(CountsMatchAsync);

    [Fact]
    [Trait("Category", "PostgresSpike")]
    public async Task Counts_match_the_nine_separate_queries_in_one_round_trip_on_postgresql()
    {
        var admin = Environment.GetEnvironmentVariable(SpikeConnection);
        if (string.IsNullOrWhiteSpace(admin))
            return;

        await OnPostgresAsync(admin, CountsMatchAsync);
    }

    private static async Task CountsMatchAsync(Store store)
    {
        await SeedAsync(store);

        store.Commands.Clear();
        var actual = (LibraryStatusCountsDto)(await store.Library.GetStatusCountsAsync()).Data!;
        var sent = store.Commands.Sent;

        // The state read plus the one aggregate.
        sent.Should().HaveCount(2);
        sent.Count(sql => sql.Contains("SELECT COUNT(*)", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        Console.WriteLine("STATUS-COUNTS SQL:\n" + string.Join("\n---\n", sent));

        var expected = await NineQueriesAsync(store);
        actual.Should().Be(expected);
        // Guard against a vacuous seed: every count is distinct and non-zero.
        actual.Should().Be(new LibraryStatusCountsDto(
            All: 9, NotStarted: 5, Reading: 2, Favorites: 2, Finished: 3, Unsorted: 8,
            Audiobooks: 2, Ebooks: 5, Pdfs: 2));
    }

    /// <summary>The previous implementation, kept verbatim as the reference.</summary>
    private static async Task<LibraryStatusCountsDto> NineQueriesAsync(Store store)
    {
        await using var db = await store.Factory.CreateDbContextAsync();
        var books = db.Books.AsNoTracking().Where(b => b.Status != BookStatus.UploadPending);
        Task<int> CountWorksAsync(IQueryable<BookModel> source) =>
            source.Select(b => b.WorkId).Distinct().CountAsync();

        return new LibraryStatusCountsDto(
            await CountWorksAsync(books),
            await CountWorksAsync(WorkReadingStatus.WhereNotStarted(books, db)),
            await CountWorksAsync(WorkReadingStatus.WhereReading(books, db)),
            await CountWorksAsync(books.Where(b => b.Work!.IsFavorite)),
            await CountWorksAsync(WorkReadingStatus.WhereFinished(books, db)),
            await CountWorksAsync(books.Where(b => !db.BookCollections.Any(bc => bc.BookId == b.Id))),
            await CountWorksAsync(books.OfType<AudioBookModel>()),
            await CountWorksAsync(books.OfType<EBookModel>().Where(
                b => b.FileDetails.FileName == null || !EF.Functions.Like(b.FileDetails.FileName, "%.pdf"))),
            await CountWorksAsync(books.Where(
                b => b.FileDetails.FileName != null && EF.Functions.Like(b.FileDetails.FileName, "%.pdf"))));
    }

    private static async Task SeedAsync(Store store)
    {
        await using var db = await store.Factory.CreateDbContextAsync();
        var t = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        WorkModel Work(string title, bool favorite = false) => new()
        {
            Title = title,
            Author = "A",
            NormalizedTitle = title.ToLowerInvariant(),
            NormalizedAuthor = "a",
            IsFavorite = favorite,
        };

        T Book<T>(T book, WorkModel work, string? file, int percent = 0, DateTime? finished = null,
            DateTime? lastRead = null, BookStatus status = BookStatus.Ready)
            where T : BookModel
        {
            book.Work = work;
            book.Title = work.Title;
            book.Author = "A";
            book.Status = status;
            book.CreatedAt = t;
            book.Progress = new ReadingProgress { ProgressPercent = percent, FinishedAt = finished, LastReadAt = lastRead };
            book.FileDetails = new FileInfoDetails { HasFile = file is not null, FileName = file };
            return book;
        }

        // 1: two editions, one finished, favourite work, shelved.
        var w1 = Work("Finished work", favorite: true);
        var b1a = Book(new EBookModel(), w1, "a.epub", 100, finished: t.AddDays(1), lastRead: t.AddDays(1));
        var b1b = Book(new AudioBookModel(), w1, "a.m4b");
        // 2: reread: finished earlier, an edition read after the finish (Reading and Finished).
        var w2 = Work("Reread work");
        var b2a = Book(new EBookModel(), w2, "b.epub", 100, finished: t.AddDays(1), lastRead: t.AddDays(1));
        var b2b = Book(new EBookModel(), w2, "b2.epub", 30, lastRead: t.AddDays(5));
        // 3: reading, no finish; PDF; favourite.
        var w3 = Work("Reading pdf", favorite: true);
        var b3 = Book(new EBookModel(), w3, "c.PDF".ToLowerInvariant(), 40, lastRead: t.AddDays(2));
        // 4: in progress but finished AFTER the last read: not Reading.
        var w4 = Work("Stale progress");
        var b4a = Book(new EBookModel(), w4, "d.epub", 20, lastRead: t.AddDays(1));
        var b4b = Book(new PhysicalBookModel(), w4, null, 0, finished: t.AddDays(3));
        // 5, 6: untouched ebook and audiobook, no file for the latter.
        var w5 = Work("Untouched ebook");
        var b5 = Book(new EBookModel(), w5, "e.epub");
        var w6 = Work("Untouched audio");
        var b6 = Book(new AudioBookModel(), w6, null);
        // 7: PDF shelved; untouched.
        var w7 = Work("Shelved pdf");
        var b7 = Book(new EBookModel(), w7, "g.pdf");
        // 8: ebook with no file (counts as ebook).
        var w8 = Work("Fileless ebook");
        var b8 = Book(new EBookModel(), w8, null);
        // 9: physical, unsorted.
        var w9 = Work("Physical");
        var b9 = Book(new PhysicalBookModel(), w9, null);
        // Excluded: an upload still pending, and a pending edition of a counted work.
        var w10 = Work("Pending upload");
        var b10 = Book(new EBookModel(), w10, "h.epub", 50, status: BookStatus.UploadPending);
        var b1c = Book(new EBookModel(), w1, "pending.epub", 50, status: BookStatus.UploadPending);

        var shelf = new CollectionModel { Name = "Shelf" };
        var child = new CollectionModel { Name = "Child", Parent = shelf };
        b1a.BookCollections.Add(new BookCollectionModel { Collection = shelf });
        b2a.BookCollections.Add(new BookCollectionModel { Collection = child });
        b7.BookCollections.Add(new BookCollectionModel { Collection = child });

        db.AddRange(w1, w2, w3, w4, w5, w6, w7, w8, w9, w10,
            b1a, b1b, b1c, b2a, b2b, b3, b4a, b4b, b5, b6, b7, b8, b9, b10, shelf, child);
        await db.SaveChangesAsync();
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
        var name = $"status_counts_{Guid.NewGuid():N}";
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
            Library = new LibraryService(
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
