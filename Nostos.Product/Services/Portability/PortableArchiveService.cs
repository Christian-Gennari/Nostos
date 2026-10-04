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
    IBookTextIngestionScheduler? bookTextScheduler = null)
    : IPortableArchiveService
{
    private const int MaxArchiveEntries = 20_000;
    private const long MaxManifestBytes = 4L * 1024 * 1024;
    private const long MaxDataBytes = 64L * 1024 * 1024;
    private const long MaxSingleEntryBytes = 16L * 1024 * 1024 * 1024;
    private const long MaxArchiveBytes = 512L * 1024 * 1024 * 1024;
    private const long MaxUncompressedBytes = 1024L * 1024 * 1024 * 1024;
    private const double MaxCompressionRatio = 1000d;
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

    public async Task<PortableExportResult> ExportAsync(
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The export destination must be writable.", nameof(destination));

        var data = await SnapshotAsync(cancellationToken);
        ValidatePortableData(data);
        var counts = CountsFor(data);

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"nostos-portable-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var dataPath = Path.Combine(tempRoot, "library.json");
            await using (var dataStream = new FileStream(
                dataPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    dataStream,
                    data,
                    JsonOptions,
                    cancellationToken);
            }

            var dataInfo = await DescribeFileAsync(dataPath, cancellationToken);
            if (dataInfo.Length > MaxDataBytes)
            {
                throw new PortableArchiveException(
                    "data_too_large",
                    $"Portable relational data exceeds the {MaxDataBytes} byte v1 limit.");
            }

            var media = new List<PortableArchiveMediaEntry>();

            // A non-seekable destination (an HTTP response body) cannot be used
            // as the archive target directly: ZipArchiveMode.Create on a
            // non-seekable stream writes ZIP data descriptors, and finalizing
            // them performs synchronous writes. Kestrel disallows synchronous IO
            // on response streams, so the export would abort mid-response.
            // Stage the archive in a seekable temp file and copy it out
            // asynchronously after the archive is fully finalized.
            var stageArchive = !destination.CanSeek;
            var archivePath = Path.Combine(tempRoot, "export.nostos");
            Stream archiveTarget = destination;
            if (stageArchive)
            {
                archiveTarget = new FileStream(
                    archivePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }

            using (var archive = new ZipArchive(
                archiveTarget,
                ZipArchiveMode.Create,
                leaveOpen: !stageArchive))
            {
                var dataEntry = archive.CreateEntry(
                    PortableArchiveFormat.DataPath,
                    CompressionLevel.Optimal);
                await using (var source = new FileStream(
                    dataPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var target = dataEntry.Open())
                {
                    await source.CopyToAsync(target, CopyBufferSize, cancellationToken);
                }

                foreach (var book in data.Books.OrderBy(x => x.Id))
                {
                    if (book.HasBookFile)
                    {
                        media.Add(await AppendStoredAssetAsync(
                            archive,
                            book.Id,
                            PortableArchiveFormat.BookMediaKind,
                            cancellationToken));
                    }

                    if (book.HasCover)
                    {
                        media.Add(await AppendStoredAssetAsync(
                            archive,
                            book.Id,
                            PortableArchiveFormat.CoverMediaKind,
                            cancellationToken));
                    }
                }

                var manifest = new PortableArchiveManifest(
                    Format: PortableArchiveFormat.Name,
                    FormatVersion: PortableArchiveFormat.Version,
                    DataVersion: PortableArchiveFormat.DataVersion,
                    ExportedAtUtc: DateTime.UtcNow,
                    ApplicationVersion:
                        typeof(PortableArchiveService).Assembly.GetName().Version?.ToString()
                        ?? "unknown",
                    Counts: counts,
                    Data: new PortableArchivePayload(
                        PortableArchiveFormat.DataPath,
                        dataInfo.Length,
                        dataInfo.Sha256),
                    Media: media);

                var manifestEntry = archive.CreateEntry(
                    PortableArchiveFormat.ManifestPath,
                    CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(
                    manifestStream,
                    manifest,
                    JsonOptions,
                    cancellationToken);
            }

            if (stageArchive)
            {
                await using var staged = new FileStream(
                    archivePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await staged.CopyToAsync(
                    destination,
                    CopyBufferSize,
                    cancellationToken);
            }

            return new PortableExportResult(
                PortableArchiveFormat.Version,
                counts,
                media.Count,
                media.Sum(x => x.Length));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
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

    private async Task<PortableLibraryData> SnapshotAsync(CancellationToken ct)
    {
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

        return new PortableLibraryData(
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

    private async Task<PortableArchiveMediaEntry> AppendStoredAssetAsync(
        ZipArchive archive,
        Guid bookId,
        string kind,
        CancellationToken ct)
    {
        var info = kind == PortableArchiveFormat.BookMediaKind
            ? await _assets.GetBookFileInfoAsync(bookId, ct)
            : await _assets.GetBookCoverInfoAsync(bookId, ct);

        if (info is null)
        {
            throw new PortableArchiveException(
                "source_media_missing",
                $"Book {bookId} references a {kind} asset that is missing from storage.");
        }

        var extension = Path.GetExtension(info.FileName).ToLowerInvariant();
        if (kind == PortableArchiveFormat.BookMediaKind)
            BookAssetFormats.RequireBookExtension($"book{extension}");
        else
            BookAssetFormats.RequireCoverExtension($"cover{extension}");

        var fileName = $"{kind}{extension}";
        var path = MediaPath(bookId, kind, extension);

        await using var opened = kind == PortableArchiveFormat.BookMediaKind
            ? await _assets.OpenBookFileAsync(bookId, null, ct)
            : await _assets.OpenBookCoverAsync(bookId, ct);

        if (opened is null)
        {
            throw new PortableArchiveException(
                "source_media_missing",
                $"Book {bookId} references a {kind} asset that could not be opened.");
        }

        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        await using var target = entry.Open();
        var copied = await CopyAndHashAsync(
            opened.Content,
            target,
            MaxSingleEntryBytes,
            ct);

        if (copied.Length != opened.Info.Length)
        {
            throw new PortableArchiveException(
                "source_media_changed",
                $"Book {bookId} {kind} changed while the archive was being exported.");
        }

        return new PortableArchiveMediaEntry(
            bookId,
            kind,
            path,
            fileName,
            opened.Info.ContentType,
            copied.Length,
            copied.Sha256);
    }

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
            if (total > MaxArchiveBytes)
            {
                throw new PortableArchiveException(
                    "archive_too_large",
                    $"Portable archive exceeds the {MaxArchiveBytes} byte v1 compressed-size limit.");
            }

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
            if (archive.Entries.Count > MaxArchiveEntries)
            {
                throw new PortableArchiveException(
                    "too_many_entries",
                    $"Portable archive contains more than {MaxArchiveEntries} entries.");
            }

            var entries = new Dictionary<string, ZipArchiveEntry>(
                StringComparer.OrdinalIgnoreCase);
            long totalUncompressed = 0;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    throw new PortableArchiveException(
                        "directory_entry_not_allowed",
                        "Portable archive v1 does not allow explicit directory entries.");
                }

                var path = ValidateArchivePath(entry.FullName);
                if (!entries.TryAdd(path, entry))
                {
                    throw new PortableArchiveException(
                        "duplicate_path",
                        $"Portable archive contains duplicate path '{path}'.");
                }

                if (entry.Length < 0 || entry.Length > MaxSingleEntryBytes)
                {
                    throw new PortableArchiveException(
                        "entry_too_large",
                        $"Portable archive entry '{path}' exceeds the v1 entry-size limit.");
                }

                totalUncompressed = checked(totalUncompressed + entry.Length);
                if (totalUncompressed > MaxUncompressedBytes)
                {
                    throw new PortableArchiveException(
                        "archive_expands_too_large",
                        "Portable archive declares too much uncompressed data.");
                }

                if (entry.Length > 1024 * 1024)
                {
                    if (entry.CompressedLength <= 0
                        || entry.Length / (double)entry.CompressedLength > MaxCompressionRatio)
                    {
                        throw new PortableArchiveException(
                            "suspicious_compression",
                            $"Portable archive entry '{path}' has a suspicious compression ratio.");
                    }
                }
            }

            if (!entries.TryGetValue(
                PortableArchiveFormat.ManifestPath,
                out var manifestEntry))
            {
                throw new PortableArchiveException(
                    "missing_manifest",
                    "Portable archive is missing manifest.json.");
            }

            var manifestBytes = await ReadEntryBytesAsync(
                manifestEntry,
                MaxManifestBytes,
                ct);
            var manifest = Deserialize<PortableArchiveManifest>(
                manifestBytes,
                "malformed_manifest",
                "Portable archive manifest is malformed.");

            ValidateManifest(manifest);

            if (!entries.TryGetValue(manifest.Data.Path, out var dataEntry))
            {
                throw new PortableArchiveException(
                    "missing_data",
                    $"Portable archive is missing '{manifest.Data.Path}'.");
            }

            if (dataEntry.Length != manifest.Data.Length)
            {
                throw new PortableArchiveException(
                    "data_length_mismatch",
                    "Portable archive relational payload length does not match its manifest.");
            }

            var dataBytes = await ReadEntryBytesAsync(dataEntry, MaxDataBytes, ct);
            var dataHash = Sha256(dataBytes);
            if (!FixedHashEquals(dataHash, manifest.Data.Sha256))
            {
                throw new PortableArchiveException(
                    "data_checksum_mismatch",
                    "Portable archive relational payload failed SHA-256 verification.");
            }

            var data = Deserialize<PortableLibraryData>(
                dataBytes,
                "malformed_data",
                "Portable archive relational payload is malformed.");

            ValidatePortableData(data);

            var actualCounts = CountsFor(data);
            if (actualCounts != manifest.Counts)
            {
                throw new PortableArchiveException(
                    "count_mismatch",
                    "Portable archive manifest counts do not match relational data.");
            }

            ValidateMediaManifest(manifest, data);

            var stagedMediaBytes = manifest.Media.Sum(media => media.Length);
            EnsureTempExtractionCapacity(tempRoot, stagedMediaBytes);

            var expectedPaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                PortableArchiveFormat.ManifestPath,
                PortableArchiveFormat.DataPath,
            };
            foreach (var media in manifest.Media)
                expectedPaths.Add(media.Path);

            var missing = expectedPaths
                .Where(path => !entries.ContainsKey(path))
                .OrderBy(path => path)
                .FirstOrDefault();
            if (missing is not null)
            {
                throw new PortableArchiveException(
                    "missing_referenced_media",
                    $"Portable archive is missing referenced entry '{missing}'.");
            }

            var unexpected = entries.Keys
                .Where(path => !expectedPaths.Contains(path))
                .OrderBy(path => path)
                .FirstOrDefault();
            if (unexpected is not null)
            {
                throw new PortableArchiveException(
                    "unexpected_entry",
                    $"Portable archive contains unexpected entry '{unexpected}'.");
            }

            var stageRoot = Path.Combine(tempRoot, "media-stage");
            Directory.CreateDirectory(stageRoot);
            var staged = new List<StagedPortableMedia>(manifest.Media.Count);

            for (var index = 0; index < manifest.Media.Count; index++)
            {
                var descriptor = manifest.Media[index];
                var entry = entries[descriptor.Path];

                if (entry.Length != descriptor.Length)
                {
                    throw new PortableArchiveException(
                        "media_length_mismatch",
                        $"Portable media '{descriptor.Path}' length does not match its manifest.");
                }

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

                if (copied.Length != descriptor.Length
                    || !FixedHashEquals(copied.Sha256, descriptor.Sha256))
                {
                    throw new PortableArchiveException(
                        "media_checksum_mismatch",
                        $"Portable media '{descriptor.Path}' failed integrity verification.");
                }

                staged.Add(new StagedPortableMedia(descriptor, stagedPath));
            }

            return new ValidatedPortableArchive(manifest, data, staged);
        }
    }

    private static void ValidateManifest(PortableArchiveManifest manifest)
    {
        if (!string.Equals(
            manifest.Format,
            PortableArchiveFormat.Name,
            StringComparison.Ordinal))
        {
            throw new PortableArchiveException(
                "unsupported_format",
                "Archive is not a Nostos portable archive.");
        }

        if (manifest.FormatVersion != PortableArchiveFormat.Version)
        {
            throw new PortableArchiveException(
                "unsupported_version",
                $"Portable archive format version {manifest.FormatVersion} is not supported. "
                + $"This build supports version {PortableArchiveFormat.Version}.");
        }

        if (manifest.DataVersion is not (1 or 2 or 3))
        {
            throw new PortableArchiveException(
                "unsupported_data_version",
                $"Portable archive data version {manifest.DataVersion} is not supported.");
        }

        if (manifest.Counts is null || manifest.Data is null || manifest.Media is null)
        {
            throw new PortableArchiveException(
                "malformed_manifest",
                "Portable archive manifest is incomplete.");
        }

        if (!string.Equals(
            manifest.Data.Path,
            PortableArchiveFormat.DataPath,
            StringComparison.Ordinal))
        {
            throw new PortableArchiveException(
                "invalid_data_path",
                "Portable archive relational payload path is not canonical.");
        }

        if (manifest.Data.Length < 0 || manifest.Data.Length > MaxDataBytes)
        {
            throw new PortableArchiveException(
                "data_too_large",
                "Portable archive relational payload exceeds the v1 limit.");
        }

        RequireSha256(manifest.Data.Sha256, manifest.Data.Path);
    }

    private static void ValidateMediaManifest(
        PortableArchiveManifest manifest,
        PortableLibraryData data)
    {
        var bookIds = data.Books.Select(x => x.Id).ToHashSet();
        var mediaKeys = new HashSet<(Guid BookId, string Kind)>();
        var mediaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var media in manifest.Media)
        {
            if (!bookIds.Contains(media.BookId))
            {
                throw new PortableArchiveException(
                    "media_unknown_book",
                    $"Portable media '{media.Path}' references an unknown book.");
            }

            if (media.Kind is not (
                PortableArchiveFormat.BookMediaKind
                or PortableArchiveFormat.CoverMediaKind))
            {
                throw new PortableArchiveException(
                    "invalid_media_kind",
                    $"Portable media '{media.Path}' has an unsupported kind.");
            }

            if (!mediaKeys.Add((media.BookId, media.Kind)))
            {
                throw new PortableArchiveException(
                    "duplicate_media",
                    $"Book {media.BookId} has duplicate '{media.Kind}' media.");
            }

            if (!mediaPaths.Add(media.Path))
            {
                throw new PortableArchiveException(
                    "duplicate_path",
                    $"Portable archive contains duplicate media path '{media.Path}'.");
            }

            if (Path.GetFileName(media.FileName) != media.FileName
                || media.FileName.Contains('\\')
                || media.FileName.Contains('/'))
            {
                throw new PortableArchiveException(
                    "invalid_media_filename",
                    $"Portable media '{media.Path}' has an unsafe filename.");
            }

            var extension = Path.GetExtension(media.FileName).ToLowerInvariant();
            var canonicalFileName = $"{media.Kind}{extension}";
            if (!string.Equals(
                media.FileName,
                canonicalFileName,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new PortableArchiveException(
                    "invalid_media_filename",
                    $"Portable media '{media.Path}' filename is not canonical.");
            }

            try
            {
                if (media.Kind == PortableArchiveFormat.BookMediaKind)
                    BookAssetFormats.RequireBookExtension(media.FileName);
                else
                    BookAssetFormats.RequireCoverExtension(media.FileName);
            }
            catch (InvalidOperationException exception)
            {
                throw new PortableArchiveException(
                    "invalid_media_filename",
                    $"Portable media '{media.Path}' uses an unsupported extension.",
                    exception);
            }

            var expectedPath = MediaPath(media.BookId, media.Kind, extension);
            if (!string.Equals(
                media.Path,
                expectedPath,
                StringComparison.Ordinal))
            {
                throw new PortableArchiveException(
                    "invalid_media_path",
                    $"Portable media path '{media.Path}' is not canonical.");
            }

            if (media.Length < 0 || media.Length > MaxSingleEntryBytes)
            {
                throw new PortableArchiveException(
                    "entry_too_large",
                    $"Portable media '{media.Path}' exceeds the v1 entry-size limit.");
            }

            RequireSha256(media.Sha256, media.Path);
        }

        foreach (var book in data.Books)
        {
            var hasBook = mediaKeys.Contains((
                book.Id,
                PortableArchiveFormat.BookMediaKind));
            var hasCover = mediaKeys.Contains((
                book.Id,
                PortableArchiveFormat.CoverMediaKind));

            if (book.HasBookFile != hasBook)
            {
                throw new PortableArchiveException(
                    "missing_referenced_media",
                    $"Book {book.Id} book-file state does not match the archive media manifest.");
            }

            if (book.HasCover != hasCover)
            {
                throw new PortableArchiveException(
                    "missing_referenced_media",
                    $"Book {book.Id} cover state does not match the archive media manifest.");
            }
        }
    }

    private static void ValidatePortableData(PortableLibraryData data)
    {
        if (data.Version is not (1 or 2 or 3))
        {
            throw new PortableArchiveException(
                "unsupported_data_version",
                $"Portable relational data version {data.Version} is not supported.");
        }

        if (data.Works is null
            || data.Books is null
            || data.Collections is null
            || data.BookCollections is null
            || data.Notes is null
            || data.Topics is null
            || data.NoteTopics is null
            || data.Writings is null
            || data.BookAcquisitions is null
            || (data.Version >= 2 && data.WritingNotes is null)
            || (data.Version >= 3 && data.NoteImportBookLinks is null))
        {
            throw new PortableArchiveException(
                "malformed_data",
                "Portable relational data is incomplete.");
        }

        RequireUniqueGuids(data.Works.Select(x => x.Id), "work");
        RequireUniqueGuids(data.Books.Select(x => x.Id), "book");
        RequireUniqueGuids(data.Collections.Select(x => x.Id), "collection");
        RequireUniqueGuids(data.Notes.Select(x => x.Id), "note");
        RequireUniqueGuids(data.Topics.Select(x => x.Id), "topic");
        RequireUniqueGuids(data.Writings.Select(x => x.Id), "writing");
        RequireUniqueGuids(data.BookAcquisitions.Select(x => x.Id), "book acquisition");
        if (data.NoteImportBookLinks is not null)
        {
            RequireUniqueGuids(data.NoteImportBookLinks.Select(x => x.Id), "note import book link");
        }

        var workIds = data.Works.Select(x => x.Id).ToHashSet();
        var bookIds = data.Books.Select(x => x.Id).ToHashSet();
        var collectionIds = data.Collections.Select(x => x.Id).ToHashSet();
        var noteIds = data.Notes.Select(x => x.Id).ToHashSet();
        var topicIds = data.Topics.Select(x => x.Id).ToHashSet();
        var writingIds = data.Writings.Select(x => x.Id).ToHashSet();

        var normalizedIsbns = new HashSet<string>(StringComparer.Ordinal);
        var normalizedAsins = new HashSet<string>(StringComparer.Ordinal);

        foreach (var book in data.Books)
        {
            if (!workIds.Contains(book.WorkId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    $"Book {book.Id} references unknown work {book.WorkId}.");
            }

            if (book.Type is not ("physical" or "ebook" or "audiobook"))
            {
                throw new PortableArchiveException(
                    "unsupported_book_type",
                    $"Book {book.Id} has unsupported type '{book.Type}'.");
            }

            if (!Enum.TryParse<BookStatus>(book.Status, ignoreCase: true, out _))
            {
                throw new PortableArchiveException(
                    "invalid_book_status",
                    $"Book {book.Id} has invalid status '{book.Status}'.");
            }

            if (book.Type is "physical" or "ebook")
            {
                var normalized = BookIdentityNormalizer.NormalizeIsbn(book.Isbn);
                if (normalized is not null && !normalizedIsbns.Add(normalized))
                {
                    throw new PortableArchiveException(
                        "duplicate_book_identity",
                        $"Portable archive contains duplicate normalized ISBN '{normalized}'.");
                }
            }

            if (book.Type == "audiobook")
            {
                var normalized = BookIdentityNormalizer.NormalizeAsin(book.Asin);
                if (normalized is not null && !normalizedAsins.Add(normalized))
                {
                    throw new PortableArchiveException(
                        "duplicate_book_identity",
                        $"Portable archive contains duplicate normalized ASIN '{normalized}'.");
                }
            }
        }

        var bookCollectionKeys = new HashSet<(Guid BookId, Guid CollectionId)>();
        foreach (var membership in data.BookCollections)
        {
            if (!bookIds.Contains(membership.BookId)
                || !collectionIds.Contains(membership.CollectionId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    "Portable archive contains a collection membership with a missing endpoint.");
            }

            if (!bookCollectionKeys.Add((
                membership.BookId,
                membership.CollectionId)))
            {
                throw new PortableArchiveException(
                    "duplicate_relationship",
                    "Portable archive contains a duplicate book/collection membership.");
            }
        }

        foreach (var collection in data.Collections)
        {
            if (collection.ParentId is { } parentId)
            {
                if (!collectionIds.Contains(parentId) || parentId == collection.Id)
                {
                    throw new PortableArchiveException(
                        "malformed_relationship",
                        $"Collection {collection.Id} has an invalid parent.");
                }
            }
        }
        RequireAcyclic(
            data.Collections.ToDictionary(x => x.Id, x => x.ParentId),
            "collection");

        foreach (var note in data.Notes)
        {
            if (!bookIds.Contains(note.BookId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    $"Note {note.Id} references unknown book {note.BookId}.");
            }
        }

        var topicNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var topic in data.Topics)
        {
            if (!topicNames.Add(topic.Topic))
            {
                throw new PortableArchiveException(
                    "duplicate_topic",
                    $"Portable archive contains duplicate topic '{topic.Topic}'.");
            }
        }

        var noteTopicKeys = new HashSet<(Guid NoteId, Guid TopicId)>();
        foreach (var link in data.NoteTopics)
        {
            if (!noteIds.Contains(link.NoteId) || !topicIds.Contains(link.TopicId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    "Portable archive contains a note/topic link with a missing endpoint.");
            }

            if (!noteTopicKeys.Add((link.NoteId, link.TopicId)))
            {
                throw new PortableArchiveException(
                    "duplicate_relationship",
                    "Portable archive contains a duplicate note/topic link.");
            }
        }

        var writingNoteKeys = new HashSet<(Guid WritingId, Guid NoteId)>();
        foreach (var link in data.WritingNotes ?? [])
        {
            if (!writingIds.Contains(link.WritingId) || !noteIds.Contains(link.NoteId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    "Portable archive contains a writing/note link with a missing endpoint.");
            }

            if (!writingNoteKeys.Add((link.WritingId, link.NoteId)))
            {
                throw new PortableArchiveException(
                    "duplicate_relationship",
                    "Portable archive contains a duplicate writing/note link.");
            }
        }

        foreach (var writing in data.Writings)
        {
            if (!Enum.TryParse<WritingType>(writing.Type, ignoreCase: true, out _))
            {
                throw new PortableArchiveException(
                    "invalid_writing_type",
                    $"Writing {writing.Id} has invalid type '{writing.Type}'.");
            }

            if (writing.ParentId is { } parentId)
            {
                if (!writingIds.Contains(parentId) || parentId == writing.Id)
                {
                    throw new PortableArchiveException(
                        "malformed_relationship",
                        $"Writing {writing.Id} has an invalid parent.");
                }
            }
        }
        RequireAcyclic(
            data.Writings.ToDictionary(x => x.Id, x => x.ParentId),
            "writing");

        var acquisitionBooks = new HashSet<Guid>();
        var acquisitionKeys = new HashSet<(string Provider, string External, string Asset)>();
        foreach (var acquisition in data.BookAcquisitions)
        {
            if (!bookIds.Contains(acquisition.BookId))
            {
                throw new PortableArchiveException(
                    "malformed_relationship",
                    $"Acquisition {acquisition.Id} references an unknown book.");
            }

            if (!acquisitionBooks.Add(acquisition.BookId))
            {
                throw new PortableArchiveException(
                    "duplicate_relationship",
                    $"Book {acquisition.BookId} has multiple acquisition records.");
            }

            if (!acquisitionKeys.Add((
                acquisition.ProviderId,
                acquisition.ExternalId,
                acquisition.AssetId)))
            {
                throw new PortableArchiveException(
                    "duplicate_acquisition",
                    "Portable archive contains duplicate acquisition provenance.");
            }
        }

        if (data.NoteImportBookLinks is not null)
        {
            var importLinkKeys = new HashSet<(string Source, string SourceKey)>();
            foreach (var link in data.NoteImportBookLinks)
            {
                if (!bookIds.Contains(link.BookId))
                {
                    throw new PortableArchiveException(
                        "malformed_relationship",
                        $"Note import book link {link.Id} references an unknown book.");
                }

                if (!importLinkKeys.Add((link.Source, link.SourceKey)))
                {
                    throw new PortableArchiveException(
                        "duplicate_relationship",
                        $"Portable archive contains duplicate note import book link for source '{link.Source}' and key '{link.SourceKey}'.");
                }
            }
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

        if (actual != expected)
        {
            throw new PortableArchiveException(
                "integrity_failed",
                "Imported relational entity counts do not match the archive manifest.");
        }

        var workIds = await _db.Works.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        var bookRows = await _db.Books.AsNoTracking().ToListAsync(ct);
        var collectionIds = await _db.Collections.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        var noteIds = await _db.Notes.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        var topicIds = await _db.Topics.AsNoTracking().Select(x => x.Id).ToListAsync(ct);
        var writingRows = await _db.Writings.AsNoTracking().ToListAsync(ct);
        var acquisitionIds = await _db.BookAcquisitions.AsNoTracking().Select(x => x.Id).ToListAsync(ct);

        RequireSameIds(source.Works.Select(x => x.Id), workIds, "work");
        RequireSameIds(source.Books.Select(x => x.Id), bookRows.Select(x => x.Id), "book");
        RequireSameIds(source.Collections.Select(x => x.Id), collectionIds, "collection");
        RequireSameIds(source.Notes.Select(x => x.Id), noteIds, "note");
        RequireSameIds(source.Topics.Select(x => x.Id), topicIds, "topic");
        RequireSameIds(source.Writings.Select(x => x.Id), writingRows.Select(x => x.Id), "writing");
        RequireSameIds(source.BookAcquisitions.Select(x => x.Id), acquisitionIds, "acquisition");

        var expectedBookWorks = source.Books
            .Select(x => (x.Id, x.WorkId))
            .ToHashSet();
        var actualBookWorks = bookRows
            .Select(x => (x.Id, x.WorkId))
            .ToHashSet();
        if (!expectedBookWorks.SetEquals(actualBookWorks))
        {
            throw new PortableArchiveException(
                "integrity_failed",
                "Imported Work/Book relationships do not match the archive.");
        }

        var expectedMemberships = source.BookCollections
            .Select(x => (x.BookId, x.CollectionId))
            .ToHashSet();
        var actualMemberships = (await _db.BookCollections
            .AsNoTracking()
            .Select(x => new { x.BookId, x.CollectionId })
            .ToListAsync(ct))
            .Select(x => (x.BookId, x.CollectionId))
            .ToHashSet();
        if (!expectedMemberships.SetEquals(actualMemberships))
        {
            throw new PortableArchiveException(
                "integrity_failed",
                "Imported collection memberships do not match the archive.");
        }

        var expectedLinks = source.NoteTopics
            .Select(x => (x.NoteId, x.TopicId))
            .ToHashSet();
        var actualLinks = (await _db.NoteTopics
            .AsNoTracking()
            .Select(x => new { x.NoteId, x.TopicId })
            .ToListAsync(ct))
            .Select(x => (x.NoteId, x.TopicId))
            .ToHashSet();
        if (!expectedLinks.SetEquals(actualLinks))
        {
            throw new PortableArchiveException(
                "integrity_failed",
                "Imported Note/Topic links do not match the archive.");
        }

        var expectedWritingNotes = (source.WritingNotes ?? [])
            .Select(x => (x.WritingId, x.NoteId))
            .ToHashSet();
        var actualWritingNotes = (await _db.WritingNotes
            .AsNoTracking()
            .Select(x => new { x.WritingId, x.NoteId })
            .ToListAsync(ct))
            .Select(x => (x.WritingId, x.NoteId))
            .ToHashSet();
        if (!expectedWritingNotes.SetEquals(actualWritingNotes))
        {
            throw new PortableArchiveException(
                "integrity_failed",
                "Imported Writing/Note links do not match the archive.");
        }

        var expectedImportLinks = (source.NoteImportBookLinks ?? [])
            .Select(x => (x.Id, x.Source, x.SourceKey, x.BookId))
            .ToHashSet();
        var actualImportLinks = (await _db.NoteImportBookLinks
            .AsNoTracking()
            .Select(x => new { x.Id, x.Source, x.SourceKey, x.BookId })
            .ToListAsync(ct))
            .Select(x => (x.Id, x.Source, x.SourceKey, x.BookId))
            .ToHashSet();
        if (!expectedImportLinks.SetEquals(actualImportLinks))
        {
            throw new PortableArchiveException(
                "integrity_failed",
                "Imported note import book links do not match the archive.");
        }

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
            if (!string.Equals(actualType, expectedBook.Type, StringComparison.Ordinal)
                || book.Progress.LastLocation != expectedBook.Progress.LastLocation
                || book.Progress.ProgressPercent != expectedBook.Progress.ProgressPercent
                || book.Progress.Rating != expectedBook.Progress.Rating
                || book.Progress.IsFavorite != expectedBook.Progress.IsFavorite
                || book.Progress.PersonalReview != expectedBook.Progress.PersonalReview)
            {
                throw new PortableArchiveException(
                    "integrity_failed",
                    $"Imported book {book.Id} format or reading state does not match the archive.");
            }
        }

        var sourceWritings = source.Writings.ToDictionary(x => x.Id);
        foreach (var writing in writingRows)
        {
            var expectedWriting = sourceWritings[writing.Id];
            if (writing.ParentId != expectedWriting.ParentId
                || writing.Name != expectedWriting.Name
                || writing.Content != expectedWriting.Content
                || !string.Equals(
                    writing.Type.ToString(),
                    expectedWriting.Type,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new PortableArchiveException(
                    "integrity_failed",
                    $"Imported writing {writing.Id} does not match the archive.");
            }
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

            if (info is null || info.Length != descriptor.Length)
            {
                throw new PortableArchiveException(
                    "integrity_failed",
                    $"Imported media '{descriptor.Path}' is missing or has the wrong length.");
            }

            await using var opened = descriptor.Kind == PortableArchiveFormat.BookMediaKind
                ? await _assets.OpenBookFileAsync(descriptor.BookId, null, ct)
                : await _assets.OpenBookCoverAsync(descriptor.BookId, ct);

            if (opened is null)
            {
                throw new PortableArchiveException(
                    "integrity_failed",
                    $"Imported media '{descriptor.Path}' could not be reopened.");
            }

            var hash = await HashStreamAsync(
                opened.Content,
                descriptor.Length,
                ct);
            if (hash.Length != descriptor.Length
                || !FixedHashEquals(hash.Sha256, descriptor.Sha256))
            {
                throw new PortableArchiveException(
                    "integrity_failed",
                    $"Imported media '{descriptor.Path}' failed post-write SHA-256 verification.");
            }
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

    private static string MediaPath(Guid bookId, string kind, string extension) =>
        $"media/books/{bookId:N}/{kind}{extension.ToLowerInvariant()}";

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

    private static string ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith('\\')
            || path.Contains('\\')
            || path.Contains('\0')
            || path.Contains(':'))
        {
            throw new PortableArchiveException(
                "unsafe_archive_path",
                $"Portable archive contains unsafe path '{path}'.");
        }

        var segments = path.Split('/');
        if (segments.Any(segment =>
            segment.Length == 0
            || segment == "."
            || segment == ".."
            || segment.Length > 255))
        {
            throw new PortableArchiveException(
                "unsafe_archive_path",
                $"Portable archive contains unsafe path '{path}'.");
        }

        return string.Join('/', segments);
    }

    private static void RequireUniqueGuids(
        IEnumerable<Guid> ids,
        string entityName)
    {
        var seen = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (id == Guid.Empty || !seen.Add(id))
            {
                throw new PortableArchiveException(
                    "duplicate_id",
                    $"Portable archive contains an empty or duplicate {entityName} ID.");
            }
        }
    }

    private static void RequireAcyclic(
        IReadOnlyDictionary<Guid, Guid?> parents,
        string entityName)
    {
        var complete = new HashSet<Guid>();

        foreach (var start in parents.Keys)
        {
            if (complete.Contains(start))
                continue;

            var current = start;
            var chain = new HashSet<Guid>();
            while (parents.TryGetValue(current, out var parent) && parent is { } parentId)
            {
                if (!chain.Add(current))
                {
                    throw new PortableArchiveException(
                        "malformed_relationship",
                        $"Portable archive contains a cyclic {entityName} hierarchy.");
                }

                current = parentId;
            }

            foreach (var visited in chain)
                complete.Add(visited);
        }
    }

    private static void RequireSameIds(
        IEnumerable<Guid> expected,
        IEnumerable<Guid> actual,
        string entityName)
    {
        if (!expected.ToHashSet().SetEquals(actual))
        {
            throw new PortableArchiveException(
                "integrity_failed",
                $"Imported {entityName} IDs do not match the archive.");
        }
    }

    private static void RequireSha256(string hash, string path)
    {
        if (hash is null
            || hash.Length != 64
            || hash.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new PortableArchiveException(
                "invalid_checksum",
                $"Portable archive entry '{path}' has an invalid SHA-256 checksum.");
        }
    }

    private static bool FixedHashEquals(string left, string right)
    {
        if (left.Length != right.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }

    private static T Deserialize<T>(
        byte[] bytes,
        string code,
        string message)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
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

    private static async Task<byte[]> ReadEntryBytesAsync(
        ZipArchiveEntry entry,
        long maxBytes,
        CancellationToken ct)
    {
        if (entry.Length > maxBytes)
        {
            throw new PortableArchiveException(
                "entry_too_large",
                $"Portable archive entry '{entry.FullName}' exceeds its v1 limit.");
        }

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
            if (total > maxBytes)
            {
                throw new PortableArchiveException(
                    "entry_too_large",
                    $"Portable archive entry '{entry.FullName}' expands beyond its v1 limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return output.ToArray();
    }

    private static async Task<(long Length, string Sha256)> DescribeFileAsync(
        string path,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await HashStreamAsync(stream, MaxDataBytes, ct);
    }

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(
        Stream source,
        Stream destination,
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
            if (total > maxBytes)
            {
                throw new PortableArchiveException(
                    "entry_too_large",
                    "Portable media exceeds the v1 entry-size limit.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return (
            total,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
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
            if (total > maxBytes)
            {
                throw new PortableArchiveException(
                    "entry_too_large",
                    "Portable media exceeds the expected size.");
            }

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
