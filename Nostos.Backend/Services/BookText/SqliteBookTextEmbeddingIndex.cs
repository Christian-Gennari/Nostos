using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Product.BookText;

namespace Nostos.Backend.Services.BookText;

/// <summary>
/// SelfHosted vector storage for book-text chunks (issue #683): one float32
/// BLOB per chunk in the same SQLite file as the chunks, searched with an
/// in-process cosine scan.
///
/// No SQLite extension is loaded on purpose — a self-hoster gets semantic
/// retrieval with zero extra setup. The scan is exact and linear in the number
/// of vectors in scope; vectors are streamed, never materialized as a set, so
/// memory stays flat however large the library is.
///
/// This lives on <see cref="SqliteBookTextIndex"/> because that class owns the
/// chunk lifecycle: every path that deletes chunks deletes their vectors in the
/// same transaction.
/// </summary>
public sealed partial class SqliteBookTextIndex : IBookTextEmbeddingIndex
{
    private static async Task EnsureEmbeddingSchemaAsync(NostosDbContext db, CancellationToken ct)
    {
        // One vector per chunk. Model is a column, not part of the key: a
        // changed embedding model makes the row "pending" again and the next
        // pass overwrites it, so stale vectors never accumulate.
        await ExecuteAsync(db, """
            CREATE TABLE IF NOT EXISTS BookTextChunkEmbeddings (
                ChunkId TEXT NOT NULL PRIMARY KEY,
                BookId TEXT NOT NULL,
                Model TEXT NOT NULL,
                Dimensions INTEGER NOT NULL,
                Vector BLOB NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            """, ct);

        await ExecuteAsync(db, """
            CREATE INDEX IF NOT EXISTS IX_BookTextChunkEmbeddings_Book
            ON BookTextChunkEmbeddings(BookId);
            """, ct);
    }

