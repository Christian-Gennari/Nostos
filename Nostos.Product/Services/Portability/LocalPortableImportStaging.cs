using System.Text.Json;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Test-only seams for the process-local staging provider: deterministic
/// interleaving before the staging gate, so a test can prove how two concurrent
/// operations queue. Production instances pass none of them.
/// </summary>
internal sealed class LocalPortableImportStagingHooks
{
    internal Func<PortableStagingOperation, Task>? BeforeAreaGateAsync { get; set; }
}

/// <summary>
/// Process-local file staging for the immediate compatibility import
/// (<c>POST /api/portability/import</c>). Every staged byte lives under a
/// caller-owned disposable scratch root; disposing the provider releases no files and
/// only <see cref="IPortableImportStaging.DeleteAsync"/> (or deleting the root) removes
/// staged bytes.
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
/// <para>
/// The open/complete/commit/rebuild/delete state machine is shared with the durable
/// provider through <see cref="PortableImportStagingBase"/>; this type supplies only
/// its persistence differences: one scratch root, no inventory file (completed media
/// are discovered from their descriptor sidecars), no fsync, no deletion tombstone.
/// </para>
/// </remarks>
internal sealed class LocalPortableImportStaging : PortableImportStagingBase
{
    private const int StreamBufferBytes = 128 * 1024;

    private readonly string _root;
    private readonly Func<string, long?> _availableBytesProbe;
    private readonly LocalPortableImportStagingHooks? _hooks;
    private readonly PortableStagingGate _gate = new();
    private readonly Dictionary<Guid, LocalArea> _areas = [];
    private readonly HashSet<Guid> _deleted = [];

    public LocalPortableImportStaging(string root)
        : this(root, availableBytesProbe: null)
    {
    }

    internal LocalPortableImportStaging(
        string root,
        Func<string, long?>? availableBytesProbe)
        : this(root, availableBytesProbe, hooks: null)
    {
    }

    /// <summary>
    /// Test seam: a hook that runs immediately before an operation takes the staging
    /// gate. Production always passes none.
    /// </summary>
    internal LocalPortableImportStaging(
        string root,
        Func<string, long?>? availableBytesProbe,
        LocalPortableImportStagingHooks? hooks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _availableBytesProbe = availableBytesProbe ?? DefaultAvailableBytesProbe;
        _hooks = hooks;
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

    protected override Func<PortableStagingOperation, Task>? BeforeAreaGate =>
        _hooks?.BeforeAreaGateAsync;

    protected override bool FlushCompletedItemsToDisk => false;

    public override async Task<PortableStagingId> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        using var lease = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid();
        var area = LocalArea.Create(id, _root, _gate);
        _areas.Add(id, area);
        return new PortableStagingId(id);
    }

