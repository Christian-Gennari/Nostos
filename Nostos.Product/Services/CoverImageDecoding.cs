using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace Nostos.Backend.Services;

/// <summary>
/// How cover art is decoded. A cover's bytes come from an upload or from a
/// content provider, and the image library picks a decoder from those bytes,
/// not from the file name. Only the formats a cover can reasonably be are
/// offered, so a file in some other format is refused instead of being handed
/// to a decoder nothing here needs.
/// </summary>
public static class CoverImageDecoding
{
    public static DecoderOptions Options { get; } = new()
    {
        Configuration = new SixLabors.ImageSharp.Configuration(
            new JpegConfigurationModule(),
            new PngConfigurationModule(),
            new WebpConfigurationModule(),
            new GifConfigurationModule(),
            new BmpConfigurationModule()),
    };
}
