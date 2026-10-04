using System.Security.Cryptography;
using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Shared backing store for <see cref="InMemoryPortableImportStaging"/>. Reusing one
/// store across two staging instances models a provider that survives a process
/// restart: disposing an instance never discards staged state, only
/// <c>DeleteAsync</c> does.
/// </summary>
internal sealed class InMemoryPortableImportStagingStore
{
    internal object Gate { get; } = new();

    internal Dictionary<Guid, StagingAreaState> Areas { get; } = new();
}

/// <summary>
/// Test-only interleaving hooks. They run before the staging area lock is taken, so
/// a test can deterministically let a competing operation win a race.
/// </summary>
internal sealed class InMemoryPortableImportStagingHooks
{
    internal Func<Task>? BeforeCompleteAsync { get; set; }

    internal Func<Task>? BeforeDeleteAsync { get; set; }

    internal Action? BeforeStreamWrite { get; set; }
}

internal enum InMemoryItemStatus
{
    Writing,
    Completed,
    Discarded,
}

internal readonly record struct MediaItemKey(Guid BookId, string Kind, string Path);

internal sealed class InMemoryItemState
{
    internal MemoryStream Buffer { get; } = new();

    internal InMemoryItemStatus Status { get; set; } = InMemoryItemStatus.Writing;

    internal long EffectiveLimit { get; init; }

    internal long CompletedLength { get; set; }

    internal string CompletedSha256 { get; set; } = string.Empty;
}

internal sealed class InMemoryMediaItem
{
    internal required PortableArchiveMediaEntry Descriptor { get; init; }

    internal required string Reference { get; init; }

    internal required InMemoryItemState State { get; init; }

    internal PortableStagingWrite? Handle { get; set; }
}

internal sealed class InMemoryPayloadItem
{
    internal required PortableArchivePayload Descriptor { get; init; }

    internal required InMemoryItemState State { get; init; }

    internal PortableStagingPayloadWrite? Handle { get; set; }
}

internal sealed class StagingAreaState
{
    internal object Gate { get; } = new();

    internal bool Deleted { get; set; }

    internal Dictionary<MediaItemKey, InMemoryMediaItem> MediaByIdentity { get; } = new();

    internal Dictionary<string, InMemoryMediaItem> MediaByReference { get; } = new(StringComparer.Ordinal);

    internal Dictionary<PortableStagingWrite, InMemoryMediaItem> MediaByHandle { get; } = new();

    internal Dictionary<PortableStagingPayloadWrite, InMemoryPayloadItem> PayloadByHandle { get; } = new();

    internal InMemoryPayloadItem? Data { get; set; }

    internal InMemoryPayloadItem? Manifest { get; set; }

    internal PreparedPortableImportMetadata? Prepared { get; set; }
}

/// <summary>
/// Test double that implements <see cref="IPortableImportStaging"/> with an in-memory
/// store shared between instances. All area state transitions are guarded by one lock
/// per staging area. It is not a production provider; it exists to exercise the
/// contract behaviourally, including restart reconstruction when two instances share
/// one <see cref="InMemoryPortableImportStagingStore"/>.
/// </summary>
internal sealed class InMemoryPortableImportStaging : IPortableImportStaging
{
    private readonly InMemoryPortableImportStagingStore _store;
    private readonly InMemoryPortableImportStagingHooks? _hooks;

    internal InMemoryPortableImportStaging(
        InMemoryPortableImportStagingStore store,
        InMemoryPortableImportStagingHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _hooks = hooks;
    }

