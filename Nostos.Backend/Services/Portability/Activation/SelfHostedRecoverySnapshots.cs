using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// One captured primary/derived media file of the previous library. The
/// relative path never leaves this process; the manifest stores only the
/// book id, kind, extension, byte length and SHA-256.
/// </summary>
internal sealed record SelfHostedRecoveryMediaPin(
    string RelativePath,
    Guid BookId,
    string Kind,
    string Extension,
    long Bytes,
    DateTime LastWriteUtc,
    string Sha256);

/// <summary>
/// In-memory pins of the live previous library, captured before maintenance
/// and rechecked (path set, bytes, last-write) under the exclusive lease.
/// </summary>
internal sealed record SelfHostedRecoveryCapture(
    Guid JobId,
    Guid OperationId,
    string PreviousDestinationRevision,
    MigrationExistingCounts Counts,
    DateTimeOffset CapturedAtUtc,
    long DatabaseBytes,
    string DatabaseSchemaVersion,
    int DatabaseMigrationCount,
    long MediaBytes,
    IReadOnlyList<SelfHostedRecoveryMediaPin> Media);

/// <summary>
/// Retention primitives for the activation coordinator (Slice 7). Every method
/// that mutates the live library requires the coordinator's current exclusive
/// lease; every method is idempotent so a crashed step can be repeated after
/// the journal reconciler has restored a complete generation. The activation
/// journal, not this service, is the authority for the cutover phase.
/// </summary>
internal interface ISelfHostedRecoverySnapshots
{
    /// <summary>Read-only pinning of the live previous library; no mutation.</summary>
    Task<SelfHostedRecoveryCapture> CaptureAsync(
        Guid jobId,
        Guid operationId,
        string previousDestinationRevision,
        MigrationExistingCounts counts,
        CancellationToken ct);

    /// <summary>
    /// Under the exclusive lease: recheck the captured media metadata, hash the
    /// checkpointed live database, and durably publish the <c>Creating</c>
    /// manifest describing the copy that retention is about to make.
    /// </summary>
    Task<SelfHostedRecoveryManifest> PrepareRetentionAsync(
        Guid jobId,
        SelfHostedRecoveryCapture capture,
        IAsyncDisposable lease,
        CancellationToken ct);

    /// <summary>
    /// Rename the live media root into <c>.nostos-recovery/&lt;id&gt;/books</c>.
    /// Crash-safe: either the live root remains or the retained one exists.
    /// </summary>
    Task<SelfHostedRecoveryManifest> RetainMediaAsync(
        Guid jobId,
        IAsyncDisposable lease,
        CancellationToken ct);

    /// <summary>
    /// Rename the checkpointed live database into
    /// <c>.nostos-recovery/&lt;id&gt;/nostos.db</c>. Refuses a nonempty WAL.
    /// </summary>
    Task<SelfHostedRecoveryManifest> RetainDatabaseAsync(
        Guid jobId,
        IAsyncDisposable lease,
        CancellationToken ct);

    /// <summary>
    /// After both candidates are active: claim the measured previous bytes as a
    /// non-expiring recovery reservation (offsetting this job's outstanding
    /// transfer claim so the same bytes are not charged twice) and publish the
    /// <c>Available</c> manifest with the seven-day expiry.
    /// </summary>
    Task<SelfHostedRecoveryManifest> FinalizeRetentionAsync(
        Guid jobId,
        Guid? transferReservationId,
        IAsyncDisposable lease,
        CancellationToken ct);

    /// <summary>
    /// Removes an unfinished retention plan after the journal has been resolved
    /// back to a pre-cutover state. Refuses while retained material exists.
    /// </summary>
    Task AbandonAsync(Guid jobId, IAsyncDisposable lease, CancellationToken ct);
}

