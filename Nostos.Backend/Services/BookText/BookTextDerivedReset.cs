using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;

namespace Nostos.Backend.Services.BookText;

/// <summary>
/// Wipes every book-text derived row (chunks, FTS rows, vectors and ingestion
/// state) in one transaction. The tables are created by
/// <see cref="SqliteBookTextIndex.EnsureSchemaAsync"/>, are not part of the
/// portable EF model, and are excluded from activation candidate media; the
/// post-activation rebuild calls this so no old-generation chunk, vector or
/// state row can outlive the switch and be served as authoritative.
/// </summary>
internal interface IBookTextDerivedReset
{
    Task ResetAllAsync(CancellationToken ct = default);
}

internal sealed class SqliteBookTextDerivedReset(IDbContextFactory<NostosDbContext> contexts)
    : IBookTextDerivedReset
{
    private static readonly string[] DerivedTables =
    [
        "BookTextChunksFts",
        "BookTextChunks",
        "BookTextChunkEmbeddings",
        "BookTextIngestionStates",
    ];

    public async Task ResetAllAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var table in DerivedTables)
            {
                // Table names are compile-time constants, never caller input.
                await db.Database.ExecuteSqlRawAsync("DELETE FROM " + table + ";", ct);
            }

            await transaction.CommitAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
