using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// #681 Slice 8 over a real in-process SelfHosted host: real Kestrel, real
/// SQLite, real media files and the real activation coordinator behind the HTTP
/// routes. Every test starts activation through the API; the coordinator and
/// dispatcher are gated only through their existing test seams so the pre-
/// maintenance and exclusive windows can be held open deterministically.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class MigrationActivationHttpTests
{
    private const string ActivatePath = "/api/portability/migration/jobs/{0}/activate";
    private const string ActivationPath = "/api/portability/migration/jobs/{0}/activation";

    [Fact]
    public async Task Empty_destination_activation_over_http_serves_the_imported_library()
    {
        var template = ActivationCoordinatorTemplate.For(populated: false);
        await using var h = StartHost();
        var jobId = await UploadImportAsync(h, template);

        var before = await GetActivationAsync(h, jobId);
        before.Status.Should().Be(HttpStatusCode.OK);
        before.Body.RootElement.GetProperty("state").GetString().Should().Be("ReadyToActivate");
        before.Body.RootElement.GetProperty("outcome").GetString().Should().Be("Idle");
        before.Body.RootElement.GetProperty("accepted").GetBoolean().Should().BeFalse();
        before.Body.RootElement.GetProperty("canActivate").GetBoolean().Should().BeTrue();
        var revision = before.Body.RootElement.GetProperty("destinationRevision").GetString();
        revision.Should().NotBeNullOrEmpty("the browser confirms against the stored revision");
        before.Body.Dispose();

        var accepted = await ActivateAsync(h, jobId, revision!, confirm: false);
        accepted.Status.Should().Be(HttpStatusCode.Accepted);
        accepted.Body.RootElement.GetProperty("outcome").GetString().Should().BeOneOf("Accepted", "Running");
        accepted.Body.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
        accepted.Body.RootElement.GetProperty("canActivate").GetBoolean().Should().BeFalse();
        accepted.Body.Dispose();

        using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
        completed.RootElement.GetProperty("state").GetString().Should().Be("Completed");
        completed.RootElement.GetProperty("accepted").GetBoolean().Should().BeFalse();
        completed.RootElement.GetProperty("canActivate").GetBoolean().Should().BeFalse();
        completed.RootElement.GetProperty("recoveryAvailable").GetBoolean().Should().BeFalse(
            "an empty destination has no retained recovery copy");

        var catalogue = await h.Client.GetStringAsync("/api/books");
        catalogue.Should().Contain("Portable EPUB");

        var file = await h.Client.GetAsync($"/api/books/{template.SourceIds.EpubBookId}/file");
        file.EnsureSuccessStatusCode();
        (await file.Content.ReadAsByteArrayAsync()).Should().Equal("EPUB-CONTENT-PORTABLE"u8.ToArray());

        // A call after completion replays the completed status instead of
        // starting a second cutover.
        var replay = await ActivateAsync(h, jobId, revision!, confirm: false);
        replay.Status.Should().Be(HttpStatusCode.Accepted);
        replay.Body.RootElement.GetProperty("outcome").GetString().Should().Be("Completed");
        replay.Body.Dispose();
        h.GetService<SelfHostedActivationDispatcher>().StartedRunCount.Should().Be(1);
    }

    [Fact]
    public async Task Populated_destination_requires_confirmation_and_retains_a_recovery_copy()
    {
        var template = ActivationCoordinatorTemplate.For(populated: true);
        await using var h = StartHost();
        await SeedPopulatedAsync(h, template);
        var jobId = await UploadImportAsync(h, template);

        var before = await GetActivationAsync(h, jobId);
        var revision = before.Body.RootElement.GetProperty("destinationRevision").GetString();
        revision.Should().NotBeNullOrEmpty();
        before.Body.Dispose();

        var refused = await ActivateAsync(h, jobId, revision!, confirm: false);
        refused.Status.Should().Be(HttpStatusCode.Conflict);
        refused.Body.RootElement.GetProperty("error").GetString()
            .Should().Be("migration_replacement_confirmation_required");
        refused.Body.RootElement.GetProperty("destinationRevision").GetString().Should().Be(revision);
        refused.Body.RootElement.GetProperty("destinationStatus").GetString().Should().Be("Populated");
        refused.Body.RootElement.GetProperty("existingCounts").GetProperty("books").GetInt64()
            .Should().BeGreaterThan(0);
        refused.Body.Dispose();
        h.GetService<SelfHostedActivationDispatcher>().StartedRunCount.Should().Be(0);

        var accepted = await ActivateAsync(h, jobId, revision!, confirm: true);
        accepted.Status.Should().Be(HttpStatusCode.Accepted);
        accepted.Body.Dispose();

        using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
        completed.RootElement.GetProperty("recoveryAvailable").GetBoolean().Should().BeTrue(
            "a populated destination retains the previous library");
        completed.RootElement.GetProperty("recoveryExpiresAtUtc").ValueKind.Should().Be(JsonValueKind.String);
        completed.RootElement.GetProperty("recoverySizeBytes").GetInt64().Should().BeGreaterThan(0);

        var catalogue = await h.Client.GetStringAsync("/api/books");
        catalogue.Should().Contain("Portable EPUB");
        catalogue.Should().NotContain("LIVE-ONLY-BOOK");
    }

    [Fact]
    public async Task Blind_confirmation_without_the_server_revision_cannot_activate()
    {
        var template = ActivationCoordinatorTemplate.For(populated: true);
        await using var h = StartHost();
        await SeedPopulatedAsync(h, template);
        var jobId = await UploadImportAsync(h, template);

        // A single destructive POST without the server-returned revision is the
        // migration 400, not an activation.
        var blind = await h.PostJsonAsync(
            string.Format(ActivatePath, jobId),
            new { confirmReplacement = true });
        blind.Status.Should().Be(HttpStatusCode.BadRequest);
        blind.Body.RootElement.GetProperty("error").GetString().Should().Be("migration_invalid_request");
        blind.Body.Dispose();

        // A confirmation bound to any other revision is the documented conflict.
        var wrong = await ActivateAsync(h, jobId, "not-the-server-revision", confirm: true);
        wrong.Status.Should().Be(HttpStatusCode.Conflict);
        wrong.Body.RootElement.GetProperty("error").GetString().Should().Be("migration_destination_conflict");
        wrong.Body.Dispose();

        h.GetService<SelfHostedActivationDispatcher>().StartedRunCount.Should().Be(0);
        (await h.Client.GetStringAsync("/api/books")).Should().Contain("LIVE-ONLY-BOOK");

        var status = await GetActivationAsync(h, jobId);
        status.Body.RootElement.GetProperty("state").GetString().Should().Be("ReadyToActivate");
        status.Body.RootElement.GetProperty("canActivate").GetBoolean().Should().BeTrue();
        status.Body.Dispose();
    }

    [Fact]
    public async Task Populated_library_changed_after_job_creation_is_confirmable_with_the_current_revision()
    {
        var template = ActivationCoordinatorTemplate.For(populated: true);
        await using var h = StartHost();
        await SeedPopulatedAsync(h, template);
        var noteId = await SeedNoteAsync(h);
        var bookId = await h.WithDb(db => db.Books.Select(book => book.Id).FirstAsync());
        var jobId = await UploadImportAsync(h, template);

        var baseline = await GetActivationAsync(h, jobId);
        baseline.Body.RootElement.GetProperty("destinationRevision").GetString().Should().NotBeNullOrEmpty();
        baseline.Body.Dispose();

        // Real in-use activity after the prepared import exists: a note edit
        // and a reading-progress save. Both advance the live revision.
        var current = await ApplyRealPortableWritesAsync(h, noteId, bookId);

        // The durable job keeps reporting its import-start baseline.
        var after = await GetActivationAsync(h, jobId);
        var stored = after.Body.RootElement.GetProperty("destinationRevision").GetString();
        after.Body.Dispose();
        stored.Should().NotBeNullOrEmpty();
        stored.Should().NotBe(current, "the live library moved after the job was created");

        // A request without confirmation learns the CURRENT facts and why.
        var refused = await ActivateAsync(h, jobId, stored!, confirm: false);
        refused.Status.Should().Be(HttpStatusCode.Conflict);
        refused.Body.RootElement.GetProperty("error").GetString()
            .Should().Be("migration_replacement_confirmation_required");
        refused.Body.RootElement.GetProperty("destinationRevision").GetString().Should().Be(current);
        refused.Body.RootElement.GetProperty("destinationStatus").GetString().Should().Be("Populated");
        refused.Body.RootElement.GetProperty("existingCounts").GetProperty("books").GetInt64()
            .Should().BeGreaterThan(0);
        refused.Body.RootElement.GetProperty("changedSinceImportStarted").GetBoolean().Should().BeTrue(
            "the live revision differs from the import-start baseline");
        refused.Body.Dispose();

        // Confirming the stale import-start revision is still refused and
        // reports the revision the user must review instead.
        var stale = await ActivateAsync(h, jobId, stored!, confirm: true);
        stale.Status.Should().Be(HttpStatusCode.Conflict);
        stale.Body.RootElement.GetProperty("error").GetString().Should().Be("migration_destination_conflict");
        stale.Body.RootElement.GetProperty("destinationRevision").GetString().Should().Be(current);
        stale.Body.Dispose();
        h.GetService<SelfHostedActivationDispatcher>().StartedRunCount.Should().Be(0);

        // The revision the user just reviewed activates; the replaced
        // library is retained as the recovery copy.
        var accepted = await ActivateAsync(h, jobId, current, confirm: true);
        accepted.Status.Should().Be(HttpStatusCode.Accepted);
        accepted.Body.Dispose();

        using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
        completed.RootElement.GetProperty("recoveryAvailable").GetBoolean().Should().BeTrue();
        var catalogue = await h.Client.GetStringAsync("/api/books");
        catalogue.Should().Contain("Portable EPUB");
        catalogue.Should().NotContain("LIVE-ONLY-BOOK");
    }

    [Fact]
    public async Task Write_after_confirmed_admission_aborts_with_fresh_facts_and_a_new_confirmation_succeeds()
    {
        var template = ActivationCoordinatorTemplate.For(populated: true);
        await using var h = StartHost();
        await SeedPopulatedAsync(h, template);
        var noteId = await SeedNoteAsync(h);
        var bookId = await h.WithDb(db => db.Books.Select(book => book.Id).FirstAsync());
        var jobId = await UploadImportAsync(h, template);
        var revision = (await GetActivationAsync(h, jobId)).Body.RootElement
            .GetProperty("destinationRevision").GetString();
        revision.Should().NotBeNullOrEmpty();

        var dispatcher = h.GetService<SelfHostedActivationDispatcher>();
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
            var accepted = await ActivateAsync(h, jobId, revision!, confirm: true);
            accepted.Status.Should().Be(HttpStatusCode.Accepted);
            accepted.Body.Dispose();
            (await Task.Run(() => admitted.Task.Wait(TimeSpan.FromSeconds(60))))
                .Should().BeTrue("the confirmed run must reach its pre-maintenance phase");

            // A real portable write lands after the 202 but before the
            // exclusive window: the confirmed generation is no longer current.
            var current = await ApplyRealPortableWritesAsync(h, noteId, bookId);
            release.SetResult();

            using var failed = await WaitForOutcomeAsync(h, jobId, "Failed");
            failed.RootElement.GetProperty("errorCode").GetString()
                .Should().Be("migration_destination_conflict");
            failed.RootElement.GetProperty("destinationRevision").GetString().Should().Be(current,
                "the background conflict must carry the same fresh facts a 409 would");
            failed.RootElement.GetProperty("destinationStatus").GetString().Should().Be("Populated");
            failed.RootElement.GetProperty("existingCounts").GetProperty("books").GetInt64()
                .Should().BeGreaterThan(0);
            failed.RootElement.GetProperty("canActivate").GetBoolean().Should().BeTrue();
            (await h.Client.GetStringAsync("/api/books")).Should().Contain("LIVE-ONLY-BOOK",
                "an aborted run must not have replaced the destination");

            var again = await ActivateAsync(h, jobId, current, confirm: true);
            again.Status.Should().Be(HttpStatusCode.Accepted);
            again.Body.Dispose();
            using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
            (await h.Client.GetStringAsync("/api/books")).Should().Contain("Portable EPUB");
        }
        finally
        {
            release.TrySetResult();
            dispatcher.CoordinatorCreatedForTesting = null;
        }
    }

    [Fact]
    public async Task Empty_destination_that_became_populated_before_activation_requires_confirmation()
    {
        var template = ActivationCoordinatorTemplate.For(populated: false);
        await using var h = StartHost();
        var jobId = await UploadImportAsync(h, template);

        // The destination becomes populated after the empty job was prepared.
        await SeedPopulatedAsync(h, template);
        var current = await ReadCurrentRevisionAsync(h);

        // The first POST carries the import-start revision without confirmation.
        var status = await GetActivationAsync(h, jobId);
        var stored = status.Body.RootElement.GetProperty("destinationRevision").GetString();
        status.Body.Dispose();
        var refused = await ActivateAsync(h, jobId, stored!, confirm: false);
        refused.Status.Should().Be(HttpStatusCode.Conflict);
        refused.Body.RootElement.GetProperty("error").GetString()
            .Should().Be("migration_replacement_confirmation_required");
        refused.Body.RootElement.GetProperty("destinationRevision").GetString().Should().Be(current);
        refused.Body.RootElement.GetProperty("destinationStatus").GetString().Should().Be("Populated");
        refused.Body.RootElement.GetProperty("changedSinceImportStarted").GetBoolean().Should().BeTrue();
        refused.Body.Dispose();

        var accepted = await ActivateAsync(h, jobId, current, confirm: true);
        accepted.Status.Should().Be(HttpStatusCode.Accepted);
        accepted.Body.Dispose();
        using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
        completed.RootElement.GetProperty("recoveryAvailable").GetBoolean().Should().BeTrue(
            "the now-populated destination is retained");
        (await h.Client.GetStringAsync("/api/books")).Should().Contain("Portable EPUB");
    }

    [Fact]
    public async Task Stale_revision_is_rejected_and_nothing_changes()
    {
        var template = ActivationCoordinatorTemplate.For(populated: true);
        await using var h = StartHost();
        await SeedPopulatedAsync(h, template);
        var jobId = await UploadImportAsync(h, template);

        var conflict = await ActivateAsync(h, jobId, "stale-revision", confirm: true);
        conflict.Status.Should().Be(HttpStatusCode.Conflict);
        conflict.Body.RootElement.GetProperty("error").GetString().Should().Be("migration_destination_conflict");
        conflict.Body.Dispose();

        h.GetService<SelfHostedActivationDispatcher>().StartedRunCount.Should().Be(0);
        var catalogue = await h.Client.GetStringAsync("/api/books");
        catalogue.Should().Contain("LIVE-ONLY-BOOK");

        var status = await GetActivationAsync(h, jobId);
        status.Body.RootElement.GetProperty("state").GetString().Should().Be("ReadyToActivate");
        status.Body.RootElement.GetProperty("outcome").GetString().Should().Be("Idle");
        status.Body.Dispose();
    }

    [Fact]
    public async Task Status_reports_accepted_then_preparing_while_phase_a_runs()
    {
        var template = ActivationCoordinatorTemplate.For(populated: false);
        await using var h = StartHost();
        var jobId = await UploadImportAsync(h, template);
        var revision = (await GetActivationAsync(h, jobId)).Body.RootElement
            .GetProperty("destinationRevision").GetString();
        revision.Should().NotBeNullOrEmpty();

        var dispatcher = h.GetService<SelfHostedActivationDispatcher>();
        var beforeRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var phaseAEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePhaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeforeRunForTesting = (_, ct) => beforeRun.Task.WaitAsync(ct);
        dispatcher.CoordinatorCreatedForTesting = coordinator =>
            coordinator.StepObserverForTesting = step =>
            {
                if (string.Equals(step, SelfHostedActivationSteps.AfterAdmission, StringComparison.Ordinal))
                {
                    phaseAEntered.TrySetResult();
                    releasePhaseA.Task.GetAwaiter().GetResult();
                }
            };

        try
        {
            var accepted = await ActivateAsync(h, jobId, revision!, confirm: false);
            accepted.Status.Should().Be(HttpStatusCode.Accepted);
            accepted.Body.Dispose();

            // Accepted but not started: a distinct queued outcome, not Idle.
            var queued = await GetActivationAsync(h, jobId);
            queued.Status.Should().Be(HttpStatusCode.OK);
            queued.Body.RootElement.GetProperty("outcome").GetString().Should().Be("Accepted");
            queued.Body.RootElement.GetProperty("phase").GetString().Should().Be("Queued");
            queued.Body.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
            queued.Body.Dispose();

            beforeRun.SetResult();
            (await Task.Run(() => phaseAEntered.Task.Wait(TimeSpan.FromSeconds(60))))
                .Should().BeTrue("the run must reach its pre-maintenance phase");

            // The durable row is still ReadyToActivate; the registry keeps the
            // status truthful anyway.
            var durableState = await h.WithDb(db => db.MigrationJobRecords.AsNoTracking()
                .Where(row => row.Id == jobId).Select(row => row.State).SingleAsync());
            durableState.Should().Be((int)MigrationJobState.ReadyToActivate);

            var running = await GetActivationAsync(h, jobId);
            running.Status.Should().Be(HttpStatusCode.OK);
            running.Body.RootElement.GetProperty("outcome").GetString().Should().Be("Running");
            running.Body.RootElement.GetProperty("phase").GetString().Should().Be("Preparing");
            running.Body.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
            running.Body.RootElement.GetProperty("canActivate").GetBoolean().Should().BeFalse();
            running.Body.Dispose();

            releasePhaseA.SetResult();
            using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
            completed.RootElement.GetProperty("state").GetString().Should().Be("Completed");
        }
        finally
        {
            releasePhaseA.TrySetResult();
            dispatcher.BeforeRunForTesting = null;
            dispatcher.CoordinatorCreatedForTesting = null;
        }
    }

    [Fact]
    public async Task Concurrent_activation_calls_start_exactly_one_run()
    {
        var template = ActivationCoordinatorTemplate.For(populated: false);
        await using var h = StartHost();
        var jobId = await UploadImportAsync(h, template);
        var revision = (await GetActivationAsync(h, jobId)).Body.RootElement
            .GetProperty("destinationRevision").GetString();
        revision.Should().NotBeNullOrEmpty();

        var dispatcher = h.GetService<SelfHostedActivationDispatcher>();
        var phaseAEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePhaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.CoordinatorCreatedForTesting = coordinator =>
            coordinator.StepObserverForTesting = step =>
            {
                if (string.Equals(step, SelfHostedActivationSteps.AfterAdmission, StringComparison.Ordinal))
                {
                    phaseAEntered.TrySetResult();
                    releasePhaseA.Task.GetAwaiter().GetResult();
                }
            };

        try
        {
            var calls = Enumerable.Range(0, 4)
                .Select(_ => ActivateAsync(h, jobId, revision!, confirm: false))
                .ToArray();
            var results = await Task.WhenAll(calls);
            try
            {
                results.Should().OnlyContain(result => result.Status == HttpStatusCode.Accepted);
                results.Should().Contain(result =>
                    result.Body.RootElement.GetProperty("outcome").GetString() == "Accepted");
            }
            finally
            {
                foreach (var result in results) result.Body.Dispose();
            }

            (await Task.Run(() => phaseAEntered.Task.Wait(TimeSpan.FromSeconds(60))))
                .Should().BeTrue("the single admitted run must reach Phase A");
            dispatcher.StartedRunCount.Should().Be(1);

            releasePhaseA.SetResult();
            using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
            completed.RootElement.GetProperty("state").GetString().Should().Be("Completed");
            dispatcher.StartedRunCount.Should().Be(1);
            (await h.Client.GetStringAsync("/api/books")).Should().Contain("Portable EPUB");
        }
        finally
        {
            releasePhaseA.TrySetResult();
            dispatcher.CoordinatorCreatedForTesting = null;
        }
    }

    [Fact]
    public async Task Run_registry_admits_exactly_one_of_many_concurrent_requests_200_times()
    {
        var request = new MigrationActivateRequest("revision", ConfirmReplacement: true);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var slot = new SelfHostedActivationRunSlot(Guid.NewGuid());
            var admissions = 0;
            var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                if (slot.TryAdmit(request)) Interlocked.Increment(ref admissions);
            })).ToArray();
            await Task.WhenAll(workers);
            admissions.Should().Be(1, $"iteration {iteration} must admit exactly one run");
            slot.State.Should().Be(SelfHostedActivationRunState.Accepted);
        }
    }

    [Fact]
    public async Task Post_after_a_rolled_back_run_is_allowed_and_succeeds()
    {
        var template = ActivationCoordinatorTemplate.For(populated: false);
        await using var h = StartHost();
        var jobId = await UploadImportAsync(h, template);
        var revision = (await GetActivationAsync(h, jobId)).Body.RootElement
            .GetProperty("destinationRevision").GetString();
        revision.Should().NotBeNullOrEmpty();

        var dispatcher = h.GetService<SelfHostedActivationDispatcher>();
        var throwOnce = 1;
        dispatcher.CoordinatorCreatedForTesting = coordinator =>
            coordinator.StepObserverForTesting = step =>
            {
                if (string.Equals(step, SelfHostedActivationSteps.PhaseCandidatePrepared, StringComparison.Ordinal)
                    && Interlocked.Exchange(ref throwOnce, 0) == 1)
                {
                    throw new InvalidOperationException("synthetic pre-maintenance failure");
                }
            };

        try
        {
            var first = await ActivateAsync(h, jobId, revision!, confirm: false);
            first.Status.Should().Be(HttpStatusCode.Accepted);
            first.Body.Dispose();

            using var failed = await WaitForOutcomeAsync(h, jobId, "Failed");
            failed.RootElement.GetProperty("state").GetString().Should().Be("ReadyToActivate");
            failed.RootElement.GetProperty("accepted").GetBoolean().Should().BeFalse();
            failed.RootElement.GetProperty("canActivate").GetBoolean().Should().BeTrue(
                "a run that failed before the durable transition leaves the job activatable");
            failed.RootElement.GetProperty("errorCode").GetString().Should().Be("migration_activation_failed");
            dispatcher.StartedRunCount.Should().Be(1);

            var second = await ActivateAsync(h, jobId, revision!, confirm: false);
            second.Status.Should().Be(HttpStatusCode.Accepted);
            second.Body.Dispose();

            using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
            completed.RootElement.GetProperty("state").GetString().Should().Be("Completed");
            dispatcher.StartedRunCount.Should().Be(2);
        }
        finally
        {
            dispatcher.CoordinatorCreatedForTesting = null;
        }
    }

    [Fact]
    public async Task Status_answers_during_the_exclusive_window_without_opening_the_live_database()
    {
        var template = ActivationCoordinatorTemplate.For(populated: true);
        await using var h = StartHost();
        await SeedPopulatedAsync(h, template);
        var jobId = await UploadImportAsync(h, template);
        var otherJobId = await UploadImportAsync(h, template);
        var revision = (await GetActivationAsync(h, jobId)).Body.RootElement
            .GetProperty("destinationRevision").GetString();
        revision.Should().NotBeNullOrEmpty();

        var dispatcher = h.GetService<SelfHostedActivationDispatcher>();
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        dispatcher.CoordinatorCreatedForTesting = coordinator =>
            coordinator.StepObserverForTesting = step =>
            {
                if (string.Equals(step, SelfHostedActivationSteps.AfterQuiesce, StringComparison.Ordinal))
                {
                    entered.Release();
                    release.Wait(TimeSpan.FromSeconds(60));
                }
            };

        var livePath = Path.GetFullPath(h.DatabasePath);
        var opened = new List<string>();
        try
        {
            var accepted = await ActivateAsync(h, jobId, revision!, confirm: true);
            accepted.Status.Should().Be(HttpStatusCode.Accepted);
            accepted.Body.Dispose();

            (await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(60))))
                .Should().BeTrue("activation must reach the exclusive window");

            File.Exists(livePath + "-shm").Should().BeFalse("the window starts quiesced");
            File.Exists(livePath + "-wal").Should().BeFalse();

            // Arm the seam only now: the coordinator's own quiesce/reopen
            // opens are legitimate and happen outside the status request.
            SelfHostedSqliteFile.ConnectionOpeningForTesting = path =>
            {
                lock (opened) opened.Add(Path.GetFullPath(path));
            };

            var during = await GetActivationAsync(h, jobId);
            during.Status.Should().Be(HttpStatusCode.OK);
            during.Body.RootElement.GetProperty("state").GetString().Should().Be("Activating");
            during.Body.RootElement.GetProperty("outcome").GetString().Should().Be("Running");
            during.Body.RootElement.GetProperty("phase").GetString().Should().Be("Activating");
            during.Body.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
            during.Body.Dispose();

            // A duplicate POST for the running job replays from memory; another
            // prepared job gets the documented busy answer. Neither may open the
            // live database.
            var duplicate = await ActivateAsync(h, jobId, revision!, confirm: true);
            duplicate.Status.Should().Be(HttpStatusCode.Accepted);
            duplicate.Body.RootElement.GetProperty("outcome").GetString().Should().Be("Running");
            duplicate.Body.Dispose();

            var other = await ActivateAsync(h, otherJobId, revision!, confirm: true);
            other.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
            other.Body.RootElement.GetProperty("error").GetString().Should().Be("migration_activation_busy");
            other.Body.Dispose();

            lock (opened) opened.Should().NotContain(livePath);
            File.Exists(livePath + "-shm").Should().BeFalse(
                "the status endpoint must not open the live database inside the window");
            File.Exists(livePath + "-wal").Should().BeFalse();
            dispatcher.StartedRunCount.Should().Be(1);

            var books = await h.SendAsync(HttpMethod.Get, "/api/books");
            books.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
            books.Body.RootElement.GetProperty("code").GetString().Should().Be("migration_activation_busy");
            books.Body.Dispose();

            release.Release();
            using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
            completed.RootElement.GetProperty("state").GetString().Should().Be("Completed");
        }
        finally
        {
            release.Release();
            SelfHostedSqliteFile.ConnectionOpeningForTesting = null;
            dispatcher.CoordinatorCreatedForTesting = null;
        }
    }

    [Fact]
    public async Task Restart_after_accepted_202_reports_idle_can_activate_and_repost_completes()
    {
        var template = ActivationCoordinatorTemplate.For(populated: false);
        await using var h = StartHost();
        var jobId = await UploadImportAsync(h, template);
        var revision = (await GetActivationAsync(h, jobId)).Body.RootElement
            .GetProperty("destinationRevision").GetString();
        revision.Should().NotBeNullOrEmpty();

        // Hold the accepted run before it starts; a host restart then drops it
        // exactly like a process crash before the durable transition.
        var dispatcher = h.GetService<SelfHostedActivationDispatcher>();
        dispatcher.BeforeRunForTesting = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);

        var accepted = await ActivateAsync(h, jobId, revision!, confirm: false);
        accepted.Status.Should().Be(HttpStatusCode.Accepted);
        accepted.Body.Dispose();

        await h.RestartAsync();

        // The lost 202 contract: the job is untouched, no run exists, and the
        // client is told to repeat the POST.
        var afterRestart = await GetActivationAsync(h, jobId);
        afterRestart.Status.Should().Be(HttpStatusCode.OK);
        afterRestart.Body.RootElement.GetProperty("state").GetString().Should().Be("ReadyToActivate");
        afterRestart.Body.RootElement.GetProperty("outcome").GetString().Should().Be("Idle");
        afterRestart.Body.RootElement.GetProperty("accepted").GetBoolean().Should().BeFalse();
        afterRestart.Body.RootElement.GetProperty("canActivate").GetBoolean().Should().BeTrue();
        afterRestart.Body.Dispose();

        var restarted = h.GetService<SelfHostedActivationDispatcher>();
        restarted.StartedRunCount.Should().Be(0);

        var repost = await ActivateAsync(h, jobId, revision!, confirm: false);
        repost.Status.Should().Be(HttpStatusCode.Accepted);
        repost.Body.Dispose();

        using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
        completed.RootElement.GetProperty("state").GetString().Should().Be("Completed");
        restarted.StartedRunCount.Should().Be(1);
        (await h.Client.GetStringAsync("/api/books")).Should().Contain("Portable EPUB");
    }

    [Fact]
    public async Task Fail_closed_recovery_state_is_reported_with_the_operator_message()
    {
        var template = ActivationCoordinatorTemplate.For(populated: true);
        await using var h = StartHost();
        await SeedPopulatedAsync(h, template);
        var jobId = await UploadImportAsync(h, template);
        var revision = (await GetActivationAsync(h, jobId)).Body.RootElement
            .GetProperty("destinationRevision").GetString();
        revision.Should().NotBeNullOrEmpty();

        var dispatcher = h.GetService<SelfHostedActivationDispatcher>();
        dispatcher.CoordinatorCreatedForTesting = coordinator =>
            coordinator.StepObserverForTesting = step =>
            {
                if (string.Equals(step, SelfHostedActivationSteps.AfterReopen, StringComparison.Ordinal))
                {
                    StripJobLease(h.DatabasePath);
                }
            };

        try
        {
            var accepted = await ActivateAsync(h, jobId, revision!, confirm: true);
            accepted.Status.Should().Be(HttpStatusCode.Accepted);
            accepted.Body.Dispose();

            using var failed = await WaitForOutcomeAsync(h, jobId, "RecoveryFailed");
            failed.RootElement.GetProperty("maintenanceRequired").GetBoolean().Should().BeTrue();
            failed.RootElement.GetProperty("accepted").GetBoolean().Should().BeFalse();
            failed.RootElement.GetProperty("canActivate").GetBoolean().Should().BeFalse();
            failed.RootElement.GetProperty("errorCode").GetString()
                .Should().Be("migration_activation_recovery_failed");
            failed.RootElement.GetProperty("message").GetString().Should().Contain("maintenance");

            var other = await h.SendAsync(HttpMethod.Get, "/api/books");
            other.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
            other.Body.RootElement.GetProperty("code").GetString().Should().Be("migration_activation_busy");
            other.Body.Dispose();
        }
        finally
        {
            dispatcher.CoordinatorCreatedForTesting = null;
        }
    }

    [Fact]
    public async Task Wrong_states_unknown_ids_and_malformed_bodies_use_the_documented_codes()
    {
        await using var h = StartHost();

        // Unknown id: identical typed 404 on both new routes.
        var unknown = Guid.NewGuid();
        var notFound = await ActivateAsync(h, unknown, "1", confirm: true);
        notFound.Status.Should().Be(HttpStatusCode.NotFound);
        notFound.Body.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().Equal("error", "message");
        notFound.Body.RootElement.GetProperty("error").GetString().Should().Be("migration_not_found");
        notFound.Body.Dispose();

        var unknownStatus = await GetActivationAsync(h, unknown);
        unknownStatus.Status.Should().Be(HttpStatusCode.NotFound);
        unknownStatus.Body.RootElement.GetProperty("error").GetString().Should().Be("migration_not_found");
        unknownStatus.Body.Dispose();

        // Malformed body: missing destination revision is the migration 400.
        var malformed = await h.PostJsonAsync(
            string.Format(ActivatePath, unknown),
            new { confirmReplacement = true });
        malformed.Status.Should().Be(HttpStatusCode.BadRequest);
        malformed.Body.RootElement.GetProperty("error").GetString().Should().Be("migration_invalid_request");
        malformed.Body.Dispose();

        // An export job never enters import activation.
        var export = await h.CreateJobAsync("Export", "activation-export-" + Guid.NewGuid().ToString("N"), null);
        export.Status.Should().Be(HttpStatusCode.Created);
        var exportId = export.Body.RootElement.GetProperty("job").GetProperty("id").GetGuid();
        export.Body.Dispose();
        var wrongDirection = await ActivateAsync(h, exportId, "1", confirm: true);
        wrongDirection.Status.Should().Be(HttpStatusCode.Conflict);
        wrongDirection.Body.RootElement.GetProperty("error").GetString().Should().Be("migration_invalid_state");
        wrongDirection.Body.Dispose();

        // A job still transferring cannot be activated.
        var pendingId = await h.CreateImportJobAsync("activation-pending-" + Guid.NewGuid().ToString("N"));
        var pending = await ActivateAsync(h, pendingId, "1", confirm: true);
        pending.Status.Should().Be(HttpStatusCode.Conflict);
        pending.Body.RootElement.GetProperty("error").GetString().Should().Be("migration_invalid_state");
        pending.Body.Dispose();
    }

    private static MigrationHttpHarness StartHost(Action<IServiceCollection>? configure = null)
    {
        var harness = new MigrationHttpHarness
        {
            // Real preparation, not the transport probe: the upload flow must
            // reach ReadyToActivate through the production handler.
            UseRealPhaseHandlers = true,
            ConfigureServices = services =>
            {
                services.AddHostedService(sp => sp.GetRequiredService<SelfHostedActivationDispatcher>());
                configure?.Invoke(services);
            },
        };
        return harness.Start();
    }

    private static async Task<Guid> UploadImportAsync(
        MigrationHttpHarness h,
        ActivationCoordinatorTemplate template)
    {
        var bytes = await File.ReadAllBytesAsync(template.ArchivePath);
        var jobId = await h.CreateImportJobAsync("activation-" + Guid.NewGuid().ToString("N"));
        const int chunkSize = 4 * 1024 * 1024;
        var session = await h.CreateSessionAsync(jobId, bytes, chunkSize, key: "activation-session");
        session.Status.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        session.Body.Dispose();

        var chunks = MigrationHttpHarness.ChunkCount(bytes.LongLength, chunkSize);
        for (var index = 0; index < chunks; index++)
        {
            var chunk = await h.UploadChunkAsync(jobId, bytes, index, chunkSize);
            chunk.Status.Should().Be(HttpStatusCode.OK);
            chunk.Body.Dispose();
        }

        var complete = await h.CompleteAsync(jobId);
        complete.Status.Should().Be(HttpStatusCode.OK);
        complete.Body.Dispose();

        using var ready = await h.WaitForJobStateAsync(jobId, "ReadyToActivate", TimeSpan.FromSeconds(60));
        return jobId;
    }

    private static async Task SeedPopulatedAsync(
        MigrationHttpHarness h,
        ActivationCoordinatorTemplate template)
    {
        await h.WithDb(async db =>
        {
            var state = await db.LibraryStates.SingleOrDefaultAsync(row => row.Id == LibraryState.WellKnownId);
            if (state is null)
            {
                db.LibraryStates.Add(new LibraryState { StateVersion = "77" });
            }
            else
            {
                state.StateVersion = "77";
            }

            db.Books.Add(new PhysicalBookModel { Title = "LIVE-ONLY-BOOK" });
            await db.SaveChangesAsync();
        });

        CopyDirectory(template.TemplateMedia, Path.Combine(h.Root, "books"));
    }

    /// <summary>
    /// Seeds one live note (with its work and book) before the import job is
    /// created, so the test can make a real note EDIT after the job exists.
    /// </summary>
    private static Task<Guid> SeedNoteAsync(MigrationHttpHarness h) =>
        h.WithDb(async db =>
        {
            var work = new WorkModel
            {
                Id = Guid.NewGuid(),
                Title = "Live work",
                NormalizedTitle = "LIVE WORK",
                NormalizedAuthor = string.Empty,
                CreatedAt = DateTime.UtcNow,
            };
            var book = new EBookModel
            {
                Id = Guid.NewGuid(),
                WorkId = work.Id,
                Work = work,
                Title = "Live book",
                CreatedAt = DateTime.UtcNow,
            };
            var note = new NoteModel
            {
                Id = Guid.NewGuid(),
                BookId = book.Id,
                Book = book,
                Content = "original note",
                CreatedAt = DateTime.UtcNow,
            };
            db.Works.Add(work);
            db.Books.Add(book);
            db.Notes.Add(note);
            var state = await db.LibraryStates.SingleAsync();
            await db.SaveChangesAsync();
            // Stamp the fixture revision in a non-portable save, matching the
            // populated seed's deterministic baseline.
            state.StateVersion = "77";
            await db.SaveChangesAsync();
            return note.Id;
        });

    /// <summary>
    /// Real service-path portable writes: a note content edit and a reading
    /// progress save. Both advance the live revision through the pipeline and
    /// return the current destination revision token after the writes.
    /// </summary>
    private static async Task<string> ApplyRealPortableWritesAsync(
        MigrationHttpHarness h,
        Guid noteId,
        Guid bookId)
    {
        await using var scope = h.GetService<IServiceScopeFactory>().CreateAsyncScope();
        var updated = await scope.ServiceProvider.GetRequiredService<INoteService>()
            .UpdateAsync(noteId, new UpdateNoteDto("edited after the import started"));
        updated.Success.Should().BeTrue(updated.ErrorMessage);
        var progress = await scope.ServiceProvider.GetRequiredService<ILibraryService>()
            .UpdateProgressAsync(bookId, "epubcfi(/6/4!/4/2)", 42);
        progress.StateVersion.Should().NotBeNullOrEmpty();
        return await scope.ServiceProvider.GetRequiredService<ILibraryDestinationRevisionProvider>()
            .GetCurrentAsync(default);
    }

    private static async Task<string> ReadCurrentRevisionAsync(MigrationHttpHarness h)
    {
        await using var scope = h.GetService<IServiceScopeFactory>().CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILibraryDestinationRevisionProvider>()
            .GetCurrentAsync(default);
    }

    private static Task<(HttpStatusCode Status, JsonDocument Body)> ActivateAsync(
        MigrationHttpHarness h,
        Guid jobId,
        string revision,
        bool confirm) =>
        h.PostJsonAsync(
            string.Format(ActivatePath, jobId),
            new { destinationRevision = revision, confirmReplacement = confirm });

    private static Task<(HttpStatusCode Status, JsonDocument Body)> GetActivationAsync(
        MigrationHttpHarness h,
        Guid jobId) =>
        h.SendAsync(HttpMethod.Get, string.Format(ActivationPath, jobId));

    private static async Task<JsonDocument> WaitForOutcomeAsync(
        MigrationHttpHarness h,
        Guid jobId,
        string outcome,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        JsonDocument? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var (status, body) = await GetActivationAsync(h, jobId);
            if (status == HttpStatusCode.OK
                && body.RootElement.GetProperty("outcome").GetString() == outcome)
            {
                last?.Dispose();
                return body;
            }

            last?.Dispose();
            last = body;
            h.Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(25);
        }

        var observed = last is null ? "<none>" : last.RootElement.GetRawText();
        last?.Dispose();
        throw new Xunit.Sdk.XunitException(
            $"Job {jobId} did not reach activation outcome {outcome}; last status was {observed}.");
    }

    private static void StripJobLease(string databasePath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE \"MigrationJobRecords\" SET \"MigrationLeaseToken\" = NULL, \"LeaseExpiresAtUtc\" = NULL;";
        command.ExecuteNonQuery();
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
