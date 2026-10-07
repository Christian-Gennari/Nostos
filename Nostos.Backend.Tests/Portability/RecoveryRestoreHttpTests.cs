using System.Net;
using FluentAssertions;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 9 HTTP surface: list, status, restore with/without confirmation,
/// unknown/expired/corrupt mappings, the migration error body, and a status
/// route that keeps answering while the exclusive window is open. The restore
/// continues outside the request; closing the client never cancels it.
/// </summary>
[Collection(ActivationCoordinatorCollection.Name)]
public sealed class RecoveryRestoreHttpTests
{
    private const string BasePath = "/api/portability/migration/recovery";

    [Fact]
    public async Task ListAndStatus_ReturnRetainedCopies_WithoutPaths()
    {
        await using var harness = await RecoveryHttpHarness.StartAsync();

        var (listStatus, list) = await harness.SendAsync(HttpMethod.Get, BasePath);
        listStatus.Should().Be(HttpStatusCode.OK);
        list.RootElement.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        list.RootElement.GetArrayLength().Should().Be(1);
        var item = list.RootElement[0];
        item.GetProperty("jobId").GetGuid().Should().Be(harness.Bed.RecoveryId);
        item.GetProperty("status").GetString().Should().Be("Available");
        item.GetProperty("sizeBytes").GetInt64().Should().BeGreaterThan(0);
        list.RootElement.GetRawText().Should().NotContain(harness.Bed.Root,
            "no filesystem path may reach a response body");

        var (statusStatus, status) = await harness.SendAsync(
            HttpMethod.Get, $"{BasePath}/{harness.Bed.RecoveryId}");
        statusStatus.Should().Be(HttpStatusCode.OK);
        status.RootElement.GetProperty("recoveryId").GetGuid().Should().Be(harness.Bed.RecoveryId);
        status.RootElement.GetProperty("status").GetString().Should().Be("Available");
        status.RootElement.GetProperty("outcome").GetString().Should().Be("Idle");
        status.RootElement.GetProperty("accepted").GetBoolean().Should().BeFalse();
        status.RootElement.GetProperty("canRestore").GetBoolean().Should().BeTrue();
        status.RootElement.GetProperty("maintenanceRequired").GetBoolean().Should().BeFalse();
        status.RootElement.GetProperty("phase").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        var counts = status.RootElement.GetProperty("counts");
        counts.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
        counts.GetProperty("books").GetInt64()
            .Should().Be(harness.Bed.ReadRecoveryManifest()!.Counts.Books);

        var (missingStatus, missing) = await harness.SendAsync(
            HttpMethod.Get, $"{BasePath}/{Guid.NewGuid()}");
        missingStatus.Should().Be(HttpStatusCode.NotFound);
        missing.RootElement.GetProperty("error").GetString().Should().Be(MigrationHttpErrors.RecoveryNotFound);
    }

