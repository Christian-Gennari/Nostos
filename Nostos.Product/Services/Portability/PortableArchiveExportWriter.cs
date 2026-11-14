using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Streaming export writer: captures one consistent relational snapshot, pins every
/// referenced media object by length/last-modified/entity-tag/SHA-256, preflights the
/// relational JSON, and then writes the ZIP.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is written until the snapshot, the media pin and the relational
/// preflight have all succeeded, so a failure that can be detected up front never
/// produces a destination byte. The archive copy pass re-verifies the pin, so an
/// archive can never mix two media revisions.
/// </para>
/// <para>
/// The manifest is written last because it carries the relational digest and every
/// pinned media digest. Aborting (rather than finalizing) is what keeps a failed
/// export from publishing a central directory for an incomplete archive.
/// </para>
/// </remarks>
internal sealed class PortableArchiveExportWriter(
    NostosDbContext db,
    IBookAssetStorage assets,
    IBookTrackStorage? trackStorage,
    TimeProvider timeProvider)
{
    private readonly NostosDbContext _db = db;
    private readonly IBookAssetStorage _assets = assets;
    private readonly IBookTrackStorage? _tracks = trackStorage;
    private readonly TimeProvider _timeProvider = timeProvider;

    internal static PortableArchiveBufferBudget CreateBufferBudget() =>
        new(PortableArchiveLimits.MaxExplicitBufferBytes);

    internal async Task<PortableExportResult> ExportAsync(
        Stream destination,
        IProgress<PortableArchiveProgress>? progress,
        PortableArchiveBufferBudget bufferBudget,
        long maxExportDataBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(bufferBudget);
        if (!destination.CanWrite)
            throw new ArgumentException("The export destination must be writable.", nameof(destination));

        progress?.Report(new PortableArchiveProgress(
            PortableArchiveProgressPhase.Snapshotting,
            0,
            null));

        var snapshot = await CaptureSnapshotAsync(cancellationToken);
        PortableArchiveValidation.ValidatePortableData(snapshot.Data);
        var counts = snapshot.Counts;

        // Media is pinned by an initial hash pass after the relational
        // transaction has closed. The archive copy pass inside
        // WriteArchiveAsync re-verifies the pin so an archive can never mix two
        // media revisions. The pin pass and the relational JSON preflight both
        // run before the archive is opened, so every failure that can be
        // detected up front (snapshot, missing media, oversized or
        // unserializable relational data) happens before the first destination
        // byte is written.
        progress?.Report(new PortableArchiveProgress(
            PortableArchiveProgressPhase.IndexingMedia,
            0,
            null,
            0,
            snapshot.Media.Count));
        var pinned = await PinSourceMediaAsync(snapshot, cancellationToken);

        var dataPreflight = await PreflightDataJsonAsync(
            snapshot.Data,
            maxExportDataBytes,
            cancellationToken);

        var media = new List<PortableArchiveMediaEntry>(pinned.Count);

        // Native ZipArchive finalization performs small synchronous writes
        // (data descriptors, Deflate purge, central directory). The bounded
        // capture sink absorbs them in memory and drains them with
        // asynchronous destination writes, so the destination never sees
        // synchronous IO. Native Create mode over this non-seekable view also
        // means entries carry ZIP data descriptors, which is the standard
        // streaming framing and is handled by native readers and historical
        // Nostos import.
        var buffered = new BoundedSynchronousCaptureSink(destination, bufferBudget, leaveOpen: true);
        try
        {
            using var copyBuffer = bufferBudget.Rent(
                PortableArchiveLimits.CopyBufferBytes,
                cancellationToken);

            await WriteArchiveAsync(
                buffered,
                snapshot,
                pinned,
                dataPreflight,
                media,
                copyBuffer.Memory,
                maxExportDataBytes,
                progress,
                cancellationToken);

            await buffered.CompleteAsync(cancellationToken);
            await buffered.DisposeAsync();

            return new PortableExportResult(
                PortableArchiveFormat.Version,
                counts,
                media.Count,
                media.Sum(x => x.Length));
        }
        catch
        {
            // Output may already have been streamed. Abort the capture sink so
            // native ZIP finalization cannot publish a central directory for an
            // incomplete archive, then release the capture lease and rethrow
            // the original failure. A truncated download must never be
            // mistaken for a complete export.
            buffered.Abort();
            await DisposeQuietlyAsync(buffered);
            throw;
        }
    }

    // Serializes the relational snapshot once into a counting/hashing null
    // sink, so the size limit is enforced before the archive is opened and
    // before any destination byte is written. This stores nothing: no temp
    // file and no whole-JSON buffer. The archive pass must reproduce the same
    // length and SHA-256 exactly.
    private static async Task<(long Length, string Sha256)> PreflightDataJsonAsync(
        PortableLibraryData data,
        long maxDataBytes,
        CancellationToken cancellationToken)
    {
        await using var preflight = new HashingWriteStream(
            Stream.Null,
            maxDataBytes,
            "data_too_large",
            $"Portable relational data exceeds the {maxDataBytes} byte v1 limit.");
        await JsonSerializer.SerializeAsync(
            preflight,
            data,
            PortableArchiveJson.Options,
            cancellationToken);
        return preflight.Complete();
    }

    private async Task<(long Length, string Sha256)> WriteArchiveAsync(
        BoundedSynchronousCaptureSink buffered,
        PortableExportSnapshot snapshot,
        IReadOnlyList<PinnedPortableSourceMedia> pinned,
        (long Length, string Sha256) dataPreflight,
        List<PortableArchiveMediaEntry> media,
        Memory<byte> copyBuffer,
        long maxExportDataBytes,
        IProgress<PortableArchiveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var archive = await ZipArchive.CreateAsync(
            buffered,
            ZipArchiveMode.Create,
            leaveOpen: true,
            entryNameEncoding: null,
            cancellationToken);

        try
        {
            var dataEntry = archive.CreateEntry(
                PortableArchiveFormat.DataPath,
                CompressionLevel.Optimal);
            long dataLength;
            string dataSha256;
            var dataStream = await dataEntry.OpenAsync(cancellationToken);
            try
            {
                await using var hashing = new HashingWriteStream(
                    dataStream,
                    maxExportDataBytes,
                    "data_too_large",
                    $"Portable relational data exceeds the {maxExportDataBytes} byte v1 limit.");
                await JsonSerializer.SerializeAsync(
                    hashing,
                    snapshot.Data,
                    PortableArchiveJson.Options,
                    cancellationToken);
                (dataLength, dataSha256) = hashing.Complete();
                PortableArchiveValidation.ValidateStreamedDataMatchesPreflight(
                    dataLength,
                    dataSha256,
                    dataPreflight.Length,
                    dataPreflight.Sha256);
            }
            catch
            {
                await DisposeQuietlyAsync(dataStream);
                throw;
            }

            await dataStream.DisposeAsync();

            var totalItems = pinned.Count + 2;
            progress?.Report(new PortableArchiveProgress(
                PortableArchiveProgressPhase.WritingArchive,
                0,
                null,
                1,
                totalItems));

            long mediaBytes = 0;
            foreach (var item in pinned)
            {
                media.Add(await AppendPinnedAssetAsync(
                    archive,
                    item,
                    copyBuffer,
                    cancellationToken));
                mediaBytes = checked(mediaBytes + item.Length);
                progress?.Report(new PortableArchiveProgress(
                    PortableArchiveProgressPhase.WritingArchive,
                    mediaBytes,
                    null,
                    media.Count + 1,
                    totalItems));
            }

            // The manifest is written last because it carries the data hash
            // and every pinned media hash.
            var manifest = new PortableArchiveManifest(
                Format: PortableArchiveFormat.Name,
                FormatVersion: PortableArchiveFormat.Version,
                DataVersion: PortableArchiveFormat.DataVersion,
                ExportedAtUtc: snapshot.SnapshotAtUtc,
                ApplicationVersion:
                    typeof(PortableArchiveService).Assembly.GetName().Version?.ToString()
                    ?? "unknown",
                Counts: snapshot.Counts,
                Data: new PortableArchivePayload(
                    PortableArchiveFormat.DataPath,
                    dataLength,
                    dataSha256),
                Media: media);

            var manifestEntry = archive.CreateEntry(
                PortableArchiveFormat.ManifestPath,
                CompressionLevel.Optimal);
            var manifestStream = await manifestEntry.OpenAsync(cancellationToken);
            try
            {
                await using var bounded = new HashingWriteStream(
                    manifestStream,
                    PortableArchiveLimits.MaxManifestBytes,
                    "entry_too_large",
                    $"Portable archive entry '{PortableArchiveFormat.ManifestPath}' exceeds its v1 limit.");
                await JsonSerializer.SerializeAsync(
                    bounded,
                    manifest,
                    PortableArchiveJson.Options,
                    cancellationToken);
            }
            catch
            {
                await DisposeQuietlyAsync(manifestStream);
                throw;
            }

            await manifestStream.DisposeAsync();

            // The manifest is the last counted entry; without this report a
            // finished export stays one item short of its own total.
            progress?.Report(new PortableArchiveProgress(
                PortableArchiveProgressPhase.WritingArchive,
                mediaBytes,
                null,
                totalItems,
                totalItems));

            await archive.DisposeAsync();
            return (dataLength, dataSha256);
        }
        catch
        {
            // Abort before finalizing: a failed export must not publish a
            // central directory for an incomplete archive. The caller repeats
            // the abort and releases the capture lease.
            buffered.Abort();
            await DisposeQuietlyAsync(archive);
            throw;
        }
    }

    private async Task<PortableExportSnapshot> CaptureSnapshotAsync(CancellationToken ct)
    {
        // One serializable read transaction owns every relational query for the
        // export. Media is never read, hashed or copied while it is open.
        await using var transaction = await _db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            ct);

        // The timestamp is taken only after the transaction has been acquired:
        // acquiring it can wait on an active writer, and the manifest must
        // never claim an earlier revision than the rows that were read.
        var snapshotAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        // The projection is shared with the recovery source, so an export and a
        // recovery payload can never describe the same library differently.
        var data = await PortableLibrarySnapshot.ReadAsync(
            _db,
            ExportBookMedia,
            ct);

        var media = new List<PortableSourceMedia>();
        foreach (var book in data.Books)
        {
            if (book.HasBookFile)
            {
                media.Add(new PortableSourceMedia(
                    book.Id,
                    PortableArchiveFormat.BookMediaKind));
            }

            foreach (var track in BookTrackList.Parse(book.TracksJson))
            {
                media.Add(new PortableSourceMedia(
                    book.Id,
                    PortableArchiveFormat.TrackMediaKind,
                    track.FileName));
            }

            if (book.HasCover)
            {
                media.Add(new PortableSourceMedia(
                    book.Id,
                    PortableArchiveFormat.CoverMediaKind));
            }
        }

        await transaction.CommitAsync(ct);

        return new PortableExportSnapshot(
            snapshotAtUtc,
            data,
            PortableLibraryCounts.ComputeArchiveCounts(data),
            media);
    }

    /// <summary>
    /// What the archive records about an edition's stored media. A multi-track
    /// audiobook reports HasFile with no primary file: its media travels as
    /// tracks instead.
    /// </summary>
    private static PortableBookMedia ExportBookMedia(BookModel book) =>
        new(
            book.FileDetails.HasFile && book.FileDetails.TracksJson is null,
            !string.IsNullOrWhiteSpace(book.FileDetails.CoverFileName),
            book.FileDetails.TracksJson);

    private async Task<IReadOnlyList<PinnedPortableSourceMedia>> PinSourceMediaAsync(
        PortableExportSnapshot snapshot,
        CancellationToken ct)
    {
        var pinned = new List<PinnedPortableSourceMedia>(snapshot.Media.Count);

        foreach (var source in snapshot.Media)
        {
            var info = await GetAssetInfoAsync(source.BookId, source.Kind, source.FileName, ct);
            if (info is null)
            {
                throw new PortableArchiveException(
                    "source_media_missing",
                    $"Book {source.BookId} references a {source.Kind} asset that is missing from storage.");
            }

            // A track keeps the name it is stored under; a book file or cover
            // is named after its kind, with the stored file's extension.
            var extension = Path.GetExtension(info.FileName).ToLowerInvariant();
            var canonicalFileName = PortableArchiveFormat.CanonicalMediaFileName(
                    source.Kind,
                    source.Kind == PortableArchiveFormat.TrackMediaKind
                        ? source.FileName ?? string.Empty
                        : $"{source.Kind}{extension}")
                ?? throw new InvalidOperationException(
                    $"Unsupported {source.Kind} file type: {extension}");

            await using var opened = await OpenAssetAsync(source.BookId, source.Kind, source.FileName, ct);
            if (opened is null)
            {
                throw new PortableArchiveException(
                    "source_media_missing",
                    $"Book {source.BookId} references a {source.Kind} asset that could not be opened.");
            }

            var hashed = await PortableArchiveHashing.HashStreamAsync(
                opened.Content,
                ct,
                PortableArchiveLimits.MaxSingleEntryBytes,
                "Portable media exceeds the expected size.");

            var after = await GetAssetInfoAsync(source.BookId, source.Kind, source.FileName, ct);
            if (after is null
                || after.Length != info.Length
                || after.LastModified != info.LastModified
                || !string.Equals(after.EntityTag, info.EntityTag, StringComparison.Ordinal)
                || hashed.Length != info.Length)
            {
                throw SourceMediaChanged(source.BookId, source.Kind);
            }

            pinned.Add(new PinnedPortableSourceMedia(
                source.BookId,
                source.Kind,
                PortableArchiveFormat.MediaPath(source.BookId, canonicalFileName),
                canonicalFileName,
                info.ContentType,
                info.Length,
                info.LastModified,
                info.EntityTag,
                hashed.Sha256));
        }

        return pinned;
    }

    private async Task<PortableArchiveMediaEntry> AppendPinnedAssetAsync(
        ZipArchive archive,
        PinnedPortableSourceMedia pinned,
        Memory<byte> copyBuffer,
        CancellationToken ct)
    {
        var info = await GetAssetInfoAsync(pinned.BookId, pinned.Kind, pinned.FileName, ct);
        if (info is null)
        {
            throw new PortableArchiveException(
                "source_media_missing",
                $"Book {pinned.BookId} references a {pinned.Kind} asset that is missing from storage.");
        }

        if (info.Length != pinned.Length
            || info.LastModified != pinned.LastModified
            || !string.Equals(info.EntityTag, pinned.EntityTag, StringComparison.Ordinal))
        {
            throw SourceMediaChanged(pinned.BookId, pinned.Kind);
        }

        await using var opened = await OpenAssetAsync(pinned.BookId, pinned.Kind, pinned.FileName, ct);
        if (opened is null)
        {
            throw new PortableArchiveException(
                "source_media_missing",
                $"Book {pinned.BookId} references a {pinned.Kind} asset that could not be opened.");
        }

        var entry = archive.CreateEntry(pinned.ArchivePath, CompressionLevel.NoCompression);
        var target = await entry.OpenAsync(ct);
        (long Length, string Sha256) copied;
        try
        {
            copied = await CopyAndHashAsync(
                opened.Content,
                target,
                PortableArchiveLimits.MaxSingleEntryBytes,
                copyBuffer,
                ct);
        }
        catch
        {
            await DisposeQuietlyAsync(target);
            throw;
        }

        await target.DisposeAsync();

        if (copied.Length != pinned.Length
            || !string.Equals(copied.Sha256, pinned.Sha256, StringComparison.Ordinal))
        {
            throw SourceMediaChanged(pinned.BookId, pinned.Kind);
        }

        var afterCopy = await GetAssetInfoAsync(pinned.BookId, pinned.Kind, pinned.FileName, ct);
        if (afterCopy is null
            || afterCopy.Length != pinned.Length
            || afterCopy.LastModified != pinned.LastModified
            || !string.Equals(afterCopy.EntityTag, pinned.EntityTag, StringComparison.Ordinal))
        {
            throw SourceMediaChanged(pinned.BookId, pinned.Kind);
        }

        return new PortableArchiveMediaEntry(
            pinned.BookId,
            pinned.Kind,
            pinned.ArchivePath,
            pinned.FileName,
            pinned.ContentType,
            pinned.Length,
            pinned.Sha256);
    }

    private Task<StoredAssetInfo?> GetAssetInfoAsync(
        Guid bookId,
        string kind,
        string? fileName,
        CancellationToken ct) =>
        PortableMediaStorage.GetInfoAsync(_assets, _tracks, bookId, kind, fileName, ct);

    private Task<StoredAssetRead?> OpenAssetAsync(
        Guid bookId,
        string kind,
        string? fileName,
        CancellationToken ct) =>
        PortableMediaStorage.OpenAsync(_assets, _tracks, bookId, kind, fileName, ct);

    private static PortableArchiveException SourceMediaChanged(Guid bookId, string kind) =>
        new(
            "source_media_changed",
            $"Book {bookId} {kind} changed while the archive was being exported.");

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        Memory<byte> buffer,
        CancellationToken ct)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
                break;

            total = checked(total + read);
            PortableArchiveValidation.ValidateCopiedMediaSize(total, maxBytes);

            hash.AppendData(buffer.Span[..read]);
            await destination.WriteAsync(buffer[..read], ct);
        }

        return (
            total,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync();
        }
        catch
        {
            // Cleanup only; the primary export failure is authoritative.
        }
    }
}
