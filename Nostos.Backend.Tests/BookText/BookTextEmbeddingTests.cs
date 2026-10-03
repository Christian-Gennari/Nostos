using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Services.BookText;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.BookText;

/// <summary>
/// The embedding stage of book-text ingestion and its SQLite vector storage
/// (issue #683). The invariant under test throughout: vectors are an optional
/// derived index, so every failure mode leaves lexical search working.
/// </summary>
public sealed class BookTextEmbeddingTests : IDisposable
{
    private const string Model = "alibaba/qwen3-embedding-0-6b";

    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"nostos-book-text-embedding-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Pending_chunks_are_embedded_stored_normalized_and_ranked_by_cosine_similarity()
    {
        var index = await CreateIndexAsync();
        var bookId = Guid.NewGuid();
        var chunks = await IndexBookAsync(index, bookId, "worship and devotion", "ships and harbours", "bread and salt");

        var pending = await index.GetPendingAsync(Model, 10);
        Assert.Equal(chunks.Select(chunk => chunk.Id), pending.Select(work => work.ChunkId));
        Assert.Equal("worship and devotion", pending[0].Text);

        var provider = new FakeEmbeddingProvider(Model)
        {
            Vectors =
            {
                ["worship and devotion"] = [3f, 0f, 0f],
                ["ships and harbours"] = [0f, 5f, 0f],
                ["bread and salt"] = [1f, 1f, 0f],
            },
        };
        var engine = CreateEngine(provider, index);

        var result = await engine.ProcessBatchAsync();

        Assert.Equal(BookTextEmbeddingPassStatus.Embedded, result.Status);
        Assert.Equal(3, result.ChunkCount);
        Assert.Empty(await index.GetPendingAsync(Model, 10));
        Assert.Equal(
            BookTextEmbeddingPassStatus.Idle,
            (await engine.ProcessBatchAsync()).Status);

        // An un-normalized query: ranking must be by angle, not magnitude.
        var hits = await index.SearchAsync(Model, new float[] { 10f, 1f, 0f }, [bookId], 2);

        Assert.Equal(2, hits.Count);
        Assert.Equal("worship and devotion", hits[0].Chunk.Text);
        Assert.Equal("bread and salt", hits[1].Chunk.Text);
        Assert.InRange(hits[0].Score, 0.99, 1.0001);
        Assert.True(hits[0].Score > hits[1].Score);
        // The hit is the canonical chunk, locator and all.
        Assert.IsType<PdfBookTextSourceLocator>(hits[0].Chunk.SourceSegments.Single().Locator);

        Assert.Empty(await index.SearchAsync(Model, new float[] { 1f, 0f, 0f }, [Guid.NewGuid()], 5));
    }

    [Fact]
    public async Task A_failing_provider_stores_nothing_and_leaves_lexical_search_working()
    {
        var index = await CreateIndexAsync();
        var bookId = Guid.NewGuid();
        await IndexBookAsync(index, bookId, "The lantern is the searchable phrase.");

        var provider = new FakeEmbeddingProvider(Model)
        {
            Failure = EmbeddingException.ProviderFailure("HTTP 503"),
        };

        var result = await CreateEngine(provider, index).ProcessBatchAsync();

        Assert.Equal(BookTextEmbeddingPassStatus.Failed, result.Status);
        Assert.Single(await index.GetPendingAsync(Model, 10));
        Assert.Empty(await index.SearchAsync(Model, new float[] { 1f, 0f, 0f }, [bookId], 5));

        // The book stayed Ready and lexical retrieval is untouched.
        Assert.Equal(BookTextIngestionStatus.Ready, (await index.GetStateAsync(bookId))!.Status);
        Assert.Single(await ((IBookTextIndex)index).SearchAsync("lantern", [bookId], 10));
    }

