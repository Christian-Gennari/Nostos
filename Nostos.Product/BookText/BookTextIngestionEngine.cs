using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Services;

namespace Nostos.Product.BookText;

public sealed class BookTextIngestionEngine(
    IBookAssetStorage sourceStorage,
    IBookDerivedArtifactStorage artifacts,
    IBookTextIndex index,
    IEnumerable<IBookTextExtractor> extractors,
    BookTextOptions options,
    ILogger<BookTextIngestionEngine> logger)
{
    private readonly IReadOnlyDictionary<BookTextSourceFormat, IBookTextExtractor> _extractors =
        extractors.ToDictionary(extractor => extractor.Format);

    public async Task ProcessAsync(BookTextIngestionWork work, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        string? tempPath = null;

        try
        {
            var info = await sourceStorage.GetBookFileInfoAsync(work.BookId, ct)
                ?? throw new BookTextUnsupportedException(
                    "book_text_source_missing",
                    "The source publication is no longer available.");

            if (info.Length <= 0)
                throw new BookTextUnsupportedException(
                    "book_text_source_empty",
                    "The source publication is empty.");

            if (options.MaxSourceBytes > 0 && info.Length > options.MaxSourceBytes)
                throw new BookTextUnsupportedException(
                    "book_text_source_too_large",
                    "The source publication is above the configured indexing size limit.");

            if (!_extractors.TryGetValue(work.Format, out var extractor))
                throw new BookTextUnsupportedException(
                    "book_text_format_unsupported",
                    "This publication format is not supported for text indexing.");

            await using var source = await sourceStorage.OpenBookFileAsync(work.BookId, range: null, ct)
                ?? throw new BookTextUnsupportedException(
                    "book_text_source_missing",
                    "The source publication is no longer available.");

            tempPath = Path.Combine(
                Path.GetTempPath(),
                $"nostos-book-text-{work.BookId:N}-{Guid.NewGuid():N}{Path.GetExtension(info.FileName)}");

            string sourceHash;
            await using (var staged = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await source.Content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await staged.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                await staged.FlushAsync(ct);
                sourceHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            await using var extractionSource = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var extracted = await extractor.ExtractAsync(extractionSource, ct);
            if (extracted.Blocks.Count == 0 || extracted.CharacterCount < 20)
                throw new BookTextUnsupportedException(
                    work.Format == BookTextSourceFormat.Pdf
                        ? "book_text_pdf_no_extractable_text"
                        : "book_text_no_extractable_text",
                    "The publication did not contain enough extractable text.");

            var revision = new BookTextSourceRevision(
                work.BookId,
                sourceHash,
                BookTextArtifactSchema.CurrentExtractorVersion,
                work.Format);

            var chunks = BookTextChunker.Chunk(
                revision,
                extracted.Blocks,
                options.TargetChunkChars,
                options.MaxChunkChars,
                options.OverlapChars);

            if (chunks.Count == 0)
                throw new BookTextUnsupportedException(
                    "book_text_no_chunks",
                    "The publication did not contain searchable text after normalization.");

            await artifacts.WriteAsync(
                revision,
                (destination, token) => BookTextArtifactCodec.WriteAsync(
                    destination,
                    revision,
                    extracted.Blocks,
                    token),
                ct);

            var committed = await index.ReplaceReadyAsync(
                revision,
                chunks,
                extracted.CharacterCount,
                work.Attempt,
                ct);

            if (!committed)
            {
                // A delete/replacement/retry superseded this worker after it had
                // already extracted the source. Never resurrect stale chunks.
                // If the state row itself is gone, the book was deleted and no
                // newer worker can own the artifact namespace, so clean it fully.
                if (await index.GetStateAsync(work.BookId, ct) is null)
                    await artifacts.DeleteBookArtifactsAsync(work.BookId, ct);

                logger.LogInformation(
                    "Book-text ingestion result for {BookId} was superseded by newer lifecycle state.",
                    work.BookId);
                return;
            }

            // The current claim won. Any older source/extractor artifacts are
            // now unreachable and can be removed without racing the active
            // revision.
            await artifacts.PruneBookArtifactsAsync(work.BookId, revision, ct);

            logger.LogInformation(
                "Book-text ingestion completed for {BookId}: format {Format}, source bytes {SourceBytes}, characters {Characters}, chunks {Chunks}, elapsed {ElapsedMs} ms.",
                work.BookId,
                work.Format,
                info.Length,
                extracted.CharacterCount,
                chunks.Count,
                (long)(DateTime.UtcNow - started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BookTextUnsupportedException exception)
        {
            await index.MarkFailedAsync(
                work.BookId,
                exception.Code,
                exception.Message,
                unsupported: true,
                work.Attempt,
                CancellationToken.None);

            logger.LogInformation(
                "Book-text ingestion is unsupported for {BookId}: {Code}.",
                work.BookId,
                exception.Code);
        }
        catch (Exception exception)
        {
            await index.MarkFailedAsync(
                work.BookId,
                "book_text_ingestion_failed",
                "Text indexing failed and can be retried.",
                unsupported: false,
                work.Attempt,
                CancellationToken.None);

            logger.LogWarning(
                "Book-text ingestion failed for {BookId}; exception type {ExceptionType}. Raw publication text is not logged.",
                work.BookId,
                exception.GetType().Name);
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); }
                catch { }
            }
        }
    }
}
