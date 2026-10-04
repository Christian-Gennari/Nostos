using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services;

namespace Nostos.Backend.Providers.Acquisition;

/// <summary>
/// In-memory, bounded acquisition job store with a background consumer.
///
/// Bounded on purpose: a job that is never pruned would keep its status forever,
/// and a job store that grows without limit is a slow leak on a server that is
/// expected to run for months. Retention is by count and by age, swept whenever a
/// job is added or finishes.
///
/// In-memory means a restart forgets in-flight jobs — the UI then sees an unknown
/// job id and says so. Nothing else is lost, because the staging directory is
/// removed with the process and no library row exists until the very end.
/// </summary>
public sealed class AcquisitionJobManager : BackgroundService, IAcquisitionJobManager
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Channel<Job> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AcquisitionOptions _options;
    private readonly ILogger<AcquisitionJobManager> _logger;
    private readonly ILibraryMaintenanceCoordinator? _maintenance;

    public AcquisitionJobManager(
        IServiceScopeFactory scopeFactory,
        IOptions<AcquisitionOptions> options,
        ILogger<AcquisitionJobManager> logger,
        ILibraryMaintenanceCoordinator? maintenance = null)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _maintenance = maintenance;
        _queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(Math.Max(4, _options.MaxRetainedJobs))
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public AcquisitionJobStatus Start(AcquisitionRequest request)
    {
        Sweep();

        var job = new Job(Guid.NewGuid().ToString("N"), request);
        _jobs[job.Id] = job;

        if (!_queue.Writer.TryWrite(job))
        {
            _jobs.TryRemove(job.Id, out _);
            throw new InvalidOperationException(
                "The acquisition queue is full. Wait for the current imports to finish and try again.");
        }

        _logger.LogInformation(
            "Queued acquisition {JobId} for {Provider}/{ExternalId}.",
            job.Id, request.ProviderId, request.ExternalId);

        return job.Snapshot();
    }

    public AcquisitionJobStatus? Get(string jobId) =>
        string.IsNullOrWhiteSpace(jobId) || !_jobs.TryGetValue(jobId, out var job)
            ? null
            : job.Snapshot();

    /// <summary>
    /// A snapshot of the store, oldest first.
    ///
    /// Deliberately does NOT sweep: every mutating entry point already sweeps,
    /// and a read-only listing that removed entries would make the retention
    /// policy depend on who happened to be watching the feed.
    /// </summary>
    public IReadOnlyList<AcquisitionJobStatus> List() =>
        _jobs.Values
            .Select(job => job.Snapshot())
            .OrderBy(status => status.CreatedAt)
            .ToList();

    public IReadOnlyList<AcquisitionJobStatus> ListActive() =>
        List().Where(status => !status.IsFinished).ToList();

    public bool Cancel(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || !_jobs.TryGetValue(jobId, out var job))
            return false;

        if (job.Snapshot().IsFinished)
            return false;

        job.Cancel();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A small fixed number of workers: enough that a short ebook import is
        // not stuck behind a multi-hour audiobook, few enough that the sources
        // behind these catalogs are not hammered.
        var workers = Math.Clamp(_options.ClampTranscodeConcurrency() + 1, 1, 3);

        var running = Enumerable
            .Range(0, workers)
            .Select(_ => ConsumeAsync(stoppingToken))
            .ToArray();

        try
        {
            await Task.WhenAll(running);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ordinary shutdown: the queue readers end with the token, and that
            // is not an error worth logging as one.
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (job.Snapshot().IsFinished)
                continue;

            try
            {
                await RunJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.Fail("shutdown", "The server stopped before the import finished.");
                return;
            }
            catch (Exception ex)
            {
                // A worker must never die: one bad acquisition would otherwise
                // silently stop every later import.
                _logger.LogError(ex, "Acquisition job {JobId} crashed.", job.Id);
                job.Fail("acquisition_failed", "The import failed unexpectedly. See the server log for details.");
            }
            finally
            {
                Sweep();
            }
        }
    }

    private async Task RunJobAsync(Job job, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Token);

        job.MarkRunning();

        try
        {
            await using var operation = _maintenance is null ? null : await _maintenance.EnterOperationAsync(linked.Token);
            // The service is scoped (it uses the scoped library service and a
            // DbContext factory), so each job gets its own scope.
            using var scope = _scopeFactory.CreateScope();

            var acquisitions = scope.ServiceProvider.GetRequiredService<IAcquisitionService>();

            var progress = new InlineProgress<AcquisitionProgress>(job.MarkProgress);

            var result = await acquisitions.AcquireAsync(job.Request, progress, linked.Token);

            if (result.Outcome == AcquisitionOutcome.Failed)
                job.Fail(result.ErrorCode ?? "acquisition_failed", result.Reply);
            else
                job.Succeed(result);

            _logger.LogInformation(
                "Acquisition job {JobId} finished as {Outcome} for {Provider}/{ExternalId}.",
                job.Id, result.Outcome, job.Request.ProviderId, job.Request.ExternalId);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            job.Cancel();
            _logger.LogInformation("Acquisition job {JobId} was cancelled.", job.Id);
        }
    }

    private void Sweep()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, _options.JobRetentionMinutes));

        foreach (var (id, job) in _jobs)
        {
            if (!job.Snapshot().IsFinished || job.UpdatedAt >= cutoff)
                continue;

            if (_jobs.TryRemove(id, out var removed))
                removed.Dispose();
        }

        // Count-based bound as a backstop for a burst of jobs inside the
        // retention window: oldest finished jobs go first.
        var excess = _jobs.Count - Math.Max(1, _options.MaxRetainedJobs);
        if (excess <= 0)
            return;

        foreach (var (id, _) in _jobs
                     .Where(kv => kv.Value.Snapshot().IsFinished)
                     .OrderBy(kv => kv.Value.UpdatedAt)
                     .Take(excess))
        {
            if (_jobs.TryRemove(id, out var removed))
                removed.Dispose();
        }
    }

    /// <summary>
    /// Delivers progress on the reporter's own thread, unlike
    /// <see cref="Progress{T}"/>, which posts the callback.
    ///
    /// A posted callback can run after the acquisition has already moved on —
    /// including after the job reached a terminal state — so the stored percent
    /// would lag the work and a terminal 100 could be observed while the job is
    /// still running. Running the handler inline makes the ordering
    /// deterministic at the source instead of leaving the store's cap to hide
    /// the window.
    /// </summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>Mutable state for one job; all reads go through <see cref="Snapshot"/>.</summary>
    private sealed class Job : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly object _sync = new();

        private AcquisitionJobState _state = AcquisitionJobState.Queued;
        private string _stage = "queued";
        private int _percent;
        private string? _detail;
        private Guid? _bookId;
        private string? _errorCode;
        private string? _message;

        public Job(string id, AcquisitionRequest request)
        {
            Id = id;
            Request = request;
            CreatedAt = DateTime.UtcNow;
            UpdatedAt = CreatedAt;
        }

        public string Id { get; }
        public AcquisitionRequest Request { get; }
        public DateTime CreatedAt { get; private set; }
        public DateTime UpdatedAt { get; private set; }

        public CancellationToken Token => _cts.Token;

        public void Cancel() => _cts.Cancel();

        public void MarkRunning()
        {
            lock (_sync)
            {
                _state = AcquisitionJobState.Running;
                _stage = "starting";
                Touch();
            }
        }

        public void MarkProgress(AcquisitionProgress progress)
        {
            lock (_sync)
            {
                if (_state != AcquisitionJobState.Running)
                    return;

                _stage = progress.Stage;
                _percent = Math.Clamp(progress.Percent, 0, 100);
                _detail = progress.Detail;
                if (progress.BookId.HasValue)
                    _bookId = progress.BookId.Value;
                Touch();
            }
        }

        public void Succeed(AcquisitionResult result)
        {
            lock (_sync)
            {
                _state = AcquisitionJobState.Succeeded;
                _stage = "done";
                _percent = 100;
                _bookId = result.BookId;
                _message = result.Reply;
                _errorCode = null;
                Touch();
            }
        }

        public void Fail(string code, string message)
        {
            lock (_sync)
            {
                if (_state == AcquisitionJobState.Cancelled)
                    return;

                _state = AcquisitionJobState.Failed;
                _stage = "failed";
                _errorCode = code;
                _message = message;
                Touch();
            }
        }

        public AcquisitionJobStatus Snapshot()
        {
            lock (_sync)
            {
                return new AcquisitionJobStatus(
                    JobId: Id,
                    State: _state,
                    Stage: _stage,
                    Percent: _percent,
                    Detail: _detail,
                    ProviderId: Request.ProviderId,
                    ExternalId: Request.ExternalId,
                    AssetId: Request.AssetId,
                    BookId: _bookId,
                    ErrorCode: _errorCode,
                    Message: _message,
                    CreatedAt: CreatedAt,
                    UpdatedAt: UpdatedAt);
            }
        }

        public void Dispose()
        {
            // Only dispose a job that can no longer be running, so a live
            // acquisition never has its token source pulled out from under it.
            if (!Snapshot().IsFinished)
                return;

            _cts.Dispose();
        }

        private void Touch() => UpdatedAt = DateTime.UtcNow;
    }
}
