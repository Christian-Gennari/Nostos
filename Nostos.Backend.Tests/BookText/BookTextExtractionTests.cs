using System.Text;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.BookText;

public sealed class BookTextExtractionTests
{
    [Fact]
    public async Task Pdf_extractor_preserves_physical_page_order_and_skips_blank_page()
    {
        var fixture = BookTextFixtureFactory.CreatePdf();
        var extractor = new PdfBookTextExtractor();

        await using var source = new MemoryStream(fixture.Bytes);
        var document = await extractor.ExtractAsync(source);

        Assert.Equal(BookTextSourceFormat.Pdf, document.Format);
        Assert.Equal(3, document.Blocks.Count);

        var pages = document.Blocks
            .Select(block => Assert.IsType<PdfBookTextSourceLocator>(block.SourceSegments.Single().Locator))
            .Select(locator => locator.PageIndex)
            .ToArray();

        Assert.Equal([0, 1, 3], pages);

        var labels = document.Blocks
            .Select(block => Assert.IsType<PdfBookTextSourceLocator>(
                block.SourceSegments.Single().Locator))
            .Select(locator => locator.PageLabel)
            .ToArray();
        Assert.Equal(["i", "ii", "2"], labels);

        Assert.Contains("CHAPTER ONE", document.Blocks[0].Text);
        Assert.Contains("chapter continues across a page boundary", document.Blocks[1].Text);
        Assert.Contains("CHAPTER TWO", document.Blocks[2].Text);
    }

    [Fact]
    public async Task Epub_extractor_follows_spine_order_and_preserves_structural_fallback()
    {
        var fixture = BookTextFixtureFactory.CreateEpub();
        var extractor = new EpubBookTextExtractor();

        await using var source = new MemoryStream(fixture.Bytes);
        var document = await extractor.ExtractAsync(source);

        Assert.Equal(BookTextSourceFormat.Epub, document.Format);
        Assert.NotEmpty(document.Blocks);

        var firstChapter = document.Blocks.First(block => block.Text.Contains("Chapter One", StringComparison.Ordinal));
        var continuation = document.Blocks.First(block => block.Text.Contains("intended continuation", StringComparison.Ordinal));

        var firstLocator = Assert.IsType<EpubBookTextSourceLocator>(firstChapter.SourceSegments.Single().Locator);
        var continuationLocator = Assert.IsType<EpubBookTextSourceLocator>(continuation.SourceSegments.Single().Locator);

        Assert.Equal(0, firstLocator.SpineIndex);
        Assert.Equal("chapter-1.xhtml", firstLocator.ResourceHref);
        Assert.Equal(2, continuationLocator.SpineIndex);
        Assert.Equal("chapter-2b.xhtml", continuationLocator.ResourceHref);
        Assert.Null(continuationLocator.Cfi);
        Assert.NotNull(continuationLocator.StartTextOffset);
    }

    [Fact]
    public async Task Epub_extractor_keeps_root_opf_manifest_href_canonical()
    {
        var fixture = BookTextFixtureFactory.CreateEpub(rootPackage: true);
        var extractor = new EpubBookTextExtractor();

        await using var source = new MemoryStream(fixture.Bytes);
        var document = await extractor.ExtractAsync(source);

        var chapter = document.Blocks.First(block =>
            block.Text.Contains("Chapter One", StringComparison.Ordinal));
        var locator = Assert.IsType<EpubBookTextSourceLocator>(
            chapter.SourceSegments.Single().Locator);

        Assert.Equal(0, locator.SpineIndex);
        Assert.Equal("chapter-1.xhtml", locator.ResourceHref);
    }

    [Fact]
    public void Chunker_is_deterministic_and_preserves_multi_segment_provenance()
    {
        var revision = new BookTextSourceRevision(
            Guid.Parse("00000000-0000-0000-0000-000000000468"),
            new string('a', 64),
            BookTextArtifactSchema.CurrentExtractorVersion,
            BookTextSourceFormat.Pdf);

        var text = string.Join(
            ' ',
            Enumerable.Repeat("grounded retrieval keeps source provenance", 120));
        var pageBoundary = text.Length / 2;
        var block = new BookTextArtifactBlock(
            0,
            text,
            ["Chapter One"],
            [
                new BookTextSourceSegment(
                    0,
                    pageBoundary,
                    new PdfBookTextSourceLocator(0, "xii", 0, pageBoundary)),
                new BookTextSourceSegment(
                    pageBoundary,
                    text.Length - pageBoundary,
                    new PdfBookTextSourceLocator(
                        1,
                        "1",
                        0,
                        text.Length - pageBoundary)),
            ]);

        var first = BookTextChunker.Chunk(revision, [block], 900, 1100, 100);
        var second = BookTextChunker.Chunk(revision, [block], 900, 1100, 100);

        Assert.True(first.Count > 1);
        Assert.Equal(first.Select(chunk => chunk.Id), second.Select(chunk => chunk.Id));
        Assert.Equal(first.Select(chunk => chunk.Text), second.Select(chunk => chunk.Text));
        Assert.All(first, chunk => Assert.NotEmpty(chunk.SourceSegments));
    }

    [Fact]
    public async Task Artifact_codec_writes_gzip_jsonl_with_manifest_first()
    {
        var revision = new BookTextSourceRevision(
            Guid.Parse("00000000-0000-0000-0000-000000000468"),
            new string('b', 64),
            BookTextArtifactSchema.CurrentExtractorVersion,
            BookTextSourceFormat.Epub);

        var block = new BookTextArtifactBlock(
            0,
            "A generated passage.",
            ["Chapter"],
            [
                new BookTextSourceSegment(
                    0,
                    20,
                    new EpubBookTextSourceLocator(0, "chapter.xhtml", null, 0, 20)),
            ]);

        await using var encoded = new MemoryStream();
        await BookTextArtifactCodec.WriteAsync(encoded, revision, [block]);
        encoded.Position = 0;

        await using var gzip = new System.IO.Compression.GZipStream(
            encoded,
            System.IO.Compression.CompressionMode.Decompress,
            leaveOpen: true);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        var manifest = await reader.ReadLineAsync();
        var data = await reader.ReadLineAsync();

        Assert.NotNull(manifest);
        Assert.Contains("\"recordType\":\"manifest\"", manifest);
        Assert.NotNull(data);
        Assert.Contains("\"recordType\":\"block\"", data);
    }
}
