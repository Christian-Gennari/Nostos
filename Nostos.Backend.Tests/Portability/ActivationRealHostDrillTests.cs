using System.Net;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Issue #681 Slice 11: real-process failure drills. Each case starts a real
/// <c>Nostos.Backend</c> process on a disposable data root with real Kestrel,
/// SQLite and media, parks the activation or restore at a named durable
/// boundary through the environment-gated drill seam, terminates the process
/// with SIGKILL, and starts a fresh host on the same files. Convergence is
/// asserted by content (table dumps and media SHA-256), never by status alone.
///
/// <para>The in-process crash matrix asserts every durable boundary and every
/// rename; these drills are the representative process-level subset the plan
/// allows: the pre-commit retention boundary, the mid-swap boundary, the
/// committed boundary, an empty-destination cutover, both restore directions,
/// and the fault cases that only a real process can exercise (read-only data
/// root and fail-closed recovery, corrupt journal refusal, corrupt recovery
/// manifest, a second host on the same data root, and clock-jump expiry
/// cleanup on startup).</para>
///
/// <para>Opt-in: the whole class is skipped unless
/// <c>NOSTOS_RUN_ACTIVATION_DRILLS=1</c> is set, because each drill spawns real
/// child processes and takes seconds. See <see cref="ActivationDrillFactAttribute"/>.</para>
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class ActivationRealHostDrillTests
{
    [ActivationDrillFact]
    public async Task Kill_at_cutover_prepared_converges_on_the_original_and_retry_activates()
    {
        await using var drill = await ActivationDrillFixture.CreateAsync(populated: true);
        await using var first = await ActivationDrillHost.StartAsync(
            drill, SelfHostedActivationSteps.PhaseCutoverPrepared);
        var jobId = await first.UploadImportAsync("drill-cutover-prepared-");
        var before = drill.SnapshotFull();
        await first.ActivateUntilMarkerAsync(jobId, await first.CurrentRevisionAsync(), confirm: true);
        await first.KillHardAsync();
        drill.ExpireJobLeases();

        await using var second = await ActivationDrillHost.StartAsync(drill);
        drill.ExpireJobLeases();
        drill.AssertSameGenerationExceptJob(before, jobId,
            "a pre-commit crash at CutoverPrepared must return the original library");
        drill.AssertMaintenanceReleased();

        using var completed = await second.ActivateToCompletionAsync(
            jobId, await second.CurrentRevisionAsync(), confirm: true);
        completed.RootElement.GetProperty("recoveryAvailable").GetBoolean().Should().BeTrue();
        await drill.AssertImportedGenerationAsync();
        drill.AssertMaintenanceReleased();
    }

    [ActivationDrillFact]
    public async Task Kill_after_candidate_database_activated_converges_on_the_original_and_retry_activates()
    {
        await using var drill = await ActivationDrillFixture.CreateAsync(populated: true);
        await using var first = await ActivationDrillHost.StartAsync(
            drill, SelfHostedActivationSteps.PhaseCandidateDatabaseActivated);
        var jobId = await first.UploadImportAsync("drill-mid-swap-");
        var before = drill.SnapshotFull();
        await first.ActivateUntilMarkerAsync(jobId, await first.CurrentRevisionAsync(), confirm: true);
        await first.KillHardAsync();
        drill.ExpireJobLeases();

        await using var second = await ActivationDrillHost.StartAsync(drill);
        drill.ExpireJobLeases();
        drill.AssertSameGenerationExceptJob(before, jobId,
            "an uncommitted mid-swap crash must roll back both components");
        drill.AssertMaintenanceReleased();

        using var completed = await second.ActivateToCompletionAsync(
            jobId, await second.CurrentRevisionAsync(), confirm: true);
        await drill.AssertImportedGenerationAsync();
    }

    [ActivationDrillFact]
    public async Task Kill_after_committed_keeps_the_imported_library_and_completes_the_job()
    {
        await using var drill = await ActivationDrillFixture.CreateAsync(populated: true);
        await using var first = await ActivationDrillHost.StartAsync(
            drill, SelfHostedActivationSteps.PhaseCommitted);
        var jobId = await first.UploadImportAsync("drill-committed-");
        await first.ActivateUntilMarkerAsync(jobId, await first.CurrentRevisionAsync(), confirm: true);
        await first.KillHardAsync();
        drill.ExpireJobLeases();

        await using var second = await ActivationDrillHost.StartAsync(drill);
        drill.AssertMaintenanceReleased();
        await drill.AssertImportedGenerationAsync();

        // The durable commit already happened; the restarted host must finish
        // the job from the committed journal, never run a second cutover.
        var status = await second.GetActivationAsync(jobId);
        var durable = status.RootElement;
        durable.GetProperty("state").GetString().Should().Be("Activating");
        durable.GetProperty("outcome").GetString().Should().Be("Running");
        status.Dispose();

        using var completed = await second.ActivateToCompletionAsync(
            jobId, await second.CurrentRevisionAsync(), confirm: false);
        completed.RootElement.GetProperty("recoveryAvailable").GetBoolean().Should().BeTrue();
        await drill.AssertImportedGenerationAsync();
    }

    [ActivationDrillFact]
    public async Task Empty_destination_kill_after_retaining_the_previous_database_converges_on_empty_and_retry_activates()
    {
        await using var drill = await ActivationDrillFixture.CreateAsync(populated: false);
        await using var first = await ActivationDrillHost.StartAsync(
            drill, SelfHostedActivationSteps.PhasePreviousDatabaseRetained);
        var jobId = await first.UploadImportAsync("drill-empty-");
        var before = drill.SnapshotFull();
        await first.ActivateUntilMarkerAsync(jobId, await first.CurrentRevisionAsync(), confirm: false);
        await first.KillHardAsync();
        drill.ExpireJobLeases();

        await using var second = await ActivationDrillHost.StartAsync(drill);
        drill.ExpireJobLeases();
        drill.AssertSameGenerationExceptJob(before, jobId,
            "a pre-commit empty-destination crash must restore the original empty library");
        drill.AssertMaintenanceReleased();

        using var completed = await second.ActivateToCompletionAsync(
            jobId, await second.CurrentRevisionAsync(), confirm: false);
        completed.RootElement.GetProperty("recoveryAvailable").GetBoolean().Should().BeFalse(
            "an empty destination retains no seven-day recovery copy");
        await drill.AssertImportedGenerationAsync();
    }

    [ActivationDrillFact]
    public async Task Restore_kill_before_commit_keeps_the_imported_library_and_the_resumed_restore_completes()
    {
        await using var drill = await ActivationDrillFixture.CreateAsync(populated: true);
        var (jobId, recoveryId) = await ActivatePopulatedAsync(drill);

        await using (var first = await ActivationDrillHost.StartAsync(
                         drill, SelfHostedRecoveryRestoreSteps.PhaseCandidateDatabaseActivated))
        {
            var restore = await first.RestoreAsync(recoveryId, await first.CurrentRevisionAsync());
            restore.Status.Should().Be(HttpStatusCode.Accepted, restore.Body.RootElement.GetRawText());
            restore.Body.Dispose();
            await first.WaitForMarkerAsync();
            await first.KillHardAsync();
        }

        drill.ExpireJobLeases();
        await using var second = await ActivationDrillHost.StartAsync(drill);
        drill.ExpireJobLeases();
        drill.AssertMaintenanceReleased();
        await drill.AssertImportedGenerationAsync();

        // The durable Restoring claim is resumed by the startup scan and the
        // restore completes; the imported library must survive until then.
        await second.WaitForRecoveryStatusAsync(recoveryId, "Restored");
        drill.AssertPortableAndMedia(drill.Template.OriginalPortable, drill.Template.OriginalMedia,
            "the completed restore must expose exactly the retained previous library");
        jobId.Should().NotBe(Guid.Empty);
    }

    [ActivationDrillFact]
    public async Task Restore_kill_after_committed_rolls_forward_to_the_restored_library()
    {
        await using var drill = await ActivationDrillFixture.CreateAsync(populated: true);
        var (jobId, recoveryId) = await ActivatePopulatedAsync(drill);

        await using (var first = await ActivationDrillHost.StartAsync(
                         drill, SelfHostedRecoveryRestoreSteps.PhaseCommitted))
        {
            var restore = await first.RestoreAsync(recoveryId, await first.CurrentRevisionAsync());
            restore.Status.Should().Be(HttpStatusCode.Accepted, restore.Body.RootElement.GetRawText());
            restore.Body.Dispose();
            await first.WaitForMarkerAsync();
            await first.KillHardAsync();
        }

        drill.ExpireJobLeases();
        await using var second = await ActivationDrillHost.StartAsync(drill);
        drill.ExpireJobLeases();
        drill.AssertMaintenanceReleased();
        await second.WaitForRecoveryStatusAsync(recoveryId, "Restored");

        drill.AssertPortableAndMedia(drill.Template.OriginalPortable, drill.Template.OriginalMedia,
            "a committed restore crash must roll forward to the restored previous library");

        // The replaced (previously imported) library is retained as a fresh
        // available copy by the restore finalization.
        var listing = await second.GetJsonAsync("/api/portability/migration/recovery");
        var available = 0;
        foreach (var item in listing.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("status", out var status)
                && status.GetString() == "Available")
            {
                available++;
            }
        }

        listing.Dispose();
        available.Should().Be(1, "the restore retains the replaced library as a new recovery copy");
        jobId.Should().NotBe(Guid.Empty);
    }

    [ActivationDrillFact]
    public async Task Read_only_data_root_fails_closed_until_restart_reconciles()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX permissions drive the fault

        await using var drill = await ActivationDrillFixture.CreateAsync(populated: true);
        var releasePath = Path.Combine(drill.Root, "drill-release");

        await using (var first = await ActivationDrillHost.StartAsync(
                         drill, SelfHostedActivationSteps.PhasePreviousDatabaseRetained, releasePath))
        {
            var jobId = await first.UploadImportAsync("drill-readonly-");
            // Snapshot after the host created its schema and the real upload
            // bookkeeping, so the comparison isolates the library generation.
            var before = drill.SnapshotFull();
            await first.ActivateUntilMarkerAsync(jobId, await first.CurrentRevisionAsync(), confirm: true);

            File.SetUnixFileMode(drill.DatabaseRoot,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);
            first.ReleaseBoundary();

            using var failed = await first.WaitForActivationOutcomeAsync(jobId, "RecoveryFailed");
            failed.RootElement.GetProperty("maintenanceRequired").GetBoolean().Should().BeTrue();
            failed.RootElement.GetProperty("errorCode").GetString()
                .Should().Be(MigrationActivationErrorCodes.RecoveryFailed);
            var busy = await first.SendAsync(HttpMethod.Get, "/api/books");
            busy.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
            busy.Body.Dispose();
            await first.KillHardAsync();

            // The operator fixes the root before the reconciler can run.
            File.SetUnixFileMode(drill.DatabaseRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            await using var second = await ActivationDrillHost.StartAsync(drill);
            drill.ExpireJobLeases();
            drill.AssertSameGenerationExceptJob(before, jobId,
                "the startup reconciler must restore the original library after the operator fixes the root");
            drill.AssertMaintenanceReleased();

            await second.ActivateToCompletionAsync(
                jobId, await second.CurrentRevisionAsync(), confirm: true);
            await drill.AssertImportedGenerationAsync();
        }
    }

    [ActivationDrillFact]
    public async Task Corrupt_journal_refuses_startup_and_data_is_unchanged()
    {
        await using var drill = await ActivationDrillFixture.CreateAsync(populated: true);
        var journalJobId = Guid.NewGuid();
        drill.SeedCorruptJournal(journalJobId);
        var before = drill.SnapshotFull();
        var beforeMedia = drill.SnapshotMedia();

        await using (var refused = await ActivationDrillHost.StartExpectingRefusalAsync(drill))
        {
            refused.IsRunning.Should().BeFalse("a corrupt journal must refuse host startup");
        }

        drill.AssertSameGenerationExceptJob(before, journalJobId, "a refused startup must not mutate the library");
        drill.SnapshotMedia().Should().BeEquivalentTo(beforeMedia);

        drill.RemoveJournalTree(journalJobId);
        await using var recovered = await ActivationDrillHost.StartAsync(drill);
        drill.AssertMaintenanceReleased();
        drill.AssertPortableAndMedia(drill.Template.OriginalPortable, drill.Template.OriginalMedia,
            "removing the unusable journal lets the operator-selected generation start normally");
        recovered.IsRunning.Should().BeTrue();
    }

    [ActivationDrillFact]
    public async Task Corrupt_recovery_manifest_keeps_the_host_running_and_answers_recovery_corrupt()
    {
        await using var drill = await ActivationDrillFixture.CreateAsync(populated: true);
        var (jobId, recoveryId) = await ActivatePopulatedAsync(drill);

        // Corrupt the retained copy's manifest while the host is stopped.
        File.WriteAllText(drill.RecoveryManifestPath(recoveryId), "{torn");

        await using var host = await ActivationDrillHost.StartAsync(drill);
        drill.AssertMaintenanceReleased();
        var books = await host.SendAsync(HttpMethod.Get, "/api/books");
        books.Status.Should().Be(HttpStatusCode.OK);
        books.Body.Dispose();

        var status = await host.SendAsync(
            HttpMethod.Get, $"/api/portability/migration/recovery/{recoveryId}");
        status.Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        status.Body.RootElement.GetProperty("error").GetString()
            .Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        status.Body.Dispose();

        var restore = await host.RestoreAsync(recoveryId, await host.CurrentRevisionAsync());
        restore.Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        restore.Body.RootElement.GetProperty("error").GetString()
            .Should().Be(MigrationActivationErrorCodes.RecoveryCorrupt);
        restore.Body.Dispose();

        await drill.AssertImportedGenerationAsync();
        File.ReadAllText(drill.RecoveryManifestPath(recoveryId)).Should().Be("{torn",
            "a corrupt manifest is preserved for operator inspection");
        jobId.Should().NotBe(Guid.Empty);
    }

    [ActivationDrillFact]
    public async Task Second_host_during_the_window_reconciles_the_journal_and_only_one_complete_generation_remains()
    {
        await using var drill = await ActivationDrillFixture.CreateAsync(populated: true);

        await using (var first = await ActivationDrillHost.StartAsync(
                         drill, SelfHostedActivationSteps.PhaseCutoverPrepared))
        {
            var jobId = await first.UploadImportAsync("drill-second-host-");
            var before = drill.SnapshotFull();

            // A second host on the same data root during the window reconciles
            // the shared pre-commit journal to the original generation; the
            // parked first host is then stopped. Running a second host that
            // performs a *new* activation concurrently is unsupported.
            await first.ActivateUntilMarkerAsync(jobId, await first.CurrentRevisionAsync(), confirm: true);

            await using (var second = await ActivationDrillHost.StartAsync(drill))
            {
                drill.AssertMaintenanceReleased();
                drill.AssertOriginalGeneration(
                    "the second host reconciled the shared pre-commit journal to the original library");
                var catalogue = await second.Client.GetStringAsync("/api/books");
                catalogue.Should().NotContain("Portable EPUB",
                    "the parked first host's cutover must not have been admitted");
            }

            await first.KillHardAsync();
            first.IsRunning.Should().BeFalse("the parked first host was terminated");
            drill.AssertSameGenerationExceptJob(before, jobId,
                "the second host's reconciliation must leave exactly one complete generation");
        }
    }

    [ActivationDrillFact]
    public async Task Recovery_expiry_cleanup_on_startup_removes_the_expired_copy_and_keeps_the_library()
    {
        await using var drill = await ActivationDrillFixture.CreateAsync(populated: true);
        var (jobId, recoveryId) = await ActivatePopulatedAsync(drill);
        var manifest = SelfHostedActivationDocument.Decode<SelfHostedRecoveryManifest>(
            File.ReadAllText(drill.RecoveryManifestPath(recoveryId)));
        manifest.RetentionReservationId.Should().NotBeNull();

        // A clock jump past the seven-day expiry; the next startup's first
        // cleanup pass (configured to run immediately) must sweep it.
        drill.ExpireRecoveryCopy(recoveryId);

        await using var host = await ActivationDrillHost.StartAsync(
            drill,
            extraEnvironment: new Dictionary<string, string>
            {
                ["ActivationMaintenance__StartupDelaySeconds"] = "0",
            });

        // Physical removal and the durable recovery-status projection are two
        // steps of the same cleanup pass; wait for both before asserting.
        var deadline = DateTime.UtcNow.AddSeconds(120);
        var stillAvailable = true;
        while (DateTime.UtcNow < deadline)
        {
            var activation = await host.GetActivationAsync(jobId);
            stillAvailable = activation.RootElement.GetProperty("recoveryAvailable").GetBoolean();
            activation.Dispose();
            if (!stillAvailable && !Directory.Exists(drill.RecoveryDirectory(recoveryId)))
            {
                break;
            }

            await Task.Delay(200);
        }

        Directory.Exists(drill.RecoveryDirectory(recoveryId)).Should().BeFalse(
            "the expired recovery copy must be physically removed by the startup cleanup pass");
        stillAvailable.Should().BeFalse(
            "the activation status must stop reporting a copy that no longer exists");
        (await drill.ReservationReleasedAtAsync(manifest.RetentionReservationId!.Value))
            .Should().NotBeNull("the copy's reservation is released only after physical cleanup");

        var status = await host.SendAsync(
            HttpMethod.Get, $"/api/portability/migration/recovery/{recoveryId}");
        status.Status.Should().Be(HttpStatusCode.NotFound);
        status.Body.Dispose();

        await drill.AssertImportedGenerationAsync();
    }

    /// <summary>Runs one real populated activation to completion and returns both ids.</summary>
    private static async Task<(Guid JobId, Guid RecoveryId)> ActivatePopulatedAsync(ActivationDrillFixture drill)
    {
        await using var host = await ActivationDrillHost.StartAsync(drill);
        var jobId = await host.UploadImportAsync("drill-activate-");
        using var completed = await host.ActivateToCompletionAsync(
            jobId, await host.CurrentRevisionAsync(), confirm: true);
        completed.RootElement.GetProperty("recoveryAvailable").GetBoolean().Should().BeTrue();
        await drill.AssertImportedGenerationAsync();
        return (jobId, jobId);
    }
}
