using System.Collections;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.BookText;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.BookText;

public sealed class BookTextSqliteIndexTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"nostos-book-text-index-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Index_lifecycle_is_revision_safe_and_lexically_searchable()
    {
        var index = new SqliteBookTextIndex(new Factory(_path));
        await index.EnsureSchemaAsync();

        var bookId = Guid.NewGuid();
        await index.ScheduleAsync(bookId, "book.pdf", BookTextSourceFormat.Pdf);

        var pending = await index.GetStateAsync(bookId);
        Assert.NotNull(pending);
        Assert.Equal(BookTextIngestionStatus.Pending, pending!.Status);
        Assert.Equal(BookTextArtifactSchema.CurrentExtractorVersion, pending.ExtractorVersion);

        var work = await index.TryClaimNextAsync(TimeSpan.FromMinutes(15));
        Assert.NotNull(work);
        Assert.Equal(bookId, work!.BookId);
        Assert.Equal(1, work.Attempt);

        var revision = new BookTextSourceRevision(
            bookId,
            new string('a', 64),
            BookTextArtifactSchema.CurrentExtractorVersion,
            BookTextSourceFormat.Pdf);

        var chunk = new BookTextIndexedChunk(
            BookTextIdentity.ChunkId(revision, 0),
            bookId,
            revision.SourceSha256,
            revision.ExtractorVersion,
            revision.Format,
            0,
            "The lantern is the only deliberately unique searchable phrase.",
            ["Chapter One"],
            [
                new BookTextSourceSegment(
                    0,
                    64,
                    new PdfBookTextSourceLocator(4, "1", 0, 64)),
            ]);

        Assert.True(await index.ReplaceReadyAsync(revision, [chunk], chunk.Text.Length, work!.Attempt));

        var hits = await index.SearchAsync("lantern searchable", [bookId], 10);
        Assert.Single(hits);
        Assert.Equal(chunk.Id, hits[0].Chunk.Id);
        Assert.Equal(4, Assert.IsType<PdfBookTextSourceLocator>(
            hits[0].Chunk.SourceSegments.Single().Locator).PageIndex);

        var otherScope = await index.SearchAsync("lantern", [Guid.NewGuid()], 10);
        Assert.Empty(otherScope);

        // A replacement invalidates and removes the old revision before the new
        // source has finished indexing: stale text can never answer a question.
        await index.ScheduleAsync(bookId, "book.pdf", BookTextSourceFormat.Pdf);
        Assert.Empty(await index.SearchAsync("lantern", [bookId], 10));

        await index.DeleteBookAsync(bookId);
        Assert.Null(await index.GetStateAsync(bookId));
    }

    [Fact]
    public async Task Stale_processing_work_is_retryable_but_fresh_work_is_not_double_claimed()
    {
        var index = new SqliteBookTextIndex(new Factory(_path));
        await index.EnsureSchemaAsync();
        var bookId = Guid.NewGuid();
        await index.ScheduleAsync(bookId, "book.epub", BookTextSourceFormat.Epub);

        var first = await index.TryClaimNextAsync(TimeSpan.FromHours(1));
        Assert.NotNull(first);

        var duplicate = await index.TryClaimNextAsync(TimeSpan.FromHours(1));
        Assert.Null(duplicate);

        // Zero stale window is normalized by the SQL comparison to "anything
        // older than now", making the persisted Processing row claimable again.
        await Task.Delay(5);
        var retry = await index.TryClaimNextAsync(TimeSpan.Zero);
        Assert.NotNull(retry);
        Assert.Equal(2, retry!.Attempt);
    }

    [Fact]
    public async Task Superseded_or_deleted_processing_claim_cannot_resurrect_chunks()
    {
        var index = new SqliteBookTextIndex(new Factory(_path));
        await index.EnsureSchemaAsync();
        var bookId = Guid.NewGuid();
        await index.ScheduleAsync(bookId, "book.pdf", BookTextSourceFormat.Pdf);

        var stale = await index.TryClaimNextAsync(TimeSpan.FromHours(1));
        Assert.NotNull(stale);

        await Task.Delay(5);
        var current = await index.TryClaimNextAsync(TimeSpan.Zero);
        Assert.NotNull(current);
        Assert.Equal(2, current!.Attempt);

        var revision = new BookTextSourceRevision(
            bookId,
            new string('d', 64),
            BookTextArtifactSchema.CurrentExtractorVersion,
            BookTextSourceFormat.Pdf);
        var staleChunk = new BookTextIndexedChunk(
            BookTextIdentity.ChunkId(revision, 0),
            bookId,
            revision.SourceSha256,
            revision.ExtractorVersion,
            revision.Format,
            0,
            "stale worker text must never become searchable",
            [],
            [new BookTextSourceSegment(
                0,
                44,
                new PdfBookTextSourceLocator(0, "1", 0, 44))]);

        Assert.False(await index.ReplaceReadyAsync(
            revision,
            [staleChunk],
            staleChunk.Text.Length,
            stale!.Attempt));
        Assert.False(await index.MarkFailedAsync(
            bookId,
            "stale_failure",
            "A stale worker cannot overwrite the current attempt.",
            unsupported: false,
            stale.Attempt));
        Assert.Empty(await index.SearchAsync("stale worker", [bookId], 8));

        var currentChunk = staleChunk with
        {
            Id = BookTextIdentity.ChunkId(revision, 1),
            Ordinal = 1,
            Text = "current worker lantern evidence",
        };
        Assert.True(await index.ReplaceReadyAsync(
            revision,
            [currentChunk],
            currentChunk.Text.Length,
            current.Attempt));
        Assert.Single(await index.SearchAsync("lantern", [bookId], 8));

        // Deletion while another claim is Processing removes the lifecycle row.
        // That worker's late completion becomes a no-op rather than recreating
        // searchable customer text after deletion.
        await index.ScheduleAsync(bookId, "book.pdf", BookTextSourceFormat.Pdf);
        var deleting = await index.TryClaimNextAsync(TimeSpan.FromHours(1));
        Assert.NotNull(deleting);
        await index.DeleteBookAsync(bookId);

        Assert.False(await index.ReplaceReadyAsync(
            revision,
            [currentChunk],
            currentChunk.Text.Length,
            deleting!.Attempt));
        Assert.Null(await index.GetStateAsync(bookId));
        Assert.Empty(await index.SearchAsync("lantern", [bookId], 8));
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            try { File.Delete(_path + suffix); } catch { }
        }
    }

    /// <summary>
    /// A book's text is written in one transaction. Past SQLite's 2 MB page
    /// cache the pager spills and takes the exclusive lock, so every library
    /// read waits for the rest of the write. The probe runs inside the write,
    /// after several megabytes of dirty pages, and gives up after a second.
    /// </summary>
    [Fact]
    public async Task A_large_book_can_be_written_while_the_library_is_read()
    {
        var index = new SqliteBookTextIndex(new Factory(_path));
        await index.EnsureSchemaAsync();
        var bookId = Guid.NewGuid();
        await index.ScheduleAsync(bookId, "book.pdf", BookTextSourceFormat.Pdf);
        var work = await index.TryClaimNextAsync(TimeSpan.FromMinutes(15));
        var revision = new BookTextSourceRevision(
            bookId,
            new string('a', 64),
            BookTextArtifactSchema.CurrentExtractorVersion,
            BookTextSourceFormat.Pdf);

        const int chunkCount = 3000;
        var readDuringWrite = (long?)null;
        var random = new Random(1);
        var chunks = Enumerable.Range(0, chunkCount)
            .Select(ordinal => new BookTextIndexedChunk(
                BookTextIdentity.ChunkId(revision, ordinal),
                bookId,
                revision.SourceSha256,
                revision.ExtractorVersion,
                revision.Format,
                ordinal,
                string.Concat(Enumerable.Range(0, 200).Select(_ => $"w{random.Next(50_000)} ")),
                ordinal == chunkCount - 1
                    ? new OnEnumerate(() => readDuringWrite = ReadChunkCount())
                    : ["Chapter"],
                []))
            .ToList();

        Assert.True(await index.ReplaceReadyAsync(revision, chunks, 1, work!.Attempt));

        Assert.Equal(0, readDuringWrite);
        Assert.Equal(chunkCount, ReadChunkCount());
    }

    [Fact]
    public async Task A_pooled_connection_returns_with_the_default_spill_limit()
    {
        var index = new SqliteBookTextIndex(new Factory(_path));
        await index.EnsureSchemaAsync();
        var before = ReadSpillLimit();
        var bookId = Guid.NewGuid();
        await index.ScheduleAsync(bookId, "book.pdf", BookTextSourceFormat.Pdf);
        var work = await index.TryClaimNextAsync(TimeSpan.FromMinutes(15));
        var revision = new BookTextSourceRevision(
            bookId,
            new string('a', 64),
            BookTextArtifactSchema.CurrentExtractorVersion,
            BookTextSourceFormat.Pdf);

        Assert.True(await index.ReplaceReadyAsync(revision, [], 0, work!.Attempt));
        Assert.False(await index.ReplaceReadyAsync(revision, [], 0, work.Attempt));

        Assert.Equal(before, ReadSpillLimit());
    }

    private long ReadChunkCount()
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM BookTextChunks;";
        return (long)command.ExecuteScalar()!;
    }

    private long ReadSpillLimit()
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cache_spill;";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>A heading list that runs a callback when the write serializes it.</summary>
    private sealed class OnEnumerate(Action callback) : IReadOnlyList<string>
    {
        public int Count => 0;

        public string this[int index] => throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<string> GetEnumerator()
        {
            callback();
            return Enumerable.Empty<string>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Factory(string path) : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
            return new NostosDbContext(options);
        }

        public Task<NostosDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
