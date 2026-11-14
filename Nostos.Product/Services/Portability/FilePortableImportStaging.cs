using System.Collections.Concurrent;
using System.Text.Json;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability;

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
/// barriers, crash stops, and fault injection. Production instances pass none of
/// them.
/// </summary>
internal sealed class FilePortableImportStagingHooks
{
    internal Action<FileStagingCrashPoint>? CrashAt { get; set; }

    internal Func<PortableStagingOperation, Task>? BeforeAreaGateAsync { get; set; }

    internal Action? BeforeStreamWrite { get; set; }

    /// <summary>
    /// Runs inside the staging machine after the new <c>state.json.tmp</c> is flushed
    /// and before it replaces <c>state.json</c>. Throwing models an I/O failure of the
    /// inventory replacement.
    /// </summary>
    internal Action? BeforeInventoryReplace { get; set; }

    /// <summary>
    /// Runs under the area gate, immediately before the durable deletion marker is
    /// written. Throwing models a failure to write the mark.
    /// </summary>
    internal Action? BeforeDeleteMark { get; set; }

    /// <summary>
    /// Replaces the best-effort recursive cleanup of a durably marked deleted
    /// area. Throwing models a filesystem deletion failure after the mark.
    /// </summary>
    internal Action<string>? CleanupTombstone { get; set; }
}

/// <summary>
/// Durable on-disk inventory for one staging area (<c>state.json</c>): the
/// completed payload identities, every completed media record with its generated
/// reference, and the committed prepared descriptor when one exists.
/// </summary>
internal sealed class FileStagingInventory
{
    /// <summary>
    /// Schema version; the initializer is deliberately zero so a state file that
    /// omits the version field is rejected by validation rather than silently
    /// treated as an empty area. Writers always set the current version.
    /// </summary>
    public int Version { get; set; }

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
internal sealed class FileStagingCoordinator : PortableStagingArea
{
    internal FileStagingCoordinator(Guid stagingId, string directory)
        : base(stagingId, directory, new PortableStagingGate())
    {
    }

    internal bool InventoryLoaded { get; set; }

    internal FileStagingInventory Inventory { get; set; } = new();
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
/// <para><b>One state machine.</b> The open/complete/commit/rebuild/delete
/// transitions live in <see cref="PortableImportStagingBase"/> and are shared with
/// the process-local provider; this type adds only the durable inventory,
/// copy-on-write publication, deletion tombstone, and path defense.</para>
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

    /// <summary>
    /// Durable deletion tombstone: its existence inside a staging directory means
    /// the staging id is deleted forever, even if best-effort cleanup failed.
    /// </summary>
    public const string DeletedMarkerFileName = "deleted";

    /// <summary>Inventory schema version written to every <c>state.json</c>.</summary>
    public const int CurrentInventoryVersion = 1;

    private readonly FilePortableImportStagingCore _core;

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
        _core = new FilePortableImportStagingCore(resolver, hooks, coordinatorScope);
    }

    public Task<PortableStagingId> CreateAsync(
        CancellationToken cancellationToken = default) =>
        _core.CreateAsync(cancellationToken);

    public Task<PortableStagingWrite> OpenMediaWriteAsync(
        PortableStagingId stagingId,
        PortableArchiveMediaEntry descriptor,
        CancellationToken cancellationToken = default) =>
        _core.OpenMediaWriteAsync(stagingId, descriptor, cancellationToken);

    public Task CompleteMediaAsync(
        PortableStagingId stagingId,
        PortableStagingWrite write,
        CancellationToken cancellationToken = default) =>
        _core.CompleteMediaAsync(stagingId, write, cancellationToken);

    public Task<Stream> OpenMediaReadAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        CancellationToken cancellationToken = default) =>
        _core.OpenMediaReadAsync(stagingId, reference, cancellationToken);

    public Task<IReadOnlyList<PortablePreparedMedia>> ListMediaAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        _core.ListMediaAsync(stagingId, cancellationToken);

    public Task<PortableStagingPayloadWrite> OpenDataWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default) =>
        _core.OpenDataWriteAsync(stagingId, descriptor, cancellationToken);

    public Task CompleteDataAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        CancellationToken cancellationToken = default) =>
        _core.CompleteDataAsync(stagingId, write, cancellationToken);

    public Task<Stream> OpenDataReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        _core.OpenDataReadAsync(stagingId, cancellationToken);

    public Task<PortableStagingPayloadWrite> OpenManifestWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default) =>
        _core.OpenManifestWriteAsync(stagingId, descriptor, cancellationToken);

    public Task CompleteManifestAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        CancellationToken cancellationToken = default) =>
        _core.CompleteManifestAsync(stagingId, write, cancellationToken);

    public Task<Stream> OpenManifestReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        _core.OpenManifestReadAsync(stagingId, cancellationToken);

    public Task CommitPreparedImportAsync(
        PortableStagingId stagingId,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken = default) =>
        _core.CommitPreparedImportAsync(stagingId, metadata, cancellationToken);

    public Task<IPreparedPortableImport> RebuildPreparedImportAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        _core.RebuildPreparedImportAsync(stagingId, cancellationToken);

    public Task DeleteAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        _core.DeleteAsync(stagingId, cancellationToken);

    public ValueTask DisposeAsync() => _core.DisposeAsync();
}

