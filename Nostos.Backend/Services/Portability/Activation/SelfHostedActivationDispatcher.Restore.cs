using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Portability.Migration;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Recovery-restore half of the shared library-switch dispatcher. Admission
/// re-validates the durable recovery manifest and keeps the coordinator's
/// conditional <c>Available → Restoring</c> claim as the durable admission, so
/// exactly one run exists per recovery copy. The startup/periodic scan enqueues
/// copies whose durable claim survived a restart. Status is served from the
/// in-memory slot while a run is active and from the manifest otherwise; both
/// are filesystem-only reads that never open SQLite.
/// </summary>
internal sealed partial class SelfHostedActivationDispatcher
{
    /// <summary>Test seam: how many restore runs actually reached the coordinator.</summary>
    internal int StartedRestoreRunCount => Volatile.Read(ref _restoreStarted);

    /// <summary>Test seam: observes the restore coordinator instance before a run starts.</summary>
    internal Action<SelfHostedRecoveryRestoreCoordinator>? RestoreCoordinatorCreatedForTesting { get; set; }

    /// <summary>Test seam: awaited after a restore run is dequeued and before it starts.</summary>
    internal Func<Guid, CancellationToken, Task>? BeforeRestoreRunForTesting { get; set; }

    /// <summary>Test seam: invoked at every restore cutover boundary (crash matrix).</summary>
    internal Action<string>? RestoreStepObserverForTesting { get; set; }

    /// <summary>
    /// Validates an explicit restore request against the durable copy and the
    /// current destination, then claims (conditionally) and queues at most one
    /// background run. A copy whose durable <c>Restoring</c> claim already
    /// exists is resumed rather than claimed again.
    /// </summary>
    internal async Task<MigrationRecoveryRestoreStatusResponse> RequestRestoreAsync(
        Guid recoveryId,
        MigrationRecoveryRestoreRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (recoveryId == Guid.Empty) throw NotFound();

        if (_stopping) throw LibraryMaintenanceCoordinator.Busy();

        // The route owns no ambient lease: admission takes a brief shared lease
        // for the durable read/claim, then releases it before the background run
        // may request the exclusive one.
        using var operation = _maintenance.TryEnterOperation()
            ?? throw LibraryMaintenanceCoordinator.Busy();

        var stripe = StripeFor(recoveryId);
        await stripe.WaitAsync(ct);
        try
        {
            if (_stopping) throw LibraryMaintenanceCoordinator.Busy();
            _restoreRegistry.Prune(_clock.GetUtcNow(), FinishedSlotTtl);
            var slot = _restoreRegistry.GetOrAdd(recoveryId);
            if (slot.State is SelfHostedActivationRunState.Accepted or SelfHostedActivationRunState.Running)
            {
                return BuildRestoreRunStatus(
                    slot,
                    slot.Phase == MigrationActivationPhase.Queued
                        ? MigrationActivationOutcome.Accepted
                        : MigrationActivationOutcome.Running);
            }

            var manifest = ReadRestoreManifest(recoveryId);
            if (manifest.Status == MigrationRecoveryStatus.Restored)
            {
                return BuildDurableRestoreStatus(manifest);
            }

            if (manifest.Status == MigrationRecoveryStatus.Restoring)
            {
                // A durable claim exists (for example after a restart before the
                // resume scan ran): resume it instead of claiming again.
                if (!slot.TryAdmitResume())
                {
                    return BuildRestoreRunStatus(slot, MigrationActivationOutcome.Accepted);
                }

                ApplyManifestSnapshot(slot, manifest);
                EnqueueRestore(slot);
                return BuildRestoreRunStatus(slot, MigrationActivationOutcome.Accepted);
            }

            // Durable validation plus the conditional Available -> Restoring
            // claim; conflicts/expiry/corruption propagate as typed errors.
            await using var scope = _scopes.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedRecoveryRestoreCoordinator>();
            var claimed = await coordinator.RequestRestoreAsync(recoveryId, request, ct);
            if (!slot.TryAdmit(request))
            {
                return BuildRestoreRunStatus(slot, MigrationActivationOutcome.Accepted);
            }

            ApplyClaimSnapshot(slot, claimed);
            EnqueueRestore(slot);
            return BuildRestoreRunStatus(slot, MigrationActivationOutcome.Accepted);
        }
        finally
        {
            stripe.Release();
        }
    }