    public Task<PortableStagingId> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_store.Gate)
        {
            var id = Guid.NewGuid();
            _store.Areas.Add(id, new StagingAreaState());
            return Task.FromResult(new PortableStagingId(id));
        }
    }

    public Task<PortableStagingWrite> OpenMediaWriteAsync(
        PortableStagingId stagingId,
        PortableArchiveMediaEntry descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        var area = ResolveArea(stagingId);

        lock (area.Gate)
        {
            EnsureAreaAcceptsWrites(area);

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

            var key = new MediaItemKey(descriptor.BookId, descriptor.Kind, descriptor.Path);
            if (area.MediaByIdentity.ContainsKey(key))
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A write for this media item is already open or completed.");
            }

            var state = new InMemoryItemState
            {
                EffectiveLimit = Math.Min(descriptor.Length, PortableArchiveLimits.MaxSingleEntryBytes),
            };
            var item = new InMemoryMediaItem
            {
                Descriptor = descriptor,
                Reference = NewReference(area),
                State = state,
            };
            var write = new PortableStagingWrite(
                new PortableStagedMediaReference(item.Reference),
                new InMemoryWriteStream(area, state, _hooks, () => DiscardMedia(area, item)));
            item.Handle = write;

            area.MediaByIdentity.Add(key, item);
            area.MediaByReference.Add(item.Reference, item);
            area.MediaByHandle.Add(write, item);

            return Task.FromResult(write);
        }
    }

    public async Task CompleteMediaAsync(
        PortableStagingId stagingId,
        PortableStagingWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        var area = ResolveArea(stagingId);
        await RunHookAsync(_hooks?.BeforeCompleteAsync).ConfigureAwait(false);

        lock (area.Gate)
        {
            EnsureAreaExists(area);
            EnsureNotCommitted(area);

            if (!area.MediaByHandle.TryGetValue(write, out var item))
            {
                throw NotFound("No media item matches the write handle.");
            }

            if (item.State.Status == InMemoryItemStatus.Completed)
            {
                return;
            }

            if (item.State.Status != InMemoryItemStatus.Writing)
            {
                throw NotFound("The media write is no longer completable.");
            }

            var bytes = item.State.Buffer.ToArray();
            var actualSha256 = Sha256Hex(bytes);
            if (bytes.LongLength != item.Descriptor.Length
                || !string.Equals(actualSha256, item.Descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                DiscardMedia(area, item);
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "Staged media bytes do not match the descriptor bound when the write was opened.");
            }

            item.State.CompletedLength = bytes.LongLength;
            item.State.CompletedSha256 = actualSha256;
            item.State.Status = InMemoryItemStatus.Completed;
        }
    }

    public Task<Stream> OpenMediaReadAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var area = ResolveArea(stagingId);

        lock (area.Gate)
        {
            EnsureAreaExists(area);

            var value = ValidateReference(reference.Value);
            if (!area.MediaByReference.TryGetValue(value, out var item)
                || item.State.Status != InMemoryItemStatus.Completed)
            {
                throw NotFound("No completed media item matches the reference.");
            }

            return Task.FromResult<Stream>(new MemoryStream(item.State.Buffer.ToArray(), writable: false));
        }
    }

    public Task<IReadOnlyList<PortablePreparedMedia>> ListMediaAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var area = ResolveArea(stagingId);

        lock (area.Gate)
        {
            EnsureAreaExists(area);
            IReadOnlyList<PortablePreparedMedia> inventory = DescribeCompletedMedia(area);
            return Task.FromResult(inventory);
        }
    }

    public Task<PortableStagingPayloadWrite> OpenDataWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default) =>
        OpenPayloadWrite(stagingId, descriptor, manifest: false, cancellationToken);

    public Task<PortableStagingPayloadWrite> OpenManifestWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        CancellationToken cancellationToken = default) =>
        OpenPayloadWrite(stagingId, descriptor, manifest: true, cancellationToken);

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

    public Task<Stream> OpenDataReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        OpenPayloadReadAsync(stagingId, manifest: false, cancellationToken);

    public Task<Stream> OpenManifestReadAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default) =>
        OpenPayloadReadAsync(stagingId, manifest: true, cancellationToken);

    public Task CommitPreparedImportAsync(
        PortableStagingId stagingId,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(metadata.Counts);
        cancellationToken.ThrowIfCancellationRequested();

        var area = ResolveArea(stagingId);

        lock (area.Gate)
        {
            EnsureAreaExists(area);

            if (metadata.StagingId != stagingId)
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "The prepared descriptor belongs to a different staging area.");
            }

            if (area.Prepared is not null)
            {
                if (area.Prepared == metadata)
                {
                    return Task.CompletedTask;
                }

                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A different prepared descriptor is already committed.");
            }

            if (area.Data?.State.Status != InMemoryItemStatus.Completed
                || area.Manifest?.State.Status != InMemoryItemStatus.Completed)
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A prepared import requires a completed relational payload and manifest.");
            }

            if (area.MediaByIdentity.Values.Any(item => item.State.Status == InMemoryItemStatus.Writing))
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A prepared import cannot commit while a media write is still open.");
            }

            var completedMedia = DescribeCompletedMedia(area);
            if (completedMedia.Count != metadata.MediaFiles)
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "The staged media count does not match the prepared descriptor.");
            }

            if (completedMedia.Sum(item => item.Descriptor.Length) != metadata.MediaBytes)
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "The staged media bytes do not match the prepared descriptor.");
            }

            if (metadata.Counts.MediaEntries != metadata.MediaFiles)
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "The prepared counts media entries do not match the media file count.");
            }

            if (area.Data.State.CompletedLength != metadata.DataBytes
                || !string.Equals(
                    area.Data.State.CompletedSha256,
                    metadata.DataSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "The staged relational payload does not match the prepared descriptor.");
            }

            area.Prepared = metadata;
            return Task.CompletedTask;
        }
    }

    public Task<IPreparedPortableImport> RebuildPreparedImportAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var area = ResolveArea(stagingId);

        lock (area.Gate)
        {
            EnsureAreaExists(area);

            var prepared = area.Prepared
                ?? throw NotFound("No prepared descriptor has been committed.");

            var media = DescribeCompletedMedia(area);
            if (media.Count != prepared.MediaFiles
                || media.Sum(item => item.Descriptor.Length) != prepared.MediaBytes)
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "Staged media no longer matches the committed prepared descriptor.");
            }

            if (area.Data is null
                || area.Data.State.Status != InMemoryItemStatus.Completed
                || area.Data.State.CompletedLength != prepared.DataBytes
                || !string.Equals(
                    area.Data.State.CompletedSha256,
                    prepared.DataSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "The staged relational payload no longer matches the committed prepared descriptor.");
            }

            return Task.FromResult<IPreparedPortableImport>(
                new PortablePreparedImport(prepared, media));
        }
    }

    public async Task DeleteAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await RunHookAsync(_hooks?.BeforeDeleteAsync).ConfigureAwait(false);

        StagingAreaState? area;
        lock (_store.Gate)
        {
            if (!_store.Areas.TryGetValue(stagingId.Value, out area))
            {
                return;
            }
        }

        lock (area.Gate)
        {
            area.Deleted = true;
            area.MediaByIdentity.Clear();
            area.MediaByReference.Clear();
            area.MediaByHandle.Clear();
            area.PayloadByHandle.Clear();
            area.Data = null;
            area.Manifest = null;
            area.Prepared = null;
        }

        lock (_store.Gate)
        {
            _store.Areas.Remove(stagingId.Value);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<PortableStagingPayloadWrite> OpenPayloadWrite(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        bool manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        var area = ResolveArea(stagingId);

        lock (area.Gate)
        {
            EnsureAreaAcceptsWrites(area);

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
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A write for this payload is already open or completed.");
            }

            var state = new InMemoryItemState
            {
                EffectiveLimit = Math.Min(descriptor.Length, hardLimit),
            };
            var item = new InMemoryPayloadItem
            {
                Descriptor = descriptor,
                State = state,
            };
            var write = new PortableStagingPayloadWrite(
                new InMemoryWriteStream(
                    area,
                    state,
                    _hooks,
                    () => DiscardPayload(area, item, manifest)));
            item.Handle = write;

            if (manifest)
            {
                area.Manifest = item;
            }
            else
            {
                area.Data = item;
            }

            area.PayloadByHandle.Add(write, item);
            return Task.FromResult(write);
        }
    }

    private async Task CompletePayloadAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        bool manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        var area = ResolveArea(stagingId);
        await RunHookAsync(_hooks?.BeforeCompleteAsync).ConfigureAwait(false);

        lock (area.Gate)
        {
            EnsureAreaExists(area);
            EnsureNotCommitted(area);

            var item = manifest ? area.Manifest : area.Data;
            if (item is null
                || !area.PayloadByHandle.TryGetValue(write, out var bound)
                || !ReferenceEquals(bound, item))
            {
                throw NotFound("No payload matches the write handle.");
            }

            if (item.State.Status == InMemoryItemStatus.Completed)
            {
                return;
            }

            if (item.State.Status != InMemoryItemStatus.Writing)
            {
                throw NotFound("The payload write is no longer completable.");
            }

            var bytes = item.State.Buffer.ToArray();
            var actualSha256 = Sha256Hex(bytes);
            if (bytes.LongLength != item.Descriptor.Length
                || !string.Equals(actualSha256, item.Descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                DiscardPayload(area, item, manifest);
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "Staged payload bytes do not match the descriptor bound when the write was opened.");
            }

            item.State.CompletedLength = bytes.LongLength;
            item.State.CompletedSha256 = actualSha256;
            item.State.Status = InMemoryItemStatus.Completed;
        }
    }

    private Task<Stream> OpenPayloadReadAsync(
        PortableStagingId stagingId,
        bool manifest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var area = ResolveArea(stagingId);

        lock (area.Gate)
        {
            EnsureAreaExists(area);

            var item = manifest ? area.Manifest : area.Data;
            if (item is null || item.State.Status != InMemoryItemStatus.Completed)
            {
                throw NotFound("No completed payload is available.");
            }

            return Task.FromResult<Stream>(new MemoryStream(item.State.Buffer.ToArray(), writable: false));
        }
    }

    private StagingAreaState ResolveArea(PortableStagingId stagingId)
    {
        lock (_store.Gate)
        {
            if (stagingId.Value == Guid.Empty
                || !_store.Areas.TryGetValue(stagingId.Value, out var area))
            {
                throw NotFound("Unknown staging area.");
            }

            return area;
        }
    }

    private static void EnsureAreaExists(StagingAreaState area)
    {
        if (area.Deleted)
        {
            throw NotFound("Unknown staging area.");
        }
    }

    private static void EnsureAreaAcceptsWrites(StagingAreaState area)
    {
        EnsureAreaExists(area);
        EnsureNotCommitted(area);
    }

    private static void EnsureNotCommitted(StagingAreaState area)
    {
        if (area.Prepared is not null)
        {
            throw new PortableStagingException(
                PortableStagingException.AlreadyCommittedCode,
                "The staging area has already been committed and accepts no further writes.");
        }
    }

    private static async Task RunHookAsync(Func<Task>? hook)
    {
        if (hook is not null)
        {
            await hook().ConfigureAwait(false);
        }
    }

    private static string NewReference(StagingAreaState area)
    {
        string reference;
        do
        {
            reference = Guid.NewGuid().ToString("N");
        }
        while (area.MediaByReference.ContainsKey(reference));

        return reference;
    }

    private static string ValidateReference(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Contains('/')
            || value.Contains('\\')
            || value.Contains("..", StringComparison.Ordinal))
        {
            throw new PortableStagingException(
                PortableStagingException.InvalidReferenceCode,
                "The staged reference is not a valid opaque token.");
        }

        return value;
    }

    private static void DiscardMedia(StagingAreaState area, InMemoryMediaItem item)
    {
        item.State.Status = InMemoryItemStatus.Discarded;
        area.MediaByIdentity.Remove(new MediaItemKey(
            item.Descriptor.BookId,
            item.Descriptor.Kind,
            item.Descriptor.Path));
        area.MediaByReference.Remove(item.Reference);

        if (item.Handle is not null)
        {
            area.MediaByHandle.Remove(item.Handle);
        }
    }

    private static void DiscardPayload(
        StagingAreaState area,
        InMemoryPayloadItem item,
        bool manifest)
    {
        item.State.Status = InMemoryItemStatus.Discarded;

        if (manifest)
        {
            area.Manifest = null;
        }
        else
        {
            area.Data = null;
        }

        if (item.Handle is not null)
        {
            area.PayloadByHandle.Remove(item.Handle);
        }
    }

    private static IReadOnlyList<PortablePreparedMedia> DescribeCompletedMedia(StagingAreaState area) =>
        area.MediaByIdentity.Values
            .Where(item => item.State.Status == InMemoryItemStatus.Completed)
            .OrderBy(item => item.Descriptor.BookId)
            .ThenBy(item => item.Descriptor.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Descriptor.Path, StringComparer.Ordinal)
            .Select(item => new PortablePreparedMedia(
                item.Descriptor,
                new PortableStagedMediaReference(item.Reference)))
            .ToArray();

    private static PortableStagingException NotFound(string message) =>
        new(PortableStagingException.NotFoundCode, message);

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

