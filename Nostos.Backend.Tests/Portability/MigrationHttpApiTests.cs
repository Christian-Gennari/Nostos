using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Slice 8 HTTP integration: real Program host + Kestrel + SQLite file + temp
/// transfer root, driven through <see cref="MigrationHttpHarness"/>.
/// </summary>
public sealed class MigrationHttpApiTests
{
    [Fact]
    public async Task Happy_path_preflight_job_session_out_of_order_chunks_complete_advances_to_ready()
    {
        await using var h = new MigrationHttpHarness().Start();

        var (preflightStatus, preflight) = await h.PreflightAsync(archiveBytes: 8L * 1024 * 1024);
        preflightStatus.Should().Be(HttpStatusCode.OK);
        preflight.RootElement.GetProperty("evaluation").GetProperty("decision").GetString()
            .Should().Be("AllowedEmpty");
        preflight.RootElement.GetProperty("evaluation").GetProperty("isAllowed").GetBoolean().Should().BeTrue();
        var reservationId = preflight.RootElement.GetProperty("reservationId").GetGuid();
        preflight.RootElement.GetProperty("chunkSizeBytes").GetInt32().Should().Be(4 * 1024 * 1024);

        var (createStatus, location, createBody) =
            await h.CreateJobWithLocationAsync("Import", "happy-key", reservationId);
        createStatus.Should().Be(HttpStatusCode.Created);
        location.Should().NotBeNull();
        location!.OriginalString.Should().Be($"/api/portability/migration/jobs/{JobIdOf(createBody)}");
        var jobId = JobIdOf(createBody);
        StateOf(createBody).Should().Be("Pending");

        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 12345);
        var (sessionStatus, sessionBody, _) = await h.CreateSessionAsync(jobId, file);
        sessionStatus.Should().Be(HttpStatusCode.Created);
        var session = sessionBody.RootElement.GetProperty("session");
        session.GetProperty("state").GetString().Should().Be("Created");
        session.GetProperty("totalChunks").GetInt32().Should().Be(2);
        sessionBody.RootElement.GetProperty("receivedRanges").GetArrayLength().Should().Be(0);

        // Out of order, final short chunk first.
        var chunk1 = await h.UploadChunkAsync(jobId, file, 1);
        chunk1.Status.Should().Be(HttpStatusCode.OK);
        chunk1.Body.RootElement.GetProperty("alreadyPresent").GetBoolean().Should().BeFalse();
        chunk1.Body.RootElement.GetProperty("chunkIndex").GetInt32().Should().Be(1);
        var chunk0 = await h.UploadChunkAsync(jobId, file, 0);
        chunk0.Status.Should().Be(HttpStatusCode.OK);

        var (sessionGetStatus, sessionGet) =
            await h.SendAsync(HttpMethod.Get, $"/api/portability/migration/jobs/{jobId}/upload-session");
        sessionGetStatus.Should().Be(HttpStatusCode.OK);
        var ranges = sessionGet.RootElement.GetProperty("receivedRanges");
        ranges.GetArrayLength().Should().Be(1);
        ranges[0].GetProperty("startIndex").GetInt32().Should().Be(0);
        ranges[0].GetProperty("endIndex").GetInt32().Should().Be(1);
        sessionGet.RootElement.GetProperty("session").GetProperty("receivedChunks")
            .EnumerateArray().Select(v => v.GetInt32()).Should().Equal(0, 1);

        var complete = await h.CompleteAsync(jobId);
        complete.Status.Should().Be(HttpStatusCode.OK);
        complete.Body.RootElement.GetProperty("state").GetString().Should().Be("Complete");

