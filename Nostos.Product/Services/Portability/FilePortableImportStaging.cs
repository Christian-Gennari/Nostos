using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Names one provider operation so interleaving tests can pause a specific
/// transition before it takes its staging-area gate. Never used in production.
/// </summary>
internal enum FileStagingOperation
{
    OpenMediaWrite,
    CompleteMedia,
    OpenPayloadWrite,
    CompletePayload,
    Commit,
    Read,
    List,
    Rebuild,
    Delete,
}

/// <summary>
/// The durable transition a crash simulation stops after. The seams sit exactly
/// where a real process death would leave either the previous state or the next
/// state, never a partially published item.
/// </summary>
internal enum FileStagingCrashPoint
{
    /// <summary>Writer flushed and verified; the scratch file is not yet renamed.</summary>
    AfterMediaSealedBeforeRename,

    /// <summary>Media renamed into place; the inventory update has not run.</summary>
    AfterMediaRenamedBeforeInventory,

    /// <summary>Payload writer flushed and verified; not yet renamed.</summary>
    AfterPayloadSealedBeforeRename,

    /// <summary>Payload renamed into place; the inventory update has not run.</summary>
    AfterPayloadRenamedBeforeInventory,

    /// <summary>Committed state flushed to <c>state.json.tmp</c>; not yet swapped in.</summary>
    BeforeCommitMarkerReplace,
}

/// <summary>
/// Thrown by a crash seam to model process death. The provider deliberately leaves
/// the area exactly as the crash point describes and never runs its discard path.
/// </summary>
internal sealed class FileStagingSimulatedCrashException : Exception
{
    public FileStagingSimulatedCrashException(FileStagingCrashPoint point)
        : base($"Simulated process crash at '{point}'.") => Point = point;

    public FileStagingCrashPoint Point { get; }
}

/// <summary>
/// Test-only seams: deterministic interleaving before an area gate, write
/// barriers, and crash stops. Production instances pass none of them.
/// </summary>
internal sealed class FilePortableImportStagingHooks
{
    internal Action<FileStagingCrashPoint>? CrashAt { get; set; }

    internal Func<FileStagingOperation, Task>? BeforeAreaGateAsync { get; set; }

    internal Action? BeforeStreamWrite { get; set; }
}

/// <summary>
/// Durable on-disk inventory for one staging area (<c>state.json</c>): the
/// completed payload identities, every completed media record with its generated
/// reference, and the committed prepared descriptor when one exists.
/// </summary>
internal sealed class FileStagingInventory
{
    public int Version { get; set; } = 1;

    public FileStagingPayloadRecord? Data { get; set; }

    public FileStagingPayloadRecord? Manifest { get; set; }

    public List<FileStagingMediaRecord> Media { get; set; } = [];

    public PreparedPortableImportMetadata? Prepared { get; set; }
}

internal sealed record FileStagingPayloadRecord(long Length, string Sha256);

internal sealed record FileStagingMediaRecord(
    string Reference,
    PortableArchiveMediaEntry Descriptor);

/// <summary>
/// One in-process staging area. Two provider instances over the same transfer root
/// share the coordinator for a staging id, so one area is always guarded by one
/// gate and cross-instance delete/complete/write races settle in the same legal
/// outcomes as single-instance races. Coordinators are process-lifetime tombstones
/// for deleted areas and are bounded by the number of staging areas a process ever
/// touches.
/// </summary>
internal sealed class FileStagingCoordinator
{
    internal FileStagingCoordinator(Guid stagingId, string directory)
    {
        StagingId = stagingId;
        Directory = directory;
    }

    internal Guid StagingId { get; }

    internal string Directory { get; }

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal bool Deleted { get; set; }

    internal bool InventoryLoaded { get; set; }

    internal FileStagingInventory Inventory { get; set; } = new();

    internal Dictionary<PortableStagingMediaIdentity, FileMediaItem> MediaByIdentity { get; } = [];

    internal Dictionary<string, FileMediaItem> MediaByReference { get; } =
        new(StringComparer.Ordinal);

    internal Dictionary<PortableStagingWrite, FileMediaItem> MediaByHandle { get; } = [];

    internal Dictionary<PortableStagingPayloadWrite, FilePayloadItem> PayloadByHandle { get; } = [];

    internal FilePayloadItem? Data { get; set; }

    internal FilePayloadItem? Manifest { get; set; }

    internal PreparedPortableImportMetadata? Prepared { get; set; }
}

internal sealed class FileMediaItem
{
    public required PortableArchiveMediaEntry Descriptor { get; init; }

    public required string Reference { get; init; }

    public required PortableStagingWriteState State { get; init; }

    public PortableStagingWrite? Handle { get; set; }
}

