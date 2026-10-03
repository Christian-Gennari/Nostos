using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Owns exact-once execution for receipt-guarded library mutations. Commands
/// provide their domain operation; this coordinator owns serialization,
/// idempotency replay, version bumping and the process-local mutation gate.
/// </summary>
internal sealed class LibraryMutationExecutor(IDbContextFactory<NostosDbContext> contexts)
{
    private static readonly SemaphoreSlim CommandGate = new(1, 1);
    private static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new LibraryCommandResultJsonConverter() },
    };

    private readonly IDbContextFactory<NostosDbContext> _contexts = contexts;
    private DateTime Now => DateTime.UtcNow;

    public async Task<LibraryCommandResultDto> ExecuteAsync(
        string clientId,
        string idempotencyKey,
        string commandKind,
        Func<NostosDbContext, CancellationToken, Task<(bool DidChange, LibraryCommandResultDto Result)>> command,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(idempotencyKey))
            return Failure("invalid_idempotency", "ClientId and IdempotencyKey are required.");
        if (clientId.Length > 64 || idempotencyKey.Length > 128)
            return Failure("invalid_idempotency", "ClientId is limited to 64 characters and IdempotencyKey to 128.");

        await CommandGate.WaitAsync(ct);
        try
        {
            await using var db = await _contexts.CreateDbContextAsync(ct);
            var prior = await db.LibraryCommandReceipts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ClientId == clientId && x.IdempotencyKey == idempotencyKey, ct);
            if (prior is not null)
                return Deserialize(prior.ResponseJson) with { Duplicate = true };

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var outcome = await command(db, ct);
            // Commands commit their own successful saves; when a command
            // reports no change it may still have left tracked mutations
            // behind (e.g. a rejected update). Discard them so the receipt
            // save below can never flush half-applied changes.
            if (!outcome.DidChange)
                db.ChangeTracker.Clear();
            var state = db.ChangeTracker.Entries<LibraryState>().Select(x => x.Entity).SingleOrDefault()
                ?? await db.LibraryStates.AsNoTracking().SingleOrDefaultAsync(ct);
            var version = state?.StateVersion ?? outcome.Result.StateVersion;
            if (outcome.DidChange && state is not null)
            {
                version = NextVersion(state.StateVersion);
                state.StateVersion = version;
                state.UpdatedAt = Now;
            }

            var result = outcome.Result with { StateVersion = version, Duplicate = false };
            db.LibraryCommandReceipts.Add(new LibraryCommandReceipt
            {
                ClientId = clientId,
                IdempotencyKey = idempotencyKey,
                CommandKind = commandKind,
                ResponseJson = Serialize(result),
                CreatedAt = Now,
            });

            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(ct);
                await using var retryDb = await _contexts.CreateDbContextAsync(ct);
                var raced = await retryDb.LibraryCommandReceipts.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ClientId == clientId && x.IdempotencyKey == idempotencyKey, ct);
                if (raced is not null)
                    return Deserialize(raced.ResponseJson) with { Duplicate = true };
                throw;
            }
        }
        finally
        {
            CommandGate.Release();
        }
    }


    private static string NextVersion(string version) =>
        (long.TryParse(version, out var parsed) ? parsed + 1 : 1).ToString();

    private static LibraryCommandResultDto Failure(
        string code,
        string reply,
        string version = "0",
        IReadOnlyList<LibraryCandidate>? candidates = null)
    {
        if (candidates is not null)
            return new LibraryCommandResultDto(
                reply,
                new LibraryConfirmationErrorDto(code, candidates),
                version);

        return new LibraryCommandResultDto(reply, new LibraryErrorDto(code), version);
    }

    private static string Serialize(LibraryCommandResultDto result) =>
        JsonSerializer.Serialize(result, ReceiptJson);

    private static LibraryCommandResultDto Deserialize(string json) =>
        JsonSerializer.Deserialize<LibraryCommandResultDto>(json, ReceiptJson)
        ?? throw new InvalidOperationException("Stored library command receipt is invalid.");
}
