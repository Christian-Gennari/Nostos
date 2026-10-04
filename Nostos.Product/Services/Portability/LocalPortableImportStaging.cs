using System.Text.Json;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Process-local file staging for the immediate compatibility import
/// (<c>POST /api/portability/import</c>). Every staged byte lives under a
/// caller-owned disposable scratch root; disposing the provider releases no files and
/// only <see cref="DeleteAsync"/> (or deleting the root) removes staged bytes.
/// </summary>
/// <remarks>
/// <para>
/// Committed prepared descriptors, the relational payload, the manifest, and every
/// completed media item are written to disk, so a second instance over the same root
/// can rebuild a committed prepared import. In-flight writes are process-local.
/// </para>
/// <para>
/// This provider is deliberately scoped to one immediate flow: it promises no
/// durability across a process restart for uncommitted work and must never back a
/// restart-survivable migration job (plan 9.10 and 9.23). The caller owns deleting
/// the scratch root, including after a failure.
/// </para>
/// </remarks>
internal sealed class LocalPortableImportStaging
    : IPortableImportStaging, IPortableStagingCapacityAdmission
{
    private const int StreamBufferBytes = 128 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _root;
    private readonly Func<string, long?> _availableBytesProbe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, LocalArea> _areas = [];
    private readonly HashSet<Guid> _deleted = [];

    public LocalPortableImportStaging(string root)
        : this(root, availableBytesProbe: null)
    {
    }

    internal LocalPortableImportStaging(
        string root,
        Func<string, long?>? availableBytesProbe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _availableBytesProbe = availableBytesProbe ?? DefaultAvailableBytesProbe;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Compatibility admission restored from the legacy extraction path: refuse an
    /// import before bulk staging when the staging volume cannot hold the declared
    /// staged bytes while preserving 20% of its remaining free space. A probe failure
    /// keeps the legacy <c>temp_space_unavailable</c> outcome.
    /// </summary>
    public void EnsureCapacity(long bytesToStage)
    {
        if (bytesToStage <= 0)
            return;

        long? available;
        try
        {
            available = _availableBytesProbe(_root);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            available = null;
        }

        if (available is null)
        {
            throw new PortableArchiveException(
                "temp_space_unavailable",
                "Portable archive extraction cannot determine temporary-storage capacity.");
        }

        var extractionBudget = available.Value - (available.Value / 5);
        if (bytesToStage > extractionBudget)
        {
            throw new PortableArchiveException(
                "insufficient_temp_space",
                "Portable archive media cannot be staged safely with the temporary storage currently available.");
        }
    }

    public async Task<PortableStagingId> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Guid.NewGuid();
            var area = LocalArea.Create(id, _root);
            _areas.Add(id, area);
            return new PortableStagingId(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PortableStagingWrite> OpenMediaWriteAsync(
        PortableStagingId stagingId,
        PortableArchiveMediaEntry descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var area = GetExistingArea(stagingId);
            EnsureAcceptsWrites(area);

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

            var identity = new PortableStagingMediaIdentity(descriptor.BookId, descriptor.Kind, descriptor.Path);
            if (area.MediaByIdentity.ContainsKey(identity))
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A write for this media item is already open or completed.");
            }

            var reference = PortableStagingFilePrimitives.NewReference(
                candidate => area.MediaByReference.ContainsKey(candidate));
            var finalPath = Path.Combine(area.MediaDirectory, reference + ".bin");
            var tempPath = finalPath + ".tmp";
            var state = PortableStagingWriteState.Open(
                OpenLocalWriteStream(tempPath),
                tempPath,
                finalPath,
                descriptor.Length);
            var item = new LocalMediaItem
            {
                Descriptor = descriptor,
                Reference = reference,
                State = state,
            };
            var write = new PortableStagingWrite(
                new PortableStagedMediaReference(reference),
                new PortableStagingWriteStream(
                    _gate,
                    state,
                    () => area.Deleted,
                    () => DiscardMedia(area, item)));
            item.Handle = write;

            area.MediaByIdentity.Add(identity, item);
            area.MediaByReference.Add(reference, item);
            area.MediaByHandle.Add(write, item);
            return write;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteMediaAsync(
        PortableStagingId stagingId,
        PortableStagingWrite write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var area = GetExistingArea(stagingId);
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
                        flushToDisk: false)
                        .ConfigureAwait(false))
                {
                    throw new PortableStagingException(
                        PortableStagingException.IntegrityMismatchCode,
                        "Staged media bytes do not match the descriptor bound when the write was opened.");
                }

                await File.WriteAllTextAsync(
                        MetaPath(area, item.Reference),
                        JsonSerializer.Serialize(item.Descriptor, JsonOptions),
                        cancellationToken)
                    .ConfigureAwait(false);
                File.Move(item.State.TempPath, item.State.FinalPath);
                item.State.Status = PortableStagingItemStatus.Completed;
            }
            catch
            {
                DiscardMedia(area, item);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Stream> OpenMediaReadAsync(
        PortableStagingId stagingId,
        PortableStagedMediaReference reference,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var area = GetExistingArea(stagingId);
            var value = PortableStagingFilePrimitives.ValidateReference(reference.Value);
            var finalPath = Path.Combine(area.MediaDirectory, value + ".bin");
            if (!File.Exists(finalPath))
                throw PortableStagingFilePrimitives.NotFound("No completed media item matches the reference.");

            return OpenRead(finalPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PortablePreparedMedia>> ListMediaAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return DescribeCompletedMedia(GetExistingArea(stagingId));
        }
        finally
        {
            _gate.Release();
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var area = GetExistingArea(stagingId);

            if (metadata.StagingId != stagingId)
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "The prepared descriptor belongs to a different staging area.");
            }

            if (area.Prepared is not null)
            {
                if (area.Prepared == metadata)
                    return;

                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A different prepared descriptor is already committed.");
            }

            if (area.Data?.State.Status != PortableStagingItemStatus.Completed
                || area.Manifest?.State.Status != PortableStagingItemStatus.Completed)
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A prepared import requires a completed relational payload and manifest.");
            }

            if (area.MediaByIdentity.Values.Any(item => item.State.Status == PortableStagingItemStatus.Writing))
            {
                throw new PortableStagingException(
                    PortableStagingException.ConflictCode,
                    "A prepared import cannot commit while a media write is still open.");
            }

            var media = DescribeCompletedMedia(area);
            if (media.Count != metadata.MediaFiles)
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "The staged media count does not match the prepared descriptor.");
            }

            if (media.Sum(item => item.Descriptor.Length) != metadata.MediaBytes)
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "The staged media bytes do not match the prepared descriptor.");
            }

            if (metadata.Counts.MediaEntries != metadata.MediaFiles)
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "The prepared counts media entries do not match the media file count.");
            }

            var (dataBytes, dataSha256) = await PortableStagingFilePrimitives.HashFileAsync(
                    area.Data.State.FinalPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (dataBytes != metadata.DataBytes
                || !PortableArchiveValidation.FixedHashEquals(dataSha256, metadata.DataSha256))
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "The staged relational payload does not match the prepared descriptor.");
            }

            // Publish the committed descriptor durably before the instance treats the
            // area as committed, so a failed or cancelled descriptor write leaves the
            // previous (uncommitted) visible state.
            var preparedPath = PreparedPath(area);
            var preparedTempPath = preparedPath + ".tmp";
            try
            {
                await File.WriteAllTextAsync(
                        preparedTempPath,
                        JsonSerializer.Serialize(metadata, JsonOptions),
                        cancellationToken)
                    .ConfigureAwait(false);
                File.Move(preparedTempPath, preparedPath, overwrite: true);
            }
            catch
            {
                PortableStagingFilePrimitives.TryDeleteFile(preparedTempPath);
                throw;
            }

            area.Prepared = metadata;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IPreparedPortableImport> RebuildPreparedImportAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var area = GetExistingArea(stagingId);
            var prepared = area.Prepared
                ?? throw PortableStagingFilePrimitives.NotFound("No prepared descriptor has been committed.");

            var media = DescribeCompletedMedia(area);
            if (media.Count != prepared.MediaFiles
                || media.Sum(item => item.Descriptor.Length) != prepared.MediaBytes)
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "Staged media no longer matches the committed prepared descriptor.");
            }

            if (area.Data?.State.Status != PortableStagingItemStatus.Completed)
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "The staged relational payload no longer matches the committed prepared descriptor.");
            }

            var (dataBytes, dataSha256) = await PortableStagingFilePrimitives.HashFileAsync(
                    area.Data.State.FinalPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (dataBytes != prepared.DataBytes
                || !PortableArchiveValidation.FixedHashEquals(dataSha256, prepared.DataSha256))
            {
                throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "The staged relational payload no longer matches the committed prepared descriptor.");
            }

            return new PortablePreparedImport(prepared, media);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(
        PortableStagingId stagingId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (stagingId.Value == Guid.Empty)
                return;

            if (_areas.TryGetValue(stagingId.Value, out var area))
            {
                area.Deleted = true;
                area.DeactivateOpenWrites();
                _areas.Remove(stagingId.Value);
            }

            _deleted.Add(stagingId.Value);
            PortableStagingFilePrimitives.TryDeleteDirectory(AreaDirectory(stagingId));
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<PortableStagingPayloadWrite> OpenPayloadWriteAsync(
        PortableStagingId stagingId,
        PortableArchivePayload descriptor,
        bool manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var area = GetExistingArea(stagingId);
            EnsureAcceptsWrites(area);

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

            var finalPath = Path.Combine(area.Directory, manifest ? "manifest.bin" : "data.bin");
            var tempPath = finalPath + ".tmp";
            var state = PortableStagingWriteState.Open(
                OpenLocalWriteStream(tempPath),
                tempPath,
                finalPath,
                descriptor.Length);
            var item = new LocalPayloadItem
            {
                Descriptor = descriptor,
                State = state,
            };
            var write = new PortableStagingPayloadWrite(
                new PortableStagingWriteStream(
                    _gate,
                    state,
                    () => area.Deleted,
                    () => DiscardPayload(area, item, manifest)));
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
            _gate.Release();
        }
    }

    private async Task CompletePayloadAsync(
        PortableStagingId stagingId,
        PortableStagingPayloadWrite write,
        bool manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var area = GetExistingArea(stagingId);
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
                        item.Descriptor.Length,
                        item.Descriptor.Sha256,
                        flushToDisk: false)
                        .ConfigureAwait(false))
                {
                    throw new PortableStagingException(
                        PortableStagingException.IntegrityMismatchCode,
                        "Staged payload bytes do not match the descriptor bound when the write was opened.");
                }

                File.Move(item.State.TempPath, item.State.FinalPath);
                item.State.Status = PortableStagingItemStatus.Completed;
            }
            catch
            {
                DiscardPayload(area, item, manifest);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Stream> OpenPayloadReadAsync(
        PortableStagingId stagingId,
        bool manifest,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var area = GetExistingArea(stagingId);
            var item = manifest ? area.Manifest : area.Data;
            if (item?.State.Status != PortableStagingItemStatus.Completed)
                throw PortableStagingFilePrimitives.NotFound("No completed payload is available.");

            return OpenRead(item.State.FinalPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private LocalArea GetExistingArea(PortableStagingId stagingId)
    {
        if (stagingId.Value == Guid.Empty || _deleted.Contains(stagingId.Value))
            throw PortableStagingFilePrimitives.NotFound("Unknown staging area.");

        if (_areas.TryGetValue(stagingId.Value, out var area))
            return area;

        var directory = AreaDirectory(stagingId);
        if (!Directory.Exists(directory))
            throw PortableStagingFilePrimitives.NotFound("Unknown staging area.");

        area = LocalArea.Load(stagingId.Value, directory);
        _areas.Add(stagingId.Value, area);
        return area;
    }

    private static void EnsureAcceptsWrites(LocalArea area)
    {
        EnsureNotCommitted(area);
    }

    private static void EnsureNotCommitted(LocalArea area)
    {
        if (area.Prepared is not null)
        {
            throw new PortableStagingException(
                PortableStagingException.AlreadyCommittedCode,
                "The staging area has already been committed and accepts no further writes.");
        }
    }

    private static void DiscardMedia(LocalArea area, LocalMediaItem item)
    {
        if (item.State.Status == PortableStagingItemStatus.Discarded)
            return;

        item.State.Status = PortableStagingItemStatus.Discarded;
        item.State.DisposeWriter();
        PortableStagingFilePrimitives.TryDeleteFile(item.State.TempPath);
        PortableStagingFilePrimitives.TryDeleteFile(item.State.FinalPath);
        PortableStagingFilePrimitives.TryDeleteFile(MetaPath(area, item.Reference));
        area.MediaByIdentity.Remove(new PortableStagingMediaIdentity(
            item.Descriptor.BookId,
            item.Descriptor.Kind,
            item.Descriptor.Path));
        area.MediaByReference.Remove(item.Reference);
        if (item.Handle is not null)
            area.MediaByHandle.Remove(item.Handle);
    }

    private static void DiscardPayload(LocalArea area, LocalPayloadItem item, bool manifest)
    {
        if (item.State.Status == PortableStagingItemStatus.Discarded)
            return;

        item.State.Status = PortableStagingItemStatus.Discarded;
        item.State.DisposeWriter();
        PortableStagingFilePrimitives.TryDeleteFile(item.State.TempPath);
        PortableStagingFilePrimitives.TryDeleteFile(item.State.FinalPath);
        if (manifest)
            area.Manifest = null;
        else
            area.Data = null;

        if (item.Handle is not null)
            area.PayloadByHandle.Remove(item.Handle);
    }

    private static IReadOnlyList<PortablePreparedMedia> DescribeCompletedMedia(LocalArea area)
    {
        if (!Directory.Exists(area.MediaDirectory))
            return [];

        var media = new List<PortablePreparedMedia>();
        foreach (var metaPath in Directory.EnumerateFiles(area.MediaDirectory, "*.json"))
        {
            var reference = Path.GetFileNameWithoutExtension(metaPath);
            if (!File.Exists(Path.Combine(area.MediaDirectory, reference + ".bin")))
                continue;

            var descriptor = JsonSerializer.Deserialize<PortableArchiveMediaEntry>(
                    File.ReadAllText(metaPath),
                    JsonOptions)
                ?? throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "Staged media metadata is malformed.");

            media.Add(new PortablePreparedMedia(
                descriptor,
                new PortableStagedMediaReference(reference)));
        }

        return media
            .OrderBy(item => item.Descriptor.BookId)
            .ThenBy(item => item.Descriptor.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Descriptor.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private string AreaDirectory(PortableStagingId stagingId) =>
        Path.Combine(_root, stagingId.Value.ToString("N"));

    private static long? DefaultAvailableBytesProbe(string root)
    {
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(root));
        if (string.IsNullOrWhiteSpace(volumeRoot))
            return null;

        try
        {
            return new DriveInfo(volumeRoot).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return null;
        }
    }

    private static string MetaPath(LocalArea area, string reference) =>
        Path.Combine(area.MediaDirectory, reference + ".json");

    private static string PreparedPath(LocalArea area) =>
        Path.Combine(area.Directory, "prepared.json");

    private static FileStream OpenRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private sealed class LocalArea
    {
        public required Guid Id { get; init; }

        public required string Directory { get; init; }

        public string MediaDirectory => Path.Combine(Directory, "media");

        public bool Deleted { get; set; }

        public Dictionary<PortableStagingMediaIdentity, LocalMediaItem> MediaByIdentity { get; } = [];

        public Dictionary<string, LocalMediaItem> MediaByReference { get; } = new(StringComparer.Ordinal);

        public Dictionary<PortableStagingWrite, LocalMediaItem> MediaByHandle { get; } = [];

        public Dictionary<PortableStagingPayloadWrite, LocalPayloadItem> PayloadByHandle { get; } = [];

        public LocalPayloadItem? Data { get; set; }

        public LocalPayloadItem? Manifest { get; set; }

        public PreparedPortableImportMetadata? Prepared { get; set; }

        public static LocalArea Create(Guid id, string root)
        {
            var directory = Path.Combine(root, id.ToString("N"));
            System.IO.Directory.CreateDirectory(Path.Combine(directory, "media"));
            return new LocalArea { Id = id, Directory = directory };
        }

        public static LocalArea Load(Guid id, string directory)
        {
            var area = new LocalArea { Id = id, Directory = directory };

            var preparedPath = Path.Combine(directory, "prepared.json");
            if (File.Exists(preparedPath))
            {
                area.Prepared = JsonSerializer.Deserialize<PreparedPortableImportMetadata>(
                    File.ReadAllText(preparedPath),
                    JsonOptions);
            }

            area.Data = LoadPayload(directory, "data.bin");
            area.Manifest = LoadPayload(directory, "manifest.bin");
            return area;
        }

        public void DeactivateOpenWrites()
        {
            foreach (var item in MediaByIdentity.Values.ToArray())
            {
                item.State.Status = PortableStagingItemStatus.Discarded;
                item.State.DisposeWriter();
            }

            if (Data is not null)
            {
                Data.State.Status = PortableStagingItemStatus.Discarded;
                Data.State.DisposeWriter();
            }

            if (Manifest is not null)
            {
                Manifest.State.Status = PortableStagingItemStatus.Discarded;
                Manifest.State.DisposeWriter();
            }

            MediaByIdentity.Clear();
            MediaByReference.Clear();
            MediaByHandle.Clear();
            PayloadByHandle.Clear();
            Data = null;
            Manifest = null;
        }

        private static LocalPayloadItem? LoadPayload(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
                return null;

            return new LocalPayloadItem
            {
                Descriptor = new PortableArchivePayload(
                    fileName,
                    new FileInfo(path).Length,
                    string.Empty),
                State = PortableStagingWriteState.Completed(path, new FileInfo(path).Length),
            };
        }
    }

    private sealed class LocalMediaItem
    {
        public required PortableArchiveMediaEntry Descriptor { get; init; }

        public required string Reference { get; init; }

        public required PortableStagingWriteState State { get; init; }

        public PortableStagingWrite? Handle { get; set; }
    }

    private sealed class LocalPayloadItem
    {
        public required PortableArchivePayload Descriptor { get; init; }

        public required PortableStagingWriteState State { get; init; }

        public PortableStagingPayloadWrite? Handle { get; set; }
    }

    private static FileStream OpenLocalWriteStream(string tempPath) =>
        new(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}
