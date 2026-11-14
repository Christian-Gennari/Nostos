namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Names one provider operation so interleaving tests can pause a specific
/// transition before it takes the staging gate. Never used in production.
/// </summary>
internal enum PortableStagingOperation
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
/// The one staging-area wait every provider and the staging write stream take: a
/// binary semaphore handed out as a lease, so acquisition order, cancellation and the
/// release guarantee live in one place instead of one try/finally per call site.
/// </summary>
/// <remarks>
/// The gate has no timeout of its own. A wait either takes the gate or observes the
/// caller's cancellation token, which surfaces as
/// <see cref="OperationCanceledException"/>; the typed staging refusals
/// (<see cref="PortableStagingException.NotFoundCode"/> and the rest) belong to the
/// transition that runs *inside* the gate, so a queued operation never observes
/// another operation's refusal. A test hook may pause an operation before it enters;
/// while paused it holds nothing, so the gate is free for the operations behind it.
/// </remarks>
internal sealed class PortableStagingGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    internal async ValueTask<PortableStagingGateLease> EnterAsync(
        CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new PortableStagingGateLease(this);
    }

    /// <summary>
    /// Synchronous entry for the synchronous <see cref="Stream"/> overrides of
    /// <see cref="PortableStagingWriteStream"/>, which cannot await. Every async path
    /// uses <see cref="EnterAsync"/>; staging callers write and dispose through the
    /// async members only.
    /// </summary>
    internal PortableStagingGateLease EnterBlocking()
    {
        _semaphore.Wait();
        return new PortableStagingGateLease(this);
    }

    internal void Release() => _semaphore.Release();
}

/// <summary>
/// One held staging gate. Disposing it releases the gate exactly once, so a transition
/// cannot double-release or leak the gate on an exception path.
/// </summary>
internal sealed class PortableStagingGateLease : IDisposable, IAsyncDisposable
{
    private PortableStagingGate? _gate;

    internal PortableStagingGateLease(PortableStagingGate gate) => _gate = gate;

    public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One staging area's live state: the gate every operation takes, the media and
/// payload registries, and the committed prepared descriptor when one exists.
/// </summary>
/// <remarks>
/// The registries are only ever touched while the area's gate is held, which is what
/// lets a write handle, a media reference and a descriptor observation agree with each
/// other. A durable provider can subclass this with its own persisted inventory; the
/// state machine below stays provider-neutral.
/// </remarks>
internal class PortableStagingArea
{
    internal PortableStagingArea(Guid stagingId, string directory, PortableStagingGate gate)
    {
        StagingId = stagingId;
        Directory = directory;
        Gate = gate;
    }

    internal Guid StagingId { get; }

    internal string Directory { get; }

    internal PortableStagingGate Gate { get; }

    internal bool Deleted { get; set; }

    internal Dictionary<PortableStagingMediaIdentity, PortableStagingMediaItem> MediaByIdentity { get; } =
        [];

    internal Dictionary<string, PortableStagingMediaItem> MediaByReference { get; } =
        new(StringComparer.Ordinal);

    internal Dictionary<PortableStagingWrite, PortableStagingMediaItem> MediaByHandle { get; } = [];

    internal Dictionary<PortableStagingPayloadWrite, PortableStagingPayloadItem> PayloadByHandle { get; } =
        [];

    internal PortableStagingPayloadItem? Data { get; set; }

    internal PortableStagingPayloadItem? Manifest { get; set; }

    internal PreparedPortableImportMetadata? Prepared { get; set; }

    internal static PortableStagingMediaIdentity IdentityOf(PortableStagingMediaItem item) =>
        new(item.Descriptor.BookId, item.Descriptor.Kind, item.Descriptor.Path);
}

/// <summary>
/// One staged media item: the descriptor bound when the write was opened, its opaque
/// reference, the write state, and the handle while the item is still writable.
/// </summary>
internal sealed class PortableStagingMediaItem
{
    internal required PortableArchiveMediaEntry Descriptor { get; init; }

    internal required string Reference { get; init; }

    internal required PortableStagingWriteState State { get; init; }

    internal PortableStagingWrite? Handle { get; set; }
}

/// <summary>
/// One staged payload (the relational data or the manifest): the length and SHA-256
/// bound when the write was opened, the write state, and the handle while writable.
/// </summary>
internal sealed class PortableStagingPayloadItem
{
    internal required long Length { get; init; }

    internal required string Sha256 { get; init; }

