using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// HTTP-facing orchestration for durable migration jobs. Creation claims the
/// preflight reservation atomically with the job row; status is derived from
/// durable job/session/artifact records only. Session and chunk bodies are
/// delegated to <see cref="ISelfHostedMigrationUploads"/> so the transport can
/// never accidentally call the metadata-less stream overload that the engine
/// intentionally rejects.
/// </summary>
public sealed class SelfHostedMigrationJobService(
    NostosDbContext db,
    IMigrationJobStore jobs,
    ITransferStorageCapacity capacity,
    ISelfHostedMigrationUploads uploads,
    MigrationTransferCleanup cleanup,
    IMigrationPhaseAvailability phaseAvailability,
    ILibraryDestinationRevisionProvider revisionProvider,
    IOptions<TransferStorageOptions> options,
    TimeProvider? timeProvider = null)
{
    private const int MaxIdempotencyKeyLength = 128;
    private static readonly JsonSerializerOptions PreparedImportJson = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<MigrationIdempotencyResult<MigrationJob>> CreateAsync(
        MigrationCreateJobRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Direction))
            throw MigrationTransferException.Error(MigrationTransferException.InvalidRequest);

        var key = request.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxIdempotencyKeyLength)
            throw MigrationTransferException.Error(MigrationTransferException.InvalidRequest);

        EnsureDirectionAvailable(request.Direction);

        if (request.Direction == MigrationDirection.Import && request.ReservationId is null)
            throw MigrationTransferException.Error(MigrationTransferException.ReservationRequired);

        var id = Guid.NewGuid();
        var now = Now;
        var reservedBytes = 0L;

        // Admission is serialized on the same per-library singleton row every
        // portable writer uses: replay lookup, outstanding count and insert run
        // inside one transaction holding that lock, so concurrent creators with
        // different keys cannot both pass the ceiling.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LibraryRevision.LockSingletonAsync(db, ct);

        var existing = await FindByIdempotencyKeyAsync(key, ct);
        if (existing is not null)
        {
            await transaction.CommitAsync(ct);
            return await ReplayAsync(existing, request, ct);
        }

        // Per-installation ceiling on outstanding durable work: a bounded
        // number of non-terminal jobs (and therefore sessions) keeps a client
        // from parking unbounded rows, reservations and scratch. Terminal jobs
        // are retention/cleanup's concern, not admission's.
        var outstanding = await db.MigrationJobRecords.AsNoTracking()
            .CountAsync(j => j.State != (int)MigrationJobState.Completed
                && j.State != (int)MigrationJobState.Failed
                && j.State != (int)MigrationJobState.Cancelled
                && j.State != (int)MigrationJobState.Expired, ct);
        if (outstanding >= options.Value.MaxOutstandingJobs)
        {
            throw MigrationTransferException.Error(MigrationTransferException.TooManyJobs);
        }

        // The revision is the destination at the moment the job is accepted,
        // before any upload or preparation work. Preparation must never
        // substitute a later value; activation compares this baseline.
        var destinationRevision = await revisionProvider.GetCurrentAsync(ct);
        if (request.ReservationId is { } reservationId)
        {
            try
            {
                await capacity.ClaimAsync(reservationId, id, ct);
            }
            catch (TransferReservationException)
            {
                await transaction.RollbackAsync(ct);
                var winner = await FindByIdempotencyKeyAsync(key, ct);
                if (winner is not null) return await ReplayAsync(winner, request, ct);
                throw MigrationTransferException.Error(MigrationTransferException.ReservationRequired);
            }

            reservedBytes = await db.MigrationStorageReservations.AsNoTracking()
                .Where(r => r.Id == reservationId)
                .Select(r => r.ReservedBytes)
                .SingleAsync(ct);
        }

        var record = new MigrationJobRecord
        {
            Id = id,
            Direction = (int)request.Direction,
            State = (int)MigrationJobState.Pending,
            RecoveryStatus = (int)MigrationRecoveryStatus.NotRequired,
            ProgressPhase = (int)MigrationProgressPhase.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IdempotencyKey = key,
            CreationPayloadHash = EfMigrationJobStore.ComputeCreationPayloadHash(request.Direction),
            ExpiresAtUtc = now.Add(EfMigrationJobStore.JobLifetime),
            AttemptNumber = 1,
            ReservationId = request.ReservationId,
            ReservedStorageBytes = reservedBytes,
            DestinationRevision = destinationRevision,
        };

        db.MigrationJobRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException exception) when (EfMigrationJobStore.IsUniqueConstraintViolation(exception))
        {
            // A concurrent creator won the unique idempotency-key race. Rolling
            // back also releases this loser's reservation claim.
            await transaction.RollbackAsync(ct);
            db.Entry(record).State = EntityState.Detached;
            var winner = await FindByIdempotencyKeyAsync(key, ct);
            if (winner is null) throw;
            return await ReplayAsync(winner, request, ct);
        }

        var job = await jobs.GetAsync(id, ct)
            ?? throw new InvalidOperationException("The created migration job could not be read back.");
        return MigrationIdempotencyResult<MigrationJob>.Created(job);
    }

    public async Task<MigrationJobStatusResponse> GetStatusAsync(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, ct) ?? throw MigrationJobStoreException.NotFound(jobId);
        var record = await FindRecordAsync(jobId, ct) ?? throw MigrationJobStoreException.NotFound(jobId);
        var sessionRecord = await db.MigrationSessionRecords.AsNoTracking()
            .SingleOrDefaultAsync(s => s.JobId == jobId, ct);
        var session = sessionRecord is null ? null : await BuildSessionStatusAsync(sessionRecord, ct);
        var artifact = await db.MigrationExportArtifactRecords.AsNoTracking()
            .SingleOrDefaultAsync(a => a.JobId == jobId, ct);
        var downloadAvailable = artifact is not null
            && artifact.State == (int)MigrationExportArtifactState.Available
            && artifact.ExpiresAtUtc > Now;

        return new MigrationJobStatusResponse(
            // The worker lease token is an internal concurrency capability: no
            // route accepts it, the browser derives processing state from the
            // job state, and it never crosses the transport.
            job with { LeaseToken = null, LeaseExpiresAtUtc = null },
            BuildProgress(record, sessionRecord, session),
            session,
            downloadAvailable,
            artifact is null ? null : AsUtc(artifact.ExpiresAtUtc),
            ReadPreparedImport(record));
    }

    public async Task<MigrationJobStatusResponse> CancelAsync(
        Guid jobId,
        MigrationCancelRequest request,
        CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, ct) ?? throw MigrationJobStoreException.NotFound(jobId);
        if (job.State != MigrationJobState.Cancelled
            && MigrationJobTransitions.IsTerminal(job.State))
        {
            // Activation is the point of no return; terminal history is never
            // rewritten. Both are the plan's "cannot cancel" transport outcome.
            // An already-cancelled job replays idempotently through the store.
            throw new MigrationJobStoreException(
                MigrationJobStoreErrorCodes.CannotCancel,
                "The migration job is terminal and cannot be cancelled.");
        }

        await uploads.CancelAsync(jobId, request, ct);
        return await GetStatusAsync(jobId, ct);
    }

    public async Task<MigrationJobStatusResponse> RetryAsync(
        Guid jobId,
        MigrationRetryRequest request,
        CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, ct) ?? throw MigrationJobStoreException.NotFound(jobId);
        if (!MigrationJobTransitions.IsRetryable(job.State))
        {
            // Deterministic path for the "session expired but its active job has
            // not been swept yet" window (review-725): clean up the expired
            // session synchronously, which requires the job to become Expired,
            // so a retry can immediately reactivate it.
            var session = await db.MigrationSessionRecords.AsNoTracking()
                .SingleOrDefaultAsync(s => s.JobId == jobId, ct);
            var sessionExpired = session is not null
                && (session.State == (int)MigrationSessionState.Expired || session.ExpiresAtUtc <= Now);
            if (sessionExpired && job.Direction == MigrationDirection.Import
                && job.State is MigrationJobState.Pending or MigrationJobState.Preparing
                    or MigrationJobState.Transferring or MigrationJobState.Validating)
            {
                await cleanup.CleanupJobAsync(jobId, ct);
            }
        }

        var retried = await jobs.RetryAsync(jobId, request, ct);
        return await GetStatusAsync(retried.Id, ct);
    }

    public async Task<MigrationIdempotencyResult<MigrationUploadSessionResponse>> CreateUploadSessionAsync(
        Guid jobId,
        MigrationSessionRequest request,
        CancellationToken ct)
    {
        var result = await uploads.CreateSessionAsync(jobId, request, ct);
        if (result.IsConflict)
            return MigrationIdempotencyResult<MigrationUploadSessionResponse>.Conflicted(result.Conflict!);

        var response = await GetUploadSessionAsync(jobId, ct);
        return result.WasReplay
            ? MigrationIdempotencyResult<MigrationUploadSessionResponse>.Replayed(response)
            : MigrationIdempotencyResult<MigrationUploadSessionResponse>.Created(response);
    }

    public async Task<MigrationUploadSessionResponse> GetUploadSessionAsync(
        Guid jobId,
        CancellationToken ct)
    {
        var session = await RequireUploadSessionAsync(jobId, ct);
        if (session.State == (int)MigrationSessionState.Expired || session.ExpiresAtUtc <= Now)
            throw MigrationTransferException.Error(MigrationTransferException.Expired);

        var status = await BuildSessionStatusAsync(session, ct);
        return new MigrationUploadSessionResponse(status, MigrationChunkRanges.Compress(status.ReceivedChunks));
    }

    public async Task<MigrationChunkUploadResult> UploadChunkAsync(
        Guid jobId,
        int chunkIndex,
        MigrationChunkMetadata metadata,
        Stream content,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);
        var session = await RequireUploadSessionAsync(jobId, ct);
        return await uploads.UploadChunkAsync(jobId, session.Id, chunkIndex, metadata, content, ct);
    }

    public async Task<MigrationSessionStatus> CompleteUploadAsync(Guid jobId, CancellationToken ct)
    {
        var session = await RequireUploadSessionAsync(jobId, ct);
        return await uploads.CompleteSessionAsync(jobId, session.Id, ct);
    }

    private void EnsureDirectionAvailable(MigrationDirection direction)
    {
        if (phaseAvailability.IsAvailable(direction)) return;
        throw MigrationTransferException.Error(direction == MigrationDirection.Import
            ? MigrationTransferException.ImportPreparationUnavailable
            : MigrationTransferException.ExportArtifactUnavailable);
    }

    private async Task<MigrationIdempotencyResult<MigrationJob>> ReplayAsync(
        MigrationJobRecord existing,
        MigrationCreateJobRequest request,
        CancellationToken ct)
    {
        // The strict creation payload is direction plus the reservation
        // selector: the same key may not silently bind a different reservation.
        if (existing.Direction != (int)request.Direction
            || existing.ReservationId != request.ReservationId)
        {
            return MigrationIdempotencyResult<MigrationJob>.Conflicted(
                new MigrationIdempotencyConflict(
                    MigrationIdempotencyConflictKind.KeyReusedWithDifferentPayload));
        }

        var job = await jobs.GetAsync(existing.Id, ct)
            ?? throw MigrationJobStoreException.NotFound(existing.Id);
        return MigrationIdempotencyResult<MigrationJob>.Replayed(job);
    }

    private async Task<MigrationSessionRecord> RequireUploadSessionAsync(Guid jobId, CancellationToken ct)
    {
        _ = await jobs.GetAsync(jobId, ct) ?? throw MigrationJobStoreException.NotFound(jobId);
        return await db.MigrationSessionRecords.AsNoTracking()
            .SingleOrDefaultAsync(s => s.JobId == jobId, ct)
            ?? throw MigrationTransferException.Error(MigrationTransferException.InvalidState);
    }

    private async Task<MigrationSessionStatus> BuildSessionStatusAsync(
        MigrationSessionRecord session,
        CancellationToken ct)
    {
        var received = await db.MigrationChunkReceiptRecords.AsNoTracking()
            .Where(r => r.SessionId == session.Id)
            .OrderBy(r => r.ChunkIndex)
            .Select(r => r.ChunkIndex)
            .ToListAsync(ct);

        return new MigrationSessionStatus(
            session.Id,
            (MigrationSessionPurpose)session.Purpose,
            (MigrationSessionState)session.State,
            session.TotalBytes,
            session.ChunkSize,
            session.TotalChunks,
            new MigrationFileIdentity(
                session.FileIdentitySizeBytes,
                session.FileIdentitySha256,
                session.ClientFingerprint),
            received,
            received.Count,
            AsUtc(session.CreatedAtUtc),
            AsUtc(session.ExpiresAtUtc),
            session.IdempotencyKey);
    }

    private static MigrationProgress BuildProgress(
        MigrationJobRecord record,
        MigrationSessionRecord? sessionRecord,
        MigrationSessionStatus? session)
    {
        var state = (MigrationJobState)record.State;
        var totalBytes = session?.TotalBytes ?? record.ProgressTotalBytes;
        var completedChunks = session?.ReceivedChunkCount ?? record.ProgressCompletedChunks;
        var totalChunks = session?.TotalChunks ?? record.ProgressTotalChunks;

        return state switch
        {
            MigrationJobState.Pending => new(
                MigrationProgressPhase.Pending, 0, totalBytes, completedChunks, totalChunks),
            MigrationJobState.Preparing => new(
                MigrationProgressPhase.Preparing, 0, totalBytes, completedChunks, totalChunks),
            // Canonical import transfer progress is derived from durable receipts,
            // not from worker memory or the sparse file length.
            MigrationJobState.Transferring => new(
                MigrationProgressPhase.Transferring,
                sessionRecord?.ReceivedBytes ?? 0,
                totalBytes,
                completedChunks,
                totalChunks),
            MigrationJobState.Validating or MigrationJobState.ReadyToActivate => new(
                MigrationProgressPhase.Validating, totalBytes ?? 0, totalBytes, completedChunks, totalChunks),
            MigrationJobState.Activating => new(
                MigrationProgressPhase.Activating, totalBytes ?? 0, totalBytes, completedChunks, totalChunks),
            MigrationJobState.Completed => new(
                MigrationProgressPhase.Completed, totalBytes ?? 0, totalBytes, completedChunks, totalChunks),
            _ => new(
                (MigrationProgressPhase)record.ProgressPhase,
                record.ProgressBytesProcessed,
                record.ProgressTotalBytes,
                record.ProgressCompletedChunks,
                record.ProgressTotalChunks,
                record.ProgressMessage),
        };
    }

    private static PreparedPortableImportMetadata? ReadPreparedImport(MigrationJobRecord record)
    {
        if (record.PreparedStagingId is null
            || string.IsNullOrWhiteSpace(record.PreparedImportMetadataJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PreparedPortableImportMetadata>(
                record.PreparedImportMetadataJson,
                PreparedImportJson);
        }
        catch (JsonException)
        {
            // Status never fails because an unreadable prepared descriptor is
            // present; activation/recovery owns that failure with a typed code.
            return null;
        }
    }

    private Task<MigrationJobRecord?> FindRecordAsync(Guid jobId, CancellationToken ct) =>
        db.MigrationJobRecords.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct);

    private Task<MigrationJobRecord?> FindByIdempotencyKeyAsync(string key, CancellationToken ct) =>
        db.MigrationJobRecords.AsNoTracking().SingleOrDefaultAsync(j => j.IdempotencyKey == key, ct);

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
