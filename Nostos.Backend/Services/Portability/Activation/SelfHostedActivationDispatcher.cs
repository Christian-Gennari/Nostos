using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Portability.Migration;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>Atomic lifecycle of one job's dispatcher run.</summary>
internal enum SelfHostedActivationRunState
{
    None = 0,
    Accepted = 1,
    Running = 2,
    Finished = 3,
}

/// <summary>
/// One job's dispatcher state. Every lifecycle transition is a compare-and-swap
/// so concurrent admission and worker completion can never both claim the job:
/// exactly one accepted run exists, its terminal outcome is retained for a
/// bounded time, and a new POST is only admitted after re-validating the
/// durable job state.
/// </summary>
internal sealed class SelfHostedActivationRunSlot(Guid jobId)
{
    private int _state;

    internal Guid JobId { get; } = jobId;

    internal MigrationActivateRequest? Request { get; private set; }

    internal SelfHostedActivationRunState State =>
        (SelfHostedActivationRunState)Volatile.Read(ref _state);

    internal MigrationActivationPhase Phase { get; private set; } = MigrationActivationPhase.Queued;

    internal MigrationActivationStatusResponse? LastStatus { get; private set; }

    internal DateTimeOffset FinishedAtUtc { get; private set; }

    internal string? DestinationRevision { get; set; }

    internal MigrationExistingCounts? ExistingCounts { get; set; }

    internal MigrationDestinationStatus? DestinationStatus { get; set; }

    /// <summary>Claims the job for a new run unless one is already accepted/running.</summary>
    internal bool TryAdmit(MigrationActivateRequest request)
    {
        while (true)
        {
            var current = State;
            if (current is SelfHostedActivationRunState.Accepted or SelfHostedActivationRunState.Running)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)SelfHostedActivationRunState.Accepted,
                    (int)current) == (int)current)
            {
                Request = request;
                Phase = MigrationActivationPhase.Queued;
                LastStatus = null;
                return true;
            }
        }
    }

    internal bool TryMarkRunning()
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)SelfHostedActivationRunState.Running,
                (int)SelfHostedActivationRunState.Accepted) != (int)SelfHostedActivationRunState.Accepted)
        {
            return false;
        }

        SetPhase(MigrationActivationPhase.Preparing);
        return true;
    }

    /// <summary>Monotonic phase update: the cutover never moves backwards.</summary>
    internal void SetPhase(MigrationActivationPhase phase)
    {
        if (phase > Phase) Phase = phase;
    }

    internal void MarkFinished(MigrationActivationStatusResponse status, DateTimeOffset now)
    {
        LastStatus = status;
        FinishedAtUtc = now;
        Volatile.Write(ref _state, (int)SelfHostedActivationRunState.Finished);
    }

    /// <summary>A queued run dropped at shutdown: no run remains and the durable job still decides.</summary>
    internal void MarkDropped(DateTimeOffset now)
    {
        LastStatus = null;
        FinishedAtUtc = now;
        Volatile.Write(ref _state, (int)SelfHostedActivationRunState.Finished);
    }
}

/// <summary>
/// Bounded per-job run registry. Finished slots are retained for a TTL so a
/// late duplicate POST can replay the outcome instead of starting a second run;
/// they are pruned afterwards, at which point the durable job row is the only
/// truth.
/// </summary>
internal sealed class SelfHostedActivationRunRegistry
{
    private readonly ConcurrentDictionary<Guid, SelfHostedActivationRunSlot> _slots = new();

    internal SelfHostedActivationRunSlot GetOrAdd(Guid jobId) =>
        _slots.GetOrAdd(jobId, id => new SelfHostedActivationRunSlot(id));

    internal SelfHostedActivationRunSlot? Find(Guid jobId) =>
        _slots.TryGetValue(jobId, out var slot) ? slot : null;

    internal void Prune(DateTimeOffset now, TimeSpan ttl)
    {
        foreach (var pair in _slots)
        {
            if (pair.Value.State == SelfHostedActivationRunState.Finished
                && pair.Value.FinishedAtUtc < now - ttl)
            {
                _slots.TryRemove(pair.Key, out _);
            }
        }
    }
}

