using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Slice 9: turns a sealed, identity-verified upload archive into a durable
/// prepared import under the transfer root and stops the job at
/// <see cref="MigrationJobState.ReadyToActivate"/>. Activation (#681) remains
/// the only path to <see cref="MigrationJobState.Completed"/>.
///
/// <para><b>Phase protocol.</b> The handler runs only in <c>Validating</c> for
/// an import whose session is <c>Complete</c>. It (1) adopts an already
/// committed staging area for this attempt when one rebuilds and matches the
/// persisted descriptor, (2) otherwise deletes any recorded staging area and
/// re-runs <see cref="PortableArchiveReader.PrepareImportAsync"/> entirely
/// outside any database transaction, and (3) persists the committed descriptor
/// through a short fenced mutation. The reader owns staging-area creation; a
/// durable decorator records the identifier the moment it exists and publishes
/// the committed metadata when the reader commits, so a restart always knows
/// which single area to delete before preparing again.</para>
///
/// <para><b>Durability.</b> <c>PreparedStagingId</c> records the attempt's
/// staging area from the moment it exists; <c>PreparedImportMetadataJson</c> is
/// the commit marker written last. Staging capacity is the reservation claimed
/// when the upload session was admitted: this phase charges nothing twice and
/// releases that reservation once the prepared import is durable, because the
/// retained upload and staging bytes are already visible to physical free-space
/// accounting. Failures release reservations and delete staging through the
/// worker's terminal cleanup.</para>
/// </summary>
internal sealed class ImportPreparationPhaseHandler(
    NostosDbContext db,
    IPortableImportStaging staging,
    ITransferStorageCapacity capacity,
    TransferPathResolver paths,
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

        var revision = string.IsNullOrEmpty(record.DestinationRevision)
            ? await ReadDestinationRevisionAsync(ct)
            : record.DestinationRevision;

        if (record.PreparedStagingId is { } existingId)
        {
            if (await TryAdoptCommittedAsync(context, record, existingId, revision, ct))
            {
                return;
            }

            // Uncommitted, corrupt, or mismatched: discard before the reader
            // creates a replacement, so one job never owns two staging areas.
            await staging.DeleteAsync(new PortableStagingId(existingId), CancellationToken.None);
        }

        var reservation = await RequireReservationAsync(record, session.TotalBytes, ct);
        var durable = new DurableImportStaging(
            staging,
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
        _ = await reader.PrepareImportAsync(source, durable, progress, ct);

        // The prepared import is durable (staging commit and job descriptor).
        // Retained bytes now govern physical free space directly, so release the
        // upload reservation instead of double-counting it against admission.
        try
        {
            await capacity.ReleaseAsync(reservation.Id, ct);
        }
        catch (Exception exception) when (
            exception is TransferReservationException or DbUpdateException)
        {
            // Terminal cleanup retries the release; it must never turn a
            // successfully prepared import into a failure.
        }
    }

    /// <summary>
    /// An already committed staging area is adopted only when the provider
    /// rebuilds it and its durable identity matches the descriptor persisted on
    /// the job. A rebuild with no persisted descriptor happens when a crash
    /// landed after the staging commit but before the job update; the provider's
    /// own commit marker and payload verification are the durable authority.
    /// </summary>
    private async Task<bool> TryAdoptCommittedAsync(
        MigrationPhaseContext context,
        MigrationJobRecord record,
        Guid existingId,
        string revision,
        CancellationToken ct)
    {
        IPreparedPortableImport rebuilt;
        try
        {
            rebuilt = await staging.RebuildPreparedImportAsync(new PortableStagingId(existingId), ct);
        }
        catch (PortableStagingException)
        {
            return false;
        }

        if (record.PreparedImportMetadataJson is { } json)
        {
            var persisted = MigrationPreparedMetadata.Deserialize(json);
            return persisted is not null && MigrationPreparedMetadata.Matches(rebuilt.Metadata, persisted);
        }

        await PublishAsync(
            context,
            existingId,
            MigrationPreparedMetadata.Serialize(rebuilt.Metadata),
            revision,
            ct);
        return true;
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

    private async Task<string> ReadDestinationRevisionAsync(CancellationToken ct)
    {
        var revision = await db.LibraryStates.AsNoTracking()
            .Select(s => s.StateVersion)
            .SingleOrDefaultAsync(ct);
        return string.IsNullOrEmpty(revision) ? "0" : revision;
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
/// exists (an uncommitted marker a restart can delete) and publishes the
/// committed descriptor immediately after the staging commit. Both callbacks
/// are short lease-fenced database mutations; all archive and file IO remains
/// outside every transaction.
/// </summary>
internal sealed class DurableImportStaging(
    IPortableImportStaging inner,
    Func<PortableStagingId, Task> onCreate,
    Func<PreparedPortableImportMetadata, Task> onCommit) : IPortableImportStaging
{
    public async Task<PortableStagingId> CreateAsync(CancellationToken cancellationToken = default)
    {
        var stagingId = await inner.CreateAsync(cancellationToken);
        await onCreate(stagingId);
        return stagingId;
    }

    public async Task CommitPreparedImportAsync(
        PortableStagingId stagingId,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        await inner.CommitPreparedImportAsync(stagingId, metadata, cancellationToken);
        await onCommit(metadata);
    }

    public Task<PortableStagingWrite> OpenMediaWriteAsync(
        PortableStagingId stagingId,
        PortableArchiveMediaEntry descriptor,
        CancellationToken cancellationToken = default) =>
        inner.OpenMediaWriteAsync(stagingId, descriptor, cancellationToken);

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
