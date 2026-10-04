using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Slice 9: turns a sealed, identity-verified upload archive into a durable
/// prepared import under the transfer root and stops the job at
/// <see cref="MigrationJobState.ReadyToActivate"/>. Activation (#681) remains
/// the only path to <see cref="MigrationJobState.Completed"/>.
///
/// <para><b>Phase protocol.</b> The handler runs only in <c>Validating</c> for
/// an import whose session is <c>Complete</c>. It (1) adopts the recorded
/// staging area only when it rebuilds and matches the descriptor persisted on
/// the job (a committed area with no job descriptor is treated as stale and
/// tombstoned), (2) otherwise deletes any recorded area and re-runs
/// <see cref="PortableArchiveReader.PrepareImportAsync"/> entirely outside any
/// database transaction, and (3) persists the committed descriptor through a
/// short fenced mutation. The reader owns staging-area creation; a durable
/// decorator records the identifier the moment it exists, checks the pinned
/// lease between media items and before the staging commit, and publishes the
/// committed metadata when the reader commits.</para>
///
/// <para><b>Ownership.</b> A successor that does not adopt the recorded area
/// deletes it (durable tombstone), after which the stale owner's in-flight
/// staging writes fail typed at the provider. A stale owner can therefore never
/// commit into or corrupt the area the successor uses, and a staging commit it
/// did complete is never publishable because the job-side mutation is fenced.</para>
///
/// <para><b>Reservation.</b> Staging capacity is the reservation claimed when
/// the upload session was admitted; this phase charges nothing twice. At commit
/// the reservation stays claimed: materialized bytes are raised to cover the
/// retained upload plus staged payload/media, while the unmaterialized
/// remainder (candidate build and mandatory recovery headroom) remains reserved
/// for #681. Terminal cleanup releases it on cancel, failure, or expiry.</para>
/// </summary>
internal sealed class ImportPreparationPhaseHandler(
    NostosDbContext db,
    IPortableImportStaging staging,
    ITransferStorageCapacity capacity,
    TransferPathResolver paths,
    ILibraryDestinationRevisionProvider revisionProvider,
    TimeProvider clock) : IMigrationPhaseHandler
{
    public bool CanHandle(MigrationDirection direction, MigrationJobState state) =>
        direction == MigrationDirection.Import && state == MigrationJobState.Validating;

    public async Task ExecuteAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        var jobId = context.Job.Id;
        var record = await db.MigrationJobRecords.AsNoTracking()
            .SingleOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw MigrationJobStoreException.NotFound(jobId);
        var session = await db.MigrationSessionRecords.AsNoTracking()
            .SingleOrDefaultAsync(s => s.JobId == jobId, ct)
            ?? throw MigrationTransferException.Error(MigrationTransferException.InvalidState);

        if (session.State != (int)MigrationSessionState.Complete
            || session.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime)
        {
            // The processor only promotes Transferring -> Validating after the
            // session is Complete; a session that left that state concurrently
            // is a stale transition, not a prepared import.
            throw MigrationTransferException.Error(MigrationTransferException.InvalidState);
        }

        var archivePath = paths.VerifyPathWithinRoot(paths.GetUploadArchivePath(session.Id));
        if (!File.Exists(archivePath))
        {
            throw MigrationTransferException.Error(MigrationTransferException.InvalidState);
        }

        // The revision was captured when the job was accepted. Only an older
        // job with no recorded value captures one here; preparation never
        // silently substitutes a later revision.
        var revision = string.IsNullOrEmpty(record.DestinationRevision)
            ? await revisionProvider.GetCurrentAsync(ct)
            : record.DestinationRevision;

        if (record.PreparedStagingId is { } existingId)
        {
            if (await TryAdoptCommittedAsync(record, existingId, session.TotalBytes, ct))
            {
                return;
            }

            // Uncommitted, stale-committed, corrupt, or mismatched: tombstone it
            // before the reader creates a replacement, so one job never owns two
            // staging areas and a stale owner's in-flight writes fail typed.
            await staging.DeleteAsync(new PortableStagingId(existingId), CancellationToken.None);
        }

        var reservation = await RequireReservationAsync(record, session.TotalBytes, ct);
        var durable = new DurableImportStaging(
            staging,
            checkpoint: context.CheckpointAsync,
            onCreate: id => PublishAsync(context, id.Value, preparedJson: null, revision, ct),
            onCommit: metadata => PublishAsync(
                context,
                metadata.StagingId.Value,
                MigrationPreparedMetadata.Serialize(metadata),
                revision,
                ct));

        var reader = new PortableArchiveReader(timeProvider: clock);
        await using var source = new FilePortableArchiveSource(archivePath);
        await using var progress = new MigrationArchiveProgressPump(
            context,
            MigrationProgressPhase.Validating);
        var prepared = await reader.PrepareImportAsync(source, durable, progress, ct);

        // The prepared import is durable (staging commit and job descriptor).
        // Keep the reservation claimed: materialized bytes now cover the
        // retained upload plus staged bytes, and the unmaterialized remainder is
        // the activation/recovery headroom #681 consumes and releases.
        await EnsureMaterializedAsync(
            reservation.Id,
            session.TotalBytes,
            prepared.Metadata,
            ct);
    }

    /// <summary>
    /// An already committed staging area is adopted only when the provider
    /// rebuilds it and its durable identity matches the descriptor persisted on
    /// the job. A committed area with no persisted descriptor is stale (a
    /// worker whose lease was lost, or a crash before publication): it is
    /// tombstoned by the caller and re-prepared from the sealed upload.
    /// </summary>
    private async Task<bool> TryAdoptCommittedAsync(
        MigrationJobRecord record,
        Guid existingId,
        long archiveBytes,
        CancellationToken ct)
    {
        if (record.PreparedImportMetadataJson is not { } json)
        {
            return false;
        }

        IPreparedPortableImport rebuilt;
        try
        {
            rebuilt = await staging.RebuildPreparedImportAsync(new PortableStagingId(existingId), ct);
        }
        catch (PortableStagingException)
        {
            return false;
        }

        var persisted = MigrationPreparedMetadata.Deserialize(json);
        if (persisted is null || !MigrationPreparedMetadata.Matches(rebuilt.Metadata, persisted))
        {
            return false;
        }

        if (record.ReservationId is { } reservationId)
        {
            try
            {
                await EnsureMaterializedAsync(reservationId, archiveBytes, rebuilt.Metadata, ct);
            }
            catch (TransferReservationException)
            {
                // A pre-retention job may have no live claim; adoption still
                // succeeds and #681 handles a missing top-up claim.
            }
        }

        return true;
    }

    private async Task EnsureMaterializedAsync(
        Guid reservationId,
        long archiveBytes,
        PreparedPortableImportMetadata metadata,
        CancellationToken ct)
    {
        // The manifest allowance is already inside the per-job overhead the
        // reservation carries; only the durable metadata totals are counted.
        var target = checked(archiveBytes + metadata.MediaBytes + metadata.DataBytes);
        await capacity.EnsureMaterializedAtLeastAsync(reservationId, target, ct);
    }

    private async Task<MigrationStorageReservationRecord> RequireReservationAsync(
        MigrationJobRecord record,
        long archiveBytes,
        CancellationToken ct)
    {
        var reservation = record.ReservationId is { } reservationId
            ? await db.MigrationStorageReservations.AsNoTracking().SingleOrDefaultAsync(
                r => r.Id == reservationId
                    && r.ClaimedJobId == record.Id
                    && r.ReleasedAtUtc == null,
                ct)
            : null;

        if (reservation is null || reservation.ReservedBytes < archiveBytes)
        {
            throw MigrationTransferException.Error(MigrationTransferException.StorageExhausted);
        }

        return reservation;
    }

    // Short fenced publication only: no file IO runs inside this callback.
    private static async Task PublishAsync(
        MigrationPhaseContext context,
        Guid stagingId,
        string? preparedJson,
        string revision,
        CancellationToken ct)
    {
        await context.ExecuteMutationAsync(async (fenced, token) =>
        {
            var updated = await fenced.MigrationJobRecords
                .Where(j => j.Id == context.Job.Id
                    && j.State == (int)MigrationJobState.Validating
                    && j.MigrationLeaseToken == context.Job.LeaseToken)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(j => j.PreparedStagingId, stagingId)
                        .SetProperty(j => j.PreparedImportMetadataJson, preparedJson)
                        .SetProperty(j => j.DestinationRevision, revision)
                        .SetProperty(j => j.Version, j => j.Version + 1),
                    token);
            if (updated != 1)
            {
                throw MigrationJobStoreException.LeaseConflict(context.Job.Id);
            }
        }, ct);
    }
}

