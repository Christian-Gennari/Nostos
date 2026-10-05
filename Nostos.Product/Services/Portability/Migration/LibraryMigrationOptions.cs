namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Operator-facing switch for library migration on this host. It is the
/// emergency kill switch for a destructive feature: when <c>false</c>, the
/// deployment-capabilities endpoint reports <c>supportsLibraryMigration: false</c>
/// and the migration routes refuse NEW preflights and jobs with the existing
/// typed availability errors. In-flight jobs may still be polled, cancelled or
/// finished. It never bypasses phase availability: a host whose handlers are
/// not registered can not advertise the feature even with <c>Enabled = true</c>.
/// </summary>
public sealed class LibraryMigrationOptions
{
    public const string SectionName = "LibraryMigration";

    /// <summary>
    /// Defaults to enabled for the SelfHosted host. Set
    /// <c>LibraryMigration:Enabled=false</c> (or the
    /// <c>LibraryMigration__Enabled</c> environment variable) to withdraw the
    /// feature without shipping a different binary.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Folds the operator switch into the same <see cref="IMigrationPhaseAvailability"/>
/// the migration routes consult, so the advertised capability can never
/// disagree with what the routes would accept.
/// </summary>
public sealed class ConfiguredMigrationPhaseAvailability(
    IMigrationPhaseAvailability inner,
    LibraryMigrationOptions options) : IMigrationPhaseAvailability
{
    public bool IsAvailable(MigrationDirection direction) =>
        options.Enabled && inner.IsAvailable(direction);
}
