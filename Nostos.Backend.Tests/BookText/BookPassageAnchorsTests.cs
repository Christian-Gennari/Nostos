using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Portability;
using Nostos.Product.BookText;
using Xunit;

namespace Nostos.Backend.Tests.BookText;

/// <summary>
/// A passage kept over MCP (no reader open) gets its two ends from the book
/// text index: epub.js point CFIs written on the server, or PDF pages.
/// </summary>
public sealed class BookPassageAnchorsTests
{
    private static readonly Guid BookId = Guid.Parse("00000000-0000-0000-0000-00000000c0de");

    // Book Two, spine item 2 (/6/4): the passage runs from "The story" in the
    // first paragraph to "to all." in the third, across inline markup, an
    // empty anchor element and a line break inside a paragraph.
    private const string BookTwo = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>Book Two</title></head>
        <body>
        <section id="s1">
        <h2>Book Two</h2>
        <p>Glaucon spoke first. <a id="p359"/>The story of <em>Gyges</em> the shepherd begins here.</p>
        <p>He found a ring
           of gold, and turned its collar inward.</p>
        <p>And so they say he became <i>invisible</i> to all.</p>
        </section>
        </body>
        </html>
        """;

    private const string BookOne = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>Book One</title></head>
        <body>
        <p>Socrates went down to the Piraeus.</p>
        <p>He asked what justice is.</p>
        </body>
        </html>
        """;

    [Fact]
    public async Task An_epub_passage_gets_the_point_cfis_epub_js_writes()
    {
        var blocks = await BlocksAsync();
        var first = blocks.Single(b => b.Text.StartsWith("Glaucon", StringComparison.Ordinal));
        var last = blocks.Single(b => b.Text.StartsWith("And so", StringComparison.Ordinal));
        var quote = Quote(
            "The story of Gyges the shepherd begins here.\n\nHe found a ring of gold, and turned its collar inward.\n\nAnd so they say he became invisible to all.",
            first, first.Text.IndexOf("The story", StringComparison.Ordinal),
            last, last.Text.Length - 1);

        var ends = await Anchors().AnchorAsync(quote);

        ends.Should().Be(new BookPassageEnds(
            "epub_cfi",
            "epubcfi(/6/4!/4/2[s1]/4/3:0)",
            "epubcfi(/6/4!/4/2[s1]/8/3:8)"));
    }

    [Fact]
    public async Task A_passage_may_start_in_one_spine_item_and_end_in_the_next()
    {
        var blocks = await BlocksAsync();
        var first = blocks.Single(b => b.Text.StartsWith("He asked", StringComparison.Ordinal));
        var last = blocks.Single(b => b.Text == "Book Two");
        var quote = Quote("He asked what justice is.\n\nBook Two", first, 0, last, last.Text.Length - 1);

        var ends = await Anchors().AnchorAsync(quote);

        ends!.Start.Should().Be("epubcfi(/6/2!/4/4/1:0)");
        ends.End.Should().Be("epubcfi(/6/4!/4/2[s1]/2/1:8)");
    }

    [Fact]
    public async Task When_the_index_offset_is_off_the_quote_is_found_by_its_words()
    {
        var blocks = await BlocksAsync();
        var first = blocks.Single(b => b.Text.StartsWith("Glaucon", StringComparison.Ordinal));
        var last = blocks.Single(b => b.Text.StartsWith("He found", StringComparison.Ordinal));
        var quote = Quote(
            "The story of Gyges the shepherd begins here.\n\nHe found a ring of gold, and turned its collar inward.",
            first, 0,
            last, 3);

        var ends = await Anchors().AnchorAsync(quote);

        ends!.Start.Should().Be("epubcfi(/6/4!/4/2[s1]/4/3:0)");
        // "inward." ends the second text node ("He found a ring\n   of gold, ... inward.").
        ends.End.Should().Be($"epubcfi(/6/4!/4/2[s1]/6/1:{"He found a ring\n   of gold, and turned its collar inward.".Length})");
    }