    public async Task<IReadOnlyList<BookTextEmbeddingWork>> GetPendingAsync(
        string model,
        int maxChunks,
        CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = Command(db, """
                SELECT c.Id, c.BookId, c.Text
                FROM BookTextChunks c
                JOIN BookTextIngestionStates s ON s.BookId = c.BookId
                LEFT JOIN BookTextChunkEmbeddings e ON e.ChunkId = c.Id AND e.Model = @model
                WHERE s.Status='Ready'
                  AND s.SourceSha256=c.SourceSha256
                  AND s.ExtractorVersion=c.ExtractorVersion
                  AND e.ChunkId IS NULL
                ORDER BY c.BookId, c.Ordinal
                LIMIT @limit;
                """,
                ("@model", model),
                ("@limit", Math.Clamp(maxChunks, 1, 256)));

            await using var reader = await command.ExecuteReaderAsync(ct);
            var pending = new List<BookTextEmbeddingWork>();
            while (await reader.ReadAsync(ct))
            {
                pending.Add(new BookTextEmbeddingWork(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    reader.GetString(2)));
            }
            return pending;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<int> StoreAsync(
        string model,
        IReadOnlyList<BookTextChunkEmbedding> embeddings,
        CancellationToken ct = default)
    {
        if (embeddings.Count == 0) return 0;

        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var stored = 0;
            var created = DateTime.UtcNow.ToString("O");

            foreach (var embedding in embeddings)
            {
                // INSERT … SELECT from the chunk row: a chunk deleted or
                // replaced while the provider call was in flight selects
                // nothing, so no orphan vector is ever written.
                stored += await ExecuteAsync(db, """
                    INSERT INTO BookTextChunkEmbeddings
                        (ChunkId, BookId, Model, Dimensions, Vector, CreatedAtUtc)
                    SELECT c.Id, c.BookId, @model, @dimensions, @vector, @created
                    FROM BookTextChunks c
                    WHERE c.Id=@id
                    ON CONFLICT(ChunkId) DO UPDATE SET
                        BookId=excluded.BookId,
                        Model=excluded.Model,
                        Dimensions=excluded.Dimensions,
                        Vector=excluded.Vector,
                        CreatedAtUtc=excluded.CreatedAtUtc;
                    """, ct,
                    ("@model", model),
                    ("@dimensions", embedding.Vector.Length),
                    ("@vector", BookTextVectors.ToBytes(embedding.Vector)),
                    ("@created", created),
                    ("@id", embedding.ChunkId.ToString("D")));
            }

            await tx.CommitAsync(ct);
            return stored;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<IReadOnlyList<BookTextSearchHit>> SearchAsync(
        string model,
        ReadOnlyMemory<float> queryVector,
        IReadOnlyList<Guid> bookIds,
        int maxCandidates,
        CancellationToken ct = default)
    {
        if (queryVector.Length == 0 || bookIds.Count == 0)
            return [];

        var query = queryVector.ToArray();
        BookTextVectors.NormalizeInPlace(query);
        var limit = Math.Clamp(maxCandidates, 1, 100);

        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var parameters = new List<(string, object?)>
            {
                ("@model", model),
                ("@dimensions", query.Length),
            };
            var ids = new List<string>();
            for (var i = 0; i < bookIds.Count; i++)
            {
                var name = $"@b{i}";
                ids.Add(name);
                parameters.Add((name, bookIds[i].ToString("D")));
            }

            // Same visibility rule as lexical search: only chunks of the book's
            // current Ready revision. Vectors from another model or of another
            // length are not comparable with the query and are filtered out.
            var sql = $"""
                SELECT e.ChunkId, e.Vector
                FROM BookTextChunkEmbeddings e
                JOIN BookTextChunks c ON c.Id = e.ChunkId
                JOIN BookTextIngestionStates s ON s.BookId = c.BookId
                WHERE e.Model=@model
                  AND e.Dimensions=@dimensions
                  AND e.BookId IN ({string.Join(",", ids)})
                  AND s.Status='Ready'
                  AND s.SourceSha256=c.SourceSha256
                  AND s.ExtractorVersion=c.ExtractorVersion;
                """;

            // Min-heap of the best `limit` scores seen so far: the weakest kept
            // candidate sits on top and is evicted by anything better.
            var best = new PriorityQueue<string, float>(limit + 1);
            var buffer = new byte[query.Length * sizeof(float)];

            await using (var command = Command(db, sql, parameters.ToArray()))
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    if (reader.GetBytes(1, 0, buffer, 0, buffer.Length) != buffer.Length)
                        continue;

                    var score = BookTextVectors.Dot(query, BookTextVectors.FromBytes(buffer));
                    if (!float.IsFinite(score))
                        continue;

                    if (best.Count < limit)
                    {
                        best.Enqueue(reader.GetString(0), score);
                    }
                    else if (best.TryPeek(out _, out var weakest) && score > weakest)
                    {
                        best.DequeueEnqueue(reader.GetString(0), score);
                    }
                }
            }

            if (best.Count == 0)
                return [];

            var scores = new Dictionary<string, float>(best.Count, StringComparer.OrdinalIgnoreCase);
            while (best.TryDequeue(out var chunkId, out var score))
                scores[chunkId] = score;

            var chunkParameters = new List<(string, object?)>();
            var chunkIds = new List<string>();
            foreach (var chunkId in scores.Keys)
            {
                var name = $"@c{chunkIds.Count}";
                chunkIds.Add(name);
                chunkParameters.Add((name, chunkId));
            }

            await using var chunkCommand = Command(db, $"""
                SELECT Id, BookId, SourceSha256, ExtractorVersion, Format,
                       Ordinal, Text, HeadingPathJson, SourceSegmentsJson
                FROM BookTextChunks
                WHERE Id IN ({string.Join(",", chunkIds)});
                """, chunkParameters.ToArray());
            await using var chunkReader = await chunkCommand.ExecuteReaderAsync(ct);
            var hits = new List<BookTextSearchHit>(scores.Count);
            while (await chunkReader.ReadAsync(ct))
            {
                var chunk = ReadChunk(chunkReader);
                hits.Add(new BookTextSearchHit(chunk, scores[chunkReader.GetString(0)]));
            }

            return hits
                .OrderByDescending(hit => hit.Score)
                .ThenBy(hit => hit.Chunk.BookId)
                .ThenBy(hit => hit.Chunk.Ordinal)
                .ToList();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
