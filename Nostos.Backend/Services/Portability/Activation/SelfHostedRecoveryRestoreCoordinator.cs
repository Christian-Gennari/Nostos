using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>Observable outcome of one recovery-restore attempt. Never carries paths.</summary>
internal enum SelfHostedRecoveryRestoreOutcome
{
    /// <summary>This call restored the previous library (or finished doing so).</summary>
    Restored,

    /// <summary>The copy was already restored; the request is an idempotent success.</summary>
    AlreadyRestored,

    /// <summary>The restore was interrupted but the pre-restore library is intact; retry.</summary>
    Interrupted,

    /// <summary>Another operation owns the library; the pending restore is retried later.</summary>
    Busy,
}

internal sealed record SelfHostedRecoveryRestoreResult(
    Guid RecoveryId,
    SelfHostedRecoveryRestoreOutcome Outcome,
    MigrationRecoveryStatus Status,
    string? ErrorCode);

/// <summary>
/// Test seam for the restore crash matrix: thrown at a named boundary to model
/// abrupt process loss. The coordinator rethrows it without rolling back,
/// releasing or cleaning anything, exactly like a host that died there.
/// </summary>
internal sealed class SelfHostedRecoveryRestoreAbandonedException : Exception;

/// <summary>Names of the durable phase writes and step boundaries tests crash at.</summary>
internal static class SelfHostedRecoveryRestoreSteps
{
    public const string PhaseCandidatePrepared = "restore:phase:CandidatePrepared";
    public const string AfterCapture = "restore:after:capture";
    public const string AfterAdmission = "restore:after:admission";
    public const string AfterVerifySourceDatabase = "restore:after:verify-source-db";
    public const string AfterBuildMedia = "restore:after:build-media";
    public const string AfterExtractPayload = "restore:after:extract-payload";
    public const string AfterBuildDatabase = "restore:after:build-database";
    public const string AfterVerifyCandidate = "restore:after:verify-candidate";
    public const string PhaseExclusiveEntered = "restore:phase:ExclusiveEntered";
    public const string AfterFinalize = "restore:after:finalize";
    public const string AfterQuiesce = "restore:after:quiesce";
    public const string PhaseDatabaseCheckpointed = "restore:phase:DatabaseCheckpointed";
    public const string AfterPrepareRetention = "restore:after:prepare-retention";
    public const string PhaseCutoverPrepared = "restore:phase:CutoverPrepared";
    public const string AfterRetainMedia = "restore:after:retain-media";
    public const string PhasePreviousMediaRetained = "restore:phase:PreviousMediaRetained";
    public const string AfterRetainDatabase = "restore:after:retain-db";
    public const string PhasePreviousDatabaseRetained = "restore:phase:PreviousDatabaseRetained";
    public const string AfterActivateMedia = "restore:after:activate-media";
    public const string PhaseCandidateMediaActivated = "restore:phase:CandidateMediaActivated";
    public const string AfterActivateDatabase = "restore:after:activate-db";
    public const string PhaseCandidateDatabaseActivated = "restore:phase:CandidateDatabaseActivated";
    public const string AfterReopen = "restore:after:reopen";
    public const string AfterPostVerify = "restore:after:post-verify";
    public const string PhasePostActivationVerified = "restore:phase:PostActivationVerified";
    public const string PhaseCommitted = "restore:phase:Committed";
    public const string AfterFinalizeReplacedRetention = "restore:after:finalize-replaced-retention";
    public const string AfterMarkSourceRestored = "restore:after:mark-source-restored";
    public const string AfterFinalizeRestore = "restore:after:finalize-restore";
}

/// <summary>
/// Customer-facing "Restore previous library" (issue #681, Slice 9). The
/// retained recovery copy is the source of previous portable state only: the
/// CURRENT live database is retained as a fresh recovery copy (identified by a
/// new operation id), and the candidate database is a fresh current-schema
/// build from the verified previous portable payload plus the CURRENT host
/// operational state — week-old host state is never resurrected.
///
/// <para><b>Protocol.</b> The recovery gate always runs outside requests: a
/// durably claimed <c>Restoring</c> manifest plus a per-copy gate gives
/// exactly-once admission; the pending work is resumed on startup. Phase A
/// (outside maintenance) verifies the retained database hash, copies and
/// hash-verifies every retained media file into the candidate root, extracts
/// the previous portable payload from a migrated working copy, builds and
/// verifies the candidate database. Phase B uses the SAME durable journal,
/// state machine, component recovery steps and SQLite lifecycle as activation:
/// every live rename is preceded by a durable journal phase, every step is
/// idempotent, and a crash at any boundary converges through the startup
/// reconciler on either the pre-restore library or the fully restored one.
/// Rollback failure keeps the host fail-closed until restart.</para>
/// </summary>
internal sealed class SelfHostedRecoveryRestoreCoordinator
{
    private readonly SelfHostedActivationPaths _paths;
    private readonly LibraryMaintenanceCoordinator _maintenance;
    private readonly SelfHostedActivationJournalStore _journals;
    private readonly SelfHostedRecoveryManifestStore _manifests;
    private readonly IServiceScopeFactory _scopes;
    private readonly ISelfHostedSqliteLifecycle _sqlite;
    private readonly IPortableLibraryVerifier _verifier;
    private readonly ISelfHostedRecoverySchemaMigrator _migrator;
    private readonly IReadOnlyList<ISelfHostedActivationRecoveryStep> _recoverySteps;
    private readonly TransferPathResolver _transferPaths;
    private readonly ILibraryDestinationRevisionProvider _revisionProvider;
    private readonly ILogger<SelfHostedRecoveryRestoreCoordinator> _logger;
    private readonly TimeProvider _clock;

    /// <summary>Test seam: invoked after each named boundary; throwing models a crash.</summary>
    internal Action<string>? StepObserverForTesting { get; set; }

