namespace Nostos.Product.Http;

/// <summary>
/// A multipart import refused before anything was spooled. The message is the
/// owner-facing reason, and each route answers it with the 400 body it already
/// used, so sharing this check never changes a response shape.
/// </summary>
public sealed class MultipartUploadRejectedException(string message) : Exception(message);

/// <summary>The limits and words one multipart import route uses.</summary>
public sealed record MultipartUploadOptions(
    long MaxBytes,
    string TempPrefix,
    string ExpectedFormMessage,
    string MissingFileMessage,
    string TooLargeMessage,
    string FileNameFallback = "upload");

/// <summary>
/// One multipart import spooled into a private temp directory that is deleted
/// when the request ends (#1629). An import reads a real file — SQLite opens
/// the upload and may create <c>-wal</c>/<c>-shm</c> beside it — so the body
/// cannot be handed to the reader as a stream. Every name written here is one
/// the server chose from <see cref="SafeUploadName"/>, never a client path.
/// </summary>
public sealed class MultipartTempUpload : IDisposable
{
    private readonly DirectoryInfo _directory;
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    private MultipartTempUpload(DirectoryInfo directory, IFormFile file, IFormCollection form)
    {
        _directory = directory;
        File = file;
        Form = form;
        OriginalName = System.IO.Path.GetFileName(file.FileName);
    }

    /// <summary>The form field this route treats as the import itself.</summary>
    public IFormFile File { get; }

    /// <summary>Every field of the request, for the parts an import also sends.</summary>
    public IFormCollection Form { get; }

    /// <summary>
    /// The client's own name for the import, reduced to a plain file name. Some
    /// importers record it as a label; nothing is ever written under it.
    /// </summary>
    public string OriginalName { get; }

    /// <summary>Where the import itself was spooled.</summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>The safe name the import was spooled under.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Checks the request is a multipart form carrying an import of an
    /// acceptable size, then spools it. Throws
    /// <see cref="MultipartUploadRejectedException"/> with the owner-facing
    /// reason, having written nothing.
    /// </summary>
    public static async Task<MultipartTempUpload> ReceiveAsync(
        HttpRequest request,
        MultipartUploadOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        if (!request.HasFormContentType)
            throw new MultipartUploadRejectedException(options.ExpectedFormMessage);

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault()
            ?? throw new MultipartUploadRejectedException(options.MissingFileMessage);
        if (file.Length <= 0 || file.Length > options.MaxBytes)
            throw new MultipartUploadRejectedException(options.TooLargeMessage);

        var upload = new MultipartTempUpload(
            Directory.CreateTempSubdirectory(options.TempPrefix),
            file,
            form);
        try
        {
            upload.Path = await upload.SpoolAsync(file, options.FileNameFallback, ct);
            upload.Name = System.IO.Path.GetFileName(upload.Path);
            return upload;
        }
        catch
        {
            upload.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Spools one more form file into the same directory, under a safe name
    /// that cannot collide with a name already used here. Returns its path.
    /// </summary>
    public async Task<string> SpoolAsync(IFormFile upload, string fallbackName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackName);

        var name = UniqueName(SafeUploadName(upload.FileName) ?? fallbackName, fallbackName);
        var target = System.IO.Path.Combine(_directory.FullName, name);
        await using var stream = new FileStream(
            target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var source = upload.OpenReadStream();
        await source.CopyToAsync(stream, ct);
        return target;
    }

    /// <summary>
    /// A name this server is willing to create a file under, or null when the
    /// client sent nothing worth keeping: a path, a dot-prefixed name, a name
    /// with a character the filesystem refuses, or one too long to be a file.
    /// </summary>
    public static string? SafeUploadName(string? fileName)
    {
        var name = System.IO.Path.GetFileName(fileName ?? string.Empty).Trim();
        if (name.Length is 0 or > 200 || name.StartsWith('.'))
            return null;
        return name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ? null : name;
    }

    public void Dispose()
    {
        try
        {
            _directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A request never fails because its scratch could not be deleted.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort; the OS reclaims the temp directory eventually.
        }
    }

    private string UniqueName(string name, string fallbackName)
    {
        if (_names.Add(name)) return name;

        var candidate = $"{fallbackName}-{_names.Count}";
        _names.Add(candidate);
        return candidate;
    }
}
