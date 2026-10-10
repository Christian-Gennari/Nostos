using Nostos.Backend.Services;
using Nostos.Product.BookText;

namespace Nostos.Product.Alignment;

/// <summary>
/// Supplies an extracted EPUB document for alignment.
///
/// Production uses the revision-aware cache. Tests can supply a deterministic
/// document without opening stored publications or running extraction.
/// </summary>
public interface IAlignmentDocumentProvider
{
    Task<BookTextExtractedDocument> GetAsync(
        Guid bookId,
        IBookAssetStorage storage,
        IBookTextExtractor extractor,
        CancellationToken ct = default);
}

/// <summary>
/// Small, process-local cache of extracted EPUB documents.
///
/// Keys include the book id, storage identity and extractor version.
/// A replaced publication therefore cannot reuse the prior extraction.
/// </summary>
public sealed class AlignmentDocumentCache : IAlignmentDocumentProvider
{
    private const int Capacity = 4;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> entries = [];
    private readonly LinkedList<string> order = [];

    private sealed record CacheEntry(
        BookTextExtractedDocument Document,
        LinkedListNode<string> Node);

    public async Task<BookTextExtractedDocument> GetAsync(
        Guid bookId,
        IBookAssetStorage storage,
        IBookTextExtractor extractor,
        CancellationToken ct = default)
    {
        var info = await storage.GetBookFileInfoAsync(bookId, ct);

        if (info is null)
        {
            throw new AlignmentException(
                "alignment_target_media_missing",
                422,
                "The target EPUB is not available in storage.");
        }

        var key = string.Join(":",
            bookId.ToString("N"),
            info.EntityTag,
            info.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            info.LastModified.UtcTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            BookTextArtifactSchema.CurrentExtractorVersion);

        await gate.WaitAsync(ct);

        try
        {
            if (entries.TryGetValue(key, out var cached))
            {
                order.Remove(cached.Node);
                order.AddFirst(cached.Node);
                return cached.Document;
            }

            await using var asset = await storage.OpenBookFileAsync(
                bookId, ct: ct);

            if (asset is null)
            {
                throw new AlignmentException(
                    "alignment_target_media_missing",
                    422,
                    "The target EPUB is not available in storage.");
            }

            // The EPUB extractor requires seekable ZIP input.
            // Temporary files are never placed in library storage.
            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"nostos-alignment-{Guid.NewGuid():N}.epub");

            BookTextExtractedDocument document;

            try
            {
                await using (var temp = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await asset.Content.CopyToAsync(temp, ct);
                    temp.Position = 0;

                    try
                    {
                        document = await extractor.ExtractAsync(temp, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (
                        ex is InvalidDataException or
                        IOException or
                        ArgumentException)
                    {
                        throw new AlignmentException(
                            "alignment_target_text_unavailable",
                            422,
                            "The stored EPUB could not be extracted.");
                    }
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }

            if (document.Format != BookTextSourceFormat.Epub ||
                document.Blocks.Count == 0)
            {
                throw new AlignmentException(
                    "alignment_target_text_unavailable",
                    422,
                    "The EPUB has no extractable text.");
            }

            var node = order.AddFirst(key);
            entries[key] = new CacheEntry(document, node);

            while (entries.Count > Capacity)
            {
                var oldest = order.Last!;
                entries.Remove(oldest.Value);
                order.RemoveLast();
            }

            return document;
        }
        finally
        {
            gate.Release();
        }
    }
}
