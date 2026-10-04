using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Transport parity with the browser mirror in
/// <c>Nostos.Frontend/src/app/library-transfer/models/migration-http.dtos.ts</c>:
/// exact property names, order and enum representation (strings, as the
/// frontend unions declare them).
/// </summary>
public sealed class MigrationHttpDtoParityTests
{
    private static readonly string[] PreflightResponse =
        ["evaluation", "reservationId", "reservationExpiresAtUtc", "chunkSizeBytes"];

    private static readonly string[] PreflightEvaluation =
    [
        "decision", "isCompatible", "incomingCounts", "existingCounts",
        "declaredArchiveBytes", "declaredMediaBytes", "estimatedRecoveryBytes",
        "requiredStorageBytes", "availableStorageBytes", "errors", "warnings",
        "destinationRevision", "isAllowed",
    ];

    private static readonly string[] ArchiveCounts =
    [
        "works", "books", "notes", "topics", "noteTopics", "writings", "writingNotes",
        "collections", "collectionMemberships", "acquisitions", "assistantSettings",
        "noteImportBookLinks", "mediaEntries", "totalRows",
    ];

    private static readonly string[] ExistingCounts =
    [
        "works", "books", "notes", "topics", "noteTopics", "writings", "writingNotes",
        "collections", "bookCollections", "acquisitions", "noteImportBookLinks",
        "assistantSettings", "totalRows",
    ];

    private static readonly string[] JobStatus =
        ["job", "progress", "session", "downloadAvailable", "artifactExpiresAtUtc", "preparedImport"];

    private static readonly string[] Job =
    [
        "id", "direction", "state", "recoveryStatus", "createdAtUtc", "updatedAtUtc",
        "leaseToken", "leaseExpiresAtUtc", "failureCode", "failureMessage",
    ];

    private static readonly string[] Progress =
        ["phase", "bytesProcessed", "totalBytes", "completedChunks", "totalChunks", "message"];

    private static readonly string[] SessionStatus =
    [
        "sessionId", "purpose", "state", "totalBytes", "chunkSize", "totalChunks",
        "fileIdentity", "receivedChunks", "receivedChunkCount", "createdAtUtc",
        "expiresAtUtc", "idempotencyKey",
    ];

    private static readonly string[] FileIdentity =
        ["totalSizeBytes", "sha256Checksum", "clientFingerprint"];

    private static readonly string[] UploadSessionResponse = ["session", "receivedRanges"];

    private static readonly string[] ChunkRange = ["startIndex", "endIndex"];

    private static readonly string[] ChunkUploadResult = ["sessionId", "chunkIndex", "alreadyPresent"];

    [Fact]
    public async Task Preflight_response_matches_the_frontend_dto_shape()
    {
        await using var h = new MigrationHttpHarness().Start();
        var (status, body) = await h.PreflightAsync(archiveBytes: 8L * 1024 * 1024);
        status.Should().Be(HttpStatusCode.OK);

        AssertProperties(body.RootElement, PreflightResponse);
        var evaluation = body.RootElement.GetProperty("evaluation");
        AssertProperties(evaluation, PreflightEvaluation);
        AssertProperties(evaluation.GetProperty("incomingCounts"), ArchiveCounts);
        AssertProperties(evaluation.GetProperty("existingCounts"), ExistingCounts);

        evaluation.GetProperty("decision").ValueKind.Should().Be(JsonValueKind.String);
        evaluation.GetProperty("decision").GetString().Should().Be("AllowedEmpty");
        evaluation.GetProperty("isAllowed").ValueKind.Should().Be(JsonValueKind.True);
        body.RootElement.GetProperty("reservationId").ValueKind.Should().Be(JsonValueKind.String);
        body.RootElement.GetProperty("chunkSizeBytes").GetInt32().Should().Be(4 * 1024 * 1024);
    }

    [Fact]
    public async Task Job_session_and_chunk_responses_match_the_frontend_dto_shapes()
    {
        await using var h = new MigrationHttpHarness().Start();
        var jobId = await h.CreateImportJobAsync("dto-shape-key");

        var status = await h.JobStatusAsync(jobId);
        AssertProperties(status.RootElement, JobStatus);
        var job = status.RootElement.GetProperty("job");
        AssertProperties(job, Job);
        AssertProperties(status.RootElement.GetProperty("progress"), Progress);
        job.GetProperty("direction").ValueKind.Should().Be(JsonValueKind.String);
        job.GetProperty("direction").GetString().Should().Be("Import");
        job.GetProperty("state").GetString().Should().Be("Pending");
        job.GetProperty("recoveryStatus").GetString().Should().Be("NotRequired");
        job.GetProperty("id").GetGuid().Should().Be(jobId);
        status.RootElement.GetProperty("session").ValueKind.Should().Be(JsonValueKind.Null);
        status.RootElement.GetProperty("downloadAvailable").GetBoolean().Should().BeFalse();
        status.RootElement.GetProperty("preparedImport").ValueKind.Should().Be(JsonValueKind.Null);

        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 9);
        var (_, created, _) = await h.CreateSessionAsync(jobId, file, key: "dto-session");
        AssertProperties(created.RootElement, UploadSessionResponse);
        var session = created.RootElement.GetProperty("session");
        AssertProperties(session, SessionStatus);
        session.GetProperty("purpose").GetString().Should().Be("Import");
        session.GetProperty("state").GetString().Should().Be("Created");
        AssertProperties(session.GetProperty("fileIdentity"), FileIdentity);
        session.GetProperty("receivedChunks").ValueKind.Should().Be(JsonValueKind.Array);

        // A fresh upload returns no ranges; a second accepted chunk compresses
        // into a range so the range record shape is exercised.
        var chunkResult = await h.UploadChunkAsync(jobId, file, 0);
        AssertProperties(chunkResult.Body.RootElement, ChunkUploadResult);
        chunkResult.Body.RootElement.GetProperty("alreadyPresent").GetBoolean().Should().BeFalse();

        var (_, sessionGet) =
            await h.SendAsync(HttpMethod.Get, $"/api/portability/migration/jobs/{jobId}/upload-session");
        AssertProperties(sessionGet.RootElement, UploadSessionResponse);
        sessionGet.RootElement.GetProperty("receivedRanges").EnumerateArray()
            .Should().OnlyContain(range => range.EnumerateObject().Select(p => p.Name).SequenceEqual(ChunkRange));

        var statusWithSession = await h.JobStatusAsync(jobId);
        AssertProperties(statusWithSession.RootElement.GetProperty("session"), SessionStatus);
    }

    [Fact]
    public async Task Error_response_shape_is_error_plus_message_only()
    {
        await using var h = new MigrationHttpHarness().Start();
        var (status, body) = await h.SendAsync(
            HttpMethod.Get,
            $"/api/portability/migration/jobs/{Guid.NewGuid()}");
        status.Should().Be(HttpStatusCode.NotFound);
        AssertProperties(body.RootElement, "error", "message");
        body.RootElement.GetProperty("error").GetString().Should().Be("migration_not_found");
    }

    private static void AssertProperties(JsonElement element, params string[] expected)
    {
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        actual.Should().Equal(expected);
    }
}
