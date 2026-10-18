using NetVips;

namespace Nostos.Backend.Tests.Support;

/// <summary>Small solid-colour images in any format libvips can write.</summary>
public static class TestImages
{
    /// <param name="format">A libvips saver suffix: <c>.png</c>, <c>.jpg</c>, <c>.webp</c>, <c>.gif</c>, <c>.tif</c>…</param>
    public static byte[] Create(
        string format,
        int width = 10,
        int height = 10,
        byte red = 0,
        byte green = 0,
        byte blue = 0)
    {
        using var black = Image.Black(width, height, bands: 3);
        using var coloured = black.Linear(new double[] { 1, 1, 1 }, new double[] { red, green, blue });
        using var image = coloured.Cast(Enums.BandFormat.Uchar);
        if (format != ".v")
            return image.WriteToBuffer(format);

        // libvips' own format can only be written to a file.
        var path = Path.Combine(Path.GetTempPath(), $"nostos-test-{Guid.NewGuid():N}.v");
        try
        {
            image.WriteToFile(path);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static (int Width, int Height, string Loader) Describe(byte[] bytes)
    {
        using var image = Image.NewFromBuffer(bytes);
        return (image.Width, image.Height, (string)image.Get("vips-loader"));
    }
}
