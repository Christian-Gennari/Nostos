using Microsoft.EntityFrameworkCore;
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
/// <para><b>Phase protocol.</b> <c>Preparing</c> ensures the artifact row
/// exists in <see cref="MigrationExportArtifactState.Preparing"/> and discards a
/// stale attempt-specific <c>library.nostos.tmp</c>. <c>Transferring</c> streams
/// the #678 export directly into that temp file. <c>Validating</c> hashes the
/// completed temp once, opens it through the engine's ZIP pre-validation,
/// atomically renames it to <c>library.nostos</c>, and publishes the artifact
/// row as <see cref="MigrationExportArtifactState.Available"/> in a short
/// fenced mutation; the processor then completes the job. A published artifact
/// is reused after an existence/size check; an incomplete temp is always
/// discarded and regenerated, and a temp is never downloadable.</para>
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
    TimeProvider clock) : IMigrationPhaseHandler
{
    /// <summary>Fixed, server-generated download name; never client-supplied.</summary>
    internal const string ArtifactFileName = "library.nostos";

    private const int HashBufferBytes = 128 * 1024;

    public bool CanHandle(MigrationDirection direction, MigrationJobState state) =>
        direction == MigrationDirection.Export
        && state is MigrationJobState.Preparing or MigrationJobState.Transferring or MigrationJobState.Validating;

    public async Task ExecuteAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        var jobId = context.Job.Id;
        var artifact = await db.MigrationExportArtifactRecords.AsNoTracking()
            .SingleOrDefaultAsync(a => a.JobId == jobId, ct);

        switch (context.Job.State)
        {
            case MigrationJobState.Preparing:
                artifact ??= await CreateArtifactRecordAsync(context, jobId, ct);
                DiscardTemp(jobId);
                return;

            case MigrationJobState.Transferring:
                if (IsAvailableOnDisk(jobId, artifact))
                {
                    return;
                }

                DiscardTemp(jobId);
                await GenerateAsync(context, jobId, ct);
                return;

            case MigrationJobState.Validating:
                if (IsAvailableOnDisk(jobId, artifact))
                {
                    return;
                }

                await ValidateAndPublishAsync(context, jobId, artifact, ct);
                return;
        }
    }

    private async Task<MigrationExportArtifactRecord> CreateArtifactRecordAsync(
        MigrationPhaseContext context,
        Guid jobId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var record = new MigrationExportArtifactRecord
        {
            JobId = jobId,
            State = (int)MigrationExportArtifactState.Preparing,
            StorageKey = paths.GetExportArtifactStorageKey(jobId),
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

    private async Task GenerateAsync(MigrationPhaseContext context, Guid jobId, CancellationToken ct)
    {
        var directory = paths.EnsureDirectoryExists(paths.GetExportDirectory(jobId));
        var tempPath = paths.VerifyPathWithinRoot(Path.Combine(directory, TransferPathResolver.ExportTempFileName));
        try
        {
            await using var file = paths.CreateNewVerifiedFile(
                tempPath,
                HashBufferBytes,
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
        MigrationExportArtifactRecord? artifact,
        CancellationToken ct)
    {
        var directory = paths.EnsureDirectoryExists(paths.GetExportDirectory(jobId));
        var tempPath = paths.VerifyPathWithinRoot(Path.Combine(directory, TransferPathResolver.ExportTempFileName));
        var finalPath = paths.VerifyPathWithinRoot(Path.Combine(directory, TransferPathResolver.ExportFileName));

        // A rename that landed before the artifact row was updated is not
        // published: it has no Available row and no hash/size identity. Remove
        // it and rebuild rather than ever serving an unverified file.
        if (File.Exists(finalPath))
        {
            paths.EnsureFileIsNotReparsePoint(finalPath);
            File.Delete(finalPath);
        }

        if (!File.Exists(tempPath))
        {
            await GenerateAsync(context, jobId, ct);
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

        if (File.Exists(finalPath))
        {
            File.Delete(finalPath);
        }

        paths.EnsureFileIsNotReparsePoint(finalPath);
        File.Move(tempPath, finalPath, overwrite: true);
        paths.VerifyPathWithinRoot(finalPath);

        var now = clock.GetUtcNow();
        await context.ExecuteMutationAsync(async (fenced, token) =>
        {
            var updated = await fenced.MigrationExportArtifactRecords
                .Where(a => a.JobId == jobId
                    && a.State == (int)MigrationExportArtifactState.Preparing)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(a => a.State, (int)MigrationExportArtifactState.Available)
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

        await context.ReportProgressAsync(
            new MigrationProgress(
                MigrationProgressPhase.Validating,
                length,
                length,
                Message: "Export artifact verified."),
            ct);

        await ReleaseReservationAsync(jobId, ct);
    }

    private bool IsAvailableOnDisk(Guid jobId, MigrationExportArtifactRecord? artifact)
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

    private void DiscardTemp(Guid jobId)
    {
        var directory = paths.EnsureDirectoryExists(paths.GetExportDirectory(jobId));
        var tempPath = paths.VerifyPathWithinRoot(Path.Combine(directory, TransferPathResolver.ExportTempFileName));
        if (File.Exists(tempPath))
        {
            paths.EnsureFileIsNotReparsePoint(tempPath);
            File.Delete(tempPath);
        }
    }

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
