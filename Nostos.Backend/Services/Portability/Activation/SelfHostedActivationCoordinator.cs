using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>Observable outcome of one activation request. Never carries paths.</summary>
internal enum SelfHostedActivationOutcome
{
    /// <summary>This call performed the cutover (or resumed and completed it).</summary>
    Completed,

    /// <summary>The job was already completed; the request is an idempotent success.</summary>
    AlreadyCompleted,

    /// <summary>Another owner holds the job lease; the returned state is current.</summary>
    InProgress,
}

internal sealed record SelfHostedActivationResult(
    Guid JobId,
    SelfHostedActivationOutcome Outcome,
    MigrationJobState State,
    MigrationRecoveryStatus RecoveryStatus);

/// <summary>
/// Test seam for the crash matrix: thrown by a step observer to model abrupt
/// process loss. The coordinator rethrows it without rolling back, releasing
/// or cleaning anything, exactly like a host that died at that boundary.
/// </summary>
internal sealed class SelfHostedActivationAbandonedException : Exception;

/// <summary>Names of the durable phase writes and rename boundaries tests crash at.</summary>
internal static class SelfHostedActivationSteps
{
    public const string PhaseCandidatePrepared = "phase:CandidatePrepared";
    public const string AfterCapture = "after:capture";
    public const string AfterAdmission = "after:admission";
    public const string AfterBuildMedia = "after:build-media";
    public const string AfterBuildDatabase = "after:build-database";
    public const string AfterVerifyCandidate = "after:verify-candidate";
    public const string PhaseExclusiveEntered = "phase:ExclusiveEntered";
    public const string AfterFinalize = "after:finalize";
    public const string AfterQuiesce = "after:quiesce";
    public const string PhaseDatabaseCheckpointed = "phase:DatabaseCheckpointed";
    public const string AfterPrepareRetention = "after:prepare-retention";
    public const string PhaseCutoverPrepared = "phase:CutoverPrepared";
    public const string AfterRetainMedia = "after:retain-media";
    public const string PhasePreviousMediaRetained = "phase:PreviousMediaRetained";
    public const string AfterRetainDatabase = "after:retain-db";
    public const string PhasePreviousDatabaseRetained = "phase:PreviousDatabaseRetained";
    public const string AfterActivateMedia = "after:activate-media";
    public const string PhaseCandidateMediaActivated = "phase:CandidateMediaActivated";
    public const string AfterActivateDatabase = "after:activate-db";
    public const string PhaseCandidateDatabaseActivated = "phase:CandidateDatabaseActivated";
    public const string AfterReopen = "after:reopen";
    public const string AfterPostVerify = "after:post-verify";
    public const string PhasePostActivationVerified = "phase:PostActivationVerified";
    public const string PhaseCommitted = "phase:Committed";
    public const string AfterJournalResolved = "after:journal-resolved";
}

/// <summary>
/// End-to-end activation coordinator (issue #681, Slice 7). It joins job
/// admission, destination revision checks, candidate construction, recovery
/// retention, the SQLite quiesce and the journaled library switch.
///
/// <para><b>Ordering contract.</b> Phase A performs every expensive, fallible
/// step away from the live library and never mutates a live path. Phase B runs
/// inside one exclusive maintenance window: authoritative revision recheck,
/// <c>Activating</c>, host-state finalization, quiesce, recovery preparation,
/// then the journaled cutover renames in the order of the durable phases. Each
/// phase is written before the filesystem step it authorises, every step is
/// idempotent, and recovery reuses the startup reconciler's component steps for
/// in-process rollback, so a crash at any boundary converges on one complete
/// generation. No live database write may follow candidate finalization.
/// Post-commit work (retention finalization, job completion) executes against
/// the newly activated database; a failure there never fails the activation,
/// because the durable commit already stands and a later activation call
/// resumes it.</para>
/// </summary>
internal sealed class SelfHostedActivationCoordinator : IMigrationActivationService
{
    internal static readonly TimeSpan ActivationLeaseDuration =
        TimeSpan.FromMinutes(MigrationContractLimits.WorkerLeaseDurationMinutes);

    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    private readonly SelfHostedActivationPaths _paths;
    private readonly LibraryMaintenanceCoordinator _maintenance;
    private readonly SelfHostedActivationJournalStore _journals;
    private readonly SelfHostedRecoveryManifestStore _manifests;
    private readonly ISelfHostedActivationDatabaseBuilder _databaseBuilder;
    private readonly ISelfHostedActivationCandidateMediaBuilder _mediaBuilder;
    private readonly ISelfHostedSqliteLifecycle _sqlite;
    private readonly IPortableLibraryVerifier _verifier;
    private readonly IBookAssetStorage _assets;
    private readonly IReadOnlyList<ISelfHostedActivationRecoveryStep> _recoverySteps;
    private readonly IServiceScopeFactory _scopes;
    private readonly TransferPathResolver _transferPaths;
    private readonly TimeProvider _clock;

