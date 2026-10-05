using Nostos.Backend.Data;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Brings a working copy of a retained recovery database to the current schema
/// before its portable payload is extracted. Injectable so tests can model a
/// migration that succeeds but does not preserve portable state.
/// </summary>
internal interface ISelfHostedRecoverySchemaMigrator
{
    Task MigrateAsync(NostosDbContext database, CancellationToken ct);
}

/// <summary>
/// Production migrator: the ordinary startup path (forward EF migrations plus
/// the idempotent identity backfill). Runs only against the working copy.
/// </summary>
internal sealed class SelfHostedRecoverySchemaMigrator : ISelfHostedRecoverySchemaMigrator
{
    public Task MigrateAsync(NostosDbContext database, CancellationToken ct) =>
        new DatabaseBootstrapService(database).EnsureReadyAsync(ct);
}
