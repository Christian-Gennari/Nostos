using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Completely validates a portable archive from a position-independent source and stages
/// its relational payload, manifest and every media entry into
/// <see cref="IPortableImportStaging"/>.
/// </summary>
/// <remarks>
/// Preparation never mutates the active library, the database or
/// <c>IBookAssetStorage</c>: the returned <see cref="PortablePreparedImport"/> only claims
/// that the archive was fully validated and its bytes were copied into host staging.
/// All archive reads go through the prefetched native reader, so the underlying source
/// observes no synchronous reads and only bounded ranges. On any failure or cancellation
/// the staging area is deleted before the typed error is rethrown.
/// </remarks>
internal sealed class PortableArchiveReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly ILogger<PortableArchiveReader> _logger;
    private readonly TimeProvider _timeProvider;

    public PortableArchiveReader(
        ILogger<PortableArchiveReader>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<PortableArchiveReader>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<PortablePreparedImport> PrepareImportAsync(
        IPortableArchiveSource source,
        IPortableImportStaging staging,
        IProgress<PortableArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        PrepareImportAsync(
            source,
            staging,
            progress,
            cancellationToken,
            resourceBudget: null);

    /// <summary>
    /// Test seam: an explicit buffer budget lets a test observe the high-water mark of
    /// one preparation. Production callers use the per-operation default.
    /// </summary>
    internal async Task<PortablePreparedImport> PrepareImportAsync(
        IPortableArchiveSource source,
        IPortableImportStaging staging,
        IProgress<PortableArchiveProgress>? progress,
        CancellationToken cancellationToken,
        PortableArchiveBufferBudget? resourceBudget)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(staging);

        // The actual source length is authoritative. Reject an over-limit or empty
        // source before creating staging or reading the first archive byte.
        PortableArchiveValidation.ValidateArchiveSize(source.Length);
        if (source.Length == 0)
        {
            throw new PortableArchiveException(
                "empty_archive",
                "Portable archive is empty.");
        }

        var budget = resourceBudget
            ?? new PortableArchiveBufferBudget(PortableArchiveLimits.MaxExplicitBufferBytes);
        var reporter = new PortableArchiveProgressReporter(progress, _timeProvider);

        var stagingId = await staging.CreateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PrepareCoreAsync(
                source,
                staging,
                stagingId,
                budget,
                reporter,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PortableArchiveException)
        {
            await DeleteStagingQuietlyAsync(staging, stagingId).ConfigureAwait(false);
            throw;
        }
        catch (PortableStagingException)
        {
            await DeleteStagingQuietlyAsync(staging, stagingId).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await DeleteStagingQuietlyAsync(staging, stagingId).ConfigureAwait(false);
            throw;
        }
        catch (InvalidDataException exception)
        {
            await DeleteStagingQuietlyAsync(staging, stagingId).ConfigureAwait(false);
            throw new PortableArchiveException(
                "invalid_zip",
                "Portable archive entry data is not a valid ZIP stream.",
                exception);
        }
        catch (Exception exception)
        {
            await DeleteStagingQuietlyAsync(staging, stagingId).ConfigureAwait(false);
            throw new PortableArchiveException(
                "import_failed",
                "Portable archive preparation failed. The staging area was deleted.",
                exception);
        }
    }

    private async Task<PortablePreparedImport> PrepareCoreAsync(
        IPortableArchiveSource source,
        IPortableImportStaging staging,
        PortableStagingId stagingId,
        PortableArchiveBufferBudget budget,
        PortableArchiveProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        reporter.Report(
            PortableArchiveProgressPhase.InspectingArchive,
            bytesProcessed: 0,
            totalBytes: source.Length,
            force: true);

        await using var reader = await PortableArchiveZipReader
            .OpenAsync(source, budget, cancellationToken)
            .ConfigureAwait(false);

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in reader.Archive.Entries)
            entries.Add(entry.FullName, entry);

        if (!entries.TryGetValue(PortableArchiveFormat.ManifestPath, out var manifestEntry))
            PortableArchiveValidation.ValidateManifestEntryFound(false);

        var manifestBytes = await ReadBoundedEntryBytesAsync(
            manifestEntry!,
            PortableArchiveLimits.MaxManifestBytes,
            budget,
            cancellationToken).ConfigureAwait(false);

        var manifest = PortableArchiveValidation.Deserialize<PortableArchiveManifest>(
            manifestBytes,
            "malformed_manifest",
            "Portable archive manifest is malformed.",
            JsonOptions);

        PortableArchiveValidation.ValidateManifest(manifest);

        await StageManifestAsync(
            staging,
            stagingId,
            manifestBytes,
            cancellationToken).ConfigureAwait(false);

        if (!entries.TryGetValue(manifest.Data.Path, out var dataEntry))
            PortableArchiveValidation.ValidateDataEntryFound(manifest.Data.Path, false);

        PortableArchiveValidation.ValidateDataEntryLength(dataEntry!.Length, manifest.Data.Length);

        var (dataLength, dataSha256) = await StageDataAsync(
            staging,
            stagingId,
            dataEntry,
            manifest,
            budget,
            reporter,
            cancellationToken).ConfigureAwait(false);

        PortableLibraryData data;
        await using (var stagedData = await staging
            .OpenDataReadAsync(stagingId, cancellationToken)
            .ConfigureAwait(false))
        {
            data = await DeserializeAsync<PortableLibraryData>(
                stagedData,
                "malformed_data",
                "Portable archive relational payload is malformed.",
                cancellationToken).ConfigureAwait(false);
        }

        PortableArchiveValidation.ValidateDataVersionAgreement(manifest.DataVersion, data.Version);
        PortableArchiveValidation.ValidatePortableData(data);
        PortableArchiveValidation.ValidateManifestCounts(CountsFor(data), manifest.Counts);
        PortableArchiveValidation.ValidateMediaManifest(manifest, data);

        // Local capacity admission (compatibility staging): after every manifest and
        // relational guard has passed, before the archive inventory and any media
        // staging, so that a volume that cannot hold the staged bytes fails with the
        // legacy typed space errors instead of being driven to exhaustion.
        EnsureStagingCapacity(staging, manifest, dataLength, manifestBytes.LongLength);

        PortableArchiveValidation.ValidateArchiveInventory(manifest, entries.Keys);

        var media = await StageMediaAsync(
            staging,
            stagingId,
            manifest,
            entries,
            budget,
            reporter,
            source.Length,
            cancellationToken).ConfigureAwait(false);

        var metadata = new PreparedPortableImportMetadata(
            stagingId,
            manifest.FormatVersion,
            manifest.DataVersion,
            dataLength,
            dataSha256,
            PortableLibraryCounts.ComputeCounts(data, media.Count),
            MediaFiles: media.Count,
            MediaBytes: manifest.Media.Sum(item => item.Length),
            ArchiveBytes: source.Length,
            PreparedAtUtc: _timeProvider.GetUtcNow().UtcDateTime,
            IntegrityVerified: true);

        // Every item is complete and verified; commit is the only durable boundary.
        await staging
            .CommitPreparedImportAsync(stagingId, metadata, cancellationToken)
            .ConfigureAwait(false);

        reporter.Report(
            PortableArchiveProgressPhase.Prepared,
            metadata.MediaBytes,
            metadata.MediaBytes,
            itemsProcessed: media.Count,
            totalItems: media.Count,
            force: true);

        return new PortablePreparedImport(metadata, media);
    }

    private static async Task StageManifestAsync(
        IPortableImportStaging staging,
        PortableStagingId stagingId,
        byte[] manifestBytes,
        CancellationToken cancellationToken)
    {
        var descriptor = new PortableArchivePayload(
            PortableArchiveFormat.ManifestPath,
            manifestBytes.LongLength,
            Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());

        await using var write = await staging
            .OpenManifestWriteAsync(stagingId, descriptor, cancellationToken)
            .ConfigureAwait(false);

        await write.Stream
            .WriteAsync(manifestBytes, cancellationToken)
            .ConfigureAwait(false);

        await staging
            .CompleteManifestAsync(stagingId, write, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<(long Length, string Sha256)> StageDataAsync(
        IPortableImportStaging staging,
        PortableStagingId stagingId,
        ZipArchiveEntry dataEntry,
        PortableArchiveManifest manifest,
        PortableArchiveBufferBudget budget,
        PortableArchiveProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        PortableArchiveValidation.ValidateDeclaredReadSize(
            dataEntry.FullName,
            dataEntry.Length,
            PortableArchiveLimits.MaxDataBytes);

        reporter.Report(
            PortableArchiveProgressPhase.ValidatingData,
            bytesProcessed: 0,
            totalBytes: manifest.Data.Length,
            force: true);

        var declaredLength = manifest.Data.Length;
        await using var write = await staging
            .OpenDataWriteAsync(stagingId, manifest.Data, cancellationToken)
            .ConfigureAwait(false);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var lease = await budget
            .RentAsync(PortableArchiveLimits.CopyBufferBytes, cancellationToken)
            .ConfigureAwait(false);
        await using var input = await dataEntry
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        long total = 0;
        while (true)
        {
            var read = await input
                .ReadAsync(lease.Memory, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            total = checked(total + read);
            PortableArchiveValidation.ValidateObservedReadSize(
                dataEntry.FullName,
                total,
                PortableArchiveLimits.MaxDataBytes);

            // The actual byte count is checked against the manifest before the hash
            // comparison, which is the gap the immediate import cannot close.
            if (total > declaredLength)
            {
                throw new PortableArchiveException(
                    "data_length_mismatch",
                    "Portable archive relational payload length does not match its manifest.");
            }

            hash.AppendData(lease.Memory.Span[..read]);
            await write.Stream
                .WriteAsync(lease.Memory[..read], cancellationToken)
                .ConfigureAwait(false);

            reporter.Report(
                PortableArchiveProgressPhase.ValidatingData,
                total,
                declaredLength);
        }

        PortableArchiveValidation.ValidateDataEntryLength(total, declaredLength);

        var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        PortableArchiveValidation.ValidateDataHash(sha256, manifest.Data.Sha256);

        await staging
            .CompleteDataAsync(stagingId, write, cancellationToken)
            .ConfigureAwait(false);

        reporter.Report(
            PortableArchiveProgressPhase.ValidatingData,
            total,
            declaredLength,
            force: true);

        return (total, sha256);
    }

    private static void EnsureStagingCapacity(
        IPortableImportStaging staging,
        PortableArchiveManifest manifest,
        long dataBytes,
        long manifestBytes)
    {
        if (staging is not IPortableStagingCapacityAdmission admission)
            return;

        admission.EnsureCapacity(checked(
            manifest.Media.Sum(item => item.Length) + dataBytes + manifestBytes));
    }

    private static async Task<IReadOnlyList<PortablePreparedMedia>> StageMediaAsync(
        IPortableImportStaging staging,
        PortableStagingId stagingId,
        PortableArchiveManifest manifest,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        PortableArchiveBufferBudget budget,
        PortableArchiveProgressReporter reporter,
        long archiveBytes,
        CancellationToken cancellationToken)
    {
        var media = new List<PortablePreparedMedia>(manifest.Media.Count);
        long mediaBytesCompleted = 0;

        for (var index = 0; index < manifest.Media.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var descriptor = manifest.Media[index];
            var entry = entries[descriptor.Path];

            PortableArchiveValidation.ValidateMediaEntryLength(
                descriptor.Path,
                entry.Length,
                descriptor.Length);

            reporter.Report(
                PortableArchiveProgressPhase.StagingMedia,
                mediaBytesCompleted,
                archiveBytes,
                itemsProcessed: index,
                totalItems: manifest.Media.Count,
                force: true);

            await using var write = await staging
                .OpenMediaWriteAsync(stagingId, descriptor, cancellationToken)
                .ConfigureAwait(false);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var lease = await budget
                .RentAsync(PortableArchiveLimits.CopyBufferBytes, cancellationToken)
                .ConfigureAwait(false);
            await using (var input = await entry
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                long total = 0;
                while (true)
                {
                    var read = await input
                        .ReadAsync(lease.Memory, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;

                    total = checked(total + read);
                    PortableArchiveValidation.ValidateCopiedMediaSize(total, descriptor.Length);

                    hash.AppendData(lease.Memory.Span[..read]);
                    await write.Stream
                        .WriteAsync(lease.Memory[..read], cancellationToken)
                        .ConfigureAwait(false);

                    reporter.Report(
                        PortableArchiveProgressPhase.StagingMedia,
                        mediaBytesCompleted + total,
                        archiveBytes,
                        itemsProcessed: index,
                        totalItems: manifest.Media.Count);
                }

                var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                PortableArchiveValidation.ValidateMediaHash(
                    descriptor.Path,
                    total,
                    descriptor.Length,
                    sha256,
                    descriptor.Sha256);

                await staging
                    .CompleteMediaAsync(stagingId, write, cancellationToken)
                    .ConfigureAwait(false);

                mediaBytesCompleted += total;
                if (mediaBytesCompleted > PortableArchiveLimits.MaxUncompressedBytes)
                {
                    throw new PortableArchiveException(
                        "archive_expands_too_large",
                        "Portable archive declares too much uncompressed data.");
                }
            }

            media.Add(new PortablePreparedMedia(descriptor, write.Reference));

            reporter.Report(
                PortableArchiveProgressPhase.StagingMedia,
                mediaBytesCompleted,
                archiveBytes,
                itemsProcessed: index + 1,
                totalItems: manifest.Media.Count,
                force: true);
        }

        return media;
    }

    private static async Task<byte[]> ReadBoundedEntryBytesAsync(
        ZipArchiveEntry entry,
        long maxBytes,
        PortableArchiveBufferBudget budget,
        CancellationToken cancellationToken)
    {
        PortableArchiveValidation.ValidateDeclaredReadSize(
            entry.FullName,
            entry.Length,
            maxBytes);

        using var output = new MemoryStream(checked((int)Math.Min(entry.Length, maxBytes)));
        await using var lease = await budget
            .RentAsync(PortableArchiveLimits.CopyBufferBytes, cancellationToken)
            .ConfigureAwait(false);
        await using var input = await entry
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        long total = 0;
        while (true)
        {
            var read = await input
                .ReadAsync(lease.Memory, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            total = checked(total + read);
            PortableArchiveValidation.ValidateObservedReadSize(
                entry.FullName,
                total,
                maxBytes);

            await output
                .WriteAsync(lease.Memory[..read], cancellationToken)
                .ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private static async Task<T> DeserializeAsync<T>(
        Stream source,
        string code,
        string message,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await JsonSerializer
                .DeserializeAsync<T>(source, JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new PortableArchiveException(code, message);
        }
        catch (PortableArchiveException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
            or NotSupportedException)
        {
            throw new PortableArchiveException(code, message, exception);
        }
    }

    private static PortableArchiveCounts CountsFor(PortableLibraryData data) =>
        new(
            data.Works.Count,
            data.Books.Count,
            data.Collections.Count,
            data.BookCollections.Count,
            data.Notes.Count,
            data.Topics.Count,
            data.NoteTopics.Count,
            data.Writings.Count,
            data.BookAcquisitions.Count);

    private async Task DeleteStagingQuietlyAsync(
        IPortableImportStaging staging,
        PortableStagingId stagingId)
    {
        try
        {
            await staging
                .DeleteAsync(stagingId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Cleanup failure is logged but must never replace the original failure
            // or cancellation that the caller is about to observe.
            _logger.LogWarning(
                "Portable archive preparation could not delete staging {StagingId}; exception type {ExceptionType}.",
                stagingId.Value,
                exception.GetType().Name);
        }
    }
}

/// <summary>
/// Throttles archive progress callbacks to at most one per 8 MiB or 500 ms while
/// always delivering phase boundaries and per-entry updates.
/// </summary>
internal sealed class PortableArchiveProgressReporter
{
    private const long MinByteInterval = 8L * 1024L * 1024L;
    private static readonly long MinTimeIntervalTicks = TimeSpan.FromMilliseconds(500).Ticks;

    private readonly IProgress<PortableArchiveProgress>? _progress;
    private readonly TimeProvider _timeProvider;
    private long _lastReportedBytes;
    private long _lastReportedTicks;

    public PortableArchiveProgressReporter(
        IProgress<PortableArchiveProgress>? progress,
        TimeProvider timeProvider)
    {
        _progress = progress;
        _timeProvider = timeProvider;
    }

    public void Report(
        PortableArchiveProgressPhase phase,
        long bytesProcessed,
        long? totalBytes,
        int itemsProcessed = 0,
        int? totalItems = null,
        string? message = null,
        bool force = false)
    {
        if (_progress is null)
            return;

        var now = _timeProvider.GetUtcNow().UtcTicks;
        if (!force
            && bytesProcessed - _lastReportedBytes < MinByteInterval
            && now - _lastReportedTicks < MinTimeIntervalTicks)
        {
            return;
        }

        _lastReportedBytes = bytesProcessed;
        _lastReportedTicks = now;

        _progress.Report(new PortableArchiveProgress(
            phase,
            bytesProcessed,
            totalBytes,
            itemsProcessed,
            totalItems,
            message));
    }
}
