using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// SelfHosted additive transport seam: the frozen stream-only contract cannot
/// express a checksum/range, so its upload method rejects missing metadata.
/// HTTP callers must use this overload after parsing the mandatory headers.
/// </summary>
public interface ISelfHostedMigrationUploads : IMigrationTransferService
{
    Task<MigrationChunkUploadResult> UploadChunkAsync(Guid jobId, Guid sessionId, int chunkIndex,
        MigrationChunkMetadata metadata, Stream content, CancellationToken ct);
    Task CancelAsync(Guid jobId, MigrationCancelRequest request, CancellationToken ct);
    Task DeleteSessionAsync(Guid jobId, Guid sessionId, CancellationToken ct);
}

/// <summary>
/// Installation-scoped durable uploads. Every file mutation holds the database
/// job write lock; session state, receipts and capped materialization commit as
/// one unit. Duplicate receipt hashes never rewrite archive bytes. Store and
/// engine clocks must be synchronized on every host sharing the database.
/// </summary>
public sealed class SelfHostedMigrationTransferService(
    NostosDbContext db,
    IMigrationJobStore jobs,
    ITransferStorageCapacity capacity,
    FileMigrationUploadStore files,
    IOptions<TransferStorageOptions> options,
    MigrationJobCancellationRegistry cancellations,
    MigrationTransferCleanup cleanup,
    TimeProvider? timeProvider = null) : ISelfHostedMigrationUploads
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<MigrationIdempotencyResult<MigrationSessionStatus>> CreateSessionAsync(
        Guid jobId, MigrationSessionRequest request, CancellationToken ct)
    {
        ValidateRequest(request);
        var hash = PayloadHash(request);
        var existing = await db.MigrationSessionRecords.AsNoTracking().SingleOrDefaultAsync(s => s.JobId == jobId, ct);
        if (existing is not null)
        {
            var conflict = CheckPayload(existing, request, hash);
            if (conflict is not null) return conflict;
            var current = await jobs.GetAsync(jobId, ct) ?? throw MigrationJobStoreException.NotFound(jobId);
            if (existing.State == (int)MigrationSessionState.Complete && !MigrationJobTransitions.IsRetryable(current.State))
                return MigrationIdempotencyResult<MigrationSessionStatus>.Replayed(await StatusAsync(existing, ct));
        }

        // Reserve before the file/DB transaction: admission owns its own serializable
        // transaction. Any losing creator releases its unclaimed candidate below.
        var job = await db.MigrationJobRecords.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw MigrationJobStoreException.NotFound(jobId);
        if (job.Direction != (int)MigrationDirection.Import || MigrationJobTransitions.IsTerminal((MigrationJobState)job.State))
            throw MigrationTransferException.Error(MigrationTransferException.InvalidState);
        var required = Math.Max(job.ReservedStorageBytes,
            TransferCapacityMath.CalculateHostPeakReservationBytes(request.TotalBytes, request.ChunkSize, options.Value));
        var reservation = job.ReservationId is { } reservationId
            ? await db.MigrationStorageReservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == reservationId
                && r.ClaimedJobId == jobId && r.ReleasedAtUtc == null, ct) : null;
        Guid? candidate = null;
        if (reservation is null)
        {
            var admitted = await capacity.TryReserveAsync(required, MigrationSessionPurpose.Import,
                options.Value.PreflightReservationTtl, ct);
            if (!admitted.IsAdmitted) throw MigrationTransferException.Error(MigrationTransferException.StorageExhausted);
            candidate = admitted.ReservationId;
        }
        else if (reservation.ReservedBytes < required)
            throw MigrationTransferException.Error(MigrationTransferException.StorageExhausted);

        var claimed = false;
        try
        {
            await using var transaction = await MigrationMutation.BeginAsync(db, ct);
            await MigrationMutation.LockUploadJobAsync(db, jobId, Now, ct);
            job = await db.MigrationJobRecords.AsNoTracking().SingleAsync(j => j.Id == jobId, ct);
            existing = await db.MigrationSessionRecords.AsNoTracking().SingleOrDefaultAsync(s => s.JobId == jobId, ct);
            if (existing is not null)
            {
                var conflict = CheckPayload(existing, request, hash);
                if (conflict is not null) return conflict;
            }
            var live = job.ReservationId is { } liveId
                ? await db.MigrationStorageReservations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == liveId
                    && r.ClaimedJobId == jobId && r.ReleasedAtUtc == null, ct) : null;
            if (live is null)
            {
                if (candidate is null) throw MigrationTransferException.Error(MigrationTransferException.StorageExhausted);
                await capacity.ClaimAsync(candidate.Value, jobId, ct);
                await db.MigrationJobRecords.Where(j => j.Id == jobId && j.Version == job.Version)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.ReservationId, candidate)
                        .SetProperty(j => j.ReservedStorageBytes, required)
                        .SetProperty(j => j.Version, j => j.Version + 1), ct);
                live = await db.MigrationStorageReservations.AsNoTracking().SingleAsync(r => r.Id == candidate.Value, ct);
            }
            if (live.ReservedBytes < required)
                throw MigrationTransferException.Error(MigrationTransferException.StorageExhausted);

            var replay = existing is not null;
            if (existing is not null && (existing.State is (int)MigrationSessionState.Cancelled or (int)MigrationSessionState.Expired
                || existing.ExpiresAtUtc <= Now))
            {
                // Only RetryAsync opens a new Pending attempt; ordinary resume
                // cannot revive a cancelled/expired session.
                if (job.State != (int)MigrationJobState.Pending || job.AttemptNumber <= 1)
                    throw MigrationTransferException.Error(MigrationTransferException.InvalidState);
                var present = files.HasArchive(existing.Id);
                if (!present)
                {
                    await db.MigrationChunkReceiptRecords.Where(r => r.SessionId == existing.Id).ExecuteDeleteAsync(ct);
                    await files.CreateAsync(existing.Id, existing.TotalBytes, ct);
                }
                await db.MigrationSessionRecords.Where(s => s.Id == existing.Id && s.Version == existing.Version)
                    .ExecuteUpdateAsync(s => s.SetProperty(s => s.State, present && existing.CompletedAtUtc != null
                            ? (int)MigrationSessionState.Complete : (int)MigrationSessionState.Receiving)
                        .SetProperty(s => s.ReceivedBytes, present ? existing.ReceivedBytes : 0)
                        .SetProperty(s => s.CompletedAtUtc, present ? existing.CompletedAtUtc : null)
                        .SetProperty(s => s.ExpiresAtUtc, Now.AddHours(MigrationContractLimits.SessionExpiryHours))
                        .SetProperty(s => s.UpdatedAtUtc, Now).SetProperty(s => s.Version, s => s.Version + 1), ct);
                existing = await SessionAsync(jobId, existing.Id, ct);
                if (present && candidate == live.Id && existing.ReceivedBytes > 0)
                    await capacity.AddMaterializedBytesAsync(live.Id, existing.ReceivedBytes, ct);
            }
            if (existing is null)
            {
                existing = new MigrationSessionRecord
                {
                    Id = Guid.NewGuid(), JobId = jobId, Purpose = (int)request.Purpose,
                    State = (int)MigrationSessionState.Created, TotalBytes = request.TotalBytes,
                    ChunkSize = request.ChunkSize, TotalChunks = request.TotalChunks,
                    FileIdentitySizeBytes = request.TotalBytes, FileIdentitySha256 = request.FileIdentity.Sha256Checksum.ToLowerInvariant(),
                    ClientFingerprint = request.FileIdentity.ClientFingerprint, IdempotencyKey = request.IdempotencyKey,
                    CreationPayloadHash = hash, CreatedAtUtc = Now, UpdatedAtUtc = Now,
                    ExpiresAtUtc = Now.AddHours(MigrationContractLimits.SessionExpiryHours),
                };
                await files.CreateAsync(existing.Id, existing.TotalBytes, ct);
                existing.StorageKey = files.Paths.ToStorageKey(files.Paths.GetUploadArchivePartPath(existing.Id));
                db.MigrationSessionRecords.Add(existing);
                await db.SaveChangesAsync(ct);
                db.Entry(existing).State = EntityState.Detached;
            }
            await transaction.CommitAsync(ct);
            claimed = candidate == live.Id;
            var status = await StatusAsync(existing, ct);
            return replay ? MigrationIdempotencyResult<MigrationSessionStatus>.Replayed(status)
                : MigrationIdempotencyResult<MigrationSessionStatus>.Created(status);
        }
        catch (IOException ex)
        {
            await FailUploadAsync(jobId, MigrationTransferException.StorageExhausted);
            throw new MigrationTransferException(MigrationTransferException.StorageExhausted, "Upload storage is unavailable.", ex);
        }
        finally
        {
            if (candidate is { } unused && !claimed)
            {
                try { await capacity.ReleaseAsync(unused, CancellationToken.None); }
                catch { /* The unclaimed TTL sweep retries; never mask the create failure. */ }
            }
        }
    }

    public async Task<MigrationSessionStatus> GetSessionAsync(Guid jobId, Guid sessionId, CancellationToken ct) =>
        await StatusAsync(await SessionAsync(jobId, sessionId, ct), ct);

    public Task<MigrationChunkUploadResult> UploadChunkAsync(Guid jobId, Guid sessionId, int chunkIndex,
        Stream content, CancellationToken ct) =>
        throw MigrationTransferException.Error(MigrationTransferException.MetadataRequired);

    public async Task<MigrationChunkUploadResult> UploadChunkAsync(Guid jobId, Guid sessionId, int chunkIndex,
        MigrationChunkMetadata metadata, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);
        string? temp = null;
        try
        {
            await using var transaction = await MigrationMutation.BeginAsync(db, ct);
            await MigrationMutation.LockUploadJobAsync(db, jobId, Now, ct);
            var session = await SessionAsync(jobId, sessionId, ct);
            RequireReceiving(session);
            var (offset, length, checksum) = ValidateChunk(session, chunkIndex, metadata);
            using var budget = new BudgetScope();
            // Even duplicate bodies are verified; a matching header alone is no proof.
            temp = await files.ReceiveAsync(sessionId, chunkIndex, length, checksum, content, budget.Value, ct);
            var receipt = await db.MigrationChunkReceiptRecords.AsNoTracking()
                .SingleOrDefaultAsync(r => r.SessionId == sessionId && r.ChunkIndex == chunkIndex, ct);
            if (receipt is not null)
            {
                if (receipt.LengthBytes != length || receipt.Sha256 != checksum)
                    throw MigrationTransferException.Error(MigrationTransferException.ChunkConflict);
                await transaction.CommitAsync(ct);
                return new(sessionId, chunkIndex, true);
            }
            await files.PlaceAsync(sessionId, temp, offset, budget.Value, ct);
            RequireReceiving(session); // The clock may have advanced during IO.
            if (await MigrationMutation.Active(db, jobId, Now).AnyAsync(ct) == false)
                throw MigrationTransferException.Error(MigrationTransferException.InvalidState);
            var reservationId = await db.MigrationJobRecords.Where(j => j.Id == jobId).Select(j => j.ReservationId).SingleAsync(ct)
                ?? throw MigrationTransferException.Error(MigrationTransferException.StorageExhausted);
            await capacity.AddMaterializedBytesAsync(reservationId, length, ct);
            var updated = await db.MigrationSessionRecords.Where(s => s.Id == sessionId && s.Version == session.Version
                    && s.ExpiresAtUtc > Now && (s.State == (int)MigrationSessionState.Created || s.State == (int)MigrationSessionState.Receiving))
                .ExecuteUpdateAsync(s => s.SetProperty(s => s.State, (int)MigrationSessionState.Receiving)
                    .SetProperty(s => s.ReceivedBytes, s => s.ReceivedBytes + length)
                    .SetProperty(s => s.UpdatedAtUtc, Now).SetProperty(s => s.Version, s => s.Version + 1), ct);
            if (updated != 1) throw MigrationTransferException.Error(MigrationTransferException.InvalidState);
            var added = new MigrationChunkReceiptRecord { SessionId = sessionId, ChunkIndex = chunkIndex,
                OffsetBytes = offset, LengthBytes = length, Sha256 = checksum, ReceivedAtUtc = Now };
            db.MigrationChunkReceiptRecords.Add(added);
            await db.SaveChangesAsync(ct);
            db.Entry(added).State = EntityState.Detached;
            await transaction.CommitAsync(ct);
            return new(sessionId, chunkIndex, false);
        }
        catch (TransferReservationException ex)
        {
            await FailUploadAsync(jobId, MigrationTransferException.StorageExhausted);
            throw new MigrationTransferException(MigrationTransferException.StorageExhausted, "Upload reservation is unavailable.", ex);
        }
        catch (IOException ex)
        {
            await FailUploadAsync(jobId, MigrationTransferException.StorageExhausted);
            throw new MigrationTransferException(MigrationTransferException.StorageExhausted, "Upload storage is unavailable.", ex);
        }
        finally { if (temp is not null) files.TryDelete(temp); }
    }

    public async Task<MigrationSessionStatus> CompleteSessionAsync(Guid jobId, Guid sessionId, CancellationToken ct)
    {
        try
        {
            await using var transaction = await MigrationMutation.BeginAsync(db, ct);
            await MigrationMutation.LockUploadJobAsync(db, jobId, Now, ct);
            var session = await SessionAsync(jobId, sessionId, ct);
            if (session.State == (int)MigrationSessionState.Complete)
                return await StatusAsync(session, ct);
            RequireReceiving(session);
            var receipts = await db.MigrationChunkReceiptRecords.AsNoTracking().Where(r => r.SessionId == sessionId)
                .OrderBy(r => r.ChunkIndex).ToListAsync(ct);
            if (receipts.Count != session.TotalChunks || session.ReceivedBytes != session.TotalBytes
                || receipts.Where((r, i) => r.ChunkIndex != i || r.OffsetBytes != checked((long)i * session.ChunkSize)
                    || r.LengthBytes != (int)Math.Min(session.ChunkSize, session.TotalBytes - r.OffsetBytes)).Any())
                throw MigrationTransferException.Error(MigrationTransferException.MissingChunks);
            using var budget = new BudgetScope();
            if (!await files.VerifyAsync(sessionId, session.TotalBytes, session.FileIdentitySha256, budget.Value, ct))
                throw MigrationTransferException.Error(MigrationTransferException.IdentityMismatch);
            RequireReceiving(session);
            files.Seal(sessionId);
            if (await db.MigrationSessionRecords.Where(s => s.Id == sessionId && s.Version == session.Version
                    && s.ExpiresAtUtc > Now && (s.State == (int)MigrationSessionState.Created || s.State == (int)MigrationSessionState.Receiving))
                .ExecuteUpdateAsync(s => s.SetProperty(s => s.State, (int)MigrationSessionState.Complete)
                    .SetProperty(s => s.CompletedAtUtc, Now).SetProperty(s => s.UpdatedAtUtc, Now)
                    .SetProperty(s => s.StorageKey, files.Paths.GetUploadArchiveStorageKey(sessionId))
                    .SetProperty(s => s.Version, s => s.Version + 1), ct) != 1)
                throw MigrationTransferException.Error(MigrationTransferException.InvalidState);
            await transaction.CommitAsync(ct);
            return await GetSessionAsync(jobId, sessionId, ct);
        }
        catch (MigrationTransferException ex) when (ex.Code == MigrationTransferException.IdentityMismatch)
        { await FailUploadAsync(jobId, ex.Code); throw; }
        catch (IOException ex)
        {
            await FailUploadAsync(jobId, MigrationTransferException.StorageExhausted);
            throw new MigrationTransferException(MigrationTransferException.StorageExhausted, "Upload storage is unavailable.", ex);
        }
    }

    public Task<MigrationActivationPreparation> PrepareActivationAsync(Guid jobId, CancellationToken ct) =>
        throw MigrationTransferException.Error(MigrationTransferException.ImportPreparationUnavailable);

    public async Task CancelAsync(Guid jobId, MigrationCancelRequest request, CancellationToken ct)
    {
        await jobs.CancelAsync(jobId, request, ct);
        cancellations.Cancel(jobId);
        await cleanup.CleanupJobAsync(jobId, ct);
    }

    /// <summary>Explicit abandon. Cancels the owning pre-activation job before releasing its resources.</summary>
    public async Task DeleteSessionAsync(Guid jobId, Guid sessionId, CancellationToken ct)
    {
        await SessionAsync(jobId, sessionId, ct);
        await jobs.CancelAsync(jobId, new("Upload abandoned."), ct);
        cancellations.Cancel(jobId);
        await cleanup.CleanupJobAsync(jobId, ct, discardUploads: true);
    }

    private async Task FailUploadAsync(Guid jobId, string code)
    {
        try
        {
            await using var transaction = await MigrationMutation.BeginAsync(db, CancellationToken.None);
            var current = await db.MigrationJobRecords.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId);
            if (current is null || !MigrationJobTransitions.CanTransition((MigrationDirection)current.Direction,
                (MigrationJobState)current.State, MigrationJobState.Failed)) return;
            if (await MigrationMutation.Active(db, jobId, Now).Where(j => j.Version == current.Version && j.State == current.State)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, (int)MigrationJobState.Failed)
                    .SetProperty(j => j.FailureCode, code).SetProperty(j => j.FailureMessage, code)
                    .SetProperty(j => j.MigrationLeaseToken, (string?)null).SetProperty(j => j.LeaseExpiresAtUtc, (DateTime?)null)
                    .SetProperty(j => j.UpdatedAtUtc, Now).SetProperty(j => j.Version, j => j.Version + 1)) != 1) return;
            await db.MigrationSessionRecords.Where(s => s.JobId == jobId)
                .ExecuteUpdateAsync(s => s.SetProperty(s => s.State, (int)MigrationSessionState.Cancelled)
                    .SetProperty(s => s.UpdatedAtUtc, Now).SetProperty(s => s.Version, s => s.Version + 1));
            await transaction.CommitAsync();
        }
        catch { /* Original IO/integrity failure remains authoritative; sweep retries leftovers. */ }
        cancellations.Cancel(jobId);
        try { await cleanup.CleanupJobAsync(jobId, CancellationToken.None, discardUploads: true); }
        catch { /* Terminal metadata is retained for cleanup retry. */ }
    }

    private void ValidateRequest(MigrationSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Purpose != MigrationSessionPurpose.Import || request.TotalBytes is <= 0 or > MigrationContractLimits.MaxArchiveBytes
            || !MigrationContractLimits.IsValidChunkSize(request.ChunkSize)
            || request.ChunkSize < options.Value.MinChunkBytes || request.ChunkSize > options.Value.MaxChunkBytes
            || request.TotalChunks != checked((request.TotalBytes - 1) / request.ChunkSize + 1)
            || request.FileIdentity is null || request.FileIdentity.TotalSizeBytes != request.TotalBytes
            || !IsSha256(request.FileIdentity.Sha256Checksum) || request.FileIdentity.ClientFingerprint is { Length: > 256 }
            || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128)
            throw MigrationTransferException.Error(MigrationTransferException.InvalidRequest);
    }

    private static MigrationIdempotencyResult<MigrationSessionStatus>? CheckPayload(MigrationSessionRecord existing,
        MigrationSessionRequest request, string hash)
    {
        if (existing.IdempotencyKey == request.IdempotencyKey && existing.CreationPayloadHash != hash)
            return MigrationIdempotencyResult<MigrationSessionStatus>.Conflicted(new(MigrationIdempotencyConflictKind.KeyReusedWithDifferentPayload));
        if (existing.FileIdentitySizeBytes != request.FileIdentity.TotalSizeBytes
            || existing.FileIdentitySha256 != request.FileIdentity.Sha256Checksum.ToLowerInvariant()
            || existing.ClientFingerprint != request.FileIdentity.ClientFingerprint)
            throw MigrationTransferException.Error(MigrationTransferException.IdentityMismatch);
        if (existing.CreationPayloadHash != hash || existing.IdempotencyKey != request.IdempotencyKey)
            return MigrationIdempotencyResult<MigrationSessionStatus>.Conflicted(new(MigrationIdempotencyConflictKind.KeyReusedWithDifferentPayload));
        return null;
    }

    private static string PayloadHash(MigrationSessionRequest r) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { r.Purpose, r.TotalBytes, r.ChunkSize, r.TotalChunks,
            Identity = new MigrationFileIdentity(r.FileIdentity.TotalSizeBytes, r.FileIdentity.Sha256Checksum.ToLowerInvariant(), r.FileIdentity.ClientFingerprint) })));

    private void RequireReceiving(MigrationSessionRecord session)
    {
        if (session.ExpiresAtUtc <= Now || session.State == (int)MigrationSessionState.Expired)
            throw MigrationTransferException.Error(MigrationTransferException.Expired);
        if (session.State is not ((int)MigrationSessionState.Created) and not ((int)MigrationSessionState.Receiving))
            throw MigrationTransferException.Error(MigrationTransferException.InvalidState);
    }

    private static (long Offset, int Length, string Hash) ValidateChunk(MigrationSessionRecord s, int index, MigrationChunkMetadata m)
    {
        if (index < 0 || index >= s.TotalChunks || !IsSha256(m.Sha256))
            throw MigrationTransferException.Error(MigrationTransferException.RangeInvalid);
        var offset = checked((long)index * s.ChunkSize);
        var length = checked((int)Math.Min(s.ChunkSize, s.TotalBytes - offset));
        if (m.Start != offset || m.End != checked(offset + length - 1) || m.Total != s.TotalBytes
            || !MigrationContractLimits.IsValidChunkBytes(length, s.ChunkSize, index == s.TotalChunks - 1))
            throw MigrationTransferException.Error(MigrationTransferException.RangeInvalid);
        return (offset, length, m.Sha256.ToLowerInvariant());
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private Task<MigrationSessionRecord> SessionAsync(Guid jobId, Guid sessionId, CancellationToken ct) =>
        FindSessionAsync(jobId, sessionId, ct);
    private async Task<MigrationSessionRecord> FindSessionAsync(Guid jobId, Guid sessionId, CancellationToken ct) =>
        await db.MigrationSessionRecords.AsNoTracking().SingleOrDefaultAsync(s => s.JobId == jobId && s.Id == sessionId, ct)
        ?? throw MigrationJobStoreException.NotFound(jobId);
    private async Task<MigrationSessionStatus> StatusAsync(MigrationSessionRecord s, CancellationToken ct)
    {
        var received = await db.MigrationChunkReceiptRecords.AsNoTracking().Where(r => r.SessionId == s.Id)
            .OrderBy(r => r.ChunkIndex).Select(r => r.ChunkIndex).ToListAsync(ct);
        return new(s.Id, (MigrationSessionPurpose)s.Purpose, (MigrationSessionState)s.State, s.TotalBytes, s.ChunkSize,
            s.TotalChunks, new(s.FileIdentitySizeBytes, s.FileIdentitySha256, s.ClientFingerprint), received, received.Count,
            new(DateTime.SpecifyKind(s.CreatedAtUtc, DateTimeKind.Utc)), new(DateTime.SpecifyKind(s.ExpiresAtUtc, DateTimeKind.Utc)), s.IdempotencyKey);
    }
    // Each upload/completion has its own budget; no shared allocation pool grows
    // with archive size or number of sessions.
    private sealed class BudgetScope : IDisposable
    {
        internal PortableArchiveBufferBudget Value { get; } = new(FileMigrationUploadStore.BufferBytes);
        public void Dispose() { }
    }
}
