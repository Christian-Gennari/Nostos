using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Exactly-once admission and out-of-request execution for recovery restores.
/// One process-wide gate per recovery copy serializes the durable
/// <c>Available -&gt; Restoring</c> claim and the cutover itself; the durable
/// manifest status is the restartable claim of record. The endpoint claims and
/// starts; a restart's <see cref="SelfHostedRecoveryRestoreProcessor"/> scans
/// for <c>Restoring</c> copies and resumes them, so closing the browser (or
/// losing the process) never abandons an accepted restore.
/// </summary>
internal sealed class SelfHostedRecoveryRestoreRunner(
    IServiceScopeFactory scopes,
    SelfHostedRecoveryManifestStore manifests,
    ILogger<SelfHostedRecoveryRestoreRunner> logger)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    /// <summary>Test seam: passed to the scoped coordinator for crash-matrix runs.</summary>
    internal Action<string>? StepObserverForTesting { get; set; }

    /// <summary>Serializes admission/claim and execution for one recovery copy.</summary>
    internal async Task<T> WithGateAsync<T>(
        Guid recoveryId,
        Func<Task<T>> action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        var gate = _gates.GetOrAdd(recoveryId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Runs (or resumes) one claimed restore to a terminal outcome.</summary>
    internal Task<SelfHostedRecoveryRestoreResult> RunAsync(Guid recoveryId, CancellationToken ct) =>
        WithGateAsync(recoveryId, async () =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var coordinator = scope.ServiceProvider
                .GetRequiredService<SelfHostedRecoveryRestoreCoordinator>();
            coordinator.StepObserverForTesting = StepObserverForTesting;
            return await coordinator.RestorePreviousLibraryAsync(recoveryId, ct).ConfigureAwait(false);
        }, ct);

    /// <summary>Starts a claimed restore detached from the caller's request.</summary>
    internal void Start(Guid recoveryId)
    {
        if (_running.ContainsKey(recoveryId)) return;
        var task = Task.Run(() => RunAsync(recoveryId, CancellationToken.None));
        if (!_running.TryAdd(recoveryId, task)) return;
        _ = task.ContinueWith(
            completed =>
            {
                _running.TryRemove(recoveryId, out _);
                if (completed.IsFaulted)
                {
                    logger.LogError(completed.Exception,
                        "A detached recovery restore failed; its durable state describes the outcome.");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>The tracked detached task for a copy, if any (test seam).</summary>
    internal Task? RunningTask(Guid recoveryId) =>
        _running.TryGetValue(recoveryId, out var task) ? task : null;

    /// <summary>Starts every durably claimed <c>Restoring</c> copy on this host.</summary>
    internal Task RunPendingAsync(CancellationToken ct)
    {
        IReadOnlyList<SelfHostedRecoveryManifest> copies;
        try
        {
            copies = manifests.ReadAll();
        }
        catch (MigrationActivationException exception)
        {
            logger.LogError(exception,
                "Recovery copies could not be scanned; a corrupt manifest must be inspected by an operator.");
            return Task.CompletedTask;
        }

        foreach (var manifest in copies)
        {
            ct.ThrowIfCancellationRequested();
            if (manifest.Status == MigrationRecoveryStatus.Restoring)
            {
                Start(manifest.JobId);
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Startup/periodic scan that resumes durably claimed restores. Runs after the
/// activation reconciler and database bootstrap in the host startup order, so a
/// crash-interrupted cutover is first resolved to one complete generation.
/// </summary>
internal sealed class SelfHostedRecoveryRestoreProcessor(
    SelfHostedRecoveryRestoreRunner runner,
    TimeProvider clock,
    ILogger<SelfHostedRecoveryRestoreProcessor> logger) : BackgroundService
{
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The recovery restore scan failed; it retries on the next pass.");
            }

            try
            {
                await Task.Delay(ScanInterval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