    [Fact]
    public async Task Words_that_are_not_in_the_book_give_no_ends()
    {
        var blocks = await BlocksAsync();
        var block = blocks.Single(b => b.Text.StartsWith("Glaucon", StringComparison.Ordinal));
        var quote = Quote("Nothing like this is written anywhere in the book.", block, 0, block, 5);

        (await Anchors().AnchorAsync(quote)).Should().BeNull();
    }

    [Fact]
    public async Task A_pdf_passage_runs_from_its_first_page_to_its_last()
    {
        var quote = new BookTextResolvedQuote(
            BookId,
            "words",
            StartAt: new BookTextQuoteEnd(new PdfBookTextSourceLocator(PageIndex: 11), 40),
            EndAt: new BookTextQuoteEnd(new PdfBookTextSourceLocator(PageIndex: 13), 12));

        var ends = await new BookPassageAnchors(new EpubStorage([])).AnchorAsync(quote);

        ends.Should().Be(new BookPassageEnds("pdf_page", "12", "14"));
    }

    [Fact]
    public async Task A_book_without_its_file_gives_no_ends()
    {
        var blocks = await BlocksAsync();
        var block = blocks[0];
        var quote = Quote(block.Text, block, 0, block, block.Text.Length - 1);

        (await new BookPassageAnchors(new EpubStorage(null)).AnchorAsync(quote)).Should().BeNull();
    }

    private static BookPassageAnchors Anchors() => new(new EpubStorage(Epub()));

    private static BookTextResolvedQuote Quote(string text, BookTextArtifactBlock start, int startWithin, BookTextArtifactBlock end, int endWithin)
    {
        var startLocator = (EpubBookTextSourceLocator)start.SourceSegments[0].Locator;
        var endLocator = (EpubBookTextSourceLocator)end.SourceSegments[0].Locator;
        return new BookTextResolvedQuote(
            BookId,
            text,
            StartAt: new BookTextQuoteEnd(startLocator, startLocator.StartTextOffset!.Value + startWithin),
            EndAt: new BookTextQuoteEnd(endLocator, endLocator.StartTextOffset!.Value + endWithin));
    }

    private static async Task<IReadOnlyList<BookTextArtifactBlock>> BlocksAsync()
    {
        using var stream = new MemoryStream(Epub());
        var document = await new EpubBookTextExtractor().ExtractAsync(stream);
        return document.Blocks;
    }

    private static byte[] Epub()
    {
        const string container = """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/package.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """;
        const string package = """
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="id">urn:uuid:00000000-0000-0000-0000-00000000c0de</dc:identifier>
                <dc:title>Republic</dc:title><dc:language>en</dc:language>
              </metadata>
              <manifest>
                <item id="b1" href="text/book-1.xhtml" media-type="application/xhtml+xml"/>
                <item id="b2" href="text/book-2.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine><itemref idref="b1"/><itemref idref="b2"/></spine>
            </package>
            """;

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "mimetype", "application/epub+zip");
            Add(archive, "META-INF/container.xml", container);
            Add(archive, "OEBPS/package.opf", package);
            Add(archive, "OEBPS/text/book-1.xhtml", BookOne);
            Add(archive, "OEBPS/text/book-2.xhtml", BookTwo);
        }

        return output.ToArray();
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private sealed class EpubStorage(byte[]? bytes) : DelegatingBookAssetStorage
    {
        public override Task<StoredAssetRead?> OpenBookFileAsync(Guid bookId, StorageByteRange? range = null, CancellationToken ct = default) =>
            Task.FromResult(bytes is null
                ? null
                : new StoredAssetRead(
                    new StoredAssetInfo("republic.epub", "application/epub+zip", bytes.Length, "etag", DateTimeOffset.UnixEpoch),
                    new MemoryStream(bytes)));
    }
}
