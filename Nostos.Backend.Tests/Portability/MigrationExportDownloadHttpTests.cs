using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 10 download contract over a real Kestrel socket: the sealed artifact
/// is served by the framework's range-enabled physical-file result, so full,
/// ranged, suffix, open-ended, If-Range, and 416 responses are exercised
/// through real HTTP. Unknown/foreign identifiers and unavailable jobs return
/// 404; expired artifacts return 410 with a typed code.
/// </summary>
public sealed class MigrationExportDownloadHttpTests
{
    [Fact]
    public async Task Full_download_streams_the_artifact_as_an_attachment()
    {
        using var factory = new LibraryEndpointFactory();
        factory.UseKestrel(0);
        using var host = factory;
        var content = DeterministicBytes(2 * 1024 * 1024 + 123);
        var jobId = await SeedArtifactAsync(host, content);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(
            $"/api/portability/migration/jobs/{jobId}/export-download",
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be(PortabilityEndpoints.ArchiveContentType);
        response.Content.Headers.ContentDisposition?.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition?.FileName.Should().Be("library.nostos");
        response.Content.Headers.ContentLength.Should().Be(content.LongLength);
        response.Headers.AcceptRanges.Should().ContainSingle("bytes");
        response.Headers.ETag.Should().NotBeNull();
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(content);
    }

    [Theory]
    [InlineData("bytes=0-9")]
    [InlineData("bytes=100-199")]
    [InlineData("bytes=1048570-")]
    [InlineData("bytes=-32")]
    public async Task Valid_ranges_return_206_with_exact_slices(string range)
    {
        using var factory = new LibraryEndpointFactory();
        factory.UseKestrel(0);
        using var host = factory;
        var content = DeterministicBytes(2 * 1024 * 1024 + 123);
        var jobId = await SeedArtifactAsync(host, content);
        using var client = host.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/portability/migration/jobs/{jobId}/export-download");
        request.Headers.TryAddWithoutValidation("Range", range);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange.Should().NotBeNull();
        var contentRange = response.Content.Headers.ContentRange!;
        contentRange.Length.Should().Be(content.LongLength);
        var start = contentRange.From!.Value;
        var end = contentRange.To!.Value;
        var body = await response.Content.ReadAsByteArrayAsync();
        body.Should().Equal(content[(int)start..(int)(end + 1)]);
    }

    [Fact]
    public async Task If_range_with_matching_etag_returns_206_and_a_stale_etag_returns_the_full_file()
    {
        using var factory = new LibraryEndpointFactory();
        factory.UseKestrel(0);
        using var host = factory;
        var content = DeterministicBytes(512 * 1024);
        var jobId = await SeedArtifactAsync(host, content);
        using var client = host.CreateClient();

        using var full = await client.GetAsync(
            $"/api/portability/migration/jobs/{jobId}/export-download");
        var entityTag = full.Headers.ETag!.ToString();

        async Task<HttpResponseMessage> RangeWithIfRangeAsync(string ifRange)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/portability/migration/jobs/{jobId}/export-download");
            request.Headers.Range = new RangeHeaderValue(0, 15);
            request.Headers.TryAddWithoutValidation("If-Range", ifRange);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }

        using var matching = await RangeWithIfRangeAsync(entityTag);
        matching.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await matching.Content.ReadAsByteArrayAsync()).Should().Equal(content[..16]);

