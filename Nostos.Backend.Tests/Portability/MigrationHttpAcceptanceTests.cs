using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 11 end-to-end acceptance over the real Program host (Kestrel, real
/// SQLite, real transfer root, real worker) with the production phase handlers:
/// a browser upload becomes a durable prepared import that survives a host
/// restart, and an export job produces a sealed artifact that streams back with
/// HTTP ranges. Engine-level crash seams remain the exhaustive proof; these
/// tests close the transport-to-real-handler gap.
/// </summary>
public sealed class MigrationHttpAcceptanceTests
{
    [Fact]
    public async Task Upload_complete_restart_then_real_worker_reaches_ready_to_activate()
    {
        await using var h = new MigrationHttpHarness { UseRealPhaseHandlers = true }.Start();
        var archive = await MigrationArchiveJobTestSupport.ExportRepresentativeAsync();

        var jobId = await h.CreateImportJobAsync("acceptance-import");
        var (sessionStatus, _, _) = await h.CreateSessionAsync(jobId, archive);
        sessionStatus.Should().Be(HttpStatusCode.Created);

        for (var index = 0; index < MigrationHttpHarness.ChunkCount(archive.LongLength, 4 * 1024 * 1024); index++)
        {
            (await h.UploadChunkAsync(jobId, archive, index)).Status.Should().Be(HttpStatusCode.OK);
        }

        (await h.CompleteAsync(jobId)).Status.Should().Be(HttpStatusCode.OK);

        // A full host restart between a sealed upload and preparation: the
        // durable session and the real worker must finish the job afterwards.
        await h.RestartAsync();

        var ready = await h.WaitForJobStateAsync(
            jobId,
            "ReadyToActivate",
            timeout: TimeSpan.FromSeconds(60));
        ready.RootElement.GetProperty("job").GetProperty("state").GetString().Should().Be("ReadyToActivate");
        ready.RootElement.GetProperty("session").GetProperty("state").GetString().Should().Be("Complete");

        // The prepared descriptor is durable and the live library stayed empty.
        var status = await h.JobStatusAsync(jobId);
        status.RootElement.GetProperty("preparedImport").ValueKind.Should().NotBe(
            System.Text.Json.JsonValueKind.Null,
            "a ReadyToActivate import carries its committed prepared descriptor");
        (await h.WithDb(db => db.Works.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Real_export_job_completes_and_streams_range_downloads()
    {
        await using var h = new MigrationHttpHarness { UseRealPhaseHandlers = true }.Start();

        var (createStatus, body) = await h.CreateJobAsync("Export", "acceptance-export", null);
        createStatus.Should().Be(HttpStatusCode.Created);
        var jobId = body.RootElement.GetProperty("job").GetProperty("id").GetGuid();

        await h.WaitForJobStateAsync(jobId, "Completed", timeout: TimeSpan.FromSeconds(60));

        var full = await h.Client.GetAsync(
            $"/api/portability/migration/jobs/{jobId}/export-download",
            HttpCompletionOption.ResponseHeadersRead);
        full.StatusCode.Should().Be(HttpStatusCode.OK);
        full.Headers.AcceptRanges.Should().ContainSingle("bytes");
        var bytes = await full.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);

        using var rangedRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/portability/migration/jobs/{jobId}/export-download");
        rangedRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 31);
        using var ranged = await h.Client.SendAsync(rangedRequest, HttpCompletionOption.ResponseHeadersRead);
        ranged.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await ranged.Content.ReadAsByteArrayAsync()).Should().Equal(bytes[..32]);
    }
}
