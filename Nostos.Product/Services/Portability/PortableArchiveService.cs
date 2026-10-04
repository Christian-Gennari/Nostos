using System.Data;
using System.IO.Compression;
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
    TimeProvider? timeProvider = null)
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
    private readonly ILogger<PortableArchiveService> _logger = logger;
    private readonly IBookTextIngestionScheduler? _bookTextScheduler = bookTextScheduler;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // The relational JSON cap is injectable so the preflight boundary can be
    // exercised without serializing a 64 MiB payload.
    internal long MaxExportDataBytes { get; init; } = PortableArchiveLimits.MaxDataBytes;

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

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"nostos-portable-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var archivePath = Path.Combine(tempRoot, "archive.nostos");
            await StageArchiveAsync(source, archivePath, cancellationToken);

            var staged = await ValidateAndStageAsync(
                archivePath,
                tempRoot,
                cancellationToken);

            var uploadedBookIds = new HashSet<Guid>();
            var committed = false;

            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            try
            {
                await EnsureDestinationIsEmptyAsync(cancellationToken);
                await ApplyRelationalDataAsync(staged.Data, staged.Media, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);

                foreach (var media in staged.Media)
                {
                    // Register the book for compensating cleanup before touching
                    // durable storage. A storage implementation can fail after
                    // partially writing an object/file, so only registering after
                    // a successful Save* call can strand media on a failed import.
                    uploadedBookIds.Add(media.Descriptor.BookId);

                    await using var content = new FileStream(
                        media.StagedPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        CopyBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    if (media.Descriptor.Kind == PortableArchiveFormat.BookMediaKind)
                    {
                        await _assets.SaveBookFileAsync(
                            media.Descriptor.BookId,
                            content,
                            media.Descriptor.FileName,
                            cancellationToken);
                    }
                    else
                    {
                        await _assets.SaveBookCoverAsync(
                            media.Descriptor.BookId,
                            content,
                            media.Descriptor.FileName,
                            cancellationToken);
                    }
                }

                await VerifyRelationalIntegrityAsync(
                    staged.Data,
                    staged.Manifest.Counts,
                    cancellationToken);
                await VerifyStoredMediaAsync(
                    staged.Manifest.Media,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                committed = true;

                // Derived text/indexes are intentionally excluded from portable
                // archives. Rebuild them from the authoritative imported source
                // only after the archive transaction and media verification have
                // succeeded, so a failed import cannot leave searchable ghosts.
                if (_bookTextScheduler is not null)
                {
                    foreach (var media in staged.Media
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
                    staged.Manifest.FormatVersion,
                    staged.Manifest.Counts,
                    staged.Manifest.Media.Count,
                    staged.Manifest.Media.Sum(x => x.Length),
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
            TryDeleteDirectory(tempRoot);
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
            book.FileDetails.HasFile,
            !string.IsNullOrWhiteSpace(book.FileDetails.CoverFileName));
    }

    private async Task<IReadOnlyList<PinnedPortableSourceMedia>> PinSourceMediaAsync(
        PortableExportSnapshot snapshot,
        CancellationToken ct)
    {
        var pinned = new List<PinnedPortableSourceMedia>(snapshot.Media.Count);

        foreach (var source in snapshot.Media)
        {
            var info = await GetAssetInfoAsync(source.BookId, source.Kind, ct);
            if (info is null)
            {
                throw new PortableArchiveException(
                    "source_media_missing",
                    $"Book {source.BookId} references a {source.Kind} asset that is missing from storage.");
            }

            var extension = Path.GetExtension(info.FileName).ToLowerInvariant();
            if (source.Kind == PortableArchiveFormat.BookMediaKind)
                BookAssetFormats.RequireBookExtension($"book{extension}");
            else
                BookAssetFormats.RequireCoverExtension($"cover{extension}");

            await using var opened = await OpenAssetAsync(source.BookId, source.Kind, ct);
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

            var after = await GetAssetInfoAsync(source.BookId, source.Kind, ct);
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
                PortableArchiveValidation.MediaPath(source.BookId, source.Kind, extension),
                $"{source.Kind}{extension}",
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
        var info = await GetAssetInfoAsync(pinned.BookId, pinned.Kind, ct);
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

        await using var opened = await OpenAssetAsync(pinned.BookId, pinned.Kind, ct);
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

        var afterCopy = await GetAssetInfoAsync(pinned.BookId, pinned.Kind, ct);
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
        CancellationToken ct) =>
        kind == PortableArchiveFormat.BookMediaKind
            ? _assets.GetBookFileInfoAsync(bookId, ct)
            : _assets.GetBookCoverInfoAsync(bookId, ct);

    private Task<StoredAssetRead?> OpenAssetAsync(
        Guid bookId,
        string kind,
        CancellationToken ct) =>
        kind == PortableArchiveFormat.BookMediaKind
            ? _assets.OpenBookFileAsync(bookId, null, ct)
            : _assets.OpenBookCoverAsync(bookId, ct);

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

    private async Task<ValidatedPortableArchive> ValidateAndStageAsync(
        string archivePath,
        string tempRoot,
        CancellationToken ct)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (InvalidDataException exception)
        {
            throw new PortableArchiveException(
                "invalid_zip",
                "Portable archive is not a valid ZIP container.",
                exception);
        }

        using (archive)
        {
            PortableArchiveValidation.ValidateArchiveEntryCount(archive.Entries.Count);

            var entries = new Dictionary<string, ZipArchiveEntry>(
                StringComparer.OrdinalIgnoreCase);
            long totalUncompressed = 0;

            foreach (var entry in archive.Entries)
            {
                PortableArchiveValidation.ValidateDirectoryEntryName(entry.Name);

                var path = PortableArchiveValidation.ValidateArchivePath(entry.FullName);
                PortableArchiveValidation.ValidateUniqueArchivePath(path, entries.TryAdd(path, entry));
                totalUncompressed = PortableArchiveValidation.ValidateDeclaredEntry(
                    path,
                    entry.Length,
                    entry.CompressedLength,
                    totalUncompressed);
            }

            var hasManifest = entries.TryGetValue(
                PortableArchiveFormat.ManifestPath,
                out var manifestEntry);
            PortableArchiveValidation.ValidateManifestEntryFound(hasManifest);

            var manifestBytes = await ReadEntryBytesAsync(
                manifestEntry!,
                PortableArchiveLimits.MaxManifestBytes,
                ct);
            var manifest = PortableArchiveValidation.Deserialize<PortableArchiveManifest>(
                manifestBytes,
                "malformed_manifest",
                "Portable archive manifest is malformed.",
                JsonOptions);

            PortableArchiveValidation.ValidateManifest(manifest);

            var hasData = entries.TryGetValue(manifest.Data.Path, out var dataEntry);
            PortableArchiveValidation.ValidateDataEntryFound(manifest.Data.Path, hasData);

            PortableArchiveValidation.ValidateDataEntryLength(dataEntry!.Length, manifest.Data.Length);

            var dataBytes = await ReadEntryBytesAsync(
                dataEntry!,
                PortableArchiveLimits.MaxDataBytes,
                ct);
            var dataHash = Sha256(dataBytes);
            PortableArchiveValidation.ValidateDataHash(dataHash, manifest.Data.Sha256);

            var data = PortableArchiveValidation.Deserialize<PortableLibraryData>(
                dataBytes,
                "malformed_data",
                "Portable archive relational payload is malformed.",
                JsonOptions);

            PortableArchiveValidation.ValidateDataVersionAgreement(manifest.DataVersion, data.Version);

            PortableArchiveValidation.ValidatePortableData(data);

            var actualCounts = CountsFor(data);
            PortableArchiveValidation.ValidateManifestCounts(actualCounts, manifest.Counts);

            PortableArchiveValidation.ValidateMediaManifest(manifest, data);

            var stagedMediaBytes = manifest.Media.Sum(media => media.Length);
            EnsureTempExtractionCapacity(tempRoot, stagedMediaBytes);

            PortableArchiveValidation.ValidateArchiveInventory(manifest, entries.Keys);

            var stageRoot = Path.Combine(tempRoot, "media-stage");
            Directory.CreateDirectory(stageRoot);
            var staged = new List<StagedPortableMedia>(manifest.Media.Count);

            for (var index = 0; index < manifest.Media.Count; index++)
            {
                var descriptor = manifest.Media[index];
                var entry = entries[descriptor.Path];

                PortableArchiveValidation.ValidateMediaEntryLength(
                    descriptor.Path,
                    entry.Length,
                    descriptor.Length);

                var stagedPath = Path.Combine(
                    stageRoot,
                    $"{index:D6}{Path.GetExtension(descriptor.FileName).ToLowerInvariant()}");

                await using var input = entry.Open();
                await using var output = new FileStream(
                    stagedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var copied = await CopyAndHashAsync(
                    input,
                    output,
                    descriptor.Length,
                    ct);

                PortableArchiveValidation.ValidateMediaHash(
                    descriptor.Path,
                    copied.Length,
                    descriptor.Length,
                    copied.Sha256,
                    descriptor.Sha256);

                staged.Add(new StagedPortableMedia(descriptor, stagedPath));
            }

            return new ValidatedPortableArchive(manifest, data, staged);
        }
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

    private async Task ApplyRelationalDataAsync(
        PortableLibraryData data,
        IReadOnlyList<StagedPortableMedia> stagedMedia,
        CancellationToken ct)
    {
        var mediaByKey = stagedMedia.ToDictionary(
            x => (x.Descriptor.BookId, x.Descriptor.Kind));

        var works = data.Works.ToDictionary(
            x => x.Id,
            x => new WorkModel
            {
                Id = x.Id,
                Title = x.Title,
                Author = x.Author,
                NormalizedTitle = BookIdentityNormalizer.NormalizeTitle(x.Title),
                NormalizedAuthor = BookIdentityNormalizer.NormalizeAuthor(x.Author),
                CreatedAt = x.CreatedAt,
            });
        _db.Works.AddRange(works.Values);

        var collections = data.Collections.ToDictionary(
            x => x.Id,
            x => new CollectionModel
            {
                Id = x.Id,
                Name = x.Name,
                ParentId = x.ParentId,
            });
        foreach (var source in data.Collections)
        {
            if (source.ParentId is { } parentId)
                collections[source.Id].Parent = collections[parentId];
        }
        _db.Collections.AddRange(collections.Values);

        var writings = data.Writings.ToDictionary(
            x => x.Id,
            x => new WritingModel
            {
                Id = x.Id,
                Name = x.Name,
                Type = Enum.Parse<WritingType>(x.Type, ignoreCase: true),
                Content = x.Content,
                ParentId = x.ParentId,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt,
            });
        foreach (var source in data.Writings)
        {
            if (source.ParentId is { } parentId)
                writings[source.Id].Parent = writings[parentId];
        }
        _db.Writings.AddRange(writings.Values);

        var books = new Dictionary<Guid, BookModel>();
        foreach (var source in data.Books)
        {
            var bookMedia = mediaByKey.GetValueOrDefault((
                source.Id,
                PortableArchiveFormat.BookMediaKind));
            var coverMedia = mediaByKey.GetValueOrDefault((
                source.Id,
                PortableArchiveFormat.CoverMediaKind));

            BookModel book = source.Type switch
            {
                "physical" => new PhysicalBookModel
                {
                    Isbn = source.Isbn,
                    PageCount = source.PageCount,
                },
                "ebook" => new EBookModel
                {
                    Isbn = source.Isbn,
                    PageCount = source.PageCount,
                },
                "audiobook" => new AudioBookModel
                {
                    Asin = source.Asin,
                    Duration = source.Duration,
                    Narrator = source.Narrator,
                },
                _ => throw new PortableArchiveException(
                    "unsupported_book_type",
                    $"Book {source.Id} has unsupported type '{source.Type}'."),
            };

            book.Id = source.Id;
            book.WorkId = source.WorkId;
            book.Work = works[source.WorkId];
            book.Status = Enum.Parse<BookStatus>(source.Status, ignoreCase: true);
            book.StatusMessage = source.StatusMessage;
            book.Title = source.Title;
            book.Author = source.Author;
            book.Metadata = new BookMetadata
            {
                Subtitle = source.Metadata.Subtitle,
                Description = source.Metadata.Description,
                Editor = source.Metadata.Editor,
                Translator = source.Metadata.Translator,
                Publisher = source.Metadata.Publisher,
                PlaceOfPublication = source.Metadata.PlaceOfPublication,
                PublishedDate = source.Metadata.PublishedDate,
                Language = source.Metadata.Language,
                Categories = source.Metadata.Categories,
                Edition = source.Metadata.Edition,
                Series = source.Metadata.Series,
                VolumeNumber = source.Metadata.VolumeNumber,
            };
            book.Progress = new ReadingProgress
            {
                LastLocation = source.Progress.LastLocation,
                ProgressPercent = source.Progress.ProgressPercent,
                Rating = source.Progress.Rating,
                IsFavorite = source.Progress.IsFavorite,
                PersonalReview = source.Progress.PersonalReview,
                LastReadAt = source.Progress.LastReadAt,
                FinishedAt = source.Progress.FinishedAt,
            };
            book.FileDetails = new FileInfoDetails
            {
                HasFile = bookMedia is not null,
                FileName = bookMedia?.Descriptor.FileName,
                CoverFileName = coverMedia?.Descriptor.FileName,
                // Chapter metadata is portable and retained because there is
                // no lazy server-side re-extraction path today. epub.js
                // locations are a client-generated cache and are rebuilt.
                ChaptersJson = source.ChaptersJson,
                LocationsJson = null,
            };
            book.CreatedAt = source.CreatedAt;
            book.NormalizedIsbn = source.Type is "physical" or "ebook"
                ? BookIdentityNormalizer.NormalizeIsbn(source.Isbn)
                : null;
            book.NormalizedAsin = source.Type == "audiobook"
                ? BookIdentityNormalizer.NormalizeAsin(source.Asin)
                : null;

            books.Add(book.Id, book);
        }
        _db.Books.AddRange(books.Values);

        foreach (var source in data.BookCollections)
        {
            _db.BookCollections.Add(new BookCollectionModel
            {
                BookId = source.BookId,
                Book = books[source.BookId],
                CollectionId = source.CollectionId,
                Collection = collections[source.CollectionId],
                AddedAt = source.AddedAt,
            });
        }

        var notes = data.Notes.ToDictionary(
            x => x.Id,
            x => new NoteModel
            {
                Id = x.Id,
                Content = x.Content,
                CfiRange = x.CfiRange,
                SelectedText = x.SelectedText,
                CreatedAt = x.CreatedAt,
                BookId = x.BookId,
                Book = books[x.BookId],
                RawContent = x.RawContent,
                CaptureSource = x.CaptureSource,
                ProcessingMode = x.ProcessingMode,
                SourceAnchorKind = x.SourceAnchorKind,
                SourceAnchorValue = x.SourceAnchorValue,
                AnchorVerified = x.AnchorVerified,
            });
        _db.Notes.AddRange(notes.Values);

        var topics = data.Topics.ToDictionary(
            x => x.Id,
            x => new TopicModel
            {
                Id = x.Id,
                Topic = x.Topic,
            });
        _db.Topics.AddRange(topics.Values);

        foreach (var source in data.NoteTopics)
        {
            _db.NoteTopics.Add(new NoteTopicModel
            {
                NoteId = source.NoteId,
                Note = notes[source.NoteId],
                TopicId = source.TopicId,
                Topic = topics[source.TopicId],
            });
        }

        if (data.WritingNotes is not null)
        {
            foreach (var source in data.WritingNotes)
            {
                _db.WritingNotes.Add(new WritingNoteModel
                {
                    WritingId = source.WritingId,
                    Writing = writings[source.WritingId],
                    NoteId = source.NoteId,
                    Note = notes[source.NoteId],
                    AddedAt = source.AddedAt,
                });
            }
        }

        if (data.NoteImportBookLinks is not null)
        {
            foreach (var source in data.NoteImportBookLinks)
            {
                _db.NoteImportBookLinks.Add(new NoteImportBookLink
                {
                    Id = source.Id,
                    Source = source.Source,
                    SourceKey = source.SourceKey,
                    BookId = source.BookId,
                    Book = books[source.BookId],
                    CreatedAtUtc = source.CreatedAtUtc,
                });
            }
        }

        foreach (var source in data.BookAcquisitions)
        {
            _db.BookAcquisitions.Add(new BookAcquisitionModel
            {
                Id = source.Id,
                BookId = source.BookId,
                Book = books[source.BookId],
                ProviderId = source.ProviderId,
                ProviderDisplayName = source.ProviderDisplayName,
                ExternalId = source.ExternalId,
                AssetId = source.AssetId,
                AssetFormat = source.AssetFormat,
                ImportedExtension = source.ImportedExtension,
                SourceUrl = source.SourceUrl,
                RightsStatement = source.RightsStatement,
                AcquiredAt = source.AcquiredAt,
            });
        }

        if (data.AssistantSettings is { } assistant)
        {
            var existing = await _db.AssistantSettings
                .SingleOrDefaultAsync(x => x.Id == AssistantSettingsModel.SingletonId, ct);
            if (existing is null)
            {
                existing = new AssistantSettingsModel
                {
                    Id = AssistantSettingsModel.SingletonId,
                };
                _db.AssistantSettings.Add(existing);
            }

            existing.CaptureProcessingMode = assistant.CaptureProcessingMode;
            existing.UpdatedAtUtc = assistant.UpdatedAtUtc;
        }
    }

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
            var info = descriptor.Kind == PortableArchiveFormat.BookMediaKind
                ? await _assets.GetBookFileInfoAsync(descriptor.BookId, ct)
                : await _assets.GetBookCoverInfoAsync(descriptor.BookId, ct);

            PortableArchiveValidation.ValidateStoredMediaLength(
                descriptor.Path,
                info?.Length,
                descriptor.Length);

            await using var opened = descriptor.Kind == PortableArchiveFormat.BookMediaKind
                ? await _assets.OpenBookFileAsync(descriptor.BookId, null, ct)
                : await _assets.OpenBookCoverAsync(descriptor.BookId, ct);

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

    private static void EnsureTempExtractionCapacity(string tempRoot, long bytesToStage)
    {
        if (bytesToStage <= 0)
            return;

        var root = Path.GetPathRoot(Path.GetFullPath(tempRoot));
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new PortableArchiveException(
                "temp_space_unavailable",
                "Portable archive extraction cannot determine temporary-storage capacity.");
        }

        long available;
        try
        {
            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            throw new PortableArchiveException(
                "temp_space_unavailable",
                "Portable archive extraction cannot determine temporary-storage capacity.",
                exception);
        }

        // The compressed archive is already staged when this runs. Preserve at
        // least 20% of the remaining temp volume so extraction cannot consume
        // the host's last bytes and destabilize unrelated requests/workers.
        var extractionBudget = available - (available / 5);
        if (bytesToStage > extractionBudget)
        {
            throw new PortableArchiveException(
                "insufficient_temp_space",
                "Portable archive media cannot be staged safely with the temporary storage currently available.");
        }
    }

    private static async Task<byte[]> ReadEntryBytesAsync(
        ZipArchiveEntry entry,
        long maxBytes,
        CancellationToken ct)
    {
        PortableArchiveValidation.ValidateDeclaredReadSize(
            entry.FullName,
            entry.Length,
            maxBytes);

        await using var source = entry.Open();
        using var output = new MemoryStream(
            checked((int)Math.Min(entry.Length, int.MaxValue)));

        var buffer = new byte[CopyBufferSize];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0)
                break;

            total = checked(total + read);
            PortableArchiveValidation.ValidateObservedReadSize(
                entry.FullName,
                total,
                maxBytes);

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return output.ToArray();
    }

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

    private static Task<(long Length, string Sha256)> CopyAndHashAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken ct) =>
        CopyAndHashAsync(
            source,
            destination,
            maxBytes,
            new byte[CopyBufferSize],
            ct);

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

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
