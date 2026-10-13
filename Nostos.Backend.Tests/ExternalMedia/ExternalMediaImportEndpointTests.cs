using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services;
using Nostos.Backend.Services.ExternalMedia;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.ExternalMedia;

public sealed class ExternalMediaImportEndpointTests : IDisposable
{
    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "nostos-external-import-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public ExternalMediaImportEndpointTests()
    {
        _root = Path.Combine(_temp, "media");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
            Directory.Delete(_temp, recursive: true);
    }

    [Fact]
    public async Task Importing_everything_ready_links_books_without_copying_or_changing_a_source_file()
    {
        var one = SyntheticMedia.Mp3(200, album: "A Novel", artist: "Jane Author", title: "Opening");
        var two = SyntheticMedia.Mp3(300, album: "A Novel", artist: "Jane Author");
        Write("Jane Author/A Novel/01 - One.mp3", one);
        Write("Jane Author/A Novel/02 - Two.mp3", two);
        Write("Jane Author/A Novel/cover.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        Write("Ebooks/Essay.epub", SyntheticMedia.Epub("An Essay", "Sam Writer"));
        Write("Ebooks/Damaged.epub", "not a zip"u8.ToArray());
        var sources = SnapshotAll();
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        await ScanAsync(factory, client);

        var import = await ImportAsync(factory, client, new { allReady = true });

        import.Status.Should().Be("done");
        import.Outcomes.Select(o => (o.Key, o.Outcome)).Should().BeEquivalentTo(
        [
            ("tracks:Jane Author/A Novel", "imported"),
            ("file:Ebooks/Essay.epub", "imported"),
        ]);
        sources.Should().OnlyContain(snapshot => snapshot.IsUnchanged());
        SnapshotAll().Should().HaveCount(sources.Count);

        await using var db = await Contexts(factory).CreateDbContextAsync();
        var audiobook = await db.Books.OfType<AudioBookModel>().SingleAsync();
        audiobook.Should().BeEquivalentTo(new
        {
            Title = "A Novel",
            Author = "Jane Author",
            Status = BookStatus.Ready,
        });
        audiobook.FileDetails.HasFile.Should().BeTrue();
        audiobook.FileDetails.FileName.Should().BeNull();
        audiobook.FileDetails.CoverFileName.Should().Be("cover.jpg");
        var tracks = BookTrackList.Parse(audiobook.FileDetails.TracksJson);
        tracks.Select(t => (t.Number, t.FileName, t.Bytes, t.Crc32, t.Title)).Should().Equal(
            (1, "track-0001.mp3", (long)one.Length, Crc32.Compute(one), "Opening"),
            (2, "track-0002.mp3", (long)two.Length, Crc32.Compute(two), "02 - Two"));
        audiobook.FileDetails.ChaptersJson.Should().Contain("Opening").And.Contain("02 - Two");

        var ebook = await db.Books.OfType<EBookModel>().SingleAsync();
        ebook.FileDetails.FileName.Should().Be("book.epub");
        (await db.ExternalMediaLinks.Select(l => new { l.BookId, l.TrackNumber, l.RelativePath }).ToListAsync())
            .Should().BeEquivalentTo(
            [
                new { BookId = audiobook.Id, TrackNumber = 1, RelativePath = "Jane Author/A Novel/01 - One.mp3" },
                new { BookId = audiobook.Id, TrackNumber = 2, RelativePath = "Jane Author/A Novel/02 - Two.mp3" },
                new { BookId = ebook.Id, TrackNumber = 0, RelativePath = "Ebooks/Essay.epub" },
            ]);

        // The only thing Nostos stored is the cover it copied.
        Directory.EnumerateFiles(factory.BooksRootPath, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Should().Equal("cover.jpg");

        var secondTrack = await client.GetByteArrayAsync($"/api/books/{audiobook.Id}/tracks/2");
        secondTrack.Should().Equal(two);
        var epub = await client.GetAsync($"/api/books/{ebook.Id}/file");
        epub.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Importing_again_creates_no_second_book_and_no_second_reference()
    {
        Write("Ebooks/Essay.epub", SyntheticMedia.Epub("An Essay", "Sam Writer"));
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        await ScanAsync(factory, client);
        await ImportAsync(factory, client, new { allReady = true });

        // The stale scan still lists the book as ready: a repeated request.
        var repeated = await ImportAsync(factory, client, new { allReady = true });
        // A fresh scan sees it as linked, so "everything ready" is empty.
        await ScanAsync(factory, client);
        var afterRescan = await client.PostAsJsonAsync(ExternalMediaEndpoints.Route + "/import", new { allReady = true });
        var named = await ImportAsync(factory, client, new { keys = new[] { "file:Ebooks/Essay.epub" } });

        repeated.Outcomes.Should().ContainSingle().Which.Outcome.Should().Be("already_linked");
        afterRescan.StatusCode.Should().Be(HttpStatusCode.Conflict);
        named.Outcomes.Should().ContainSingle().Which.Outcome.Should().Be("already_linked");
        await using var db = await Contexts(factory).CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(1);
        (await db.ExternalMediaLinks.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_book_that_needs_review_is_imported_only_when_named_and_stays_a_separate_book()
    {
        Write("Ebooks/Essay.epub", SyntheticMedia.Epub("An Essay", "Sam Writer"));
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/books", new { type = "ebook", title = "An Essay", author = "Sam Writer" }))
            .EnsureSuccessStatusCode();
        await ScanAsync(factory, client);

        var everything = await client.PostAsJsonAsync(ExternalMediaEndpoints.Route + "/import", new { allReady = true });
        var named = await ImportAsync(factory, client, new { keys = new[] { "file:Ebooks/Essay.epub" } });

        everything.StatusCode.Should().Be(HttpStatusCode.Conflict);
        named.Outcomes.Should().ContainSingle().Which.Outcome.Should().Be("imported");
        await using var db = await Contexts(factory).CreateDbContextAsync();
        var books = await db.Books.ToListAsync();
        books.Should().HaveCount(2);
        books.Count(b => b.FileDetails.HasFile).Should().Be(1);
    }

    [Fact]
    public async Task A_file_that_vanished_after_the_scan_fails_alone_and_leaves_no_book_behind()
    {
        Write("Ebooks/Essay.epub", SyntheticMedia.Epub("An Essay", "Sam Writer"));
        Write("Ebooks/Other.pdf", SyntheticMedia.Pdf());
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        await ScanAsync(factory, client);
        File.Delete(Path.Combine(_root, "Ebooks", "Essay.epub"));

        var import = await ImportAsync(factory, client, new { allReady = true });

        import.Outcomes.Select(o => (o.Key, o.Outcome)).Should().BeEquivalentTo(
        [
            ("file:Ebooks/Essay.epub", "failed"),
            ("file:Ebooks/Other.pdf", "imported"),
        ]);
        await using var db = await Contexts(factory).CreateDbContextAsync();
        (await db.Books.Select(b => b.Title).ToListAsync()).Should().Equal("Other");
    }

    [Fact]
    public async Task A_folder_with_an_unreadable_track_is_not_imported_and_leaves_no_book_behind()
    {
        Write("Title/01.mp3", SyntheticMedia.Mp3(100));
        Write("Title/02.mp3", "not audio"u8.ToArray());
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        await ScanAsync(factory, client);

        var import = await ImportAsync(factory, client, new { allReady = true });

        import.Outcomes.Should().ContainSingle().Which.Outcome.Should().Be("failed");
        await using var db = await Contexts(factory).CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(0);
        (await db.ExternalMediaLinks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Removing_an_imported_book_keeps_its_source_and_a_rescan_offers_it_again()
    {
        Write("Title/01.mp3", SyntheticMedia.Mp3(100));
        Write("Title/02.mp3", SyntheticMedia.Mp3(100));
        Write("Title/cover.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var sources = SnapshotAll();
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        await ScanAsync(factory, client);
        var bookId = (await ImportAsync(factory, client, new { allReady = true })).Outcomes.Single().BookId;

        var deleted = await client.DeleteAsync($"/api/books/{bookId}");
        var rescan = await ScanAsync(factory, client);

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        sources.Should().OnlyContain(snapshot => snapshot.IsUnchanged());
        Directory.EnumerateFiles(factory.BooksRootPath, "*", SearchOption.AllDirectories).Should().BeEmpty();
        rescan.Items.Should().ContainSingle().Which.Status.Should().Be("ready");
        rescan.Missing.Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_that_disappears_later_leaves_the_book_in_place_and_is_reported_missing()
    {
        Write("Ebooks/Essay.epub", SyntheticMedia.Epub("An Essay", "Sam Writer"));
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        await ScanAsync(factory, client);
        var bookId = (await ImportAsync(factory, client, new { allReady = true })).Outcomes.Single().BookId;
        File.Delete(Path.Combine(_root, "Ebooks", "Essay.epub"));

        var file = await client.GetAsync($"/api/books/{bookId}/file");
        var book = await client.GetAsync($"/api/books/{bookId}");
        var rescan = await ScanAsync(factory, client);

        file.StatusCode.Should().Be(HttpStatusCode.NotFound);
        book.StatusCode.Should().Be(HttpStatusCode.OK);
        rescan.Missing.Should().ContainSingle().Which.BookId.Should().Be(bookId!.Value);
        await using var db = await Contexts(factory).CreateDbContextAsync();
        (await db.ExternalMediaLinks.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_book_reports_whether_it_is_linked_and_whether_its_files_can_be_read()
    {
        Write("Title/01.mp3", SyntheticMedia.Mp3(100));
        Write("Title/02.mp3", SyntheticMedia.Mp3(100));
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        await ScanAsync(factory, client);
        var bookId = (await ImportAsync(factory, client, new { allReady = true })).Outcomes.Single().BookId;
        var route = $"{ExternalMediaEndpoints.Route}/books/";

        var available = await client.GetFromJsonAsync<ExternalMediaBookResponse>(route + bookId);
        File.Delete(Path.Combine(_root, "Title", "02.mp3"));
        var partly = await client.GetFromJsonAsync<ExternalMediaBookResponse>(route + bookId);
        var unrelated = await client.GetFromJsonAsync<ExternalMediaBookResponse>(route + Guid.NewGuid());

        available.Should().Be(new ExternalMediaBookResponse(Linked: true, Files: 2, UnavailableFiles: 0));
        partly.Should().Be(new ExternalMediaBookResponse(Linked: true, Files: 2, UnavailableFiles: 1));
        unrelated.Should().Be(new ExternalMediaBookResponse(Linked: false, Files: 0, UnavailableFiles: 0));
    }

    [Fact]
    public async Task Importing_before_scanning_is_refused()
    {
        await using var factory = new Factory(_root);

        var response = await factory.CreateClient()
            .PostAsJsonAsync(ExternalMediaEndpoints.Route + "/import", new { allReady = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("external_media_scan_required");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"allReady\":true,\"keys\":[\"file:a.epub\"]}")]
    [InlineData("{\"keys\":[]}")]
    public async Task A_selection_must_be_everything_ready_or_a_list_never_both_or_neither(string body)
    {
        await using var factory = new Factory(_root);

        var response = await factory.CreateClient().PostAsync(
            ExternalMediaEndpoints.Route + "/import",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_key_can_only_name_a_scanned_candidate_never_a_path()
    {
        Write("Ebooks/Essay.epub", SyntheticMedia.Epub("An Essay", "Sam Writer"));
        File.WriteAllBytes(Path.Combine(_temp, "outside.epub"), SyntheticMedia.Epub("Outside", "Nobody"));
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        await ScanAsync(factory, client);

        var response = await client.PostAsJsonAsync(
            ExternalMediaEndpoints.Route + "/import",
            new { keys = new[] { "file:../outside.epub", "file:" + Path.Combine(_temp, "outside.epub") } });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("external_media_nothing_to_import");
        await using var db = await Contexts(factory).CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(0);
    }

    private void Write(string relativePath, byte[] content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 3, 2, 10, 0, 0, DateTimeKind.Utc));
    }

    private List<SourceSnapshot> SnapshotAll() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(SourceSnapshot.Take).ToList();

    private static IDbContextFactory<NostosDbContext> Contexts(LibraryEndpointFactory factory) =>
        factory.Services.GetRequiredService<IDbContextFactory<NostosDbContext>>();

    private static async Task<ExternalMediaScanResponse> ScanAsync(LibraryEndpointFactory factory, HttpClient client)
    {
        (await client.PostAsync(ExternalMediaEndpoints.Route + "/scan", null))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        await factory.Services.GetRequiredService<ExternalMediaScanService>().Completion
            .WaitAsync(TimeSpan.FromSeconds(30));
        return (await client.GetFromJsonAsync<ExternalMediaScanResponse>(ExternalMediaEndpoints.Route + "/scan"))!;
    }

    private static async Task<ExternalMediaImportState> ImportAsync(
        LibraryEndpointFactory factory, HttpClient client, object request)
    {
        var started = await client.PostAsJsonAsync(ExternalMediaEndpoints.Route + "/import", request);
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        await factory.Services.GetRequiredService<ExternalMediaImportService>().Completion
            .WaitAsync(TimeSpan.FromSeconds(30));
        return (await client.GetFromJsonAsync<ExternalMediaImportState>(ExternalMediaEndpoints.Route + "/import"))!;
    }

    private sealed class Factory(string externalRoot) : LibraryEndpointFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ExternalMedia:Root", externalRoot);
        }
    }
}