/// <summary>
/// Durable staging decorator for import preparation. The archive reader creates
/// its own staging area; this decorator persists the identifier the moment it
/// exists (an uncommitted marker a restart can delete), checks the pinned lease
/// before opening each media item and before the staging commit, and publishes
/// the committed descriptor immediately after the staging commit. Every
/// callback is a short lease-fenced database mutation; all archive and file IO
/// remains outside every transaction.
/// </summary>
internal sealed class DurableImportStaging(
    IPortableImportStaging inner,
    Func<CancellationToken, Task> checkpoint,
    Func<PortableStagingId, Task> onCreate,
    Func<PreparedPortableImportMetadata, Task> onCommit) : IPortableImportStaging
{
    public async Task<PortableStagingId> CreateAsync(CancellationToken cancellationToken = default)
    {
        var stagingId = await inner.CreateAsync(cancellationToken);
        await onCreate(stagingId);
        return stagingId;
    }

    public async Task<PortableStagingWrite> OpenMediaWriteAsync(
        PortableStagingId stagingId,
        PortableArchiveMediaEntry descriptor,
        CancellationToken cancellationToken = default)
    {
        // One fenced lease check per media item: a stale owner stops before it
        // can write into an area a successor may already own or delete.
        await checkpoint(cancellationToken);
        return await inner.OpenMediaWriteAsync(stagingId, descriptor, cancellationToken);
    }

    public async Task CommitPreparedImportAsync(
        PortableStagingId stagingId,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        await checkpoint(cancellationToken);
        await inner.CommitPreparedImportAsync(stagingId, metadata, cancellationToken);
        await onCommit(metadata);
    }

    public Task CompleteMediaAsync(
        PortableStagingId stagingId,
        PortableStagingWrite write,
        CancellationToken cancellationToken = default) =>
        inner.CompleteMediaAsync(stagingId, write, cancellationToken);

    public Task<Stream> OpenMediaReadAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        CancellationToken cancellationToken = default) =>
        inner.OpenMediaReadAsync(stagingId, reference, cancellationToken);

    public Task<IReadOnlyList<PortablePreparedMedia>> ListMediaAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        inner.ListMediaAsync(stagingId, cancellationToken);

    public Task<PortableStagingPayloadWrite> OpenDataWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default) =>
        inner.OpenDataWriteAsync(stagingId, descriptor, cancellationToken);

    public Task CompleteDataAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        CancellationToken cancellationToken = default) =>
        inner.CompleteDataAsync(stagingId, write, cancellationToken);

    public Task<Stream> OpenDataReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        inner.OpenDataReadAsync(stagingId, cancellationToken);

    public Task<PortableStagingPayloadWrite> OpenManifestWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default) =>
        inner.OpenManifestWriteAsync(stagingId, descriptor, cancellationToken);

    public Task CompleteManifestAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        CancellationToken cancellationToken = default) =>
        inner.CompleteManifestAsync(stagingId, write, cancellationToken);

    public Task<Stream> OpenManifestReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        inner.OpenManifestReadAsync(stagingId, cancellationToken);

    public Task<IPreparedPortableImport> RebuildPreparedImportAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        inner.RebuildPreparedImportAsync(stagingId, cancellationToken);

    public Task DeleteAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(stagingId, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
