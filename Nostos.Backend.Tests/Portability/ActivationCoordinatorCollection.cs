using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Activation cutover tests call the process-wide
/// <c>SqliteConnection.ClearAllPools()</c> primitive and move real SQLite files,
/// so they must not run in parallel with other collections: an idle pooled
/// connection in another test's database could otherwise be closed (and its WAL
/// checkpointed) by this suite's maintenance windows.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ActivationCoordinatorCollection
{
    public const string Name = "activation-coordinator";
}