    /// <summary>
    /// Status of one recovery copy. Served from the in-memory run slot while a
    /// run is active, then merged with the durable manifest; only manifest files
    /// are read, never SQLite.
    /// </summary>
    internal Task<MigrationRecoveryRestoreStatusResponse> GetRestoreStatusAsync(
        Guid recoveryId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (recoveryId == Guid.Empty) throw NotFound();
        _restoreRegistry.Prune(_clock.GetUtcNow(), FinishedSlotTtl);
        var slot = _restoreRegistry.Find(recoveryId);
        if (slot is { State: SelfHostedActivationRunState.Accepted })
        {
            return Task.FromResult(BuildRestoreRunStatus(slot, MigrationActivationOutcome.Accepted));
        }

        if (slot is { State: SelfHostedActivationRunState.Running })
        {
            return Task.FromResult(BuildRestoreRunStatus(slot, MigrationActivationOutcome.Running));
        }

        var manifest = ReadRestoreManifest(recoveryId);
        return Task.FromResult(MergeRestoreFinishedSlot(slot, manifest));
    }

    /// <summary>
    /// Enqueues every durably claimed <c>Restoring</c> copy that has no live
    /// run. One corrupt manifest is skipped and reported instead of blocking
    /// the scan for the other copies.
    /// </summary>
    internal Task ScanPendingRestoresAsync(CancellationToken ct)
    {
        if (_stopping) return Task.CompletedTask;
        IReadOnlyList<Guid> copies;
        try
        {
            copies = _manifests.ListJobDirectories();
        }
        catch (MigrationActivationException exception)
        {
            _logger.LogError(exception,
                "Recovery copies could not be enumerated; a malformed recovery directory must be inspected.");
            return Task.CompletedTask;
        }

        foreach (var recoveryId in copies)
        {
            ct.ThrowIfCancellationRequested();
            SelfHostedRecoveryManifest? manifest;
            try
            {
                manifest = _manifests.Read(recoveryId);
            }
            catch (MigrationActivationException exception)
            {
                _logger.LogError(exception,
                    "A recovery copy is unusable and was skipped by the restore resume scan.");
                continue;
            }

            if (manifest?.Status != MigrationRecoveryStatus.Restoring) continue;
            var slot = _restoreRegistry.GetOrAdd(recoveryId);
            if (slot.State is SelfHostedActivationRunState.Accepted or SelfHostedActivationRunState.Running)
            {
                continue;
            }

            if (!slot.TryAdmitResume()) continue;
            ApplyManifestSnapshot(slot, manifest);
            if (!_queue.Writer.TryWrite(SelfHostedLibrarySwitchWorkItem.ForRestore(recoveryId)))
            {
                slot.MarkDropped(_clock.GetUtcNow());
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Awaits the current restore run for a copy, if one exists (test seam).</summary>
    internal Task AwaitRestoreFinishedAsync(Guid recoveryId, CancellationToken ct)
    {
        var slot = _restoreRegistry.Find(recoveryId);
        return slot is null ? Task.CompletedTask : slot.Finished.WaitAsync(ct);
    }

    private async Task RunRestoreItemAsync(Guid recoveryId, CancellationToken stoppingToken)
    {
        var slot = _restoreRegistry.Find(recoveryId);
        if (slot is null || slot.State != SelfHostedActivationRunState.Accepted)
        {
            return;
        }

        await RunRestoreAsync(slot, stoppingToken);
    }

    private async Task RunRestoreAsync(
        SelfHostedRecoveryRestoreRunSlot slot,
        CancellationToken stoppingToken)
    {
        if (BeforeRestoreRunForTesting is { } hook)
        {
            await hook(slot.RecoveryId, stoppingToken);
        }

        stoppingToken.ThrowIfCancellationRequested();
        if (!slot.TryMarkRunning())
        {
            return;
        }

        Interlocked.Increment(ref _restoreStarted);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedRecoveryRestoreCoordinator>();
            RestoreCoordinatorCreatedForTesting?.Invoke(coordinator);
            coordinator.StepObserverForTesting = step =>
            {
                slot.SetPhase(RestorePhaseForStep(step));
                RestoreStepObserverForTesting?.Invoke(step);
            };
            var result = await coordinator.RestorePreviousLibraryAsync(slot.RecoveryId, CancellationToken.None);
            await RecordRestoreResultAsync(slot, result);
        }
        catch (SelfHostedRecoveryRestoreAbandonedException)
        {
            // Only the crash-matrix tests raise this sentinel; a real process
            // death takes the in-memory slot with it and startup recovery
            // decides the generation.
            RecordRestoreRecoveryFailure(
                slot,
                MigrationActivationErrorCodes.RecoveryFailed,
                "The process was interrupted during the restore; a restart reconciles the library.");
        }
        catch (MigrationActivationException exception)
        {
            await RecordRestoreFailureAsync(slot, exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Recovery restore for {RecoveryId} failed unexpectedly", slot.RecoveryId);
            await RecordRestoreFailureAsync(
                slot,
                MigrationActivationErrorCodes.Failed,
                "The restore failed before the library switch; the current library is unchanged.");
        }
    }

    private async Task RecordRestoreResultAsync(
        SelfHostedRecoveryRestoreRunSlot slot,
        SelfHostedRecoveryRestoreResult result)
    {
        var now = _clock.GetUtcNow();
        var durable = TryReadManifest(slot.RecoveryId);
        var outcome = result.Outcome switch
        {
            SelfHostedRecoveryRestoreOutcome.Restored or SelfHostedRecoveryRestoreOutcome.AlreadyRestored =>
                MigrationActivationOutcome.Completed,
            _ when result.ErrorCode == MigrationActivationErrorCodes.RecoveryFailed =>
                MigrationActivationOutcome.RecoveryFailed,
            _ when result.ErrorCode == MigrationActivationErrorCodes.Busy =>
                MigrationActivationOutcome.Running,
            _ => MigrationActivationOutcome.Failed,
        };

        var status = durable is null
            ? new MigrationRecoveryRestoreStatusResponse(
                slot.RecoveryId, MigrationRecoveryStatus.Restoring, outcome,
                CreatedAtUtc: slot.CreatedAtUtc, ExpiresAtUtc: slot.ExpiresAtUtc,
                SizeBytes: slot.SizeBytes, Counts: slot.Counts)
            : BuildDurableRestoreStatus(durable);
        status = status with
        {
            Outcome = outcome,
            ErrorCode = outcome == MigrationActivationOutcome.Completed ? null : result.ErrorCode,
            Message = outcome == MigrationActivationOutcome.Completed
                ? null
                : MigrationHttpErrors.MessageFor(result.ErrorCode ?? MigrationActivationErrorCodes.Failed),
            MaintenanceRequired = outcome == MigrationActivationOutcome.RecoveryFailed,
            Accepted = outcome is MigrationActivationOutcome.Accepted or MigrationActivationOutcome.Running,
            CanRestore = outcome == MigrationActivationOutcome.Failed && status.CanRestore,
        };
        slot.MarkFinished(status, now);
        await Task.CompletedTask;
    }

    private async Task RecordRestoreFailureAsync(
        SelfHostedRecoveryRestoreRunSlot slot,
        string code,
        string message)
    {
        if (code == MigrationActivationErrorCodes.RecoveryFailed)
        {
            RecordRestoreRecoveryFailure(slot, code, message);
            return;
        }

        var now = _clock.GetUtcNow();
        var durable = TryReadManifest(slot.RecoveryId);
        var status = durable is null
            ? new MigrationRecoveryRestoreStatusResponse(
                slot.RecoveryId, MigrationRecoveryStatus.Restoring,
                MigrationActivationOutcome.Failed,
                ErrorCode: code, Message: message,
                CreatedAtUtc: slot.CreatedAtUtc, ExpiresAtUtc: slot.ExpiresAtUtc,
                SizeBytes: slot.SizeBytes, Counts: slot.Counts)
            : BuildDurableRestoreStatus(durable) with
            {
                Outcome = MigrationActivationOutcome.Failed,
                ErrorCode = code,
                Message = message,
                CanRestore = durable.Status == MigrationRecoveryStatus.Available,
            };
        slot.MarkFinished(status, now);
        await Task.CompletedTask;
    }

    private void RecordRestoreRecoveryFailure(
        SelfHostedRecoveryRestoreRunSlot slot,
        string code,
        string message)
    {
        var durable = TryReadManifest(slot.RecoveryId);
        var status = durable is null
            ? new MigrationRecoveryRestoreStatusResponse(
                slot.RecoveryId, MigrationRecoveryStatus.Restoring,
                MigrationActivationOutcome.RecoveryFailed,
                ErrorCode: code, Message: message, MaintenanceRequired: true,
                CreatedAtUtc: slot.CreatedAtUtc, ExpiresAtUtc: slot.ExpiresAtUtc,
                SizeBytes: slot.SizeBytes, Counts: slot.Counts)
            : BuildDurableRestoreStatus(durable) with
            {
                Outcome = MigrationActivationOutcome.RecoveryFailed,
                ErrorCode = code,
                Message = message,
                MaintenanceRequired = true,
                Accepted = false,
                CanRestore = false,
            };
        slot.MarkFinished(status, _clock.GetUtcNow());
    }

    private MigrationRecoveryRestoreStatusResponse MergeRestoreFinishedSlot(
        SelfHostedRecoveryRestoreRunSlot? slot,
        SelfHostedRecoveryManifest manifest)
    {
        var durable = BuildDurableRestoreStatus(manifest);
        if (slot is not { State: SelfHostedActivationRunState.Finished, LastStatus: not null })
        {
            return durable;
        }

        // RecoveryFailed is intentionally in-memory only: admission stays closed
        // until a restart reconciles, so the durable manifest is not authoritative.
        if (slot.LastStatus.Outcome == MigrationActivationOutcome.RecoveryFailed)
        {
            return slot.LastStatus;
        }

        if (slot.LastStatus.Outcome == MigrationActivationOutcome.Failed)
        {
            return durable with
            {
                Outcome = MigrationActivationOutcome.Failed,
                ErrorCode = slot.LastStatus.ErrorCode,
                Message = slot.LastStatus.Message,
                CanRestore = durable.Status == MigrationRecoveryStatus.Available && durable.CanRestore,
            };
        }

        if (slot.LastStatus.Outcome == MigrationActivationOutcome.Running)
        {
            // The run never started (busy gate); the durable claim stays pending.
            return durable with
            {
                Outcome = MigrationActivationOutcome.Running,
                ErrorCode = slot.LastStatus.ErrorCode,
                Message = slot.LastStatus.Message,
                Accepted = true,
                Phase = MigrationActivationPhase.Preparing,
                CanRestore = false,
            };
        }

        return durable;
    }

    private MigrationRecoveryRestoreStatusResponse BuildDurableRestoreStatus(
        SelfHostedRecoveryManifest manifest)
    {
        var now = _clock.GetUtcNow();
        var common = new MigrationRecoveryRestoreStatusResponse(
            manifest.JobId,
            manifest.Status,
            MigrationActivationOutcome.Idle,
            CreatedAtUtc: manifest.CreatedAtUtc,
            ExpiresAtUtc: manifest.ExpiresAtUtc,
            SizeBytes: manifest.TotalBytes,
            Counts: manifest.Counts);
        return manifest.Status switch
        {
            MigrationRecoveryStatus.Available when manifest.ExpiresAtUtc > now =>
                common with { CanRestore = true },
            MigrationRecoveryStatus.Available =>
                common with { ErrorCode = MigrationActivationErrorCodes.RecoveryExpired },
            MigrationRecoveryStatus.Restoring => common with
            {
                Outcome = MigrationActivationOutcome.Running,
                Accepted = true,
                Phase = MigrationActivationPhase.Preparing,
            },
            MigrationRecoveryStatus.Restored => common with
            {
                Outcome = MigrationActivationOutcome.Completed,
            },
            MigrationRecoveryStatus.Failed => common with
            {
                Outcome = MigrationActivationOutcome.Failed,
                ErrorCode = manifest.RestoreError,
                Message = manifest.RestoreError is null
                    ? null
                    : MigrationHttpErrors.MessageFor(manifest.RestoreError),
            },
            _ => common,
        };
    }

    private MigrationRecoveryRestoreStatusResponse BuildRestoreRunStatus(
        SelfHostedRecoveryRestoreRunSlot slot,
        MigrationActivationOutcome outcome) =>
        new(
            slot.RecoveryId,
            slot.DurableStatus,
            outcome,
            Phase: slot.Phase,
            Accepted: true,
            CanRestore: false,
            CreatedAtUtc: slot.CreatedAtUtc,
            ExpiresAtUtc: slot.ExpiresAtUtc,
            SizeBytes: slot.SizeBytes,
            Counts: slot.Counts);

    private SelfHostedRecoveryManifest ReadRestoreManifest(Guid recoveryId)
    {
        // A valid manifest whose status is Failed is reported as Failed by the
        // status route; only admission (the coordinator's RequireAvailable)
        // refuses it. An undecodable manifest propagates migration_recovery_corrupt.
        return _manifests.Read(recoveryId) ?? throw NotFound();
    }

    private SelfHostedRecoveryManifest? TryReadManifest(Guid recoveryId)
    {
        try
        {
            return _manifests.Read(recoveryId);
        }
        catch (MigrationActivationException exception)
            when (exception.Code == MigrationActivationErrorCodes.RecoveryCorrupt)
        {
            return null;
        }
    }

    private static void ApplyClaimSnapshot(
        SelfHostedRecoveryRestoreRunSlot slot,
        MigrationRecoveryStatusResponse claimed)
    {
        slot.DurableStatus = claimed.Status;
        slot.CreatedAtUtc = claimed.CreatedAtUtc;
        slot.ExpiresAtUtc = claimed.ExpiresAtUtc;
        slot.SizeBytes = claimed.SizeBytes;
        slot.Counts = claimed.Counts;
    }

    private static void ApplyManifestSnapshot(
        SelfHostedRecoveryRestoreRunSlot slot,
        SelfHostedRecoveryManifest manifest)
    {
        slot.DurableStatus = manifest.Status;
        slot.CreatedAtUtc = manifest.CreatedAtUtc;
        slot.ExpiresAtUtc = manifest.ExpiresAtUtc;
        slot.SizeBytes = manifest.TotalBytes;
        slot.Counts = manifest.Counts;
    }

    private void EnqueueRestore(SelfHostedRecoveryRestoreRunSlot slot)
    {
        if (!_queue.Writer.TryWrite(SelfHostedLibrarySwitchWorkItem.ForRestore(slot.RecoveryId)))
        {
            slot.MarkDropped(_clock.GetUtcNow());
            throw LibraryMaintenanceCoordinator.Busy();
        }
    }

    private void DropQueuedRestore(Guid recoveryId)
    {
        if (_restoreRegistry.Find(recoveryId) is { State: SelfHostedActivationRunState.Accepted } slot)
        {
            slot.MarkDropped(_clock.GetUtcNow());
        }
    }

    private static MigrationActivationPhase RestorePhaseForStep(string step) => step switch
    {
        SelfHostedRecoveryRestoreSteps.PhaseExclusiveEntered => MigrationActivationPhase.Activating,
        SelfHostedRecoveryRestoreSteps.PhaseCommitted => MigrationActivationPhase.Finalizing,
        SelfHostedRecoveryRestoreSteps.AfterFinalizeRestore => MigrationActivationPhase.Finalizing,
        SelfHostedRecoveryRestoreSteps.PhaseCandidatePrepared => MigrationActivationPhase.Preparing,
        _ => MigrationActivationPhase.Preparing,
    };

    private static MigrationActivationException NotFound() => new(
        MigrationActivationErrorCodes.RecoveryNotFound,
        "The retained recovery copy was not found.");
}

/// <summary>
/// One recovery copy's dispatcher state on the shared library-switch run
/// lifecycle, plus the snapshot fields the status reader serves while the live
/// database is closed.
/// </summary>
internal sealed class SelfHostedRecoveryRestoreRunSlot(Guid recoveryId)
    : SelfHostedLibrarySwitchRunSlot(recoveryId)
{
    internal Guid RecoveryId => Key;

    internal MigrationRecoveryRestoreRequest? Request { get; private set; }

    internal MigrationActivationPhase Phase { get; private set; } = MigrationActivationPhase.Queued;

    internal MigrationRecoveryRestoreStatusResponse? LastStatus { get; private set; }

    internal MigrationRecoveryStatus DurableStatus { get; set; } = MigrationRecoveryStatus.Restoring;

    internal DateTimeOffset CreatedAtUtc { get; set; }

    internal DateTimeOffset ExpiresAtUtc { get; set; }

    internal long SizeBytes { get; set; }

    internal MigrationExistingCounts? Counts { get; set; }

    internal bool TryAdmit(MigrationRecoveryRestoreRequest request)
    {
        if (!TryAdmitCore()) return false;
        Request = request;
        Phase = MigrationActivationPhase.Queued;
        LastStatus = null;
        return true;
    }

    internal bool TryAdmitResume()
    {
        if (!TryAdmitCore()) return false;
        Request = null;
        Phase = MigrationActivationPhase.Queued;
        LastStatus = null;
        return true;
    }

    internal bool TryMarkRunning()
    {
        if (!TryMarkRunningCore()) return false;
        SetPhase(MigrationActivationPhase.Preparing);
        return true;
    }

    /// <summary>Monotonic phase update: the restore never moves backwards.</summary>
    internal void SetPhase(MigrationActivationPhase phase)
    {
        if (phase > Phase) Phase = phase;
    }

    internal void MarkFinished(MigrationRecoveryRestoreStatusResponse status, DateTimeOffset now)
    {
        LastStatus = status;
        MarkFinishedCore(now);
    }

    internal void MarkDropped(DateTimeOffset now)
    {
        LastStatus = null;
        MarkDroppedCore(now);
    }
}
