using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// An uploaded EPUB or PDF fills what the book row left empty (#1365), and
/// never overwrites what a person typed.
/// </summary>
public sealed class BookUploadFileMetadataTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public BookUploadFileMetadataTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Epub_upload_fills_author_language_and_cover_and_replaces_a_file_name_title()
    {
        var book = await CreateBookAsync(title: "my_scan-final");

        await UploadAsync(book.Id, "my_scan-final.epub", "application/epub+zip", Epub());

        var after = await GetAsync(book.Id);
        after.Title.Should().Be("Embedded Title");
        after.Author.Should().Be("Embedded Author");
        after.Language.Should().Be("French");
        after.CoverVersion.Should().NotBeNullOrEmpty();
        using var cover = await Client.GetAsync($"/api/books/{book.Id}/cover");
        cover.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Epub_upload_keeps_what_the_user_typed()
    {
        var book = await CreateBookAsync(title: "My own title", author: "My Author", language: "Swedish");

        await UploadAsync(book.Id, "x.epub", "application/epub+zip", Epub());

        var after = await GetAsync(book.Id);
        after.Title.Should().Be("My own title");
        after.Author.Should().Be("My Author");
        after.Language.Should().Be("Swedish");
        after.CoverVersion.Should().NotBeNullOrEmpty("an empty cover is still filled");
    }

    [Fact]
    public async Task Pdf_upload_fills_the_page_count()
    {
        var builder = new PdfDocumentBuilder();
        builder.DocumentInformation.Title = "Pdf Title";
        for (var i = 0; i < 4; i++) builder.AddPage(200, 200);
        var book = await CreateBookAsync(title: "scan");

        await UploadAsync(book.Id, "scan.pdf", "application/pdf", builder.Build());

        var after = await GetAsync(book.Id);
        after.PageCount.Should().Be(4);
        after.Title.Should().Be("Pdf Title");
    }

    [Fact]
    public async Task Preview_endpoint_reads_metadata_without_creating_a_book()
    {
        using var form = new MultipartFormDataContent();
        using var bytes = new ByteArrayContent(Epub());
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/epub+zip");
        form.Add(bytes, "file", "x.epub");

        using var response = await Client.PostAsync("/api/books/file-metadata", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var found = (await response.Content.ReadFromJsonAsync<BookFileMetadataDto>())!;
        found.Should().Be(new BookFileMetadataDto("Embedded Title", "Embedded Author", "French", null));
    }

    private static byte[] Epub()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "META-INF/container.xml",
                """<container xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf"/></rootfiles></container>""");
            Add(zip, "content.opf",
                """
                <package xmlns="http://www.idpf.org/2007/opf"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Embedded Title</dc:title><dc:creator>Embedded Author</dc:creator><dc:language>fr</dc:language>
                </metadata><manifest><item id="c" href="c.png" properties="cover-image"/></manifest></package>
                """);
            using var stream = zip.CreateEntry("c.png").Open();
            stream.Write(TestImages.Create(".png", 12, 18));
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private async Task<BookDto> CreateBookAsync(string title, string? author = null, string? language = null)
    {
        using var response = await Client.PostAsJsonAsync("/api/books", new { type = "ebook", title, author, language });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private async Task<BookDto> GetAsync(Guid id) =>
        (await Client.GetFromJsonAsync<BookDto>($"/api/books/{id}"))!;

    private async Task UploadAsync(Guid id, string fileName, string contentType, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        using var bytes = new ByteArrayContent(content);
        bytes.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(bytes, "file", fileName);
        using var response = await Client.PostAsync($"/api/books/{id}/file", form);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