internal sealed class FilePayloadItem
{
    public required long Length { get; init; }

    public required string Sha256 { get; init; }

    public required PortableStagingWriteState State { get; init; }

    public PortableStagingPayloadWrite? Handle { get; set; }
}

/// <summary>
/// Durable, restart-survivable <see cref="IPortableImportStaging"/> that stores a
/// prepared import under the configured transfer root's <c>staging/</c> area
/// (issue #679, plan sections 2.2, 2.4, and Slice 4).
/// </summary>
/// <remarks>
/// <para><b>On-disk layout.</b> One directory per staging id, named from the
/// server-generated GUID (<c>"N"</c> format), containing <c>state.json</c> (the
/// durable inventory and, after commit, the prepared descriptor),
/// <c>state.json.tmp</c> (a transient copy-on-write replacement),
/// <c>manifest.json</c>, <c>data/library.json</c>, and <c>media/&lt;reference&gt;</c>
/// where each reference is 32 random bytes encoded lowercase hexadecimal. Names
/// are derived only from server-generated identifiers and opaque references
/// validated by <see cref="TransferPathResolver"/>; archive entry names,
/// filenames, headers, and client input never become paths.</para>
/// <para><b>Atomicity and durability policy.</b> Every item is written to a
/// server-generated scratch file and flushed with <c>Flush(flushToDisk: true)</c>
/// before it is atomically renamed into place on the same volume, and the complete
/// length and SHA-256 recorded while writing must match the descriptor bound to
/// the handle before publication. The inventory (including the committed prepared
/// descriptor, which is always written last) is replaced copy-on-write:
/// <c>state.json.tmp</c> is written, flushed to disk, and atomically moved over
/// <c>state.json</c>. A crash at any point therefore leaves either the previous
/// visible state or the new state (plus an unreferenced scratch file or final file
/// that is never listed, read, or rebuilt and is removed by delete or a later
/// sweep). The provider does not claim transactional filesystem+database
/// atomicity; reconciliation treats durable state as authoritative. Directory
/// entries themselves are not fsynced, which is best-effort power-loss durability
/// rather than a power-failure guarantee.</para>
/// <para><b>Concurrency.</b> Every transition runs under one gate per staging area.
/// Two instances over the same transfer root share that gate for the same staging
/// id, so delete wins or completion wins deterministically and a racing operation
/// fails with the typed not-found outcome rather than publishing a partial item.
/// In-flight write handles are process-local, so after a process restart any
/// scratch files from the previous process are invisible leftovers.</para>
/// </remarks>
public sealed class FilePortableImportStaging : IPortableImportStaging
{
    /// <summary>Durable inventory and committed prepared descriptor.</summary>
    public const string StateFileName = "state.json";

    /// <summary>Transient copy-on-write replacement for <see cref="StateFileName"/>.</summary>
    public const string StateTempFileName = "state.json.tmp";

    /// <summary>Directory that holds the singular relational payload.</summary>
    public const string DataDirectoryName = "data";

    /// <summary>File name of the singular relational payload.</summary>
    public const string DataFileName = "library.json";

    /// <summary>File name of the archive manifest.</summary>
    public const string ManifestFileName = "manifest.json";

    private static readonly ConcurrentDictionary<string, FileStagingCoordinator> Coordinators =
        new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly TransferPathResolver _resolver;
    private readonly FilePortableImportStagingHooks? _hooks;
    private readonly string _coordinatorScope;

    /// <summary>
    /// Creates a durable staging provider over the transfer root the resolver
    /// owns. The provider shares one process-wide coordinator per staging id with
    /// every other instance built from the same resolver root.
    /// </summary>
    public FilePortableImportStaging(TransferPathResolver resolver)
        : this(resolver, hooks: null, coordinatorScope: string.Empty)
    {
    }

    /// <summary>
    /// Test seam: a non-empty coordinator scope isolates this instance's in-process
    /// coordination from other instances over the same root, so a test can prove
    /// that committed state is rebuilt from disk alone. Production always uses the
    /// default scope.
    /// </summary>
    internal FilePortableImportStaging(
        TransferPathResolver resolver,
        FilePortableImportStagingHooks? hooks,
        string coordinatorScope)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(coordinatorScope);

