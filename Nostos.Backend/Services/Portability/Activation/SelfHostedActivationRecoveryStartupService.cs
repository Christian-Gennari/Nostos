namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Explicit pre-bootstrap startup engine, not a hosted worker. Reads/validates all
/// journals before touching live files. Startup owns a special exclusive lease
/// while admission is still closed. On any failure neither bootstrap nor workers
/// start and admission stays closed. Only successful reconciliation clears the
/// advisory marker; no process-local lease can survive a restart.
/// </summary>
internal sealed class SelfHostedActivationRecoveryStartupService(
    SelfHostedActivationPaths paths,
    SelfHostedActivationJournalStore journals,
    LibraryMaintenanceCoordinator maintenance,
    IEnumerable<ISelfHostedActivationRecoveryStep> steps)
{
    private bool _completed;

    internal async Task ReconcileIncompleteAsync(CancellationToken ct = default)
    {
        if (_completed) return;
        await using (var lease = maintenance.EnterStartupRecovery())
        {
            maintenance.WithExclusiveLease(lease, () =>
            {
                ct.ThrowIfCancellationRequested();
                var documents = journals.ReadAll(); // corrupt files fail before any live mutation
                var actionable = documents.Where(j => SelfHostedActivationState.RecoveryAction(j)
                    is SelfHostedRecoveryAction.RollBackOriginal or SelfHostedRecoveryAction.RollForwardCandidate).ToArray();
                if (actionable.Length > 1)
                    throw SelfHostedActivationPaths.Failure("Multiple unresolved cutovers require operator recovery.");
                var ordered = steps.OrderBy(s => s.Order).ToArray();
                foreach (var journal in documents)
                {
                    paths.Verify(journal.JobId);
                    if (SelfHostedActivationState.RecoveryAction(journal) == SelfHostedRecoveryAction.Nothing)
                    {
                        if (File.Exists(paths.PreviousDatabase(journal.JobId)) || Directory.Exists(paths.PreviousMedia(journal.JobId)))
                            throw SelfHostedActivationPaths.Failure("A no-cutover journal unexpectedly has retained live components.");
                        if (actionable.Length == 0 && (!File.Exists(paths.LiveDatabase) || !Directory.Exists(paths.LiveMedia)))
                            throw SelfHostedActivationPaths.Failure("A pre-cutover or rolled-back journal has missing live components.");
                    }
                }
                foreach (var journal in actionable)
                {
                    var action = SelfHostedActivationState.RecoveryAction(journal);
                    foreach (var step in ordered) step.Validate(journal, action);
                }
                foreach (var journal in actionable)
                {
                    var action = SelfHostedActivationState.RecoveryAction(journal);
                    if (action == SelfHostedRecoveryAction.RollBackOriginal)
                        journals.Advance(journal.JobId, SelfHostedActivationPhase.RollingBack, lease);
                    // Once repair begins do not honor cancellation between the
                    // components. A process crash still resumes RollingBack.
                    foreach (var step in ordered) step.Execute(journal, action);
                    if (!File.Exists(paths.LiveDatabase) || !Directory.Exists(paths.LiveMedia))
                        throw SelfHostedActivationPaths.Failure("Recovery did not produce both live components.");
                    if (action == SelfHostedRecoveryAction.RollBackOriginal)
                        journals.Advance(journal.JobId, SelfHostedActivationPhase.RolledBack, lease);
                    journals.MarkResolved(journal.JobId, lease);
                }
                foreach (var journal in documents.Where(j => SelfHostedActivationState.RecoveryAction(j) == SelfHostedRecoveryAction.Nothing))
                {
                    if (!File.Exists(paths.LiveDatabase) || !Directory.Exists(paths.LiveMedia))
                        throw SelfHostedActivationPaths.Failure("A pre-cutover or rolled-back journal has missing live components.");
                    if (File.Exists(paths.PreviousDatabase(journal.JobId)) || Directory.Exists(paths.PreviousMedia(journal.JobId)))
                        throw SelfHostedActivationPaths.Failure("A no-cutover journal unexpectedly has retained live components.");
                }
                foreach (var journal in documents.Where(j => j.Phase == SelfHostedActivationPhase.RolledBack))
                    journals.MarkResolved(journal.JobId, lease);
            });
        }
        maintenance.CompleteStartupRecovery();
        _completed = true;
    }
}