    [Fact]
    public async Task With_no_active_model_the_pass_is_disabled_and_never_calls_the_provider()
    {
        var index = await CreateIndexAsync();
        await IndexBookAsync(index, Guid.NewGuid(), "Some passage.");
        var provider = new FakeEmbeddingProvider(model: null);

        var result = await CreateEngine(provider, index).ProcessBatchAsync();

        Assert.Equal(BookTextEmbeddingPassStatus.Disabled, result.Status);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task A_short_or_ragged_provider_answer_is_a_failed_pass_not_a_partial_index()
    {
        var index = await CreateIndexAsync();
        await IndexBookAsync(index, Guid.NewGuid(), "one", "two");
        var provider = new FakeEmbeddingProvider(Model) { DropLastVector = true };

        var result = await CreateEngine(provider, index).ProcessBatchAsync();

        Assert.Equal(BookTextEmbeddingPassStatus.Failed, result.Status);
        Assert.Equal(2, (await index.GetPendingAsync(Model, 10)).Count);
    }

    [Fact]
    public async Task Changing_the_model_makes_chunks_pending_again_and_hides_the_old_vectors()
    {
        var index = await CreateIndexAsync();
        var bookId = Guid.NewGuid();
        var chunks = await IndexBookAsync(index, bookId, "only passage");
        await index.StoreAsync(Model, [new BookTextChunkEmbedding(chunks[0].Id, [1f, 0f])]);

        const string newModel = "text-embedding-3-small";
        Assert.Single(await index.GetPendingAsync(newModel, 10));
        // Vectors from different models are not comparable.
        Assert.Empty(await index.SearchAsync(newModel, new float[] { 1f, 0f }, [bookId], 5));

        // Re-embedding overwrites in place: one vector per chunk, never two.
        Assert.Equal(1, await index.StoreAsync(newModel, [new BookTextChunkEmbedding(chunks[0].Id, [0f, 1f, 0f])]));
        Assert.Empty(await index.GetPendingAsync(newModel, 10));
        Assert.Single(await index.GetPendingAsync(Model, 10));
        Assert.Single(await index.SearchAsync(newModel, new float[] { 0f, 1f, 0f }, [bookId], 5));
        // A query of another dimensionality cannot match either.
        Assert.Empty(await index.SearchAsync(newModel, new float[] { 0f, 1f }, [bookId], 5));
        Assert.Equal(1L, await CountVectorsAsync());
    }

    [Fact]
    public async Task Vectors_never_outlive_their_chunks()
    {
        var index = await CreateIndexAsync();
        var bookId = Guid.NewGuid();
        var chunks = await IndexBookAsync(index, bookId, "first", "second");
        await index.StoreAsync(Model, chunks.Select(chunk => new BookTextChunkEmbedding(chunk.Id, [1f, 0f])).ToList());
        Assert.Equal(2L, await CountVectorsAsync());

        // A replaced source invalidates the vectors with the chunks.
        await index.ScheduleAsync(bookId, "book.pdf", BookTextSourceFormat.Pdf);
        Assert.Equal(0L, await CountVectorsAsync());
        Assert.Empty(await index.GetPendingAsync(Model, 10));
        Assert.Empty(await index.SearchAsync(Model, new float[] { 1f, 0f }, [bookId], 5));

        // A vector that arrives for a chunk that no longer exists is dropped.
        Assert.Equal(0, await index.StoreAsync(Model, [new BookTextChunkEmbedding(chunks[0].Id, [1f, 0f])]));
        Assert.Equal(0L, await CountVectorsAsync());

        var other = Guid.NewGuid();
        var otherChunks = await IndexBookAsync(index, other, "kept");
        await index.StoreAsync(Model, [new BookTextChunkEmbedding(otherChunks[0].Id, [1f, 0f])]);
        await index.DeleteBookAsync(other);
        Assert.Equal(0L, await CountVectorsAsync());
    }

    [Fact]
    public void Vector_helpers_normalize_round_trip_and_agree_with_a_scalar_dot_product()
    {
        var vector = Enumerable.Range(1, 1024).Select(value => (float)value).ToArray();
        var other = Enumerable.Range(1, 1024).Select(value => 1f / value).ToArray();

        Assert.Equal(1024f, BookTextVectors.Dot(vector, other), 1);

        BookTextVectors.NormalizeInPlace(vector);
        Assert.Equal(1f, BookTextVectors.Dot(vector, vector), 4);

        var roundTripped = BookTextVectors.FromBytes(BookTextVectors.ToBytes(vector)).ToArray();
        Assert.Equal(vector, roundTripped);
        Assert.Equal(1024 * sizeof(float), BookTextVectors.ToBytes(vector).Length);

        var zero = new float[4];
        BookTextVectors.NormalizeInPlace(zero);
        Assert.All(zero, component => Assert.Equal(0f, component));
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
        try { File.Delete(_path + "-shm"); } catch { }
        try { File.Delete(_path + "-wal"); } catch { }
    }

    private async Task<SqliteBookTextIndex> CreateIndexAsync()
    {
        var index = new SqliteBookTextIndex(new Factory(_path));
        await index.EnsureSchemaAsync();
        return index;
    }

    private static BookTextEmbeddingEngine CreateEngine(
        IEmbeddingProvider provider,
        IBookTextEmbeddingIndex index) =>
        new(provider, index, new BookTextOptions(), NullLogger<BookTextEmbeddingEngine>.Instance);

    private static async Task<IReadOnlyList<BookTextIndexedChunk>> IndexBookAsync(
        SqliteBookTextIndex index,
        Guid bookId,
        params string[] texts)
    {
        await index.ScheduleAsync(bookId, "book.pdf", BookTextSourceFormat.Pdf);
        BookTextIngestionWork? work;
        do
        {
            work = await index.TryClaimNextAsync(TimeSpan.FromMinutes(15));
            Assert.NotNull(work);
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

        Assert.True(await index.ReplaceReadyAsync(revision, chunks, chunks.Sum(chunk => chunk.Text.Length), work.Attempt));
        return chunks;
    }

    private async Task<long> CountVectorsAsync()
    {
        await using var db = new Factory(_path).CreateDbContext();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM BookTextChunkEmbeddings;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FakeEmbeddingProvider(string? model) : IEmbeddingProvider
    {
        public Dictionary<string, float[]> Vectors { get; } = new(StringComparer.Ordinal);
        public EmbeddingException? Failure { get; init; }
        public bool DropLastVector { get; init; }
        public int Calls { get; private set; }

        public Task<string?> GetActiveModelAsync(CancellationToken ct = default) =>
            Task.FromResult(model);

        public Task<EmbeddingBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
        {
            Calls++;
            if (Failure is not null)
                throw Failure;

            var vectors = inputs
                .Select(input => Vectors.TryGetValue(input, out var vector) ? vector.ToArray() : new[] { 1f, 0f, 0f })
                .ToList();
            if (DropLastVector)
                vectors.RemoveAt(vectors.Count - 1);

            return Task.FromResult(new EmbeddingBatch(model!, vectors, TotalTokens: inputs.Count));
        }
    }

    private sealed class Factory(string path) : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options);

        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(CreateDbContext());
    }
}