/// <summary>
/// Provider-neutral listing surface for retained recovery copies. Implemented
/// by the same service as <see cref="ISelfHostedRecoverySnapshots"/>; the
/// HTTP layer (Slice 8) can project it directly.
/// </summary>
public interface ISelfHostedRecoveryCatalog
{
    Task<IReadOnlyList<MigrationRecoveryStatusResponse>> ListAsync(CancellationToken ct);

    Task<MigrationRecoveryStatusResponse?> GetAsync(Guid jobId, CancellationToken ct);
}

/// <summary>
/// Expired-copy cleanup surface. No transfer cleanup worker is merged on main
/// yet, so Slice 7/10 must invoke this operation from the eventual sweep.
/// </summary>
public interface ISelfHostedRecoveryCleanup
{
    /// <summary>
    /// Deletes expired, inactive recovery copies and releases their capacity.
    /// Returns the number of copies physically removed. Failures leave the
    /// durable deletion marker in place so a later sweep resumes safely.
    /// </summary>
    Task<int> DeleteExpiredAsync(CancellationToken ct);
}

/// <summary>
/// Filesystem-backed recovery snapshots for #681 (plan sections 14-17).
/// Retains the previous library by same-volume rename, describes it with a
/// checksummed manifest, accounts it through <see cref="ITransferStorageCapacity"/>,
/// lists it provider-neutrally, and deletes it only after the seven-day expiry
/// with a durable deleting mark and a confined, idempotent removal.
/// </summary>
internal sealed class SelfHostedMigrationRecoveryService :
    ISelfHostedRecoverySnapshots,
    ISelfHostedRecoveryCatalog,
    ISelfHostedRecoveryCleanup
{
    private const int CopyBufferSize = 81920;

    private readonly SelfHostedActivationPaths _paths;
    private readonly SelfHostedRecoveryManifestStore _manifests;
    private readonly SelfHostedActivationJournalStore _journals;
    private readonly SelfHostedActivationCapacity _activationCapacity;
    private readonly ITransferStorageCapacity _capacity;
    private readonly NostosDbContext _db;
    private readonly LibraryMaintenanceCoordinator _maintenance;
    private readonly TransferStorageOptions _options;
    private readonly TimeProvider _clock;

    internal SelfHostedMigrationRecoveryService(
        SelfHostedActivationPaths paths,
        SelfHostedRecoveryManifestStore manifests,
        SelfHostedActivationJournalStore journals,
        SelfHostedActivationCapacity activationCapacity,
        ITransferStorageCapacity capacity,
        NostosDbContext db,
        LibraryMaintenanceCoordinator maintenance,
        IOptions<TransferStorageOptions> options,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(journals);
        ArgumentNullException.ThrowIfNull(activationCapacity);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(options);
        TransferStorageOptions.Validate(options.Value);

        _paths = paths;
        _manifests = manifests;
        _journals = journals;
        _activationCapacity = activationCapacity;
        _capacity = capacity;
        _db = db;
        _maintenance = maintenance;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<SelfHostedRecoveryCapture> CaptureAsync(
        Guid jobId,
        Guid operationId,
        string previousDestinationRevision,
        MigrationExistingCounts counts,
        CancellationToken ct)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("A job identifier is required.", nameof(jobId));
        if (operationId == Guid.Empty) throw new ArgumentException("An operation identifier is required.", nameof(operationId));
        if (string.IsNullOrWhiteSpace(previousDestinationRevision))
            throw new ArgumentException("A destination revision is required.", nameof(previousDestinationRevision));
        ArgumentNullException.ThrowIfNull(counts);

        if (!File.Exists(_paths.LiveDatabase)) throw Flaw("The live database is missing.");
        var media = await EnumerateLiveMediaWithHashesAsync(ct);
        var database = new FileInfo(_paths.LiveDatabase);
        var schema = ReadSchemaLevel(_paths.LiveDatabase);
        return new SelfHostedRecoveryCapture(jobId, operationId, previousDestinationRevision, counts,
            _clock.GetUtcNow(), database.Length, schema.Version, schema.Count, media.Bytes, media.Pins);
    }

    public async Task<SelfHostedRecoveryManifest> PrepareRetentionAsync(
        Guid jobId,
        SelfHostedRecoveryCapture capture,
        IAsyncDisposable lease,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capture);
        RequireLease(lease);
        if (capture.JobId != jobId) throw Flaw("The recovery capture belongs to a different job.");

        var existing = _manifests.Read(jobId);
        if (existing is not null
            && (existing.Status != MigrationRecoveryStatus.Creating || existing.MediaRetained || existing.DatabaseRetained))
            return existing;

        var journal = _journals.Read(jobId)
            ?? throw Flaw("An activation journal is required before recovery retention.");
        if (journal.OperationId != capture.OperationId
            || journal.DestinationRevision != capture.PreviousDestinationRevision
            || !journal.RetainPreviousLibrary)
            throw Flaw("The recovery capture does not match the activation journal.");
        if (journal.Phase is not (SelfHostedActivationPhase.DatabaseCheckpointed
            or SelfHostedActivationPhase.CutoverPrepared))
            throw Flaw("The live database must be checkpointed before recovery retention.");

        RecheckMedia(capture);
        if (!File.Exists(_paths.LiveDatabase)) throw Flaw("The live database is missing.");
        var databaseBytes = new FileInfo(_paths.LiveDatabase).Length;
        var databaseSha256 = await HashFileAsync(_paths.LiveDatabase, ct);
        var schema = ReadSchemaLevel(_paths.LiveDatabase);
        var created = _clock.GetUtcNow();
        var manifest = new SelfHostedRecoveryManifest(
            jobId,
            capture.OperationId,
            created,
            SelfHostedRecoveryManifest.Expiry(created),
            MigrationRecoveryStatus.Creating,
            capture.PreviousDestinationRevision,
            capture.Counts,
            databaseBytes,
            capture.MediaBytes,
            databaseSha256,
            capture.Media.Select(pin => new RecoveryMediaDescriptor(pin.BookId, pin.Kind, pin.Extension, pin.Bytes, pin.Sha256)).ToArray(),
            DatabaseSchemaVersion: schema.Version,
            DatabaseMigrationCount: schema.Count);
        _manifests.Write(manifest);
        return manifest;
    }

    public Task<SelfHostedRecoveryManifest> RetainMediaAsync(
        Guid jobId,
        IAsyncDisposable lease,
        CancellationToken ct)
    {
        RequireLease(lease);
        var manifest = RequireCreating(jobId);
        var live = _paths.LiveMedia;
        var previous = _paths.PreviousMedia(jobId);
        _paths.VerifyMediaPath(live);
        _paths.VerifyMediaPath(previous);

        if (manifest.MediaRetained && Directory.Exists(previous) && !Directory.Exists(live))
            return Task.FromResult(manifest);
        if (Directory.Exists(previous) && Directory.Exists(live))
            throw Flaw("Both the live and the retained media roots exist.");
        if (!Directory.Exists(live))
            throw Flaw("The live media root is missing.");

        ActivationFileSystem.Rename(live, previous);
        return Task.FromResult(Update(jobId, manifest with { MediaRetained = true }));
    }

    public Task<SelfHostedRecoveryManifest> RetainDatabaseAsync(
        Guid jobId,
        IAsyncDisposable lease,
        CancellationToken ct)
    {
        RequireLease(lease);
        var manifest = RequireCreating(jobId);
        if (!manifest.MediaRetained) throw Flaw("Media must be retained before the database.");
        var live = _paths.LiveDatabase;
        var previous = _paths.PreviousDatabase(jobId);
        _paths.VerifyDatabasePath(live);
        _paths.VerifyDatabasePath(previous);

        if (manifest.DatabaseRetained && File.Exists(previous) && !File.Exists(live))
            return Task.FromResult(manifest);
        if (File.Exists(previous) && File.Exists(live))
            throw Flaw("Both the live and the retained databases exist.");
        if (!File.Exists(live)) throw Flaw("The live database is missing.");

        // The retained database must be self-contained: a nonempty WAL would
        // carry committed rows that the recovery copy cannot replay without it.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = live + suffix;
            if (!File.Exists(sidecar)) continue;
            if (string.Equals(suffix, "-wal", StringComparison.Ordinal) && new FileInfo(sidecar).Length > 0)
                throw Flaw("The live database still has an uncheckpointed WAL.");
            File.Delete(sidecar);
        }

        ActivationFileSystem.Rename(live, previous);
        return Task.FromResult(Update(jobId, manifest with { DatabaseRetained = true }));
    }

    public async Task<SelfHostedRecoveryManifest> FinalizeRetentionAsync(
        Guid jobId,
        Guid? transferReservationId,
        IAsyncDisposable lease,
        CancellationToken ct)
    {
        RequireLease(lease);
        var manifest = _manifests.Read(jobId) ?? throw Flaw("A recovery manifest is required before finalization.");
        if (manifest.Status == MigrationRecoveryStatus.Available)
        {
            var healed = await EnsureRetentionReservationAsync(manifest, transferReservationId, ct);
            if (healed.RetentionReservationId != manifest.RetentionReservationId)
            {
                _manifests.Write(healed);
                return healed;
            }

            return manifest;
        }

        if (manifest.Status != MigrationRecoveryStatus.Creating
            || !manifest.MediaRetained
            || !manifest.DatabaseRetained)
            throw Flaw("The retained copy is not complete yet.");

        manifest = await EnsureRetentionReservationAsync(manifest, transferReservationId, ct);
        var now = _clock.GetUtcNow();
        var available = manifest with
        {
            Status = MigrationRecoveryStatus.Available,
            CreatedAtUtc = now,
            ExpiresAtUtc = SelfHostedRecoveryManifest.Expiry(now),
            DatabaseRetained = true,
            MediaRetained = true,
        };
        _manifests.Write(available);
        return available;
    }

    public async Task AbandonAsync(Guid jobId, IAsyncDisposable lease, CancellationToken ct)
    {
        RequireLease(lease);
        var manifest = _manifests.Read(jobId);
        if (manifest is null)
        {
            _manifests.DeleteUnusedPlan(jobId);
            return;
        }

        if (manifest.Status != MigrationRecoveryStatus.Creating)
            throw Flaw("Only an unfinished retention plan can be abandoned.");
        if (_manifests.MaterialExists(jobId))
            throw Flaw("The recovery copy is still present; reconcile the activation journal first.");
        await ReleaseAsync(manifest.RetentionReservationId, ct);
        _manifests.DeleteUnusedPlan(jobId);
    }

    public Task<IReadOnlyList<MigrationRecoveryStatusResponse>> ListAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<MigrationRecoveryStatusResponse> result = _manifests.ReadAll()
            .OrderBy(manifest => manifest.CreatedAtUtc)
            .ThenBy(manifest => manifest.JobId)
            .Select(ToResponse)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<MigrationRecoveryStatusResponse?> GetAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (jobId == Guid.Empty) throw new ArgumentException("A job identifier is required.", nameof(jobId));
        if (_manifests.HasDeletionMarker(jobId)) return Task.FromResult<MigrationRecoveryStatusResponse?>(null);
        var manifest = _manifests.Read(jobId);
        return Task.FromResult(manifest is null ? null : ToResponse(manifest));
    }

    public async Task<int> DeleteExpiredAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var cleaned = 0;
        foreach (var jobId in _manifests.ListJobDirectories())
        {
            ct.ThrowIfCancellationRequested();
            var deleting = _manifests.HasDeletionMarker(jobId);
            var manifest = _manifests.Read(jobId);
            if (!deleting)
            {
                if (manifest is null)
                {
                    if (_manifests.MaterialExists(jobId)) throw SelfHostedRecoveryManifestStore.Corrupt();
                    continue;
                }

                if (manifest.Status == MigrationRecoveryStatus.Creating)
                {
                    if (!_manifests.MaterialExists(jobId) && _journals.Read(jobId) is null)
                        _manifests.DeleteUnusedPlan(jobId);
                    continue;
                }

                if (manifest.Status != MigrationRecoveryStatus.Available) continue;
                if (manifest.ExpiresAtUtc > now) continue;
                if (_journals.Read(jobId) is not null) continue; // activation or restore still references it
            }
            else if (manifest is null && !_manifests.MaterialExists(jobId))
            {
                _manifests.DeleteEmptyDirectory(jobId);
                continue;
            }

            _manifests.CreateDeletionMarker(jobId);
            DeleteMaterial(jobId);
            if (manifest?.RetentionReservationId is { } reservationId && reservationId != Guid.Empty)
                await ReleaseAsync(reservationId, ct);
            _manifests.DeleteEmptyDirectory(jobId);
            cleaned++;
        }

        return cleaned;
    }

    /// <summary>
    /// Per-volume admission entry point for Slice 7, called before the
    /// maintenance window: the candidate database, candidate media and any
    /// remaining staging must fit the volumes that will physically hold them,
    /// and the retention's extra claim must fit the transfer volume's durable
    /// reservation accounting. The single durable reservation below is
    /// transfer-volume race control; it is never treated as covering the
    /// database and media volumes, which are admitted by their own local
    /// margin checks.
    /// </summary>
    internal async Task EnsureAdmittedAsync(
        SelfHostedActivationSizing sizing,
        string? transferRoot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sizing);
        _activationCapacity.EnsureActivationFits(_paths.LiveDatabase, _paths.LiveMedia, transferRoot, sizing);
        var snapshot = await _capacity.GetSnapshotAsync(ct);
        _activationCapacity.EnsureRetentionClaimFits(snapshot, sizing.PreviousBytes);
    }

    private async Task<SelfHostedRecoveryManifest> EnsureRetentionReservationAsync(
        SelfHostedRecoveryManifest manifest,
        Guid? transferReservationId,
        CancellationToken ct)
    {
        var bytes = manifest.TotalBytes;
        if (bytes <= 0) return manifest with { RetentionReservationId = null };

        if (transferReservationId is { } transferId && transferId != Guid.Empty)
            await ApplyRetentionTopUpAsync(transferId, bytes, ct);

        if (manifest.RetentionReservationId is { } existing
            && existing != Guid.Empty
            && await TryClaimAsync(manifest.JobId, existing, ct))
            return manifest;

        var reserved = await _capacity.TryReserveAsync(
            bytes, MigrationSessionPurpose.Import, _options.PreflightReservationTtl, ct);
        if (!reserved.IsAdmitted || reserved.ReservationId is not { } reservationId)
            throw SelfHostedActivationCapacity.Exhausted();

        // Record the claim durably before claiming it, so a crash between the
        // two can reuse the reservation instead of orphaning a claimed row.
        manifest = manifest with { RetentionReservationId = reservationId };
        _manifests.Write(manifest);
        if (!await TryClaimAsync(manifest.JobId, reservationId, ct))
        {
            await ReleaseAsync(reservationId, ct);
            throw SelfHostedActivationCapacity.Exhausted();
        }

        return manifest;
    }

    /// <summary>
    /// The transfer reservation already charged an estimated recovery in
    /// preflight. Recording the retained bytes as materialized against it up to
    /// its outstanding amount makes the combined charge max(transfer, retained)
    /// instead of their sum, so the same bytes are never charged twice.
    /// </summary>
    private async Task ApplyRetentionTopUpAsync(Guid transferReservationId, long previousBytes, CancellationToken ct)
    {
        var reservation = await _db.MigrationStorageReservations
            .AsNoTracking()
            .Where(r => r.Id == transferReservationId)
            .Select(r => new { r.ReservedBytes, r.MaterializedBytes, r.ReleasedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (reservation is null || reservation.ReleasedAtUtc is not null) return;
        var outstanding = Math.Max(reservation.ReservedBytes - reservation.MaterializedBytes, 0);
        var topUp = Math.Min(previousBytes, outstanding);
        if (topUp <= 0) return;
        try
        {
            await _capacity.AddMaterializedBytesAsync(transferReservationId, topUp, ct);
        }
        catch (TransferReservationException)
        {
            // Fully materialized, released or gone: there is no unmaterialized
            // transfer claim left to offset.
        }
    }

    private async Task<bool> TryClaimAsync(Guid jobId, Guid reservationId, CancellationToken ct)
    {
        try
        {
            await _capacity.ClaimAsync(reservationId, jobId, ct);
            return true;
        }
        catch (TransferReservationException exception)
            when (exception.Kind == TransferReservationConflictKind.AlreadyClaimed)
        {
            var owner = await _db.MigrationStorageReservations
                .AsNoTracking()
                .Where(r => r.Id == reservationId)
                .Select(r => r.ClaimedJobId)
                .FirstOrDefaultAsync(ct);
            return owner == jobId;
        }
        catch (TransferReservationException)
        {
            return false;
        }
    }

    private Task ReleaseAsync(Guid? reservationId, CancellationToken ct) =>
        reservationId is { } id && id != Guid.Empty
            ? _capacity.ReleaseAsync(id, ct)
            : Task.CompletedTask;

    private void DeleteMaterial(Guid jobId)
    {
        var previousMedia = _paths.PreviousMedia(jobId);
        _paths.VerifyMediaPath(previousMedia);
        if (Directory.Exists(previousMedia))
        {
            Directory.Delete(previousMedia, recursive: true);
        }
        else if (File.Exists(previousMedia))
        {
            File.Delete(previousMedia);
        }

        var previousDatabase = _paths.PreviousDatabase(jobId);
        _paths.VerifyDatabasePath(previousDatabase);
        DeleteIfPresent(previousDatabase);
        foreach (var suffix in new[] { "-wal", "-shm" }) DeleteIfPresent(previousDatabase + suffix);
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private (string Version, int Count) ReadSchemaLevel(string databasePath)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), MAX(MigrationId) FROM __EFMigrationsHistory";
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return ("", 0);
            var count = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var version = reader.IsDBNull(1) ? "" : reader.GetString(1);
            return (version, count);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        {
            // No EF migration history table (for example an EnsureCreated test
            // database): record the absence rather than inventing a level.
            return ("", 0);
        }
    }

    private async Task<(List<SelfHostedRecoveryMediaPin> Pins, long Bytes)> EnumerateLiveMediaWithHashesAsync(
        CancellationToken ct)
    {
        var root = _paths.LiveMedia;
        if (!Directory.Exists(root)) throw Flaw("The live media root is missing.");
        _paths.VerifyMediaPath(root);
        var pins = new List<SelfHostedRecoveryMediaPin>();
        long total = 0;
        foreach (var directory in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            _paths.VerifyMediaPath(directory);
            if (File.Exists(directory)) throw Flaw("The media root contains an unexpected file.");
            if (!Directory.Exists(directory)) throw Flaw("The media root contains an unexpected entry.");
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out var bookId) || bookId == Guid.Empty || name != bookId.ToString("N"))
                throw Flaw("The media root contains an unexpected directory.");

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                _paths.VerifyMediaPath(entry);
                if (Directory.Exists(entry)) throw Flaw("A book folder contains an unexpected directory.");
                if (!File.Exists(entry)) throw Flaw("The media root contains an unexpected entry.");
                var before = new FileInfo(entry);
                var length = before.Length;
                var lastWrite = before.LastWriteTimeUtc;
                var sha256 = await HashFileAsync(entry, ct);
                var after = new FileInfo(entry);
                if (after.Length != length || after.LastWriteTimeUtc != lastWrite)
                    throw Conflict("The live media changed while recovery pins were captured.");
                var (kind, extension) = ClassifyMediaFile(Path.GetFileName(entry));
                pins.Add(new SelfHostedRecoveryMediaPin(
                    Path.GetRelativePath(root, entry), bookId, kind, extension, length, lastWrite, sha256));
                total += length;
            }
        }

        return (pins, total);
    }

    private void RecheckMedia(SelfHostedRecoveryCapture capture)
    {
        var root = _paths.LiveMedia;
        if (!Directory.Exists(root)) throw Conflict("The live media root changed after recovery pins were captured.");
        _paths.VerifyMediaPath(root);
        var current = new List<(string Relative, long Bytes, DateTime LastWrite)>();
        foreach (var directory in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            _paths.VerifyMediaPath(directory);
            if (!Directory.Exists(directory) || File.Exists(directory))
                throw Conflict("The live media layout changed after recovery pins were captured.");
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out _))
                throw Conflict("The live media layout changed after recovery pins were captured.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                _paths.VerifyMediaPath(entry);
                if (!File.Exists(entry) || Directory.Exists(entry))
                    throw Conflict("The live media layout changed after recovery pins were captured.");
                var info = new FileInfo(entry);
                current.Add((Path.GetRelativePath(root, entry), info.Length, info.LastWriteTimeUtc));
            }
        }

        var expected = capture.Media
            .Select(pin => (pin.RelativePath, pin.Bytes, pin.LastWriteUtc))
            .OrderBy(pin => pin.RelativePath, StringComparer.Ordinal)
            .ToArray();
        current.Sort((left, right) => string.CompareOrdinal(left.Relative, right.Relative));
        if (current.Count != expected.Length)
            throw Conflict("The live media changed after recovery pins were captured.");
        for (var index = 0; index < current.Count; index++)
        {
            if (current[index].Relative != expected[index].RelativePath
                || current[index].Bytes != expected[index].Bytes
                || current[index].LastWrite != expected[index].LastWriteUtc)
                throw Conflict("The live media changed after recovery pins were captured.");
        }
    }

    private static (string Kind, string Extension) ClassifyMediaFile(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (fileName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) return ("partial", extension);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.Equals(stem, "book", StringComparison.OrdinalIgnoreCase)) return ("book", extension);
        if (string.Equals(stem, "cover", StringComparison.OrdinalIgnoreCase)) return ("cover", extension);
        if (stem.StartsWith("cover-thumb-", StringComparison.OrdinalIgnoreCase)) return ("thumbnail", extension);
        return ("other", extension);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        var digest = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private SelfHostedRecoveryManifest RequireCreating(Guid jobId) =>
        _manifests.Read(jobId) is { Status: MigrationRecoveryStatus.Creating } manifest
            ? manifest
            : throw Flaw("A matching retention plan is required.");

    private SelfHostedRecoveryManifest Update(Guid jobId, SelfHostedRecoveryManifest manifest)
    {
        _manifests.Write(manifest);
        return manifest;
    }

    private void RequireLease(IAsyncDisposable lease)
    {
        // Entry proof: the coordinator verifies this is the current exclusive
        // lease. The caller keeps it for the whole operation.
        _maintenance.WithExclusiveLease(lease, static () => { });
    }

    private static MigrationRecoveryStatusResponse ToResponse(SelfHostedRecoveryManifest manifest) =>
        new(manifest.JobId, manifest.Status, manifest.CreatedAtUtc, manifest.ExpiresAtUtc,
            manifest.TotalBytes, manifest.Counts);

    private static MigrationActivationException Flaw(string message) =>
        SelfHostedActivationPaths.Failure(message);

    private static MigrationActivationException Conflict(string message) =>
        new(MigrationActivationErrorCodes.DestinationConflict, message);
}
