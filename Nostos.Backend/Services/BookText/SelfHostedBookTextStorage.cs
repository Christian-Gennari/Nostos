using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Product.BookText;

namespace Nostos.Backend.Services.BookText;

public sealed class FileBookTextArtifactStorage(
    IWebHostEnvironment environment,
    IOptions<FileStorageOptions> storageOptions) : IBookDerivedArtifactStorage
{
    /// <summary>
    /// The per-book derived artifact directory name. The exact, case-correct
    /// name is part of the library-media layout contract: the activation
    /// recovery boundary treats this directory as regenerable and never retains
    /// it, while refusing every other unexpected entry.
    /// </summary>
    internal const string DerivedDirectoryName = "derived";

    private readonly string _root = FileStorageOptions.ResolveBooksRoot(
        environment.ContentRootPath,
        storageOptions.Value);

    public async Task WriteAsync(
        BookTextSourceRevision revision,
        Func<Stream, CancellationToken, Task> writer,
        CancellationToken ct = default)
    {
        var directory = Path.Combine(
            _root,
            revision.BookId.ToString(),
            DerivedDirectoryName,
            revision.SourceSha256,
            Safe(revision.ExtractorVersion));
        Directory.CreateDirectory(directory);

        var finalPath = Path.Combine(directory, BookTextArtifactSchema.SuggestedFileName);
        var partial = finalPath + $".partial-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await writer(stream, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(partial, finalPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }
    }

    public Task DeleteBookArtifactsAsync(Guid bookId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(_root, bookId.ToString(), DerivedDirectoryName);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }

    public Task PruneBookArtifactsAsync(
        Guid bookId,
        BookTextSourceRevision keep,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var derived = Path.Combine(_root, bookId.ToString(), DerivedDirectoryName);
        if (!Directory.Exists(derived))
            return Task.CompletedTask;

        var keepDirectory = Path.GetFullPath(Path.Combine(
            derived,
            keep.SourceSha256,
            Safe(keep.ExtractorVersion)));

        foreach (var file in Directory.EnumerateFiles(
            derived,
            "*",
            SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(
                    keepDirectory + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                && !string.Equals(full, keepDirectory, StringComparison.Ordinal))
            {
                File.Delete(full);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(
            derived,
            "*",
            SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length))
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        return Task.CompletedTask;
    }

    private static string Safe(string value) =>
        string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'));
}

public sealed partial class SqliteBookTextIndex(
    IDbContextFactory<NostosDbContext> contexts) : IBookTextIndex
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await ExecuteAsync(db, """
                CREATE TABLE IF NOT EXISTS BookTextIngestionStates (
                    BookId TEXT NOT NULL PRIMARY KEY,
                    Status TEXT NOT NULL,
                    SourceFileName TEXT NULL,
                    Format TEXT NULL,
                    SourceSha256 TEXT NULL,
                    ExtractorVersion TEXT NULL,
                    ErrorCode TEXT NULL,
                    ErrorMessage TEXT NULL,
                    Attempts INTEGER NOT NULL DEFAULT 0,
                    ChunkCount INTEGER NOT NULL DEFAULT 0,
                    CharacterCount INTEGER NOT NULL DEFAULT 0,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """, ct);

            await ExecuteAsync(db, """
                CREATE TABLE IF NOT EXISTS BookTextChunks (
                    Id TEXT NOT NULL PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    SourceSha256 TEXT NOT NULL,
                    ExtractorVersion TEXT NOT NULL,
                    Format TEXT NOT NULL,
                    Ordinal INTEGER NOT NULL,
                    Text TEXT NOT NULL,
                    HeadingPathJson TEXT NOT NULL,
                    SourceSegmentsJson TEXT NOT NULL
                );
                """, ct);

            await ExecuteAsync(db, """
                CREATE UNIQUE INDEX IF NOT EXISTS IX_BookTextChunks_RevisionOrdinal
                ON BookTextChunks(BookId, SourceSha256, ExtractorVersion, Ordinal);
                """, ct);

            await ExecuteAsync(db, """
                CREATE INDEX IF NOT EXISTS IX_BookTextChunks_Book
                ON BookTextChunks(BookId);
                """, ct);

            await ExecuteAsync(db, """
                CREATE VIRTUAL TABLE IF NOT EXISTS BookTextChunksFts
                USING fts5(ChunkId UNINDEXED, BookId UNINDEXED, Text, HeadingPath, tokenize='unicode61');
                """, ct);

            await EnsureEmbeddingSchemaAsync(db, ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task ScheduleAsync(
        Guid bookId,
        string sourceFileName,
        BookTextSourceFormat format,
        CancellationToken ct = default)
    {
        // The derived schema is absent in a freshly activated/restored
        // generation until a first caller recreates it; scheduling a new
        // upload must not fail on that gap.
        await EnsureSchemaAsync(ct);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await DeleteChunksAsync(db, bookId, ct);
            await ExecuteAsync(db, """
                INSERT INTO BookTextIngestionStates
                    (BookId, Status, SourceFileName, Format, SourceSha256, ExtractorVersion,
                     ErrorCode, ErrorMessage, Attempts, ChunkCount, CharacterCount, UpdatedAtUtc)
                VALUES
                    (@bookId, 'Pending', @fileName, @format, NULL, @version,
                     NULL, NULL, 0, 0, 0, @updated)
                ON CONFLICT(BookId) DO UPDATE SET
                    Status='Pending',
                    SourceFileName=excluded.SourceFileName,
                    Format=excluded.Format,
                    SourceSha256=NULL,
                    ExtractorVersion=excluded.ExtractorVersion,
                    ErrorCode=NULL,
                    ErrorMessage=NULL,
                    Attempts=0,
                    ChunkCount=0,
                    CharacterCount=0,
                    UpdatedAtUtc=excluded.UpdatedAtUtc;
                """, ct,
                ("@bookId", bookId.ToString("D")),
                ("@fileName", sourceFileName),
                ("@format", format.ToString()),
                ("@version", BookTextArtifactSchema.CurrentExtractorVersion),
                ("@updated", DateTime.UtcNow.ToString("O")));
            await tx.CommitAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<BookTextIngestionWork?> TryClaimNextAsync(
        TimeSpan staleAfter,
        CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var cutoff = DateTime.UtcNow.Subtract(staleAfter).ToString("O");
            await using var select = Command(db, """
                SELECT BookId, SourceFileName, Format, Attempts
                FROM BookTextIngestionStates
                WHERE Status='Pending'
                   OR (Status='Processing' AND UpdatedAtUtc < @cutoff)
                ORDER BY UpdatedAtUtc
                LIMIT 1;
                """, ("@cutoff", cutoff));

            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            var id = Guid.Parse(reader.GetString(0));
            var fileName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (!Enum.TryParse<BookTextSourceFormat>(
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    ignoreCase: true,
                    out var format))
                return null;
            var attempt = reader.GetInt32(3) + 1;
            await reader.DisposeAsync();

            var changed = await ExecuteAsync(db, """
                UPDATE BookTextIngestionStates
                SET Status='Processing', Attempts=@attempt, UpdatedAtUtc=@updated,
                    ErrorCode=NULL, ErrorMessage=NULL
                WHERE BookId=@bookId
                  AND (Status='Pending' OR (Status='Processing' AND UpdatedAtUtc < @cutoff));
                """, ct,
                ("@attempt", attempt),
                ("@updated", DateTime.UtcNow.ToString("O")),
                ("@bookId", id.ToString("D")),
                ("@cutoff", cutoff));

            return changed == 1
                ? new BookTextIngestionWork(id, fileName, format, attempt)
                : null;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<bool> ReplaceReadyAsync(
        BookTextSourceRevision revision,
        IReadOnlyList<BookTextIndexedChunk> chunks,
        long characterCount,
        int expectedAttempt,
        CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Claim ownership of completion before writing chunks. A delete,
            // replacement, or stale-work retry changes/removes this row and
            // therefore makes the old worker a no-op.
            var claimed = await ExecuteAsync(db, """
                UPDATE BookTextIngestionStates
                SET Status='Ready', SourceSha256=@hash, ExtractorVersion=@version,
                    ErrorCode=NULL, ErrorMessage=NULL, ChunkCount=@chunks,
                    CharacterCount=@characters, UpdatedAtUtc=@updated
                WHERE BookId=@bookId
                  AND Status='Processing'
                  AND Attempts=@attempt;
                """, ct,
                ("@hash", revision.SourceSha256),
                ("@version", revision.ExtractorVersion),
                ("@chunks", chunks.Count),
                ("@characters", characterCount),
                ("@updated", DateTime.UtcNow.ToString("O")),
                ("@bookId", revision.BookId.ToString("D")),
                ("@attempt", expectedAttempt));

            if (claimed != 1)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            await DeleteChunksAsync(db, revision.BookId, ct);

            foreach (var chunk in chunks.OrderBy(chunk => chunk.Ordinal))
            {
                var headingJson = JsonSerializer.Serialize(chunk.HeadingPath, Json);
                var sourceJson = JsonSerializer.Serialize(chunk.SourceSegments, Json);

                await ExecuteAsync(db, """
                    INSERT INTO BookTextChunks
                        (Id, BookId, SourceSha256, ExtractorVersion, Format, Ordinal,
                         Text, HeadingPathJson, SourceSegmentsJson)
                    VALUES
                        (@id, @bookId, @hash, @version, @format, @ordinal,
                         @text, @heading, @source);
                    """, ct,
                    ("@id", chunk.Id.ToString("D")),
                    ("@bookId", chunk.BookId.ToString("D")),
                    ("@hash", chunk.SourceSha256),
                    ("@version", chunk.ExtractorVersion),
                    ("@format", chunk.Format.ToString()),
                    ("@ordinal", chunk.Ordinal),
                    ("@text", chunk.Text),
                    ("@heading", headingJson),
                    ("@source", sourceJson));

                await ExecuteAsync(db, """
                    INSERT INTO BookTextChunksFts(ChunkId, BookId, Text, HeadingPath)
                    VALUES (@id, @bookId, @text, @heading);
                    """, ct,
                    ("@id", chunk.Id.ToString("D")),
                    ("@bookId", chunk.BookId.ToString("D")),
                    ("@text", chunk.Text),
                    ("@heading", string.Join(" / ", chunk.HeadingPath)));
            }

            await tx.CommitAsync(ct);
            return true;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<bool> MarkFailedAsync(
        Guid bookId,
        string errorCode,
        string errorMessage,
        bool unsupported,
        int expectedAttempt,
        CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var changed = await ExecuteAsync(db, """
                UPDATE BookTextIngestionStates
                SET Status=@status, ExtractorVersion=@version,
                    ErrorCode=@code, ErrorMessage=@message, UpdatedAtUtc=@updated
                WHERE BookId=@bookId
                  AND Status='Processing'
                  AND Attempts=@attempt;
                """, ct,
                ("@status", unsupported ? "Unsupported" : "Failed"),
                ("@version", BookTextArtifactSchema.CurrentExtractorVersion),
                ("@code", Limit(errorCode, 100)),
                ("@message", Limit(errorMessage, 500)),
                ("@updated", DateTime.UtcNow.ToString("O")),
                ("@bookId", bookId.ToString("D")),
                ("@attempt", expectedAttempt));

            return changed == 1;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task DeleteBookAsync(Guid bookId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await DeleteChunksAsync(db, bookId, ct);
            await ExecuteAsync(
                db,
                "DELETE FROM BookTextIngestionStates WHERE BookId=@bookId;",
                ct,
                ("@bookId", bookId.ToString("D")));
            await tx.CommitAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<BookTextIngestionState?> GetStateAsync(Guid bookId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = Command(
                db,
                "SELECT BookId, Status, SourceFileName, Format, SourceSha256, ExtractorVersion, ErrorCode, ErrorMessage, Attempts, ChunkCount, CharacterCount, UpdatedAtUtc FROM BookTextIngestionStates WHERE BookId=@bookId;",
                ("@bookId", bookId.ToString("D")));
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? State(reader) : null;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<IReadOnlyList<BookTextSearchHit>> SearchAsync(
        string query,
        IReadOnlyList<Guid> bookIds,
        int maxCandidates,
        CancellationToken ct = default)
    {
        var fts = BuildFtsQuery(query);
        if (fts.Length == 0 || bookIds.Count == 0)
            return [];

        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var parameters = new List<(string, object?)> { ("@query", fts), ("@limit", Math.Clamp(maxCandidates, 1, 100)) };
            var ids = new List<string>();
            for (var i = 0; i < bookIds.Count; i++)
            {
                var name = $"@b{i}";
                ids.Add(name);
                parameters.Add((name, bookIds[i].ToString("D")));
            }

            var sql = $"""
                SELECT c.Id, c.BookId, c.SourceSha256, c.ExtractorVersion, c.Format,
                       c.Ordinal, c.Text, c.HeadingPathJson, c.SourceSegmentsJson,
                       bm25(BookTextChunksFts) AS Rank
                FROM BookTextChunksFts
                JOIN BookTextChunks c ON c.Id = BookTextChunksFts.ChunkId
                JOIN BookTextIngestionStates s ON s.BookId = c.BookId
                WHERE BookTextChunksFts MATCH @query
                  AND c.BookId IN ({string.Join(",", ids)})
                  AND s.Status='Ready'
                  AND s.SourceSha256=c.SourceSha256
                  AND s.ExtractorVersion=c.ExtractorVersion
                ORDER BY Rank
                LIMIT @limit;
                """;

            await using var command = Command(db, sql, parameters.ToArray());
            await using var reader = await command.ExecuteReaderAsync(ct);
            var hits = new List<BookTextSearchHit>();
            while (await reader.ReadAsync(ct))
            {
                var chunk = ReadChunk(reader);
                var rank = reader.IsDBNull(9) ? 0d : reader.GetDouble(9);
                hits.Add(new BookTextSearchHit(chunk, -rank));
            }
            return hits;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public async Task<IReadOnlyList<BookTextIndexedChunk>> GetNeighborsAsync(
        Guid bookId,
        string sourceSha256,
        string extractorVersion,
        int ordinal,
        int radius,
        CancellationToken ct = default)
    {
        if (radius <= 0) return [];
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = Command(db, """
                SELECT Id, BookId, SourceSha256, ExtractorVersion, Format,
                       Ordinal, Text, HeadingPathJson, SourceSegmentsJson
                FROM BookTextChunks
                WHERE BookId=@bookId AND SourceSha256=@hash AND ExtractorVersion=@version
                  AND Ordinal BETWEEN @start AND @end
                ORDER BY Ordinal;
                """,
                ("@bookId", bookId.ToString("D")),
                ("@hash", sourceSha256),
                ("@version", extractorVersion),
                ("@start", ordinal - radius),
                ("@end", ordinal + radius));
            await using var reader = await command.ExecuteReaderAsync(ct);
            var chunks = new List<BookTextIndexedChunk>();
            while (await reader.ReadAsync(ct))
                chunks.Add(ReadChunk(reader));
            return chunks;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task DeleteChunksAsync(NostosDbContext db, Guid bookId, CancellationToken ct)
    {
        // Vectors are derived from chunks: they never outlive the chunk they embed.
        await ExecuteAsync(db, "DELETE FROM BookTextChunkEmbeddings WHERE BookId=@bookId;", ct, ("@bookId", bookId.ToString("D")));
        await ExecuteAsync(db, "DELETE FROM BookTextChunksFts WHERE BookId=@bookId;", ct, ("@bookId", bookId.ToString("D")));
        await ExecuteAsync(db, "DELETE FROM BookTextChunks WHERE BookId=@bookId;", ct, ("@bookId", bookId.ToString("D")));
    }

    private static BookTextIngestionState State(DbDataReader reader)
    {
        Enum.TryParse<BookTextIngestionStatus>(reader.GetString(1), true, out var status);
        BookTextSourceFormat? format = null;
        if (!reader.IsDBNull(3) && Enum.TryParse<BookTextSourceFormat>(reader.GetString(3), true, out var parsed))
            format = parsed;

        return new BookTextIngestionState(
            Guid.Parse(reader.GetString(0)),
            status,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            format,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt64(10),
            DateTime.Parse(reader.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    private static BookTextIndexedChunk ReadChunk(DbDataReader reader)
    {
        Enum.TryParse<BookTextSourceFormat>(reader.GetString(4), true, out var format);
        var headings = JsonSerializer.Deserialize<string[]>(reader.GetString(7), Json) ?? [];
        var segments = JsonSerializer.Deserialize<BookTextSourceSegment[]>(reader.GetString(8), Json) ?? [];
        return new BookTextIndexedChunk(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            format,
            reader.GetInt32(5),
            reader.GetString(6),
            headings,
            segments);
    }

    private static string BuildFtsQuery(string query)
    {
        var tokens = query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(ch => char.IsLetterOrDigit(ch) || ch == '\'').ToArray()))
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .Select(token => "\"" + token.Replace("\"", "\"\"") + "\"")
            .ToList();
        return string.Join(" OR ", tokens);
    }

    private static string Limit(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static DbCommand Command(NostosDbContext db, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static async Task<int> ExecuteAsync(
        NostosDbContext db,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(db, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
