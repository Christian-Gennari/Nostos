namespace Nostos.Backend.Services;

/// <summary>
/// What a book file is: something to read, or something to listen to. This is
/// the digital half of the book's <c>BookType</c>; a physical book has no file
/// at all.
/// </summary>
public enum BookDigitalFormat
{
    Ebook,
    Audiobook,
}

/// <summary>
/// Shared format policy for every storage provider.
/// </summary>
public static class BookAssetFormats
{
    public static readonly IReadOnlySet<string> BookExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".epub",
            ".pdf",
            ".txt",
            ".mobi",
            ".azw3",
            ".m4b",
            ".m4a",
            ".mp3",
        };

    /// <summary>The book extensions that make an audiobook rather than an ebook.</summary>
    public static readonly IReadOnlySet<string> AudioExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".m4b",
            ".m4a",
            ".mp3",
        };

    public static readonly IReadOnlySet<string> CoverExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
        };

    private static readonly IReadOnlyDictionary<string, string> MimeToExtension =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/epub+zip"] = ".epub",
            ["application/pdf"] = ".pdf",
            ["text/plain"] = ".txt",
            ["audio/mpeg"] = ".mp3",
            ["audio/mp4"] = ".m4a",
            ["audio/x-m4a"] = ".m4a",
            ["audio/x-m4b"] = ".m4b",
            ["application/x-mobipocket-ebook"] = ".mobi",
            ["application/octet-stream"] = "",
        };

    public static bool IsAllowedUpload(string contentType, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!BookExtensions.Contains(extension))
            return false;

        var mediaType = NormalizeContentType(contentType);
        if (mediaType.Length == 0
            || string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!MimeToExtension.TryGetValue(mediaType, out var mapped) || mapped.Length == 0)
            return false;

        if (string.Equals(mediaType, "audio/mp4", StringComparison.OrdinalIgnoreCase))
            return extension is ".m4a" or ".m4b";

        if (string.Equals(mediaType, "application/x-mobipocket-ebook", StringComparison.OrdinalIgnoreCase))
            return extension is ".mobi" or ".azw3";

        return string.Equals(mapped, extension, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedCoverUpload(string contentType, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var mediaType = NormalizeContentType(contentType);

        return (extension == ".png"
                && string.Equals(mediaType, "image/png", StringComparison.OrdinalIgnoreCase))
            || (extension is ".jpg" or ".jpeg"
                && string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return string.Empty;

        var semicolon = contentType.IndexOf(';');
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
    }

    /// <summary>
    /// The digital book a file's extension makes it, or null when the name has
    /// no book extension this product accepts. Case-insensitive; the file's own
    /// content is not consulted.
    /// </summary>
    public static BookDigitalFormat? DigitalFormatForFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var extension = Path.GetExtension(fileName);
        if (!BookExtensions.Contains(extension))
            return null;

        return AudioExtensions.Contains(extension) ? BookDigitalFormat.Audiobook : BookDigitalFormat.Ebook;
    }

    public static string RequireBookExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (!BookExtensions.Contains(extension))
            throw new InvalidOperationException($"Unsupported file type: {extension}");

        return extension.ToLowerInvariant();
    }

    public static string RequireCoverExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!CoverExtensions.Contains(extension))
            throw new InvalidOperationException("Only PNG, JPG, or JPEG allowed.");

        return extension;
    }
}
