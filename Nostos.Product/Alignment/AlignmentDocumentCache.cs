using Nostos.Backend.Services;
using Nostos.Product.BookText;

namespace Nostos.Product.Alignment;

/// <param name="ReadingAids">
/// Per block order, the parts of the block's text nobody reads aloud (ruby
/// glosses). Null when the format has none or the extractor does not report them.
/// </param>
public sealed record AlignmentDocumentSnapshot(
    BookTextExtractedDocument Document,
    string? Revision,
    IReadOnlyDictionary<int, IReadOnlyList<BookTextRange>>? ReadingAids = null);

/// <summary>
/// Supplies an extracted EPUB or PDF document and the storage revision
/// from the same read.
/// </summary>
public interface IAlignmentDocumentProvider
{
    Task<AlignmentDocumentSnapshot> GetAsync(
        Guid bookId,
        IBookAssetStorage storage,
        IBookTextExtractor extractor,
        CancellationToken ct = default);
}

/// <summary>
/// Bounded, revision-aware, process-local text extraction cache.
/// The storage read itself supplies the revision used for the cache key.
/// </summary>
public sealed class AlignmentDocumentCache(
    AlignmentOptions options) : IAlignmentDocumentProvider
{
    private const int Capacity = 4;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> entries = [];
    private readonly LinkedList<string> order = [];

    private long totalCharacters;

    private sealed record CacheEntry(
        BookTextExtractedDocument Document,
        IReadOnlyDictionary<int, IReadOnlyList<BookTextRange>>? ReadingAids,
        long Characters,
        LinkedListNode<string> Node);

    public static void ValidateEpubLength(
        long length,
        long maximum)
    {
        if (maximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximum));

        if (length < 0 || length > maximum)
        {
            throw new AlignmentException(
                "alignment_target_too_large",
                413,
                "The text edition exceeds the alignment extraction size limit.");
        }
    }

    public static long CountCharacters(
        BookTextExtractedDocument document)
    {
        long count = 0;

        foreach (var block in document.Blocks)
            count = checked(count + block.Text.Length);

        return count;
    }

    public async Task<AlignmentDocumentSnapshot> GetAsync(
        Guid bookId,
        IBookAssetStorage storage,
        IBookTextExtractor extractor,
        CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);

        try
        {
            // Open first. A separate metadata lookup could race with
            // replacement and create a cache entry for the wrong revision.
            await using var asset = await storage.OpenBookFileAsync(
                bookId, ct: ct);

            if (asset is null)
            {
                throw new AlignmentException(
                    "alignment_target_media_missing",
                    422,
                    "The target text edition is not available in storage.");
            }

            ValidateEpubLength(
                asset.Info.Length,
                options.MaxEpubBytes);

            var revision = AlignmentRevisions.Text(
                bookId, asset.Info);

            // A missing stable identity must not create a cache hit.
            // Extraction still works; only caching and anchor persistence
            // are disabled.
            if (revision is not null &&
                entries.TryGetValue(revision, out var cached))
            {
                order.Remove(cached.Node);
                order.AddFirst(cached.Node);

                return new AlignmentDocumentSnapshot(
                    cached.Document, revision, cached.ReadingAids);
            }

            var extension = extractor.Format switch
            {
                BookTextSourceFormat.Epub => ".epub",
                BookTextSourceFormat.Pdf => ".pdf",
                _ => throw new AlignmentException(
                    "alignment_target_text_unavailable",
                    422,
                    "The text extractor format is unsupported."),
            };

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"nostos-alignment-{Guid.NewGuid():N}{extension}");

            BookTextExtractedDocument document;
            IReadOnlyDictionary<int, IReadOnlyList<BookTextRange>>? readingAids = null;

            try
            {
                await using var temp = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan);

                // Do not trust a provider's declared Content-Length.
                // Enforce the limit while bytes are actually staged.
                var copied = 0L;
                var buffer = new byte[81920];

                while (true)
                {
                    var read = await asset.Content.ReadAsync(
                        buffer, ct);

                    if (read == 0)
                        break;

                    copied = checked(copied + read);

                    ValidateEpubLength(
                        copied,
                        options.MaxEpubBytes);

                    await temp.WriteAsync(
                        buffer.AsMemory(0, read), ct);
                }

                if (copied != asset.Info.Length)
                {
                    throw new AlignmentException(
                        "alignment_target_text_unavailable",
                        422,
                        "The stored text file length changed during extraction.");
                }

                temp.Position = 0;

                try
                {
                    if (extractor is IBookTextReadingAidSource annotated)
                    {
                        var extracted = await annotated.ExtractAnnotatedAsync(temp, ct);
                        document = extracted.Document;
                        readingAids = extracted.ReadingAids.Count > 0
                            ? extracted.ReadingAids
                            : null;
                    }
                    else
                    {
                        document = await extractor.ExtractAsync(temp, ct);
                    }
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
                        "The stored text file could not be extracted.");
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }

            if (document.Format != extractor.Format ||
                document.Blocks.Count == 0)
            {
                throw new AlignmentException(
                    "alignment_target_text_unavailable",
                    422,
                    "The text edition has no extractable text.");
            }

            var characters = CountCharacters(document);

            if (revision is null ||
                characters > options.MaxCachedCharacters)
            {
                return new AlignmentDocumentSnapshot(
                    document, revision, readingAids);
            }

            var node = order.AddFirst(revision);

            entries.Add(
                revision,
                new CacheEntry(document, readingAids, characters, node));

            totalCharacters = checked(totalCharacters + characters);

            while (entries.Count > Capacity ||
                   totalCharacters > options.MaxCachedCharacters)
            {
                var oldest = order.Last!;
                var removed = entries[oldest.Value];

                totalCharacters -= removed.Characters;
                entries.Remove(oldest.Value);
                order.RemoveLast();
            }

            return new AlignmentDocumentSnapshot(
                document, revision, readingAids);
        }
        finally
        {
            gate.Release();
        }
    }
}