        _resolver = resolver;
        _hooks = hooks;
        _coordinatorScope = coordinatorScope;
    }

    public async Task<PortableStagingId> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var id = Guid.NewGuid();
            var area = GetCoordinator(id);
            if (area.Deleted || area.InventoryLoaded || Directory.Exists(area.Directory))
                continue;

            await area.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(area.Directory))
                    continue;

                _resolver.EnsureDirectoryExists(area.Directory);
                _resolver.EnsureDirectoryExists(MediaDirectoryPath(area));
                await PersistInventoryAsync(area, crashPoint: null, cancellationToken)
                    .ConfigureAwait(false);
                return new PortableStagingId(id);
            }
            catch
            {
                PortableStagingFilePrimitives.TryDeleteDirectory(area.Directory);
                throw;
            }
            finally
            {
                area.Gate.Release();
            }
        }

        throw PortableStagingFilePrimitives.Conflict(
            "Could not allocate a fresh staging identifier.");
    }

    public async Task<PortableStagingWrite> OpenMediaWriteAsync(
        PortableStagingId stagingId,
        PortableArchiveMediaEntry descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var area = await EnterAreaAsync(stagingId, FileStagingOperation.OpenMediaWrite, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            EnsureNotCommitted(area);

            if (descriptor.Length < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(descriptor),
                    "The declared media length cannot be negative.");
            }

            if (descriptor.Length > PortableArchiveLimits.MaxSingleEntryBytes)
            {
                throw new PortableStagingException(
                    PortableStagingException.LimitExceededCode,
                    $"The declared media length exceeds the {PortableArchiveLimits.MaxSingleEntryBytes}-byte staging limit.");
            }

            var identity = new PortableStagingMediaIdentity(
                descriptor.BookId,
                descriptor.Kind,
                descriptor.Path);
            if (area.MediaByIdentity.ContainsKey(identity))
            {
                throw PortableStagingFilePrimitives.Conflict(
                    "A write for this media item is already open or completed.");
            }

            _resolver.EnsureDirectoryExists(MediaDirectoryPath(area));
            var reference = PortableStagingFilePrimitives.NewReference(
                candidate => area.MediaByReference.ContainsKey(candidate));
            var finalPath = MediaPath(area, reference);
            var tempPath = finalPath + ".tmp." + Guid.NewGuid().ToString("N");
            var state = PortableStagingWriteState.Open(
                _resolver.CreateNewVerifiedFile(
                    tempPath,
                    PortableStagingFilePrimitives.StreamBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan),
                tempPath,
                finalPath,
                descriptor.Length);
            var item = new FileMediaItem
            {
                Descriptor = descriptor,
                Reference = reference,
                State = state,
            };
            var write = new PortableStagingWrite(
                new PortableStagedMediaReference(reference),
                new PortableStagingWriteStream(
                    area.Gate,
                    state,
                    () => area.Deleted,
                    () => DiscardMedia(area, item),
                    _hooks?.BeforeStreamWrite));
            item.Handle = write;

            area.MediaByIdentity.Add(identity, item);
            area.MediaByReference.Add(reference, item);
            area.MediaByHandle.Add(write, item);
            return write;
        }
        finally
        {
            area.Gate.Release();
        }
    }

    public async Task CompleteMediaAsync(
        PortableStagingId stagingId,
        PortableStagingWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        var area = await EnterAreaAsync(stagingId, FileStagingOperation.CompleteMedia, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            EnsureNotCommitted(area);

            if (!area.MediaByHandle.TryGetValue(write, out var item))
                throw PortableStagingFilePrimitives.NotFound("No media item matches the write handle.");

            if (item.State.Status == PortableStagingItemStatus.Completed)
                return;

            if (item.State.Status != PortableStagingItemStatus.Writing)
                throw PortableStagingFilePrimitives.NotFound("The media write is no longer completable.");

            try
            {
                if (!await PortableStagingSealing.SealAsync(
                        item.State,
                        item.Descriptor.Length,
                        item.Descriptor.Sha256,
                        flushToDisk: true)
                        .ConfigureAwait(false))
                {
                    throw PortableStagingFilePrimitives.IntegrityMismatch(
                        "Staged media bytes do not match the descriptor bound when the write was opened.");
                }

                RunCrashPoint(FileStagingCrashPoint.AfterMediaSealedBeforeRename);
                PublishVerifiedItem(item.State);
                RunCrashPoint(FileStagingCrashPoint.AfterMediaRenamedBeforeInventory);

                area.Inventory.Media.RemoveAll(record =>
                    string.Equals(record.Reference, item.Reference, StringComparison.Ordinal));
                area.Inventory.Media.Add(new FileStagingMediaRecord(
                    item.Reference,
                    item.Descriptor));
                await PersistInventoryAsync(area, crashPoint: null, cancellationToken)
                    .ConfigureAwait(false);
                item.State.Status = PortableStagingItemStatus.Completed;
            }
            catch (FileStagingSimulatedCrashException)
            {
                throw;
            }
            catch
            {
                DiscardMedia(area, item);
                throw;
            }
        }
        finally
        {
            area.Gate.Release();
        }
    }

    public async Task<Stream> OpenMediaReadAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        CancellationToken cancellationToken = default)
    {
        var area = await EnterAreaAsync(stagingId, FileStagingOperation.Read, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var value = PortableStagingFilePrimitives.ValidateReference(reference.Value);
            if (!area.MediaByReference.TryGetValue(value, out var item)
                || item.State.Status != PortableStagingItemStatus.Completed)
            {
                throw PortableStagingFilePrimitives.NotFound(
                    "No completed media item matches the reference.");
            }

            return OpenVerifiedRead(item.State.FinalPath, item.Descriptor.Length);
        }
        finally
        {
            area.Gate.Release();
        }
    }

    public async Task<IReadOnlyList<PortablePreparedMedia>> ListMediaAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        var area = await EnterAreaAsync(stagingId, FileStagingOperation.List, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return DescribeCompletedMedia(area);
        }
        finally
        {
            area.Gate.Release();
        }
    }

    public Task<PortableStagingPayloadWrite> OpenDataWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default) =>
        OpenPayloadWriteAsync(stagingId, descriptor, manifest: false, cancellationToken);

    public Task<PortableStagingPayloadWrite> OpenManifestWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default) =>
        OpenPayloadWriteAsync(stagingId, descriptor, manifest: true, cancellationToken);

    public async Task CompleteDataAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        CancellationToken cancellationToken = default) =>
        await CompletePayloadAsync(stagingId, write, manifest: false, cancellationToken)
            .ConfigureAwait(false);

    public async Task CompleteManifestAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        CancellationToken cancellationToken = default) =>
        await CompletePayloadAsync(stagingId, write, manifest: true, cancellationToken)
            .ConfigureAwait(false);

    public async Task<Stream> OpenDataReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        await OpenPayloadReadAsync(stagingId, manifest: false, cancellationToken)
            .ConfigureAwait(false);

    public async Task<Stream> OpenManifestReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        await OpenPayloadReadAsync(stagingId, manifest: true, cancellationToken)
            .ConfigureAwait(false);

    public async Task CommitPreparedImportAsync(
        PortableStagingId stagingId,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(metadata.Counts);

        var area = await EnterAreaAsync(stagingId, FileStagingOperation.Commit, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (metadata.StagingId != stagingId)
            {
                throw PortableStagingFilePrimitives.Conflict(
                    "The prepared descriptor belongs to a different staging area.");
            }

            if (area.Prepared is not null)
            {
                if (area.Prepared == metadata)
                    return;

                throw PortableStagingFilePrimitives.Conflict(
                    "A different prepared descriptor is already committed.");
            }

            if (area.Data?.State.Status != PortableStagingItemStatus.Completed
                || area.Manifest?.State.Status != PortableStagingItemStatus.Completed)
            {
                throw PortableStagingFilePrimitives.Conflict(
                    "A prepared import requires a completed relational payload and manifest.");
            }

            if (area.MediaByIdentity.Values.Any(
                    item => item.State.Status == PortableStagingItemStatus.Writing))
            {
                throw PortableStagingFilePrimitives.Conflict(
                    "A prepared import cannot commit while a media write is still open.");
            }

            var media = DescribeCompletedMedia(area);
            if (media.Count != metadata.MediaFiles)
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The staged media count does not match the prepared descriptor.");
            }

            if (media.Sum(item => item.Descriptor.Length) != metadata.MediaBytes)
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The staged media bytes do not match the prepared descriptor.");
            }

            if (metadata.Counts.MediaEntries != metadata.MediaFiles)
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The prepared counts media entries do not match the media file count.");
            }

            var (dataBytes, dataSha256) = await HashVerifiedStagedFileAsync(
                    area.Data.State.FinalPath,
                    area.Data.Length,
                    cancellationToken)
                .ConfigureAwait(false);
            if (dataBytes != metadata.DataBytes
                || !PortableArchiveValidation.FixedHashEquals(dataSha256, metadata.DataSha256))
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The staged relational payload does not match the prepared descriptor.");
            }

            // The committed descriptor is written last and atomically: the new
            // state.json only appears after every staged byte was verified, and a
            // failure or crash before the swap leaves the area uncommitted.
            var previous = area.Inventory.Prepared;
            area.Inventory.Prepared = metadata;
            try
            {
                await PersistInventoryAsync(
                        area,
                        FileStagingCrashPoint.BeforeCommitMarkerReplace,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                area.Inventory.Prepared = previous;
                throw;
            }

            area.Prepared = metadata;
        }
        finally
        {
            area.Gate.Release();
        }
    }

    public async Task<IPreparedPortableImport> RebuildPreparedImportAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        var area = await EnterAreaAsync(stagingId, FileStagingOperation.Rebuild, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var prepared = area.Prepared
                ?? throw PortableStagingFilePrimitives.NotFound(
                    "No prepared descriptor has been committed.");

            var media = new List<PortablePreparedMedia>(area.Inventory.Media.Count);
            foreach (var record in area.Inventory.Media)
            {
                if (!TransferPathResolver.IsValidOpaqueToken(record.Reference))
                {
                    throw PortableStagingFilePrimitives.IntegrityMismatch(
                        "The committed media inventory contains an invalid reference.");
                }

                var path = MediaPath(area, record.Reference);
                _resolver.VerifyPathWithinRoot(path);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != record.Descriptor.Length)
                {
                    throw PortableStagingFilePrimitives.IntegrityMismatch(
                        "Staged media no longer matches the committed prepared descriptor.");
                }

                media.Add(new PortablePreparedMedia(
                    record.Descriptor,
                    new PortableStagedMediaReference(record.Reference)));
            }

            media.Sort(CompareMedia);

            if (media.Count != prepared.MediaFiles
                || media.Sum(item => item.Descriptor.Length) != prepared.MediaBytes)
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "Staged media no longer matches the committed prepared descriptor.");
            }

            if (area.Data?.State.Status != PortableStagingItemStatus.Completed)
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The staged relational payload no longer matches the committed prepared descriptor.");
            }

            var (dataBytes, dataSha256) = await HashVerifiedStagedFileAsync(
                    area.Data.State.FinalPath,
                    area.Data.Length,
                    cancellationToken)
                .ConfigureAwait(false);
            if (dataBytes != prepared.DataBytes
                || !PortableArchiveValidation.FixedHashEquals(dataSha256, prepared.DataSha256))
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The staged relational payload no longer matches the committed prepared descriptor.");
            }

            return new PortablePreparedImport(prepared, media);
        }
        finally
        {
            area.Gate.Release();
        }
    }

    public async Task DeleteAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        if (stagingId.Value == Guid.Empty)
            return;

        var area = GetCoordinator(stagingId.Value);
        var beforeGate = _hooks?.BeforeAreaGateAsync;
        if (beforeGate is not null)
        {
            await beforeGate(FileStagingOperation.Delete).ConfigureAwait(false);
        }

        await area.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Prevent new writers first, discard every in-flight write, then remove
            // the entire generated directory. Idempotent for unknown identifiers.
            area.Deleted = true;
            DeactivateAll(area);
        }
        finally
        {
            area.Gate.Release();
        }

        PortableStagingFilePrimitives.TryDeleteDirectory(area.Directory);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<PortableStagingPayloadWrite> OpenPayloadWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        bool manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var area = await EnterAreaAsync(stagingId, FileStagingOperation.OpenPayloadWrite, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            EnsureNotCommitted(area);

            if (descriptor.Length < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(descriptor),
                    "The declared payload length cannot be negative.");
            }

            var hardLimit = manifest
                ? PortableArchiveLimits.MaxManifestBytes
                : PortableArchiveLimits.MaxDataBytes;
            if (descriptor.Length > hardLimit)
            {
                throw new PortableStagingException(
                    PortableStagingException.LimitExceededCode,
                    $"The declared payload length exceeds the {hardLimit}-byte staging limit.");
            }

            if (manifest ? area.Manifest is not null : area.Data is not null)
            {
                throw PortableStagingFilePrimitives.Conflict(
                    "A write for this payload is already open or completed.");
            }

            string finalPath;
            if (manifest)
            {
                finalPath = ManifestPath(area);
            }
            else
            {
                _resolver.EnsureDirectoryExists(DataDirectoryPath(area));
                finalPath = DataPath(area);
            }

            var tempPath = finalPath + ".tmp." + Guid.NewGuid().ToString("N");
            var state = PortableStagingWriteState.Open(
                _resolver.CreateNewVerifiedFile(
                    tempPath,
                    PortableStagingFilePrimitives.StreamBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan),
                tempPath,
                finalPath,
                descriptor.Length);
            var item = new FilePayloadItem
            {
                Length = descriptor.Length,
                Sha256 = descriptor.Sha256,
                State = state,
            };
            var write = new PortableStagingPayloadWrite(
                new PortableStagingWriteStream(
                    area.Gate,
                    state,
                    () => area.Deleted,
                    () => DiscardPayload(area, item, manifest),
                    _hooks?.BeforeStreamWrite));
            item.Handle = write;

            if (manifest)
                area.Manifest = item;
            else
                area.Data = item;

            area.PayloadByHandle.Add(write, item);
            return write;
        }
        finally
        {
            area.Gate.Release();
        }
    }

    private async Task CompletePayloadAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        bool manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        var area = await EnterAreaAsync(stagingId, FileStagingOperation.CompletePayload, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            EnsureNotCommitted(area);

            var item = manifest ? area.Manifest : area.Data;
            if (item is null
                || !area.PayloadByHandle.TryGetValue(write, out var bound)
                || !ReferenceEquals(bound, item))
            {
                throw PortableStagingFilePrimitives.NotFound("No payload matches the write handle.");
            }

            if (item.State.Status == PortableStagingItemStatus.Completed)
                return;

            if (item.State.Status != PortableStagingItemStatus.Writing)
                throw PortableStagingFilePrimitives.NotFound("The payload write is no longer completable.");

            try
            {
                if (!await PortableStagingSealing.SealAsync(
                        item.State,
                        item.Length,
                        item.Sha256,
                        flushToDisk: true)
                        .ConfigureAwait(false))
                {
                    throw PortableStagingFilePrimitives.IntegrityMismatch(
                        "Staged payload bytes do not match the descriptor bound when the write was opened.");
                }

                RunCrashPoint(FileStagingCrashPoint.AfterPayloadSealedBeforeRename);
                PublishVerifiedItem(item.State);
                RunCrashPoint(FileStagingCrashPoint.AfterPayloadRenamedBeforeInventory);

                if (manifest)
                {
                    area.Inventory.Manifest = new FileStagingPayloadRecord(
                        item.Length,
                        item.Sha256);
                }
                else
                {
                    area.Inventory.Data = new FileStagingPayloadRecord(
                        item.Length,
                        item.Sha256);
                }

                await PersistInventoryAsync(area, crashPoint: null, cancellationToken)
                    .ConfigureAwait(false);
                item.State.Status = PortableStagingItemStatus.Completed;
            }
            catch (FileStagingSimulatedCrashException)
            {
                throw;
            }
            catch
            {
                DiscardPayload(area, item, manifest);
                throw;
            }
        }
        finally
        {
            area.Gate.Release();
        }
    }

    private async Task<Stream> OpenPayloadReadAsync(
        PortableStagingId stagingId,
        bool manifest,
        CancellationToken cancellationToken)
    {
        var area = await EnterAreaAsync(stagingId, FileStagingOperation.Read, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var item = manifest ? area.Manifest : area.Data;
            if (item?.State.Status != PortableStagingItemStatus.Completed)
                throw PortableStagingFilePrimitives.NotFound("No completed payload is available.");

            return OpenVerifiedRead(item.State.FinalPath, item.Length);
        }
        finally
        {
            area.Gate.Release();
        }
    }

    private async Task<FileStagingCoordinator> EnterAreaAsync(
        PortableStagingId stagingId,
        FileStagingOperation operation,
        CancellationToken cancellationToken)
    {
        var area = GetCoordinator(stagingId.Value);
        var beforeGate = _hooks?.BeforeAreaGateAsync;
        if (beforeGate is not null)
        {
            await beforeGate(operation).ConfigureAwait(false);
        }

        await area.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (area.Deleted || !Directory.Exists(area.Directory))
                throw PortableStagingFilePrimitives.NotFound("Unknown staging area.");

            await LoadInventoryAsync(area, cancellationToken).ConfigureAwait(false);
            return area;
        }
        catch
        {
            area.Gate.Release();
            throw;
        }
    }

    private FileStagingCoordinator GetCoordinator(Guid stagingId)
    {
        if (stagingId == Guid.Empty)
            throw PortableStagingFilePrimitives.NotFound("Unknown staging area.");

        var directory = _resolver.GetStagingDirectory(stagingId);
        var key = _coordinatorScope.Length == 0
            ? directory
            : _coordinatorScope + "\n" + directory;
        return Coordinators.GetOrAdd(
            key,
            _ => new FileStagingCoordinator(stagingId, directory));
    }

    private async Task LoadInventoryAsync(
        FileStagingCoordinator area,
        CancellationToken cancellationToken)
    {
        if (area.InventoryLoaded)
            return;

        var statePath = StatePath(area);
        _resolver.VerifyPathWithinRoot(statePath);
        if (!File.Exists(statePath))
            throw PortableStagingFilePrimitives.NotFound("Unknown staging area.");

        FileStagingInventory inventory;
        try
        {
            await using var stream = new FileStream(
                statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                PortableStagingFilePrimitives.StateBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            inventory = await JsonSerializer
                .DeserializeAsync<FileStagingInventory>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new JsonException("The staging state file is empty.");
        }
        catch (JsonException exception)
        {
            throw new PortableStagingException(
                PortableStagingException.IntegrityMismatchCode,
                "The staging state file is malformed.",
                exception);
        }

        var references = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<PortableStagingMediaIdentity>();
        foreach (var record in inventory.Media)
        {
            if (!TransferPathResolver.IsValidOpaqueToken(record.Reference))
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The staging inventory contains an invalid media reference.");
            }

            if (!references.Add(record.Reference))
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The staging inventory contains a duplicate media reference.");
            }

            if (!identities.Add(new PortableStagingMediaIdentity(
                    record.Descriptor.BookId,
                    record.Descriptor.Kind,
                    record.Descriptor.Path)))
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The staging inventory contains a duplicate media item.");
            }
        }

        area.Inventory = inventory;
        area.InventoryLoaded = true;
        area.Prepared = inventory.Prepared;

        if (inventory.Data is { } data)
        {
            var path = DataPath(area);
            _resolver.VerifyPathWithinRoot(path);
            if (File.Exists(path))
            {
                area.Data = new FilePayloadItem
                {
                    Length = data.Length,
                    Sha256 = data.Sha256,
                    State = PortableStagingWriteState.Completed(
                        path,
                        new FileInfo(path).Length),
                };
            }
        }

        if (inventory.Manifest is { } manifest)
        {
            var path = ManifestPath(area);
            _resolver.VerifyPathWithinRoot(path);
            if (File.Exists(path))
            {
                area.Manifest = new FilePayloadItem
                {
                    Length = manifest.Length,
                    Sha256 = manifest.Sha256,
                    State = PortableStagingWriteState.Completed(
                        path,
                        new FileInfo(path).Length),
                };
            }
        }

        foreach (var record in inventory.Media)
        {
            var path = MediaPath(area, record.Reference);
            _resolver.VerifyPathWithinRoot(path);
            if (!File.Exists(path))
                continue;

            var item = new FileMediaItem
            {
                Descriptor = record.Descriptor,
                Reference = record.Reference,
                State = PortableStagingWriteState.Completed(
                    path,
                    new FileInfo(path).Length),
            };
            area.MediaByIdentity[new PortableStagingMediaIdentity(
                record.Descriptor.BookId,
                record.Descriptor.Kind,
                record.Descriptor.Path)] = item;
            area.MediaByReference[record.Reference] = item;
        }
    }

    private async Task PersistInventoryAsync(
        FileStagingCoordinator area,
        FileStagingCrashPoint? crashPoint,
        CancellationToken cancellationToken)
    {
        var statePath = StatePath(area);
        var tempPath = StateTempPath(area);

        // A crash can leave state.json.tmp behind. It is never read and is always
        // replaced by a complete copy-on-write inventory, so remove it first.
        PortableStagingFilePrimitives.TryDeleteFile(tempPath);

        await using (var stream = _resolver.CreateNewVerifiedFile(
            tempPath,
            PortableStagingFilePrimitives.StateBufferBytes,
            FileOptions.Asynchronous))
        {
            await JsonSerializer
                .SerializeAsync(stream, area.Inventory, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (crashPoint is { } point)
            RunCrashPoint(point);

        _resolver.EnsureFileIsNotReparsePoint(statePath);
        File.Move(tempPath, statePath, overwrite: true);
        _resolver.VerifyPathWithinRoot(statePath);
    }

    private void PublishVerifiedItem(PortableStagingWriteState state)
    {
        _resolver.VerifyPathWithinRoot(state.TempPath);
        _resolver.EnsureFileIsNotReparsePoint(state.FinalPath);
        _resolver.EnsureParentDirectoryExists(state.FinalPath);
        File.Move(state.TempPath, state.FinalPath, overwrite: true);
        _resolver.VerifyPathWithinRoot(state.FinalPath);
    }

    private Stream OpenVerifiedRead(string path, long expectedLength)
    {
        var full = _resolver.VerifyPathWithinRoot(path);
        if (!File.Exists(full))
        {
            throw PortableStagingFilePrimitives.IntegrityMismatch(
                "The staged item bytes are missing.");
        }

        if (new FileInfo(full).Length != expectedLength)
        {
            throw PortableStagingFilePrimitives.IntegrityMismatch(
                "The staged item length no longer matches the completed descriptor.");
        }

        var stream = new FileStream(
            full,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            PortableStagingFilePrimitives.StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            _resolver.VerifyPathWithinRoot(full);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private async Task<(long Length, string Sha256)> HashVerifiedStagedFileAsync(
        string path,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        _resolver.VerifyPathWithinRoot(path);
        if (!File.Exists(path) || new FileInfo(path).Length != expectedLength)
        {
            throw PortableStagingFilePrimitives.IntegrityMismatch(
                "The staged payload no longer matches the committed prepared descriptor.");
        }

        return await PortableStagingFilePrimitives
            .HashFileAsync(path, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<PortablePreparedMedia> DescribeCompletedMedia(
        FileStagingCoordinator area)
    {
        var media = area.MediaByReference.Values
            .Where(item => item.State.Status == PortableStagingItemStatus.Completed
                && File.Exists(item.State.FinalPath))
            .Select(item => new PortablePreparedMedia(
                item.Descriptor,
                new PortableStagedMediaReference(item.Reference)))
            .ToList();
        media.Sort(CompareMedia);
        return media;
    }

    private static int CompareMedia(PortablePreparedMedia left, PortablePreparedMedia right)
    {
        var byBook = left.Descriptor.BookId.CompareTo(right.Descriptor.BookId);
        if (byBook != 0)
            return byBook;

        var byKind = string.Compare(left.Descriptor.Kind, right.Descriptor.Kind, StringComparison.Ordinal);
        if (byKind != 0)
            return byKind;

        return string.Compare(left.Descriptor.Path, right.Descriptor.Path, StringComparison.Ordinal);
    }

    private static void DiscardMedia(FileStagingCoordinator area, FileMediaItem item)
    {
        if (item.State.Status == PortableStagingItemStatus.Discarded)
            return;

        item.State.Status = PortableStagingItemStatus.Discarded;
        item.State.DisposeWriter();
        PortableStagingFilePrimitives.TryDeleteFile(item.State.TempPath);
        PortableStagingFilePrimitives.TryDeleteFile(item.State.FinalPath);
        area.MediaByIdentity.Remove(new PortableStagingMediaIdentity(
            item.Descriptor.BookId,
            item.Descriptor.Kind,
            item.Descriptor.Path));
        area.MediaByReference.Remove(item.Reference);
        if (item.Handle is not null)
            area.MediaByHandle.Remove(item.Handle);
    }

    private static void DiscardPayload(
        FileStagingCoordinator area,
        FilePayloadItem item,
        bool manifest)
    {
        if (item.State.Status == PortableStagingItemStatus.Discarded)
            return;

        item.State.Status = PortableStagingItemStatus.Discarded;
        item.State.DisposeWriter();
        PortableStagingFilePrimitives.TryDeleteFile(item.State.TempPath);
        PortableStagingFilePrimitives.TryDeleteFile(item.State.FinalPath);

        if (manifest)
        {
            if (ReferenceEquals(area.Manifest, item))
                area.Manifest = null;
        }
        else if (ReferenceEquals(area.Data, item))
        {
            area.Data = null;
        }

        if (item.Handle is not null)
            area.PayloadByHandle.Remove(item.Handle);
    }

    private static void DeactivateAll(FileStagingCoordinator area)
    {
        foreach (var item in area.MediaByReference.Values.ToArray())
        {
            item.State.Status = PortableStagingItemStatus.Discarded;
            item.State.DisposeWriter();
        }

        if (area.Data is not null)
        {
            area.Data.State.Status = PortableStagingItemStatus.Discarded;
            area.Data.State.DisposeWriter();
        }

        if (area.Manifest is not null)
        {
            area.Manifest.State.Status = PortableStagingItemStatus.Discarded;
            area.Manifest.State.DisposeWriter();
        }

        area.MediaByIdentity.Clear();
        area.MediaByReference.Clear();
        area.MediaByHandle.Clear();
        area.PayloadByHandle.Clear();
        area.Data = null;
        area.Manifest = null;
        area.Prepared = null;
        area.Inventory = new FileStagingInventory();
        area.InventoryLoaded = true;
    }

    private static void EnsureNotCommitted(FileStagingCoordinator area)
    {
        if (area.Prepared is not null)
        {
            throw new PortableStagingException(
                PortableStagingException.AlreadyCommittedCode,
                "The staging area has already been committed and accepts no further writes.");
        }
    }

    private void RunCrashPoint(FileStagingCrashPoint point) =>
        _hooks?.CrashAt?.Invoke(point);

    private string StatePath(FileStagingCoordinator area) =>
        Path.Combine(area.Directory, StateFileName);

    private string StateTempPath(FileStagingCoordinator area) =>
        Path.Combine(area.Directory, StateTempFileName);

    private string MediaDirectoryPath(FileStagingCoordinator area) =>
        _resolver.GetStagingMediaDirectory(area.StagingId);

    private string MediaPath(FileStagingCoordinator area, string reference) =>
        _resolver.GetStagingMediaPath(area.StagingId, reference);

    private string DataDirectoryPath(FileStagingCoordinator area) =>
        Path.Combine(area.Directory, DataDirectoryName);

    private string DataPath(FileStagingCoordinator area) =>
        Path.Combine(DataDirectoryPath(area), DataFileName);

    private string ManifestPath(FileStagingCoordinator area) =>
        Path.Combine(area.Directory, ManifestFileName);
}
