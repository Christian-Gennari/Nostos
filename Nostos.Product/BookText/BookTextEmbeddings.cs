using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Nostos.Product.Services.Ai;

namespace Nostos.Product.BookText;

/// <summary>One indexed chunk that has no vector for the active embedding model yet.</summary>
public sealed record BookTextEmbeddingWork(Guid ChunkId, Guid BookId, string Text);

/// <summary>One chunk's vector, ready to store. <see cref="Vector"/> is L2-normalized.</summary>
public sealed record BookTextChunkEmbedding(Guid ChunkId, float[] Vector);

/// <summary>
/// Host-supplied vector storage for book-text chunks (issue #683): SQLite with
/// in-process cosine similarity on SelfHosted, <c>pgvector</c> on Nostos Cloud.
///
/// The stored vectors are a derived index over <see cref="IBookTextIndex"/>
/// chunks and nothing else: an implementation must drop a chunk's vector when
/// the chunk itself is deleted or replaced, and must only ever return chunks of
/// the book's current <c>Ready</c> source revision — the same visibility rule
/// as lexical search. Losing every vector loses no user data; the embedding
/// pass simply fills them in again.
/// </summary>
public interface IBookTextEmbeddingIndex
{
    /// <summary>
    /// Up to <paramref name="maxChunks"/> searchable chunks with no vector for
    /// <paramref name="model"/> — never embedded, or embedded with another model.
    /// </summary>
    Task<IReadOnlyList<BookTextEmbeddingWork>> GetPendingAsync(
        string model,
        int maxChunks,
        CancellationToken ct = default);

    /// <summary>
    /// Stores one vector per chunk, replacing any vector the chunk already has.
    /// A chunk that no longer exists is skipped. Returns the number stored.
    /// </summary>
    Task<int> StoreAsync(
        string model,
        IReadOnlyList<BookTextChunkEmbedding> embeddings,
        CancellationToken ct = default);

    /// <summary>
    /// The chunks closest to <paramref name="queryVector"/> by cosine
    /// similarity, best first, restricted to <paramref name="bookIds"/> and to
    /// vectors produced by <paramref name="model"/>. <c>Score</c> is the cosine
    /// similarity. An index with no usable vectors returns an empty list.
    /// </summary>
    Task<IReadOnlyList<BookTextSearchHit>> SearchAsync(
        string model,
        ReadOnlyMemory<float> queryVector,
        IReadOnlyList<Guid> bookIds,
        int maxCandidates,
        CancellationToken ct = default);
}

