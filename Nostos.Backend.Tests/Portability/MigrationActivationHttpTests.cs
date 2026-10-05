using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// #681 Slice 8 over a real in-process SelfHosted host: real Kestrel, real
/// SQLite, real media files and the real activation coordinator behind the HTTP
/// routes. Every test starts activation through the API; the coordinator is
/// gated only through its existing step observer so the exclusive window can be
/// held open deterministically.
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
        before.Body.RootElement.GetProperty("destinationStatus").ValueKind.Should().Be(JsonValueKind.Null);
        var revision = before.Body.RootElement.GetProperty("destinationRevision").GetString();
        revision.Should().NotBeNullOrEmpty("the browser confirms against the stored revision");
        before.Body.Dispose();

        var accepted = await ActivateAsync(h, jobId, revision!, confirm: false);
        accepted.Status.Should().Be(HttpStatusCode.Accepted);
        accepted.Body.RootElement.GetProperty("outcome").GetString().Should().Be("Running");
        accepted.Body.Dispose();

        using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
        completed.RootElement.GetProperty("state").GetString().Should().Be("Completed");
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
    public async Task Duplicate_activation_calls_start_exactly_one_run()
    {
        var template = ActivationCoordinatorTemplate.For(populated: false);
        await using var h = StartHost();
        var jobId = await UploadImportAsync(h, template);
        var revision = (await GetActivationAsync(h, jobId)).Body.RootElement.GetProperty("destinationRevision").GetString();
        revision.Should().NotBeNullOrEmpty();

        var first = ActivateAsync(h, jobId, revision!, confirm: false);
        var second = ActivateAsync(h, jobId, revision!, confirm: false);
        var results = await Task.WhenAll(first, second);
        try
        {
            results.Should().Contain(result => result.Status == HttpStatusCode.Accepted,
                "at least one call is accepted while the other observes or waits out the run");
            results.Should().OnlyContain(result =>
                result.Status == HttpStatusCode.Accepted
                || result.Status == HttpStatusCode.Conflict
                || result.Status == HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            foreach (var result in results) result.Body.Dispose();
        }

        using var completed = await WaitForOutcomeAsync(h, jobId, "Completed");
        completed.RootElement.GetProperty("state").GetString().Should().Be("Completed");
        h.GetService<SelfHostedActivationDispatcher>().StartedRunCount.Should().Be(1);
        (await h.Client.GetStringAsync("/api/books")).Should().Contain("Portable EPUB");
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

    [Fact]
    public async Task Status_answers_during_the_exclusive_window_without_opening_the_live_database()
    {
        var template = ActivationCoordinatorTemplate.For(populated: true);
        await using var h = StartHost();
        await SeedPopulatedAsync(h, template);
        var jobId = await UploadImportAsync(h, template);
        var revision = (await GetActivationAsync(h, jobId)).Body.RootElement.GetProperty("destinationRevision").GetString();
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
            during.Body.Dispose();

            lock (opened) opened.Should().NotContain(livePath);
            File.Exists(livePath + "-shm").Should().BeFalse(
                "the status endpoint must not open the live database inside the window");
            File.Exists(livePath + "-wal").Should().BeFalse();

            var other = await h.SendAsync(HttpMethod.Get, "/api/books");
            other.Status.Should().Be(HttpStatusCode.ServiceUnavailable);
            other.Body.RootElement.GetProperty("code").GetString().Should().Be("migration_activation_busy");
            other.Body.Dispose();

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
    public async Task Fail_closed_recovery_state_is_reported_with_the_operator_message()
    {
        var template = ActivationCoordinatorTemplate.For(populated: true);
        await using var h = StartHost();
        await SeedPopulatedAsync(h, template);
        var jobId = await UploadImportAsync(h, template);
        var revision = (await GetActivationAsync(h, jobId)).Body.RootElement.GetProperty("destinationRevision").GetString();
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

    private static MigrationHttpHarness StartHost(Action<IServiceCollection>? configure = null)
    {
        var harness = new MigrationHttpHarness
        {
            ConfigureServices = services =>
            {
                // Real preparation, not the transport probe: the upload flow
                // must reach ReadyToActivate through the production handler.
                services.RemoveAll<IMigrationPhaseHandler>();
                services.AddScoped<IMigrationPhaseHandler, ImportPreparationPhaseHandler>();
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