    protected override async ValueTask<PortableStagingAreaLease> EnterAreaAsync(
        PortableStagingId stagingId,
        PortableStagingOperation operation,
        CancellationToken cancellationToken)
    {
        var lease = await EnterGateAsync(_gate, operation, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new PortableStagingAreaLease(GetExistingArea(stagingId), lease);
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
        var lease = await EnterGateAsync(_gate, operation, cancellationToken)
            .ConfigureAwait(false);

        // Delete is idempotent, so an area this instance never touched resolves to an
        // empty area over the same directory: the tree still goes away and the id is
        // still marked deleted.
        var area = _areas.TryGetValue(stagingId.Value, out var existing)
            ? existing
            : LocalArea.Detached(stagingId.Value, _root, _gate);
        return new PortableStagingAreaLease(area, lease);
    }

    protected override FileStream CreateScratchFile(string finalPath, out string tempPath)
    {
        tempPath = finalPath + ".tmp";
        return new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    protected override string MediaPath(PortableStagingArea area, string reference) =>
        Path.Combine(AsLocal(area).MediaDirectory, reference + ".bin");

    protected override string PayloadPath(PortableStagingArea area, bool manifest) =>
        Path.Combine(area.Directory, manifest ? "manifest.bin" : "data.bin");

    /// <summary>The area's media directory is created with the area itself.</summary>
    protected override void EnsureMediaDirectory(PortableStagingArea area)
    {
    }

    protected override async ValueTask PublishMediaAsync(
        PortableStagingArea area,
        PortableStagingMediaItem item,
        CancellationToken cancellationToken)
    {
        var local = AsLocal(area);

        // The descriptor sidecar is what a later instance discovers, so it is written
        // before the bytes are published: a crash between the two leaves a descriptor
        // without bytes, which discovery skips, never bytes without a descriptor.
        await File.WriteAllTextAsync(
                MetaPath(local, item.Reference),
                JsonSerializer.Serialize(item.Descriptor, PortableArchiveJson.StagingOptions),
                cancellationToken)
            .ConfigureAwait(false);
        File.Move(item.State.TempPath, item.State.FinalPath);
    }

    protected override ValueTask PublishPayloadAsync(
        PortableStagingArea area,
        PortableStagingPayloadItem item,
        bool manifest,
        CancellationToken cancellationToken)
    {
        File.Move(item.State.TempPath, item.State.FinalPath);
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask PublishPreparedAsync(
        PortableStagingArea area,
        PreparedPortableImportMetadata metadata,
        CancellationToken cancellationToken)
    {
        var preparedPath = PreparedPath(area);
        var preparedTempPath = preparedPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                    preparedTempPath,
                    JsonSerializer.Serialize(metadata, PortableArchiveJson.StagingOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(preparedTempPath, preparedPath, overwrite: true);
        }
        catch
        {
            PortableStagingFilePrimitives.TryDeleteFile(preparedTempPath);
            throw;
        }
    }

    protected override IReadOnlyList<PortablePreparedMedia> DescribeCompletedMedia(
        PortableStagingArea area) =>
        ReadCompletedMedia(AsLocal(area));

    protected override ValueTask<IReadOnlyList<PortablePreparedMedia>>
        DescribeCompletedMediaForRebuildAsync(
            PortableStagingArea area,
            CancellationToken cancellationToken) =>
        ValueTask.FromResult(DescribeCompletedMedia(area));

    protected override async ValueTask<(long Length, string Sha256)> HashCompletedPayloadAsync(
        PortableStagingArea area,
        PortableStagingPayloadItem item,
        CancellationToken cancellationToken) =>
        await PortableStagingFilePrimitives
            .HashFileAsync(item.State.FinalPath, cancellationToken)
            .ConfigureAwait(false);

    protected override Stream OpenCompletedMediaRead(
        PortableStagingArea area,
        string reference)
    {
        var finalPath = Path.Combine(AsLocal(area).MediaDirectory, reference + ".bin");
        if (!File.Exists(finalPath))
            throw PortableStagingFilePrimitives.NotFound("No completed media item matches the reference.");

        return OpenRead(finalPath);
    }

    protected override Stream OpenCompletedPayloadRead(
        PortableStagingArea area,
        PortableStagingPayloadItem item,
        bool manifest) =>
        OpenRead(item.State.FinalPath);

    protected override void OnMediaDiscarded(
        PortableStagingArea area,
        PortableStagingMediaItem item) =>
        PortableStagingFilePrimitives.TryDeleteFile(MetaPath(AsLocal(area), item.Reference));

    protected override void OnAreaDeactivated(PortableStagingArea area)
    {
        _areas.Remove(area.StagingId);
        _deleted.Add(area.StagingId);
    }

    private static LocalArea AsLocal(PortableStagingArea area) => (LocalArea)area;

    private LocalArea GetExistingArea(PortableStagingId stagingId)
    {
        if (stagingId.Value == Guid.Empty || _deleted.Contains(stagingId.Value))
            throw PortableStagingFilePrimitives.NotFound("Unknown staging area.");

        if (_areas.TryGetValue(stagingId.Value, out var area))
            return area;

        var directory = AreaDirectory(stagingId);
        if (!Directory.Exists(directory))
            throw PortableStagingFilePrimitives.NotFound("Unknown staging area.");

        area = LocalArea.Load(stagingId.Value, directory, _gate);
        _areas.Add(stagingId.Value, area);
        return area;
    }

    /// <summary>
    /// Discovers the completed media of the area from its descriptor sidecars. A
    /// descriptor without its bytes is a crash leftover and is skipped; a malformed
    /// descriptor is a typed integrity failure rather than a silent omission.
    /// </summary>
    private static IReadOnlyList<PortablePreparedMedia> ReadCompletedMedia(LocalArea area)
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
                    PortableArchiveJson.StagingOptions)
                ?? throw new PortableStagingException(
                    PortableStagingException.IntegrityMismatchCode,
                    "Staged media metadata is malformed.");

            media.Add(new PortablePreparedMedia(
                descriptor,
                new PortableStagedMediaReference(reference)));
        }

        return media;
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

    private static string PreparedPath(PortableStagingArea area) =>
        Path.Combine(area.Directory, "prepared.json");

    private static FileStream OpenRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            StreamBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private sealed class LocalArea : PortableStagingArea
    {
        internal LocalArea(Guid id, string directory, PortableStagingGate gate)
            : base(id, directory, gate)
        {
        }

        internal string MediaDirectory => Path.Combine(Directory, "media");

        internal static LocalArea Create(Guid id, string root, PortableStagingGate gate)
        {
            var area = Detached(id, root, gate);
            System.IO.Directory.CreateDirectory(area.MediaDirectory);
            return area;
        }

        /// <summary>
        /// The directory an id maps to without touching the filesystem: used by
        /// creation and by an idempotent delete of an area this instance never knew.
        /// </summary>
        internal static LocalArea Detached(Guid id, string root, PortableStagingGate gate) =>
            new(id, Path.Combine(root, id.ToString("N")), gate);

        internal static LocalArea Load(Guid id, string directory, PortableStagingGate gate)
        {
            var area = new LocalArea(id, directory, gate);

            var preparedPath = Path.Combine(directory, "prepared.json");
            if (File.Exists(preparedPath))
            {
                area.Prepared = JsonSerializer.Deserialize<PreparedPortableImportMetadata>(
                    File.ReadAllText(preparedPath),
                    PortableArchiveJson.StagingOptions);
            }

            area.Data = LoadPayload(directory, "data.bin");
            area.Manifest = LoadPayload(directory, "manifest.bin");
            return area;
        }

        private static PortableStagingPayloadItem? LoadPayload(string directory, string fileName)
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
                return null;

            var length = new FileInfo(path).Length;
            return new PortableStagingPayloadItem
            {
                Length = length,
                Sha256 = string.Empty,
                State = PortableStagingWriteState.Completed(path, length),
            };
        }
    }
}