/// <summary>
/// Sequential write stream that enforces the staged item limit while bytes are
/// written, discards the item on overflow, seals on completion, and fails once the
/// owning staging area is deleted.
/// </summary>
internal sealed class InMemoryWriteStream : Stream
{
    private readonly StagingAreaState _area;
    private readonly InMemoryItemState _item;
    private readonly InMemoryPortableImportStagingHooks? _hooks;
    private readonly Action _discard;
    private bool _disposed;

    internal InMemoryWriteStream(
        StagingAreaState area,
        InMemoryItemState item,
        InMemoryPortableImportStagingHooks? hooks,
        Action discard)
    {
        _area = area;
        _item = item;
        _hooks = hooks;
        _discard = discard;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_disposed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hooks?.BeforeStreamWrite?.Invoke();

        lock (_area.Gate)
        {
            EnsureWritable();

            if (_item.Buffer.Length + buffer.Length > _item.EffectiveLimit)
            {
                _discard();
                throw new PortableStagingException(
                    PortableStagingException.LimitExceededCode,
                    $"The staged item exceeds its {_item.EffectiveLimit}-byte limit.");
            }

            _item.Buffer.Write(buffer);
        }
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;

            if (disposing)
            {
                lock (_area.Gate)
                {
                    if (_item.Status == InMemoryItemStatus.Writing)
                    {
                        _discard();
                    }
                }
            }
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void EnsureWritable()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(InMemoryWriteStream));
        }

        if (_area.Deleted)
        {
            throw new PortableStagingException(
                PortableStagingException.NotFoundCode,
                "The staging area has been deleted.");
        }

        if (_item.Status != InMemoryItemStatus.Writing)
        {
            throw new PortableStagingException(
                PortableStagingException.ConflictCode,
                "The write handle has been completed, discarded, or closed.");
        }
    }
}
