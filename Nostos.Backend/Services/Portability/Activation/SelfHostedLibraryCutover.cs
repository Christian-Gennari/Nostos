using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Portability.Activation;

/// <summary>
/// Marker for the crash-matrix sentinels: a step observer throws one of these to
/// model abrupt process loss. The cutover runner must let it through untouched —
/// no rollback, no cleanup, no lease release — exactly like a host that died at
/// that boundary.
/// </summary>
internal interface ISelfHostedLibrarySwitchAbandoned;

/// <summary>
/// The words and the test-visible step names that distinguish the two kinds of
/// library switch. Everything else about the protocol is identical.
/// </summary>
internal sealed record SelfHostedLibraryCutoverLabels(
    string JournalNoun,
    string TitleNoun,
    string RollbackStep)
{
    /// <summary>Activation of a prepared import: the words used by the recovery guide.</summary>
    internal static SelfHostedLibraryCutoverLabels Activation { get; } =
        new("activation", "Activation", "after:rollback");

    /// <summary>Restore of a retained recovery copy.</summary>
    internal static SelfHostedLibraryCutoverLabels RecoveryRestore { get; } =
        new("restore", "Restore", "restore:after:rollback");
}

/// <summary>
/// The named boundaries the cutover emits, in protocol order. They are the
/// durable vocabulary the crash matrix and the real-host drills park at, so they
/// are supplied verbatim per switch kind rather than composed.
/// </summary>
internal sealed record SelfHostedLibraryCutoverSteps(
    string PhaseExclusiveEntered,
    string AfterFinalize,
    string AfterQuiesce,
    string PhaseDatabaseCheckpointed,
    string AfterPrepareRetention,
    string PhaseCutoverPrepared,
    string AfterRetainMedia,
    string PhasePreviousMediaRetained,
    string AfterRetainDatabase,
    string PhasePreviousDatabaseRetained,
    string AfterActivateMedia,
    string PhaseCandidateMediaActivated,
    string AfterActivateDatabase,
    string PhaseCandidateDatabaseActivated,
    string AfterReopen,
    string AfterPostVerify,
    string PhasePostActivationVerified,
    string PhaseCommitted)
{
    internal static SelfHostedLibraryCutoverSteps Activation { get; } = new(
        SelfHostedActivationSteps.PhaseExclusiveEntered,
        SelfHostedActivationSteps.AfterFinalize,
        SelfHostedActivationSteps.AfterQuiesce,
        SelfHostedActivationSteps.PhaseDatabaseCheckpointed,
        SelfHostedActivationSteps.AfterPrepareRetention,
        SelfHostedActivationSteps.PhaseCutoverPrepared,
        SelfHostedActivationSteps.AfterRetainMedia,
        SelfHostedActivationSteps.PhasePreviousMediaRetained,
        SelfHostedActivationSteps.AfterRetainDatabase,
        SelfHostedActivationSteps.PhasePreviousDatabaseRetained,
        SelfHostedActivationSteps.AfterActivateMedia,
        SelfHostedActivationSteps.PhaseCandidateMediaActivated,
        SelfHostedActivationSteps.AfterActivateDatabase,
        SelfHostedActivationSteps.PhaseCandidateDatabaseActivated,
        SelfHostedActivationSteps.AfterReopen,
        SelfHostedActivationSteps.AfterPostVerify,
        SelfHostedActivationSteps.PhasePostActivationVerified,
        SelfHostedActivationSteps.PhaseCommitted);

    internal static SelfHostedLibraryCutoverSteps RecoveryRestore { get; } = new(
        SelfHostedRecoveryRestoreSteps.PhaseExclusiveEntered,
        SelfHostedRecoveryRestoreSteps.AfterFinalize,
        SelfHostedRecoveryRestoreSteps.AfterQuiesce,
        SelfHostedRecoveryRestoreSteps.PhaseDatabaseCheckpointed,
        SelfHostedRecoveryRestoreSteps.AfterPrepareRetention,
        SelfHostedRecoveryRestoreSteps.PhaseCutoverPrepared,
        SelfHostedRecoveryRestoreSteps.AfterRetainMedia,
        SelfHostedRecoveryRestoreSteps.PhasePreviousMediaRetained,
        SelfHostedRecoveryRestoreSteps.AfterRetainDatabase,
        SelfHostedRecoveryRestoreSteps.PhasePreviousDatabaseRetained,
        SelfHostedRecoveryRestoreSteps.AfterActivateMedia,
        SelfHostedRecoveryRestoreSteps.PhaseCandidateMediaActivated,
        SelfHostedRecoveryRestoreSteps.AfterActivateDatabase,
        SelfHostedRecoveryRestoreSteps.PhaseCandidateDatabaseActivated,
        SelfHostedRecoveryRestoreSteps.AfterReopen,
        SelfHostedRecoveryRestoreSteps.AfterPostVerify,
        SelfHostedRecoveryRestoreSteps.PhasePostActivationVerified,
        SelfHostedRecoveryRestoreSteps.PhaseCommitted);
}