/// <summary>
/// The durable provider's persistence half of the shared staging state machine: the
/// copy-on-write inventory, the atomic publication of verified items, the deletion
/// tombstone, and every path that touches the transfer root through
/// <see cref="TransferPathResolver"/>.
/// </summary>
internal sealed class FilePortableImportStagingCore : PortableImportStagingBase
{
    private static readonly ConcurrentDictionary<string, FileStagingCoordinator> Coordinators =
        new(StringComparer.Ordinal);

    private readonly TransferPathResolver _resolver;
    private readonly FilePortableImportStagingHooks? _hooks;
    private readonly string _coordinatorScope;

    internal FilePortableImportStagingCore(
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

    protected override Func<PortableStagingOperation, Task>? BeforeAreaGate =>
        _hooks?.BeforeAreaGateAsync;

    protected override Action? BeforeStreamWrite => _hooks?.BeforeStreamWrite;

    protected override bool FlushCompletedItemsToDisk => true;

    protected override bool MustNotDiscardOnFailure(Exception exception) =>
        exception is FileStagingSimulatedCrashException;

    public override async Task<PortableStagingId> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var id = Guid.NewGuid();
            var area = GetCoordinator(id);
            if (area.Deleted || area.InventoryLoaded || Directory.Exists(area.Directory))
                continue;

            using var lease = await area.Gate.EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(area.Directory))
                    continue;

