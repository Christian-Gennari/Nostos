using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Support;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Nostos.Backend.Tests.Services;

/// <summary>An uploaded EPUB or PDF is untrusted; these pin what is read from it and that nothing throws.</summary>
public sealed class BookFileMetadataReaderTests
{
    private static byte[] Epub(
        string opf,
        byte[]? cover = null,
        string coverPath = "OEBPS/images/cover.png",
        string opfPath = "OEBPS/content.opf")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "mimetype", "application/epub+zip");
            Add(zip, "META-INF/container.xml",
                $"""
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="{opfPath}" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            Add(zip, opfPath, opf);
            if (cover is not null)
            {
                var entry = zip.CreateEntry(coverPath);
                using var stream = entry.Open();
                stream.Write(cover);
            }
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string Opf(string metadataExtra = "", string manifest = "") =>
        $"""
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:title>  The   Left Hand of Darkness </dc:title>
            <dc:creator>Ursula K. Le Guin</dc:creator>
            <dc:language>en-US</dc:language>
            {metadataExtra}
          </metadata>
          <manifest>{manifest}</manifest>
        </package>
        """;

    private static Task<BookFileMetadata> Read(byte[] bytes, string name) =>
        BookFileMetadataReader.ReadAsync(new MemoryStream(bytes), name);

    [Fact]
    public async Task Epub_ReadsTitleAuthorAndLanguage()
    {
        var found = await Read(Epub(Opf()), "book.epub");

        found.Title.Should().Be("The Left Hand of Darkness");
        found.Author.Should().Be("Ursula K. Le Guin");
        found.Language.Should().Be("English");
        found.PageCount.Should().BeNull();
        found.CoverBytes.Should().BeNull();
    }

    [Fact]
    public async Task Epub_JoinsSeveralAuthors()
    {
        var opf = Opf("<dc:creator>Second Author</dc:creator><dc:creator>Ursula K. Le Guin</dc:creator>");

        var found = await Read(Epub(opf), "book.epub");

        found.Author.Should().Be("Ursula K. Le Guin, Second Author");
    }

    [Fact]
    public async Task Epub3_ReadsCoverMarkedAsCoverImage()
    {
        var png = TestImages.Create(".png", 20, 30);
        var opf = Opf(manifest: """<item id="c" href="images/cover.png" media-type="image/png" properties="cover-image"/>""");

        var found = await Read(Epub(opf, png), "book.epub");

        found.CoverBytes.Should().Equal(png);
        found.CoverExtension.Should().Be(".png");
    }

    [Fact]
    public async Task Epub2_ReadsCoverNamedByMetaElement()
    {
        var jpg = TestImages.Create(".jpg", 20, 30);
        var opf = Opf(
            """<meta name="cover" content="cov"/>""",
            """<item id="cov" href="images/cover.png" media-type="image/jpeg"/>""");

        var found = await Read(Epub(opf, jpg), "book.epub");

        found.CoverExtension.Should().Be(".jpg", because: "the type comes from the bytes, not the name");
    }

    [Fact]
    public async Task Epub_IgnoresACoverThatIsNotAnImage()
    {
        var opf = Opf(manifest: """<item id="c" href="images/cover.png" properties="cover-image"/>""");

        var found = await Read(Epub(opf, Encoding.UTF8.GetBytes("not an image")), "book.epub");

        found.Title.Should().Be("The Left Hand of Darkness");
        found.CoverBytes.Should().BeNull();
    }

    [Fact]
    public async Task Epub_RefusesACoverPathThatLeavesTheArchive()
    {
        var opf = Opf(manifest: """<item id="c" href="../../../etc/cover.png" properties="cover-image"/>""");

        var found = await Read(Epub(opf, TestImages.Create(".png", 5, 5)), "book.epub");

        found.CoverBytes.Should().BeNull();
    }

    [Fact]
    public async Task Epub_DoesNotExpandDoctypeEntities()
    {
        var opf = "<!DOCTYPE package [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>" + Opf().Replace("The   Left", "&x;");

        var found = await Read(Epub(opf), "book.epub");

        found.Should().Be(BookFileMetadata.Empty);
    }

    [Fact]
    public async Task Epub_WithUnknownLanguageCode_KeepsTheCode()
    {
        var opf = Opf().Replace("en-US", "xx-zz");

        var found = await Read(Epub(opf), "book.epub");

        found.Language.Should().NotBeNullOrWhiteSpace();
        found.Language.Should().NotContain("Unknown");
    }

    [Fact]
    public async Task Pdf_ReadsTitleAuthorAndPageCount()
    {
        var builder = new PdfDocumentBuilder();
        builder.DocumentInformation.Title = "Dune";
        builder.DocumentInformation.Author = "Frank Herbert";
        for (var i = 0; i < 3; i++) builder.AddPage(200, 200);

        var found = await Read(builder.Build(), "dune.pdf");

        found.Title.Should().Be("Dune");
        found.Author.Should().Be("Frank Herbert");
        found.PageCount.Should().Be(3);
        found.CoverBytes.Should().BeNull();
    }

    [Fact]
    public async Task Pdf_WithoutInfoStillGivesPageCount()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(200, 200);

        var found = await Read(builder.Build(), "x.pdf");

        found.Title.Should().BeNull();
        found.PageCount.Should().Be(1);
    }

    [Theory]
    [InlineData("book.epub")]
    [InlineData("book.pdf")]
    public async Task BrokenFiles_GiveEmptyMetadata(string name)
    {
        var found = await Read(Encoding.UTF8.GetBytes("this is not a book"), name);

        found.Should().Be(BookFileMetadata.Empty);
    }

    [Fact]
    public async Task OtherFormats_AreNotRead()
    {
        var found = await Read(Epub(Opf()), "book.mp3");

        found.Should().Be(BookFileMetadata.Empty);
    }
}