    /// <summary>Test seam: invoked after each named boundary; throwing models a crash.</summary>
    internal Action<string>? StepObserverForTesting { get; set; }

    public SelfHostedActivationCoordinator(
        SelfHostedActivationPaths paths,
        LibraryMaintenanceCoordinator maintenance,
        SelfHostedActivationJournalStore journals,
        SelfHostedRecoveryManifestStore manifests,
        ISelfHostedActivationDatabaseBuilder databaseBuilder,
        ISelfHostedActivationCandidateMediaBuilder mediaBuilder,
        ISelfHostedSqliteLifecycle sqlite,
        IPortableLibraryVerifier verifier,
        IBookAssetStorage assets,
        IEnumerable<ISelfHostedActivationRecoveryStep> recoverySteps,
        IServiceScopeFactory scopes,
        TransferPathResolver transferPaths,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(journals);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(databaseBuilder);
        ArgumentNullException.ThrowIfNull(mediaBuilder);
        ArgumentNullException.ThrowIfNull(sqlite);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(recoverySteps);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(transferPaths);

        _paths = paths;
        _maintenance = maintenance;
        _journals = journals;
        _manifests = manifests;
        _databaseBuilder = databaseBuilder;
        _mediaBuilder = mediaBuilder;
        _sqlite = sqlite;
        _verifier = verifier;
        _assets = assets;
        _recoverySteps = recoverySteps.OrderBy(step => step.Order).ToArray();
        _scopes = scopes;
        _transferPaths = transferPaths;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Public worker entry point. A durably admitted (<c>Activating</c>) job is
    /// resumed; a <c>ReadyToActivate</c> job may only be activated without an
    /// explicit request object when the destination is empty, because the
    /// replacement confirmation travels through <see cref="ActivateAsync(Guid, MigrationActivateRequest, CancellationToken)"/>.
    /// </summary>
    public Task ActivateAsync(Guid jobId, CancellationToken ct) =>
        ActivateCoreAsync(jobId, request: null, ct);

    /// <summary>
    /// Full admission entry point: carries the exact confirmation the user
    /// reviewed before the job enters <c>Activating</c>.
    /// </summary>
    internal async Task<SelfHostedActivationResult> ActivateAsync(
        Guid jobId,
        MigrationActivateRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ActivateCoreAsync(jobId, request, ct);
    }

    private async Task<SelfHostedActivationResult> ActivateCoreAsync(
        Guid jobId,
        MigrationActivateRequest? request,
        CancellationToken ct)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("A migration job identifier is required.", nameof(jobId));
        }

        var record = await LoadJobAsync(jobId, ct);
        var state = (MigrationJobState)record.State;
        if (state == MigrationJobState.Completed)
        {
            return new SelfHostedActivationResult(jobId, SelfHostedActivationOutcome.AlreadyCompleted,
                state, (MigrationRecoveryStatus)record.RecoveryStatus);
        }

        if (MigrationJobTransitions.IsTerminal(state))
        {
            throw MigrationJobStoreException.InvalidState(jobId,
                $"Migration job {jobId} is {state} and cannot be activated.");
        }

        // A durable Committed journal means the switch already happened: only
        // post-commit completion may run, never a second cutover.
        var committed = ReadCommittedJournal(jobId);
        if (committed is not null)
        {
            return await ResumeCommittedAsync(jobId, record, committed, ct);
        }

