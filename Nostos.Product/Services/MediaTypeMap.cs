namespace Nostos.Backend.Services;

/// <summary>
/// The single source of truth for the media types Nostos advertises for book
/// files and cover images.
///
/// There used to be three independent maps — the download endpoints, the cover
/// endpoint and the OPDS feed each carried their own — and they had drifted
/// apart: the OPDS feed advertised every audiobook as
/// <c>application/octet-stream</c> and every cover as <c>image/png</c>, while
/// the download endpoints got both right. A media type is a fact about the
/// stored file, not about the surface that reports it, so it lives here once.
///
/// The book-file set mirrors the extensions Nostos actually stores as book
/// files (epub, pdf, txt, mobi, azw3, mp3, m4a, m4b); a format Nostos cannot
/// hold is not advertised. Extensions the OPDS feed used to advertise without
/// ever supporting them (cbz, cbr) are gone.
/// </summary>
public static class MediaTypeMap
{
    private const string UnknownBookFileType = "application/octet-stream";

    // Historical fallback for a cover whose extension is not recognized. Kept
    // so a legacy file keeps the type it has always been served with.
    private const string UnknownCoverType = "image/png";

    private static readonly Dictionary<string, string> BookFileTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [".epub"] = "application/epub+zip",
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain",
        [".mobi"] = "application/x-mobipocket-ebook",
        [".azw3"] = "application/x-mobipocket-ebook",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".m4b"] = "audio/mp4",
    };

    private static readonly Dictionary<string, string> CoverTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
    };

    /// <summary>
    /// Content type for a stored book file (a path or a bare file name).
    /// </summary>
    public static string ForBookFile(string? fileNameOrPath) =>
        Lookup(BookFileTypes, fileNameOrPath) ?? UnknownBookFileType;

    /// <summary>
    /// Content type for a stored cover image (a path or a bare file name).
    /// </summary>
    public static string ForCover(string? fileNameOrPath) =>
        Lookup(CoverTypes, fileNameOrPath) ?? UnknownCoverType;

    private static string? Lookup(IReadOnlyDictionary<string, string> map, string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
            return null;

        var extension = Path.GetExtension(fileNameOrPath);
        return extension.Length > 0 && map.TryGetValue(extension, out var mediaType)
            ? mediaType
            : null;
    }
}
