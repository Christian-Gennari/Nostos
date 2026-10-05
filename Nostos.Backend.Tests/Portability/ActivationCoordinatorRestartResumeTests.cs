using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Restart semantics for a durably admitted activation (#681 review of #753).
/// The confirmation that binds a run is the activation journal written at
/// admission; after a restart the in-memory request is gone, so later requests
/// are status-only and the run resumes from the journal — including a resolved
/// rollback, which proves the original generation is live. A missing record
/// never mutates: the job returns to re-confirmation.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class ActivationCoordinatorRestartResumeTests
{
    [Fact]
    public async Task RequestlessResume_UsesTheJournaledConfirmedRevision_NotTheJobBaseline()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var baseline = bed.RevisionToken;

        // The library advances after job creation; the user confirms the new
        // current revision. The durable journal records that revision, while
        // the job row keeps the older import-start baseline.
        await bed.WritePortableRevisionBumpAsync();
        var confirmed = await ReadCurrentRevisionAsync(bed);
        confirmed.Should().NotBe(baseline);

        await CrashAtAsync(bed, SelfHostedActivationSteps.PhaseExclusiveEntered,
            revision: confirmed, confirm: true);
        (await bed.ReadJobAsync()).State.Should().Be((int)MigrationJobState.Activating);
        (await bed.ReadJobAsync()).DestinationRevision.Should().Be(baseline,
            "the stored baseline is the import-start revision, not the confirmation");

        await bed.RecoverHostAsync();
        bed.Clock.Advance(SelfHostedActivationCoordinator.ActivationLeaseDuration + TimeSpan.FromMinutes(1));

        // The public request-less entry point is the pure resume path: only the
        // journal can tell it what was confirmed. The old baseline fallback
        // would fail the in-window recheck against the newer live revision.
        await bed.ActivatePublicAsync();

        var job = await bed.ReadJobAsync();
        job.State.Should().Be((int)MigrationJobState.Completed);
        job.RecoveryStatus.Should().Be((int)MigrationRecoveryStatus.Available);
        await bed.AssertImportedGenerationAsync();
        bed.Manifests.Read(bed.JobId)!.PreviousDestinationRevision.Should().Be(confirmed);
    }

    [Fact]
    public async Task ResumeAfterRestart_WithAChangedLibrary_AbortsInWindowWithConflictAndNothingMutated()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var confirmed = bed.RevisionToken;

        await CrashAtAsync(bed, SelfHostedActivationSteps.PhaseExclusiveEntered,
            revision: confirmed, confirm: true);
        await bed.RecoverHostAsync();
        bed.Clock.Advance(SelfHostedActivationCoordinator.ActivationLeaseDuration + TimeSpan.FromMinutes(1));

        var dispatcher = await StartDispatcherAsync(bed);
        string changed = string.Empty;
        Dictionary<string, List<string>>? afterWrite = null;
        Dictionary<string, string>? mediaAfterWrite = null;
        dispatcher.CoordinatorCreatedForTesting = coordinator =>
            coordinator.StepObserverForTesting = step =>
            {
                if (string.Equals(step, SelfHostedActivationSteps.AfterVerifyCandidate, StringComparison.Ordinal))
                {
                    // A real portable write lands after the resumed run's own
                    // admission read, so only the exclusive-window recheck can
                    // see it.
                    bed.WritePortableRevisionBumpAsync().GetAwaiter().GetResult();
                    changed = ReadCurrentRevisionAsync(bed).GetAwaiter().GetResult();
                    afterWrite = DumpAllTablesExceptActivationJob(bed);
                    mediaAfterWrite = ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia);
                }
            };

        try
        {
            // The request is status-only; the journal is the binding. The run
            // is refused inside the window, returns the job to re-confirmation,
            // and mutates nothing.
            var accepted = await dispatcher.RequestAsync(bed.JobId,
                new MigrationActivateRequest(confirmed, ConfirmReplacement: false), default);
            accepted.Outcome.Should().Be(MigrationActivationRequestOutcome.Accepted);

            var failed = await WaitForStatusAsync(dispatcher, bed.JobId, MigrationActivationOutcome.Failed);
            failed.ErrorCode.Should().Be(MigrationActivationErrorCodes.DestinationConflict);
            failed.State.Should().Be(MigrationJobState.ReadyToActivate,
                "the job returns to a re-confirmable state");
            failed.CanActivate.Should().BeTrue();
            failed.DestinationRevision.Should().Be(changed);
            changed.Should().NotBe(confirmed);

            afterWrite.Should().NotBeNull("the in-window write must have been observed");
            DumpAllTablesExceptActivationJob(bed).Should().BeEquivalentTo(afterWrite,
                "the aborted resume must not mutate anything");
            ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia).Should().BeEquivalentTo(mediaAfterWrite);

            // Only an explicit confirmation of the changed generation replaces it.
            dispatcher.CoordinatorCreatedForTesting = null;
            var confirmedAgain = await dispatcher.RequestAsync(bed.JobId,
                new MigrationActivateRequest(changed, ConfirmReplacement: true), default);
            confirmedAgain.Outcome.Should().Be(MigrationActivationRequestOutcome.Accepted);
            await WaitForStatusAsync(dispatcher, bed.JobId, MigrationActivationOutcome.Completed);
            await bed.AssertImportedGenerationAsync();
            dispatcher.StartedRunCount.Should().Be(2);
        }
        finally
        {
            dispatcher.CoordinatorCreatedForTesting = null;
            await dispatcher.StopAsync(default);
        }
    }

    [Fact]
    public async Task ResumeAfterRestart_IgnoresTheRequestRevisionAndFlag_AndUsesTheJournal()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var confirmed = bed.RevisionToken;

        await CrashAtAsync(bed, SelfHostedActivationSteps.PhaseExclusiveEntered,
            revision: confirmed, confirm: true);
        await bed.RecoverHostAsync();
        bed.Clock.Advance(SelfHostedActivationCoordinator.ActivationLeaseDuration + TimeSpan.FromMinutes(1));

        var dispatcher = await StartDispatcherAsync(bed);
        try
        {
            // Both status-only values are lies: a different revision and
            // confirmReplacement=false. Neither may change the binding. The run
            // completes only because the durable confirmed revision equals the
            // in-window revision and the durable record carries the confirmation.
            var accepted = await dispatcher.RequestAsync(bed.JobId,
                new MigrationActivateRequest("substituted-revision", ConfirmReplacement: false), default);
            accepted.Outcome.Should().Be(MigrationActivationRequestOutcome.Accepted);

            await WaitForStatusAsync(dispatcher, bed.JobId, MigrationActivationOutcome.Completed);
            await bed.AssertImportedGenerationAsync();
            bed.Manifests.Read(bed.JobId)!.PreviousDestinationRevision.Should().Be(confirmed,
                "the journal's confirmed revision was used, not the requested one");
        }
        finally
        {
            await dispatcher.StopAsync(default);
        }
    }

    [Fact]
    public async Task MissingDurableRecord_ReturnsToReconfirmationAndMutatesNothing()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var confirmed = bed.RevisionToken;

        await CrashAtAsync(bed, SelfHostedActivationSteps.PhaseExclusiveEntered,
            revision: confirmed, confirm: true);
        await bed.RecoverHostAsync();
        bed.Clock.Advance(SelfHostedActivationCoordinator.ActivationLeaseDuration + TimeSpan.FromMinutes(1));

        // The exclusive-entered journal is clearable: the durable admission
        // record is gone, while the live library is provably pre-activation.
        bed.Journals.PrepareForRetry(bed.JobId);
        var before = DumpAllTablesExceptActivationJob(bed);
        var mediaBefore = ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia);

        var dispatcher = await StartDispatcherAsync(bed);
        try
        {
            var accepted = await dispatcher.RequestAsync(bed.JobId,
                new MigrationActivateRequest(confirmed, ConfirmReplacement: false), default);
            accepted.Outcome.Should().Be(MigrationActivationRequestOutcome.Accepted);

            var refused = await WaitForStatusAsync(dispatcher, bed.JobId, MigrationActivationOutcome.Failed);
            refused.ErrorCode.Should().Be(MigrationActivationErrorCodes.ConfirmationRequired);
            refused.State.Should().Be(MigrationJobState.ReadyToActivate);
            refused.CanActivate.Should().BeTrue();
            refused.DestinationRevision.Should().Be(confirmed);
            refused.DestinationStatus.Should().Be(MigrationDestinationStatus.Populated);

            DumpAllTablesExceptActivationJob(bed).Should().BeEquivalentTo(before,
                "a lost confirmation record must never authorise a mutation");
            ActivationBuildFixture.MediaSnapshot(bed.Paths.LiveMedia).Should().BeEquivalentTo(mediaBefore);

            var confirmedAgain = await dispatcher.RequestAsync(bed.JobId,
                new MigrationActivateRequest(confirmed, ConfirmReplacement: true), default);
            confirmedAgain.Outcome.Should().Be(MigrationActivationRequestOutcome.Accepted);
            await WaitForStatusAsync(dispatcher, bed.JobId, MigrationActivationOutcome.Completed);
            await bed.AssertImportedGenerationAsync();
        }
        finally
        {
            await dispatcher.StopAsync(default);
        }
    }

    [Fact]
    public async Task ConcurrentPostsOnAResumedRun_AdmitExactlyOneRunBoundToTheJournal()
    {
        await using var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        var confirmed = bed.RevisionToken;

        await CrashAtAsync(bed, SelfHostedActivationSteps.PhaseExclusiveEntered,
            revision: confirmed, confirm: true);
        await bed.RecoverHostAsync();
        bed.Clock.Advance(SelfHostedActivationCoordinator.ActivationLeaseDuration + TimeSpan.FromMinutes(1));

        var dispatcher = await StartDispatcherAsync(bed);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.CoordinatorCreatedForTesting = coordinator =>
            coordinator.StepObserverForTesting = step =>
            {
                if (string.Equals(step, SelfHostedActivationSteps.AfterAdmission, StringComparison.Ordinal))
                {
                    admitted.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                }
            };

        try
        {
            // Simultaneous duplicate POSTs carrying different revisions: the
            // admission stripe plus the slot CAS must admit exactly one run,
            // and the journal — not either request — is what it records.
            var results = await Task.WhenAll(
                dispatcher.RequestAsync(bed.JobId,
                    new MigrationActivateRequest("first-request-revision", ConfirmReplacement: false), default),
                dispatcher.RequestAsync(bed.JobId,
                    new MigrationActivateRequest("second-request-revision", ConfirmReplacement: true), default));

            results.Count(result => result.Outcome == MigrationActivationRequestOutcome.Accepted)
                .Should().Be(1);
            results.Count(result => result.Outcome == MigrationActivationRequestOutcome.Replayed)
                .Should().Be(1, "one admitted run absorbs every later request for the same job");

            (await Task.Run(() => admitted.Task.Wait(TimeSpan.FromSeconds(60))))
                .Should().BeTrue("the single admitted resume must reach Phase A");
            dispatcher.StartedRunCount.Should().Be(1);
            release.SetResult();

            await WaitForStatusAsync(dispatcher, bed.JobId, MigrationActivationOutcome.Completed);
            dispatcher.StartedRunCount.Should().Be(1);
            await bed.AssertImportedGenerationAsync();
            bed.Manifests.Read(bed.JobId)!.PreviousDestinationRevision.Should().Be(confirmed,
                "both requests were status-only; only the journal revision was recorded");
        }
        finally
        {
            release.TrySetResult();
            dispatcher.CoordinatorCreatedForTesting = null;
            await dispatcher.StopAsync(default);
        }
    }

    private static async Task CrashAtAsync(
        ActivationCoordinatorTestBed bed,
        string boundary,
        string revision,
        bool confirm)
    {
        var crashed = false;
        try
        {
            await ActivateWithRevisionAsync(bed, revision, confirm, observer: step =>
            {
                if (string.Equals(step, boundary, StringComparison.Ordinal))
                {
                    throw new SelfHostedActivationAbandonedException();
                }
            });
        }
        catch (SelfHostedActivationAbandonedException)
        {
            crashed = true;
        }

        crashed.Should().BeTrue($"the {boundary} boundary must be reached");
    }

    private static async Task<SelfHostedActivationResult> ActivateWithRevisionAsync(
        ActivationCoordinatorTestBed bed,
        string revision,
        bool confirm,
        Action<string>? observer = null)
    {
        await using var scope = bed.Host.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedActivationCoordinator>();
        coordinator.StepObserverForTesting = observer;
        return await coordinator.ActivateAsync(
            bed.JobId, new MigrationActivateRequest(revision, confirm), default);
    }

    private static async Task<SelfHostedActivationDispatcher> StartDispatcherAsync(
        ActivationCoordinatorTestBed bed)
    {
        var dispatcher = new SelfHostedActivationDispatcher(
            bed.Host.GetRequiredService<IServiceScopeFactory>(),
            bed.Maintenance,
            bed.Manifests,
            bed.Clock,
            NullLogger<SelfHostedActivationDispatcher>.Instance);
        await dispatcher.StartAsync(default);
        return dispatcher;
    }

    private static async Task<string> ReadCurrentRevisionAsync(ActivationCoordinatorTestBed bed)
    {
        await using var scope = bed.Host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILibraryDestinationRevisionProvider>()
            .GetCurrentAsync(default);
    }

    private static async Task<MigrationActivationStatusResponse> WaitForStatusAsync(
        SelfHostedActivationDispatcher dispatcher,
        Guid jobId,
        MigrationActivationOutcome outcome)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        MigrationActivationStatusResponse? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await dispatcher.GetStatusAsync(jobId, default);
            if (last.Outcome == outcome)
            {
                return last;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException(
            $"Job {jobId} did not reach activation outcome {outcome}; last was {last?.Outcome}.");
    }

    private static Dictionary<string, List<string>> DumpAllTablesExceptActivationJob(
        ActivationCoordinatorTestBed bed)
    {
        var all = ActivationBuildFixture.DumpAllTables(bed.Paths.LiveDatabase);
        all["MigrationJobRecords"] = all["MigrationJobRecords"]
            .Where(row => !string.Equals(
                row.Split('|', 2)[0], bed.JobId.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return all;
    }
}
