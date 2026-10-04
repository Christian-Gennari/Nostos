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

internal enum InMemoryItemState
{
    Writing,
    Completed,
    Abandoned,
}

internal readonly record struct MediaItemKey(Guid BookId, string Kind, string Path);

internal sealed class StagingAreaState
{
    internal Dictionary<MediaItemKey, InMemoryMediaItem> MediaByIdentity { get; } = new();

    internal Dictionary<string, InMemoryMediaItem> MediaByReference { get; } = new(StringComparer.Ordinal);

    internal InMemoryPayloadItem? Data { get; set; }

    internal InMemoryPayloadItem? Manifest { get; set; }

    internal PreparedPortableImportMetadata? Prepared { get; set; }
}

internal sealed class InMemoryMediaItem
{
    internal required PortableArchiveMediaEntry Descriptor { get; init; }

    internal required string Reference { get; init; }

    internal required MemoryStream Buffer { get; init; }

    internal InMemoryItemState State { get; set; } = InMemoryItemState.Writing;

    internal long CompletedLength { get; set; }

    internal string CompletedSha256 { get; set; } = string.Empty;
}

internal sealed class InMemoryPayloadItem
{
    internal required PortableArchivePayload Descriptor { get; init; }

    internal required string Reference { get; init; }

    internal required MemoryStream Buffer { get; init; }

    internal InMemoryItemState State { get; set; } = InMemoryItemState.Writing;

    internal long CompletedLength { get; set; }

    internal string CompletedSha256 { get; set; } = string.Empty;
}

/// <summary>
/// Test double that implements <see cref="IPortableImportStaging"/> with an in-memory
/// store shared between instances. It is not a production provider; it exists to
/// exercise the contract behaviourally, including restart reconstruction when two
/// instances share one <see cref="InMemoryPortableImportStagingStore"/>.
/// </summary>
internal sealed class InMemoryPortableImportStaging : IPortableImportStaging
{
    private readonly InMemoryPortableImportStagingStore _store;