    [Fact]
    public async Task RestoreWithoutConfirmation_AnswersTheMigrationErrorBody()
    {
        await using var harness = await RecoveryHttpHarness.StartAsync();
        var token = await harness.RevisionTokenAsync();

        var (status, body) = await harness.PostJsonAsync(
            $"{BasePath}/{harness.Bed.RecoveryId}/restore",
            new { destinationRevision = token, confirmReplacement = false });

        status.Should().Be(HttpStatusCode.Conflict);
        body.RootElement.GetProperty("error").GetString()
            .Should().Be(MigrationActivationErrorCodes.ConfirmationRequired);
        body.RootElement.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        harness.Bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Available);
    }

    [Fact]
    public async Task RestoreWithAStaleRevision_AnswersDestinationConflict()
    {
        await using var harness = await RecoveryHttpHarness.StartAsync();

        var (status, body) = await harness.PostJsonAsync(
            $"{BasePath}/{harness.Bed.RecoveryId}/restore",
            new { destinationRevision = "stale-revision", confirmReplacement = true });

        status.Should().Be(HttpStatusCode.Conflict);
        body.RootElement.GetProperty("error").GetString()
            .Should().Be(MigrationActivationErrorCodes.DestinationConflict);
        harness.Bed.ReadRecoveryManifest()!.Status.Should().Be(MigrationRecoveryStatus.Available);
    }

    [Fact]
    public async Task RestoreWithConfirmation_Returns202_AndCompletesOutsideTheRequest()
    {
        await using var harness = await RecoveryHttpHarness.StartAsync();
        var token = await harness.RevisionTokenAsync();

        var (accepted, acceptedBody) = await harness.PostJsonAsync(
            $"{BasePath}/{harness.Bed.RecoveryId}/restore",
            new { destinationRevision = token, confirmReplacement = true });
        accepted.Should().Be(HttpStatusCode.Accepted);
        acceptedBody.RootElement.GetProperty("status").GetString().Should().Be("Restoring");
        acceptedBody.RootElement.GetProperty("outcome").GetString().Should().Be("Accepted");
        acceptedBody.RootElement.GetProperty("accepted").GetBoolean().Should().BeTrue();
        acceptedBody.RootElement.GetProperty("canRestore").GetBoolean().Should().BeFalse();

        await harness.AwaitRestoreAsync(harness.Bed.RecoveryId);

        var (statusCode, status) = await harness.SendAsync(
            HttpMethod.Get, $"{BasePath}/{harness.Bed.RecoveryId}");
        statusCode.Should().Be(HttpStatusCode.OK);
        status.RootElement.GetProperty("status").GetString().Should().Be("Restored");
        status.RootElement.GetProperty("outcome").GetString().Should().Be("Completed");
        status.RootElement.GetProperty("accepted").GetBoolean().Should().BeFalse();
        status.RootElement.GetProperty("canRestore").GetBoolean().Should().BeFalse();
        status.RootElement.GetProperty("errorCode").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);

        await harness.Bed.AssertRestoredPortableGenerationAsync();

        // A repeated request replays the completed outcome; it never starts a
        // second run.
        var (againStatus, againBody) = await harness.PostJsonAsync(
            $"{BasePath}/{harness.Bed.RecoveryId}/restore",
            new { destinationRevision = token, confirmReplacement = true });
        againStatus.Should().Be(HttpStatusCode.Accepted);
        againBody.RootElement.GetProperty("outcome").GetString().Should().Be("Completed");
        harness.GetService<SelfHostedActivationDispatcher>().StartedRestoreRunCount
            .Should().Be(1, "a duplicate request never starts a second restore");
    }

    [Fact]
    public async Task StatusRoute_AnswersDuringTheExclusiveWindow_WhileLibraryRoutesAreBusy()
    {
        await using var harness = await RecoveryHttpHarness.StartAsync();
        var maintenance = harness.GetService<LibraryMaintenanceCoordinator>();

        await using (await maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.RecoveryRestore))
        {
            var opened = new List<string>();
            SelfHostedSqliteFile.ConnectionOpeningForTesting = opened.Add;
            try
            {
                var (listCode, list) = await harness.SendAsync(HttpMethod.Get, BasePath);
                listCode.Should().Be(HttpStatusCode.OK,
                    "list reads only durable manifests and must answer during maintenance");
                list.RootElement.GetArrayLength().Should().Be(1);

                var (statusCode, status) = await harness.SendAsync(
                    HttpMethod.Get, $"{BasePath}/{harness.Bed.RecoveryId}");
                statusCode.Should().Be(HttpStatusCode.OK,
                    "status reads only durable manifests and must answer during maintenance");
                status.RootElement.GetProperty("status").GetString().Should().Be("Available");
                status.RootElement.GetProperty("outcome").GetString().Should().Be("Idle");
                status.RootElement.GetProperty("canRestore").GetBoolean().Should().BeTrue();
            }
            finally
            {
                SelfHostedSqliteFile.ConnectionOpeningForTesting = null;
            }

            opened.Should().BeEmpty(
                "recovery list/status must never open SQLite, especially not inside the exclusive window");

            var (busyStatus, busy) = await harness.SendAsync(HttpMethod.Get, "/api/books");
            busyStatus.Should().Be(HttpStatusCode.ServiceUnavailable);
            busy.RootElement.GetProperty("code").GetString()
                .Should().Be(MigrationActivationErrorCodes.Busy);
        }
    }

    [Fact]
    public async Task MalformedIdentifiers_AnswerTheMigrationInvalidRequestBody()
    {
        await using var harness = await RecoveryHttpHarness.StartAsync();

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, $"{BasePath}/not-a-guid"),
                     (HttpMethod.Post, $"{BasePath}/not-a-guid/restore"),
                 })
        {
            var (status, body) = await harness.SendAsync(
                method, path, method == HttpMethod.Post ? new ByteArrayContent([]) : null);
            status.Should().Be(HttpStatusCode.BadRequest);
            body.RootElement.GetProperty("error").GetString().Should().Be(MigrationHttpErrors.InvalidRequest);
        }
    }
}