/// <summary>
/// SelfHosted activation driver behind the HTTP API (#681, Slice 8).
///
/// <para><b>Exactly one run per job.</b> Admission takes a short shared
/// maintenance lease and a per-job admission stripe, so the activating worker
/// can never wait for a lease this route still holds and two stale concurrent
/// requests cannot both create a run. The per-job slot lifecycle is a
/// compare-and-swap state machine (None → Accepted → Running → Finished); a
/// finished slot is retained for a bounded TTL, and a new POST is admitted
/// only after re-validating the durable job state (completed replays without a
/// run, a rolled-back job allows a retry, fail-closed is refused). Across hosts
/// the coordinator's durable job lease still guarantees one cutover.</para>
///
/// <para><b>Status while the live database is closed.</b> The run registry is
/// consulted before the durable row, so the endpoint never reports Idle while
/// a run exists — including during the long pre-maintenance Phase A. When
/// exclusive maintenance closes admission the status reader serves only the
/// in-memory slot: the live database, its WAL and its connection pools are
/// never opened inside the swap window. After a committed-but-unfinalized
/// failure the slot reports <c>migration_activation_recovery_failed</c> with
/// the operator-facing fail-closed message.</para>
///
/// <para><b>Lost acceptance.</b> Acceptance is in-memory by design. A host
/// restart before the durable <c>Activating</c> transition drops the run and
/// leaves the job <c>ReadyToActivate</c>; the status then reports
/// <c>Idle</c> + <c>canActivate</c> so the browser repeats the POST. On
/// graceful shutdown the dispatcher stops accepting (503 busy), drops queued
/// runs, and lets an in-flight coordinator run finish or fail as the
/// coordinator's own crash safety dictates.</para>
/// </summary>
internal sealed class SelfHostedActivationDispatcher : BackgroundService, IMigrationActivationDispatcher
{
    internal static readonly TimeSpan FinishedSlotTtl = TimeSpan.FromMinutes(15);

    private const int AdmissionStripes = 32;