    public SelfHostedRecoveryRestoreCoordinator(
        SelfHostedActivationPaths paths,
        LibraryMaintenanceCoordinator maintenance,
        SelfHostedActivationJournalStore journals,
        SelfHostedRecoveryManifestStore manifests,
        IServiceScopeFactory scopes,
        ISelfHostedSqliteLifecycle sqlite,
        IPortableLibraryVerifier verifier,
        ISelfHostedRecoverySchemaMigrator migrator,
        IEnumerable<ISelfHostedActivationRecoveryStep> recoverySteps,
        TransferPathResolver transferPaths,
        ILibraryDestinationRevisionProvider revisionProvider,
        ILogger<SelfHostedRecoveryRestoreCoordinator> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(journals);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(sqlite);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(migrator);
        ArgumentNullException.ThrowIfNull(recoverySteps);
        ArgumentNullException.ThrowIfNull(transferPaths);
        ArgumentNullException.ThrowIfNull(revisionProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _maintenance = maintenance;
        _journals = journals;
        _manifests = manifests;
        _scopes = scopes;
        _sqlite = sqlite;
        _verifier = verifier;
        _migrator = migrator;
        _recoverySteps = recoverySteps.OrderBy(step => step.Order).ToArray();
        _transferPaths = transferPaths;
        _revisionProvider = revisionProvider;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Request-time admission (runs with the caller holding a shared operation
    /// lease): validates the copy, the confirmation bound to the CURRENT
    /// destination revision, and capacity, then durably claims the copy as
    /// <c>Restoring</c>. The caller starts the runner; nothing is processed on
    /// the request thread.
    /// </summary>
    internal async Task<MigrationRecoveryStatusResponse> RequestRestoreAsync(
        Guid recoveryId,
        MigrationRecoveryRestoreRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (recoveryId == Guid.Empty) throw new ArgumentException("A recovery identifier is required.", nameof(recoveryId));

        // The endpoint route owns its admission; this read/claim must sit inside
        // a shared lease so it can never straddle an exclusive window.
        using var operation = _maintenance.TryEnterOperation()
            ?? throw LibraryMaintenanceCoordinator.Busy();

        var manifest = RequireAvailable(recoveryId);
        var currentRevision = await ReadCurrentRevisionAsync(ct);
        MigrationActivationAdmission.ValidateRecoveryRestore(request, currentRevision);
        await EnsureAdmittedAsync(manifest, manifest.MediaBytes, ct);

        var restoreId = Guid.NewGuid();
        var claimed = manifest with
        {
            Status = MigrationRecoveryStatus.Restoring,
            RestoreOperationId = restoreId,
            RestoreDestinationRevision = request.DestinationRevision,
            RestoreError = null,
            RestoredAtUtc = null,
        };
        // Conditional on the exact manifest just validated: if the cleanup
        // sweep decided to prune this expired copy in between, or any other
        // writer changed it, the claim loses and the copy is not touched.
        if (_manifests.TryWriteIfUnchanged(recoveryId, manifest, claimed) is null)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryRestoreConflict,
                "This recovery copy is no longer available for restore.");
        }

        return ToResponse(claimed);
    }

    /// <summary>
    /// Runs (or resumes) the claimed restore. The per-copy gate and the durable
    /// <c>Restoring</c> claim make this exactly-once; a caller that loses the
    /// race observes the current state instead of starting a second switch.
    /// </summary>
    internal async Task<SelfHostedRecoveryRestoreResult> RestorePreviousLibraryAsync(
        Guid recoveryId,
        CancellationToken ct)
    {
        if (recoveryId == Guid.Empty) throw new ArgumentException("A recovery identifier is required.", nameof(recoveryId));

        var manifest = ReadManifest(recoveryId);
        if (manifest.Status == MigrationRecoveryStatus.Restored)
        {
            return new SelfHostedRecoveryRestoreResult(recoveryId, SelfHostedRecoveryRestoreOutcome.AlreadyRestored,
                MigrationRecoveryStatus.Restored, null);
        }

        if (manifest.Status != MigrationRecoveryStatus.Restoring
            || manifest.RestoreOperationId is not { } restoreId
            || restoreId == Guid.Empty)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryRestoreConflict,
                "This recovery copy has no restore in progress.");
        }