        var ready = await h.WaitForJobStateAsync(jobId, "ReadyToActivate");
        ready.RootElement.GetProperty("job").GetProperty("state").GetString().Should().Be("ReadyToActivate");
        ready.RootElement.GetProperty("session").GetProperty("state").GetString().Should().Be("Complete");
        ready.RootElement.GetProperty("progress").GetProperty("phase").GetString().Should().Be("Validating");
        ready.RootElement.GetProperty("progress").GetProperty("bytesProcessed").GetInt64()
            .Should().Be(file.LongLength);
    }

    [Fact]
    public async Task Resume_after_restart_reports_receipts_and_accepts_only_missing_chunks()
    {
        await using var h = new MigrationHttpHarness().Start();
        var jobId = await h.CreateImportJobAsync("restart-key");
        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 4096);
        var (_, sessionBody, _) = await h.CreateSessionAsync(jobId, file);

        (await h.UploadChunkAsync(jobId, file, 0)).Status.Should().Be(HttpStatusCode.OK);

        await h.RestartAsync();

        var (status, sessionGet) =
            await h.SendAsync(HttpMethod.Get, $"/api/portability/migration/jobs/{jobId}/upload-session");
        status.Should().Be(HttpStatusCode.OK);
        var ranges = sessionGet.RootElement.GetProperty("receivedRanges");
        ranges.GetArrayLength().Should().Be(1);
        ranges[0].GetProperty("startIndex").GetInt32().Should().Be(0);
        ranges[0].GetProperty("endIndex").GetInt32().Should().Be(0);
        sessionGet.RootElement.GetProperty("session").GetProperty("sessionId").GetGuid()
            .Should().Be(sessionBody.RootElement.GetProperty("session").GetProperty("sessionId").GetGuid());

        (await h.UploadChunkAsync(jobId, file, 1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.CompleteAsync(jobId)).Status.Should().Be(HttpStatusCode.OK);
        (await h.WaitForJobStateAsync(jobId, "ReadyToActivate"))
            .RootElement.GetProperty("job").GetProperty("state").GetString().Should().Be("ReadyToActivate");
    }

    [Fact]
    public async Task Job_creation_replays_same_payload_and_conflicts_on_different_payload()
    {
        await using var h = new MigrationHttpHarness().Start();
        var reservationA = await h.ReserveAsync("job-replay-a");

        var first = await h.CreateJobAsync("Import", "job-replay", reservationA);
        first.Status.Should().Be(HttpStatusCode.Created);
        var jobId = JobIdOf(first.Body);

        var replay = await h.CreateJobAsync("Import", "job-replay", reservationA);
        replay.Status.Should().Be(HttpStatusCode.OK);
        JobIdOf(replay.Body).Should().Be(jobId);

        // A later preflight supersedes only unclaimed holds; the claimed one
        // stays bound to its job.
        var reservationB = await h.ReserveAsync("job-replay-b");
        var differentReservation = await h.CreateJobAsync("Import", "job-replay", reservationB);
        differentReservation.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(differentReservation.Body).Should().Be("migration_idempotency_conflict");

        var differentDirection = await h.CreateJobAsync("Export", "job-replay", null);
        differentDirection.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(differentDirection.Body).Should().Be("migration_idempotency_conflict");

        var second = await h.CreateJobAsync("Import", "job-replay-2", reservationB);
        second.Status.Should().Be(HttpStatusCode.Created);
        (await h.WithDb(db => db.MigrationJobRecords.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task Session_creation_replays_same_payload_and_conflicts_on_different_payload_or_identity()
    {
        await using var h = new MigrationHttpHarness().Start();
        var jobId = await h.CreateImportJobAsync("session-replay");
        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 100);

        var first = await h.CreateSessionAsync(jobId, file, key: "session-replay");
        first.Status.Should().Be(HttpStatusCode.Created);
        var sessionId = first.Body.RootElement.GetProperty("session").GetProperty("sessionId").GetGuid();

        var replay = await h.CreateSessionAsync(jobId, file, key: "session-replay");
        replay.Status.Should().Be(HttpStatusCode.OK);
        replay.Body.RootElement.GetProperty("session").GetProperty("sessionId").GetGuid()
            .Should().Be(sessionId);

        var otherFile = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 200);
        var changedPayload = await h.CreateSessionAsync(jobId, otherFile, key: "session-replay");
        changedPayload.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(changedPayload.Body).Should().Be("migration_idempotency_conflict");

        var otherIdentity = await h.CreateSessionAsync(jobId, otherFile, key: "session-other");
        otherIdentity.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(otherIdentity.Body).Should().Be("migration_file_identity_mismatch");
    }

    [Fact]
    public async Task Duplicate_chunk_same_bytes_is_idempotent_and_different_bytes_conflict()
    {
        await using var h = new MigrationHttpHarness().Start();
        var jobId = await h.CreateImportJobAsync("dup-key");
        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 777);
        await h.CreateSessionAsync(jobId, file);

        var first = await h.UploadChunkAsync(jobId, file, 0);
        first.Status.Should().Be(HttpStatusCode.OK);
        first.Body.RootElement.GetProperty("alreadyPresent").GetBoolean().Should().BeFalse();

        var duplicate = await h.UploadChunkAsync(jobId, file, 0);
        duplicate.Status.Should().Be(HttpStatusCode.OK);
        duplicate.Body.RootElement.GetProperty("alreadyPresent").GetBoolean().Should().BeTrue();

        var conflictingBytes = file.ToArray();
        conflictingBytes[0] ^= 0xFF;
        var conflict = await h.UploadChunkAsync(
            jobId,
            file,
            0,
            mutateBody: _ => conflictingBytes.AsSpan(0, 4 * 1024 * 1024).ToArray());
        conflict.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(conflict.Body).Should().Be("migration_chunk_conflict");

        var (_, sessionGet) =
            await h.SendAsync(HttpMethod.Get, $"/api/portability/migration/jobs/{jobId}/upload-session");
        sessionGet.RootElement.GetProperty("session").GetProperty("receivedChunkCount").GetInt32().Should().Be(1);
        sessionGet.RootElement.GetProperty("session").GetProperty("receivedChunks")[0].GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Wrong_range_wrong_hash_short_body_out_of_bounds_index_and_malformed_headers_are_typed()
    {
        await using var h = new MigrationHttpHarness().Start();
        var jobId = await h.CreateImportJobAsync("bad-chunk-key");
        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 100);
        await h.CreateSessionAsync(jobId, file);

        var wrongStart = await h.UploadChunkAsync(
            jobId, file, 0, contentRange: $"bytes 1-{4 * 1024 * 1024}/{file.LongLength}");
        wrongStart.Status.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        CodeOf(wrongStart.Body).Should().Be("migration_chunk_range_invalid");

        var wrongHash = await h.UploadChunkAsync(jobId, file, 0, declaredHash: new string('a', 64));
        wrongHash.Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        CodeOf(wrongHash.Body).Should().Be("migration_chunk_hash_mismatch");

        var shortBody = await h.UploadChunkAsync(
            jobId,
            file,
            0,
            mutateBody: bytes => bytes.AsSpan(0, bytes.Length / 2).ToArray());
        shortBody.Status.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        CodeOf(shortBody.Body).Should().Be("migration_chunk_range_invalid");

        var outOfBounds = await h.SendChunkRawAsync(
            jobId,
            index: 2,
            body: [],
            contentRange: $"bytes 0-0/{file.LongLength}",
            hash: new string('a', 64));
        outOfBounds.Status.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        CodeOf(outOfBounds.Body).Should().Be("migration_chunk_range_invalid");

        var malformedRange = await h.UploadChunkAsync(jobId, file, 0, contentRange: "bytes 0-10");
        malformedRange.Status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(malformedRange.Body).Should().Be("migration_invalid_request");

        using var missingHash = new ByteArrayContent([]);
        missingHash.Headers.TryAddWithoutValidation("Content-Range", $"bytes 0-0/{file.LongLength}");
        var noHash = await h.SendAsync(
            HttpMethod.Put,
            $"/api/portability/migration/jobs/{jobId}/upload-session/chunks/0",
            missingHash);
        noHash.Status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(noHash.Body).Should().Be("migration_invalid_request");

        using var invalidHash = new ByteArrayContent([]);
        invalidHash.Headers.TryAddWithoutValidation("Content-Range", $"bytes 0-0/{file.LongLength}");
        invalidHash.Headers.TryAddWithoutValidation("X-Nostos-Chunk-SHA256", "not-a-sha256");
        var badHashHeader = await h.SendAsync(
            HttpMethod.Put,
            $"/api/portability/migration/jobs/{jobId}/upload-session/chunks/0",
            invalidHash);
        badHashHeader.Status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(badHashHeader.Body).Should().Be("migration_invalid_request");

        var jobStatus = await h.JobStatusAsync(jobId);
        jobStatus.RootElement.GetProperty("session").GetProperty("receivedChunkCount").GetInt32().Should().Be(0);
        jobStatus.RootElement.GetProperty("progress").GetProperty("bytesProcessed").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task Chunk_larger_than_the_endpoint_limit_is_413_with_nothing_written()
    {
        await using var h = new MigrationHttpHarness().Start();
        var jobId = await h.CreateImportJobAsync("too-large-key");
        var file = MigrationHttpHarness.DeterministicBytes(8 * 1024 * 1024 + 100);
        var created = await h.CreateSessionAsync(jobId, file);
        created.Status.Should().Be(
            HttpStatusCode.Created,
            $"session body was {created.Body.RootElement.GetRawText()}");

        // Declared oversize body: rejected before a single byte is read.
        var oversize = new byte[4 * 1024 * 1024 + 1];
        using (var declared = new ByteArrayContent(oversize))
        {
            declared.Headers.TryAddWithoutValidation("Content-Range", $"bytes 0-{4 * 1024 * 1024 - 1}/{file.LongLength}");
            declared.Headers.TryAddWithoutValidation("X-Nostos-Chunk-SHA256", MigrationHttpHarness.Sha256(oversize));
            var declaredResponse = await h.SendAsync(
                HttpMethod.Put,
                $"/api/portability/migration/jobs/{jobId}/upload-session/chunks/0",
                declared);
            declaredResponse.Status.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            CodeOf(declaredResponse.Body).Should().Be("migration_invalid_request");
        }

        // Undeclared (chunked) oversize body: Kestrel enforces the per-endpoint
        // cap while the engine streams the request.
        using (var chunked = new IncrementalContent(oversize))
        {
            chunked.Headers.TryAddWithoutValidation("Content-Range", $"bytes 0-{4 * 1024 * 1024 - 1}/{file.LongLength}");
            chunked.Headers.TryAddWithoutValidation("X-Nostos-Chunk-SHA256", MigrationHttpHarness.Sha256(oversize));
            var chunkedResponse = await h.SendAsync(
                HttpMethod.Put,
                $"/api/portability/migration/jobs/{jobId}/upload-session/chunks/0",
                chunked);
            chunkedResponse.Status.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            CodeOf(chunkedResponse.Body).Should().Be("migration_invalid_request");
        }

        var (_, sessionGet) =
            await h.SendAsync(HttpMethod.Get, $"/api/portability/migration/jobs/{jobId}/upload-session");
        sessionGet.RootElement.TryGetProperty("session", out var sessionElement)
            .Should().BeTrue($"upload-session body was {sessionGet.RootElement.GetRawText()}");
        sessionElement.GetProperty("receivedChunkCount").GetInt32().Should().Be(0);
        (await h.JobStatusAsync(jobId)).RootElement.GetProperty("progress")
            .GetProperty("bytesProcessed").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task Expired_session_is_410_and_retry_resolves_the_unswept_window()
    {
        await using var h = new MigrationHttpHarness().Start();
        var jobId = await h.CreateImportJobAsync("expiry-key");
        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 100);
        await h.CreateSessionAsync(jobId, file, key: "expiry-key");
        (await h.UploadChunkAsync(jobId, file, 0)).Status.Should().Be(HttpStatusCode.OK);

        // Expire only the session; the job (and its worker lease clock) stays
        // active, which is exactly the review-725 window before the TTL sweep.
        await h.WithDb(async db =>
        {
            var session = await db.MigrationSessionRecords.SingleAsync(s => s.JobId == jobId);
            session.ExpiresAtUtc = new DateTime(2026, 10, 4, 11, 59, 0, DateTimeKind.Utc);
            await db.SaveChangesAsync();
        });

        var upload = await h.UploadChunkAsync(jobId, file, 1);
        upload.Status.Should().Be(HttpStatusCode.Gone);
        CodeOf(upload.Body).Should().Be("migration_session_expired");

        var (sessionStatus, sessionBody) =
            await h.SendAsync(HttpMethod.Get, $"/api/portability/migration/jobs/{jobId}/upload-session");
        sessionStatus.Should().Be(HttpStatusCode.Gone);
        CodeOf(sessionBody).Should().Be("migration_session_expired");

        // Retry is the deterministic path out of the window: the expired session
        // expires its job synchronously before the store reactivates it.
        var retry = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{jobId}/retry",
            new { idempotencyKey = (string?)null });
        retry.Status.Should().Be(HttpStatusCode.OK);
        StateOf(retry.Body).Should().Be("Pending");

        var reactivated = await h.CreateSessionAsync(jobId, file, key: "expiry-key");
        reactivated.Status.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        reactivated.Body.RootElement.GetProperty("session").GetProperty("state").GetString()
            .Should().Be("Receiving");
        reactivated.Body.RootElement.GetProperty("session").GetProperty("receivedChunkCount").GetInt32()
            .Should().Be(0);
    }

    [Fact]
    public async Task Retry_is_rejected_while_the_job_is_active_and_after_failed_upload_it_reactivates()
    {
        await using var h = new MigrationHttpHarness().Start();
        var activeJob = await h.CreateImportJobAsync("retry-active");
        var retryActive = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{activeJob}/retry",
            new { idempotencyKey = (string?)null });
        retryActive.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(retryActive.Body).Should().Be("migration_not_retryable");

        var jobId = await h.CreateImportJobAsync("retry-failed");
        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 - 321);
        var wrongHash = MigrationHttpHarness.Sha256(
            MigrationHttpHarness.DeterministicBytes(64, seed: 99));
        (await h.CreateSessionAsync(jobId, file, key: "retry-failed", identityHash: wrongHash))
            .Status.Should().Be(HttpStatusCode.Created);
        (await h.UploadChunkAsync(jobId, file, 0)).Status.Should().Be(HttpStatusCode.OK);

        var complete = await h.CompleteAsync(jobId);
        complete.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(complete.Body).Should().Be("migration_file_identity_mismatch");

        var failed = await h.JobStatusAsync(jobId);
        failed.RootElement.GetProperty("job").GetProperty("state").GetString().Should().Be("Failed");
        failed.RootElement.GetProperty("job").GetProperty("failureCode").GetString()
            .Should().Be("migration_file_identity_mismatch");

        var retry = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{jobId}/retry",
            new { idempotencyKey = (string?)null });
        retry.Status.Should().Be(HttpStatusCode.OK);
        StateOf(retry.Body).Should().Be("Pending");
        retry.Body.RootElement.GetProperty("job").GetProperty("failureCode").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Cancel_works_at_every_pre_activation_stage_and_refuses_terminal_or_activating()
    {
        await using var h = new MigrationHttpHarness().Start();

        // Pending.
        var pending = await h.CreateImportJobAsync("cancel-pending");
        var cancelledPending = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{pending}/cancel",
            new { reason = "user" });
        cancelledPending.Status.Should().Be(HttpStatusCode.OK);
        StateOf(cancelledPending.Body).Should().Be("Cancelled");
        // Idempotent replay.
        var replay = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{pending}/cancel",
            new { reason = "user" });
        replay.Status.Should().Be(HttpStatusCode.OK);
        StateOf(replay.Body).Should().Be("Cancelled");

        // Transferring with partial chunks.
        var transferring = await h.CreateImportJobAsync("cancel-transferring");
        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 555);
        await h.CreateSessionAsync(transferring, file);
        (await h.UploadChunkAsync(transferring, file, 0)).Status.Should().Be(HttpStatusCode.OK);
        h.Advance(TimeSpan.FromSeconds(11));
        var inTransfer = await h.WaitForJobStateAsync(transferring, "Transferring");
        inTransfer.RootElement.GetProperty("progress").GetProperty("bytesProcessed").GetInt64()
            .Should().Be(4 * 1024 * 1024);
        var cancelledTransferring = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{transferring}/cancel",
            new { reason = "stop" });
        cancelledTransferring.Status.Should().Be(HttpStatusCode.OK);
        StateOf(cancelledTransferring.Body).Should().Be("Cancelled");
        cancelledTransferring.Body.RootElement.GetProperty("session").GetProperty("state").GetString()
            .Should().Be("Cancelled");
        // No further uploads are accepted after cancellation.
        var lateChunk = await h.UploadChunkAsync(transferring, file, 1);
        lateChunk.Status.Should().Be(HttpStatusCode.Conflict);

        // ReadyToActivate.
        var ready = await h.CreateImportJobAsync("cancel-ready");
        var readyFile = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 42);
        await h.CreateSessionAsync(ready, readyFile);
        (await h.UploadChunkAsync(ready, readyFile, 0)).Status.Should().Be(HttpStatusCode.OK);
        (await h.UploadChunkAsync(ready, readyFile, 1)).Status.Should().Be(HttpStatusCode.OK);
        (await h.CompleteAsync(ready)).Status.Should().Be(HttpStatusCode.OK);
        await h.WaitForJobStateAsync(ready, "ReadyToActivate");
        var cancelledReady = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{ready}/cancel",
            new { reason = "abandon" });
        cancelledReady.Status.Should().Be(HttpStatusCode.OK);
        StateOf(cancelledReady.Body).Should().Be("Cancelled");

        // Completed export (terminal history is never rewritten).
        var export = await h.CreateJobAsync("Export", "cancel-completed", null);
        export.Status.Should().Be(HttpStatusCode.Created);
        var exportId = JobIdOf(export.Body);
        h.Advance(TimeSpan.FromSeconds(11));
        await h.WaitForJobStateAsync(exportId, "Completed");
        var cancelledCompleted = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{exportId}/cancel",
            new { reason = "too late" });
        cancelledCompleted.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(cancelledCompleted.Body).Should().Be("migration_cannot_cancel");

        // Activating (point of no return).
        var activating = await h.CreateImportJobAsync("cancel-activating");
        await h.WithDb(async db =>
        {
            var record = await db.MigrationJobRecords.SingleAsync(j => j.Id == activating);
            record.State = (int)MigrationJobState.Activating;
            await db.SaveChangesAsync();
        });
        var cancelledActivating = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{activating}/cancel",
            new { reason = "nope" });
        cancelledActivating.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(cancelledActivating.Body).Should().Be("migration_cannot_cancel");
    }

    [Fact]
    public async Task Outstanding_job_ceiling_refuses_new_work_until_a_job_is_terminal()
    {
        await using var h = new MigrationHttpHarness
        {
            ConfigureServices = services => services.Configure<TransferStorageOptions>(
                options => options.MaxOutstandingJobs = 1),
        }.Start();

        var first = await h.CreateImportJobAsync("ceiling-first");
        var second = await h.CreateJobAsync("Import", "ceiling-second", await h.ReserveAsync("ceiling-second"));
        second.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(second.Body).Should().Be("migration_too_many_jobs");

        // A terminal job no longer counts against the ceiling.
        var cancel = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{first}/cancel",
            new { reason = "done" });
        cancel.Status.Should().Be(HttpStatusCode.OK);

        var third = await h.CreateJobAsync("Import", "ceiling-third", await h.ReserveAsync("ceiling-third"));
        third.Status.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Outstanding_job_ceiling_is_a_hard_bound_under_concurrent_creators()
    {
        await using var h = new MigrationHttpHarness
        {
            ConfigureServices = services => services.Configure<TransferStorageOptions>(
                options => options.MaxOutstandingJobs = 1),
        }.Start();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            h.CreateJobAsync("Export", $"hard-ceiling-{index}", null)));

        results.Count(result => result.Status == HttpStatusCode.Created).Should().Be(
            1,
            "the ceiling is enforced inside the creation transaction, not by a check-then-insert");
        results.Count(result => result.Status == HttpStatusCode.Conflict
            && CodeOf(result.Body) == "migration_too_many_jobs").Should().Be(5);

        (await h.WithDb(db => db.MigrationJobRecords.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Second_concurrent_job_is_accepted_and_queued()
    {
        await using var h = new MigrationHttpHarness().Start();
        var first = await h.CreateImportJobAsync("concurrent-first");
        var second = await h.CreateImportJobAsync("concurrent-second");

        first.Should().NotBe(second);
        (await h.JobStatusAsync(first)).RootElement.GetProperty("job").GetProperty("state").GetString()
            .Should().Be("Pending");
        (await h.JobStatusAsync(second)).RootElement.GetProperty("job").GetProperty("state").GetString()
            .Should().Be("Pending");

        var cancel = await h.PostJsonAsync(
            $"/api/portability/migration/jobs/{first}/cancel",
            new { reason = "done" });
        cancel.Status.Should().Be(HttpStatusCode.OK);
        (await h.JobStatusAsync(second)).RootElement.GetProperty("job").GetProperty("state").GetString()
            .Should().Be("Pending");
    }

    [Fact]
    public async Task Unknown_ids_and_missing_sessions_return_typed_not_found_or_invalid_state()
    {
        await using var h = new MigrationHttpHarness().Start();
        var unknown = Guid.NewGuid();

        var gets = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, $"/api/portability/migration/jobs/{unknown}"),
            (HttpMethod.Get, $"/api/portability/migration/jobs/{unknown}/upload-session"),
            (HttpMethod.Post, $"/api/portability/migration/jobs/{unknown}/cancel"),
            (HttpMethod.Post, $"/api/portability/migration/jobs/{unknown}/retry"),
            (HttpMethod.Post, $"/api/portability/migration/jobs/{unknown}/upload-session/complete"),
        };
        foreach (var (method, path) in gets)
        {
            var response = method == HttpMethod.Get
                ? await h.SendAsync(method, path)
                : await h.SendAsync(method, path, new ByteArrayContent([]));
            response.Status.Should().Be(HttpStatusCode.NotFound, $"{method} {path}");
            CodeOf(response.Body).Should().Be("migration_not_found", $"{method} {path}");
        }

        var unknownSession = await h.CreateSessionRawAsync(
            unknown,
            totalBytes: 16,
            chunkSize: 4 * 1024 * 1024,
            identityHash: new string('a', 64));
        unknownSession.Status.Should().Be(HttpStatusCode.NotFound);
        CodeOf(unknownSession.Body).Should().Be("migration_not_found");

        var chunk = await h.UploadChunkAsync(unknown, MigrationHttpHarness.DeterministicBytes(16), 0);
        chunk.Status.Should().Be(HttpStatusCode.NotFound);
        CodeOf(chunk.Body).Should().Be("migration_not_found");

        var job = await h.CreateImportJobAsync("no-session");
        var (sessionStatus, sessionBody) =
            await h.SendAsync(HttpMethod.Get, $"/api/portability/migration/jobs/{job}/upload-session");
        sessionStatus.Should().Be(HttpStatusCode.Conflict);
        CodeOf(sessionBody).Should().Be("migration_invalid_state");
    }

    [Fact]
    public async Task Preflight_capacity_rejection_returns_business_result_without_reservation()
    {
        await using var h = new MigrationHttpHarness().Start();

        h.Volume.AvailableFreeSpaceBytes = 1024 * 1024;
        var declared = await h.PreflightAsync(archiveBytes: 8L * 1024 * 1024);
        declared.Status.Should().Be(HttpStatusCode.OK);
        declared.Body.RootElement.GetProperty("evaluation").GetProperty("decision").GetString()
            .Should().Be("RejectedInsufficientStorage");
        declared.Body.RootElement.GetProperty("reservationId").ValueKind.Should().Be(JsonValueKind.Null);
        (await h.WithDb(db => db.MigrationStorageReservations.CountAsync())).Should().Be(0);

        // Contract bytes fit after the margin but host peak (contract + chunk +
        // per-job overhead) does not.
        h.Volume.AvailableFreeSpaceBytes = 9L * 1024 * 1024;
        var peakRejected = await h.PreflightAsync(archiveBytes: 8L * 1024 * 1024);
        peakRejected.Status.Should().Be(HttpStatusCode.OK);
        peakRejected.Body.RootElement.GetProperty("evaluation").GetProperty("decision").GetString()
            .Should().Be("RejectedInsufficientStorage");
        peakRejected.Body.RootElement.GetProperty("reservationId").ValueKind.Should().Be(JsonValueKind.Null);
        peakRejected.Body.RootElement.GetProperty("evaluation").GetProperty("errors")
            .EnumerateArray().Should().Contain(e => e.GetString()!.Contains("host peak"));

        h.Volume.AvailableFreeSpaceBytes = 256L * 1024 * 1024;
        var allowed = await h.PreflightAsync(archiveBytes: 8L * 1024 * 1024);
        allowed.Status.Should().Be(HttpStatusCode.OK);
        allowed.Body.RootElement.GetProperty("evaluation").GetProperty("decision").GetString()
            .Should().Be("AllowedEmpty");
        allowed.Body.RootElement.GetProperty("reservationId").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task Preflight_rejects_incompatible_and_stale_revisions_without_reserving()
    {
        await using var h = new MigrationHttpHarness().Start();

        var stale = await h.PreflightAsync(archiveBytes: 8L * 1024 * 1024, clientRevision: "not-the-revision");
        stale.Status.Should().Be(HttpStatusCode.OK);
        stale.Body.RootElement.GetProperty("evaluation").GetProperty("decision").GetString()
            .Should().Be("RejectedDestinationConflict");
        stale.Body.RootElement.GetProperty("reservationId").ValueKind.Should().Be(JsonValueKind.Null);

        var backup = await h.PreflightAsync(archiveBytes: 8L * 1024 * 1024, operationalBackup: true);
        backup.Status.Should().Be(HttpStatusCode.OK);
        backup.Body.RootElement.GetProperty("evaluation").GetProperty("decision").GetString()
            .Should().Be("RejectedOperationalBackupNotPortable");

        var format = await h.PostJsonAsync("/api/portability/migration/preflight", new
        {
            incomingCounts = new
            {
                works = 0,
                books = 0,
                notes = 0,
                topics = 0,
                noteTopics = 0,
                writings = 0,
                writingNotes = 0,
                collections = 0,
                collectionMemberships = 0,
                acquisitions = 0,
                assistantSettings = 0,
                noteImportBookLinks = 0,
                mediaEntries = 0,
            },
            declaredArchiveBytes = 1024,
            declaredMediaBytes = 0,
            maxSingleEntryBytes = 1024,
            declaredFormatVersion = 99,
            declaredDataVersion = 1,
            isOperationalBackup = false,
        });
        format.Status.Should().Be(HttpStatusCode.OK);
        format.Body.RootElement.GetProperty("evaluation").GetProperty("decision").GetString()
            .Should().Be("RejectedIncompatible");

        var allowed = await h.PreflightAsync(archiveBytes: 1024);
        var revision = allowed.Body.RootElement.GetProperty("evaluation")
            .GetProperty("destinationRevision").GetString();
        var fresh = await h.PreflightAsync(archiveBytes: 1024, clientRevision: revision);
        fresh.Body.RootElement.GetProperty("evaluation").GetProperty("decision").GetString()
            .Should().Be("AllowedEmpty");
        fresh.Body.RootElement.GetProperty("reservationId").ValueKind.Should().Be(JsonValueKind.String);

        // Superseding keeps exactly one live unclaimed hold; the earlier one is
        // released but retained as a row.
        var reservations = await h.WithDb(db => db.MigrationStorageReservations.AsNoTracking().ToListAsync());
        reservations.Count.Should().Be(2);
        reservations.Count(r => r.ReleasedAtUtc == null && r.ClaimedJobId == null).Should().Be(1);
    }

    [Fact]
    public async Task Requests_during_exclusive_maintenance_get_the_documented_503()
    {
        await using var h = new MigrationHttpHarness().Start();
        var job = await h.CreateImportJobAsync("maintenance-key");
        var coordinator = h.GetService<ILibraryMaintenanceCoordinator>();

        await using (await coordinator.EnterExclusiveAsync(LibraryMaintenanceReason.Activation))
        {
            foreach (var (method, path, content) in new (HttpMethod, string, HttpContent?)[]
            {
                (HttpMethod.Get, $"/api/portability/migration/jobs/{job}", null),
                (HttpMethod.Post, "/api/portability/migration/preflight", new ByteArrayContent([])),
                (HttpMethod.Post, $"/api/portability/migration/jobs/{job}/cancel", new ByteArrayContent([])),
            })
            {
                var response = await h.SendAsync(method, path, content);
                response.Status.Should().Be(HttpStatusCode.ServiceUnavailable, $"{method} {path}");
                response.Body.RootElement.EnumerateObject().Select(property => property.Name)
                    .Should().Equal(new[] { "error", "message" }, $"{method} {path}");
                response.Body.RootElement.GetProperty("error").GetString()
                    .Should().Be("migration_activation_busy");
                response.Body.RootElement.GetProperty("message").GetString()
                    .Should().Contain("maintenance");
            }

            using var statusRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/portability/migration/jobs/{job}");
            using var statusResponse = await h.Client.SendAsync(statusRequest);
            statusResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            statusResponse.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));

            // Non-migration routes keep their historical { code, error } body.
            using var legacyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/portability/export");
            using var legacyResponse = await h.Client.SendAsync(legacyRequest);
            legacyResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            using var legacyBody = JsonDocument.Parse(await legacyResponse.Content.ReadAsStringAsync());
            legacyBody.RootElement.EnumerateObject().Select(property => property.Name)
                .Should().Equal("code", "error");
            legacyBody.RootElement.GetProperty("code").GetString()
                .Should().Be("migration_activation_busy");
        }

        var after = await h.SendAsync(HttpMethod.Get, $"/api/portability/migration/jobs/{job}");
        after.Status.Should().Be(HttpStatusCode.OK);
        StateOf(after.Body).Should().Be("Pending");
    }

    [Fact]
    public async Task Chunk_body_is_streamed_to_the_engine_without_endpoint_buffering()
    {
        await using var h = new MigrationHttpHarness().Start();
        var jobId = await h.CreateImportJobAsync("streaming-key");
        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024);
        await h.CreateSessionAsync(jobId, file, chunkSize: 4 * 1024 * 1024);

        using var content = new IncrementalContent(file);
        content.Headers.TryAddWithoutValidation("Content-Range", $"bytes 0-{file.Length - 1}/{file.Length}");
        content.Headers.TryAddWithoutValidation("X-Nostos-Chunk-SHA256", MigrationHttpHarness.Sha256(file));
        var response = await h.SendAsync(
            HttpMethod.Put,
            $"/api/portability/migration/jobs/{jobId}/upload-session/chunks/0",
            content);

        response.Status.Should().Be(HttpStatusCode.OK);
        h.Probe.ChunkStreamCanSeek.Should().BeFalse(
            "a buffered request body would arrive as a seekable stream");
        h.Probe.ChunkStreamType.Should().NotBe(nameof(MemoryStream));
        h.Probe.SynchronousReadAttempted.Should().BeFalse();
        h.Probe.ChunkReadCount.Should().BeGreaterThan(8, "the engine consumes the body incrementally");
        h.Probe.ChunkMaxReadBytes.Should().BeLessThanOrEqualTo(128 * 1024);
    }

    [Fact]
    public async Task Chunk_upload_storage_failure_is_507_and_writes_no_receipt()
    {
        await using var h = new MigrationHttpHarness
        {
            ConfigureServices = services =>
            {
                services.RemoveAll<FileMigrationUploadStore>();
                services.AddScoped<FileMigrationUploadStore, FailingChunkUploadStore>();
            },
        }.Start();
        var jobId = await h.CreateImportJobAsync("enospc-key");
        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 1);
        await h.CreateSessionAsync(jobId, file);

        var upload = await h.UploadChunkAsync(jobId, file, 0);
        upload.Status.Should().Be(HttpStatusCode.InsufficientStorage);
        CodeOf(upload.Body).Should().Be("migration_storage_exhausted");

        var status = await h.JobStatusAsync(jobId);
        status.RootElement.GetProperty("job").GetProperty("state").GetString().Should().Be("Failed");
        status.RootElement.GetProperty("job").GetProperty("failureCode").GetString()
            .Should().Be("migration_storage_exhausted");
        status.RootElement.GetProperty("session").GetProperty("receivedChunkCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Capability_flags_and_missing_phase_handlers_refuse_work_up_front()
    {
        await using var h = new MigrationHttpHarness { PhasesAvailable = false }.Start();

        var capabilities = await h.SendAsync(HttpMethod.Get, "/api/runtime/capabilities");
        capabilities.Status.Should().Be(HttpStatusCode.OK);
        capabilities.Body.RootElement.GetProperty("supportsLibraryMigration").GetBoolean().Should().BeFalse(
            "a host whose phase handlers are unavailable never advertises a feature it would refuse to run");
        capabilities.Body.RootElement.GetProperty("supportsSafeActivation").GetBoolean().Should().BeTrue(
            "the SelfHosted host implements safe activation (#681 Slice 8)");

        // Preflight refuses and creates no durable reservation while the import
        // phase handler is unavailable.
        var preflight = await h.PreflightAsync(archiveBytes: 8L * 1024 * 1024);
        preflight.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(preflight.Body).Should().Be("migration_import_preparation_unavailable");
        (await h.WithDb(db => db.MigrationStorageReservations.CountAsync())).Should().Be(0);

        var import = await h.CreateJobAsync("Import", "unavailable-key", null);
        import.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(import.Body).Should().Be("migration_import_preparation_unavailable");

        var export = await h.CreateJobAsync("Export", "unavailable-export", null);
        export.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(export.Body).Should().Be("migration_export_artifact_unavailable");
        (await h.WithDb(db => db.MigrationJobRecords.CountAsync())).Should().Be(0);

        // Once a host wires the handlers, the server-side write paths open
        // (preflight can reserve and jobs can be created) and the same
        // availability drives the frontend advertisement.
        h.PhasesAvailable = true;
        var nowAvailable = await h.SendAsync(HttpMethod.Get, "/api/runtime/capabilities");
        nowAvailable.Body.RootElement.GetProperty("supportsLibraryMigration").GetBoolean().Should().BeTrue(
            "the capability follows the same phase availability the routes consult");
        nowAvailable.Body.RootElement.GetProperty("supportsSafeActivation").GetBoolean().Should().BeTrue(
            "safe activation is a SelfHosted host capability independent of the frontend switch");
        var reservation = await h.ReserveAsync("unavailable-key");
        var created = await h.CreateJobAsync("Import", "unavailable-key", reservation);
        created.Status.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Operator_kill_switch_hides_the_capability_and_refuses_new_jobs()
    {
        await using var h = new MigrationHttpHarness { LibraryMigrationEnabled = false }.Start();

        var capabilities = await h.SendAsync(HttpMethod.Get, "/api/runtime/capabilities");
        capabilities.Status.Should().Be(HttpStatusCode.OK);
        capabilities.Body.RootElement.GetProperty("supportsLibraryMigration").GetBoolean().Should().BeFalse(
            "the operator turned the feature off");

        var preflight = await h.PreflightAsync(archiveBytes: 8L * 1024 * 1024);
        preflight.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(preflight.Body).Should().Be("migration_import_preparation_unavailable");
        (await h.WithDb(db => db.MigrationStorageReservations.CountAsync())).Should().Be(0);

        var import = await h.CreateJobAsync("Import", "switch-off-import", null);
        import.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(import.Body).Should().Be("migration_import_preparation_unavailable");

        var export = await h.CreateJobAsync("Export", "switch-off-export", null);
        export.Status.Should().Be(HttpStatusCode.Conflict);
        CodeOf(export.Body).Should().Be("migration_export_artifact_unavailable");
        (await h.WithDb(db => db.MigrationJobRecords.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Legacy_portability_routes_keep_their_contract()
    {
        await using var h = new MigrationHttpHarness().Start();

        using (var export = await h.Client.GetAsync(
            "/api/portability/export",
            HttpCompletionOption.ResponseHeadersRead))
        {
            export.StatusCode.Should().Be(HttpStatusCode.OK);
            export.Content.Headers.ContentType!.MediaType
                .Should().Be("application/vnd.nostos.portable+zip");
            export.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        }

        using (var empty = new ByteArrayContent([]))
        {
            empty.Headers.ContentLength = 0;
            var import = await h.SendAsync(HttpMethod.Post, "/api/portability/import", empty);
            import.Status.Should().Be(HttpStatusCode.BadRequest);
            import.Body.RootElement.GetProperty("error").GetString().Should().Be("empty_archive");
        }
    }

    [Fact]
    public async Task Responses_never_leak_paths_storage_keys_or_internal_identifiers()
    {
        await using var h = new MigrationHttpHarness().Start();
        var jobId = await h.CreateImportJobAsync("leak-key");
        var file = MigrationHttpHarness.DeterministicBytes(4 * 1024 * 1024 + 10);
        await h.CreateSessionAsync(jobId, file);
        (await h.UploadChunkAsync(jobId, file, 0)).Status.Should().Be(HttpStatusCode.OK);

        var payloads = new List<string>();
        foreach (var (method, path) in new (HttpMethod, string)[]
        {
            (HttpMethod.Get, $"/api/portability/migration/jobs/{jobId}"),
            (HttpMethod.Get, $"/api/portability/migration/jobs/{jobId}/upload-session"),
            (HttpMethod.Get, "/api/runtime/capabilities"),
        })
        {
            using var response = await h.Client.SendAsync(new HttpRequestMessage(method, path));
            payloads.Add(await response.Content.ReadAsStringAsync());
        }

        var preflight = await h.PreflightAsync(archiveBytes: 1024);
        payloads.Add(preflight.Body.RootElement.GetRawText());

        foreach (var payload in payloads)
        {
            payload.Should().NotContain(h.Root);
            payload.Should().NotContain(h.TransferPath);
            payload.Should().NotContain(h.DatabasePath);
            payload.Should().NotContain("storageKey");
            payload.Should().NotContain("storage_key");
            payload.Should().NotContain("MigrationJobRecord");
        }
    }

    [Fact]
    public async Task Repeated_preflights_keep_exactly_one_unclaimed_hold()
    {
        await using var h = new MigrationHttpHarness().Start();

        Guid lastReservation = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var (status, body) = await h.PreflightAsync(archiveBytes: 8L * 1024 * 1024);
            status.Should().Be(HttpStatusCode.OK);
            lastReservation = body.RootElement.GetProperty("reservationId").GetGuid();
        }

        var reservations = await h.WithDb(db => db.MigrationStorageReservations.AsNoTracking().ToListAsync());
        reservations.Count.Should().Be(100);
        reservations.Count(r => r.ReleasedAtUtc == null && r.ClaimedJobId == null).Should().Be(1);
        reservations.Count(r => r.ReleasedAtUtc != null).Should().Be(99);
        var live = reservations.Single(r => r.ReleasedAtUtc == null && r.ClaimedJobId == null);
        live.Id.Should().Be(lastReservation);

        // Capacity usage equals exactly one hold, not 100.
        var snapshot = await h.WithCapacityAsync(capacity => capacity.GetSnapshotAsync(default));
        snapshot.ActiveReservationCount.Should().Be(1);
        snapshot.OutstandingReservedBytes.Should().Be(live.ReservedBytes);
    }

    [Fact]
    public async Task Preflight_supersedes_only_unclaimed_reservations()
    {
        await using var h = new MigrationHttpHarness().Start();

        var firstReservation = await h.ReserveAsync("claimed-first");
        var job = await h.CreateJobAsync("Import", "claimed-first", firstReservation);
        job.Status.Should().Be(HttpStatusCode.Created);
        var jobId = JobIdOf(job.Body);

        var second = await h.PreflightAsync(archiveBytes: 8L * 1024 * 1024);
        second.Status.Should().Be(HttpStatusCode.OK);
        var secondReservation = second.Body.RootElement.GetProperty("reservationId").GetGuid();
        secondReservation.Should().NotBe(firstReservation);

        var claimed = await h.WithDb(db => db.MigrationStorageReservations.AsNoTracking()
            .SingleAsync(r => r.Id == firstReservation));
        claimed.ClaimedJobId.Should().Be(jobId);
        claimed.ReleasedAtUtc.Should().BeNull();
        var replacement = await h.WithDb(db => db.MigrationStorageReservations.AsNoTracking()
            .SingleAsync(r => r.Id == secondReservation));
        replacement.ClaimedJobId.Should().BeNull();
        replacement.ReleasedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task Preflight_rejects_out_of_contract_and_overflowing_declared_values()
    {
        await using var h = new MigrationHttpHarness().Start();
        var maxArchiveBytes = 512L * 1024 * 1024 * 1024;
        var maxEntryBytes = 16L * 1024 * 1024 * 1024;

        var cases = new[]
        {
            // Counts bounded by the archive entry ceiling.
            PreflightJson(Counts(books: "20001")),
            PreflightJson(Counts(works: "-1")),
            // Byte totals bounded by the contract limits.
            PreflightJson(Counts(), archiveBytes: (maxArchiveBytes + 1).ToString()),
            PreflightJson(Counts(), mediaBytes: "-1"),
            PreflightJson(Counts(), maxEntry: (maxEntryBytes + 1).ToString()),
            // Numeric overflow in JSON is a parse failure, not a default.
            PreflightJson(Counts(), archiveBytes: "99999999999999999999999999"),
        };

        foreach (var payload in cases)
        {
            using var content = Json(payload);
            var response = await h.SendAsync(
                HttpMethod.Post,
                "/api/portability/migration/preflight",
                content);
            response.Status.Should().Be(HttpStatusCode.BadRequest, payload);
            response.Body.RootElement.EnumerateObject().Select(property => property.Name)
                .Should().Equal(new[] { "error", "message" }, payload);
            CodeOf(response.Body).Should().Be("migration_invalid_request", payload);
        }

        (await h.WithDb(db => db.MigrationStorageReservations.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Malformed_inputs_on_migration_routes_use_the_migration_error_body()
    {
        await using var h = new MigrationHttpHarness().Start();
        var jobId = await h.CreateImportJobAsync("binding-key");

        var sessionPrefix = $"/api/portability/migration/jobs/{jobId}/upload-session";
        var cases = new (string Path, HttpContent Content)[]
        {
            ("/api/portability/migration/preflight", Json("{not json")),
            ("/api/portability/migration/preflight", Json("{}")),
            ("/api/portability/migration/preflight",
                Json(PreflightJson(Counts()).Replace("\"declaredArchiveBytes\":1024", "\"declaredArchiveBytes\":\"lots\""))),
            ("/api/portability/migration/jobs",
                Json("""{"direction":"DefinitelyNotADirection","idempotencyKey":"x"}""")),
            ("/api/portability/migration/jobs", Json("{}")),
            ("/api/portability/migration/jobs", Json("""{"direction":5,"idempotencyKey":"x"}""")),
            ("/api/portability/migration/jobs", Json("""{"direction":"Import"}""")),
            (sessionPrefix, Json("{}")),
            (sessionPrefix, Json("""{"purpose":"Import","totalBytes":"many"}""")),
            (sessionPrefix, Json("""
                {"purpose":"Bogus","totalBytes":1,"chunkSize":4194304,"totalChunks":1,
                 "fileIdentity":{"totalSizeBytes":1,"sha256Checksum":"aa"},"idempotencyKey":"k"}
                """)),
            (sessionPrefix, Json("""
                {"purpose":"Import","totalBytes":99999999999999999999,"chunkSize":4194304,
                 "totalChunks":1,"fileIdentity":{"totalSizeBytes":1,"sha256Checksum":"aa"},"idempotencyKey":"k"}
                """)),
            (sessionPrefix, Json("""
                {"purpose":"Import","totalBytes":1,"chunkSize":4194304,"totalChunks":1,"idempotencyKey":"k"}
                """)),
            ("/api/portability/migration/jobs",
                new StringContent("""{"direction":"Import","idempotencyKey":"x"}""", Encoding.UTF8, "text/plain")),
            ($"/api/portability/migration/jobs/{jobId}/cancel", Json("{bad")),
            ($"/api/portability/migration/jobs/{jobId}/retry", Json("{bad")),
        };

        foreach (var (path, content) in cases)
        {
            using (content)
            {
                var response = await h.SendAsync(HttpMethod.Post, path, content);
                response.Status.Should().Be(HttpStatusCode.BadRequest, path);
                response.Body.RootElement.EnumerateObject().Select(property => property.Name)
                    .Should().Equal(new[] { "error", "message" }, path);
                CodeOf(response.Body).Should().Be("migration_invalid_request", path);
            }
        }

        // No malformed request created a durable row before validation.
        (await h.WithDb(db => db.MigrationJobRecords.CountAsync())).Should().Be(1);
        (await h.WithDb(db => db.MigrationSessionRecords.CountAsync())).Should().Be(0);
        var reservations = await h.WithDb(db => db.MigrationStorageReservations.AsNoTracking().ToListAsync());
        reservations.Should().ContainSingle().Which.ClaimedJobId.Should().Be(jobId);
    }

    [Fact]
    public async Task Invalid_route_values_are_400_and_unknown_routes_stay_404()
    {
        await using var h = new MigrationHttpHarness().Start();

        foreach (var (method, path) in new (HttpMethod, string)[]
        {
            (HttpMethod.Get, "/api/portability/migration/jobs/not-a-guid"),
            (HttpMethod.Post, "/api/portability/migration/jobs/not-a-guid/cancel"),
            (HttpMethod.Post, "/api/portability/migration/jobs/not-a-guid/retry"),
            (HttpMethod.Get, "/api/portability/migration/jobs/not-a-guid/upload-session"),
            (HttpMethod.Post, "/api/portability/migration/jobs/not-a-guid/upload-session"),
            (HttpMethod.Post, "/api/portability/migration/jobs/not-a-guid/upload-session/complete"),
        })
        {
            var response = method == HttpMethod.Get
                ? await h.SendAsync(method, path)
                : await h.SendAsync(method, path, new ByteArrayContent([]));
            response.Status.Should().Be(HttpStatusCode.BadRequest, $"{method} {path}");
            CodeOf(response.Body).Should().Be("migration_invalid_request", $"{method} {path}");
        }

        var badIndex = await h.SendAsync(
            HttpMethod.Put,
            $"/api/portability/migration/jobs/{Guid.NewGuid()}/upload-session/chunks/not-an-int",
            new ByteArrayContent([]));
        badIndex.Status.Should().Be(HttpStatusCode.BadRequest);
        CodeOf(badIndex.Body).Should().Be("migration_invalid_request");

        // Unknown routes under the group are not mapped and stay 404.
        var unknown = await h.SendAsync(
            HttpMethod.Get,
            $"/api/portability/migration/jobs/{Guid.NewGuid()}/definitely-not-a-route");
        unknown.Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Session_payload_hash_is_independent_of_json_enum_formatting()
    {
        var request = new MigrationSessionRequest(
            MigrationSessionPurpose.Import,
            4 * 1024 * 1024 + 100,
            4 * 1024 * 1024,
            2,
            new MigrationFileIdentity(4 * 1024 * 1024 + 100, new string('a', 64), "fingerprint"),
            "canonical-key");

        var hash = SelfHostedMigrationTransferService.PayloadHash(request);
        var canonical = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Purpose = (int)request.Purpose,
            request.TotalBytes,
            request.ChunkSize,
            request.TotalChunks,
            Identity = new MigrationFileIdentity(
                request.FileIdentity.TotalSizeBytes,
                request.FileIdentity.Sha256Checksum.ToLowerInvariant(),
                request.FileIdentity.ClientFingerprint),
        })));

        hash.Should().Be(canonical);
    }

    private static StringContent Json(string payload) =>
        new(payload, Encoding.UTF8, "application/json");

    private static string Counts(string works = "0", string books = "0") =>
        $$"""
        {"works":{{works}},"books":{{books}},"notes":0,"topics":0,"noteTopics":0,"writings":0,
         "writingNotes":0,"collections":0,"collectionMemberships":0,"acquisitions":0,
         "assistantSettings":0,"noteImportBookLinks":0,"mediaEntries":0}
        """;

    private static string PreflightJson(
        string counts,
        string archiveBytes = "1024",
        string mediaBytes = "0",
        string maxEntry = "1024") =>
        $$"""
        {"incomingCounts":{{counts}},"declaredArchiveBytes":{{archiveBytes}},
         "declaredMediaBytes":{{mediaBytes}},"maxSingleEntryBytes":{{maxEntry}},
         "declaredFormatVersion":1,"declaredDataVersion":1,"isOperationalBackup":false}
        """;

    private static Guid JobIdOf(JsonDocument body) =>
        body.RootElement.GetProperty("job").GetProperty("id").GetGuid();

    private static string StateOf(JsonDocument body) =>
        body.RootElement.GetProperty("job").GetProperty("state").GetString()!;

    private static string CodeOf(JsonDocument body) =>
        body.RootElement.GetProperty("error").GetString()!;
}

/// <summary>Body producer that never declares a length and writes incrementally.</summary>
internal sealed class IncrementalContent(byte[] body) : HttpContent
{
    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
    {
        const int step = 64 * 1024;
        for (var offset = 0; offset < body.Length; offset += step)
        {
            var count = Math.Min(step, body.Length - offset);
            await stream.WriteAsync(body.AsMemory(offset, count));
            await Task.Yield();
        }
    }
}