    internal required PortableStagingWriteState State { get; init; }

    internal PortableStagingPayloadWrite? Handle { get; set; }
}

/// <summary>
/// An area with its gate held. Disposing the lease releases the gate exactly once, so
/// no transition can leak the area gate on an exception path.
/// </summary>
internal sealed class PortableStagingAreaLease : IDisposable
{
    private PortableStagingGateLease? _lease;

    internal PortableStagingAreaLease(
        PortableStagingArea area,
        PortableStagingGateLease lease)
    {
        Area = area;
        _lease = lease;
    }

    internal PortableStagingArea Area { get; }

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}

/// <summary>
/// The one prepared-import staging state machine: open, complete, commit, rebuild and
/// delete transitions, declared-length bounds, media identity, handle lifecycle, and
/// discard on failure. The two file-backed providers (the durable transfer-root
/// staging used for migration, and the process-local compatibility staging) implement
/// only their persistence differences through the abstract members below, so a fix to
/// a bound, a descriptor check or a commit invariant lands once.
/// </summary>
/// <remarks>
/// Ordering is part of the contract and is not the providers' business: the test hook
/// runs before the area gate, the gate is held for exactly one transition, completion
/// seals and verifies the bytes before the provider publishes them, the payload and
/// manifest must both be completed before a commit, and the prepared descriptor is
/// published before the area reports itself committed.
/// </remarks>
internal abstract class PortableImportStagingBase : IPortableImportStaging
{
    public abstract Task<PortableStagingId> CreateAsync(
        CancellationToken cancellationToken = default);

    public async Task<PortableStagingWrite> OpenMediaWriteAsync(
        PortableStagingId stagingId,
        PortableArchiveMediaEntry descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        using var lease = await EnterAreaAsync(
                stagingId,
                PortableStagingOperation.OpenMediaWrite,
                cancellationToken)
            .ConfigureAwait(false);
        var area = lease.Area;

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

        EnsureMediaDirectory(area);
        var reference = PortableStagingFilePrimitives.NewReference(
            candidate => area.MediaByReference.ContainsKey(candidate));
        var finalPath = MediaPath(area, reference);
        var state = PortableStagingWriteState.Open(
            CreateScratchFile(finalPath, out var tempPath),
            tempPath,
            finalPath,
            descriptor.Length);
        var item = new PortableStagingMediaItem
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
                BeforeStreamWrite));
        item.Handle = write;

