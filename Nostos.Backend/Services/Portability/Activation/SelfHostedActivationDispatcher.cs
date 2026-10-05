using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability.Migration;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// SelfHosted activation driver behind the HTTP API (#681, Slice 8).
///
/// <para><b>Exactly one run per job.</b> Admission runs under a short shared
/// maintenance lease, so the activating worker can never wait for a lease this
/// route still holds. An accepted job is handed to one in-process background
/// run; duplicate calls observe that run instead of starting a second one, and
/// across hosts the coordinator's durable job lease still guarantees a single
/// cutover. A crash before the run writes the journal leaves the job
/// <c>ReadyToActivate</c>; repeating the request resumes it.</para>
///
/// <para><b>Status while the live database is closed.</b> Every accepted run
/// publishes an in-memory snapshot. When exclusive maintenance closes admission
/// the status reader serves only that snapshot: the live database, its WAL and
/// its connection pools are never opened inside the swap window. After a
/// committed-but-unfinalized failure the snapshot reports
/// <c>migration_activation_recovery_failed</c> with the operator-facing
/// fail-closed message, because admission stays closed until a restart
/// reconciles the journal.</para>
/// </summary>
internal sealed class SelfHostedActivationDispatcher : BackgroundService, IMigrationActivationDispatcher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly LibraryMaintenanceCoordinator _maintenance;
    private readonly ILogger<SelfHostedActivationDispatcher> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, ActivationRun> _runs = new();
    private readonly ConcurrentDictionary<Guid, MigrationActivationStatusResponse> _statuses = new();

    public SelfHostedActivationDispatcher(
        IServiceScopeFactory scopes,
        LibraryMaintenanceCoordinator maintenance,
        ILogger<SelfHostedActivationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _maintenance = maintenance;
        _logger = logger;
    }

    /// <summary>Test seam: how many runs this host actually started.</summary>
    internal int StartedRunCount => Volatile.Read(ref _started);

    /// <summary>
    /// Test seam: observes the coordinator instance before a run starts, so a
    /// test can gate a cutover boundary through
    /// <see cref="SelfHostedActivationCoordinator.StepObserverForTesting"/>.
    /// </summary>
    internal Action<SelfHostedActivationCoordinator>? CoordinatorCreatedForTesting { get; set; }

    public async Task<MigrationActivationRequestResult> RequestAsync(
        Guid jobId,
        MigrationActivateRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (jobId == Guid.Empty)
        {
            return new MigrationActivationRequestResult(MigrationActivationRequestOutcome.NotFound);
        }

        // The route owns no ambient lease: admission takes a brief shared lease
        // for the durable read, then releases it before the background run may
        // request the exclusive one. With maintenance closed, only the
        // in-memory snapshot can answer.
        using var operation = _maintenance.TryEnterOperation();
        if (operation is null)
        {
            return _statuses.TryGetValue(jobId, out var snapshot)
                ? new MigrationActivationRequestResult(
                    MigrationActivationRequestOutcome.Replayed, snapshot)
                : new MigrationActivationRequestResult(MigrationActivationRequestOutcome.Busy);
        }

        await using var scope = _scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
        var job = await store.GetAsync(jobId, ct);
        if (job is null)
        {
            return new MigrationActivationRequestResult(MigrationActivationRequestOutcome.NotFound);
        }

        if (job.Direction != MigrationDirection.Import)
        {
            return new MigrationActivationRequestResult(
                MigrationActivationRequestOutcome.InvalidState,
                Message: "Only a prepared import can be activated.");
        }

        if (job.State == MigrationJobState.Completed)
        {
            return new MigrationActivationRequestResult(
                MigrationActivationRequestOutcome.Accepted,
                await BuildStatusFromJobAsync(scope.ServiceProvider, job, null, ct));
        }

        if (MigrationJobTransitions.IsTerminal(job.State))
        {
            return new MigrationActivationRequestResult(
                MigrationActivationRequestOutcome.InvalidState,
                Message: $"The migration job is {job.State} and cannot be activated.");
        }

        if (job.State is not (MigrationJobState.ReadyToActivate or MigrationJobState.Activating))
        {
            return new MigrationActivationRequestResult(
                MigrationActivationRequestOutcome.InvalidState,
                Message: "Only a prepared import can be activated.");
        }

        var record = await scope.ServiceProvider.GetRequiredService<NostosDbContext>()
            .MigrationJobRecords.AsNoTracking()
            .SingleAsync(row => row.Id == jobId, ct);
        if (record.PreparedStagingId is not { } stagingId || stagingId == Guid.Empty)
        {
            return new MigrationActivationRequestResult(
                MigrationActivationRequestOutcome.Failed,
                Message: "The job has no prepared import staging area.");
        }

        // The same authoritative reader the coordinator admits with, so this
        // synchronous answer can never disagree with the later cutover check.
        var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedActivationCoordinator>();
        var facts = await coordinator.ReadDestinationFactsForAdmissionAsync(ct);

        if (job.State == MigrationJobState.ReadyToActivate)
        {
            try
            {
                MigrationActivationAdmission.Validate(
                    MigrationDirection.Import,
                    job.State,
                    record.DestinationRevision ?? string.Empty,
                    request,
                    facts.Status,
                    facts.Revision);
            }
            catch (MigrationActivationException exception)
                when (exception.Code is MigrationActivationErrorCodes.ConfirmationRequired
                    or MigrationActivationErrorCodes.DestinationConflict)
            {
                return new MigrationActivationRequestResult(
                    exception.Code == MigrationActivationErrorCodes.ConfirmationRequired
                        ? MigrationActivationRequestOutcome.ConfirmationRequired
                        : MigrationActivationRequestOutcome.DestinationConflict,
                    Message: exception.Message,
                    DestinationRevision: facts.Revision,
                    ExistingCounts: facts.Counts,
                    DestinationStatus: facts.Status);
            }
        }

        var started = TryStartRun(jobId, request);
        var status = new MigrationActivationStatusResponse(
            jobId,
            job.State,
            MigrationActivationOutcome.Running,
            DestinationRevision: record.DestinationRevision,
            ExistingCounts: facts.Counts,
            DestinationStatus: facts.Status);
        var current = _statuses.AddOrUpdate(jobId, status, (_, existing) => existing.Outcome == MigrationActivationOutcome.Running
            ? existing
            : status);
        return new MigrationActivationRequestResult(
            started ? MigrationActivationRequestOutcome.Accepted : MigrationActivationRequestOutcome.Replayed,
            current);
    }

    public async Task<MigrationActivationStatusResponse> GetStatusAsync(Guid jobId, CancellationToken ct)
    {
        if (jobId == Guid.Empty) throw MigrationJobStoreException.NotFound(jobId);

        using var operation = _maintenance.TryEnterOperation();
        if (operation is null)
        {
            return _statuses.TryGetValue(jobId, out var snapshot)
                ? snapshot
                : throw LibraryMaintenanceCoordinator.Busy();
        }

        await using var scope = _scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
        var job = await store.GetAsync(jobId, ct);
        if (job is null || job.Direction != MigrationDirection.Import)
        {
            throw MigrationJobStoreException.NotFound(jobId);
        }

        var status = await BuildStatusFromJobAsync(scope.ServiceProvider, job, null, ct);
        if (_statuses.TryGetValue(jobId, out var run) && run.ErrorCode is not null
            && job.State == MigrationJobState.Failed)
        {
            // The rollback path marks the row Failed without persisting the
            // coordinator's typed reason; the in-process run still has it.
            status = status with
            {
                Outcome = MigrationActivationOutcome.Failed,
                ErrorCode = run.ErrorCode,
                Message = run.Message,
            };
        }

        return status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            while (_queue.Reader.TryRead(out var jobId))
            {
                if (!_runs.TryGetValue(jobId, out var run)) continue;
                try
                {
                    await RunActivationAsync(run);
                }
                catch (Exception exception)
                {
                    // The pump must survive one run's unexpected failure; the
                    // coordinator already rolled the library back or failed
                    // closed, and a later request can observe that state.
                    _logger.LogError(exception, "Activation run for migration job {JobId} crashed", jobId);
                    _runs.TryRemove(jobId, out _);
                }
            }
        }
    }

    private async Task RunActivationAsync(ActivationRun run)
    {
        var jobId = run.JobId;
        _statuses.AddOrUpdate(
            jobId,
            new MigrationActivationStatusResponse(jobId, MigrationJobState.Activating, MigrationActivationOutcome.Running),
            (_, existing) => existing with { State = MigrationJobState.Activating, Outcome = MigrationActivationOutcome.Running });
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedActivationCoordinator>();
            CoordinatorCreatedForTesting?.Invoke(coordinator);
            var result = await coordinator.ActivateAsync(jobId, run.Request, CancellationToken.None);
            await RecordResultAsync(run, result);
        }
        catch (SelfHostedActivationAbandonedException)
        {
            // Only the crash-matrix tests raise this sentinel; a real process
            // death takes the in-memory snapshot with it and startup recovery
            // decides the generation.
            RecordRecoveryFailure(run, MigrationActivationErrorCodes.RecoveryFailed,
                "The process was interrupted during activation; a restart reconciles the library.");
        }
        catch (MigrationActivationException exception)
        {
            await RecordFailureAsync(run, exception.Code, exception.Message);
        }
        catch (MigrationJobStoreException exception)
        {
            await RecordFailureAsync(run, exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Activation of migration job {JobId} failed unexpectedly", jobId);
            await RecordFailureAsync(run, MigrationActivationErrorCodes.Failed,
                "The activation failed before the library switch; the original library is unchanged.");
        }
        finally
        {
            _runs.TryRemove(jobId, out _);
        }
    }

    private async Task RecordResultAsync(ActivationRun run, SelfHostedActivationResult result)
    {
        using var operation = _maintenance.TryEnterOperation();
        if (operation is not null)
        {
            await using var scope = _scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
            var job = await store.GetAsync(run.JobId, CancellationToken.None);
            if (job is not null)
            {
                _statuses[run.JobId] = await BuildStatusFromJobAsync(
                    scope.ServiceProvider, job, result, CancellationToken.None);
                return;
            }
        }

        _statuses[run.JobId] = new MigrationActivationStatusResponse(
            run.JobId,
            result.State,
            result.Outcome == SelfHostedActivationOutcome.InProgress
                ? MigrationActivationOutcome.Running
                : MigrationActivationOutcome.Completed,
            RecoveryAvailable: result.RecoveryStatus == MigrationRecoveryStatus.Available);
    }

    private async Task RecordFailureAsync(ActivationRun run, string code, string message)
    {
        if (code == MigrationActivationErrorCodes.RecoveryFailed)
        {
            RecordRecoveryFailure(run, code, message);
            return;
        }

        if (code == MigrationJobStoreErrorCodes.LeaseConflict)
        {
            // Another host owns the job lease; this run never cut over and the
            // owner (or a later request) decides the outcome.
            _statuses[run.JobId] = new MigrationActivationStatusResponse(
                run.JobId, MigrationJobState.Activating, MigrationActivationOutcome.Running);
            return;
        }

        using var operation = _maintenance.TryEnterOperation();
        if (operation is not null)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
                var job = await store.GetAsync(run.JobId, CancellationToken.None);
                if (job is not null && job.State == MigrationJobState.Failed)
                {
                    var status = await BuildStatusFromJobAsync(scope.ServiceProvider, job, null, CancellationToken.None);
                    _statuses[run.JobId] = status with
                    {
                        Outcome = MigrationActivationOutcome.Failed,
                        ErrorCode = code,
                        Message = message,
                    };
                    return;
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not read the activation failure status for {JobId}", run.JobId);
            }
        }

        _statuses[run.JobId] = new MigrationActivationStatusResponse(
            run.JobId,
            MigrationJobState.ReadyToActivate,
            MigrationActivationOutcome.Failed,
            ErrorCode: code,
            Message: message);
    }

    private void RecordRecoveryFailure(ActivationRun run, string code, string message)
    {
        _statuses[run.JobId] = new MigrationActivationStatusResponse(
            run.JobId,
            MigrationJobState.Activating,
            MigrationActivationOutcome.RecoveryFailed,
            ErrorCode: code,
            Message: message,
            MaintenanceRequired: true);
    }

    private async Task<MigrationActivationStatusResponse> BuildStatusFromJobAsync(
        IServiceProvider services,
        MigrationJob job,
        SelfHostedActivationResult? result,
        CancellationToken ct)
    {
        var record = await services.GetRequiredService<NostosDbContext>()
            .MigrationJobRecords.AsNoTracking()
            .SingleAsync(row => row.Id == job.Id, ct);
        var outcome = job.State switch
        {
            MigrationJobState.Completed => MigrationActivationOutcome.Completed,
            MigrationJobState.Activating => MigrationActivationOutcome.Running,
            MigrationJobState.Failed => MigrationActivationOutcome.Failed,
            _ => MigrationActivationOutcome.Idle,
        };

        var recoveryStatus = result?.RecoveryStatus
            ?? (MigrationRecoveryStatus)record.RecoveryStatus;
        var recoveryAvailable = recoveryStatus == MigrationRecoveryStatus.Available;
        DateTimeOffset? expiresAt = null;
        long? sizeBytes = null;
        if (recoveryAvailable)
        {
            try
            {
                var manifest = await services.GetRequiredService<SelfHostedMigrationRecoveryService>()
                    .GetAsync(job.Id, ct);
                if (manifest is not null)
                {
                    expiresAt = manifest.ExpiresAtUtc;
                    sizeBytes = manifest.SizeBytes;
                }
            }
            catch (MigrationActivationException exception)
                when (exception.Code == MigrationActivationErrorCodes.RecoveryCorrupt)
            {
                // Status stays available; the recovery list/restore surface owns
                // reporting a corrupt retained copy.
            }
        }

        return new MigrationActivationStatusResponse(
            job.Id,
            job.State,
            outcome,
            ErrorCode: job.FailureCode,
            Message: job.FailureMessage,
            DestinationRevision: record.DestinationRevision,
            RecoveryAvailable: recoveryAvailable,
            RecoveryExpiresAtUtc: expiresAt,
            RecoverySizeBytes: sizeBytes);
    }

    private int _started;

    private bool TryStartRun(Guid jobId, MigrationActivateRequest request)
    {
        if (!_runs.TryAdd(jobId, new ActivationRun(jobId, request))) return false;
        Interlocked.Increment(ref _started);
        _queue.Writer.TryWrite(jobId);
        return true;
    }

    private sealed record ActivationRun(Guid JobId, MigrationActivateRequest Request);
}
