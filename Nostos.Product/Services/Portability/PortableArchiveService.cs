using System.Data;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Enums;
using Nostos.Product.BookText;

namespace Nostos.Backend.Services.Portability;

public sealed class PortableArchiveService(
    NostosDbContext db,
    IBookAssetStorage assets,
    ILogger<PortableArchiveService> logger,
    IBookTextIngestionScheduler? bookTextScheduler = null,
    TimeProvider? timeProvider = null,
    IBookTrackStorage? trackStorage = null)
    : IPortableArchiveService
{
    private const int CopyBufferSize = 128 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly NostosDbContext _db = db;
    private readonly IBookAssetStorage _assets = assets;
    private readonly IBookTrackStorage? _tracks = trackStorage;
    private readonly ILogger<PortableArchiveService> _logger = logger;
    private readonly IBookTextIngestionScheduler? _bookTextScheduler = bookTextScheduler;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // The relational JSON cap is injectable so the preflight boundary can be
    // exercised without serializing a 64 MiB payload.
    internal long MaxExportDataBytes { get; init; } = PortableArchiveLimits.MaxDataBytes;

    // Test seam: the directory that receives the per-import scratch directory
    // (upload spool plus local staging). Production uses the process temp path.
    internal string ScratchRoot { get; init; } = Path.GetTempPath();

    // Test seam: free-space probe for the scratch volume. Production uses DriveInfo.
    internal Func<string, long?>? ScratchFreeSpaceProbe { get; init; }

    public Task<PortableExportResult> ExportAsync(
        Stream destination,
        CancellationToken cancellationToken = default) =>
        ExportAsync(
            destination,
            progress: null,
            CreateExportBufferBudget(),
            cancellationToken);

    public async Task<PortableExportResult> ExportAsync(
        IPortableArchiveSink destination,
        IProgress<PortableArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var writer = await destination.OpenWriteAsync(cancellationToken);
        return await ExportAsync(
            writer,
            progress,
            CreateExportBufferBudget(),
            cancellationToken);
    }

    // Shared-budget overload for callers that must account the export against
    // an operation-wide PortableArchiveBufferBudget (and for the adapter
    // high-water tests).
    internal Task<PortableExportResult> ExportAsync(
        Stream destination,
        PortableArchiveBufferBudget exportBufferBudget,
        CancellationToken cancellationToken = default) =>
        ExportAsync(destination, progress: null, exportBufferBudget, cancellationToken);

    // Shared-budget sink overload for the migration export job: the 16 MiB
    // synchronous capture lease is charged to the caller's per-job operation
    // budget, so concurrent exports each account their own capture buffer.
    internal async Task<PortableExportResult> ExportAsync(
        IPortableArchiveSink destination,
        IProgress<PortableArchiveProgress>? progress,
        PortableArchiveBufferBudget exportBufferBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(exportBufferBudget);

        await using var writer = await destination.OpenWriteAsync(cancellationToken);
        return await ExportAsync(writer, progress, exportBufferBudget, cancellationToken);
    }

    private static PortableArchiveBufferBudget CreateExportBufferBudget() =>
        new(PortableArchiveLimits.MaxExplicitBufferBytes);

    private async Task<PortableExportResult> ExportAsync(
        Stream destination,
        IProgress<PortableArchiveProgress>? progress,
        PortableArchiveBufferBudget bufferBudget,
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
            MaxExportDataBytes,
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

            var dataInfo = await WriteArchiveAsync(
                buffered,
                snapshot,
                pinned,
                dataPreflight,
                media,
                copyBuffer.Memory,
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
            JsonOptions,
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
                    MaxExportDataBytes,
                    "data_too_large",
                    $"Portable relational data exceeds the {MaxExportDataBytes} byte v1 limit.");
                await JsonSerializer.SerializeAsync(
                    hashing,
                    snapshot.Data,
                    JsonOptions,
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
                    JsonOptions,
                    cancellationToken);
            }
            catch
            {
                await DisposeQuietlyAsync(manifestStream);
                throw;
            }

            await manifestStream.DisposeAsync();

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

    public async Task<PortableImportResult> ImportAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The import source must be readable.", nameof(source));

        // The raw HTTP request body is not seekable and the archive reader needs a
        // position-independent source, so the compatibility endpoint spools the
        // compressed archive to one bounded local scratch file. That spool is the
        // only whole-archive local copy: every media entry then streams from the
        // archive into local staging and, one item at a time, into asset storage.
        //
        // The lease is the ownership signal cleanup uses: an import may legally
        // run longer than the scratch TTL, and its directory timestamp alone
        // cannot prove it is dead.
        var tempRoot = Path.Combine(
            ScratchRoot,
            $"nostos-portable-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var scratchLease = LegacyPortabilityScratchLease.Acquire(tempRoot);

        try
        {
            var archivePath = Path.Combine(tempRoot, "archive.nostos");
            await StageArchiveAsync(source, archivePath, cancellationToken);

            await using var archiveSource = new FilePortableArchiveSource(archivePath);
            await using var staging = new LocalPortableImportStaging(
                Path.Combine(tempRoot, "staging"),
                ScratchFreeSpaceProbe);
            var reader = new PortableArchiveReader(timeProvider: _timeProvider);

            var prepared = await PrepareStagedImportAsync(
                reader,
                archiveSource,
                staging,
                cancellationToken);

            var data = await ReadStagedDataAsync(staging, prepared.StagingId, cancellationToken);
            var manifest = await ReadStagedManifestAsync(staging, prepared.StagingId, cancellationToken);

            var uploadedBookIds = new HashSet<Guid>();
            var committed = false;

            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            try
            {
                await EnsureDestinationIsEmptyAsync(cancellationToken);
                await ApplyRelationalDataAsync(data, prepared.Media, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);

                foreach (var media in prepared.Media)
                {
                    // Register the book for compensating cleanup before touching
                    // durable storage. A storage implementation can fail after
                    // partially writing an object/file, so only registering after
                    // a successful Save* call can strand media on a failed import.
                    uploadedBookIds.Add(media.Descriptor.BookId);

                    await using var content = await staging.OpenMediaReadAsync(
                        prepared.StagingId,
                        media.Reference,
                        cancellationToken);

                    await PortableMediaStorage.SaveAsync(
                        _assets,
                        _tracks,
                        media.Descriptor.BookId,
                        media.Descriptor.Kind,
                        media.Descriptor.FileName,
                        content,
                        cancellationToken);
                }

                await VerifyRelationalIntegrityAsync(
                    data,
                    manifest.Counts,
                    cancellationToken);
                await VerifyStoredMediaAsync(
                    manifest.Media,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                committed = true;

                // Derived text/indexes are intentionally excluded from portable
                // archives. Rebuild them from the authoritative imported source
                // only after the archive transaction and media verification have
                // succeeded, so a failed import cannot leave searchable ghosts.
                if (_bookTextScheduler is not null)
                {
                    foreach (var media in prepared.Media
                        .Where(media =>
                            media.Descriptor.Kind == PortableArchiveFormat.BookMediaKind
                            && BookTextFormatResolver.TryResolve(
                                media.Descriptor.FileName,
                                out _))
                        .GroupBy(media => media.Descriptor.BookId)
                        .Select(group => group.First()))
                    {
                        try
                        {
                            await _bookTextScheduler.ScheduleAsync(
                                media.Descriptor.BookId,
                                media.Descriptor.FileName,
                                CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            // The archive and authoritative publication bytes are
                            // already committed. A derived-index scheduling failure
                            // must be retryable/backfillable, never turn a successful
                            // portable restore into a false failure.
                            _logger.LogWarning(
                                "Portable import could not schedule derived text for book {BookId}; exception type {ExceptionType}. Publication text is not logged.",
                                media.Descriptor.BookId,
                                exception.GetType().Name);
                        }
                    }
                }

                return new PortableImportResult(
                    manifest.FormatVersion,
                    manifest.Counts,
                    prepared.MediaFiles,
                    prepared.MediaBytes,
                    IntegrityVerified: true);
            }
            catch (Exception exception)
            {
                if (!committed)
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    catch (Exception rollbackException)
                    {
                        _logger.LogError(
                            "Portable import relational rollback failed with {ExceptionType}; details suppressed.",
                            rollbackException.GetType().Name);
                    }

                    await CleanupImportedMediaAsync(uploadedBookIds);
                    _db.ChangeTracker.Clear();
                }

                if (exception is PortableArchiveException)
                    throw;

                throw new PortableArchiveException(
                    "import_failed",
                    "Portable archive import failed. The destination was rolled back.",
                    exception);
            }
        }
        finally
        {
            // Release ownership before deleting: on Windows the held lock file
            // would otherwise block the recursive delete.
            scratchLease.Dispose();
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task<PortablePreparedImport> PrepareStagedImportAsync(
        PortableArchiveReader reader,
        IPortableArchiveSource archiveSource,
        IPortableImportStaging staging,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.PrepareImportAsync(
                archiveSource,
                staging,
                progress: null,
                cancellationToken);
        }
        catch (PortableArchiveException exception) when (
            exception.Code == "import_failed"
            && exception.InnerException is IOException ioException)
        {
            // The legacy validation/extraction phase surfaced a filesystem failure
            // as a raw IOException (unhandled -> HTTP 500). The reader wraps
            // unexpected failures as import_failed; unwrap the original I/O failure
            // so the compatibility endpoint keeps the pre-slice observable outcome.
            ExceptionDispatchInfo.Capture(ioException).Throw();
            throw;
        }
    }

    // The staged relational payload was already validated by the reader; the
    // shared materializer deserializes the same verified bytes for the
    // relational restore.
    private static Task<PortableLibraryData> ReadStagedDataAsync(
        IPortableImportStaging staging,
        PortableStagingId stagingId,
        CancellationToken cancellationToken) =>
        PortableLibraryDatabaseMaterializer.ReadRelationalDataAsync(
            staging,
            stagingId,
            cancellationToken);

    private static async Task<PortableArchiveManifest> ReadStagedManifestAsync(
        IPortableImportStaging staging,
        PortableStagingId stagingId,
        CancellationToken cancellationToken)
    {
        await using var staged = await staging
            .OpenManifestReadAsync(stagingId, cancellationToken);
        try
        {
            return await JsonSerializer
                .DeserializeAsync<PortableArchiveManifest>(staged, JsonOptions, cancellationToken)
                ?? throw new PortableArchiveException(
                    "malformed_manifest",
                    "Portable archive manifest is malformed.");
        }
        catch (JsonException exception)
        {
            throw new PortableArchiveException(
                "malformed_manifest",
                "Portable archive manifest is malformed.",
                exception);
        }
    }

    private async Task<PortableExportSnapshot> CaptureSnapshotAsync(CancellationToken ct)
    {
        // One serializable read transaction owns every relational query for the
        // export. Media is never read, hashed or copied while it is open.
        await using var transaction = await _db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            ct);

        // The timestamp is taken only after the transaction has been acquired:
        // acquiring it can wait on an active writer, and the manifest must
        // never claim an earlier revision than the rows that were read.
        var snapshotAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var works = (await _db.Works
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(ct))
            .Select(x => new PortableWork(
                x.Id,
                x.Title,
                x.Author,
                x.CreatedAt))
            .ToList();

        var books = (await _db.Books
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(ct))
            .Select(ToPortableBook)
            .ToList();

        var collections = (await _db.Collections
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(ct))
            .Select(x => new PortableCollection(
                x.Id,
                x.Name,
                x.ParentId))
            .ToList();

        var bookCollections = (await _db.BookCollections
            .AsNoTracking()
            .OrderBy(x => x.BookId)
            .ThenBy(x => x.CollectionId)
            .ToListAsync(ct))
            .Select(x => new PortableBookCollection(
                x.BookId,
                x.CollectionId,
                x.AddedAt))
            .ToList();

        var notes = (await _db.Notes
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(ct))
            .Select(x => new PortableNote(
                x.Id,
                x.Content,
                x.CfiRange,
                x.SelectedText,
                x.CreatedAt,
                x.BookId,
                x.RawContent,
                x.CaptureSource,
                x.ProcessingMode,
                x.SourceAnchorKind,
                x.SourceAnchorValue,
                x.AnchorVerified))
            .ToList();

        var topics = (await _db.Topics
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(ct))
            .Select(x => new PortableTopic(
                x.Id,
                x.Topic))
            .ToList();

        var noteTopics = (await _db.NoteTopics
            .AsNoTracking()
            .OrderBy(x => x.NoteId)
            .ThenBy(x => x.TopicId)
            .ToListAsync(ct))
            .Select(x => new PortableNoteTopic(
                x.NoteId,
                x.TopicId))
            .ToList();

        var writings = (await _db.Writings
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(ct))
            .Select(x => new PortableWriting(
                x.Id,
                x.Name,
                x.Type.ToString(),
                x.Content,
                x.ParentId,
                x.CreatedAt,
                x.UpdatedAt))
            .ToList();

        var acquisitions = (await _db.BookAcquisitions
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(ct))
            .Select(x => new PortableBookAcquisition(
                x.Id,
                x.BookId,
                x.ProviderId,
                x.ProviderDisplayName,
                x.ExternalId,
                x.AssetId,
                x.AssetFormat,
                x.ImportedExtension,
                x.SourceUrl,
                x.RightsStatement,
                x.AcquiredAt))
            .ToList();

        var writingNotes = (await _db.WritingNotes
            .AsNoTracking()
            .OrderBy(x => x.WritingId)
            .ThenBy(x => x.NoteId)
            .ToListAsync(ct))
            .Select(x => new PortableWritingNote(
                x.WritingId,
                x.NoteId,
                x.AddedAt))
            .ToList();

        var noteImportBookLinks = (await _db.NoteImportBookLinks
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(ct))
            .Select(x => new PortableNoteImportBookLink(
                x.Id,
                x.Source,
                x.SourceKey,
                x.BookId,
                x.CreatedAtUtc))
            .ToList();

        var assistant = await _db.AssistantSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(ct);

        var data = new PortableLibraryData(
            PortableArchiveFormat.DataVersion,
            works,
            books,
            collections,
            bookCollections,
            notes,
            topics,
            noteTopics,
            writings,
            acquisitions,
            assistant is null
                ? null
                : new PortableAssistantSettings(
                    assistant.CaptureProcessingMode,
                    assistant.UpdatedAtUtc),
            writingNotes,
            noteImportBookLinks);

        var media = new List<PortableSourceMedia>();
        foreach (var book in books)
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
            CountsFor(data),
            media);
    }

    private static PortableBook ToPortableBook(BookModel book)
    {
        var type = book switch
        {
            PhysicalBookModel => "physical",
            EBookModel => "ebook",
            AudioBookModel => "audiobook",
            _ => throw new PortableArchiveException(
                "unsupported_book_type",
                $"Book {book.Id} has an unsupported concrete type.")
        };

        var isbn = book switch
        {
            PhysicalBookModel physical => physical.Isbn,
            EBookModel ebook => ebook.Isbn,
            _ => null,
        };
        var pageCount = book switch
        {
            PhysicalBookModel physical => physical.PageCount,
            EBookModel ebook => ebook.PageCount,
            _ => null,
        };
        var audio = book as AudioBookModel;

        return new PortableBook(
            book.Id,
            book.WorkId,
            type,
            book.Status.ToString(),
            book.StatusMessage,
            book.Title,
            book.Author,
            new PortableBookMetadata(
                book.Metadata.Subtitle,
                book.Metadata.Description,
                book.Metadata.Editor,
                book.Metadata.Translator,
                book.Metadata.Publisher,
                book.Metadata.PlaceOfPublication,
                book.Metadata.PublishedDate,
                book.Metadata.Language,
                book.Metadata.Categories,
                book.Metadata.Edition,
                book.Metadata.Series,
                book.Metadata.VolumeNumber),
            new PortableReadingProgress(
                book.Progress.LastLocation,
                book.Progress.ProgressPercent,
                book.Progress.Rating,
                book.Progress.IsFavorite,
                book.Progress.PersonalReview,
                book.Progress.LastReadAt,
                book.Progress.FinishedAt),
            book.CreatedAt,
            isbn,
            pageCount,
            audio?.Asin,
            audio?.Duration,
            audio?.Narrator,
            book.FileDetails.ChaptersJson,
            // A multi-track audiobook reports HasFile with no primary file; its
            // media travels as tracks instead.
            book.FileDetails.HasFile && book.FileDetails.TracksJson is null,
            !string.IsNullOrWhiteSpace(book.FileDetails.CoverFileName),
            book.FileDetails.TracksJson);
    }

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

            var hashed = await HashStreamAsync(
                opened.Content,
                PortableArchiveLimits.MaxSingleEntryBytes,
                ct);

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

    private async Task StageArchiveAsync(
        Stream source,
        string archivePath,
        CancellationToken ct)
    {
        await using var target = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[CopyBufferSize];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
                break;

            total = checked(total + read);
            PortableArchiveValidation.ValidateArchiveSize(total);

            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (total == 0)
            throw new PortableArchiveException("empty_archive", "Portable archive is empty.");
    }

    private async Task EnsureDestinationIsEmptyAsync(CancellationToken ct)
    {
        var hasUserData =
            await _db.Books.AnyAsync(ct)
            || await _db.Works.AnyAsync(ct)
            || await _db.Collections.AnyAsync(ct)
            || await _db.BookCollections.AnyAsync(ct)
            || await _db.Notes.AnyAsync(ct)
            || await _db.Topics.AnyAsync(ct)
            || await _db.NoteTopics.AnyAsync(ct)
            || await _db.Writings.AnyAsync(ct)
            || await _db.WritingNotes.AnyAsync(ct)
            || await _db.BookAcquisitions.AnyAsync(ct)
            || await _db.NoteImportBookLinks.AnyAsync(ct)
            || await _db.AssistantSettings.AnyAsync(
                x => x.CaptureProcessingMode != null,
                ct);

        if (hasUserData)
        {
            throw new PortableArchiveException(
                "destination_not_empty",
                "Portable archive v1 can only be imported into an empty/new Nostos library.");
        }
    }

    private Task ApplyRelationalDataAsync(
        PortableLibraryData data,
        IReadOnlyList<PortablePreparedMedia> stagedMedia,
        CancellationToken ct) =>
        PortableLibraryDatabaseMaterializer.ApplyRelationalDataAsync(_db, data, stagedMedia, ct);

    private async Task VerifyRelationalIntegrityAsync(
        PortableLibraryData source,
        PortableArchiveCounts expected,
        CancellationToken ct)
    {
        var actual = new PortableArchiveCounts(
            await _db.Works.CountAsync(ct),
            await _db.Books.CountAsync(ct),
            await _db.Collections.CountAsync(ct),
            await _db.BookCollections.CountAsync(ct),
            await _db.Notes.CountAsync(ct),
            await _db.Topics.CountAsync(ct),
            await _db.NoteTopics.CountAsync(ct),
            await _db.Writings.CountAsync(ct),
            await _db.BookAcquisitions.CountAsync(ct));

        PortableArchiveValidation.ValidateImportedCounts(actual, expected);

        var workIds = await _db.Works.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        var bookRows = await _db.Books.AsNoTracking().ToListAsync(ct);
        var collectionIds = await _db.Collections.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        var noteIds = await _db.Notes.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        var topicIds = await _db.Topics.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        var writingRows = await _db.Writings.AsNoTracking().ToListAsync(ct);
        var acquisitionIds = await _db.BookAcquisitions.AsNoTracking().Select(x => x.Id).ToListAsync(ct);

        PortableArchiveValidation.RequireSameIds(source.Works.Select(x => x.Id), workIds, "work");
        PortableArchiveValidation.RequireSameIds(source.Books.Select(x => x.Id), bookRows.Select(x => x.Id), "book");
        PortableArchiveValidation.RequireSameIds(source.Collections.Select(x => x.Id), collectionIds, "collection");
        PortableArchiveValidation.RequireSameIds(source.Notes.Select(x => x.Id), noteIds, "note");
        PortableArchiveValidation.RequireSameIds(source.Topics.Select(x => x.Id), topicIds, "topic");
        PortableArchiveValidation.RequireSameIds(source.Writings.Select(x => x.Id), writingRows.Select(x => x.Id), "writing");
        PortableArchiveValidation.RequireSameIds(source.BookAcquisitions.Select(x => x.Id), acquisitionIds, "acquisition");

        var expectedBookWorks = source.Books
            .Select(x => (x.Id, x.WorkId))
            .ToHashSet();
        var actualBookWorks = bookRows
            .Select(x => (x.Id, x.WorkId))
            .ToHashSet();
        PortableArchiveValidation.ValidateImportedRelationshipSet(
            expectedBookWorks,
            actualBookWorks,
            "Imported Work/Book relationships do not match the archive.");

        var expectedMemberships = source.BookCollections
            .Select(x => (x.BookId, x.CollectionId))
            .ToHashSet();
        var actualMemberships = (await _db.BookCollections
            .AsNoTracking()
            .Select(x => new { x.BookId, x.CollectionId })
            .ToListAsync(ct))
            .Select(x => (x.BookId, x.CollectionId))
            .ToHashSet();
        PortableArchiveValidation.ValidateImportedRelationshipSet(
            expectedMemberships,
            actualMemberships,
            "Imported collection memberships do not match the archive.");

        var expectedLinks = source.NoteTopics
            .Select(x => (x.NoteId, x.TopicId))
            .ToHashSet();
        var actualLinks = (await _db.NoteTopics
            .AsNoTracking()
            .Select(x => new { x.NoteId, x.TopicId })
            .ToListAsync(ct))
            .Select(x => (x.NoteId, x.TopicId))
            .ToHashSet();
        PortableArchiveValidation.ValidateImportedRelationshipSet(
            expectedLinks,
            actualLinks,
            "Imported Note/Topic links do not match the archive.");

        var expectedWritingNotes = (source.WritingNotes ?? [])
            .Select(x => (x.WritingId, x.NoteId))
            .ToHashSet();
        var actualWritingNotes = (await _db.WritingNotes
            .AsNoTracking()
            .Select(x => new { x.WritingId, x.NoteId })
            .ToListAsync(ct))
            .Select(x => (x.WritingId, x.NoteId))
            .ToHashSet();
        PortableArchiveValidation.ValidateImportedRelationshipSet(
            expectedWritingNotes,
            actualWritingNotes,
            "Imported Writing/Note links do not match the archive.");

        var expectedImportLinks = (source.NoteImportBookLinks ?? [])
            .Select(x => (x.Id, x.Source, x.SourceKey, x.BookId, x.CreatedAtUtc))
            .ToHashSet();
        var actualImportLinks = (await _db.NoteImportBookLinks
            .AsNoTracking()
            .Select(x => new { x.Id, x.Source, x.SourceKey, x.BookId, x.CreatedAtUtc })
            .ToListAsync(ct))
            .Select(x => (x.Id, x.Source, x.SourceKey, x.BookId, x.CreatedAtUtc))
            .ToHashSet();
        PortableArchiveValidation.ValidateImportedRelationshipSet(
            expectedImportLinks,
            actualImportLinks,
            "Imported note import book links do not match the archive.");

        var sourceBooks = source.Books.ToDictionary(x => x.Id);
        foreach (var book in bookRows)
        {
            var expectedBook = sourceBooks[book.Id];
            var actualType = book switch
            {
                PhysicalBookModel => "physical",
                EBookModel => "ebook",
                AudioBookModel => "audiobook",
                _ => "unknown",
            };
            PortableArchiveValidation.ValidateImportedBookType(
                expectedBook.Id,
                expectedBook.Type,
                actualType);
            PortableArchiveValidation.ValidateImportedBookProgress(
                expectedBook,
                book.Progress.LastLocation,
                book.Progress.ProgressPercent,
                book.Progress.Rating,
                book.Progress.IsFavorite,
                book.Progress.PersonalReview);
        }

        var sourceWritings = source.Writings.ToDictionary(x => x.Id);
        foreach (var writing in writingRows)
        {
            var expectedWriting = sourceWritings[writing.Id];
            PortableArchiveValidation.ValidateImportedWritingState(
                expectedWriting,
                writing.ParentId,
                writing.Name,
                writing.Content,
                writing.Type.ToString());
        }
    }

    private async Task VerifyStoredMediaAsync(
        IReadOnlyList<PortableArchiveMediaEntry> media,
        CancellationToken ct)
    {
        foreach (var descriptor in media)
        {
            var info = await GetAssetInfoAsync(descriptor.BookId, descriptor.Kind, descriptor.FileName, ct);

            PortableArchiveValidation.ValidateStoredMediaLength(
                descriptor.Path,
                info?.Length,
                descriptor.Length);

            await using var opened = await OpenAssetAsync(descriptor.BookId, descriptor.Kind, descriptor.FileName, ct);

            PortableArchiveValidation.ValidateStoredMediaCanReopen(
                descriptor.Path,
                opened is not null);

            var hash = await HashStreamAsync(
                opened!.Content,
                descriptor.Length,
                ct);
            PortableArchiveValidation.ValidateStoredMediaHash(
                descriptor.Path,
                hash.Length,
                descriptor.Length,
                hash.Sha256,
                descriptor.Sha256);
        }
    }

    private async Task CleanupImportedMediaAsync(IEnumerable<Guid> bookIds)
    {
        foreach (var bookId in bookIds.Distinct())
        {
            try
            {
                await _assets.DeleteBookFilesAsync(bookId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Failed to clean portable-import media for book {BookId}; exception type {ExceptionType}. Details suppressed.",
                    bookId,
                    exception.GetType().Name);
            }
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

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        Memory<byte> buffer,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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

    private static async Task<(long Length, string Sha256)> HashStreamAsync(
        Stream source,
        long maxBytes,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
                break;

            total = checked(total + read);
            PortableArchiveValidation.ValidateExpectedMediaSize(total, maxBytes);

            hash.AppendData(buffer, 0, read);
        }

        return (
            total,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temporary staging is best-effort cleanup only. It never contains
            // the user's source library or destination durable storage.
        }
    }
}

/// <summary>
/// Serializes relational JSON straight into a ZIP entry while counting and
/// hashing the exact bytes that will be stored. Only asynchronous writes are
/// forwarded; the archived entry never needs a temporary file. Exceeding
/// <paramref name="maxBytes"/> fails the export before more than the limit is
/// written.
/// </summary>
internal sealed class HashingWriteStream : Stream
{
    private readonly Stream _destination;
    private readonly long _maxBytes;
    private readonly string _limitCode;
    private readonly string _limitMessage;
    private readonly IncrementalHash _hash =
        IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _length;
    private bool _completed;
    private bool _disposed;

    public HashingWriteStream(
        Stream destination,
        long maxBytes,
        string limitCode,
        string limitMessage)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _destination = destination;
        _maxBytes = maxBytes;
        _limitCode = limitCode;
        _limitMessage = limitMessage;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed && !_completed;
    public override long Length => _length;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public (long Length, string Sha256) Complete()
    {
        if (_completed)
            throw new InvalidOperationException("The hashing stream is already complete.");
        if (_disposed)
            throw new ObjectDisposedException(nameof(HashingWriteStream));

        _completed = true;
        return (
            _length,
            Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant());
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        Observe(buffer.Span);
        await _destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException(
            "Portable archive serialization must use asynchronous writes.");

    public override void Write(ReadOnlySpan<byte> buffer) =>
        throw new NotSupportedException(
            "Portable archive serialization must use asynchronous writes.");

    public override void WriteByte(byte value) =>
        throw new NotSupportedException(
            "Portable archive serialization must use asynchronous writes.");

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _destination.FlushAsync(cancellationToken);

    public override void Flush() =>
        throw new NotSupportedException(
            "Portable archive serialization must use asynchronous writes.");

    public override ValueTask DisposeAsync()
    {
        Dispose(disposing: true);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        _disposed = true;
        if (disposing)
            _hash.Dispose();
        base.Dispose(disposing);
    }

    private void Observe(ReadOnlySpan<byte> buffer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HashingWriteStream));
        if (_completed)
            throw new InvalidOperationException("The hashing stream is already complete.");

        var next = checked(_length + buffer.Length);
        if (next > _maxBytes)
            throw new PortableArchiveException(_limitCode, _limitMessage);

        _hash.AppendData(buffer);
        _length = next;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();
}