/// <summary>
/// One library switch's operation-specific half of the shared cutover. The
/// runner owns the exclusive window, every durable phase write, every rename and
/// the rollback; a plan supplies only what genuinely differs between activating
/// a prepared import and restoring a retained recovery copy.
/// </summary>
internal sealed class SelfHostedLibraryCutoverPlan
{
    /// <summary>Job id (activation) or restore operation id (recovery restore).</summary>
    internal required Guid SwitchId { get; init; }

    internal required LibraryMaintenanceReason Reason { get; init; }

    internal required SelfHostedLibraryCutoverLabels Labels { get; init; }

    internal required SelfHostedLibraryCutoverSteps Steps { get; init; }

    /// <summary>
    /// True when the replaced generation is retained as a recovery copy through
    /// the manifest protocol; false only for activation into an empty
    /// destination, which uses the same renames with no manifest to publish.
    /// </summary>
    internal required bool RetainPreviousLibrary { get; init; }

    /// <summary>
    /// True when a failure from <see cref="AdmitAsync"/> itself must still be
    /// undone in-process. Recovery restore durably claims the copy as
    /// <c>Restoring</c> before the window opens, so a refused admission has to
    /// release that claim; activation has changed nothing yet, so a refused
    /// admission returns the job to re-confirmation instead.
    /// </summary>
    internal bool RollBackWhenEnterFails { get; init; }

    /// <summary>
    /// The authoritative admission recheck, run after admission is closed and
    /// every reader/writer has drained, before anything is renamed.
    /// </summary>
    internal required Func<IAsyncDisposable, CancellationToken, Task> AdmitAsync { get; init; }

    /// <summary>
    /// The durable entry into the switch — activation's transition out of
    /// <c>ReadyToActivate</c>. From here on a failure must be rolled back.
    /// </summary>
    internal Func<IAsyncDisposable, CancellationToken, Task> EnterSwitchAsync { get; init; } =
        (_, _) => Task.CompletedTask;

    /// <summary>Keeps the operation's lease alive once the window is entered.</summary>
    internal Func<IAsyncDisposable, CancellationToken, Task> RenewLeaseAsync { get; init; } =
        (_, _) => Task.CompletedTask;

    /// <summary>Finalize the candidate and refuse an unfinalized one.</summary>
    internal required Func<IAsyncDisposable, Task> FinalizeCandidateAsync { get; init; }

    /// <summary>
    /// Recovery restore drops a crash-terminated retention plan here, before the
    /// checkpoint boundary, so no database access is needed after quiescence.
    /// </summary>
    internal Func<IAsyncDisposable, Task> ClearStaleRetentionAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>Publish the retention plan for the generation being replaced.</summary>
    internal Func<ISelfHostedRecoverySnapshots, IAsyncDisposable, Task> PrepareRetentionAsync { get; init; } =
        (_, _) => Task.CompletedTask;

    internal required Func<ISelfHostedRecoverySnapshots, IAsyncDisposable, Task> RetainMediaAsync { get; init; }

    internal required Func<ISelfHostedRecoverySnapshots, IAsyncDisposable, Task> RetainDatabaseAsync { get; init; }

    /// <summary>Empty-destination activation: the same scratch renames, no manifest.</summary>
    internal Func<Task> RetainEmptyMediaAsync { get; init; } = () => Task.CompletedTask;

    internal Func<Task> RetainEmptyDatabaseAsync { get; init; } = () => Task.CompletedTask;

    /// <summary>Authoritative verification of the newly activated generation.</summary>
    internal required Func<Task> VerifyActivatedLibraryAsync { get; init; }

    /// <summary>Post-commit completion; a failure here keeps the host fail-closed.</summary>
    internal required Func<IAsyncDisposable, Task> FinalizeCommittedAsync { get; init; }

    /// <summary>Bookkeeping after an in-process rollback restored the original.</summary>
    internal Func<IAsyncDisposable, Exception, Task> OnRolledBackAsync { get; init; } = (_, _) => Task.CompletedTask;

    /// <summary>Durable record that in-process rollback could not complete.</summary>
    internal Func<Task> OnRollbackFailedAsync { get; init; } = () => Task.CompletedTask;

    internal required string RollbackFailureLogMessage { get; init; }

    internal required string RollbackFailedMessage { get; init; }