        area.MediaByIdentity.Add(identity, item);
        area.MediaByReference.Add(reference, item);
        area.MediaByHandle.Add(write, item);
        return write;
    }

    public async Task CompleteMediaAsync(
        PortableStagingId stagingId,
        PortableStagingWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        using var lease = await EnterAreaAsync(
                stagingId,
                PortableStagingOperation.CompleteMedia,
                cancellationToken)
            .ConfigureAwait(false);
        var area = lease.Area;

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
                    FlushCompletedItemsToDisk)
                    .ConfigureAwait(false))
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "Staged media bytes do not match the descriptor bound when the write was opened.");
            }

            await PublishMediaAsync(area, item, cancellationToken).ConfigureAwait(false);
            item.State.Status = PortableStagingItemStatus.Completed;
        }
        catch (Exception exception) when (!MustNotDiscardOnFailure(exception))
        {
            DiscardMedia(area, item);
            throw;
        }
    }

    public Task<Stream> OpenMediaReadAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        CancellationToken cancellationToken = default) =>
        OpenMediaReadCoreAsync(stagingId, reference, cancellationToken);

    public async Task<IReadOnlyList<PortablePreparedMedia>> ListMediaAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        using var lease = await EnterAreaAsync(
                stagingId,
                PortableStagingOperation.List,
                cancellationToken)
            .ConfigureAwait(false);
        return SortMedia(DescribeCompletedMedia(lease.Area));
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

        using var lease = await EnterAreaAsync(
                stagingId,
                PortableStagingOperation.Commit,
                cancellationToken)
            .ConfigureAwait(false);
        var area = lease.Area;

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
            throw PortableStagingFilePrimitives.Conflict(
                "The staged media count does not match the prepared descriptor.");
        }

        if (media.Sum(item => item.Descriptor.Length) != metadata.MediaBytes)
        {
            throw PortableStagingFilePrimitives.Conflict(
                "The staged media bytes do not match the prepared descriptor.");
        }

        if (metadata.Counts.MediaEntries != metadata.MediaFiles)
        {
            throw PortableStagingFilePrimitives.Conflict(
                "The prepared counts media entries do not match the media file count.");
        }

        var (dataBytes, dataSha256) = await HashCompletedPayloadAsync(
                area,
                area.Data,
                cancellationToken)
            .ConfigureAwait(false);
        if (dataBytes != metadata.DataBytes
            || !PortableArchiveValidation.FixedHashEquals(dataSha256, metadata.DataSha256))
        {
            throw PortableStagingFilePrimitives.Conflict(
                "The staged relational payload does not match the prepared descriptor.");
        }

        // The provider publishes the committed descriptor durably. The area only
        // reports itself committed after that write succeeded, so a failed or
        // cancelled publish leaves the previous (uncommitted) visible state.
        await PublishPreparedAsync(area, metadata, cancellationToken).ConfigureAwait(false);
        area.Prepared = metadata;
    }

    /// <summary>
    /// Reconstructs the committed prepared import from staged state and the staging id
    /// alone, as required after a process restart.
    /// </summary>
    /// <remarks>
    /// Reconstruction verifies the relational payload against the committed SHA-256
    /// and verifies every staged media length, but it does not re-hash media contents.
    /// Activation (#681) MUST re-hash every staged media file against its
    /// <see cref="PortableArchiveMediaEntry.Sha256"/> before mutating the live library.
    /// </remarks>
    public async Task<IPreparedPortableImport> RebuildPreparedImportAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        using var lease = await EnterAreaAsync(
                stagingId,
                PortableStagingOperation.Rebuild,
                cancellationToken)
            .ConfigureAwait(false);
        var area = lease.Area;

        var prepared = area.Prepared
            ?? throw PortableStagingFilePrimitives.NotFound(
                "No prepared descriptor has been committed.");

        var media = SortMedia(
            await DescribeCompletedMediaForRebuildAsync(area, cancellationToken)
                .ConfigureAwait(false));

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

        var (dataBytes, dataSha256) = await HashCompletedPayloadAsync(
                area,
                area.Data,
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

    public async Task DeleteAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        if (stagingId.Value == Guid.Empty)
            return;

        PortableStagingArea area;
        using (var lease = await EnterAreaForDeleteAsync(
                   stagingId,
                   PortableStagingOperation.Delete,
                   cancellationToken)
                   .ConfigureAwait(false))
        {
            area = lease.Area;

            // The provider's durable deletion mark is written first and atomically:
            // from the moment it exists every operation and every fresh instance
            // treats the id as not found, even if physical cleanup later fails. A
            // failed mark therefore leaves the area exactly as it was.
            await MarkDeletedAsync(area, cancellationToken).ConfigureAwait(false);
            area.Deleted = true;
            DeactivateAll(area);
            OnAreaDeactivated(area);
        }

        // Best-effort physical cleanup: the durable state already hides the id, and a
        // leftover tombstoned tree is harmless and sweepable.
        CleanupDeletedArea(area);
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Resolves an id to its area and takes that area's gate, or throws the typed
    /// not-found outcome for an unknown, deleted or unreadable area. The returned lease
    /// owns the gate; the only failure path that must release it is inside the
    /// implementation itself.
    /// </summary>
    protected abstract ValueTask<PortableStagingAreaLease> EnterAreaAsync(
        PortableStagingId stagingId,
        PortableStagingOperation operation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an id to the area deletion must clear and takes that area's gate.
    /// Delete is idempotent, so an unknown id resolves to an empty area for that id
    /// instead of failing.
    /// </summary>
    protected abstract ValueTask<PortableStagingAreaLease> EnterAreaForDeleteAsync(
        PortableStagingId stagingId,
        PortableStagingOperation operation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Test-only seam: runs immediately before an operation takes the gate, outside
    /// it. Production providers return null.
    /// </summary>
    protected virtual Func<PortableStagingOperation, Task>? BeforeAreaGate => null;

    /// <summary>Test-only seam on the staging write streams; production returns null.</summary>
    protected virtual Action? BeforeStreamWrite => null;

    /// <summary>
    /// True when the provider flushes a completed item to disk before publishing it by
    /// rename. The durable provider does; the process-local one deliberately does not.
    /// </summary>
    protected abstract bool FlushCompletedItemsToDisk { get; }

    /// <summary>True for a failure that models process death and must not run the discard path.</summary>
    protected virtual bool MustNotDiscardOnFailure(Exception exception) => false;

    /// <summary>Creates the provider's scratch file for an item that will be published at <paramref name="finalPath"/>.</summary>
    protected abstract FileStream CreateScratchFile(string finalPath, out string tempPath);

    /// <summary>Resolves the published path of one staged media reference.</summary>
    protected abstract string MediaPath(PortableStagingArea area, string reference);

    /// <summary>Resolves the published path of the singular relational payload or manifest.</summary>
    protected abstract string PayloadPath(PortableStagingArea area, bool manifest);

    /// <summary>Ensures the media directory of the area exists before a media write opens.</summary>
    protected abstract void EnsureMediaDirectory(PortableStagingArea area);

    /// <summary>
    /// Publishes a sealed, verified media item: rename the scratch file into place and
    /// record the item in the provider's durable inventory. Throwing leaves the area in
    /// its previous visible state and discards the item.
    /// </summary>
    protected abstract ValueTask PublishMediaAsync(
        PortableStagingArea area,
        PortableStagingMediaItem item,
        CancellationToken cancellationToken);

    /// <summary>Publishes a sealed, verified payload and records it in the durable inventory.</summary>
    protected abstract ValueTask PublishPayloadAsync(
        PortableStagingArea area,
        PortableStagingPayloadItem item,
        bool manifest,
        CancellationToken cancellationToken);

    /// <summary>Publishes the committed prepared descriptor durably.</summary>
    protected abstract ValueTask PublishPreparedAsync(
        PortableStagingArea area,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken);

    /// <summary>Lists the completed media of the area as the provider sees it.</summary>
    protected abstract IReadOnlyList<PortablePreparedMedia> DescribeCompletedMedia(
        PortableStagingArea area);

    /// <summary>
    /// Lists the committed media of the area for a rebuild, verifying whatever the
    /// provider's durable inventory can prove (lengths, references, paths) first.
    /// </summary>
    protected abstract ValueTask<IReadOnlyList<PortablePreparedMedia>> DescribeCompletedMediaForRebuildAsync(
        PortableStagingArea area,
        CancellationToken cancellationToken);

    /// <summary>
    /// Hashes the completed relational payload after checking it still has the length
    /// the provider recorded, so a commit and a rebuild never trust a stale length.
    /// </summary>
    protected abstract ValueTask<(long Length, string Sha256)> HashCompletedPayloadAsync(
        PortableStagingArea area,
        PortableStagingPayloadItem item,
        CancellationToken cancellationToken);

    /// <summary>Opens a read stream over a completed media reference, or throws typed not found.</summary>
    protected abstract Stream OpenCompletedMediaRead(PortableStagingArea area, string reference);

    /// <summary>Opens a read stream over a completed payload.</summary>
    protected abstract Stream OpenCompletedPayloadRead(
        PortableStagingArea area,
        PortableStagingPayloadItem item,
        bool manifest);

    /// <summary>Removes provider-specific side state for a discarded media item.</summary>
    protected virtual void OnMediaDiscarded(
        PortableStagingArea area,
        PortableStagingMediaItem item)
    {
    }

    /// <summary>Writes the provider's durable deletion mark, if it has one.</summary>
    protected virtual ValueTask MarkDeletedAsync(
        PortableStagingArea area,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Removes the staging tree after the deletion mark is durable. Best effort.</summary>
    protected virtual void CleanupDeletedArea(PortableStagingArea area) =>
        PortableStagingFilePrimitives.TryDeleteDirectory(area.Directory);

    /// <summary>Provider bookkeeping after the area was deactivated under the gate.</summary>
    protected virtual void OnAreaDeactivated(PortableStagingArea area)
    {
    }

    /// <summary>
    /// Runs the test hook (if any) and then takes the gate. The hook is outside the
    /// gate on purpose: a paused operation must not block the operations behind it.
    /// </summary>
    protected async ValueTask<PortableStagingGateLease> EnterGateAsync(
        PortableStagingGate gate,
        PortableStagingOperation operation,
        CancellationToken cancellationToken)
    {
        var beforeGate = BeforeAreaGate;
        if (beforeGate is not null)
            await beforeGate(operation).ConfigureAwait(false);

        return await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Stream> OpenMediaReadCoreAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        CancellationToken cancellationToken)
    {
        using var lease = await EnterAreaAsync(
                stagingId,
                PortableStagingOperation.Read,
                cancellationToken)
            .ConfigureAwait(false);
        return OpenCompletedMediaRead(
            lease.Area,
            PortableStagingFilePrimitives.ValidateReference(reference.Value));
    }

    private async Task<PortableStagingPayloadWrite> OpenPayloadWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        bool manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        using var lease = await EnterAreaAsync(
                stagingId,
                PortableStagingOperation.OpenPayloadWrite,
                cancellationToken)
            .ConfigureAwait(false);
        var area = lease.Area;

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

        var finalPath = PayloadPath(area, manifest);
        var state = PortableStagingWriteState.Open(
            CreateScratchFile(finalPath, out var tempPath),
            tempPath,
            finalPath,
            descriptor.Length);
        var item = new PortableStagingPayloadItem
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
                BeforeStreamWrite));
        item.Handle = write;

        if (manifest)
            area.Manifest = item;
        else
            area.Data = item;

        area.PayloadByHandle.Add(write, item);
        return write;
    }

    private async Task CompletePayloadAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        bool manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        using var lease = await EnterAreaAsync(
                stagingId,
                PortableStagingOperation.CompletePayload,
                cancellationToken)
            .ConfigureAwait(false);
        var area = lease.Area;

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
                    FlushCompletedItemsToDisk)
                    .ConfigureAwait(false))
            {
                throw PortableStagingFilePrimitives.IntegrityMismatch(
                    "Staged payload bytes do not match the descriptor bound when the write was opened.");
            }

            await PublishPayloadAsync(area, item, manifest, cancellationToken).ConfigureAwait(false);
            item.State.Status = PortableStagingItemStatus.Completed;
        }
        catch (Exception exception) when (!MustNotDiscardOnFailure(exception))
        {
            DiscardPayload(area, item, manifest);
            throw;
        }
    }

    private async Task<Stream> OpenPayloadReadAsync(
        PortableStagingId stagingId,
        bool manifest,
        CancellationToken cancellationToken)
    {
        using var lease = await EnterAreaAsync(
                stagingId,
                PortableStagingOperation.Read,
                cancellationToken)
            .ConfigureAwait(false);
        var area = lease.Area;
        var item = manifest ? area.Manifest : area.Data;
        if (item?.State.Status != PortableStagingItemStatus.Completed)
            throw PortableStagingFilePrimitives.NotFound("No completed payload is available.");

        return OpenCompletedPayloadRead(area, item, manifest);
    }

    /// <summary>
    /// Discards one media item: closes its writer, removes its scratch and published
    /// bytes, drops it from every registry, and lets the provider drop its side state.
    /// Cleanup only: it never throws.
    /// </summary>
    protected void DiscardMedia(PortableStagingArea area, PortableStagingMediaItem item)
    {
        if (item.State.Status == PortableStagingItemStatus.Discarded)
            return;

        item.State.Status = PortableStagingItemStatus.Discarded;
        item.State.DisposeWriter();
        PortableStagingFilePrimitives.TryDeleteFile(item.State.TempPath);
        PortableStagingFilePrimitives.TryDeleteFile(item.State.FinalPath);
        OnMediaDiscarded(area, item);
        area.MediaByIdentity.Remove(PortableStagingArea.IdentityOf(item));
        area.MediaByReference.Remove(item.Reference);
        if (item.Handle is not null)
            area.MediaByHandle.Remove(item.Handle);
    }

    /// <summary>Discards one payload and clears the area's slot only while it still holds that item.</summary>
    private static void DiscardPayload(
        PortableStagingArea area,
        PortableStagingPayloadItem item,
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

    /// <summary>
    /// Discards every in-flight write of a deleted area. Completed items stay on disk
    /// for the provider's cleanup; the area is unusable from here on.
    /// </summary>
    private static void DeactivateAll(PortableStagingArea area)
    {
        foreach (var item in area.MediaByIdentity.Values.ToArray())
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
    }

    private static void EnsureNotCommitted(PortableStagingArea area)
    {
        if (area.Prepared is not null)
        {
            throw new PortableStagingException(
                PortableStagingException.AlreadyCommittedCode,
                "The staging area has already been committed and accepts no further writes.");
        }
    }

    /// <summary>
    /// The one media ordering both providers return: by book, then kind, then archive
    /// path, ordinally. A staging inventory and a rebuilt import therefore list the
    /// same items in the same order whichever provider staged them.
    /// </summary>
    protected static IReadOnlyList<PortablePreparedMedia> SortMedia(
        IReadOnlyList<PortablePreparedMedia> media)
    {
        var sorted = media.ToList();
        sorted.Sort(CompareMedia);
        return sorted;
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
}
