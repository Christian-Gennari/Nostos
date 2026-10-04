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
/// In-memory evidence of the live previous library captured before maintenance.
/// <see cref="CaptureStartedAtUtc"/> is the start of the capture pass: media
/// last written at or after that instant minus the safety window is re-hashed
/// under the exclusive lease because its timestamp cannot prove the hashed
/// bytes are still the retained bytes.
/// </summary>
internal sealed record SelfHostedRecoveryCapture(
    Guid JobId,
    Guid OperationId,
    string PreviousDestinationRevision,
    MigrationExistingCounts Counts,
    DateTimeOffset CaptureStartedAtUtc,
    long DatabaseBytes,
    string DatabaseSchemaVersion,
    int DatabaseMigrationCount,
    long MediaBytes,
    IReadOnlyList<SelfHostedRecoveryMediaPin> Media);

/// <summary>Fault-injection points for the finalization accounting sequence.</summary>
internal enum SelfHostedRecoveryFinalizeStep
{
    BeforeTopUp,
    AfterTopUp,
    AfterReservationRecorded,
    AfterClaim,
    BeforeAvailable,
}

/// <summary>
/// Retention primitives for the activation coordinator (Slice 7). Every method
/// that mutates the live library requires the coordinator's current exclusive
/// lease; every method is idempotent so a crashed step can be repeated after
/// the journal reconciler has restored a complete generation. The activation
/// journal, not this service, is the authority for the cutover phase.
/// <para>
/// Each replacement retains its own seven-day copy under
/// <c>.nostos-recovery/&lt;job-id&gt;/</c>, several can coexist, and each keeps
/// its own claimed recovery reservation until its own cleanup deletes it.
/// </para>
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
    /// Under the exclusive lease: build the retained-media evidence (re-hashing
    /// every file whose metadata cannot prove its capture hash), hash the
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
    /// The first rename requires journal phase <c>CutoverPrepared</c>; if the
    /// source is already gone and the destination present (the rename completed
    /// but the manifest flag write crashed), the destination is verified against
    /// the manifest and the step continues.
    /// </summary>
    Task<SelfHostedRecoveryManifest> RetainMediaAsync(
        Guid jobId,
        IAsyncDisposable lease,
        CancellationToken ct);

    /// <summary>
    /// Rename the checkpointed live database into
    /// <c>.nostos-recovery/&lt;id&gt;/nostos.db</c>. Refuses a nonempty WAL. An
    /// already-completed rename is verified by exact length and SHA-256 against
    /// the manifest before continuing.
    /// </summary>
    Task<SelfHostedRecoveryManifest> RetainDatabaseAsync(
        Guid jobId,
        IAsyncDisposable lease,
        CancellationToken ct);

    /// <summary>
    /// After both candidates are active: settle the one-time absolute offset of
    /// the job's unmaterialized transfer claim, claim the measured previous
    /// bytes as a non-expiring recovery reservation, and publish the
    /// <c>Available</c> manifest with the seven-day expiry. Every step is
    /// idempotent across retries and crashes.
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

    /// <summary>
    /// Conservative timestamp-granularity window: a file last written at or
    /// after <c>captureStart - this window</c> is re-hashed under the exclusive
    /// lease. Two seconds covers the coarsest common granularity (FAT/exFAT
    /// 2 s, HFS+ 1 s; ext4/NTFS are finer), so a same-length rewrite inside one
    /// timestamp granule can never keep a stale capture hash. Deliberate
    /// back-dating of timestamps by a local actor is outside the threat model.
    /// </summary>
    internal static readonly TimeSpan MediaHashSafetyWindow = TimeSpan.FromSeconds(2);

    private readonly SelfHostedActivationPaths _paths;
    private readonly SelfHostedRecoveryManifestStore _manifests;
    private readonly SelfHostedActivationJournalStore _journals;
    private readonly SelfHostedActivationCapacity _activationCapacity;
    private readonly ITransferStorageCapacity _capacity;
    private readonly NostosDbContext _db;
    private readonly LibraryMaintenanceCoordinator _maintenance;
    private readonly TransferStorageOptions _options;
    private readonly TimeProvider _clock;

    /// <summary>Test seam: counts hashing work so re-hash bounds can be asserted.</summary>
    internal Action<string>? HashingForTesting { get; set; }

    /// <summary>Test seam: throws at named finalization steps to simulate a crash.</summary>
    internal Action<SelfHostedRecoveryFinalizeStep>? FinalizeStepForTesting { get; set; }

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
        var captureStartedAtUtc = _clock.GetUtcNow();
        var files = EnumerateLiveMediaFiles();
        var pins = new List<SelfHostedRecoveryMediaPin>(files.Count);
        long mediaBytes = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var sha256 = await HashStableFileAsync(file.Path, ct);
            pins.Add(new SelfHostedRecoveryMediaPin(
                file.RelativePath, file.BookId, file.Kind, file.Extension, file.Bytes, file.LastWriteUtc, sha256));
            mediaBytes = AddChecked(mediaBytes, file.Bytes);
        }

        var database = new FileInfo(_paths.LiveDatabase);
        var schema = ReadSchemaLevel(_paths.LiveDatabase);
        return new SelfHostedRecoveryCapture(jobId, operationId, previousDestinationRevision, counts,
            captureStartedAtUtc, database.Length, schema.Version, schema.Count, mediaBytes, pins);
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

        // Writers are drained: the evidence built here is the final description
        // of the bytes that the next rename retains.
        var evidence = await BuildRetainedMediaEvidenceAsync(capture, ct);
        if (!File.Exists(_paths.LiveDatabase)) throw Flaw("The live database is missing.");
        var databaseBytes = new FileInfo(_paths.LiveDatabase).Length;
        var databaseSha256 = await HashStableFileAsync(_paths.LiveDatabase, ct);
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
            evidence.MediaBytes,
            databaseSha256,
            evidence.Media,
            DatabaseSchemaVersion: schema.Version,
            DatabaseMigrationCount: schema.Count,
            MediaRehashedCount: evidence.RehashedCount);
        _manifests.Write(manifest);
        return manifest;
    }

    public async Task<SelfHostedRecoveryManifest> RetainMediaAsync(
        Guid jobId,
        IAsyncDisposable lease,
        CancellationToken ct)
    {
        RequireLease(lease);
        var manifest = RequireCreating(jobId);
        var journal = RequireJournal(jobId, manifest);
        var live = _paths.LiveMedia;
        var previous = _paths.PreviousMedia(jobId);
        _paths.VerifyMediaPath(live);
        _paths.VerifyMediaPath(previous);

        if (File.Exists(live) || File.Exists(previous))
            throw Flaw("A media path exists as a file where a directory belongs.");
        var source = Directory.Exists(live);
        var destination = Directory.Exists(previous);
        if (source && destination) throw Flaw("Both the live and the retained media roots exist.");
        if (!source && !destination) throw Flaw("Neither the live nor the retained media root exists.");

        if (source)
        {
            RequireJournalPhase(journal, SelfHostedActivationPhase.CutoverPrepared);
            ActivationFileSystem.Rename(live, previous);
        }
        else
        {
            RequireJournalPhaseAtLeast(journal, SelfHostedActivationPhase.CutoverPrepared);
            VerifyRetainedMediaMatchesManifest(jobId, manifest);
        }

        return Update(jobId, manifest with { MediaRetained = true });
    }

    public async Task<SelfHostedRecoveryManifest> RetainDatabaseAsync(
        Guid jobId,
        IAsyncDisposable lease,
        CancellationToken ct)
    {
        RequireLease(lease);
        var manifest = RequireCreating(jobId);
        if (!manifest.MediaRetained) throw Flaw("Media must be retained before the database.");
        var journal = RequireJournal(jobId, manifest);
        var live = _paths.LiveDatabase;
        var previous = _paths.PreviousDatabase(jobId);
        _paths.VerifyDatabasePath(live);
        _paths.VerifyDatabasePath(previous);

        var source = File.Exists(live);
        var destination = File.Exists(previous);
        if (source && destination) throw Flaw("Both the live and the retained databases exist.");
        if (!source && !destination) throw Flaw("Neither the live nor the retained database exists.");

        if (source)
        {
            RequireJournalPhase(journal, SelfHostedActivationPhase.PreviousMediaRetained);
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
        }
        else
        {
            RequireJournalPhaseAtLeast(journal, SelfHostedActivationPhase.PreviousMediaRetained);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = previous + suffix;
                if (!File.Exists(sidecar)) continue;
                if (string.Equals(suffix, "-wal", StringComparison.Ordinal) && new FileInfo(sidecar).Length > 0)
                    throw Flaw("The retained database has an uncheckpointed WAL.");
                File.Delete(sidecar);
            }

            var retained = new FileInfo(previous);
            if (retained.Length != manifest.DatabaseBytes
                || !string.Equals(await HashStableFileAsync(previous, ct), manifest.DatabaseSha256, StringComparison.Ordinal))
                throw Flaw("The retained database does not match the recovery manifest.");
        }

        return Update(jobId, manifest with { DatabaseRetained = true });
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
            manifest = await SettleTopUpAsync(manifest, transferReservationId, ct);
            manifest = await EnsureRetentionReservationAsync(manifest, ct);
            return manifest;
        }

        if (manifest.Status != MigrationRecoveryStatus.Creating
            || !manifest.MediaRetained
            || !manifest.DatabaseRetained)
            throw Flaw("The retained copy is not complete yet.");

        manifest = await SettleTopUpAsync(manifest, transferReservationId, ct);
        manifest = await EnsureRetentionReservationAsync(manifest, ct);
        FinalizeStepForTesting?.Invoke(SelfHostedRecoveryFinalizeStep.BeforeAvailable);
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

    /// <summary>
    /// Records the one-time absolute offset target for this job's transfer
    /// reservation before mutating it. The target is durable in the manifest,
    /// so any retry re-applies the same absolute value (a no-op once reached)
    /// instead of adding an increment again.
    /// </summary>
    private async Task<SelfHostedRecoveryManifest> SettleTopUpAsync(
        SelfHostedRecoveryManifest manifest,
        Guid? transferReservationId,
        CancellationToken ct)
    {
        if (!manifest.TransferTopUpSettled)
        {
            var retainedBytes = TotalRetainedBytes(manifest);
            if (transferReservationId is not { } transferId || transferId == Guid.Empty || retainedBytes <= 0)
            {
                manifest = Update(manifest.JobId, manifest with { TransferTopUpSettled = true });
            }
            else
            {
                var row = await _db.MigrationStorageReservations
                    .AsNoTracking()
                    .Where(r => r.Id == transferId)
                    .Select(r => new { r.ClaimedJobId, r.ReleasedAtUtc, r.ReservedBytes, r.MaterializedBytes })
                    .FirstOrDefaultAsync(ct);
                if (row is null || row.ReleasedAtUtc is not null || row.ClaimedJobId != manifest.JobId)
                {
                    // Not this job's live transfer claim: nothing may be offset.
                    manifest = Update(manifest.JobId, manifest with { TransferTopUpSettled = true });
                }
                else
                {
                    long target;
                    try
                    {
                        target = Math.Min(row.ReservedBytes, checked(row.MaterializedBytes + retainedBytes));
                    }
                    catch (OverflowException)
                    {
                        throw SelfHostedActivationCapacity.Exhausted();
                    }

                    manifest = Update(manifest.JobId, manifest with
                    {
                        TransferTopUpSettled = true,
                        TransferTopUpReservationId = transferId,
                        TransferTopUpTargetBytes = target,
                    });
                }
            }
        }

        if (manifest.TransferTopUpReservationId is { } reservationId
            && reservationId != Guid.Empty
            && manifest.TransferTopUpTargetBytes > 0)
        {
            FinalizeStepForTesting?.Invoke(SelfHostedRecoveryFinalizeStep.BeforeTopUp);
            try
            {
                await _capacity.EnsureMaterializedAtLeastAsync(reservationId, manifest.TransferTopUpTargetBytes, ct);
            }
            catch (TransferReservationException exception)
                when (exception.Kind is TransferReservationConflictKind.NotFound
                    or TransferReservationConflictKind.Released
                    or TransferReservationConflictKind.Expired)
            {
                // The transfer claim no longer exists: there is nothing to offset.
            }

            FinalizeStepForTesting?.Invoke(SelfHostedRecoveryFinalizeStep.AfterTopUp);
        }

        return manifest;
    }

    /// <summary>
    /// Finds or creates the job's claimed, non-expiring retention reservation.
    /// A retry finds the existing claim deterministically by
    /// <c>(ClaimedJobId, Purpose = RecoveryRetention)</c> or by the id recorded
    /// durably in the manifest before the first claim attempt.
    /// </summary>
    private async Task<SelfHostedRecoveryManifest> EnsureRetentionReservationAsync(
        SelfHostedRecoveryManifest manifest,
        CancellationToken ct)
    {
        var bytes = TotalRetainedBytes(manifest);
        if (bytes <= 0) return manifest with { RetentionReservationId = null };

        var existing = await _db.MigrationStorageReservations
            .AsNoTracking()
            .Where(r => r.ClaimedJobId == manifest.JobId
                && r.Purpose == (int)MigrationSessionPurpose.RecoveryRetention
                && r.ReleasedAtUtc == null)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } claimedId && claimedId != Guid.Empty)
        {
            return manifest.RetentionReservationId == claimedId
                ? manifest
                : Update(manifest.JobId, manifest with { RetentionReservationId = claimedId });
        }

        if (manifest.RetentionReservationId is { } recorded
            && recorded != Guid.Empty
            && await TryClaimAsync(manifest.JobId, recorded, ct))
            return manifest;

        var reserved = await _capacity.TryReserveAsync(
            bytes, MigrationSessionPurpose.RecoveryRetention, _options.PreflightReservationTtl, ct);
        if (!reserved.IsAdmitted || reserved.ReservationId is not { } reservationId)
            throw SelfHostedActivationCapacity.Exhausted();

        // Record the claim durably before claiming it, so a crash between the
        // two reuses the reservation instead of orphaning a claimed row.
        manifest = Update(manifest.JobId, manifest with { RetentionReservationId = reservationId });
        FinalizeStepForTesting?.Invoke(SelfHostedRecoveryFinalizeStep.AfterReservationRecorded);
        if (!await TryClaimAsync(manifest.JobId, reservationId, ct))
        {
            await ReleaseAsync(reservationId, ct);
            throw SelfHostedActivationCapacity.Exhausted();
        }

        FinalizeStepForTesting?.Invoke(SelfHostedRecoveryFinalizeStep.AfterClaim);
        return manifest;
    }

    /// <summary>
    /// Rebuilds the retained-media descriptors under the exclusive lease. Only
    /// a file whose path, length and full-precision last-write time are
    /// unchanged and whose last write is strictly before
    /// <c>captureStart - MediaHashSafetyWindow</c> keeps its capture hash;
    /// everything else is re-hashed now. Removed files are dropped and new
    /// files are added, so the resulting set is exactly the retained set.
    /// </summary>
    private async Task<(RecoveryMediaDescriptor[] Media, long MediaBytes, int RehashedCount)> BuildRetainedMediaEvidenceAsync(
        SelfHostedRecoveryCapture capture,
        CancellationToken ct)
    {
        var pinned = capture.Media.ToDictionary(pin => pin.RelativePath, StringComparer.Ordinal);
        var threshold = capture.CaptureStartedAtUtc - MediaHashSafetyWindow;
        var files = EnumerateLiveMediaFiles();
        var descriptors = new List<RecoveryMediaDescriptor>(files.Count);
        var rehashed = 0;
        long mediaBytes = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string sha256;
            if (pinned.TryGetValue(file.RelativePath, out var pin)
                && pin.Bytes == file.Bytes
                && pin.LastWriteUtc == file.LastWriteUtc
                && file.LastWriteUtc < threshold)
            {
                sha256 = pin.Sha256;
            }
            else
            {
                sha256 = await HashStableFileAsync(file.Path, ct);
                rehashed++;
            }

            descriptors.Add(new RecoveryMediaDescriptor(file.BookId, file.Kind, file.Extension, file.Bytes, sha256));
            mediaBytes = AddChecked(mediaBytes, file.Bytes);
        }

        return (descriptors.ToArray(), mediaBytes, rehashed);
    }

    /// <summary>
    /// Structural enumeration of the live media root. Fails closed on
    /// unexpected entries so the retained set is exactly describable.
    /// </summary>
    private List<LiveMediaFile> EnumerateLiveMediaFiles()
    {
        var root = _paths.LiveMedia;
        if (!Directory.Exists(root)) throw Flaw("The live media root is missing.");
        _paths.VerifyMediaPath(root);
        var files = new List<LiveMediaFile>();
        foreach (var directory in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            _paths.VerifyMediaPath(directory);
            if (File.Exists(directory)) throw Flaw("The media root contains an unexpected file.");
            if (!Directory.Exists(directory)) throw Flaw("The media root contains an unexpected entry.");
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out var bookId) || bookId == Guid.Empty || name != bookId.ToString("N"))
                throw Flaw("The media root contains an unexpected directory.");

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                _paths.VerifyMediaPath(entry);
                if (Directory.Exists(entry)) throw Flaw("A book folder contains an unexpected directory.");
                if (!File.Exists(entry)) throw Flaw("The media root contains an unexpected entry.");
                var info = new FileInfo(entry);
                var (kind, extension) = ClassifyMediaFile(Path.GetFileName(entry));
                files.Add(new LiveMediaFile(
                    entry, Path.GetRelativePath(root, entry), bookId, kind, extension, info.Length, info.LastWriteTimeUtc));
            }
        }

        return files;
    }

    private void VerifyRetainedMediaMatchesManifest(Guid jobId, SelfHostedRecoveryManifest manifest)
    {
        var root = _paths.PreviousMedia(jobId);
        _paths.VerifyMediaPath(root);
        var retained = new List<(Guid BookId, string Kind, string Extension, long Bytes)>();
        foreach (var directory in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            _paths.VerifyMediaPath(directory);
            if (!Directory.Exists(directory)) continue;
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out var bookId)) continue;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                _paths.VerifyMediaPath(entry);
                if (!File.Exists(entry)) continue;
                var (kind, extension) = ClassifyMediaFile(Path.GetFileName(entry));
                retained.Add((bookId, kind, extension, new FileInfo(entry).Length));
            }
        }

        var expected = manifest.Media
            .Select(descriptor => (descriptor.BookId, descriptor.Kind, descriptor.Extension, descriptor.Bytes))
            .OrderBy(item => item.BookId).ThenBy(item => item.Kind)
            .ThenBy(item => item.Extension).ThenBy(item => item.Bytes)
            .ToArray();
        var actual = retained
            .OrderBy(item => item.BookId).ThenBy(item => item.Kind)
            .ThenBy(item => item.Extension).ThenBy(item => item.Bytes)
            .ToArray();
        if (!expected.SequenceEqual(actual))
            throw Flaw("The retained media directory does not match the recovery manifest.");
    }

    private SelfHostedActivationJournal RequireJournal(Guid jobId, SelfHostedRecoveryManifest manifest)
    {
        var journal = _journals.Read(jobId) ?? throw Flaw("An activation journal is required for recovery retention.");
        if (journal.OperationId != manifest.OperationId
            || journal.DestinationRevision != manifest.PreviousDestinationRevision
            || !journal.RetainPreviousLibrary)
            throw Flaw("The recovery manifest does not match the activation journal.");
        return journal;
    }

    private static void RequireJournalPhase(SelfHostedActivationJournal journal, SelfHostedActivationPhase phase)
    {
        if (journal.Phase != phase)
            throw Flaw($"Recovery retention requires journal phase {phase}.");
    }

    private static void RequireJournalPhaseAtLeast(SelfHostedActivationJournal journal, SelfHostedActivationPhase phase)
    {
        if (journal.Phase < phase || journal.Phase > SelfHostedActivationPhase.Committed)
            throw Flaw("The activation journal is not in the retention window for this recovery step.");
    }

    /// <summary>
    /// Hashes a file and proves it did not change while being read (length and
    /// last-write before and after must match). Called only under the exclusive
    /// lease for live-library files, and for the retained database when
    /// verifying an already-completed rename.
    /// </summary>
    private async Task<string> HashStableFileAsync(string path, CancellationToken ct)
    {
        HashingForTesting?.Invoke(path);
        var before = new FileInfo(path);
        var length = before.Length;
        var lastWrite = before.LastWriteTimeUtc;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        var digest = await sha256.ComputeHashAsync(stream, ct);
        var after = new FileInfo(path);
        if (after.Length != length || after.LastWriteTimeUtc != lastWrite)
            throw Conflict("A library file changed while it was being hashed for recovery.");
        return Convert.ToHexString(digest).ToLowerInvariant();
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

    private static long TotalRetainedBytes(SelfHostedRecoveryManifest manifest)
    {
        try
        {
            return checked(manifest.DatabaseBytes + manifest.MediaBytes);
        }
        catch (OverflowException)
        {
            throw SelfHostedActivationCapacity.Exhausted();
        }
    }

    private static long AddChecked(long total, long bytes)
    {
        try
        {
            return checked(total + bytes);
        }
        catch (OverflowException)
        {
            throw SelfHostedActivationCapacity.Exhausted();
        }
    }

    private static MigrationRecoveryStatusResponse ToResponse(SelfHostedRecoveryManifest manifest) =>
        new(manifest.JobId, manifest.Status, manifest.CreatedAtUtc, manifest.ExpiresAtUtc,
            manifest.TotalBytes, manifest.Counts);

    private static MigrationActivationException Flaw(string message) =>
        SelfHostedActivationPaths.Failure(message);

    private static MigrationActivationException Conflict(string message) =>
        new(MigrationActivationErrorCodes.DestinationConflict, message);

    private sealed record LiveMediaFile(
        string Path,
        string RelativePath,
        Guid BookId,
        string Kind,
        string Extension,
        long Bytes,
        DateTime LastWriteUtc);
}
