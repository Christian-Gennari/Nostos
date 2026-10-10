using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Nostos.Backend.Services;

public class FileStorageService : IFileStorageService, IBookAssetStorage, IBookTrackStorage
{
    private const int CopyBufferSize = 81920;

    private readonly string _root;
    private readonly ILogger<FileStorageService> _logger;

    private static readonly IReadOnlySet<string> AllowedBookExtensions = BookAssetFormats.BookExtensions;
    private static readonly IReadOnlySet<string> AllowedCoverExtensions = BookAssetFormats.CoverExtensions;

    /// <summary>
    /// Compatibility wrapper. New callers should use <see cref="BookAssetFormats"/>.
    /// </summary>
    public static bool IsAllowedUpload(string contentType, string fileName) =>
        BookAssetFormats.IsAllowedUpload(contentType, fileName);

    /// <summary>
    /// Maps a file path to the correct Content-Type for download responses.
    /// Delegates to <see cref="MediaTypeMap"/> so the download endpoints and the
    /// OPDS feed can never disagree about a book file's media type.
    /// </summary>
    public static string GetContentType(string filePath) => MediaTypeMap.ForBookFile(filePath);

    public FileStorageService(
        IWebHostEnvironment env,
        IOptions<FileStorageOptions> options,
        ILogger<FileStorageService> logger
    )
    {
        var storageOptions = options.Value;
        if (string.Equals(env.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(storageOptions.BooksRoot))
        {
            throw new InvalidOperationException(
                "Testing hosts must configure 'Storage:BooksRoot' to a disposable directory; " +
                "the implicit Storage/books default is reserved for real application data.");
        }

        _root = FileStorageOptions.ResolveBooksRoot(env.ContentRootPath, storageOptions);
        _logger = logger;
        Directory.CreateDirectory(_root);
    }

    public string StorageRoot => _root;

    public async Task<string> SaveBookFileAsync(Guid bookId, IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        return await SaveBookFileAsync(bookId, stream, file.FileName);
    }

    public async Task<string> SaveBookFileAsync(
        Guid bookId,
        Stream content,
        string fileName,
        CancellationToken ct = default
    )
    {
        var ext = BookAssetFormats.RequireBookExtension(fileName);

        var bookFolder = BookFolder(bookId);
        Directory.CreateDirectory(bookFolder);

        var finalPath = Path.Combine(bookFolder, $"book{ext}");
        var tempPath = TempSiblingPath(bookFolder, finalPath);

        try
        {
            await WriteStreamAsync(content, tempPath, ct);

            // Only once the transfer is complete do we disturb what is already
            // there. Replacing is a rename rather than a truncate, so a reader
            // can never observe a partially written book file.
            CommitStagedBookFile(bookFolder, tempPath, finalPath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return finalPath;
    }

    public async Task<string> AdoptBookFileAsync(
        Guid bookId,
        string sourcePath,
        string fileName,
        CancellationToken ct = default
    )
    {
        var ext = Path.GetExtension(fileName);
        if (!AllowedBookExtensions.Contains(ext))
            throw new InvalidOperationException($"Unsupported file type: {ext}");

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The staged file to adopt does not exist.", sourcePath);

        var bookFolder = BookFolder(bookId);
        Directory.CreateDirectory(bookFolder);

        var finalPath = Path.Combine(bookFolder, $"book{ext}");
        var tempPath = TempSiblingPath(bookFolder, finalPath);

        try
        {
            try
            {
                // Staging normally sits on the same volume as the library, so
                // committing is a rename: the bytes are never copied, and the
                // final name never exists in a partial state.
                File.Move(sourcePath, tempPath, overwrite: true);
            }
            catch (IOException)
            {
                // Different volume (staging pointed elsewhere): fall back to a
                // copy, still via the temporary sibling.
                await using var source = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
                await WriteStreamAsync(source, tempPath, ct);
                TryDelete(sourcePath);
            }

            CommitStagedBookFile(bookFolder, tempPath, finalPath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return finalPath;
    }

    public FileStream? GetBookFile(Guid bookId)
    {
        var file = GetBookFileName(bookId);
        return file is null ? null : new FileStream(file, FileMode.Open, FileAccess.Read);
    }

    public Task<StoredAssetInfo?> GetBookFileInfoAsync(
        Guid bookId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = GetBookFileName(bookId);
        return Task.FromResult(path is null ? null : InfoFromPath(path, MediaTypeMap.ForBookFile(path)));
    }

    public Task<StoredAssetRead?> OpenBookFileAsync(
        Guid bookId,
        StorageByteRange? range = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = GetBookFileName(bookId);
        if (path is null)
            return Task.FromResult<StoredAssetRead?>(null);

        return Task.FromResult<StoredAssetRead?>(
            OpenPath(path, MediaTypeMap.ForBookFile(path), range));
    }

    public string? GetBookFileName(Guid bookId)
    {
        var folder = Path.Combine(_root, bookId.ToString());
        if (!Directory.Exists(folder))
            return null;

        // A multi-track audiobook keeps its tracks in this folder too, and
        // they share an extension with a single-file audiobook. A track is
        // never the primary file.
        return Directory
            .EnumerateFiles(folder)
            .FirstOrDefault(f => AllowedBookExtensions.Contains(Path.GetExtension(f))
                && !BookTrackFormats.IsTrackFileName(Path.GetFileName(f)));
    }

    public bool DeleteBookFile(Guid bookId)
    {
        var file = GetBookFileName(bookId);
        if (file is null)
            return false;

        TryDelete(file);
        return true;
    }

    public void DeleteBookFiles(Guid bookId)
    {
        var bookFolder = Path.Combine(_root, bookId.ToString());
        if (Directory.Exists(bookFolder))
            Directory.Delete(bookFolder, true);
    }

    public Task<bool> DeleteBookFileAsync(Guid bookId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(DeleteBookFile(bookId));
    }

    public Task DeleteBookFilesAsync(Guid bookId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        DeleteBookFiles(bookId);
        return Task.CompletedTask;
    }

    public async Task<string> SaveBookCoverAsync(Guid bookId, IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        return await SaveBookCoverAsync(bookId, stream, file.FileName);
    }

    public async Task<string> SaveBookCoverAsync(
        Guid bookId,
        Stream content,
        string fileName,
        CancellationToken ct = default
    )
    {
        var ext = BookAssetFormats.RequireCoverExtension(fileName);

        var bookFolder = BookFolder(bookId);
        Directory.CreateDirectory(bookFolder);

        var finalPath = Path.Combine(bookFolder, $"cover{ext}");
        var tempPath = TempSiblingPath(bookFolder, finalPath);

        try
        {
            await WriteStreamAsync(content, tempPath, ct);

            foreach (var existing in Directory.EnumerateFiles(bookFolder, "cover.*"))
            {
                if (existing.EndsWith(".partial", StringComparison.Ordinal)
                    || string.Equals(existing, finalPath, StringComparison.Ordinal))
                    continue;

                TryDelete(existing);
            }

            // Replacing the cover must also drop its cached thumbnails, or the
            // library grid keeps rendering the previous artwork.
            foreach (var thumbnail in Directory.EnumerateFiles(bookFolder, "cover-thumb-*.webp"))
                TryDelete(thumbnail);

            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return finalPath;
    }

    public string? GetBookCoverPath(Guid bookId)
    {
        var folder = Path.Combine(_root, bookId.ToString());
        if (!Directory.Exists(folder))
            return null;

        return Directory
            .EnumerateFiles(folder, "cover.*")
            .FirstOrDefault(f => AllowedCoverExtensions.Contains(Path.GetExtension(f)));
    }

    public Task<StoredAssetInfo?> GetBookCoverInfoAsync(
        Guid bookId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = GetBookCoverPath(bookId);
        return Task.FromResult(path is null ? null : InfoFromPath(path, MediaTypeMap.ForCover(path)));
    }

    public Task<StoredAssetRead?> OpenBookCoverAsync(
        Guid bookId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = GetBookCoverPath(bookId);
        return Task.FromResult<StoredAssetRead?>(
            path is null ? null : OpenPath(path, MediaTypeMap.ForCover(path), range: null));
    }

    /// <summary>
    /// A cached, resized WebP copy of the cover for list views.
    ///
    /// The cache is invalidated by <see cref="SaveBookCoverAsync(Guid, Stream, string, CancellationToken)"/>,
    /// which discards these thumbnails whenever the cover itself is replaced —
    /// otherwise a replaced cover would keep showing its old artwork in the
    /// library grid while the detail view showed the new one.
    /// </summary>
    public async Task<string?> GetBookCoverThumbnailPathAsync(
        Guid bookId,
        int width,
        CancellationToken ct = default
    )
    {
        var coverPath = GetBookCoverPath(bookId);
        if (coverPath is null)
            return null;

        var safeWidth = Math.Clamp(width, 120, 640);
        var thumbnailPath = Path.Combine(
            Path.GetDirectoryName(coverPath)!,
            $"cover-thumb-{safeWidth}.webp"
        );

        if (File.Exists(thumbnailPath))
            return thumbnailPath;

        using var image = await Image.LoadAsync(coverPath, ct);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Size = new Size(safeWidth, 0),
            Mode = ResizeMode.Max,
        }));

        await image.SaveAsWebpAsync(
            thumbnailPath,
            new WebpEncoder { Quality = 82 },
            ct
        );

        return thumbnailPath;
    }

    public async Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(
        Guid bookId,
        int width,
        CancellationToken ct = default)
    {
        var path = await GetBookCoverThumbnailPathAsync(bookId, width, ct);
        return path is null
            ? null
            : InfoFromPath(path, "image/webp");
    }

    public async Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(
        Guid bookId,
        int width,
        CancellationToken ct = default)
    {
        var path = await GetBookCoverThumbnailPathAsync(bookId, width, ct);
        return path is null
            ? null
            : OpenPath(path, "image/webp", range: null);
    }

    public bool DeleteCover(Guid bookId)
    {
        var coverPath = GetBookCoverPath(bookId);
        if (coverPath is null)
            return false;

        File.Delete(coverPath);
        return true;
    }

    public Task<bool> DeleteCoverAsync(Guid bookId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(DeleteCover(bookId));
    }

    // ------------------------------------------------------------------
    // Tracks (multi-track audiobooks)
    // ------------------------------------------------------------------

    public async Task<string> SaveTrackAsync(
        Guid bookId,
        int number,
        Stream content,
        string fileName,
        CancellationToken ct = default)
    {
        var (folder, finalPath) = TrackDestination(bookId, number, fileName);
        var tempPath = TempSiblingPath(folder, finalPath);

        try
        {
            await WriteStreamAsync(content, tempPath, ct);
            CommitStagedTrack(folder, number, tempPath, finalPath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return Path.GetFileName(finalPath);
    }

    public async Task<string> AdoptTrackAsync(
        Guid bookId,
        int number,
        string sourcePath,
        string fileName,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The staged track to adopt does not exist.", sourcePath);

        var (folder, finalPath) = TrackDestination(bookId, number, fileName);
        var tempPath = TempSiblingPath(folder, finalPath);

        try
        {
            try
            {
                File.Move(sourcePath, tempPath, overwrite: true);
            }
            catch (IOException)
            {
                await using var source = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
                await WriteStreamAsync(source, tempPath, ct);
                TryDelete(sourcePath);
            }

            CommitStagedTrack(folder, number, tempPath, finalPath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return Path.GetFileName(finalPath);
    }

    public Task<StoredAssetInfo?> GetTrackInfoAsync(
        Guid bookId,
        int number,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = FindTrackPath(bookId, number);
        return Task.FromResult(path is null ? null : InfoFromPath(path, MediaTypeMap.ForBookFile(path)));
    }

    public Task<StoredAssetRead?> OpenTrackAsync(
        Guid bookId,
        int number,
        StorageByteRange? range = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = FindTrackPath(bookId, number);
        return Task.FromResult<StoredAssetRead?>(
            path is null ? null : OpenPath(path, MediaTypeMap.ForBookFile(path), range));
    }

    public Task DeleteTracksAsync(Guid bookId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var folder = BookFolder(bookId);
        if (!Directory.Exists(folder))
            return Task.CompletedTask;

        foreach (var path in Directory.EnumerateFiles(folder, BookTrackFormats.FileNamePrefix + "*"))
            TryDelete(path);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tracks are stored flat in the book folder, beside its cover, under
    /// <see cref="BookTrackFormats.CanonicalFileName"/>. Flat on purpose:
    /// backup, restore and library-switch retention all treat a book folder as
    /// a set of files and refuse or ignore unexpected subfolders.
    /// </summary>
    private (string Folder, string FinalPath) TrackDestination(Guid bookId, int number, string fileName)
    {
        var extension = BookTrackFormats.RequireTrackExtension(fileName);
        var folder = BookFolder(bookId);
        Directory.CreateDirectory(folder);
        return (folder, Path.Combine(folder, BookTrackFormats.CanonicalFileName(number, extension)));
    }

    private string? FindTrackPath(Guid bookId, int number)
    {
        if (number is < 1 or > BookTrackList.MaxTracks)
            return null;

        var folder = BookFolder(bookId);
        if (!Directory.Exists(folder))
            return null;

        return Directory
            .EnumerateFiles(folder, $"{BookTrackFormats.FileNamePrefix}{number:D4}.*")
            .FirstOrDefault(path => BookTrackFormats.IsCanonicalFileName(Path.GetFileName(path), number));
    }

    /// <summary>
    /// One file per track number: the staged file is renamed into place first
    /// and a same-numbered track in another format is removed only afterwards.
    /// </summary>
    private void CommitStagedTrack(string folder, int number, string tempPath, string finalPath)
    {
        File.Move(tempPath, finalPath, overwrite: true);

        foreach (var existing in Directory.EnumerateFiles(folder, $"{BookTrackFormats.FileNamePrefix}{number:D4}.*"))
        {
            if (existing.EndsWith(".partial", StringComparison.Ordinal)
                || string.Equals(existing, finalPath, StringComparison.Ordinal))
                continue;

            TryDelete(existing);
        }
    }

    private static StoredAssetInfo InfoFromPath(string path, string contentType)
    {
        var file = new FileInfo(path);
        return new StoredAssetInfo(
            FileName: file.Name,
            ContentType: contentType,
            Length: file.Length,
            EntityTag: $"\"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}\"",
            LastModified: new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static StoredAssetRead OpenPath(
        string path,
        string contentType,
        StorageByteRange? range)
    {
        var info = InfoFromPath(path, contentType);
        if (range is { } requested && requested.EndInclusive >= info.Length)
            throw new ArgumentOutOfRangeException(nameof(range));

        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (range is { } actual)
            stream.Seek(actual.Start, SeekOrigin.Begin);

        return new StoredAssetRead(info, stream, range);
    }

    private string BookFolder(Guid bookId) => Path.Combine(_root, bookId.ToString());

    /// <summary>
    /// The in-progress file lives beside its destination, never in a shared
    /// temp directory: the final move is then a same-volume rename, which is
    /// atomic, instead of a copy that could be interrupted half way.
    /// </summary>
    private static string TempSiblingPath(string folder, string finalPath) =>
        Path.Combine(folder, Path.GetFileName(finalPath) + ".partial");

    private static async Task WriteStreamAsync(Stream content, string path, CancellationToken ct)
    {
        await using var destination = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            useAsync: true);

        await content.CopyToAsync(destination, CopyBufferSize, ct);
        await destination.FlushAsync(ct);
    }

    /// <summary>
    /// Commits a fully staged book file under its final name, keeping the
    /// invariant of one <c>book.*</c> per folder. The staged file is renamed
    /// into place first and any other-format book file is removed only after
    /// that succeeds, so a failed commit leaves the previous book intact
    /// rather than deleting it with nothing to replace it.
    /// </summary>
    private void CommitStagedBookFile(string bookFolder, string tempPath, string finalPath)
    {
        File.Move(tempPath, finalPath, overwrite: true);
        DeleteExistingBookFiles(bookFolder, except: finalPath);
    }

    private void DeleteExistingBookFiles(string bookFolder, string except)
    {
        foreach (var existingFile in Directory.EnumerateFiles(bookFolder))
        {
            if (!AllowedBookExtensions.Contains(Path.GetExtension(existingFile))
                || BookTrackFormats.IsTrackFileName(Path.GetFileName(existingFile)))
                continue;

            if (string.Equals(existingFile, except, StringComparison.Ordinal))
                continue;

            TryDelete(existingFile);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete file during cleanup: {FileName}", path);
        }
    }
}