        var storedRevision = record.DestinationRevision;
        if (string.IsNullOrWhiteSpace(storedRevision))
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.Failed,
                "The job does not record a destination revision to confirm.");
        }

        if (record.PreparedStagingId is not { } stagingValue || stagingValue == Guid.Empty)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.Failed,
                "The job has no prepared import staging area.");
        }

        string? token = null;
        LeaseHeartbeat? heartbeat = null;
        try
        {
            token = await AcquireLeaseAsync(jobId, ct);
            if (token is null)
            {
                var current = await LoadJobAsync(jobId, CancellationToken.None);
                return new SelfHostedActivationResult(jobId, SelfHostedActivationOutcome.InProgress,
                    (MigrationJobState)current.State, (MigrationRecoveryStatus)current.RecoveryStatus);
            }

            heartbeat = StartHeartbeat(jobId, token);

            // ---- Phase A: long, outside maintenance, no live mutation. ----
            var (prepared, expected) = await RebuildAndVerifyPreparedAsync(stagingValue, ct);
            var facts = await ReadDestinationFactsAsync(ct);
            if (state == MigrationJobState.ReadyToActivate)
            {
                // Without an explicit request an empty destination may proceed;
                // a populated one fails closed with confirmation_required.
                var effective = request ?? new MigrationActivateRequest(storedRevision, false);
                MigrationActivationAdmission.Validate(MigrationDirection.Import, state, storedRevision,
                    effective, facts.Status, facts.Revision);
            }
            else
            {
                // Activating is the durable record that explicit intent was
                // accepted; only the destination revision is revalidated here.
                MigrationActivationAdmission.ValidateReplacement(storedRevision, storedRevision,
                    confirmReplacement: true, facts.Status, facts.Revision);
            }

            var retain = facts.Status == MigrationDestinationStatus.Populated;
            var operationId = Guid.NewGuid();
            _journals.PrepareForRetry(jobId);
            _journals.Write(new SelfHostedActivationJournal(jobId, operationId,
                SelfHostedActivationPhase.CandidatePrepared, storedRevision, retain, _clock.GetUtcNow()));
            Step(SelfHostedActivationSteps.PhaseCandidatePrepared);

            SelfHostedRecoveryCapture? capture = null;
            if (retain)
            {
                capture = await CaptureAsync(jobId, operationId, storedRevision, facts.Counts, ct);
                Step(SelfHostedActivationSteps.AfterCapture);
            }

            await EnsureAdmittedAsync(prepared, capture, ct);
            Step(SelfHostedActivationSteps.AfterAdmission);

            await _mediaBuilder.BuildMediaAsync(jobId, prepared, progress: null, ct);
            Step(SelfHostedActivationSteps.AfterBuildMedia);
            await _databaseBuilder.BuildPortableCandidateAsync(jobId, prepared, ct);
            Step(SelfHostedActivationSteps.AfterBuildDatabase);
            await VerifyCandidateAsync(jobId, prepared, expected, ct);
            Step(SelfHostedActivationSteps.AfterVerifyCandidate);

            await heartbeat.DisposeAsync();
            heartbeat = null;

            // ---- Phase B: exclusive maintenance, bounded, committed or rolled back. ----
            var recoveryStatus = await RunCutoverAsync(jobId, state, record, prepared, expected,
                capture, retain, storedRevision, token, ct);
            return new SelfHostedActivationResult(jobId, SelfHostedActivationOutcome.Completed,
                MigrationJobState.Completed, recoveryStatus);
        }
        catch (SelfHostedActivationAbandonedException)
        {
            // A crash leaves every artifact exactly as the boundary produced it;
            // the fresh host's reconciler decides the generation.
            throw;
        }
        catch (Exception exception)
        {
            TryCleanCandidateArtifacts(jobId);
            throw WrapActivationFailure(exception);
        }
        finally
        {
            if (heartbeat is not null) await heartbeat.DisposeAsync();
            if (token is not null) await TryReleaseLeaseAsync(jobId, token);
        }
    }

    private async Task<MigrationRecoveryStatus> RunCutoverAsync(
        Guid jobId,
        MigrationJobState state,
        MigrationJobRecord record,
        IPreparedPortableImport prepared,
        PortablePreparedImportVerification expected,
        SelfHostedRecoveryCapture? capture,
        bool retain,
        string storedRevision,
        string token,
        CancellationToken ct)
    {
        IAsyncDisposable? exclusive = null;
        LeaseHeartbeat? heartbeat = null;
        var jobEnteredActivation = false;
        var committed = false;
        try
        {
            // Fail closed when the drain cannot complete: nothing has changed.
            exclusive = await _maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation, ct);

            // The authoritative revision recheck only closes the race once
            // admission is closed and every reader/writer has drained.
            var facts = await ReadDestinationFactsAsync(CancellationToken.None);
            MigrationActivationAdmission.ValidateReplacement(storedRevision, storedRevision,
                confirmReplacement: true, facts.Status, facts.Revision);
            if ((facts.Status == MigrationDestinationStatus.Populated) != retain)
            {
                throw new MigrationActivationException(MigrationActivationErrorCodes.DestinationConflict,
                    "The destination changed. Review replacement again.");
            }

            if (state == MigrationJobState.ReadyToActivate)
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IMigrationJobStore>()
                    .TransitionAsync(jobId, MigrationJobState.Activating, token, CancellationToken.None);
            }

            jobEnteredActivation = true;

            // The cutover window must not outlive the job lease: renew once the
            // exact boundary is reached (the pre-window heartbeat has stopped).
            await RenewLeaseOnceAsync(jobId, token);

            _journals.Advance(jobId, SelfHostedActivationPhase.ExclusiveEntered, exclusive);
            Step(SelfHostedActivationSteps.PhaseExclusiveEntered);

            // Last live database mutation is the finalized candidate snapshot
            // plus the revision bump. Everything after quiesce is path movement.
            await _databaseBuilder.FinalizeCandidateAsync(jobId, exclusive, CancellationToken.None);
            if (!_databaseBuilder.IsFinalized(jobId))
            {
                throw new MigrationActivationException(MigrationActivationErrorCodes.Failed,
                    "The activation candidate was not finalized.");
            }

            Step(SelfHostedActivationSteps.AfterFinalize);
            _sqlite.QuiesceLive(exclusive);
            Step(SelfHostedActivationSteps.AfterQuiesce);
            _journals.Advance(jobId, SelfHostedActivationPhase.DatabaseCheckpointed, exclusive);
            Step(SelfHostedActivationSteps.PhaseDatabaseCheckpointed);

            await using (var scope = _scopes.CreateAsyncScope())
            {
                var recovery = scope.ServiceProvider.GetRequiredService<SelfHostedMigrationRecoveryService>();
                if (retain)
                {
                    if (_manifests.Read(jobId) is { Status: MigrationRecoveryStatus.Creating })
                    {
                        // A crash-terminated attempt may have left an unfinished
                        // retention plan bound to the previous attempt.
                        await recovery.AbandonAsync(jobId, exclusive, CancellationToken.None);
                    }

                    await recovery.PrepareRetentionAsync(jobId, capture!, exclusive, CancellationToken.None);
                    Step(SelfHostedActivationSteps.AfterPrepareRetention);
                }

                _journals.Advance(jobId, SelfHostedActivationPhase.CutoverPrepared, exclusive);
                Step(SelfHostedActivationSteps.PhaseCutoverPrepared);

                if (retain)
                {
                    await recovery.RetainMediaAsync(jobId, exclusive, CancellationToken.None);
                    Step(SelfHostedActivationSteps.AfterRetainMedia);
                    _journals.Advance(jobId, SelfHostedActivationPhase.PreviousMediaRetained, exclusive);
                    Step(SelfHostedActivationSteps.PhasePreviousMediaRetained);
                    await recovery.RetainDatabaseAsync(jobId, exclusive, CancellationToken.None);
                    Step(SelfHostedActivationSteps.AfterRetainDatabase);
                    _journals.Advance(jobId, SelfHostedActivationPhase.PreviousDatabaseRetained, exclusive);
                    Step(SelfHostedActivationSteps.PhasePreviousDatabaseRetained);
                }
                else
                {
                    // Empty destination: the same scratch renames, no manifest.
                    RetainEmptyMedia(jobId);
                    Step(SelfHostedActivationSteps.AfterRetainMedia);
                    _journals.Advance(jobId, SelfHostedActivationPhase.PreviousMediaRetained, exclusive);
                    Step(SelfHostedActivationSteps.PhasePreviousMediaRetained);
                    RetainEmptyDatabase(jobId);
                    Step(SelfHostedActivationSteps.AfterRetainDatabase);
                    _journals.Advance(jobId, SelfHostedActivationPhase.PreviousDatabaseRetained, exclusive);
                    Step(SelfHostedActivationSteps.PhasePreviousDatabaseRetained);
                }
            }

            ActivationFileSystem.Rename(_paths.CandidateMedia(jobId), _paths.LiveMedia);
            Step(SelfHostedActivationSteps.AfterActivateMedia);
            _journals.Advance(jobId, SelfHostedActivationPhase.CandidateMediaActivated, exclusive);
            Step(SelfHostedActivationSteps.PhaseCandidateMediaActivated);

            ActivationFileSystem.Rename(_paths.CandidateDatabase(jobId), _paths.LiveDatabase);
            Step(SelfHostedActivationSteps.AfterActivateDatabase);
            _journals.Advance(jobId, SelfHostedActivationPhase.CandidateDatabaseActivated, exclusive);
            Step(SelfHostedActivationSteps.PhaseCandidateDatabaseActivated);

            _sqlite.ReopenActivated(exclusive);
            Step(SelfHostedActivationSteps.AfterReopen);

            // Verification reads the newly activated generation and may be long;
            // keep the job lease alive against the activated database.
            heartbeat = StartHeartbeat(jobId, token);
            await PostActivationVerifyAsync(jobId, prepared, expected);
            await heartbeat.DisposeAsync();
            heartbeat = null;
            Step(SelfHostedActivationSteps.AfterPostVerify);

            _journals.Advance(jobId, SelfHostedActivationPhase.PostActivationVerified, exclusive);
            Step(SelfHostedActivationSteps.PhasePostActivationVerified);
            _journals.Advance(jobId, SelfHostedActivationPhase.Committed, exclusive);
            Step(SelfHostedActivationSteps.PhaseCommitted);
            committed = true;

            return await FinalizePostCommitAsync(jobId, token, retain, exclusive, record);
        }
        catch (SelfHostedActivationAbandonedException)
        {
            throw;
        }
        catch (Exception exception) when (!committed)
        {
            if (jobEnteredActivation && exclusive is not null)
            {
                try
                {
                    await RollBackAsync(jobId, exclusive);
                }
                catch (Exception)
                {
                    throw new MigrationActivationException(MigrationActivationErrorCodes.RecoveryFailed,
                        "Activation failed and the original library could not be restored. Stop the host and follow the activation recovery guide.");
                }

                await TryMarkFailedAsync(jobId, token);
            }

            throw WrapActivationFailure(exception);
        }
        finally
        {
            if (heartbeat is not null) await heartbeat.DisposeAsync();
            if (exclusive is not null) await exclusive.DisposeAsync();
        }
    }

    /// <summary>
    /// Rollback through the startup reconciler's own steps: the durable phase
    /// selects the direction, every step validates before any executes, and
    /// repeating from any partially repaired position succeeds.
    /// </summary>
    private async Task RollBackAsync(Guid jobId, IAsyncDisposable exclusive)
    {
        var journal = _journals.Read(jobId)
            ?? throw SelfHostedActivationPaths.Failure("The activation journal is missing.");
        var action = SelfHostedActivationState.RecoveryAction(journal);
        if (action == SelfHostedRecoveryAction.Nothing)
        {
            // No rename is possible before CutoverPrepared: the live paths are
            // still the original generation and the journal is retryable.
            return;
        }

        if (action != SelfHostedRecoveryAction.RollBackOriginal)
        {
            throw SelfHostedActivationPaths.Failure(
                "The activation journal does not describe a recoverable pre-commit state.");
        }

        foreach (var step in _recoverySteps) step.Validate(journal, action);
        _journals.Advance(jobId, SelfHostedActivationPhase.RollingBack, exclusive);
        foreach (var step in _recoverySteps) step.Execute(journal, action);
        if (!File.Exists(_paths.LiveDatabase) || !Directory.Exists(_paths.LiveMedia))
        {
            throw SelfHostedActivationPaths.Failure("Activation rollback did not restore the live library.");
        }

        _journals.Advance(jobId, SelfHostedActivationPhase.RolledBack, exclusive);
        _journals.MarkResolved(jobId, exclusive);
        Step("after:rollback");
        await Task.CompletedTask;
    }

    private async Task<SelfHostedActivationResult> ResumeCommittedAsync(
        Guid jobId,
        MigrationJobRecord record,
        SelfHostedActivationJournal journal,
        CancellationToken ct)
    {
        if ((MigrationJobState)record.State != MigrationJobState.Activating)
        {
            throw MigrationJobStoreException.InvalidState(jobId,
                "A committed activation must be completed from the Activating state.");
        }

        var token = await AcquireLeaseAsync(jobId, ct);
        if (token is null)
        {
            return new SelfHostedActivationResult(jobId, SelfHostedActivationOutcome.InProgress,
                (MigrationJobState)record.State, (MigrationRecoveryStatus)record.RecoveryStatus);
        }

        try
        {
            var exclusive = await _maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.Activation, ct);
            try
            {
                var status = await FinalizePostCommitAsync(jobId, token, journal.RetainPreviousLibrary,
                    exclusive, record);
                return new SelfHostedActivationResult(jobId, SelfHostedActivationOutcome.Completed,
                    MigrationJobState.Completed, status);
            }
            finally
            {
                await exclusive.DisposeAsync();
            }
        }
        finally
        {
            await TryReleaseLeaseAsync(jobId, token);
        }
    }

    /// <summary>
    /// Post-commit completion against the newly activated database. The durable
    /// commit already stands, so any failure here is retryable by invoking
    /// activation again and must never turn a successful activation into a
    /// failure.
    /// </summary>
    private async Task<MigrationRecoveryStatus> FinalizePostCommitAsync(
        Guid jobId,
        string token,
        bool retain,
        IAsyncDisposable exclusive,
        MigrationJobRecord record)
    {
        var status = MigrationRecoveryStatus.NotRequired;
        try
        {
            if (retain)
            {
                await using var scope = _scopes.CreateAsyncScope();
                var manifest = await scope.ServiceProvider
                    .GetRequiredService<SelfHostedMigrationRecoveryService>()
                    .FinalizeRetentionAsync(jobId, record.ReservationId, exclusive, CancellationToken.None);
                status = manifest.Status;
            }
            else
            {
                DiscardEmptyPrevious(jobId);
            }

            await using (var scope = _scopes.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<EfMigrationJobStore>();
                await store.UpdateRecoveryStatusAsync(jobId, status, token, CancellationToken.None);
                await scope.ServiceProvider.GetRequiredService<IMigrationJobStore>()
                    .TransitionAsync(jobId, MigrationJobState.Completed, token, CancellationToken.None);
            }

            _journals.MarkResolved(jobId, exclusive);
            Step(SelfHostedActivationSteps.AfterJournalResolved);
        }
        catch (SelfHostedActivationAbandonedException)
        {
            throw;
        }
        catch
        {
            // Non-essential to the authoritative activation truth.
        }

        return status;
    }

    private async Task PostActivationVerifyAsync(
        Guid jobId,
        IPreparedPortableImport prepared,
        PortablePreparedImportVerification expected)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var database = await _verifier.VerifyCandidateAsync(
            db, _paths.LiveMedia, prepared, expected, CancellationToken.None);
        if (!database.Passed)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.Failed,
                $"The activated library failed authoritative verification ({FirstFailureCode(database.Failures)}).");
        }

        var media = await _verifier.VerifyMediaAsync(_assets, expected.Media, CancellationToken.None);
        if (!media.Passed)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.Failed,
                $"The activated media failed authoritative verification ({FirstFailureCode(media.Failures)}).");
        }
    }

    private async Task VerifyCandidateAsync(
        Guid jobId,
        IPreparedPortableImport prepared,
        PortablePreparedImportVerification expected,
        CancellationToken ct)
    {
        using var candidate = _databaseBuilder.OpenCandidate(jobId);
        var report = await _verifier.VerifyCandidateAsync(
            candidate, _paths.CandidateMedia(jobId), prepared, expected, ct);
        if (!report.Passed)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.Failed,
                $"The activation candidate failed verification ({FirstFailureCode(report.Failures)}).");
        }
    }

    private async Task<(IPreparedPortableImport Prepared, PortablePreparedImportVerification Expected)>
        RebuildAndVerifyPreparedAsync(Guid stagingId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var staging = scope.ServiceProvider.GetRequiredService<IPortableImportStaging>();
        IPreparedPortableImport prepared;
        try
        {
            prepared = await staging.RebuildPreparedImportAsync(new PortableStagingId(stagingId), ct);
        }
        catch (PortableStagingException exception)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.Failed,
                $"The prepared import could not be reconstructed ({exception.Code}).");
        }

        var expected = await _verifier.VerifyPreparedImportAsync(staging, prepared, ct);
        if (!expected.Passed)
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.Failed,
                $"The prepared import failed verification ({FirstFailureCode(expected.Failures)}).");
        }

        if (expected.Metadata.StagingId != prepared.Metadata.StagingId
            || expected.Metadata.DataBytes != prepared.Metadata.DataBytes
            || !string.Equals(expected.Metadata.DataSha256, prepared.Metadata.DataSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.Failed,
                "The verified expected state does not belong to the prepared import.");
        }

        return (prepared, expected);
    }

    private async Task EnsureAdmittedAsync(
        IPreparedPortableImport prepared,
        SelfHostedRecoveryCapture? capture,
        CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var recovery = scope.ServiceProvider.GetRequiredService<SelfHostedMigrationRecoveryService>();
        var liveBytes = new FileInfo(_paths.LiveDatabase).Length;
        var sizing = new SelfHostedActivationSizing(
            CandidateDatabaseBytes: AddChecked(liveBytes, prepared.Metadata.DataBytes),
            CandidateMediaBytes: prepared.Metadata.MediaBytes,
            PreviousDatabaseBytes: liveBytes,
            PreviousMediaBytes: capture?.MediaBytes ?? 0,
            StagedBytes: AddChecked(prepared.Metadata.DataBytes, prepared.Metadata.MediaBytes));
        await recovery.EnsureAdmittedAsync(sizing, _transferPaths.RootPath, ct);
    }

    private async Task<SelfHostedRecoveryCapture> CaptureAsync(
        Guid jobId,
        Guid operationId,
        string storedRevision,
        MigrationExistingCounts counts,
        CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var recovery = scope.ServiceProvider.GetRequiredService<SelfHostedMigrationRecoveryService>();
        return await recovery.CaptureAsync(jobId, operationId, storedRevision, counts, ct);
    }

    private SelfHostedActivationJournal? ReadCommittedJournal(Guid jobId)
    {
        var current = _journals.Read(jobId);
        if (current is { Phase: SelfHostedActivationPhase.Committed })
        {
            return current;
        }

        var resolved = _journals.ReadResolved(jobId);
        return resolved is { Phase: SelfHostedActivationPhase.Committed } ? resolved : null;
    }

    private async Task<MigrationJobRecord> LoadJobAsync(Guid jobId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        return await db.MigrationJobRecords.AsNoTracking()
            .SingleOrDefaultAsync(job => job.Id == jobId, ct)
            ?? throw MigrationJobStoreException.NotFound(jobId);
    }

    private async Task<DestinationFacts> ReadDestinationFactsAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var revision = await db.LibraryStates.AsNoTracking()
            .Select(state => state.StateVersion)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(revision))
        {
            throw new MigrationActivationException(MigrationActivationErrorCodes.Failed,
                "The live library revision is missing.");
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
        var status = counts.TotalRows > 0
            ? MigrationDestinationStatus.Populated
            : MigrationDestinationStatus.Empty;
        return new DestinationFacts(revision, status, counts);
    }

    private async Task<string?> AcquireLeaseAsync(Guid jobId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMigrationJobStore>()
            .TryAcquireLeaseAsync(jobId, ActivationLeaseDuration, ct);
    }

    private async Task RenewLeaseOnceAsync(Guid jobId, string token)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var renewed = await scope.ServiceProvider.GetRequiredService<IMigrationJobStore>()
            .RenewLeaseAsync(jobId, token, ActivationLeaseDuration, CancellationToken.None);
        if (!renewed) throw MigrationJobStoreException.LeaseConflict(jobId);
    }

    private async Task TryReleaseLeaseAsync(Guid jobId, string token)
    {
        try
        {
            // Between retaining the previous database and moving the candidate
            // into place the live path does not exist. Opening it would create an
            // empty SQLite file and corrupt the recorded layout, so the lease is
            // left to expire in exactly that window.
            if (!File.Exists(_paths.LiveDatabase)) return;
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IMigrationJobStore>()
                .ReleaseLeaseAsync(jobId, token, CancellationToken.None);
        }
        catch
        {
            // Expiry releases the lease; a crashed owner must never block recovery.
        }
    }

    private async Task TryMarkFailedAsync(Guid jobId, string token)
    {
        try
        {
            if (!File.Exists(_paths.LiveDatabase)) return;
            await using var scope = _scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
            var job = await store.GetAsync(jobId, CancellationToken.None);
            if (job is null || job.State != MigrationJobState.Activating || job.LeaseToken != token)
            {
                return;
            }

            await store.TransitionAsync(jobId, MigrationJobState.Failed, token, CancellationToken.None);
        }
        catch
        {
            // The durable journal still describes the rolled-back generation.
        }
    }

    private void RetainEmptyMedia(Guid jobId)
    {
        var live = _paths.LiveMedia;
        var previous = _paths.PreviousMedia(jobId);
        _paths.VerifyMediaPath(live);
        _paths.VerifyMediaPath(previous);
        if (File.Exists(live) || File.Exists(previous))
        {
            throw SelfHostedActivationPaths.Failure("A media path exists as a file where a directory belongs.");
        }

        if (!Directory.Exists(live) || Directory.Exists(previous))
        {
            throw SelfHostedActivationPaths.Failure("The live media root is not in a retryable state.");
        }

        ActivationFileSystem.Rename(live, previous);
    }

    private void RetainEmptyDatabase(Guid jobId)
    {
        var live = _paths.LiveDatabase;
        var previous = _paths.PreviousDatabase(jobId);
        _paths.VerifyDatabasePath(live);
        _paths.VerifyDatabasePath(previous);
        if (!File.Exists(live) || File.Exists(previous))
        {
            throw SelfHostedActivationPaths.Failure("The live database is not in a retryable state.");
        }

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = live + suffix;
            if (!File.Exists(sidecar)) continue;
            if (string.Equals(suffix, "-wal", StringComparison.Ordinal) && new FileInfo(sidecar).Length > 0)
            {
                throw new MigrationActivationException(MigrationActivationErrorCodes.Busy,
                    "The live database still has an uncheckpointed WAL.");
            }

            File.Delete(sidecar);
        }

        ActivationFileSystem.Rename(live, previous);
    }

    private void DiscardEmptyPrevious(Guid jobId)
    {
        var media = _paths.PreviousMedia(jobId);
        _paths.VerifyMediaPath(media);
        if (Directory.Exists(media)) Directory.Delete(media, recursive: true);
        else if (File.Exists(media)) File.Delete(media);

        var database = _paths.PreviousDatabase(jobId);
        _paths.VerifyDatabasePath(database);
        foreach (var path in new[] { database, database + "-wal", database + "-shm" })
        {
            if (File.Exists(path)) File.Delete(path);
        }

        _manifests.DeleteEmptyDirectory(jobId);
    }

    private void TryCleanCandidateArtifacts(Guid jobId)
    {
        try
        {
            var candidate = _paths.CandidateDatabase(jobId);
            _paths.VerifyDatabasePath(candidate);
            SelfHostedSqliteFile.ClearPoolFor(candidate);
            foreach (var path in new[] { candidate, candidate + "-wal", candidate + "-shm" })
            {
                if (File.Exists(path)) File.Delete(path);
            }

            var marker = _paths.CandidateFinalizationMarker(jobId);
            _paths.VerifyDatabasePath(marker);
            if (File.Exists(marker)) File.Delete(marker);
            var directory = Path.GetDirectoryName(marker)!;
            if (Directory.Exists(directory))
            {
                foreach (var temporary in Directory.EnumerateFiles(
                             directory, Path.GetFileName(marker) + ".*").ToArray())
                {
                    File.Delete(temporary);
                }
            }

            var media = _paths.CandidateMedia(jobId);
            _paths.VerifyMediaPath(media);
            if (Directory.Exists(media)) Directory.Delete(media, recursive: true);
        }
        catch
        {
            // Best effort only; the next attempt rebuilds from scratch.
        }
    }

    private LeaseHeartbeat StartHeartbeat(Guid jobId, string token)
    {
        var stopped = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            while (!stopped.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HeartbeatInterval, _clock, stopped.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var renewed = await scope.ServiceProvider.GetRequiredService<IMigrationJobStore>()
                        .RenewLeaseAsync(jobId, token, ActivationLeaseDuration, stopped.Token);
                    if (!renewed) return;
                }
                catch
                {
                    // A failed renewal stops this owner; the store lease expires.
                    return;
                }
            }
        }, CancellationToken.None);
        return new LeaseHeartbeat(stopped, task);
    }

    private void Step(string name) => StepObserverForTesting?.Invoke(name);

    private static long AddChecked(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            throw SelfHostedActivationCapacity.Exhausted();
        }
    }

    private static string FirstFailureCode(IReadOnlyList<PortableLibraryVerificationFailure> failures) =>
        failures.Count == 0 ? "portable_verify_failed" : failures[0].Code;

    private static Exception WrapActivationFailure(Exception exception) => exception switch
    {
        MigrationActivationException => exception,
        MigrationJobStoreException => exception,
        OperationCanceledException => exception,
        _ => new MigrationActivationException(MigrationActivationErrorCodes.Failed,
            "The activation failed before the library switch; the original library is unchanged."),
    };

    private sealed record DestinationFacts(
        string Revision,
        MigrationDestinationStatus Status,
        MigrationExistingCounts Counts);

    private sealed class LeaseHeartbeat(CancellationTokenSource stopped, Task task) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            stopped.Cancel();
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Cancellation of the delay is the expected stop path.
            }

            stopped.Dispose();
        }
    }
}