        try
        {
            var resolved = _journals.ReadResolved(restoreId);
            if (resolved is { Phase: SelfHostedActivationPhase.Committed })
            {
                // The durable commit happened before the process died; the
                // restored library is already live and only bookkeeping remains.
                return await CompleteCommittedAsync(recoveryId, manifest, restoreId);
            }

            // Any other resolved state is a rolled-back attempt whose live
            // mutation was undone by the startup reconciler: the pre-restore
            // library is intact, so the restore is retried from scratch.
            return await RunCutoverAsync(recoveryId, manifest, restoreId, ct);
        }
        catch (SelfHostedRecoveryRestoreAbandonedException)
        {
            // The crash-matrix sentinel models process death and must propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new SelfHostedRecoveryRestoreResult(recoveryId, SelfHostedRecoveryRestoreOutcome.Interrupted,
                CurrentStatus(recoveryId), ErrorCode(exception));
        }
    }

    private MigrationRecoveryStatus CurrentStatus(Guid recoveryId)
    {
        try
        {
            return _manifests.Read(recoveryId)?.Status ?? MigrationRecoveryStatus.Restoring;
        }
        catch (MigrationActivationException)
        {
            return MigrationRecoveryStatus.Restoring;
        }
    }

    private async Task<SelfHostedRecoveryRestoreResult> CompleteCommittedAsync(
        Guid recoveryId,
        SelfHostedRecoveryManifest manifest,
        Guid restoreId)
    {
        IAsyncDisposable exclusive;
        try
        {
            exclusive = await _maintenance.EnterExclusiveAsync(
                LibraryMaintenanceReason.RecoveryRestore, CancellationToken.None);
        }
        catch (MigrationActivationException exception) when (exception.Code == MigrationActivationErrorCodes.Busy)
        {
            return new SelfHostedRecoveryRestoreResult(recoveryId, SelfHostedRecoveryRestoreOutcome.Busy,
                MigrationRecoveryStatus.Restoring, exception.Code);
        }

        try
        {
            await FinalizeCommittedAsync(manifest, restoreId, exclusive);
        }
        finally
        {
            await exclusive.DisposeAsync();
        }

        return new SelfHostedRecoveryRestoreResult(recoveryId, SelfHostedRecoveryRestoreOutcome.Restored,
            MigrationRecoveryStatus.Restored, null);
    }

    private async Task<SelfHostedRecoveryRestoreResult> RunCutoverAsync(
        Guid recoveryId,
        SelfHostedRecoveryManifest manifest,
        Guid restoreId,
        CancellationToken ct)
    {
        var storedRevision = manifest.RestoreDestinationRevision;
        if (string.IsNullOrWhiteSpace(storedRevision))
        {
            throw Corrupt("The restore does not record the confirmed destination revision.");
        }

        IAsyncDisposable? exclusive = null;
        var exclusiveEntered = false;
        var committed = false;
        var journalPrepared = false;
        var operationId = Guid.NewGuid();
        try
        {
            // ---- Phase A: long, outside maintenance, no live mutation. ----
            _journals.PrepareForRetry(restoreId);
            _journals.Write(new SelfHostedActivationJournal(restoreId, operationId,
                SelfHostedActivationPhase.CandidatePrepared, storedRevision, RetainPreviousLibrary: true,
                _clock.GetUtcNow()));
            journalPrepared = true;
            Step(SelfHostedRecoveryRestoreSteps.PhaseCandidatePrepared);

            var facts = await ReadDestinationFactsAsync(ct);
            if (!string.Equals(facts.Revision, storedRevision, StringComparison.Ordinal))
            {
                throw new MigrationActivationException(MigrationActivationErrorCodes.DestinationConflict,
                    "The destination changed. Review the restore again.");
            }

            var capture = await CaptureAsync(restoreId, operationId, storedRevision, facts.Counts, ct);
            Step(SelfHostedRecoveryRestoreSteps.AfterCapture);

            await EnsureAdmittedAsync(manifest, manifest.MediaBytes, ct);
            Step(SelfHostedRecoveryRestoreSteps.AfterAdmission);

            var source = await PrepareSourceAsync(recoveryId, restoreId, manifest, ct);

            // ---- Phase B: exclusive maintenance, committed or rolled back. ----
            exclusive = await _maintenance.EnterExclusiveAsync(
                LibraryMaintenanceReason.RecoveryRestore, ct);
            exclusiveEntered = true;

            // The authoritative revision recheck closes the race once admission
            // is closed and every reader/writer has drained.
            var currentRevision = await ReadCurrentRevisionAsync(CancellationToken.None);
            MigrationActivationAdmission.ValidateRecoveryRestore(
                new MigrationRecoveryRestoreRequest(storedRevision, ConfirmReplacement: true),
                currentRevision);

            _journals.Advance(restoreId, SelfHostedActivationPhase.ExclusiveEntered, exclusive);
            Step(SelfHostedRecoveryRestoreSteps.PhaseExclusiveEntered);

            // Last live database mutation is the finalized candidate snapshot
            // plus the revision bump. Everything after quiesce is path movement.
            await using (var scope = _scopes.CreateAsyncScope())
            {
                var builder = (SelfHostedActivationDatabaseBuilder)scope.ServiceProvider
                    .GetRequiredService<ISelfHostedActivationDatabaseBuilder>();
                await builder.FinalizeCandidateAsync(restoreId, exclusive, CancellationToken.None);
                if (!builder.IsFinalized(restoreId))
                {
                    throw Failed("The restore candidate was not finalized.");
                }
            }

            Step(SelfHostedRecoveryRestoreSteps.AfterFinalize);

            // A crash-terminated attempt may have left an unfinished retention
            // plan bound to a previous attempt; clear it before quiescence so no
            // database access is needed after the checkpoint boundary.
            await using (var scope = _scopes.CreateAsyncScope())
            {
                if (_manifests.Read(restoreId) is { Status: MigrationRecoveryStatus.Creating })
                {
                    await scope.ServiceProvider.GetRequiredService<SelfHostedMigrationRecoveryService>()
                        .AbandonAsync(restoreId, exclusive, CancellationToken.None);
                }
            }

            _sqlite.QuiesceLive(exclusive);
            Step(SelfHostedRecoveryRestoreSteps.AfterQuiesce);
            _journals.Advance(restoreId, SelfHostedActivationPhase.DatabaseCheckpointed, exclusive);
            Step(SelfHostedRecoveryRestoreSteps.PhaseDatabaseCheckpointed);

            await using (var scope = _scopes.CreateAsyncScope())
            {
                var recovery = scope.ServiceProvider.GetRequiredService<SelfHostedMigrationRecoveryService>();
                // Retain the CURRENT library as the new recovery copy: the same
                // manifest/rename machinery that activation uses for the
                // previous library. The operation id doubles as the new copy's
                // recovery id, so the restore itself is reversible.
                await recovery.PrepareRetentionAsync(restoreId, capture, exclusive, CancellationToken.None);
                Step(SelfHostedRecoveryRestoreSteps.AfterPrepareRetention);

                _journals.Advance(restoreId, SelfHostedActivationPhase.CutoverPrepared, exclusive);
                Step(SelfHostedRecoveryRestoreSteps.PhaseCutoverPrepared);

                await recovery.RetainMediaAsync(restoreId, exclusive, CancellationToken.None);
                Step(SelfHostedRecoveryRestoreSteps.AfterRetainMedia);
                _journals.Advance(restoreId, SelfHostedActivationPhase.PreviousMediaRetained, exclusive);
                Step(SelfHostedRecoveryRestoreSteps.PhasePreviousMediaRetained);

                await recovery.RetainDatabaseAsync(restoreId, exclusive, CancellationToken.None);
                Step(SelfHostedRecoveryRestoreSteps.AfterRetainDatabase);
                _journals.Advance(restoreId, SelfHostedActivationPhase.PreviousDatabaseRetained, exclusive);
                Step(SelfHostedRecoveryRestoreSteps.PhasePreviousDatabaseRetained);
            }

            ActivationFileSystem.Rename(_paths.CandidateMedia(restoreId), _paths.LiveMedia);
            Step(SelfHostedRecoveryRestoreSteps.AfterActivateMedia);
            _journals.Advance(restoreId, SelfHostedActivationPhase.CandidateMediaActivated, exclusive);
            Step(SelfHostedRecoveryRestoreSteps.PhaseCandidateMediaActivated);

            SelfHostedActivationComponentStep.RenameDatabase(
                _paths.CandidateDatabase(restoreId), _paths.LiveDatabase);
            Step(SelfHostedRecoveryRestoreSteps.AfterActivateDatabase);
            _journals.Advance(restoreId, SelfHostedActivationPhase.CandidateDatabaseActivated, exclusive);
            Step(SelfHostedRecoveryRestoreSteps.PhaseCandidateDatabaseActivated);

            _sqlite.ReopenActivated(exclusive);
            Step(SelfHostedRecoveryRestoreSteps.AfterReopen);

            await PostActivationVerifyAsync(source.Payload, source.Media);
            Step(SelfHostedRecoveryRestoreSteps.AfterPostVerify);

            _journals.Advance(restoreId, SelfHostedActivationPhase.PostActivationVerified, exclusive);
            Step(SelfHostedRecoveryRestoreSteps.PhasePostActivationVerified);
            _journals.Advance(restoreId, SelfHostedActivationPhase.Committed, exclusive);
            Step(SelfHostedRecoveryRestoreSteps.PhaseCommitted);
            committed = true;

            await FinalizeCommittedAsync(manifest, restoreId, exclusive);
            return new SelfHostedRecoveryRestoreResult(recoveryId, SelfHostedRecoveryRestoreOutcome.Restored,
                MigrationRecoveryStatus.Restored, null);
        }
        catch (SelfHostedRecoveryRestoreAbandonedException)
        {
            // A process crash never runs a finally: admission must stay closed
            // until a restart's reconciler decides the generation.
            _maintenance.FailClosedForRecovery();
            throw;
        }
        catch (Exception exception) when (!committed)
        {
            if (exclusiveEntered && exclusive is not null)
            {
                try
                {
                    await RollBackAsync(restoreId, exclusive);
                }
                catch (Exception rollbackFailure)
                {
                    _maintenance.FailClosedForRecovery();
                    TryRecordRestoreError(recoveryId, MigrationActivationErrorCodes.RecoveryFailed);
                    _logger.LogCritical(rollbackFailure,
                        "Recovery restore rollback could not complete; the library stays in maintenance until "
                        + "startup reconciliation. Follow the activation recovery guide.");
                    throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryFailed,
                        "The restore failed and the current library could not be restored in-process. "
                        + "The host stays in maintenance; restart to reconcile, following the activation recovery guide.");
                }

                await TryAbandonRetentionPlanAsync(restoreId, exclusive);
                TryClearRestoreJournal(restoreId);
                CleanupWorkingFiles(_paths.RestoreSourceDatabase(restoreId), _paths.RestorePayload(restoreId));
                await TryMarkRetryableAsync(recoveryId, ErrorCode(exception));
            }
            else if (exception is MigrationActivationException { Code: MigrationActivationErrorCodes.Busy })
            {
                // Another operation owns the library; this request stays
                // pending and the runner retries it on the next scan.
            }
            else if (!journalPrepared)
            {
                // The journal could not even be prepared: the durable state is
                // inconsistent and must be inspected rather than retried.
                TryMarkTerminal(recoveryId, ErrorCode(exception));
            }
            else
            {
                // No live path could have changed yet: reset the copy for a
                // later attempt (or mark tamper/corruption terminal).
                TryClearRestoreJournal(restoreId);
                CleanupWorkingFiles(_paths.RestoreSourceDatabase(restoreId), _paths.RestorePayload(restoreId));
                await TryMarkFailedOrRetryableAsync(recoveryId, exception);
            }

            throw WrapRestoreFailure(exception);
        }
        finally
        {
            if (exclusive is not null) await exclusive.DisposeAsync();
        }
    }

    /// <summary>
    /// Phase A: verify the retained database hash; copy and hash-verify every
    /// retained media file into the candidate root; migrate a working copy of
    /// the retained database forward if its schema is older; extract the
    /// previous portable payload; build and verify the candidate database. No
    /// live path is touched and the retained original is never modified.
    /// </summary>
    private async Task<SelfHostedRecoveryRestoreSource> PrepareSourceAsync(
        Guid recoveryId,
        Guid restoreId,
        SelfHostedRecoveryManifest manifest,
        CancellationToken ct)
    {
        await VerifySourceDatabaseAsync(recoveryId, manifest, ct);
        Step(SelfHostedRecoveryRestoreSteps.AfterVerifySourceDatabase);

        var media = await BuildCandidateMediaAsync(recoveryId, restoreId, manifest, ct);
        Step(SelfHostedRecoveryRestoreSteps.AfterBuildMedia);

        var payload = await ExtractPayloadAsync(recoveryId, restoreId, media.PrimaryMedia, manifest.Counts, ct);
        Step(SelfHostedRecoveryRestoreSteps.AfterExtractPayload);

        await BuildCandidateDatabaseAsync(restoreId, payload, ct);
        Step(SelfHostedRecoveryRestoreSteps.AfterBuildDatabase);

        await VerifyCandidateDatabaseAsync(restoreId, payload, ct);
        Step(SelfHostedRecoveryRestoreSteps.AfterVerifyCandidate);
        TryDelete(_paths.RestoreSourceDatabase(restoreId));
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            TryDelete(_paths.RestoreSourceDatabase(restoreId) + suffix);
        }

        TryDelete(_paths.RestorePayload(restoreId));
        return new SelfHostedRecoveryRestoreSource(payload, media.Files);
    }

    private async Task VerifySourceDatabaseAsync(
        Guid recoveryId,
        SelfHostedRecoveryManifest manifest,
        CancellationToken ct)
    {
        var path = _paths.PreviousDatabase(recoveryId);
        _paths.VerifyDatabasePath(path);
        if (!File.Exists(path)) throw Corrupt("The retained recovery database is missing.");

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = path + suffix;
            if (File.Exists(sidecar) && new FileInfo(sidecar).Length > 0)
            {
                throw Corrupt("The retained recovery database has an unexpected journal file.");
            }
        }

        var info = new FileInfo(path);
        if (info.Length != manifest.DatabaseBytes)
        {
            throw Corrupt("The retained recovery database length does not match its manifest.");
        }

        if (!string.Equals(await HashFileAsync(path, ct), manifest.DatabaseSha256, StringComparison.Ordinal))
        {
            throw Corrupt("The retained recovery database failed SHA-256 verification against its manifest.");
        }
    }

    private async Task<(IReadOnlyList<RetainedMediaFile> Files, IReadOnlyList<PortableRecoveryMedia> PrimaryMedia, long MediaBytes)>
        BuildCandidateMediaAsync(
            Guid recoveryId,
            Guid restoreId,
            SelfHostedRecoveryManifest manifest,
            CancellationToken ct)
    {
        var source = _paths.PreviousMedia(recoveryId);
        var target = _paths.CandidateMedia(restoreId);
        _paths.VerifyMediaPath(source);
        _paths.VerifyMediaPath(target);
        if (!Directory.Exists(source)) throw Corrupt("The retained recovery media root is missing.");

        if (File.Exists(target)) Directory.Delete(target);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        _paths.VerifyMediaPath(target);

        var files = new List<RetainedMediaFile>();
        var rootOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };

        foreach (var directory in Directory.EnumerateFileSystemEntries(source, "*", rootOptions)
                     .Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            _paths.VerifyMediaPath(directory);
            if (!Directory.Exists(directory))
            {
                throw Corrupt("The retained recovery media root contains an unexpected file.");
            }

            var folderName = Path.GetFileName(directory);
            if (!TryReadBookDirectoryName(folderName, out var bookId))
            {
                throw Corrupt("The retained recovery media root contains an unexpected directory.");
            }

            var targetFolder = Path.Combine(target, folderName);
            Directory.CreateDirectory(targetFolder);
            _paths.VerifyMediaPath(targetFolder);

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", rootOptions)
                         .Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                _paths.VerifyMediaPath(entry);
                if (!File.Exists(entry))
                {
                    throw Corrupt("A retained book folder contains an unexpected directory.");
                }

                var fileName = Path.GetFileName(entry);
                var (kind, extension) = ClassifyMediaFile(fileName);
                var targetPath = Path.Combine(targetFolder, fileName);
                _paths.VerifyMediaPath(targetPath);

                var (length, sha256) = await CopyAndHashAsync(entry, targetPath, ct);
                files.Add(new RetainedMediaFile(
                    Path.GetRelativePath(source, entry),
                    new PortableRecoveryMedia(bookId, kind, fileName, length, sha256)));
            }
        }

        var expected = manifest.Media
            .Select(item => (item.BookId, item.Kind, item.Extension, item.Bytes, item.Sha256))
            .OrderBy(item => item.BookId).ThenBy(item => item.Kind)
            .ThenBy(item => item.Extension).ThenBy(item => item.Bytes)
            .ToArray();
        var actual = files
            .Select(item => (item.Descriptor.BookId, item.Descriptor.Kind,
                Extension: Path.GetExtension(item.Descriptor.FileName).ToLowerInvariant(),
                item.Descriptor.Length, item.Descriptor.Sha256))
            .OrderBy(item => item.BookId).ThenBy(item => item.Kind)
            .ThenBy(item => item.Extension).ThenBy(item => item.Length)
            .ToArray();
        if (!expected.SequenceEqual(actual))
        {
            throw Corrupt("The retained recovery media does not match its manifest.");
        }

        try
        {
            return (files, files.Select(item => item.Descriptor).ToArray(),
                checked(files.Sum(item => item.Descriptor.Length)));
        }
        catch (OverflowException)
        {
            throw Corrupt("The retained recovery media size is invalid.");
        }
    }

    /// <summary>
    /// Extracts the previous portable payload from a migrated working copy.
    /// A forward migration may not redefine what "the retained library" is:
    /// when migrations are pending, the working copy's portable baseline is
    /// captured first and must be provably preserved afterwards (row counts,
    /// column presence, order-independent row digests, declared renames only),
    /// and the extracted payload's counts must match the retention manifest.
    /// Any mismatch is corruption and aborts before any live mutation. A copy
    /// already at the current schema skips the baseline scan entirely.
    /// </summary>
    private async Task<PortableRecoveryPayload> ExtractPayloadAsync(
        Guid recoveryId,
        Guid restoreId,
        IReadOnlyList<PortableRecoveryMedia> primaryMedia,
        MigrationExistingCounts manifestCounts,
        CancellationToken ct)
    {
        var retained = _paths.PreviousDatabase(recoveryId);
        var working = _paths.RestoreSourceDatabase(restoreId);
        var payloadPath = _paths.RestorePayload(restoreId);
        _paths.VerifyDatabasePath(working);
        _paths.VerifyDatabasePath(payloadPath);
        File.Copy(retained, working, overwrite: true);

        try
        {
            PortableRecoveryBaseline? baseline = null;
            await using (var db = CreateDatabaseContext(working))
            {
                var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
                var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
                if (applied.Any(id => !known.Contains(id)))
                {
                    // The retained library was created by a newer schema; this
                    // build cannot upgrade it forward without guessing.
                    throw Corrupt("The retained library uses a schema this version cannot upgrade.");
                }

                if (known.Any(id => !applied.Contains(id)))
                {
                    baseline = await PortableLibraryRecoveryBaseline.CaptureAsync(working, ct);
                }

                ct.ThrowIfCancellationRequested();
                await _migrator.MigrateAsync(db, ct);
            }

            if (baseline is not null)
            {
                await PortableLibraryRecoveryBaseline.RequirePreservedAsync(working, baseline, ct);
            }

            PortableRecoveryPayload payload;
            await using (var db = CreateDatabaseContext(working))
            await using (var output = new FileStream(
                payloadPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                payload = await PortableLibraryRecoverySource
                    .WriteRelationalPayloadAsync(db, output, primaryMedia, ct);
            }

            PortableLibraryRecoveryBaseline.RequireCountsPreserved(manifestCounts, payload.Counts);
            return payload;
        }
        catch (MigrationActivationException)
        {
            CleanupWorkingFiles(working, payloadPath);
            throw;
        }
        catch (OperationCanceledException)
        {
            CleanupWorkingFiles(working, payloadPath);
            throw;
        }
        catch (Exception exception)
        {
            CleanupWorkingFiles(working, payloadPath);
            if (exception is PortableRecoveryBaselineException)
            {
                throw Corrupt(exception.Message);
            }

            // Storage and permission failures are retryable; a payload that
            // cannot be interpreted is corruption and must fail closed.
            if (exception is IOException or UnauthorizedAccessException)
            {
                throw Failed("The retained library could not be read for restore.");
            }

            throw Corrupt("The retained library could not be read for restore.");
        }
    }

    private static void CleanupWorkingFiles(string working, string payloadPath)
    {
        TryDelete(working);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            TryDelete(working + suffix);
        }

        TryDelete(payloadPath);
    }

    private async Task BuildCandidateDatabaseAsync(
        Guid restoreId,
        PortableRecoveryPayload payload,
        CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var builder = (SelfHostedActivationDatabaseBuilder)scope.ServiceProvider
            .GetRequiredService<ISelfHostedActivationDatabaseBuilder>();
        await builder.BuildPortableCandidateFromVerifiedPayloadAsync(
            restoreId, _paths.RestorePayload(restoreId), payload.PrimaryMedia, ct);
    }

    private async Task VerifyCandidateDatabaseAsync(
        Guid restoreId,
        PortableRecoveryPayload payload,
        CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var builder = (SelfHostedActivationDatabaseBuilder)scope.ServiceProvider
            .GetRequiredService<ISelfHostedActivationDatabaseBuilder>();
        using var candidate = builder.OpenCandidate(restoreId);
        var report = await _verifier.VerifyDatabaseAgainstExpectedAsync(candidate, payload, ct);
        if (!report.Passed)
        {
            throw Failed($"The restore candidate failed verification ({FirstFailureCode(report.Failures)}).");
        }
    }

    private async Task PostActivationVerifyAsync(
        PortableRecoveryPayload payload,
        IReadOnlyList<RetainedMediaFile> media)
    {
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            var report = await _verifier.VerifyDatabaseAgainstExpectedAsync(db, payload, CancellationToken.None);
            if (!report.Passed)
            {
                throw Failed($"The restored library failed authoritative verification ({FirstFailureCode(report.Failures)}).");
            }
        }

        var root = _paths.LiveMedia;
        _paths.VerifyMediaPath(root);
        var expected = media.ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };
        var seen = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            var relative = Path.GetRelativePath(root, file);
            if (!expected.TryGetValue(relative, out var pinned))
            {
                throw Failed("The restored media root contains a file the retained copy does not declare.");
            }

            var info = new FileInfo(file);
            if (info.Length != pinned.Descriptor.Length
                || !string.Equals(await HashFileAsync(file, CancellationToken.None),
                    pinned.Descriptor.Sha256, StringComparison.Ordinal))
            {
                throw Failed("The restored media failed SHA-256 verification against the retained copy.");
            }

            seen++;
        }

        if (seen != expected.Count)
        {
            throw Failed("The restored media root is missing files from the retained copy.");
        }
    }

    private async Task FinalizeCommittedAsync(
        SelfHostedRecoveryManifest manifest,
        Guid restoreId,
        IAsyncDisposable exclusive)
    {
        try
        {
            // The replaced library becomes a normal seven-day recovery copy:
            // claim its retention reservation and publish it as Available.
            await using (var scope = _scopes.CreateAsyncScope())
            {
                var recovery = scope.ServiceProvider.GetRequiredService<SelfHostedMigrationRecoveryService>();
                await recovery.FinalizeRetentionAsync(restoreId, transferReservationId: null,
                    exclusive, CancellationToken.None);
            }

            Step(SelfHostedRecoveryRestoreSteps.AfterFinalizeReplacedRetention);

            _manifests.Write(manifest with
            {
                Status = MigrationRecoveryStatus.Restored,
                RestoreError = null,
                RestoredAtUtc = _clock.GetUtcNow(),
            });
            Step(SelfHostedRecoveryRestoreSteps.AfterMarkSourceRestored);

            _journals.MarkResolved(restoreId, exclusive);
            Step(SelfHostedRecoveryRestoreSteps.AfterFinalizeRestore);
        }
        catch (SelfHostedRecoveryRestoreAbandonedException)
        {
            _maintenance.FailClosedForRecovery();
            throw;
        }
        catch (Exception exception)
        {
            _maintenance.FailClosedForRecovery();
            _logger.LogCritical(exception,
                "The restore is committed but finalization could not complete; the library stays in maintenance "
                + "until startup reconciliation. Follow the activation recovery guide.");
            throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryFailed,
                "The restore is committed but finalization could not complete in-process. "
                + "The host stays in maintenance; restart to reconcile, following the activation recovery guide.");
        }
    }

    /// <summary>
    /// Rollback through the startup reconciler's own steps: the durable phase
    /// selects the direction, every step validates before any executes, and
    /// repeating from any partially repaired position succeeds.
    /// </summary>
    private async Task RollBackAsync(Guid restoreId, IAsyncDisposable exclusive)
    {
        var journal = _journals.Read(restoreId)
            ?? throw SelfHostedActivationPaths.Failure("The restore journal is missing.");
        var action = SelfHostedActivationState.RecoveryAction(journal);
        if (action == SelfHostedRecoveryAction.Nothing) return;
        if (action != SelfHostedRecoveryAction.RollBackOriginal)
        {
            throw SelfHostedActivationPaths.Failure(
                "The restore journal does not describe a recoverable pre-commit state.");
        }

        foreach (var step in _recoverySteps) step.Validate(journal, action);
        _journals.Advance(restoreId, SelfHostedActivationPhase.RollingBack, exclusive);
        foreach (var step in _recoverySteps) step.Execute(journal, action);
        if (!File.Exists(_paths.LiveDatabase) || !Directory.Exists(_paths.LiveMedia))
        {
            throw SelfHostedActivationPaths.Failure("Restore rollback did not restore the live library.");
        }

        _sqlite.ReopenActivated(exclusive);
        _journals.Advance(restoreId, SelfHostedActivationPhase.RolledBack, exclusive);
        _journals.MarkResolved(restoreId, exclusive);
        Step("restore:after:rollback");
        await Task.CompletedTask;
    }

    private async Task TryAbandonRetentionPlanAsync(Guid restoreId, IAsyncDisposable exclusive)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var recovery = scope.ServiceProvider.GetRequiredService<SelfHostedMigrationRecoveryService>();
            if (_manifests.Read(restoreId) is { Status: MigrationRecoveryStatus.Creating })
            {
                await recovery.AbandonAsync(restoreId, exclusive, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "An abandoned restore retention plan could not be removed.");
        }
    }

    private Task TryMarkRetryableAsync(Guid recoveryId, string? errorCode)
    {
        try
        {
            var manifest = _manifests.Read(recoveryId);
            if (manifest is null || manifest.Status != MigrationRecoveryStatus.Restoring)
            {
                return Task.CompletedTask;
            }

            _manifests.Write(manifest with
            {
                Status = MigrationRecoveryStatus.Available,
                RestoreOperationId = null,
                RestoreDestinationRevision = null,
                RestoreError = errorCode,
                RestoredAtUtc = null,
            });
        }
        catch (Exception exception)
        {
            _logger.LogCritical(exception,
                "A failed restore could not reset its recovery state; the copy stays Restoring.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears a retryable attempt's pre-cutover journal so the next claim starts
    /// from a clean control state. A resolved <c>Committed</c> journal is never
    /// cleared (that path only completes, never retries).
    /// </summary>
    private void TryClearRestoreJournal(Guid restoreId)
    {
        try
        {
            if (_journals.Read(restoreId) is null && _journals.ReadResolved(restoreId) is null) return;
            _journals.PrepareForRetry(restoreId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A failed restore journal could not be cleared.");
        }
    }

    /// <summary>Keeps the copy Restoring but records why the in-process repair stopped.</summary>
    private void TryRecordRestoreError(Guid recoveryId, string errorCode)
    {
        try
        {
            var manifest = _manifests.Read(recoveryId);
            if (manifest is null || manifest.Status != MigrationRecoveryStatus.Restoring) return;
            _manifests.Write(manifest with { RestoreError = errorCode });
        }
        catch (Exception exception)
        {
            _logger.LogCritical(exception, "A sticky restore failure could not be recorded.");
        }
    }

    private void TryMarkTerminal(Guid recoveryId, string errorCode)
    {
        try
        {
            var manifest = _manifests.Read(recoveryId);
            if (manifest is null || manifest.Status != MigrationRecoveryStatus.Restoring) return;
            _manifests.Write(manifest with
            {
                Status = MigrationRecoveryStatus.Failed,
                RestoreOperationId = null,
                RestoreDestinationRevision = null,
                RestoreError = errorCode,
                RestoredAtUtc = null,
            });
        }
        catch (Exception exception)
        {
            _logger.LogCritical(exception,
                "A terminal restore failure could not be recorded; the copy stays Restoring.");
        }
    }

    private Task TryMarkFailedOrRetryableAsync(Guid recoveryId, Exception exception)
    {
        if (IsTerminal(exception))
        {
            TryMarkTerminal(recoveryId, ErrorCode(exception));
            return Task.CompletedTask;
        }

        return TryMarkRetryableAsync(recoveryId, ErrorCode(exception));
    }

    private SelfHostedRecoveryManifest RequireAvailable(Guid recoveryId)
    {
        if (_manifests.HasDeletionMarker(recoveryId))
        {
            // Cleanup already decided to prune this copy; it is not listed and
            // must never be claimed.
            throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryNotFound,
                "The retained recovery copy was not found.");
        }

        var manifest = ReadManifest(recoveryId);
        if (manifest.Status == MigrationRecoveryStatus.Restoring)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryRestoreConflict,
                "A restore of this recovery copy is already in progress.");
        }

        if (manifest.Status != MigrationRecoveryStatus.Available)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryRestoreConflict,
                "This recovery copy is not available for restore.");
        }

        if (manifest.ExpiresAtUtc <= _clock.GetUtcNow())
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryExpired,
                "The retained recovery copy has expired.");
        }

        if (_journals.Read(recoveryId) is not null)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryRestoreConflict,
                "This recovery copy still references an unresolved library switch.");
        }

        return manifest;
    }

    private SelfHostedRecoveryManifest ReadManifest(Guid recoveryId)
    {
        var manifest = _manifests.Read(recoveryId)
            ?? throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryNotFound,
                "The retained recovery copy was not found.");
        if (manifest.Status == MigrationRecoveryStatus.Failed
            && manifest.RestoreError == MigrationActivationErrorCodes.RecoveryCorrupt)
        {
            throw Corrupt("The retained recovery copy failed verification and is unusable.");
        }

        return manifest;
    }

    private async Task<SelfHostedRecoveryCapture> CaptureAsync(
        Guid restoreId,
        Guid operationId,
        string storedRevision,
        MigrationExistingCounts counts,
        CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var recovery = scope.ServiceProvider.GetRequiredService<SelfHostedMigrationRecoveryService>();
        return await recovery.CaptureAsync(restoreId, operationId, storedRevision, counts, ct);
    }

    private async Task EnsureAdmittedAsync(
        SelfHostedRecoveryManifest manifest,
        long sourceMediaBytes,
        CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var recovery = scope.ServiceProvider.GetRequiredService<SelfHostedMigrationRecoveryService>();
        var liveDatabaseBytes = new FileInfo(_paths.LiveDatabase).Length;
        var liveMediaBytes = MeasureMediaBytes(_paths.LiveMedia);
        var sizing = new SelfHostedActivationSizing(
            CandidateDatabaseBytes: Math.Max(liveDatabaseBytes, manifest.DatabaseBytes),
            CandidateMediaBytes: sourceMediaBytes,
            PreviousDatabaseBytes: liveDatabaseBytes,
            PreviousMediaBytes: liveMediaBytes);
        await recovery.EnsureAdmittedAsync(sizing, _transferPaths.RootPath, ct);
    }

    private async Task<(string Revision, MigrationExistingCounts Counts)> ReadDestinationFactsAsync(
        CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var revision = await _revisionProvider.GetCurrentAsync(ct);
        if (string.IsNullOrWhiteSpace(revision))
        {
            throw Failed("The live library revision is missing.");
        }

        var counts = new MigrationExistingCounts(
            Works: await db.Works.CountAsync(ct),
            Books: await db.Books.CountAsync(ct),
            Notes: await db.Notes.CountAsync(ct),
            Topics: await db.Topics.CountAsync(ct),
            NoteTopics: await db.NoteTopics.CountAsync(ct),
            Writings: await db.Writings.CountAsync(ct),
            WritingNotes: await db.WritingNotes.CountAsync(ct),
            Collections: await db.Collections.CountAsync(ct),
            BookCollections: await db.BookCollections.CountAsync(ct),
            Acquisitions: await db.BookAcquisitions.CountAsync(ct),
            NoteImportBookLinks: await db.NoteImportBookLinks.CountAsync(ct),
            AssistantSettings: await db.AssistantSettings
                .CountAsync(settings => settings.CaptureProcessingMode != null, ct));
        return (revision, counts);
    }

    private Task<string> ReadCurrentRevisionAsync(CancellationToken ct) => _revisionProvider.GetCurrentAsync(ct);

    private static long MeasureMediaBytes(string root)
    {
        if (!Directory.Exists(root)) return 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            try
            {
                total = checked(total + new FileInfo(file).Length);
            }
            catch (OverflowException)
            {
                throw SelfHostedActivationCapacity.Exhausted();
            }
        }

        return total;
    }

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(
        string source,
        string target,
        CancellationToken ct)
    {
        var temporary = target + ".partial";
        if (File.Exists(temporary)) File.Delete(temporary);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            var buffer = new byte[128 * 1024];
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    length = checked(length + read);
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }

            ActivationFileSystem.Rename(temporary, target);
            return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static NostosDbContext CreateDatabaseContext(string path) =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                SelfHostedSqliteFile.ConnectionString(path, readOnly: false, pooling: false),
                sqlite => sqlite.MigrationsAssembly(typeof(PersistenceRegistration).Assembly.FullName))
            .Options);

    private static bool TryReadBookDirectoryName(string name, out Guid bookId)
    {
        bookId = Guid.Empty;
        if (!Guid.TryParse(name, out var parsed) || parsed == Guid.Empty) return false;
        if (!string.Equals(name, parsed.ToString("D"), StringComparison.Ordinal)
            && !string.Equals(name, parsed.ToString("N"), StringComparison.Ordinal))
        {
            return false;
        }

        bookId = parsed;
        return true;
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

    private void Step(string name) => StepObserverForTesting?.Invoke(name);

    private static bool IsTerminal(Exception exception) =>
        exception is MigrationActivationException { Code: MigrationActivationErrorCodes.RecoveryCorrupt };

    private static string ErrorCode(Exception exception) =>
        exception is MigrationActivationException activation ? activation.Code : MigrationActivationErrorCodes.Failed;

    private static string FirstFailureCode(IReadOnlyList<PortableLibraryVerificationFailure> failures) =>
        failures.Count == 0 ? "portable_verify_failed" : failures[0].Code;

    private static MigrationActivationException Failed(string message) =>
        new(MigrationActivationErrorCodes.Failed, message);

    private static MigrationActivationException Corrupt(string message) =>
        new(MigrationActivationErrorCodes.RecoveryCorrupt, message);

    private static Exception WrapRestoreFailure(Exception exception) => exception switch
    {
        MigrationActivationException => exception,
        OperationCanceledException => exception,
        _ => new MigrationActivationException(MigrationActivationErrorCodes.Failed,
            "The restore failed before the library switch; the current library is unchanged."),
    };

    private static MigrationRecoveryStatusResponse ToResponse(SelfHostedRecoveryManifest manifest) =>
        new(manifest.JobId, manifest.Status, manifest.CreatedAtUtc, manifest.ExpiresAtUtc,
            manifest.TotalBytes, manifest.Counts, manifest.RestoreError);

    private sealed record SelfHostedRecoveryRestoreSource(
        PortableRecoveryPayload Payload,
        IReadOnlyList<RetainedMediaFile> Media);

    private sealed record RetainedMediaFile(string RelativePath, PortableRecoveryMedia Descriptor);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