    private readonly IServiceScopeFactory _scopes;
    private readonly LibraryMaintenanceCoordinator _maintenance;
    private readonly TimeProvider _clock;
    private readonly ILogger<SelfHostedActivationDispatcher> _logger;
    private readonly SelfHostedActivationRunRegistry _registry = new();
    private readonly SemaphoreSlim[] _admissionStripes =
        Enumerable.Range(0, AdmissionStripes).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true });
    private volatile bool _stopping;
    private int _started;

    public SelfHostedActivationDispatcher(
        IServiceScopeFactory scopes,
        LibraryMaintenanceCoordinator maintenance,
        TimeProvider clock,
        ILogger<SelfHostedActivationDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _maintenance = maintenance;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Test seam: how many runs actually reached the coordinator.</summary>
    internal int StartedRunCount => Volatile.Read(ref _started);

    /// <summary>Test seam: observes the coordinator instance before a run starts.</summary>
    internal Action<SelfHostedActivationCoordinator>? CoordinatorCreatedForTesting { get; set; }

    /// <summary>
    /// Test seam: awaited after a run is dequeued and before it starts. It
    /// receives the hosted-service stopping token, so a restart/shutdown while
    /// the run is gated drops it exactly like a queued run.
    /// </summary>
    internal Func<Guid, CancellationToken, Task>? BeforeRunForTesting { get; set; }

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

        if (_stopping)
        {
            return new MigrationActivationRequestResult(MigrationActivationRequestOutcome.Busy);
        }

        // The route owns no ambient lease: admission takes a brief shared lease
        // for the durable read, then releases it before the background run may
        // request the exclusive one. With maintenance closed, only the
        // in-memory run slot can answer.
        using var operation = _maintenance.TryEnterOperation();
        if (operation is null)
        {
            return ReplayOrBusy(jobId);
        }

        var stripe = StripeFor(jobId);
        await stripe.WaitAsync(ct);
        try
        {
            if (_stopping)
            {
                return new MigrationActivationRequestResult(MigrationActivationRequestOutcome.Busy);
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

            // Serialized per job by the stripe: once the slot is accepted/running
            // every later request replays it, even if the first run already
            // finished, because the slot survives until its TTL.
            var slot = _registry.GetOrAdd(jobId);
            if (slot.State is SelfHostedActivationRunState.Accepted or SelfHostedActivationRunState.Running)
            {
                return new MigrationActivationRequestResult(
                    MigrationActivationRequestOutcome.Replayed,
                    BuildRunStatus(slot, job.State));
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

            if (!slot.TryAdmit(request))
            {
                return new MigrationActivationRequestResult(
                    MigrationActivationRequestOutcome.Replayed,
                    BuildRunStatus(slot, job.State));
            }

            slot.DestinationRevision = record.DestinationRevision;
            slot.ExistingCounts = facts.Counts;
            slot.DestinationStatus = facts.Status;
            if (!_queue.Writer.TryWrite(jobId))
            {
                slot.MarkDropped(_clock.GetUtcNow());
                return new MigrationActivationRequestResult(MigrationActivationRequestOutcome.Busy);
            }

            return new MigrationActivationRequestResult(
                MigrationActivationRequestOutcome.Accepted,
                BuildRunStatus(slot, job.State));
        }
        finally
        {
            stripe.Release();
        }
    }

    public async Task<MigrationActivationStatusResponse> GetStatusAsync(Guid jobId, CancellationToken ct)
    {
        if (jobId == Guid.Empty) throw MigrationJobStoreException.NotFound(jobId);
        _registry.Prune(_clock.GetUtcNow(), FinishedSlotTtl);
        var slot = _registry.Find(jobId);

        // The run registry is authoritative before the durable row: a run in
        // Phase A must never be reported Idle just because the job row has not
        // durably transitioned yet.
        if (slot is { State: SelfHostedActivationRunState.Accepted })
        {
            return BuildRunStatus(slot, MigrationJobState.ReadyToActivate);
        }

        if (slot is { State: SelfHostedActivationRunState.Running })
        {
            using var runningOperation = _maintenance.TryEnterOperation();
            if (runningOperation is not null)
            {
                await using var scope = _scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
                var job = await store.GetAsync(jobId, ct);
                if (job is not null && job.Direction == MigrationDirection.Import)
                {
                    return BuildRunStatus(slot, job.State);
                }
            }

            return BuildRunStatus(
                slot,
                slot.Phase >= MigrationActivationPhase.Activating
                    ? MigrationJobState.Activating
                    : MigrationJobState.ReadyToActivate);
        }

        using var operation = _maintenance.TryEnterOperation();
        if (operation is not null)
        {
            await using var scope = _scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
            var job = await store.GetAsync(jobId, ct);
            if (job is null || job.Direction != MigrationDirection.Import)
            {
                throw MigrationJobStoreException.NotFound(jobId);
            }

            var status = await BuildStatusFromJobAsync(scope.ServiceProvider, job, null, ct);
            return MergeFinishedSlot(slot, job, status);
        }

        // Admission closed: the in-memory slot is the only truth.
        if (slot is { State: SelfHostedActivationRunState.Finished, LastStatus: not null })
        {
            return slot.LastStatus;
        }

        throw LibraryMaintenanceCoordinator.Busy();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_queue.Reader.TryRead(out var jobId))
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        DropQueued(jobId);
                        continue;
                    }

                    var slot = _registry.Find(jobId);
                    if (slot is null || slot.State != SelfHostedActivationRunState.Accepted)
                    {
                        continue;
                    }

                    try
                    {
                        await RunActivationAsync(slot, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        slot.MarkDropped(_clock.GetUtcNow());
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Activation run for migration job {JobId} crashed", jobId);
                        slot.MarkDropped(_clock.GetUtcNow());
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown: queued runs were already dropped by the inner check.
        }
    }

    private void DropQueued(Guid jobId)
    {
        if (_registry.Find(jobId) is { State: SelfHostedActivationRunState.Accepted } slot)
        {
            slot.MarkDropped(_clock.GetUtcNow());
        }
    }

    private async Task RunActivationAsync(SelfHostedActivationRunSlot slot, CancellationToken stoppingToken)
    {
        if (BeforeRunForTesting is { } hook)
        {
            await hook(slot.JobId, stoppingToken);
        }

        stoppingToken.ThrowIfCancellationRequested();
        if (!slot.TryMarkRunning())
        {
            return;
        }

        Interlocked.Increment(ref _started);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedActivationCoordinator>();
            CoordinatorCreatedForTesting?.Invoke(coordinator);
            coordinator.CutoverStepObserver = step => slot.SetPhase(PhaseForStep(step));
            var result = await coordinator.ActivateAsync(slot.JobId, slot.Request!, CancellationToken.None);
            await RecordResultAsync(slot, result);
        }
        catch (SelfHostedActivationAbandonedException)
        {
            // Only the crash-matrix tests raise this sentinel; a real process
            // death takes the in-memory slot with it and startup recovery
            // decides the generation.
            RecordRecoveryFailure(slot, MigrationActivationErrorCodes.RecoveryFailed,
                "The process was interrupted during activation; a restart reconciles the library.");
        }
        catch (MigrationActivationException exception)
        {
            await RecordFailureAsync(slot, exception.Code, exception.Message);
        }
        catch (MigrationJobStoreException exception)
        {
            await RecordFailureAsync(slot, exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Activation of migration job {JobId} failed unexpectedly", slot.JobId);
            await RecordFailureAsync(slot, MigrationActivationErrorCodes.Failed,
                "The activation failed before the library switch; the original library is unchanged.");
        }
    }

    private MigrationActivationRequestResult ReplayOrBusy(Guid jobId)
    {
        var slot = _registry.Find(jobId);
        if (slot is { State: SelfHostedActivationRunState.Accepted or SelfHostedActivationRunState.Running })
        {
            return new MigrationActivationRequestResult(
                MigrationActivationRequestOutcome.Replayed,
                BuildRunStatus(
                    slot,
                    slot.Phase >= MigrationActivationPhase.Activating
                        ? MigrationJobState.Activating
                        : MigrationJobState.ReadyToActivate));
        }

        if (slot is { State: SelfHostedActivationRunState.Finished, LastStatus: not null })
        {
            return new MigrationActivationRequestResult(
                MigrationActivationRequestOutcome.Replayed, slot.LastStatus);
        }

        return new MigrationActivationRequestResult(MigrationActivationRequestOutcome.Busy);
    }

    private async Task RecordResultAsync(
        SelfHostedActivationRunSlot slot,
        SelfHostedActivationResult result)
    {
        var now = _clock.GetUtcNow();
        using var operation = _maintenance.TryEnterOperation();
        if (operation is not null)
        {
            await using var scope = _scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
            var job = await store.GetAsync(slot.JobId, CancellationToken.None);
            if (job is not null)
            {
                slot.MarkFinished(
                    await BuildStatusFromJobAsync(scope.ServiceProvider, job, result, CancellationToken.None),
                    now);
                return;
            }
        }

        var running = result.Outcome == SelfHostedActivationOutcome.InProgress;
        slot.MarkFinished(
            new MigrationActivationStatusResponse(
                slot.JobId,
                result.State,
                running ? MigrationActivationOutcome.Running : MigrationActivationOutcome.Completed,
                Phase: running ? MigrationActivationPhase.Activating : null,
                Accepted: running,
                RecoveryAvailable: result.RecoveryStatus == MigrationRecoveryStatus.Available),
            now);
    }

    private async Task RecordFailureAsync(
        SelfHostedActivationRunSlot slot,
        string code,
        string message)
    {
        var now = _clock.GetUtcNow();
        if (code == MigrationActivationErrorCodes.RecoveryFailed)
        {
            RecordRecoveryFailure(slot, code, message);
            return;
        }

        if (code == MigrationJobStoreErrorCodes.LeaseConflict)
        {
            // Another host owns the job lease; this run never cut over and the
            // owner (or a later request) decides the outcome.
            slot.MarkFinished(
                new MigrationActivationStatusResponse(
                    slot.JobId,
                    MigrationJobState.Activating,
                    MigrationActivationOutcome.Running,
                    Phase: MigrationActivationPhase.Activating,
                    Accepted: true),
                now);
            return;
        }

        using var operation = _maintenance.TryEnterOperation();
        if (operation is not null)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IMigrationJobStore>();
                var job = await store.GetAsync(slot.JobId, CancellationToken.None);
                if (job is not null)
                {
                    var status = await BuildStatusFromJobAsync(
                        scope.ServiceProvider, job, null, CancellationToken.None);
                    slot.MarkFinished(
                        status with
                        {
                            Outcome = MigrationActivationOutcome.Failed,
                            ErrorCode = code,
                            Message = message,
                            CanActivate = job.State == MigrationJobState.ReadyToActivate,
                        },
                        now);
                    return;
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not read the activation failure status for {JobId}", slot.JobId);
            }
        }

        slot.MarkFinished(
            new MigrationActivationStatusResponse(
                slot.JobId,
                MigrationJobState.ReadyToActivate,
                MigrationActivationOutcome.Failed,
                ErrorCode: code,
                Message: message,
                CanActivate: true),
            now);
    }

    private void RecordRecoveryFailure(SelfHostedActivationRunSlot slot, string code, string message)
    {
        slot.MarkFinished(
            new MigrationActivationStatusResponse(
                slot.JobId,
                MigrationJobState.Activating,
                MigrationActivationOutcome.RecoveryFailed,
                ErrorCode: code,
                Message: message,
                MaintenanceRequired: true),
            _clock.GetUtcNow());
    }

    private MigrationActivationStatusResponse MergeFinishedSlot(
        SelfHostedActivationRunSlot? slot,
        MigrationJob job,
        MigrationActivationStatusResponse durable)
    {
        if (slot is not { State: SelfHostedActivationRunState.Finished, LastStatus: not null })
        {
            return durable;
        }

        // RecoveryFailed is intentionally in-memory only: admission stays closed
        // until a restart reconciles, so the durable row is not authoritative.
        if (slot.LastStatus.Outcome == MigrationActivationOutcome.RecoveryFailed)
        {
            return slot.LastStatus;
        }

        if (job.State == MigrationJobState.Failed && slot.LastStatus.ErrorCode is not null)
        {
            return durable with
            {
                Outcome = MigrationActivationOutcome.Failed,
                ErrorCode = slot.LastStatus.ErrorCode,
                Message = slot.LastStatus.Message,
                CanActivate = false,
            };
        }

        if (job.State == MigrationJobState.ReadyToActivate
            && slot.LastStatus.Outcome == MigrationActivationOutcome.Failed)
        {
            // The run failed before the durable Activating transition; the job
            // is unchanged and the browser may repeat the activation.
            return durable with
            {
                Outcome = MigrationActivationOutcome.Failed,
                ErrorCode = slot.LastStatus.ErrorCode,
                Message = slot.LastStatus.Message,
                CanActivate = true,
            };
        }

        return durable;
    }

    private MigrationActivationStatusResponse BuildRunStatus(
        SelfHostedActivationRunSlot slot,
        MigrationJobState state) =>
        new(
            slot.JobId,
            state,
            slot.Phase == MigrationActivationPhase.Queued
                ? MigrationActivationOutcome.Accepted
                : MigrationActivationOutcome.Running,
            Phase: slot.Phase,
            Accepted: true,
            DestinationRevision: slot.DestinationRevision,
            ExistingCounts: slot.ExistingCounts,
            DestinationStatus: slot.DestinationStatus);

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
            RecoverySizeBytes: sizeBytes,
            Phase: job.State == MigrationJobState.Activating
                ? MigrationActivationPhase.Activating
                : null,
            Accepted: job.State == MigrationJobState.Activating,
            CanActivate: job.State == MigrationJobState.ReadyToActivate);
    }

    private SemaphoreSlim StripeFor(Guid jobId)
    {
        var bytes = jobId.ToByteArray();
        return _admissionStripes[bytes[0] % AdmissionStripes];
    }

    private static MigrationActivationPhase PhaseForStep(string step) => step switch
    {
        SelfHostedActivationSteps.PhaseExclusiveEntered => MigrationActivationPhase.Activating,
        SelfHostedActivationSteps.PhaseCommitted => MigrationActivationPhase.Finalizing,
        SelfHostedActivationSteps.AfterJournalResolved => MigrationActivationPhase.Finalizing,
        _ => MigrationActivationPhase.Preparing,
    };
}
