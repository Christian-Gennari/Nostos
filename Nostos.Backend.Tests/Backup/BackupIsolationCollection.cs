using Xunit;

namespace Nostos.Backend.Tests.Backup;

/// <summary>
/// Serializes every test that creates a local backup. Backup creation spools a
/// temporary archive under the process temp path with a second-resolution name
/// (<c>nostos-backup-{yyyy-MM-ddTHHmmss}.nostos</c>, see
/// <c>BackupService.BuildArchiveAsync</c>), so two creations that overlap in
/// the same second share that path and one can fail with "Could not find file".
/// That path shape is product behaviour and is reported separately rather than
/// changed here; this collection removes the test-side parallelism that makes
/// it reachable from the suite. <see cref="BackupServiceTests"/> and
/// <c>LibraryMaintenanceHostTests</c> are the only classes that create
/// backups.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BackupIsolationCollection
{
    public const string Name = "BackupIsolation";
}
