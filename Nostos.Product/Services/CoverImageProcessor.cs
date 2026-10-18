using NetVips;
using VipsImage = NetVips.Image;

namespace Nostos.Backend.Services;

/// <summary>What <see cref="CoverImageProcessor.InspectAsync"/> learned about a cover.</summary>
/// <param name="Format">The decoder that read it: <c>jpeg</c>, <c>png</c>, <c>webp</c> or <c>gif</c>.</param>
public sealed record CoverImageInfo(int Width, int Height, string Format);

/// <summary>
/// Thrown when cover bytes are not an image a cover can be: a format outside
/// the allow-list, a file that does not decode, or one with absurd dimensions.
/// </summary>
public sealed class UnsupportedCoverImageException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Every decode of cover art goes through here, for local and remote storage
/// alike. A cover's bytes come from an upload or from a content provider, so
/// they are untrusted.
///
/// The decoder is picked from the bytes, not the file name, and only the
/// formats a cover can reasonably be are offered (JPEG, PNG, WebP, GIF). A
/// file in some other format is refused instead of being handed to a decoder
/// nothing here needs. libvips ships loaders for SVG, TIFF, HEIF and more;
/// they are all blocked process-wide, so nothing else in the process may rely
/// on them.
/// </summary>
public static class CoverImageProcessor
{
    /// <summary>The WebP quality list views have always been served at.</summary>
    public const int ThumbnailQuality = 82;

    /// <summary>
    /// The largest cover decoded, in pixels. A real cover scan is a few
    /// megapixels; a hundred is room to spare, and refusing above it keeps a
    /// small file that claims enormous dimensions (a decompression bomb) from
    /// tying up the CPU.
    /// </summary>
    public const long MaxPixels = 100_000_000;

    /// <summary>The largest cover file read into memory.</summary>
    public const long MaxSourceBytes = 64L * 1024 * 1024;

    // libvips' largest coordinate.
    private const int UnboundedHeight = 10_000_000;

    // libvips class names. Unblocking a class unblocks its file, buffer and
    // source variants with it.
    private static readonly string[] AllowedLoaders =
    [
        "VipsForeignLoadJpeg",
        "VipsForeignLoadPng",
        "VipsForeignLoadWebp",
        "VipsForeignLoadNsgif",
    ];

    static CoverImageProcessor()
    {
        NetVips.NetVips.BlockUntrusted = true;
        Operation.Block("VipsForeignLoad", true);
        foreach (var loader in AllowedLoaders)
            Operation.Block(loader, false);

        // Every cover is decoded once and its thumbnail cached on disk or in
        // object storage, so the operation cache would only hold on to the
        // memory of images that are never asked for again.
        Cache.Max = 0;
    }

    /// <summary>
    /// Reads the cover completely, so a truncated or corrupt file is caught
    /// here rather than when a list view first asks for its thumbnail.
    /// </summary>
    /// <returns>Its size and format, or <c>null</c> when it is not a usable cover.</returns>
    public static async Task<CoverImageInfo?> InspectAsync(Stream source, CancellationToken ct = default)
    {
        try
        {
            var bytes = await ReadAllAsync(source, ct);
            using var image = Open(bytes);
            var info = new CoverImageInfo(image.Width, image.Height, FormatOf(image));

            // Header checks pass on a file cut off part-way; a decode does not.
            _ = image.Avg();
            return info;
        }
        catch (Exception ex) when (ex is VipsException or UnsupportedCoverImageException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a WebP copy of the cover, <paramref name="width"/> pixels wide
    /// with the height that keeps its proportions.
    /// </summary>
    /// <exception cref="UnsupportedCoverImageException">The cover cannot be decoded.</exception>
    public static async Task WriteWebpThumbnailAsync(
        Stream source,
        int width,
        Stream destination,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);

        var bytes = await ReadAllAsync(source, ct);
        byte[] encoded;
        try
        {
            // The header is read first so the pixel limit applies before any
            // pixel is decoded.
            using (Open(bytes)) { }

            ct.ThrowIfCancellationRequested();
            // Only the width constrains: libvips bounds the height to the
            // width unless told otherwise. The loader options are where
            // fail_on reaches the decoder, so a truncated file is refused
            // rather than finished off in grey.
            using var thumbnail = VipsImage.ThumbnailBuffer(
                bytes,
                width,
                optionString: "fail_on=error",
                height: UnboundedHeight);
            encoded = thumbnail.WebpsaveBuffer(q: ThumbnailQuality, keep: Enums.ForeignKeep.None);
        }
        catch (VipsException ex)
        {
            throw new UnsupportedCoverImageException("The cover could not be decoded.", ex);
        }

        await destination.WriteAsync(encoded, ct);
    }

    private static VipsImage Open(byte[] bytes)
    {
        VipsImage image;
        try
        {
            image = VipsImage.NewFromBuffer(bytes, access: Enums.Access.Sequential, failOn: Enums.FailOn.Error);
        }
        catch (VipsException ex)
        {
            throw new UnsupportedCoverImageException("The cover is not a JPEG, PNG, WebP or GIF image.", ex);
        }

        var (width, height) = (image.Width, image.Height);
        if ((long)width * height > MaxPixels)
        {
            image.Dispose();
            throw new UnsupportedCoverImageException($"The cover is too large to decode ({width}x{height}).");
        }

        return image;
    }

    private static string FormatOf(VipsImage image)
    {
        // "jpegload_buffer" -> "jpeg"; libvips names its GIF loader "gifload".
        var loader = image.Contains("vips-loader") ? (string)image.Get("vips-loader") : "";
        var end = loader.IndexOf("load", StringComparison.Ordinal);
        return end > 0 ? loader[..end] : loader;
    }

    private static async Task<byte[]> ReadAllAsync(Stream source, CancellationToken ct)
    {
        if (source.CanSeek && source.Length - source.Position > MaxSourceBytes)
            throw new UnsupportedCoverImageException("The cover file is too large.");

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxSourceBytes)
                throw new UnsupportedCoverImageException("The cover file is too large.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
