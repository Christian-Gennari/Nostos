using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Host-local path authority; never serialized or exposed in product DTOs. Only
/// nonempty generated GUIDs select an activation area. Operator-configured parent
/// roots may be mounts/links; every descendant is checked as in TransferPathResolver.
/// DB and media may occupy different volumes; each candidate/recovery component
/// must occupy the same mount as its own live component. Empty libraries use the
/// same rollback paths temporarily; seven-day retention is a later policy layer.
/// </summary>
internal sealed class SelfHostedActivationPaths
{
    private readonly TransferPathResolver _database;
    private readonly TransferPathResolver _media;
    private readonly IActivationVolume _volume;
    internal string LiveDatabase { get; }
    internal string LiveMedia { get; }
    internal string JournalRoot => Db(".nostos-activation");
    internal string RecoveryRoot => Db(".nostos-recovery");

    internal SelfHostedActivationPaths(string database, string media, IActivationVolume? volume = null)
    {
        LiveDatabase = Path.GetFullPath(database);
        LiveMedia = Path.TrimEndingDirectorySeparator(Path.GetFullPath(media));
        _database = new TransferPathResolver(Path.GetDirectoryName(LiveDatabase)!);
        _media = new TransferPathResolver(Path.GetDirectoryName(LiveMedia)!);
        _volume = volume ?? new ActivationVolume();
        // Nesting either live component inside the other makes directory rename
        // capable of relocating the DB/journal. Reserved control names also collide.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (LiveDatabase.StartsWith(LiveMedia + Path.DirectorySeparatorChar, comparison)
            || string.Equals(LiveDatabase, LiveMedia, comparison)
            || new[] { ".nostos-activation", ".nostos-recovery" }.Any(n =>
                Path.GetFileName(LiveMedia).Equals(n, comparison) || Path.GetFileName(LiveDatabase).Equals(n, comparison)))
            throw Failure("The configured library locations overlap activation control paths.");
    }

    internal string CandidateDatabase(Guid id) => Db($".nostos-activation/{Id(id)}/candidate.db");
    internal string CandidateMedia(Guid id) => Media($".nostos-activation/{Id(id)}/candidate-books");
    internal string CandidateFinalizationMarker(Guid id) => Db($".nostos-activation/{Id(id)}/candidate.finalized.json");
    internal string PreviousDatabase(Guid id) => Db($".nostos-recovery/{Id(id)}/nostos.db");
    internal string PreviousMedia(Guid id) => Media($".nostos-recovery/{Id(id)}/books");
    internal string RecoveryManifest(Guid id) => Db($".nostos-recovery/{Id(id)}/recovery.json");
    internal string RecoveryDeletionMarker(Guid id) => Db($".nostos-recovery/{Id(id)}/recovery.deleting");
    internal string TemporaryRecoveryManifest(Guid id, Guid writeId) =>
        Db($".nostos-recovery/{Id(id)}/recovery.{Id(writeId)}.tmp");
    internal string Journal(Guid id) => Db($".nostos-activation/{Id(id)}/activation.json");
    internal string ResolvedJournal(Guid id) => Db($".nostos-activation/{Id(id)}/activation.resolved.json");
    internal string TemporaryJournal(Guid id, Guid writeId) => Db($".nostos-activation/{Id(id)}/activation.{Id(writeId)}.tmp");

    internal void Verify(Guid id)
    {
        _database.VerifyPathWithinRoot(LiveDatabase);
        _media.VerifyPathWithinRoot(LiveMedia);
        foreach (var path in new[] { CandidateDatabase(id), PreviousDatabase(id), Journal(id), ResolvedJournal(id), RecoveryManifest(id), CandidateFinalizationMarker(id) })
            _database.VerifyPathWithinRoot(path);
        foreach (var path in new[] { CandidateMedia(id), PreviousMedia(id) }) _media.VerifyPathWithinRoot(path);
        foreach (var (live, other) in new[] { (LiveDatabase, CandidateDatabase(id)), (LiveDatabase, PreviousDatabase(id)),
            (LiveMedia, CandidateMedia(id)), (LiveMedia, PreviousMedia(id)) })
            if (!_volume.SameVolume(live, other))
                throw new MigrationActivationException("migration_activation_cross_volume", "Activation components must share their live component's volume.");
    }

    internal void Prepare(Guid id)
    {
        Verify(id);
        EnsureParent(CandidateDatabase(id), _database);
        EnsureParent(CandidateMedia(id), _media);
        EnsureParent(PreviousDatabase(id), _database);
        EnsureParent(PreviousMedia(id), _media);
        Verify(id); // detect mount changes after creation
    }

    /// <summary>Ensures only the recovery directory exists on the database volume.</summary>
    internal void PrepareRecovery(Guid id)
    {
        EnsureParent(RecoveryManifest(id), _database);
        Verify(id);
    }

    internal void VerifyDatabasePath(string path) => _database.VerifyPathWithinRoot(path);
    internal void VerifyMediaPath(string path) => _media.VerifyPathWithinRoot(path);
    private string Db(string key) => _database.ResolveStorageKey(key);
    private string Media(string key) => _media.ResolveStorageKey(key);
    private static string Id(Guid id) => id == Guid.Empty ? throw Failure("Activation identifiers must be generated nonempty GUIDs.") : id.ToString("N");
    private static void EnsureParent(string path, TransferPathResolver resolver)
    {
        resolver.EnsureParentDirectoryExists(path);
        for (var parent = Path.GetDirectoryName(path)!; ; parent = Path.GetDirectoryName(parent)!)
        {
            ActivationFileSystem.FlushDirectory(parent);
            if (parent == resolver.RootPath) break;
        }
    }
    internal static MigrationActivationException Failure(string message) => new(MigrationActivationErrorCodes.RecoveryFailed, message);
}