                _resolver.EnsureDirectoryExists(area.Directory);
                _resolver.EnsureDirectoryExists(MediaDirectoryPath(area));
                var inventory = new FileStagingInventory
                {
                    Version = FilePortableImportStaging.CurrentInventoryVersion,
                };
                await PersistInventoryAsync(area, inventory, crashPoint: null, cancellationToken)
                    .ConfigureAwait(false);
                area.Inventory = inventory;
                area.InventoryLoaded = true;
                return new PortableStagingId(id);
            }
            catch
            {
                PortableStagingFilePrimitives.TryDeleteDirectory(area.Directory);
                throw;
            }
        }

        throw PortableStagingFilePrimitives.Conflict(
            "Could not allocate a fresh staging identifier.");
    }

    protected override async ValueTask<PortableStagingAreaLease> EnterAreaAsync(
        PortableStagingId stagingId,
        PortableStagingOperation operation,
        CancellationToken cancellationToken)
    {
        var area = GetCoordinator(stagingId.Value);
        var lease = await EnterGateAsync(area.Gate, operation, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (area.Deleted
                || !Directory.Exists(area.Directory)
                || File.Exists(DeletedMarkerPath(area)))
            {
                throw PortableStagingFilePrimitives.NotFound("Unknown staging area.");
            }

            await LoadInventoryAsync(area, cancellationToken).ConfigureAwait(false);
            return new PortableStagingAreaLease(area, lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    protected override async ValueTask<PortableStagingAreaLease> EnterAreaForDeleteAsync(
        PortableStagingId stagingId,
        PortableStagingOperation operation,
        CancellationToken cancellationToken)
    {
        var area = GetCoordinator(stagingId.Value);
        var lease = await EnterGateAsync(area.Gate, operation, cancellationToken)
            .ConfigureAwait(false);
        return new PortableStagingAreaLease(area, lease);
    }

    protected override FileStream CreateScratchFile(string finalPath, out string tempPath)
    {
        tempPath = finalPath + ".tmp." + Guid.NewGuid().ToString("N");
        return _resolver.CreateNewVerifiedFile(
            tempPath,
            PortableStagingFilePrimitives.StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    protected override string MediaPath(PortableStagingArea area, string reference) =>
        _resolver.GetStagingMediaPath(area.StagingId, reference);

    protected override string PayloadPath(PortableStagingArea area, bool manifest)
    {
        if (manifest)
            return ManifestPath(area);

        _resolver.EnsureDirectoryExists(DataDirectoryPath(area));
        return DataPath(area);
    }

    protected override void EnsureMediaDirectory(PortableStagingArea area) =>
        _resolver.EnsureDirectoryExists(MediaDirectoryPath(area));

    protected override async ValueTask PublishMediaAsync(
        PortableStagingArea area,
        PortableStagingMediaItem item,
        CancellationToken cancellationToken)
    {
        var coordinator = AsCoordinator(area);

        RunCrashPoint(FileStagingCrashPoint.AfterMediaSealedBeforeRename);
        PublishVerifiedItem(item.State);
        RunCrashPoint(FileStagingCrashPoint.AfterMediaRenamedBeforeInventory);

        // Durable state is authoritative: persist a complete snapshot of the
        // inventory with this item added, and install it in memory only after the
        // replacement succeeded. A failed persist leaves the in-memory view exactly
        // as it is on disk.
        var updated = CloneInventory(coordinator.Inventory);
        updated.Media.RemoveAll(record =>
            string.Equals(record.Reference, item.Reference, StringComparison.Ordinal));
        updated.Media.Add(new FileStagingMediaRecord(item.Reference, item.Descriptor));
        await PersistInventoryAsync(coordinator, updated, crashPoint: null, cancellationToken)
            .ConfigureAwait(false);
        coordinator.Inventory = updated;
    }

    protected override async ValueTask PublishPayloadAsync(
        PortableStagingArea area,
        PortableStagingPayloadItem item,
        bool manifest,
        CancellationToken cancellationToken)
    {
        var coordinator = AsCoordinator(area);

        RunCrashPoint(FileStagingCrashPoint.AfterPayloadSealedBeforeRename);
        PublishVerifiedItem(item.State);
        RunCrashPoint(FileStagingCrashPoint.AfterPayloadRenamedBeforeInventory);

        // Persist a snapshot with this payload record and install it in memory only
        // after the durable replacement succeeded, so a failed persist can never
        // leave the runtime inventory ahead of disk.
        var updated = CloneInventory(coordinator.Inventory);
        var record = new FileStagingPayloadRecord(item.Length, item.Sha256);
        if (manifest)
        {
            updated.Manifest = record;
        }
        else
        {
            updated.Data = record;
        }

        await PersistInventoryAsync(coordinator, updated, crashPoint: null, cancellationToken)
            .ConfigureAwait(false);
        coordinator.Inventory = updated;
    }

    protected override async ValueTask PublishPreparedAsync(
        PortableStagingArea area,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken)
    {
        var coordinator = AsCoordinator(area);

        // The committed descriptor is written last and atomically: the new
        // state.json only appears after every staged byte was verified, and a
        // failure or crash before the swap leaves the area uncommitted. The
        // in-memory inventory is replaced only after the durable write succeeds.
        var updated = CloneInventory(coordinator.Inventory);
        updated.Prepared = metadata;
        await PersistInventoryAsync(
                coordinator,
                updated,
                FileStagingCrashPoint.BeforeCommitMarkerReplace,
                cancellationToken)
            .ConfigureAwait(false);
        coordinator.Inventory = updated;
    }

    protected override IReadOnlyList<PortablePreparedMedia> DescribeCompletedMedia(
        PortableStagingArea area) =>
        area.MediaByReference.Values
            .Where(item => item.State.Status == PortableStagingItemStatus.Completed
                && File.Exists(item.State.FinalPath))
            .Select(item => new PortablePreparedMedia(
                item.Descriptor,
                new PortableStagedMediaReference(item.Reference)))
            .ToArray();

    protected override ValueTask<IReadOnlyList<PortablePreparedMedia>>
        DescribeCompletedMediaForRebuildAsync(
            PortableStagingArea area,
            CancellationToken cancellationToken)
    {
        var coordinator = AsCoordinator(area);
        var media = new List<PortablePreparedMedia>(coordinator.Inventory.Media.Count);
        foreach (var record in coordinator.Inventory.Media)
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

        return ValueTask.FromResult<IReadOnlyList<PortablePreparedMedia>>(media);
    }

    protected override async ValueTask<(long Length, string Sha256)> HashCompletedPayloadAsync(
        PortableStagingArea area,
        PortableStagingPayloadItem item,
        CancellationToken cancellationToken) =>
        await HashVerifiedStagedFileAsync(item.State.FinalPath, item.Length, cancellationToken)
            .ConfigureAwait(false);

    protected override Stream OpenCompletedMediaRead(
        PortableStagingArea area,
        string reference)
    {
        if (!area.MediaByReference.TryGetValue(reference, out var item)
            || item.State.Status != PortableStagingItemStatus.Completed)
        {
            throw PortableStagingFilePrimitives.NotFound(
                "No completed media item matches the reference.");
        }

        return OpenVerifiedRead(item.State.FinalPath, item.Descriptor.Length);
    }

    protected override Stream OpenCompletedPayloadRead(
        PortableStagingArea area,
        PortableStagingPayloadItem item,
        bool manifest) =>
        OpenVerifiedRead(item.State.FinalPath, item.Length);

    protected override async ValueTask MarkDeletedAsync(
        PortableStagingArea area,
        CancellationToken cancellationToken)
    {
        var coordinator = AsCoordinator(area);
        if (area.Deleted || !Directory.Exists(area.Directory))
            return;

        // Durable first: write the deletion tombstone atomically. From the moment
        // the marker exists, every operation and every fresh instance treats the
        // staging id as not found, even if physical cleanup fails. A failed marker
        // write leaves the area untouched.
        if (File.Exists(DeletedMarkerPath(coordinator)))
            return;

        _hooks?.BeforeDeleteMark?.Invoke();
        await WriteDeletedMarkerAsync(coordinator, cancellationToken).ConfigureAwait(false);
    }

    protected override void CleanupDeletedArea(PortableStagingArea area)
    {
        var cleanup = _hooks?.CleanupTombstone;
        if (cleanup is not null)
        {
            try
            {
                cleanup(area.Directory);
            }
            catch
            {
                // Test seam for a physical cleanup failure after the durable mark.
            }

            return;
        }

        PortableStagingFilePrimitives.TryDeleteDirectory(area.Directory);
    }

    protected override void OnAreaDeactivated(PortableStagingArea area)
    {
        var coordinator = AsCoordinator(area);
        coordinator.Inventory = new FileStagingInventory
        {
            Version = FilePortableImportStaging.CurrentInventoryVersion,
        };
        coordinator.InventoryLoaded = true;
    }

    private static FileStagingCoordinator AsCoordinator(PortableStagingArea area) =>
        (FileStagingCoordinator)area;

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

        if (File.Exists(DeletedMarkerPath(area)))
            throw PortableStagingFilePrimitives.NotFound("Unknown staging area.");

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
                .DeserializeAsync<FileStagingInventory>(stream, PortableArchiveJson.StagingOptions, cancellationToken)
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

        ValidateInventory(inventory, area.StagingId);

        area.Inventory = inventory;
        area.InventoryLoaded = true;
        area.Prepared = inventory.Prepared;

        if (inventory.Data is { } data)
        {
            var path = DataPath(area);
            _resolver.VerifyPathWithinRoot(path);
            if (File.Exists(path))
            {
                area.Data = new PortableStagingPayloadItem
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
                area.Manifest = new PortableStagingPayloadItem
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

            var item = new PortableStagingMediaItem
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
        FileStagingInventory inventory,
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
                .SerializeAsync(stream, inventory, PortableArchiveJson.StagingOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (crashPoint is { } point)
            RunCrashPoint(point);

        _hooks?.BeforeInventoryReplace?.Invoke();

        // The move is the durable boundary; nothing may fail after it, or a
        // reported failure could leave the on-disk inventory ahead of memory.
        _resolver.EnsureFileIsNotReparsePoint(statePath);
        File.Move(tempPath, statePath, overwrite: true);
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

        return OpenVerifiedReadStream(full);
    }

    /// <summary>
    /// Opens an existing staged file for read and re-runs the resolver's component
    /// check immediately after the open, per the resolver's reopen rule.
    /// </summary>
    private FileStream OpenVerifiedReadStream(string fullPath)
    {
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            PortableStagingFilePrimitives.StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            _resolver.VerifyPathWithinRoot(fullPath);
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
        var full = _resolver.VerifyPathWithinRoot(path);
        if (!File.Exists(full) || new FileInfo(full).Length != expectedLength)
        {
            throw PortableStagingFilePrimitives.IntegrityMismatch(
                "The staged payload no longer matches the committed prepared descriptor.");
        }

        await using var stream = OpenVerifiedReadStream(full);
        return await PortableArchiveHashing
            .HashStreamAsync(stream, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the inventory so mutations are persisted as a complete snapshot and
    /// installed in memory only after the durable replacement succeeded.
    /// </summary>
    private static FileStagingInventory CloneInventory(FileStagingInventory source) =>
        new()
        {
            Version = source.Version,
            Data = source.Data,
            Manifest = source.Manifest,
            Prepared = source.Prepared,
            Media = new List<FileStagingMediaRecord>(source.Media),
        };

    /// <summary>
    /// Rejects every structurally invalid or unsupported state file as a typed
    /// integrity failure; a state file is never treated as an empty area.
    /// </summary>
    private static void ValidateInventory(FileStagingInventory inventory, Guid stagingId)
    {
        if (inventory.Version != FilePortableImportStaging.CurrentInventoryVersion)
        {
            throw PortableStagingFilePrimitives.IntegrityMismatch(
                $"The staging state file schema version '{inventory.Version}' is not supported.");
        }

        if (inventory.Media is null)
        {
            throw PortableStagingFilePrimitives.IntegrityMismatch(
                "The staging state file has no media inventory.");
        }

        ValidatePayloadRecord(inventory.Data, "relational payload");
        ValidatePayloadRecord(inventory.Manifest, "manifest");

        var references = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<PortableStagingMediaIdentity>();
        foreach (var record in inventory.Media)
        {
            if (record is null || record.Descriptor is null)
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The staging state file contains a null media record.");
            }

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

        if (inventory.Prepared is { } prepared)
        {
            if (prepared.StagingId.Value != stagingId)
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The committed prepared descriptor belongs to a different staging area.");
            }

            if (prepared.Counts is null
                || !PortableStagingFilePrimitives.IsSha256Hex(prepared.DataSha256)
                || prepared.DataBytes < 0
                || prepared.MediaFiles < 0
                || prepared.MediaBytes < 0
                || prepared.ArchiveBytes < 0)
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "The committed prepared descriptor is malformed.");
            }
        }
    }

    private static void ValidatePayloadRecord(FileStagingPayloadRecord? record, string name)
    {
        if (record is null)
            return;

        if (record.Length < 0 || !PortableStagingFilePrimitives.IsSha256Hex(record.Sha256))
        {
            throw PortableStagingFilePrimitives.IntegrityMismatch(
                $"The staging state file has a malformed {name} record.");
        }
    }

    /// <summary>
    /// Writes the durable deletion tombstone atomically: a flushed scratch file
    /// atomically renamed to <c>deleted</c> inside the area. A failure leaves the
    /// area exactly as it was, and the marker's existence permanently hides the
    /// canonical staging id even if physical cleanup later fails.
    /// </summary>
    private async Task WriteDeletedMarkerAsync(
        FileStagingCoordinator area,
        CancellationToken cancellationToken)
    {
        var markerPath = DeletedMarkerPath(area);
        var tempPath = markerPath + ".tmp";
        PortableStagingFilePrimitives.TryDeleteFile(tempPath);
        try
        {
            await using (var stream = _resolver.CreateNewVerifiedFile(
                tempPath,
                PortableStagingFilePrimitives.StateBufferBytes,
                FileOptions.Asynchronous))
            {
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            _resolver.EnsureFileIsNotReparsePoint(markerPath);
            File.Move(tempPath, markerPath, overwrite: true);
        }
        catch
        {
            PortableStagingFilePrimitives.TryDeleteFile(tempPath);
            throw;
        }
    }

    private void RunCrashPoint(FileStagingCrashPoint point) =>
        _hooks?.CrashAt?.Invoke(point);

    private string DeletedMarkerPath(FileStagingCoordinator area) =>
        Path.Combine(area.Directory, FilePortableImportStaging.DeletedMarkerFileName);

    private string StatePath(FileStagingCoordinator area) =>
        Path.Combine(area.Directory, FilePortableImportStaging.StateFileName);

    private string StateTempPath(FileStagingCoordinator area) =>
        Path.Combine(area.Directory, FilePortableImportStaging.StateTempFileName);

    private string MediaDirectoryPath(PortableStagingArea area) =>
        _resolver.GetStagingMediaDirectory(area.StagingId);

    private string DataDirectoryPath(PortableStagingArea area) =>
        Path.Combine(area.Directory, FilePortableImportStaging.DataDirectoryName);

    private string DataPath(PortableStagingArea area) =>
        Path.Combine(DataDirectoryPath(area), FilePortableImportStaging.DataFileName);

    private string ManifestPath(PortableStagingArea area) =>
        Path.Combine(area.Directory, FilePortableImportStaging.ManifestFileName);
}
