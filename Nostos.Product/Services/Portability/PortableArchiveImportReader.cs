using System.Data;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Portability.Verification;
using Nostos.Product.BookText;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// The legacy compatibility import: bounds the uploaded archive into local scratch,
/// prepares it through the shared <see cref="PortableArchiveReader"/>, applies the
/// verified payload inside one serializable transaction, verifies the result with the
/// shared verifier, and only then commits.
/// </summary>
/// <remarks>
/// <para>
/// The whole-archive local copy is the compressed scratch spool only; media streams
/// from the archive into staging and, one item at a time, into asset storage. The
/// scratch lease is the ownership signal cleanup uses: an import may legally run
/// longer than the scratch TTL.
/// </para>
/// <para>
/// Post-apply verification is <see cref="Verification.PortableImportIntegrityVerifier"/>,
/// not a private comparison: the same tables that admit an activation candidate also
/// guard a compatibility restore. A failure rolls the transaction back and deletes
/// every media object already written before rethrowing.
/// </para>
/// </remarks>
internal sealed class PortableArchiveImportReader(
    NostosDbContext db,
    IBookAssetStorage assets,
    IBookTrackStorage? trackStorage,
    ILogger<PortableArchiveService> logger,
    IBookTextIngestionScheduler? bookTextScheduler,
    TimeProvider timeProvider)
{
    private const int CopyBufferSize = 128 * 1024;

    private readonly NostosDbContext _db = db;
    private readonly IBookAssetStorage _assets = assets;
    private readonly IBookTrackStorage? _tracks = trackStorage;
    private readonly ILogger<PortableArchiveService> _logger = logger;
    private readonly IBookTextIngestionScheduler? _bookTextScheduler = bookTextScheduler;
    private readonly TimeProvider _timeProvider = timeProvider;

    internal async Task<PortableImportResult> ImportAsync(
        Stream source,
        string scratchRoot,
        Func<string, long?>? scratchFreeSpaceProbe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The import source must be readable.", nameof(source));

        // The directory name is part of the legacy sweep contract
        // (LegacyPortabilityScratchCleanup.LegacyImportScratchPrefix), so the
        // spool keeps its shape and only its root moves: ScratchRoot comes from
        // the host's product scratch root (issue #1596), never /tmp by default.
        var tempRoot = Path.Combine(
            scratchRoot,
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
                scratchFreeSpaceProbe);
            var reader = new PortableArchiveReader(timeProvider: _timeProvider);

            var prepared = await PrepareStagedImportAsync(
                reader,
                archiveSource,
                staging,
                cancellationToken);

            var data = await ReadStagedDataAsync(
                staging,
                prepared.StagingId,
                prepared.Media,
                cancellationToken);
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

                await PortableImportIntegrityVerifier.VerifyAppliedAsync(
                    _db,
                    _assets,
                    _tracks,
                    data,
                    manifest.Counts,
                    manifest.Media,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                committed = true;

                // An older archive can hold topic names that differ only in
                // case; restored exactly and verified, they are joined now. A
                // failure here leaves them to the next merge pass, not a failed
                // restore.
                try
                {
                    await new Nostos.Backend.Data.Repositories.TopicRepository(_db)
                        .MergeCaseVariantsAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        "Portable import could not merge topic case variants; exception type {ExceptionType}.",
                        exception.GetType().Name);
                }

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
        IReadOnlyList<PortablePreparedMedia> stagedMedia,
        CancellationToken cancellationToken) =>
        PortableLibraryDatabaseMaterializer.ReadRelationalDataAsync(
            staging,
            stagingId,
            stagedMedia,
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
                .DeserializeAsync<PortableArchiveManifest>(staged, PortableArchiveJson.Options, cancellationToken)
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
            || await _db.ReadingListEntries.AnyAsync(ct)
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
