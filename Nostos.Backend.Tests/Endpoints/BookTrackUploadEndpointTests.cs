using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Nostos.Backend.Tests.ExternalMedia;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// Several uploaded audio files become the tracks of one audiobook (#1373),
/// ordered the way a person reads the file names.
/// </summary>
public sealed class BookTrackUploadEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private readonly LibraryEndpointFactory _factory;

    public BookTrackUploadEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Several_files_become_the_tracks_of_one_book_in_natural_file_name_order()
    {
        var id = await CreateAsync("audiobook");
        var one = SyntheticMedia.Mp3(100);
        var two = SyntheticMedia.Mp3(200);
        var ten = SyntheticMedia.Mp3(300);

        // Sent out of order; "10" must come after "2", not after "1".
        var response = await UploadAsync(id, ("10 - End.mp3", ten), ("1 - Start.mp3", one), ("2 - Middle.mp3", two));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var book = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{id}"))!;
        book.HasFile.Should().BeTrue();
        book.FileName.Should().BeNull();
        book.Tracks!.Select(t => t.Number).Should().Equal(1, 2, 3);
        book.Tracks!.Select(t => t.Title).Should().Equal("1 - Start", "2 - Middle", "10 - End");
        book.Tracks!.Select(t => t.Bytes).Should().Equal(one.Length, two.Length, ten.Length);

        (await Client.GetByteArrayAsync($"/api/books/{id}/tracks/3")).Should().Equal(ten);
        var chapters = book.Chapters!.ToList();
        chapters.Should().HaveCount(3);
        chapters[0].StartTime.Should().Be(0);
        chapters[1].StartTime.Should().BeApproximately(SyntheticMedia.Mp3Seconds(100), 0.05);
    }

    [Fact]
    public async Task A_file_that_is_not_audio_leaves_the_book_without_tracks()
    {
        var id = await CreateAsync("audiobook");

        var response = await UploadAsync(id, ("1.mp3", SyntheticMedia.Mp3(100)), ("2.mp3", "not audio"u8.ToArray()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var book = (await Client.GetFromJsonAsync<BookDto>($"/api/books/{id}"))!;
        book.HasFile.Should().BeFalse();
        book.Tracks.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Only_audio_extensions_are_accepted()
    {
        var id = await CreateAsync("audiobook");

        var response = await UploadAsync(id, ("1.mp3", SyntheticMedia.Mp3(100)), ("2.epub", [1, 2, 3]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_ebook_cannot_take_tracks()
    {
        var id = await CreateAsync("ebook");

        var response = await UploadAsync(id, ("1.mp3", SyntheticMedia.Mp3(100)), ("2.mp3", SyntheticMedia.Mp3(100)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<Guid> CreateAsync(string type)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/books",
            new { type, title = $"Tracks {Guid.NewGuid():N}", author = "Test Author", forceCreate = true });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!.Id;
    }

    private Task<HttpResponseMessage> UploadAsync(Guid id, params (string Name, byte[] Bytes)[] files)
    {
        var content = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
            content.Add(part, "files", name);
        }

        return Client.PostAsync($"/api/books/{id}/tracks", content);
    }
}