    internal InMemoryPortableImportStaging(InMemoryPortableImportStagingStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
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

        lock (_store.Gate)
        {
            var area = ResolveArea(stagingId);

            if (descriptor.Length < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(descriptor),
                    "The declared media length cannot be negative.");
            }

            EnforceDeclaredLimit(descriptor.Length, PortableArchiveLimits.MaxSingleEntryBytes);

            var key = new MediaItemKey(descriptor.BookId, descriptor.Kind, descriptor.Path);
            if (area.MediaByIdentity.TryGetValue(key, out var existing))
            {
                if (existing.State != InMemoryItemState.Abandoned)
                {
                    throw new PortableStagingException(
                        PortableStagingException.ConflictCode,
                        "A write for this media item is already open or completed.");
                }

                area.MediaByReference.Remove(existing.Reference);
                area.MediaByIdentity.Remove(key);
            }

            var reference = NewReference(area);
            var item = new InMemoryMediaItem
            {
                Descriptor = descriptor,
                Reference = reference,
                Buffer = new MemoryStream(),
            };

            area.MediaByIdentity.Add(key, item);
            area.MediaByReference.Add(reference, item);

            return Task.FromResult(new PortableStagingWrite(
                new PortableStagedMediaReference(reference),
                new InMemoryWriteStream(
                    item.Buffer,
                    PortableArchiveLimits.MaxSingleEntryBytes,
                    () => IsAreaAlive(stagingId),
                    () =>
                    {
                        if (item.State == InMemoryItemState.Writing)
                        {
                            item.State = InMemoryItemState.Abandoned;
                        }
                    })));
        }
    }

    public Task CompleteMediaAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_store.Gate)
        {
            var area = ResolveArea(stagingId);
            var value = ValidateReference(reference.Value);
            if (!area.MediaByReference.TryGetValue(value, out var item))
            {
                throw NotFound("No media item matches the reference.");
            }

            if (WasCompletedWithSameValues(item.State, item.CompletedLength, item.CompletedSha256, expectedLength, expectedSha256))
            {
                return Task.CompletedTask;
            }

            if (item.State != InMemoryItemState.Writing)
            {
                throw NotFound("The media item is not writable.");
            }

            EnsureExpectedMatchesDescriptor(
                item.Descriptor.Length,
                item.Descriptor.Sha256,
                expectedLength,
                expectedSha256,
                () => DiscardMedia(area, item));

            var bytes = item.Buffer.ToArray();
            var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (bytes.LongLength != expectedLength
                || !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                DiscardMedia(area, item);
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "Staged media bytes do not match the expected length and SHA-256.");
            }

            item.CompletedLength = bytes.LongLength;
            item.CompletedSha256 = actualSha256;
            item.State = InMemoryItemState.Completed;

            return Task.CompletedTask;
        }
    }

    public Task<Stream> OpenMediaReadAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_store.Gate)
        {
            var area = ResolveArea(stagingId);
            var value = ValidateReference(reference.Value);
            if (!area.MediaByReference.TryGetValue(value, out var item)
                || item.State != InMemoryItemState.Completed)
            {
                throw NotFound("No completed media item matches the reference.");
            }

            return Task.FromResult<Stream>(new MemoryStream(item.Buffer.ToArray(), writable: false));
        }
    }

    public Task<IReadOnlyList<PortablePreparedMedia>> ListMediaAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_store.Gate)
        {
            var area = ResolveArea(stagingId);
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

    public Task CompleteDataAsync(
        PortableStagingId stagingId,
        PortableStagedPayloadReference reference,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default) =>
        CompletePayloadAsync(stagingId, reference, expectedLength, expectedSha256, manifest: false, cancellationToken);

    public Task CompleteManifestAsync(
        PortableStagingId stagingId,
        PortableStagedPayloadReference reference,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default) =>
        CompletePayloadAsync(stagingId, reference, expectedLength, expectedSha256, manifest: true, cancellationToken);

    public Task<Stream> OpenDataReadAsync(
        PortableStagingId stagingId,
        PortableStagedPayloadReference reference,
        CancellationToken cancellationToken = default) =>
        OpenPayloadReadAsync(stagingId, reference, manifest: false, cancellationToken);

    public Task<Stream> OpenManifestReadAsync(
        PortableStagingId stagingId,
        PortableStagedPayloadReference reference,
        CancellationToken cancellationToken = default) =>
        OpenPayloadReadAsync(stagingId, reference, manifest: true, cancellationToken);

    public Task CommitPreparedImportAsync(
        PortableStagingId stagingId,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_store.Gate)
        {
            var area = ResolveArea(stagingId);

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

            if (area.Data?.State != InMemoryItemState.Completed
                || area.Manifest?.State != InMemoryItemState.Completed)
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A prepared import requires a completed relational payload and manifest.");
            }

            if (area.MediaByIdentity.Values.Any(item => item.State == InMemoryItemState.Writing))
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A prepared import cannot commit while a media write is still open.");
            }

            var completedMedia = DescribeCompletedMedia(area);
            if (completedMedia.Count != metadata.MediaFiles)
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "The staged media count does not match the prepared descriptor.");
            }

            if (completedMedia.Sum(item => item.Descriptor.Length) != metadata.MediaBytes)
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "The staged media bytes do not match the prepared descriptor.");
            }

            if (area.Data.CompletedLength != metadata.DataBytes
                || !string.Equals(area.Data.CompletedSha256, metadata.DataSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
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

        lock (_store.Gate)
        {
            var area = ResolveArea(stagingId);
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

            return Task.FromResult<IPreparedPortableImport>(
                new PortablePreparedImport(prepared, media));
        }
    }

    public Task DeleteAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_store.Gate)
        {
            _store.Areas.Remove(stagingId.Value);
        }

        return Task.CompletedTask;
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

        lock (_store.Gate)
        {
            var area = ResolveArea(stagingId);

            if (descriptor.Length < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(descriptor),
                    "The declared payload length cannot be negative.");
            }

            var limit = manifest
                ? PortableArchiveLimits.MaxManifestBytes
                : PortableArchiveLimits.MaxDataBytes;
            EnforceDeclaredLimit(descriptor.Length, limit);

            var existing = manifest ? area.Manifest : area.Data;
            if (existing is not null && existing.State != InMemoryItemState.Abandoned)
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A write for this payload is already open or completed.");
            }

            var reference = NewReference(area);
            var item = new InMemoryPayloadItem
            {
                Descriptor = descriptor,
                Reference = reference,
                Buffer = new MemoryStream(),
            };

            if (manifest)
            {
                area.Manifest = item;
            }
            else
            {
                area.Data = item;
            }

            return Task.FromResult(new PortableStagingPayloadWrite(
                new PortableStagedPayloadReference(reference),
                new InMemoryWriteStream(
                    item.Buffer,
                    limit,
                    () => IsAreaAlive(stagingId),
                    () =>
                    {
                        if (item.State == InMemoryItemState.Writing)
                        {
                            item.State = InMemoryItemState.Abandoned;
                        }
                    })));
        }
    }

    private Task CompletePayloadAsync(
        PortableStagingId stagingId,
        PortableStagedPayloadReference reference,
        long expectedLength,
        string expectedSha256,
        bool manifest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_store.Gate)
        {
            var area = ResolveArea(stagingId);
            var value = ValidateReference(reference.Value);
            var item = manifest ? area.Manifest : area.Data;
            if (item is null || !string.Equals(item.Reference, value, StringComparison.Ordinal))
            {
                throw NotFound("No payload matches the reference.");
            }

            if (WasCompletedWithSameValues(item.State, item.CompletedLength, item.CompletedSha256, expectedLength, expectedSha256))
            {
                return Task.CompletedTask;
            }

            if (item.State != InMemoryItemState.Writing)
            {
                throw NotFound("The payload is not writable.");
            }

            EnsureExpectedMatchesDescriptor(
                item.Descriptor.Length,
                item.Descriptor.Sha256,
                expectedLength,
                expectedSha256,
                () => item.State = InMemoryItemState.Abandoned);

            var bytes = item.Buffer.ToArray();
            var actualSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (bytes.LongLength != expectedLength
                || !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                item.State = InMemoryItemState.Abandoned;
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "Staged payload bytes do not match the expected length and SHA-256.");
            }

            item.CompletedLength = bytes.LongLength;
            item.CompletedSha256 = actualSha256;
            item.State = InMemoryItemState.Completed;

            return Task.CompletedTask;
        }
    }

    private Task<Stream> OpenPayloadReadAsync(
        PortableStagingId stagingId,
        PortableStagedPayloadReference reference,
        bool manifest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_store.Gate)
        {
            var area = ResolveArea(stagingId);
            var value = ValidateReference(reference.Value);
            var item = manifest ? area.Manifest : area.Data;
            if (item is null
                || !string.Equals(item.Reference, value, StringComparison.Ordinal)
                || item.State != InMemoryItemState.Completed)
            {
                throw NotFound("No completed payload matches the reference.");
            }

            return Task.FromResult<Stream>(new MemoryStream(item.Buffer.ToArray(), writable: false));
        }
    }

    private StagingAreaState ResolveArea(PortableStagingId stagingId)
    {
        if (stagingId.Value == Guid.Empty
            || !_store.Areas.TryGetValue(stagingId.Value, out var area))
        {
            throw NotFound("Unknown staging area.");
        }

        return area;
    }

    private bool IsAreaAlive(PortableStagingId stagingId) =>
        _store.Areas.ContainsKey(stagingId.Value);

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

    private static void EnforceDeclaredLimit(long declaredLength, long limit)
    {
        if (declaredLength > limit)
        {
            throw new PortableStagingException(
                PortableStagingException.LimitExceededCode,
                $"The declared length exceeds the {limit}-byte staging limit.");
        }
    }

    private static void EnsureExpectedMatchesDescriptor(
        long descriptorLength,
        string descriptorSha256,
        long expectedLength,
        string expectedSha256,
        Action discard)
    {
        if (descriptorLength != expectedLength
            || !string.Equals(descriptorSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            discard();
            throw new PortableStagingException(
                PortableStagingException.IntegrityMismatchCode,
                "Completion values do not match the descriptor bound when the write was opened.");
        }
    }

    private static bool WasCompletedWithSameValues(
        InMemoryItemState state,
        long completedLength,
        string completedSha256,
        long expectedLength,
        string expectedSha256)
    {
        if (state != InMemoryItemState.Completed)
        {
            return false;
        }

        if (completedLength == expectedLength
            && string.Equals(completedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new PortableStagingException(
            PortableStagingException.ConflictCode,
            "The item is already completed with different values.");
    }

    private static void DiscardMedia(StagingAreaState area, InMemoryMediaItem item)
    {
        item.State = InMemoryItemState.Abandoned;
        area.MediaByIdentity.Remove(new MediaItemKey(
            item.Descriptor.BookId,
            item.Descriptor.Kind,
            item.Descriptor.Path));
        area.MediaByReference.Remove(item.Reference);
    }

    private static IReadOnlyList<PortablePreparedMedia> DescribeCompletedMedia(StagingAreaState area) =>
        area.MediaByIdentity.Values
            .Where(item => item.State == InMemoryItemState.Completed)
            .OrderBy(item => item.Descriptor.BookId)
            .ThenBy(item => item.Descriptor.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Descriptor.Path, StringComparer.Ordinal)
            .Select(item => new PortablePreparedMedia(
                item.Descriptor,
                new PortableStagedMediaReference(item.Reference)))
            .ToArray();

    private static PortableStagingException NotFound(string message) =>
        new(PortableStagingException.NotFoundCode, message);
}

/// <summary>
/// Sequential write stream that enforces the staging size limit while bytes are
/// written and fails once the owning staging area is deleted.
/// </summary>
internal sealed class InMemoryWriteStream : Stream
{
    private readonly MemoryStream _buffer;
    private readonly long _limit;
    private readonly Func<bool> _isAreaAlive;
    private readonly Action _onAbandoned;
    private bool _disposed;

    internal InMemoryWriteStream(
        MemoryStream buffer,
        long limit,
        Func<bool> isAreaAlive,
        Action onAbandoned)
    {
        _buffer = buffer;
        _limit = limit;
        _isAreaAlive = isAreaAlive;
        _onAbandoned = onAbandoned;
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
        EnsureWritable();

        if (_buffer.Length + buffer.Length > _limit)
        {
            throw new PortableStagingException(
                PortableStagingException.LimitExceededCode,
                $"The staged item exceeds the {_limit}-byte staging limit.");
        }

        _buffer.Write(buffer);
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
                _onAbandoned();
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

        if (!_isAreaAlive())
        {
            throw new PortableStagingException(
                PortableStagingException.NotFoundCode,
                "The staging area has been deleted.");
        }
    }
}
