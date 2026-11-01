using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Product.BookText;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;
using Xunit.Abstractions;

namespace Nostos.Backend.Tests.BookText;

/// <summary>
/// Ask Nostos book search (#1321): the reads before the search itself no longer
/// grow with the library, and the answer is exactly what the per-book,
/// per-hit reads produced.
/// </summary>
public sealed class BookTextSearchQueryCountTests(ITestOutputHelper output) : IDisposable
{
    private readonly SqliteTestFixture _fixture = new();

    [Fact]
    public async Task Batched_reads_return_exactly_what_the_per_item_reads_returned()
    {
        var bed = await SeedAsync(books: 12, collectionBooks: 5);

        foreach (var request in new[]
        {
            new BookTextSearchRequest("lantern", MaxPassages: 6),
            new BookTextSearchRequest("lantern harbour", MaxPassages: 4),
            // No strict match: exercises the relaxed fallback and its hits.
            new BookTextSearchRequest("lantern zzzmissing harbour", MaxPassages: 5),
            new BookTextSearchRequest("lantern", CollectionId: bed.CollectionId),
            new BookTextSearchRequest(
                "lantern",
                BookIds: [bed.BookIds[3], Guid.NewGuid(), bed.BookIds[1], bed.BookIds[3]]),
            new BookTextSearchRequest("lantern", MaxPassages: 6, NeighborRadius: 0),
        })
        {
            var after = await bed.Batched.SearchAsync(request);
            var before = await bed.PerItem.SearchAsync(request);

            after.Passages.Should().NotBeEmpty();
            after.EvidenceAvailable.Should().Be(before.EvidenceAvailable);
            after.Passages.Should().BeEquivalentTo(before.Passages, o => o.WithStrictOrdering());
            after.States.Should().BeEquivalentTo(before.States, o => o.WithStrictOrdering());
        }
    }

    [Fact]
    public async Task One_search_costs_the_same_number_of_reads_at_10_and_100_books()
    {
        var small = await SeedAsync(books: 10);
        var large = await SeedAsync(books: 100);
        var request = new BookTextSearchRequest("lantern", MaxPassages: 6);

        var smallAfter = await Measure(small, small.Batched, request);
        var largeAfter = await Measure(large, large.Batched, request);
        var smallBefore = await Measure(small, small.PerItem, request);
        var largeBefore = await Measure(large, large.PerItem, request);

        output.WriteLine($"before: 10 books = {smallBefore}, 100 books = {largeBefore}");
        output.WriteLine($"after:  10 books = {smallAfter}, 100 books = {largeAfter}");

        largeAfter.Should().Be(smallAfter);
        smallAfter.Should().BeLessThanOrEqualTo(6);
        largeBefore.Should().BeGreaterThan(largeAfter * 10);
        largeBefore.Should().BeGreaterThan(smallBefore);
    }

    [Fact]
    public async Task Backfill_reads_states_in_batches_and_schedules_the_same_books()
    {
        var bed = await SeedAsync(books: 30);
        // Give half of them a file so the backfill considers them.
        await using (var db = await bed.LibraryFactory.CreateDbContextAsync())
        {
            foreach (var book in await db.Books.ToListAsync())
            {
                book.FileDetails.HasFile = true;
                book.FileDetails.FileName = book.Title + ".epub";
            }
            await db.SaveChangesAsync();
        }

        // Every seeded book is already indexed at the current version, so a
        // backfill schedules nothing; one extra file-bearing book is missing.
        await using (var db = await bed.LibraryFactory.CreateDbContextAsync())
        {
            db.Books.Add(new EBookModel
            {
                Id = Guid.NewGuid(),
                Title = "Unindexed",
                FileDetails = { HasFile = true, FileName = "Unindexed.epub" },
            });
            await db.SaveChangesAsync();
        }

        var scheduler = new RecordingScheduler();
        var backfill = new BookTextBackfillService(
            bed.Library, bed.BatchIndex, scheduler, new NullLogger<BookTextBackfillService>());
        bed.Counter.Reset();
        var scheduled = await backfill.ScheduleMissingAsync();

        scheduled.Should().Be(1);
        scheduler.Titles.Should().Equal("Unindexed.epub");
        // 1 scope read + 1 state read per 100 candidates.
        bed.Counter.Total.Should().BeLessThanOrEqualTo(4);
    }

    private static async Task<int> Measure(Bed bed, IBookTextSearchService service, BookTextSearchRequest request)
    {
        bed.Counter.Reset();
        var response = await service.SearchAsync(request);
        response.Passages.Should().NotBeEmpty();
        return bed.Counter.Total;
    }