    /// <summary>Set by the runner once the exclusive window is held.</summary>
    internal bool WindowEntered { get; set; }

    /// <summary>Set by the runner once the durable commit stands.</summary>
    internal bool Committed { get; set; }
}

/// <summary>
/// The one journaled library-switch protocol (#1604): activation and recovery
/// restore used to carry two copies of this sequence, which is the most
/// crash-sensitive code in the SelfHosted host.
///
/// <para><b>Order.</b> Inside one exclusive maintenance window: authoritative
/// recheck, <c>ExclusiveEntered</c>, candidate finalization, quiesce,
/// <c>DatabaseCheckpointed</c>, retention of the previous generation,
/// <c>CutoverPrepared</c>, the media rename, the database rename,
/// <c>ReopenActivated</c>, authoritative post-activation verification,
/// <c>PostActivationVerified</c>, <c>Committed</c>. Every phase is written before
/// the filesystem step it authorises, every step is idempotent, and recovery
/// reuses the startup reconciler's component steps for in-process rollback, so a
/// crash at any boundary converges on one complete generation.</para>
///
/// <para><b>Failure.</b> A pre-commit failure rolls back through the reconciler's
/// own steps and then runs the plan's bookkeeping. A rollback that cannot
/// complete keeps admission closed and the journal unresolved, so only a
/// restart's reconciler may finish. The crash sentinel is rethrown untouched.</para>
/// </summary>
internal sealed class SelfHostedLibraryCutover(
    SelfHostedActivationPaths paths,
    LibraryMaintenanceCoordinator maintenance,
    SelfHostedActivationJournalStore journals,
    ISelfHostedSqliteLifecycle sqlite,
    IReadOnlyList<ISelfHostedActivationRecoveryStep> recoverySteps,
    IServiceScopeFactory scopes,
    ILogger logger,
    Action<string> step)
{
    internal async Task RunAsync(SelfHostedLibraryCutoverPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        IAsyncDisposable? exclusive = null;
        var enteredSwitch = false;
        var committed = false;
        try
        {
            // Fail closed when the drain cannot complete: nothing has changed.
            exclusive = await maintenance.EnterExclusiveAsync(plan.Reason, ct);
            plan.WindowEntered = true;

            await plan.AdmitAsync(exclusive, CancellationToken.None);
            await plan.EnterSwitchAsync(exclusive, CancellationToken.None);
            enteredSwitch = true;
            await plan.RenewLeaseAsync(exclusive, CancellationToken.None);

            journals.Advance(plan.SwitchId, SelfHostedActivationPhase.ExclusiveEntered, exclusive);
            step(plan.Steps.PhaseExclusiveEntered);

            // Last live database mutation is the finalized candidate snapshot
            // plus the revision bump. Everything after quiesce is path movement.
            await plan.FinalizeCandidateAsync(exclusive);
            step(plan.Steps.AfterFinalize);

            await plan.ClearStaleRetentionAsync(exclusive);
            sqlite.QuiesceLive(exclusive);
            step(plan.Steps.AfterQuiesce);
            journals.Advance(plan.SwitchId, SelfHostedActivationPhase.DatabaseCheckpointed, exclusive);
            step(plan.Steps.PhaseDatabaseCheckpointed);

            // One scope for the whole retention block: the recovery service reads
            // the live database and must see one connection identity across the
            // plan write and the two retention renames.
            await using (var scope = scopes.CreateAsyncScope())
            {
                var recovery = scope.ServiceProvider.GetRequiredService<ISelfHostedRecoverySnapshots>();
                if (plan.RetainPreviousLibrary)
                {
                    await plan.PrepareRetentionAsync(recovery, exclusive);
                    step(plan.Steps.AfterPrepareRetention);
                }

                journals.Advance(plan.SwitchId, SelfHostedActivationPhase.CutoverPrepared, exclusive);
                step(plan.Steps.PhaseCutoverPrepared);

                if (plan.RetainPreviousLibrary)
                {
                    await plan.RetainMediaAsync(recovery, exclusive);
                    step(plan.Steps.AfterRetainMedia);
                    journals.Advance(plan.SwitchId, SelfHostedActivationPhase.PreviousMediaRetained, exclusive);
                    step(plan.Steps.PhasePreviousMediaRetained);

                    await plan.RetainDatabaseAsync(recovery, exclusive);
                    step(plan.Steps.AfterRetainDatabase);
                    journals.Advance(plan.SwitchId, SelfHostedActivationPhase.PreviousDatabaseRetained, exclusive);
                    step(plan.Steps.PhasePreviousDatabaseRetained);
                }
                else
                {
                    await plan.RetainEmptyMediaAsync();
                    step(plan.Steps.AfterRetainMedia);
                    journals.Advance(plan.SwitchId, SelfHostedActivationPhase.PreviousMediaRetained, exclusive);
                    step(plan.Steps.PhasePreviousMediaRetained);

                    await plan.RetainEmptyDatabaseAsync();
                    step(plan.Steps.AfterRetainDatabase);
                    journals.Advance(plan.SwitchId, SelfHostedActivationPhase.PreviousDatabaseRetained, exclusive);
                    step(plan.Steps.PhasePreviousDatabaseRetained);
                }
            }

            ActivationFileSystem.Rename(paths.CandidateMedia(plan.SwitchId), paths.LiveMedia);
            step(plan.Steps.AfterActivateMedia);
            journals.Advance(plan.SwitchId, SelfHostedActivationPhase.CandidateMediaActivated, exclusive);
            step(plan.Steps.PhaseCandidateMediaActivated);

            ActivationFileSystem.Rename(paths.CandidateDatabase(plan.SwitchId), paths.LiveDatabase);
            step(plan.Steps.AfterActivateDatabase);
            journals.Advance(plan.SwitchId, SelfHostedActivationPhase.CandidateDatabaseActivated, exclusive);
            step(plan.Steps.PhaseCandidateDatabaseActivated);

            sqlite.ReopenActivated(exclusive);
            step(plan.Steps.AfterReopen);

            await plan.VerifyActivatedLibraryAsync();
            step(plan.Steps.AfterPostVerify);

            journals.Advance(plan.SwitchId, SelfHostedActivationPhase.PostActivationVerified, exclusive);
            step(plan.Steps.PhasePostActivationVerified);
            journals.Advance(plan.SwitchId, SelfHostedActivationPhase.Committed, exclusive);
            step(plan.Steps.PhaseCommitted);
            committed = true;
            plan.Committed = true;

            await plan.FinalizeCommittedAsync(exclusive);
        }
        catch (Exception exception) when (exception is ISelfHostedLibrarySwitchAbandoned)
        {
            // A process crash never runs a finally: admission must stay closed
            // until a restart's reconciler decides the generation.
            maintenance.FailClosedForRecovery();
            throw;
        }
        catch (Exception exception) when (!committed)
        {
            if (exclusive is not null && (enteredSwitch || plan.RollBackWhenEnterFails))
            {
                try
                {
                    await RollBackAsync(plan, exclusive);
                }
                catch (Exception rollbackFailure)
                {
                    // Admission must not reopen onto a partially restored
                    // generation. Keep the journal unresolved, keep the durable
                    // maintenance marker, and let a restart's reconciler finish.
                    maintenance.FailClosedForRecovery();
                    await plan.OnRollbackFailedAsync();
                    logger.LogCritical(rollbackFailure, plan.RollbackFailureLogMessage);
                    throw new MigrationActivationException(
                        MigrationActivationErrorCodes.RecoveryFailed, plan.RollbackFailedMessage);
                }

                await plan.OnRolledBackAsync(exclusive, exception);
            }

            throw;
        }
        finally
        {
            if (exclusive is not null) await exclusive.DisposeAsync();
        }
    }

    /// <summary>
    /// Rollback through the startup reconciler's own steps: the durable phase
    /// selects the direction, every step validates before any executes, and
    /// repeating from any partially repaired position succeeds. The restored
    /// original has a new file identity at the live pathname, so the pool is
    /// cleared and the restored database verified before admission can reopen.
    /// </summary>
    private async Task RollBackAsync(SelfHostedLibraryCutoverPlan plan, IAsyncDisposable exclusive)
    {
        var journal = journals.Read(plan.SwitchId)
            ?? throw SelfHostedActivationPaths.Failure($"The {plan.Labels.JournalNoun} journal is missing.");
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
                $"The {plan.Labels.JournalNoun} journal does not describe a recoverable pre-commit state.");
        }

        foreach (var recoveryStep in recoverySteps) recoveryStep.Validate(journal, action);
        journals.Advance(plan.SwitchId, SelfHostedActivationPhase.RollingBack, exclusive);
        foreach (var recoveryStep in recoverySteps) recoveryStep.Execute(journal, action);
        if (!File.Exists(paths.LiveDatabase) || !Directory.Exists(paths.LiveMedia))
        {
            throw SelfHostedActivationPaths.Failure(
                $"{plan.Labels.TitleNoun} rollback did not restore the live library.");
        }

        sqlite.ReopenActivated(exclusive);

        journals.Advance(plan.SwitchId, SelfHostedActivationPhase.RolledBack, exclusive);
        journals.MarkResolved(plan.SwitchId, exclusive);
        step(plan.Labels.RollbackStep);
        await Task.CompletedTask;
    }
}
