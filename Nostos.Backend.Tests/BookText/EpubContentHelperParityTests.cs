using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Portability;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.BookText;

/// <summary>
/// Verifies that <see cref="EpubBookTextExtractor"/> and <see cref="EpubCfiWriter"/>
/// share canonical EPUB spine/package reading and block-filtering rules so
/// block ordering and resource offsets stay byte-identical.
/// </summary>
public sealed class EpubContentHelperParityTests
{
    private static readonly Guid BookId = Guid.Parse("00000000-0000-0000-0000-00000000e90b");

    private const string ComplexChapter = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>Parity Chapter</title></head>
        <body>
          <script><p>Script text must be ignored.</p></script>
          <style><p>Style text must be ignored.</p></style>
          <nav><p>Navigation block must be ignored.</p></nav>
          <svg><p>SVG text must be ignored.</p></svg>
          <math><p>Math text must be ignored.</p></math>
          <h1>Heading One</h1>
          <p>   </p>
          <blockquote>
            <p>Quoted outer block with <em>inline</em> markup &amp; entity.</p>
            <aside><p>Nested aside inside blockquote is not emitted separately.</p></aside>
          </blockquote>
          <ul>
            <li><p>First list item with nested paragraph.</p></li>
            <li>Second list item directly inside li.</li>
          </ul>
          <figure>
            <figcaption>Figure caption text.</figcaption>
          </figure>
          <dl>
            <dt>Defined term</dt>
            <dd>Term definition.</dd>
          </dl>
          <aside>Top-level aside note.</aside>
          <pre>Preformatted   line
            kept normalized.</pre>
          <p id="target">Final target paragraph with enough words for CFI matching.</p>
        </body>
        </html>
        """;

    private const string MalformedHtmlChapter = """
        <html>
        <head><title>Legacy HTML</title></head>
        <body>
          <h2>Legacy &amp; HTML</h2>
          <p>First legacy <b><i>bold</b></i> paragraph<br>with break.</p>
          <nav><p>Ignored legacy nav.</p></nav>
          <p>Second legacy paragraph after ignored block.</p>
        </body>
        </html>
        """;

    [Fact]
    public async Task Extractor_and_cfi_enumerator_produce_identical_blocks_and_offsets()
    {
        var epub = BuildEpub(
            [("front", null, null), ("ch1", "text/ch1.xhtml", ComplexChapter)],
            includeBlankSpineItemRefFirst: true);

        using var extractStream = new MemoryStream(epub);
        var extracted = await new EpubBookTextExtractor().ExtractAsync(extractStream);

        using var archiveStream = new MemoryStream(epub);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var package = EpubPackage.Read(archive);

        package.IndexedSpineItems.Should().ContainSingle();
        package.IndexedSpineItems[0].IndexedIndex.Should().Be(0);
        package.IndexedSpineItems[0].RawIndex.Should().Be(1);

        var entry = package.FindResourceEntry(archive, "text/ch1.xhtml");
        entry.Should().NotBeNull();
        string rawXhtml;
        using (var reader = new StreamReader(entry!.Open(), detectEncodingFromByteOrderMarks: true))
            rawXhtml = await reader.ReadToEndAsync();

        var xdoc = EpubContentHelper.ParseXhtml(rawXhtml);
        var body = EpubContentHelper.FindBody(xdoc);
        body.Should().NotBeNull();

        var xhtmlBlocks = EpubContentHelper.EnumerateBlocks(body!).ToList();

        extracted.Blocks.Should().HaveCount(xhtmlBlocks.Count);
        for (var i = 0; i < extracted.Blocks.Count; i++)
        {
            var extractedBlock = extracted.Blocks[i];
            var locator = extractedBlock.SourceSegments.Single().Locator
                .Should().BeOfType<EpubBookTextSourceLocator>().Subject;

            extractedBlock.Text.Should().Be(xhtmlBlocks[i].Text);
            locator.StartTextOffset.Should().Be(xhtmlBlocks[i].ResourceOffset);
            locator.EndTextOffset.Should().Be(xhtmlBlocks[i].ResourceOffset + xhtmlBlocks[i].Text.Length);
            locator.SpineIndex.Should().Be(0);
        }

        // Verify ignored and nested blocks were excluded.
        var allText = string.Join("\n", extracted.Blocks.Select(b => b.Text));
        allText.Should().NotContain("Script text must be ignored");
        allText.Should().NotContain("Style text must be ignored");
        allText.Should().NotContain("Navigation block must be ignored");
        allText.Should().NotContain("SVG text must be ignored");
        allText.Should().NotContain("Math text must be ignored");
        extracted.Blocks.Should().ContainSingle(b => b.Text.Contains("Quoted outer block", StringComparison.Ordinal));
        extracted.Blocks.Should().ContainSingle(b => b.Text == "First list item with nested paragraph.");
        extracted.Blocks.Should().Contain(b => b.Text == "Figure caption text.");
        extracted.Blocks.Should().Contain(b => b.Text == "Defined term");
        extracted.Blocks.Should().Contain(b => b.Text == "Term definition.");
        extracted.Blocks.Should().Contain(b => b.Text == "Top-level aside note.");
    }

    [Fact]
    public async Task Blank_spine_itemref_still_anchors_to_correct_raw_cfi_step()
    {
        var epub = BuildEpub(
            [("ch1", "text/ch1.xhtml", ComplexChapter)],
            includeBlankSpineItemRefFirst: true);

        using var extractStream = new MemoryStream(epub);
        var extracted = await new EpubBookTextExtractor().ExtractAsync(extractStream);
        var target = extracted.Blocks.Single(b => b.Text.StartsWith("Final target paragraph", StringComparison.Ordinal));
        var locator = (EpubBookTextSourceLocator)target.SourceSegments.Single().Locator;

        var quote = new BookTextResolvedQuote(
            BookId,
            target.Text,
            StartAt: new BookTextQuoteEnd(locator, locator.StartTextOffset!.Value),
            EndAt: new BookTextQuoteEnd(locator, locator.EndTextOffset!.Value - 1));

        var ends = await new BookPassageAnchors(new InMemoryEpubStorage(epub)).AnchorAsync(quote);

        ends.Should().NotBeNull();
        ends!.AnchorKind.Should().Be("epub_cfi");
        // Raw spine item 1 (0-based) => /6/4 even though indexed spine index is 0.
        ends.Start.Should().StartWith("epubcfi(/6/4!");
        ends.End.Should().StartWith("epubcfi(/6/4!");
    }

    [Fact]
    public async Task Malformed_xhtml_fallback_keeps_block_offsets_aligned_for_cfi_anchoring()
    {
        var epub = BuildEpub(
            [("legacy", "text/legacy.xhtml", MalformedHtmlChapter)],
            includeBlankSpineItemRefFirst: false);

        using var extractStream = new MemoryStream(epub);
        var extracted = await new EpubBookTextExtractor().ExtractAsync(extractStream);
        var second = extracted.Blocks.Single(b => b.Text == "Second legacy paragraph after ignored block.");
        var locator = (EpubBookTextSourceLocator)second.SourceSegments.Single().Locator;

        var quote = new BookTextResolvedQuote(
            BookId,
            second.Text,
            StartAt: new BookTextQuoteEnd(locator, locator.StartTextOffset!.Value),
            EndAt: new BookTextQuoteEnd(locator, locator.EndTextOffset!.Value - 1));

        var ends = await new BookPassageAnchors(new InMemoryEpubStorage(epub)).AnchorAsync(quote);

        ends.Should().NotBeNull();
        ends!.AnchorKind.Should().Be("epub_cfi");
        ends.Start.Should().StartWith("epubcfi(/6/2!");
        ends.End.Should().StartWith("epubcfi(/6/2!");
    }

    private static byte[] BuildEpub(
        IReadOnlyList<(string Id, string? Href, string? Content)> items,
        bool includeBlankSpineItemRefFirst)
    {
        const string container = """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """;

        var manifestXml = string.Join(
            "\n",
            items.Where(i => i.Href is not null)
                .Select(i => $"    <item id=\"{i.Id}\" href=\"{i.Href}\" media-type=\"application/xhtml+xml\"/>"));

        var spineItems = new List<string>();
        if (includeBlankSpineItemRefFirst)
            spineItems.Add("    <itemref id=\"blank-ref\"/>");
        spineItems.AddRange(items.Where(i => i.Href is not null).Select(i => $"    <itemref idref=\"{i.Id}\"/>"));

        var package = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="id">urn:uuid:00000000-0000-0000-0000-00000000e90b</dc:identifier>
                <dc:title>Parity Book</dc:title><dc:language>en</dc:language>
              </metadata>
              <manifest>
            {manifestXml}
              </manifest>
              <spine>
            {string.Join("\n", spineItems)}
              </spine>
            </package>
            """;

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "mimetype", "application/epub+zip");
            Add(archive, "META-INF/container.xml", container);
            Add(archive, "OEBPS/package.opf", package);
            foreach (var item in items)
            {
                if (item.Href is not null && item.Content is not null)
                    Add(archive, $"OEBPS/{item.Href}", item.Content);
            }
        }

        return output.ToArray();
    }

    private static void Add(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private sealed class InMemoryEpubStorage(byte[] bytes) : DelegatingBookAssetStorage
    {
        public override Task<StoredAssetRead?> OpenBookFileAsync(Guid bookId, StorageByteRange? range = null, CancellationToken ct = default) =>
            Task.FromResult<StoredAssetRead?>(new StoredAssetRead(
                new StoredAssetInfo("parity.epub", "application/epub+zip", bytes.Length, "etag", DateTimeOffset.UnixEpoch),
                new MemoryStream(bytes)));
    }
}