    private async Task<Bed> SeedAsync(int books, int collectionBooks = 0)
    {
        var path = _fixture.CreateDatabasePath();
        var counter = new QueryCounter();
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .AddInterceptors(new CountingInterceptor(counter))
            .Options;
        var factory = new Factory(options);
        await using (var bootstrap = new NostosDbContext(
            new DbContextOptionsBuilder<NostosDbContext>().UseSqlite($"Data Source={path}").Options))
        {
            bootstrap.Database.EnsureCreated();
        }

        var bookIds = new List<Guid>();
        var collectionId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Collections.Add(new CollectionModel { Id = collectionId, Name = "Sea" });
            for (var i = 0; i < books; i++)
            {
                var book = new PhysicalBookModel
                {
                    Id = Guid.NewGuid(),
                    Title = $"Book {i:D3}",
                    Author = i % 3 == 0 ? null : $"Author {i % 7}",
                };
                db.Books.Add(book);
                bookIds.Add(book.Id);
                if (i < collectionBooks)
                    db.BookCollections.Add(new BookCollectionModel { BookId = book.Id, CollectionId = collectionId });
            }
            await db.SaveChangesAsync();
        }

        var sqlite = new SqliteBookTextIndex(factory);
        await sqlite.EnsureSchemaAsync();
        for (var i = 0; i < books; i++)
            await sqlite.ScheduleAsync(bookIds[i], $"Book {i:D3}.epub", BookTextSourceFormat.Epub);
        while (await sqlite.TryClaimNextAsync(TimeSpan.FromHours(1)) is { } work)
        {
            var revision = new BookTextSourceRevision(
                work.BookId,
                new string('b', 64),
                BookTextArtifactSchema.CurrentExtractorVersion,
                BookTextSourceFormat.Epub);
            var chunks = Enumerable.Range(0, 6)
                .Select(ordinal => new BookTextIndexedChunk(
                    BookTextIdentity.ChunkId(revision, ordinal),
                    work.BookId,
                    revision.SourceSha256,
                    revision.ExtractorVersion,
                    revision.Format,
                    ordinal,
                    ordinal % 2 == 0
                        ? $"The lantern on the harbour wall, part {ordinal} of {work.BookId:N}."
                        : $"Gulls over the water, part {ordinal} of {work.BookId:N}, a lantern too.",
                    ["Chapter " + (ordinal / 2)],
                    []))
                .ToList();
            (await sqlite.ReplaceReadyAsync(revision, chunks, 100, work.Attempt)).Should().BeTrue();
        }

        var library = new LibraryService(
            factory,
            new BookLookupService(new NoopHttpClientFactory(), new NullLogger<BookLookupService>()));
        var options2 = new BookTextOptions();
        var batchIndex = new CountingBatchIndex(sqlite, counter);
        var perItemIndex = new CountingPerItemIndex(sqlite, counter);

