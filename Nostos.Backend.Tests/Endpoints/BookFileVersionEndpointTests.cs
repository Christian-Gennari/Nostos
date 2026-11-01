using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// A client keeps a book's bytes under this version (#1316), so a replaced file
/// showing the old bytes comes down to this endpoint answering the same twice.
/// </summary>
public sealed class BookFileVersionEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public BookFileVersionEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Version_is_stable_for_one_file_and_reports_its_length()
    {
        var book = await CreateBookAsync();
        await UploadAsync(book.Id, "%PDF-1.7 first copy");

        var first = await ReadVersionAsync(book.Id);
        var second = await ReadVersionAsync(book.Id);

        second.Should().Be(first);
        first.Bytes.Should().Be("%PDF-1.7 first copy".Length);
        first.Version.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Theory]
    [InlineData("%PDF-1.7 another copy, a different length")]
    [InlineData("%PDF-1.7 first cop2")] // Same length: only the bytes moved.
    public async Task Replacing_the_file_moves_the_version(string replacement)
    {
        var book = await CreateBookAsync();
        await UploadAsync(book.Id, "%PDF-1.7 first copy");
        var before = await ReadVersionAsync(book.Id);

        await UploadAsync(book.Id, replacement);

        (await ReadVersionAsync(book.Id)).Version.Should().NotBe(before.Version);
    }

    [Fact]
    public async Task Putting_the_same_bytes_back_is_a_new_version()
    {
        var book = await CreateBookAsync();
        await UploadAsync(book.Id, "%PDF-1.7 first copy");
        var before = await ReadVersionAsync(book.Id);

        await UploadAsync(book.Id, "%PDF-1.7 first copy");

        (await ReadVersionAsync(book.Id)).Version.Should().NotBe(before.Version);
    }

    [Fact]
    public async Task Version_is_never_cached_by_the_browser()
    {
        var book = await CreateBookAsync();
        await UploadAsync(book.Id, "%PDF-1.7 first copy");

        using var response = await Client.GetAsync($"/api/books/{book.Id}/file/version");

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task A_book_without_a_file_has_no_version()
    {
        var book = await CreateBookAsync();

        using var response = await Client.GetAsync($"/api/books/{book.Id}/file/version");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("\"etag-b\"", 19, 0)] // The object's tag moved.
    [InlineData("\"etag-a\"", 20, 0)] // Its length moved.
    [InlineData("\"etag-a\"", 19, 1)] // Rewritten with the same content tag, as S3 reports it.
    public void Every_part_of_the_stored_identity_moves_the_version(
        string entityTag,
        long length,
        int laterSeconds)
    {
        var modified = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var original = new StoredAssetInfo("book.epub", "application/epub+zip", 19, "\"etag-a\"", modified);

        var changed = original with
        {
            EntityTag = entityTag,
            Length = length,
            LastModified = modified.AddSeconds(laterSeconds),
        };

        BookFileVersion.From(changed).Should().NotBe(BookFileVersion.From(original));
    }

    private async Task<BookFileVersionDto> ReadVersionAsync(Guid bookId) =>
        (await Client.GetFromJsonAsync<BookFileVersionDto>($"/api/books/{bookId}/file/version"))!;

    private async Task<BookDto> CreateBookAsync()
    {
        using var response = await Client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title = $"File version {Guid.NewGuid():N}",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private async Task UploadAsync(Guid bookId, string content)
    {
        using var form = new MultipartFormDataContent();
        using var bytes = new ByteArrayContent(Encoding.ASCII.GetBytes(content));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(bytes, "file", "copy.pdf");
        using var response = await Client.PostAsync($"/api/books/{bookId}/file", form);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