public sealed class NoOpBookTextEmbeddingIndex : IBookTextEmbeddingIndex
{
    public Task<IReadOnlyList<BookTextEmbeddingWork>> GetPendingAsync(string model, int maxChunks, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BookTextEmbeddingWork>>([]);
    public Task<int> StoreAsync(string model, IReadOnlyList<BookTextChunkEmbedding> embeddings, CancellationToken ct = default) =>
        Task.FromResult(0);
    public Task<IReadOnlyList<BookTextSearchHit>> SearchAsync(string model, ReadOnlyMemory<float> queryVector, IReadOnlyList<Guid> bookIds, int maxCandidates, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BookTextSearchHit>>([]);
}

public enum BookTextEmbeddingPassStatus
{
    /// <summary>No embedding model is active; nothing was attempted.</summary>
    Disabled,

    /// <summary>Every searchable chunk already has a vector for the active model.</summary>
    Idle,

    /// <summary>One batch was embedded and stored.</summary>
    Embedded,

    /// <summary>The provider failed. Nothing was stored; lexical search is unaffected.</summary>
    Failed,
}

public sealed record BookTextEmbeddingPassResult(BookTextEmbeddingPassStatus Status, int ChunkCount = 0);

/// <summary>
/// The embedding stage of book-text ingestion (issue #683).
///
/// It runs AFTER, and independently of, the extraction stage: a book becomes
/// <c>Ready</c> for lexical search as soon as its chunks are committed, and this
/// engine later fills in vectors for whatever searchable chunks lack one for the
/// active model. That one rule covers a newly imported book, a library indexed
/// before embeddings were configured, and a changed embedding model alike.
///
/// A provider failure is an outcome, not an exception: it never touches the
/// ingestion state, so Ask Nostos keeps answering from lexical retrieval.
/// </summary>
public sealed class BookTextEmbeddingEngine(
    IEmbeddingProvider provider,
    IBookTextEmbeddingIndex index,
    BookTextOptions options,
    ILogger<BookTextEmbeddingEngine> logger)
{
    public async Task<BookTextEmbeddingPassResult> ProcessBatchAsync(CancellationToken ct = default)
    {
        try
        {
            var model = await provider.GetActiveModelAsync(ct);
            if (string.IsNullOrWhiteSpace(model))
                return new BookTextEmbeddingPassResult(BookTextEmbeddingPassStatus.Disabled);

            var pending = await index.GetPendingAsync(
                model,
                Math.Clamp(options.EmbeddingBatchSize, 1, 256),
                ct);
            if (pending.Count == 0)
                return new BookTextEmbeddingPassResult(BookTextEmbeddingPassStatus.Idle);

            var batch = await provider.EmbedAsync(pending.Select(work => work.Text).ToList(), ct);
            if (batch.Vectors.Count != pending.Count)
            {
                throw EmbeddingException.InvalidResponse(
                    $"expected {pending.Count} embedding(s) but received {batch.Vectors.Count}");
            }

            var embeddings = new List<BookTextChunkEmbedding>(pending.Count);
            for (var i = 0; i < pending.Count; i++)
            {
                var vector = batch.Vectors[i];
                if (vector.Length == 0 || vector.Length != batch.Vectors[0].Length)
                    throw EmbeddingException.InvalidResponse("the embeddings were empty or of differing lengths");

                BookTextVectors.NormalizeInPlace(vector);
                embeddings.Add(new BookTextChunkEmbedding(pending[i].ChunkId, vector));
            }

            // Keyed by the model that was ASKED for, so the pending query above
            // and this write always agree on what "embedded" means.
            var stored = await index.StoreAsync(model, embeddings, ct);

            // Debug, not Information: a first pass over a library is thousands
            // of batches.
            logger.LogDebug(
                "Book-text embedding stored {Stored} of {Requested} chunk vector(s) with model {Model} ({Dimensions} dimensions, {Tokens} token(s)).",
                stored,
                pending.Count,
                model,
                batch.Vectors[0].Length,
                batch.TotalTokens);

            return new BookTextEmbeddingPassResult(BookTextEmbeddingPassStatus.Embedded, stored);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EmbeddingException exception)
        {
            logger.LogWarning(
                "Book-text embedding failed with {Code}; lexical search is unaffected and the batch will be retried. Publication text is not logged.",
                exception.Code);
            return new BookTextEmbeddingPassResult(BookTextEmbeddingPassStatus.Failed);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Book-text embedding failed with {ExceptionType}; lexical search is unaffected and the batch will be retried. Publication text is not logged.",
                exception.GetType().Name);
            return new BookTextEmbeddingPassResult(BookTextEmbeddingPassStatus.Failed);
        }
    }
}

/// <summary>
/// Vector helpers shared by every <see cref="IBookTextEmbeddingIndex"/>.
/// Vectors are L2-normalized once, when stored, so cosine similarity at query
/// time is a plain dot product.
/// </summary>
public static class BookTextVectors
{
    /// <summary>
    /// Scales <paramref name="vector"/> to unit length. A zero vector has no
    /// direction and is left untouched (it then scores 0 against everything).
    /// </summary>
    public static void NormalizeInPlace(Span<float> vector)
    {
        var norm = MathF.Sqrt(Dot(vector, vector));
        if (norm <= 0f || !float.IsFinite(norm))
            return;

        for (var i = 0; i < vector.Length; i++)
            vector[i] /= norm;
    }

    public static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
            throw new ArgumentException("Vectors must have the same number of dimensions.");

        var sum = 0f;
        var i = 0;
        if (Vector.IsHardwareAccelerated && left.Length >= Vector<float>.Count)
        {
            var accumulator = Vector<float>.Zero;
            var last = left.Length - Vector<float>.Count;
            for (; i <= last; i += Vector<float>.Count)
                accumulator += new Vector<float>(left[i..]) * new Vector<float>(right[i..]);
            sum = Vector.Sum(accumulator);
        }

        for (; i < left.Length; i++)
            sum += left[i] * right[i];

        return sum;
    }

    /// <summary>
    /// The storage encoding: consecutive IEEE-754 float32 values in the
    /// machine's byte order (little-endian on every platform Nostos runs on).
    /// </summary>
    public static byte[] ToBytes(ReadOnlySpan<float> vector) =>
        MemoryMarshal.AsBytes(vector).ToArray();

    /// <summary>Views stored bytes as floats without copying.</summary>
    public static ReadOnlySpan<float> FromBytes(ReadOnlySpan<byte> bytes) =>
        MemoryMarshal.Cast<byte, float>(bytes);
}