        return new Bed(
            counter,
            factory,
            library,
            batchIndex,
            collectionId,
            bookIds,
            Batched: new BookTextSearchService(batchIndex, library, options2),
            // The per-item read path: a library and an index that only know the
            // original members, so every default (fall back to per item)
            // implementation runs. This is also what a host that has not yet
            // overridden the new members gets.
            PerItem: new BookTextSearchService(
                perItemIndex, new PerItemLibrary(library), options2));
    }

    public void Dispose() => _fixture.Dispose();

    private sealed record Bed(
        QueryCounter Counter,
        IDbContextFactory<NostosDbContext> LibraryFactory,
        ILibraryService Library,
        IBookTextIndex BatchIndex,
        Guid CollectionId,
        IReadOnlyList<Guid> BookIds,
        IBookTextSearchService Batched,
        IBookTextSearchService PerItem);

    private sealed class QueryCounter
    {
        private int _total;
        public int Total => Volatile.Read(ref _total);
        public void Increment() => Interlocked.Increment(ref _total);
        public void Reset() => Volatile.Write(ref _total, 0);
    }

    // Counts every EF command (library reads). Index reads are counted by the
    // decorators below, one statement per call.
    private sealed class CountingInterceptor(QueryCounter counter) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            counter.Increment();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private class CountingPerItemIndex(SqliteBookTextIndex inner, QueryCounter counter) : IBookTextIndex
    {
        public Task EnsureSchemaAsync(CancellationToken ct = default) => inner.EnsureSchemaAsync(ct);
        public Task ScheduleAsync(Guid bookId, string sourceFileName, BookTextSourceFormat format, CancellationToken ct = default) =>
            inner.ScheduleAsync(bookId, sourceFileName, format, ct);
        public Task<BookTextIngestionWork?> TryClaimNextAsync(TimeSpan staleAfter, CancellationToken ct = default) =>
            inner.TryClaimNextAsync(staleAfter, ct);
        public Task<bool> ReplaceReadyAsync(BookTextSourceRevision revision, IReadOnlyList<BookTextIndexedChunk> chunks, long characterCount, int expectedAttempt, CancellationToken ct = default) =>
            inner.ReplaceReadyAsync(revision, chunks, characterCount, expectedAttempt, ct);
        public Task<bool> MarkFailedAsync(Guid bookId, string errorCode, string errorMessage, bool unsupported, int expectedAttempt, CancellationToken ct = default) =>
            inner.MarkFailedAsync(bookId, errorCode, errorMessage, unsupported, expectedAttempt, ct);
        public Task DeleteBookAsync(Guid bookId, CancellationToken ct = default) => inner.DeleteBookAsync(bookId, ct);

        public Task<BookTextIngestionState?> GetStateAsync(Guid bookId, CancellationToken ct = default)
        {
            counter.Increment();
            return inner.GetStateAsync(bookId, ct);
        }

        public Task<IReadOnlyList<BookTextSearchHit>> SearchAsync(string query, IReadOnlyList<Guid> bookIds, int maxCandidates, CancellationToken ct = default)
        {
            counter.Increment();
            return inner.SearchAsync(query, bookIds, maxCandidates, ct);
        }

        public Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsAsync(Guid bookId, string sourceSha256, string extractorVersion, int ordinal, int radius, CancellationToken ct = default)
        {
            counter.Increment();
            return inner.GetNeighborsAsync(bookId, sourceSha256, extractorVersion, ordinal, radius, ct);
        }
    }

    private sealed class CountingBatchIndex(SqliteBookTextIndex inner, QueryCounter counter)
        : CountingPerItemIndex(inner, counter), IBookTextIndex
    {
        private readonly SqliteBookTextIndex _inner = inner;
        private readonly QueryCounter _counter = counter;

        public Task<IReadOnlyList<BookTextIngestionState>> GetStatesAsync(IReadOnlyList<Guid> bookIds, CancellationToken ct = default)
        {
            _counter.Increment();
            return _inner.GetStatesAsync(bookIds, ct);
        }

        public Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsManyAsync(IReadOnlyList<BookTextNeighborQuery> queries, int radius, CancellationToken ct = default)
        {
            _counter.Increment();
            return _inner.GetNeighborsManyAsync(queries, radius, ct);
        }
    }

    /// <summary>Delegates the original members only, so the interface defaults apply.</summary>
    private sealed class PerItemLibrary(ILibraryService inner) : ILibraryService
    {
        public Task<LibraryCommandResultDto> ListBooksAsync(BookFilter filter, BookSort sort, string? search, int page, int pageSize, Guid? collectionId, bool? groupByWork = false, string? format = null, CancellationToken ct = default) =>
            inner.ListBooksAsync(filter, sort, search, page, pageSize, collectionId, groupByWork, format, ct);
        public Task<LibraryCommandResultDto> GetBookAsync(Guid bookId, CancellationToken ct = default) => inner.GetBookAsync(bookId, ct);

        public Task<LibraryCommandResultDto> GetStatusCountsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryResolveResult> ResolveBookAsync(LibraryResolveBookRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> ListCollectionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> ListCollectionCountsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> GetCollectionAsync(Guid collectionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> CreateOrMatchBookAsync(LibraryCreateBookRequest request, bool strictConfirmation, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> AttachAcquiredAssetAsync(LibraryAttachAcquiredAssetRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> UpdateBookAsync(LibraryUpdateBookRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> LinkWorkAsync(LibraryLinkWorkRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> UnlinkWorkAsync(LibraryUnlinkWorkRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> UpdateProgressAsync(Guid bookId, string location, int percentage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> ResetProgressAsync(Guid bookId, bool allEditions = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> DeleteBookAsync(Guid bookId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> SetBookStatusAsync(Guid bookId, BookStatus status, string? statusMessage = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> CreateCollectionAsync(LibraryCreateCollectionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> RenameCollectionAsync(LibraryRenameCollectionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> MoveCollectionAsync(LibraryMoveCollectionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> UpdateCollectionAsync(string clientId, string idempotencyKey, Guid collectionId, string name, Guid? parentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryCommandResultDto> DeleteCollectionAsync(LibraryDeleteCollectionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingScheduler : IBookTextIngestionScheduler
    {
        public List<string> Titles { get; } = [];

        public Task ScheduleAsync(Guid bookId, string sourceFileName, CancellationToken ct = default)
        {
            Titles.Add(sourceFileName);
            return Task.CompletedTask;
        }
    }

    private sealed class Factory(DbContextOptions<NostosDbContext> options) : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);

        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
