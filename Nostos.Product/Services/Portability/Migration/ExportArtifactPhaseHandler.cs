using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Slice 10: builds an export artifact independently of any browser request and
/// publishes it for the range-enabled download endpoint.
///
/// <para><b>Phase protocol.</b> <c>Preparing</c> ensures the artifact row exists
/// in <see cref="MigrationExportArtifactState.Preparing"/> and points its
/// storage key at this attempt's unique temp file. <c>Transferring</c> streams
/// the #678 export directly into that temp file. <c>Validating</c> hashes the
/// completed temp once, opens it through the engine's ZIP pre-validation,
/// re-checks the pinned lease, atomically renames it to an attempt-unique final
/// name, and publishes the artifact row as
/// <see cref="MigrationExportArtifactState.Available"/> — including the final
/// storage key — in a short fenced mutation; the processor then completes the
/// job. A published artifact is reused after an existence/size check; an
/// incomplete or stale temp is always discarded and regenerated, and a temp is
/// never downloadable.</para>
///
/// <para><b>Ownership.</b> Every attempt publishes under its own attempt-unique
/// final name with <c>overwrite: false</c>, so a stale owner can never replace
/// the successor's bytes. The lease is re-checked through the fencing helper
/// immediately before the rename; if the fenced row update then fails (lease
/// lost or state changed), the worker deletes its own file and stops. Files no
/// row references are removed by the cleanup sweep.</para>
///
/// <para><b>Concurrency and memory.</b> Generation runs outside every database
/// transaction. The #678 export rents its 16 MiB synchronous capture lease from
/// the phase context's operation budget (when the real service is present), so
/// each concurrent export accounts the capture buffer plus the copy buffer on
/// its own budget; worker admission (<c>MaxConcurrentJobs</c>) bounds how many
/// exports generate at once. The artifact reservation is released when the
/// artifact is published: the sealed file is already reflected in physical
/// free-space accounting.</para>
/// </summary>
internal sealed class ExportArtifactPhaseHandler(
    NostosDbContext db,
    IPortableArchiveService archiveService,
    ITransferStorageCapacity capacity,
    TransferPathResolver paths,
    IOptions<TransferStorageOptions> options,
    TimeProvider clock,
    IServiceProvider services) : IMigrationPhaseHandler
{
    /// <summary>Fixed, server-generated download name; never client-supplied.</summary>
    internal const string ArtifactFileName = "library.nostos";

    private const int BufferBytes = 128 * 1024;

    public bool CanHandle(MigrationDirection direction, MigrationJobState state) =>
        direction == MigrationDirection.Export
        && state is MigrationJobState.Preparing or MigrationJobState.Transferring or MigrationJobState.Validating;

    public async Task ExecuteAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        var jobId = context.Job.Id;
        var attempt = await db.MigrationJobRecords.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => (int?)j.AttemptNumber)
            .SingleOrDefaultAsync(ct) ?? 1;
        var artifact = await db.MigrationExportArtifactRecords.AsNoTracking()
            .SingleOrDefaultAsync(a => a.JobId == jobId, ct);

        switch (context.Job.State)
        {
            case MigrationJobState.Preparing:
                if (artifact is null)
                {
                    await CreateArtifactRecordAsync(context, jobId, attempt, ct);
                }
                else if (artifact.State == (int)MigrationExportArtifactState.Preparing)
                {
                    // A restart may re-enter Preparing with a stale attempt key.
                    await ResetToPreparingAsync(context, jobId, attempt, ct);
                }

                return;

            case MigrationJobState.Transferring:
                if (IsAvailableOnDisk(artifact))
                {
                    return;
                }

                if (artifact is { State: (int)MigrationExportArtifactState.Available })
                {
                    artifact = await ResetToPreparingAsync(context, jobId, attempt, ct);
                }

                await GenerateAsync(context, jobId, RequireTempKey(artifact), ct);
                return;

            case MigrationJobState.Validating:
                if (IsAvailableOnDisk(artifact))
                {
                    return;
                }

                if (artifact is { State: (int)MigrationExportArtifactState.Available })
                {
                    artifact = await ResetToPreparingAsync(context, jobId, attempt, ct);
                }

                await ValidateAndPublishAsync(context, jobId, attempt, artifact, ct);
                return;
        }
    }

    private async Task<MigrationExportArtifactRecord> CreateArtifactRecordAsync(
        MigrationPhaseContext context,
        Guid jobId,
        int attempt,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var tempKey = paths.GetExportAttemptTempStorageKey(jobId, attempt, NewToken());
        var record = new MigrationExportArtifactRecord
        {
            JobId = jobId,
            State = (int)MigrationExportArtifactState.Preparing,
            StorageKey = tempKey,
            FileName = ArtifactFileName,
            ContentType = PortabilityEndpoints.ArchiveContentType,
            CreatedAtUtc = now.UtcDateTime,
            ExpiresAtUtc = now.Add(options.Value.ExportRetentionTtl).UtcDateTime,
        };

        await context.ExecuteMutationAsync(async (fenced, token) =>
        {
            if (await fenced.MigrationExportArtifactRecords.AnyAsync(a => a.JobId == jobId, token))
            {
                return;
            }

            fenced.MigrationExportArtifactRecords.Add(record);
            await fenced.SaveChangesAsync(token);
        }, ct);

        return await db.MigrationExportArtifactRecords.AsNoTracking()
            .SingleAsync(a => a.JobId == jobId, ct);
    }

    /// <summary>
    /// Points the row back at a fresh attempt temp key and deletes the old
    /// attempt temp it referenced (a stale owner's writes fail typed once its
    /// fenced checkpoint runs). Used when the row is Available but the file is
    /// missing or corrupt, so recovery regenerates instead of failing forever.
    /// </summary>
    private async Task<MigrationExportArtifactRecord> ResetToPreparingAsync(
        MigrationPhaseContext context,
        Guid jobId,
        int attempt,
        CancellationToken ct)
    {
        var tempKey = paths.GetExportAttemptTempStorageKey(jobId, attempt, NewToken());
        await context.ExecuteMutationAsync(async (fenced, token) =>
        {
            var updated = await fenced.MigrationExportArtifactRecords
                .Where(a => a.JobId == jobId
                    && a.State == (int)MigrationExportArtifactState.Available)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(a => a.State, (int)MigrationExportArtifactState.Preparing)
                        .SetProperty(a => a.StorageKey, tempKey)
                        .SetProperty(a => a.Version, a => a.Version + 1),
                    token);
            if (updated != 1)
            {
                throw MigrationJobStoreException.LeaseConflict(jobId);
            }
        }, ct);

        return await db.MigrationExportArtifactRecords.AsNoTracking()
            .SingleAsync(a => a.JobId == jobId, ct);
    }

    private string RequireTempKey(MigrationExportArtifactRecord? artifact)
    {
        if (artifact is null || !paths.TryResolveStorageKey(artifact.StorageKey, out _))
        {
            throw MigrationTransferException.Error(MigrationTransferException.InvalidState);
        }

        return artifact.StorageKey;
    }

    private async Task GenerateAsync(
        MigrationPhaseContext context,
        Guid jobId,
        string tempKey,
        CancellationToken ct)
    {
        paths.EnsureDirectoryExists(paths.GetExportDirectory(jobId));
        var tempPath = paths.VerifyPathWithinRoot(paths.ResolveStorageKey(tempKey));

        // An incomplete temp from a crashed generation is never trusted.
        if (File.Exists(tempPath))
        {
            paths.EnsureFileIsNotReparsePoint(tempPath);
            File.Delete(tempPath);
        }

        try
        {
            await using var file = paths.CreateNewVerifiedFile(
                tempPath,
                BufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var sink = new StreamPortableArchiveSink(file, leaveOpen: true);
            await using (sink)
            await using (var progress = new MigrationArchiveProgressPump(
                context,
                MigrationProgressPhase.Transferring))
            {
                if (archiveService is PortableArchiveService concrete)
                {
                    // The capture lease is charged to this job's operation
                    // budget, which is the accounting concurrent exports see.
                    await concrete.ExportAsync(sink, progress, context.BufferBudget, ct);
                }
                else
                {
                    await archiveService.ExportAsync(sink, progress, ct);
                }
            }

            await file.FlushAsync(ct);
            file.Flush(flushToDisk: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private async Task ValidateAndPublishAsync(
        MigrationPhaseContext context,
        Guid jobId,
        int attempt,
        MigrationExportArtifactRecord? artifact,
        CancellationToken ct)
    {
        var tempKey = RequireTempKey(artifact);
        var tempPath = paths.VerifyPathWithinRoot(paths.ResolveStorageKey(tempKey));
        if (!File.Exists(tempPath))
        {
            await GenerateAsync(context, jobId, tempKey, ct);
        }

        var (length, sha256) = await PortableStagingFilePrimitives
            .HashFileAsync(paths.VerifyPathWithinRoot(tempPath), ct);
        if (length == 0 || length > MigrationContractLimits.MaxArchiveBytes)
        {
            throw new PortableArchiveException(
                "export_artifact_invalid",
                "The generated export artifact is empty or exceeds the archive contract limit.");
        }

        // Pre-validate the finished bytes through the same bounded reader the
        // import path uses; a structurally invalid artifact is never published.
        await using (var source = new FilePortableArchiveSource(tempPath))
        {
            var reader = await PortableArchiveZipReader.OpenAsync(source, context.BufferBudget, ct);
            await reader.DisposeAsync();
        }

        var finalKey = paths.GetExportAttemptArtifactStorageKey(jobId, attempt, NewToken());
        var finalPath = paths.VerifyPathWithinRoot(paths.ResolveStorageKey(finalKey));
        paths.EnsureParentDirectoryExists(finalPath);

        // Ownership fence immediately before the filesystem mutation: a lease
        // lost while hashing stops here, before any shared name is touched.
        services.GetService<MigrationArchivePhaseTestHooks>()?.BeforeExportRename?.Invoke();
        await context.CheckpointAsync(ct);

        paths.EnsureFileIsNotReparsePoint(finalPath);
        File.Move(tempPath, finalPath, overwrite: false);
        paths.VerifyPathWithinRoot(finalPath);

        // Test seam: production leaves this null. It lets a test force the
        // fenced publication below to lose its lease after the rename and prove
        // the worker deletes its own file.
        services.GetService<MigrationArchivePhaseTestHooks>()?.AfterExportRename?.Invoke(finalPath);

        var now = clock.GetUtcNow();
        try
        {
            await context.ExecuteMutationAsync(async (fenced, token) =>
            {
                var updated = await fenced.MigrationExportArtifactRecords
                    .Where(a => a.JobId == jobId
                        && a.State == (int)MigrationExportArtifactState.Preparing)
                    .ExecuteUpdateAsync(
                        set => set
                            .SetProperty(a => a.State, (int)MigrationExportArtifactState.Available)
                            .SetProperty(a => a.StorageKey, finalKey)
                            .SetProperty(a => a.SizeBytes, length)
                            .SetProperty(a => a.Sha256, sha256)
                            .SetProperty(a => a.AvailableAtUtc, now.UtcDateTime)
                            .SetProperty(a => a.ExpiresAtUtc, now.Add(options.Value.ExportRetentionTtl).UtcDateTime)
                            .SetProperty(a => a.Version, a => a.Version + 1),
                        token);
                if (updated != 1)
                {
                    throw MigrationJobStoreException.LeaseConflict(jobId);
                }
            }, ct);
        }
        catch (MigrationJobStoreException)
        {
            // The row was not published under this key: remove this attempt's
            // own file and stop. No successor-owned bytes are ever touched.
            TryDelete(finalPath);
            throw;
        }

        await context.ReportProgressAsync(
            new MigrationProgress(
                MigrationProgressPhase.Validating,
                length,
                length,
                Message: "Export artifact verified."),
            ct);

        await ReleaseReservationAsync(jobId, ct);
    }

    private bool IsAvailableOnDisk(MigrationExportArtifactRecord? artifact)
    {
        if (artifact is not { State: (int)MigrationExportArtifactState.Available })
        {
            return false;
        }

        if (!paths.TryResolveStorageKey(artifact.StorageKey, out var path) || !File.Exists(path))
        {
            return false;
        }

        return new FileInfo(path).Length == artifact.SizeBytes;
    }

    private async Task ReleaseReservationAsync(Guid jobId, CancellationToken ct)
    {
        var reservationId = await db.MigrationJobRecords.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => j.ReservationId)
            .SingleOrDefaultAsync(ct);
        if (reservationId is not { } id)
        {
            return;
        }

        try
        {
            await capacity.ReleaseAsync(id, ct);
        }
        catch (Exception exception) when (
            exception is TransferReservationException or DbUpdateException)
        {
            // Terminal cleanup retries the release; a published artifact must
            // not be reported as failed because accounting is contended.
        }
    }

    private static string NewToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort: the job's terminal cleanup detaches the export scope.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort: the job's terminal cleanup detaches the export scope.
        }
    }
}