        using var stale = await RangeWithIfRangeAsync("\"0000000000000000000000000000000000000000000000000000000000000000\"");
        stale.StatusCode.Should().Be(HttpStatusCode.OK);
        (await stale.Content.ReadAsByteArrayAsync()).Should().Equal(content);
    }

    [Fact]
    public async Task Invalid_range_returns_416()
    {
        using var factory = new LibraryEndpointFactory();
        factory.UseKestrel(0);
        using var host = factory;
        var content = DeterministicBytes(64 * 1024);
        var jobId = await SeedArtifactAsync(host, content);
        using var client = host.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/portability/migration/jobs/{jobId}/export-download");
        request.Headers.Range = new RangeHeaderValue(content.LongLength + 1, null);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    [Fact]
    public async Task Unknown_and_import_identifiers_return_404()
    {
        using var factory = new LibraryEndpointFactory();
        factory.UseKestrel(0);
        using var host = factory;
        using var client = host.CreateClient();

        using var unknown = await client.GetAsync(
            $"/api/portability/migration/jobs/{Guid.NewGuid()}/export-download");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(unknown)).Should().Be("migration_not_found");

        Guid importJobId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            importJobId = Guid.NewGuid();
            db.MigrationJobRecords.Add(NewJob(importJobId, MigrationJobState.Pending, MigrationDirection.Import));
            await db.SaveChangesAsync();
        }

        using var import = await client.GetAsync(
            $"/api/portability/migration/jobs/{importJobId}/export-download");
        import.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(import)).Should().Be("migration_not_found");
    }

    [Fact]
    public async Task Not_completed_job_returns_404_and_expired_artifact_returns_410()
    {
        using var factory = new LibraryEndpointFactory();
        factory.UseKestrel(0);
        using var host = factory;
        using var client = host.CreateClient();

        // An artifact that exists for a job that has not reached Completed.
        var active = await SeedArtifactAsync(host, DeterministicBytes(128), state: MigrationJobState.Validating);
        using var notCompleted = await client.GetAsync(
            $"/api/portability/migration/jobs/{active}/export-download");
        notCompleted.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(notCompleted)).Should().Be("migration_export_not_available");

        var expired = await SeedArtifactAsync(
            host,
            DeterministicBytes(128),
            artifactExpired: true);
        using var expiredResponse = await client.GetAsync(
            $"/api/portability/migration/jobs/{expired}/export-download");
        expiredResponse.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ErrorCodeAsync(expiredResponse)).Should().Be("migration_export_expired");
    }

    [Fact]
    public async Task Download_headers_arrive_before_the_body_is_read_and_partial_reads_stream()
    {
        using var factory = new LibraryEndpointFactory();
        factory.UseKestrel(0);
        using var host = factory;
        var content = DeterministicBytes(12 * 1024 * 1024);
        var jobId = await SeedArtifactAsync(host, content);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(
            $"/api/portability/migration/jobs/{jobId}/export-download",
            HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.Should().Be(content.LongLength);

        // Read only a prefix of the declared body; the response must stream
        // (SendFileAsync) rather than require the whole artifact in memory.
        await using var stream = await response.Content.ReadAsStreamAsync();
        var prefix = new byte[4096];
        var read = await stream.ReadAtLeastAsync(prefix, prefix.Length);
        read.Should().Be(prefix.Length);
        prefix.Should().Equal(content[..prefix.Length]);
    }

    [Fact]
    public async Task Head_returns_the_file_headers_without_a_body()
    {
        using var factory = new LibraryEndpointFactory();
        factory.UseKestrel(0);
        using var host = factory;
        var content = DeterministicBytes(256 * 1024);
        var jobId = await SeedArtifactAsync(host, content);
        using var client = host.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Head,
            $"/api/portability/migration/jobs/{jobId}/export-download");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.Should().Be(content.LongLength);
        response.Headers.AcceptRanges.Should().ContainSingle("bytes");
        response.Content.Headers.ContentDisposition?.DispositionType.Should().Be("attachment");
        (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_in_flight_download_survives_the_sweep_deleting_the_artifact()
    {
        using var factory = new LibraryEndpointFactory();
        factory.UseKestrel(0);
        using var host = factory;
        var content = DeterministicBytes(2 * 1024 * 1024 + 7);
        var jobId = await SeedArtifactAsync(host, content);
        using var client = host.CreateClient();

        using var response = await client.GetAsync(
            $"/api/portability/migration/jobs/{jobId}/export-download",
            HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var stream = await response.Content.ReadAsStreamAsync();
        var prefix = new byte[4096];
        (await stream.ReadAtLeastAsync(prefix, prefix.Length)).Should().Be(prefix.Length);

        // Expire the artifact and run the sweep while the response is open. The
        // endpoint serves from the opened handle, so the rest of the body still
        // arrives even though the row is now expired/deleted.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            await db.MigrationExportArtifactRecords
                .Where(a => a.JobId == jobId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    a => a.ExpiresAtUtc,
                    clock.GetUtcNow().AddHours(-1).UtcDateTime));
            var cleanup = scope.ServiceProvider
                .GetRequiredService<Nostos.Backend.Services.Portability.Migration.MigrationTransferCleanup>();
            await cleanup.SweepAsync(default);
        }

        using var rest = new MemoryStream();
        await stream.CopyToAsync(rest);
        rest.ToArray().Should().Equal(content[prefix.Length..]);
    }

    private static byte[] DeterministicBytes(int count)
    {
        var bytes = new byte[count];
        new Random(20261004).NextBytes(bytes);
        return bytes;
    }

    private static async Task<Guid> SeedArtifactAsync(
        LibraryEndpointFactory host,
        byte[] content,
        MigrationJobState state = MigrationJobState.Completed,
        bool artifactExpired = false)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var paths = scope.ServiceProvider.GetRequiredService<TransferPathResolver>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow().UtcDateTime;
        var jobId = Guid.NewGuid();

        db.MigrationJobRecords.Add(NewJob(jobId, state, MigrationDirection.Export, now));
        paths.EnsureDirectoryExists(paths.GetExportDirectory(jobId));
        var storageKey = paths.GetExportAttemptArtifactStorageKey(jobId, 1, new string('d', 64));
        var artifactPath = paths.ResolveStorageKey(storageKey);
        await File.WriteAllBytesAsync(artifactPath, content);
        db.MigrationExportArtifactRecords.Add(new MigrationExportArtifactRecord
        {
            JobId = jobId,
            State = (int)MigrationExportArtifactState.Available,
            StorageKey = storageKey,
            FileName = "library.nostos",
            ContentType = PortabilityEndpoints.ArchiveContentType,
            SizeBytes = content.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            CreatedAtUtc = now,
            AvailableAtUtc = now,
            ExpiresAtUtc = artifactExpired ? now.AddHours(-1) : now.AddHours(24),
        });
        await db.SaveChangesAsync();
        return jobId;
    }

    private static MigrationJobRecord NewJob(
        Guid id,
        MigrationJobState state,
        MigrationDirection direction,
        DateTime? now = null) =>
        new()
        {
            Id = id,
            Direction = (int)direction,
            State = (int)state,
            RecoveryStatus = (int)MigrationRecoveryStatus.NotRequired,
            ProgressPhase = (int)MigrationProgressPhase.Completed,
            CreatedAtUtc = now ?? DateTime.UtcNow,
            UpdatedAtUtc = now ?? DateTime.UtcNow,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreationPayloadHash = new string('a', 64),
            ExpiresAtUtc = (now ?? DateTime.UtcNow).AddHours(24),
            AttemptNumber = 1,
        };

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("error", out var error)
            ? error.GetString()
            : null;
    }
}
