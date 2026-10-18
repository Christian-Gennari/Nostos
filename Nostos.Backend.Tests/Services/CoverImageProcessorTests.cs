using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services;

/// <summary>
/// Cover bytes are untrusted: they come from uploads and content providers.
/// These pin which of them the processor agrees to decode and what it makes.
/// </summary>
public sealed class CoverImageProcessorTests
{
    [Theory]
    [InlineData(".jpg", "jpeg")]
    [InlineData(".png", "png")]
    [InlineData(".webp", "webp")]
    [InlineData(".gif", "gif")]
    public async Task InspectAsync_ReadsEveryCoverFormat(string suffix, string format)
    {
        var info = await CoverImageProcessor.InspectAsync(new MemoryStream(TestImages.Create(suffix, 30, 40)));

        info.Should().Be(new CoverImageInfo(30, 40, format));
    }

    public static TheoryData<string, byte[]> RefusedFormats() => new()
    {
        { "TIFF", TestImages.Create(".tif") },
        { "libvips native", TestImages.Create(".v") },
        { "SVG", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>") },
        { "BMP", Bmp24(2, 2) },
        { "HTML error page", Encoding.UTF8.GetBytes("<!doctype html><title>404</title>") },
        { "empty", [] },
    };

    [Theory]
    [MemberData(nameof(RefusedFormats))]
    public async Task InspectAsync_RefusesFormatsACoverIsNot(string name, byte[] bytes)
    {
        var info = await CoverImageProcessor.InspectAsync(new MemoryStream(bytes));

        info.Should().BeNull(because: $"{name} is not a cover format");
    }

    [Theory]
    [MemberData(nameof(RefusedFormats))]
    public async Task WriteWebpThumbnailAsync_RefusesFormatsACoverIsNot(string name, byte[] bytes)
    {
        using var output = new MemoryStream();

        var act = () => CoverImageProcessor.WriteWebpThumbnailAsync(new MemoryStream(bytes), 200, output);

        await act.Should().ThrowAsync<UnsupportedCoverImageException>(because: $"{name} is not a cover format");
        output.Length.Should().Be(0);
    }

    [Fact]
    public async Task A_truncated_cover_is_refused()
    {
        var jpeg = TestImages.Create(".jpg", 400, 600, red: 200, green: 100, blue: 50);
        var truncated = jpeg[..(jpeg.Length / 2)];

        (await CoverImageProcessor.InspectAsync(new MemoryStream(truncated))).Should().BeNull();
        var act = () => CoverImageProcessor.WriteWebpThumbnailAsync(new MemoryStream(truncated), 200, new MemoryStream());
        await act.Should().ThrowAsync<UnsupportedCoverImageException>();
    }

    [Fact]
    public async Task A_cover_claiming_enormous_dimensions_is_refused_before_it_is_decoded()
    {
        // A tiny file whose header claims 20000 x 20000 pixels.
        var bomb = WithPngDimensions(TestImages.Create(".png", 1, 1), 20_000, 20_000);

        (await CoverImageProcessor.InspectAsync(new MemoryStream(bomb))).Should().BeNull();
        var act = () => CoverImageProcessor.WriteWebpThumbnailAsync(new MemoryStream(bomb), 200, new MemoryStream());
        (await act.Should().ThrowAsync<UnsupportedCoverImageException>())
            .WithMessage("*too large*");
    }

    [Theory]
    [InlineData(".jpg", 600, 900, 200, 300)]
    [InlineData(".png", 640, 480, 320, 240)]
    [InlineData(".gif", 120, 200, 240, 400)]
    public async Task WriteWebpThumbnailAsync_WritesAWebpAtTheRequestedWidthKeepingProportions(
        string suffix, int width, int height, int targetWidth, int expectedHeight)
    {
        using var output = new MemoryStream();

        await CoverImageProcessor.WriteWebpThumbnailAsync(
            new MemoryStream(TestImages.Create(suffix, width, height)), targetWidth, output);

        TestImages.Describe(output.ToArray()).Should().Be((targetWidth, expectedHeight, "webpload_buffer"));
    }

    private static byte[] Bmp24(int width, int height)
    {
        var rowBytes = (width * 3 + 3) & ~3;
        var bmp = new byte[54 + rowBytes * height];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        return bmp;
    }

    /// <summary>Rewrites a PNG's IHDR dimensions and fixes up its checksum.</summary>
    private static byte[] WithPngDimensions(byte[] png, int width, int height)
    {
        // Signature (8) + IHDR length (4) + "IHDR" (4), then width and height.
        var patched = (byte[])png.Clone();
        BinaryPrimitives.WriteInt32BigEndian(patched.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(patched.AsSpan(20), height);
        var crc = Crc32(patched.AsSpan(12, 4 + 13));
        BinaryPrimitives.WriteUInt32BigEndian(patched.AsSpan(12 + 4 + 13), crc);
        return patched;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }
}
