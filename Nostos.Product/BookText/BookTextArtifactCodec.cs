using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Nostos.Product.BookText;

public static class BookTextArtifactCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(
        Stream destination,
        BookTextSourceRevision revision,
        IReadOnlyList<BookTextArtifactBlock> blocks,
        CancellationToken ct = default)
    {
        await using var gzip = new GZipStream(
            destination,
            CompressionLevel.Optimal,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            gzip,
            new UTF8Encoding(false),
            64 * 1024,
            leaveOpen: true);

        await writer.WriteLineAsync(
            JsonSerializer.Serialize<BookTextArtifactRecord>(
                new BookTextArtifactManifest(BookTextArtifactSchema.CurrentVersion, revision),
                Json).AsMemory(),
            ct);

        foreach (var block in blocks.OrderBy(block => block.Order))
        {
            await writer.WriteLineAsync(
                JsonSerializer.Serialize<BookTextArtifactRecord>(block, Json).AsMemory(),
                ct);
        }

        await writer.FlushAsync(ct);
    }
}
