using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// The HTTP surface of a multi-track audiobook (issue #835), exercised through
/// the real host: playback streams one track at a time, while the Download
/// button and OPDS hand over the whole book as one archive.
/// </summary>
public sealed class MultiTrackAudiobookEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private const string AcquisitionRel = "http://opds-spec.org/acquisition";

    private static readonly byte[][] TrackBytes =
    [
        Enumerable.Range(0, 40_000).Select(i => (byte)(i * 7)).ToArray(),
        Enumerable.Range(0, 1_234).Select(i => (byte)(i * 13)).ToArray(),
    ];

    private readonly LibraryEndpointFactory _factory;

    public MultiTrackAudiobookEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Book_reports_its_tracks_and_no_primary_file()
    {
        var id = await SeedAsync("Tracks Reported");

        var book = await Client.GetFromJsonAsync<BookDto>($"/api/books/{id}");

        book!.HasFile.Should().BeTrue();
        book.FileName.Should().BeNull();
        book.Tracks.Should().HaveCount(2);
        book.Tracks!.Select(t => t.Number).Should().Equal(1, 2);
        book.Tracks!.Select(t => t.Title).Should().Equal("Opening", "Track 2");
        book.Tracks!.Select(t => t.Duration).Should().Equal(600, 61.5);
        book.Tracks!.Select(t => t.Bytes).Should().Equal(40_000, 1_234);
    }

    [Fact]
    public async Task Track_streams_inline_and_honours_byte_ranges()
    {
        var id = await SeedAsync("Track Stream");

        var whole = await Client.GetAsync($"/api/books/{id}/tracks/1");
        whole.StatusCode.Should().Be(HttpStatusCode.OK);
        whole.Content.Headers.ContentType!.MediaType.Should().Be("audio/mpeg");
        whole.Headers.AcceptRanges.Should().Contain("bytes");
        whole.Content.Headers.ContentDisposition.Should().BeNull("playback is inline");
        (await whole.Content.ReadAsByteArrayAsync()).Should().Equal(TrackBytes[0]);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/books/{id}/tracks/1");
        request.Headers.Range = new RangeHeaderValue(1000, 1999);
        var partial = await Client.SendAsync(request);
        partial.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        partial.Content.Headers.ContentRange!.ToString().Should().Be("bytes 1000-1999/40000");
        (await partial.Content.ReadAsByteArrayAsync()).Should().Equal(TrackBytes[0][1000..2000]);

        (await Client.GetAsync($"/api/books/{id}/tracks/3")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.GetAsync($"/api/books/{id}/tracks/0")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.GetAsync($"/api/books/{id}/file")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "there is no primary file to stream");
    }

    [Fact]
    public async Task Single_track_downloads_under_a_readable_name()
    {
        var id = await SeedAsync("Track Download");

        var response = await Client.GetAsync($"/api/books/{id}/tracks/1/download");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileNameStar.Should().Be("01 - Opening.mp3");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(TrackBytes[0]);
    }

    [Fact]
    public async Task Download_delivers_the_whole_book_as_one_resumable_archive()
    {
        var id = await SeedAsync("Whole Book Download");

        var response = await Client.GetAsync($"/api/books/{id}/file/download");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        response.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("Whole Book Download - Test Author.zip");
        response.Headers.AcceptRanges.Should().Contain("bytes");
        response.Headers.ETag.Should().NotBeNull();
        var archiveBytes = await response.Content.ReadAsByteArrayAsync();
        response.Content.Headers.ContentLength.Should().Be(archiveBytes.Length, "the length is known before the first byte");

        using (var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read))
        {
            archive.Entries.Select(e => e.FullName).Should().Equal(
                "manifest.json", "01 - Opening.mp3", "02.mp3");
            Read(archive, "01 - Opening.mp3").Should().Equal(TrackBytes[0]);
            Read(archive, "02.mp3").Should().Equal(TrackBytes[1]);
        }

        // An interrupted download resumes from where it stopped.
        var resumeFrom = archiveBytes.Length / 2;
        var resume = new HttpRequestMessage(HttpMethod.Get, $"/api/books/{id}/file/download");
        resume.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        var rest = await Client.SendAsync(resume);
        rest.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        rest.Content.Headers.ContentRange!.ToString()
            .Should().Be($"bytes {resumeFrom}-{archiveBytes.Length - 1}/{archiveBytes.Length}");
        (await rest.Content.ReadAsByteArrayAsync()).Should().Equal(archiveBytes[resumeFrom..]);

        // And an unchanged archive is not sent twice.
        var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/books/{id}/file/download");
        conditional.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        (await Client.SendAsync(conditional)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Opds_offers_the_book_as_a_package_and_as_a_streaming_manifest()
    {
        var id = await SeedAsync("Opds Multi Track");

        var feed = XDocument.Parse(await Client.GetStringAsync("/opds/"));
        var entry = feed.Root!.Elements(Atom + "entry")
            .Single(e => e.Element(Atom + "title")!.Value == "Opds Multi Track");
        var links = entry.Elements(Atom + "link")
            .Where(link => (string?)link.Attribute("rel") == AcquisitionRel)
            .ToDictionary(link => (string)link.Attribute("type")!, link => (string)link.Attribute("href")!);

        links.Keys.Should().BeEquivalentTo("application/audiobook+zip", "application/audiobook+json");
        links["application/audiobook+zip"].Should().EndWith($"/opds/books/{id}/audiobook");
        links["application/audiobook+json"].Should().EndWith($"/opds/books/{id}/manifest.json");

        var package = await Client.GetAsync(links["application/audiobook+zip"]);
        package.StatusCode.Should().Be(HttpStatusCode.OK);
        package.Content.Headers.ContentType!.MediaType.Should().Be("application/audiobook+zip");
        package.Content.Headers.ContentDisposition!.FileNameStar.Should().EndWith(".audiobook");
        using (var archive = new ZipArchive(await package.Content.ReadAsStreamAsync(), ZipArchiveMode.Read))
        {
            archive.GetEntry("manifest.json").Should().NotBeNull("a Readium package keeps its manifest at the root");
        }

        var manifestResponse = await Client.GetAsync(links["application/audiobook+json"]);
        manifestResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/audiobook+json");
        using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync());
        var hrefs = manifest.RootElement.GetProperty("readingOrder").EnumerateArray()
            .Select(item => item.GetProperty("href").GetString()!)
            .ToList();
        hrefs.Should().HaveCount(2);
        hrefs[0].Should().EndWith($"/opds/books/{id}/tracks/1");
        manifest.RootElement.GetProperty("links")[0].GetProperty("href").GetString()
            .Should().Be(links["application/audiobook+json"]);

        // The manifest's own links resolve to the audio.
        (await (await Client.GetAsync(hrefs[1])).Content.ReadAsByteArrayAsync()).Should().Equal(TrackBytes[1]);
    }

    [Fact]
    public async Task Package_and_manifest_do_not_exist_for_a_single_file_book()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/books",
            new { type = "ebook", title = "Single File For Package", author = "Test Author", forceCreate = true });
        var book = (await response.Content.ReadFromJsonAsync<BookDto>())!;

        (await Client.GetAsync($"/opds/books/{book.Id}/audiobook")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.GetAsync($"/opds/books/{book.Id}/manifest.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Uploading_a_single_file_replaces_the_tracks()
    {
        var id = await SeedAsync("Replaced By Upload");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([1, 2, 3, 4]);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(file, "file", "whole-book.mp3");
        var upload = await Client.PostAsync($"/api/books/{id}/file", form);
        upload.StatusCode.Should().Be(HttpStatusCode.OK);

        var book = await Client.GetFromJsonAsync<BookDto>($"/api/books/{id}");
        book!.Tracks.Should().BeNull("a book holds one file or a track list, never both");
        book.FileName.Should().Be("book.mp3");
        (await Client.GetAsync($"/api/books/{id}/tracks/1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        File.Exists(Path.Combine(_factory.BooksRootPath, id.ToString(), "track-0001.mp3")).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_the_book_removes_its_tracks()
    {
        var id = await SeedAsync("Deleted With Tracks");

        (await Client.DeleteAsync($"/api/books/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        Directory.Exists(Path.Combine(_factory.BooksRootPath, id.ToString())).Should().BeFalse();
    }

    private async Task<Guid> SeedAsync(string title)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/books",
            new { type = "audiobook", title, author = "Test Author", forceCreate = true });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var book = (await response.Content.ReadFromJsonAsync<BookDto>())!;

        var folder = Path.Combine(_factory.BooksRootPath, book.Id.ToString());
        Directory.CreateDirectory(folder);
        var tracks = new List<BookTrack>();
        for (var number = 1; number <= TrackBytes.Length; number++)
        {
            var bytes = TrackBytes[number - 1];
            var fileName = BookTrackFormats.CanonicalFileName(number, ".mp3");
            await File.WriteAllBytesAsync(Path.Combine(folder, fileName), bytes);
            tracks.Add(new BookTrack(
                number,
                fileName,
                "audio/mpeg",
                number == 1 ? 600_000 : 61_500,
                bytes.Length,
                Crc32.Compute(bytes),
                number == 1 ? "Opening" : null));
        }

        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_factory.DatabasePath}")
            .Options;
        await using var db = new NostosDbContext(options);
        var stored = await db.Books.SingleAsync(b => b.Id == book.Id);
        stored.FileDetails.HasFile = true;
        stored.FileDetails.FileName = null;
        stored.FileDetails.TracksJson = BookTrackList.Serialize(tracks);
        await db.SaveChangesAsync();

        return book.Id;
    }

    private static byte[] Read(ZipArchive archive, string name)
    {
        using var stream = archive.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
