using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.ExternalMedia;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.ExternalMedia;

public sealed class ExternalMediaScanEndpointTests : IDisposable
{
    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "nostos-external-scan-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public ExternalMediaScanEndpointTests()
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
    public async Task A_scan_proposes_books_and_changes_neither_the_library_nor_the_folder()
    {
        Write("Jane Author/A Novel/01 - One.mp3", SyntheticMedia.Mp3(200, album: "A Novel", artist: "Jane Author"));
        Write("Jane Author/A Novel/02 - Two.mp3", SyntheticMedia.Mp3(200, album: "A Novel", artist: "Jane Author"));
        Write("Jane Author/A Novel/cover.jpg", [1, 2, 3]);
        Write("Ebooks/Essay.epub", SyntheticMedia.Epub("An Essay", "Sam Writer"));
        Write("Ebooks/Untitled Paper.pdf", SyntheticMedia.Pdf());
        Write("Ebooks/Damaged.epub", "not a zip"u8.ToArray());
        Write("Other/Album.flac", [1]);
        var before = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(SourceSnapshot.Take).ToList();
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();

        var scan = await ScanAsync(factory, client);

        scan.Scan.Status.Should().Be("done");
        scan.Summary.Should().BeEquivalentTo(new
        {
            Discovered = 4,
            Ready = 3,
            AlreadyLinked = 0,
            NeedsReview = 0,
            Unusable = 1,
            UnsupportedFiles = 1,
            MissingBooks = 0,
            Truncated = false,
        });
        scan.UnsupportedSamples.Should().Equal("Other/Album.flac");
        scan.Items.Select(i => (i.Key, i.Status, i.Title, i.Author, i.FileCount)).Should().BeEquivalentTo(
        [
            ("tracks:Jane Author/A Novel", "ready", "A Novel", "Jane Author", 2),
            ("file:Ebooks/Essay.epub", "ready", "An Essay", "Sam Writer", 1),
            ("file:Ebooks/Untitled Paper.pdf", "ready", "Untitled Paper", (string?)null, 1),
            ("file:Ebooks/Damaged.epub", "unusable", "Damaged", (string?)null, 1),
        ]);

        before.Should().OnlyContain(snapshot => snapshot.IsUnchanged());
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Should().HaveCount(before.Count);
        await using var db = await Contexts(factory).CreateDbContextAsync();
        (await db.Books.CountAsync()).Should().Be(0);
        (await db.ExternalMediaLinks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_scan_never_reveals_where_the_folder_is_on_the_server()
    {
        Write("Ebooks/Essay.epub", SyntheticMedia.Epub("An Essay", "Sam Writer"));
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        await ScanAsync(factory, client);

        var status = await client.GetStringAsync(ExternalMediaEndpoints.Route);
        var scan = await client.GetStringAsync(ExternalMediaEndpoints.Route + "/scan");

        (status + scan).Should().NotContain(_temp);
    }

    [Fact]
    public async Task Linked_books_are_recognised_and_books_whose_files_are_gone_are_reported_missing()
    {
        Write("Ebooks/Essay.epub", SyntheticMedia.Epub("An Essay", "Sam Writer"));
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        var linked = await AddLinkedBookAsync(factory, "An Essay", "Ebooks/Essay.epub");
        var gone = await AddLinkedBookAsync(factory, "Gone", "Ebooks/Gone.epub");

        var scan = await ScanAsync(factory, client);

        var item = scan.Items.Should().ContainSingle().Subject;
        item.Status.Should().Be("linked");
        item.LinkedBookId.Should().Be(linked);
        scan.Summary!.AlreadyLinked.Should().Be(1);
        scan.Missing.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { BookId = gone, Title = "Gone", MissingFiles = 1 });

        var status = await client.GetFromJsonAsync<ExternalMediaStatusResponse>(ExternalMediaEndpoints.Route);
        status!.LinkedBooks.Should().Be(2);
        status.Mounted.Should().BeTrue();
    }

    [Fact]
    public async Task A_title_already_in_the_library_needs_review_instead_of_being_merged()
    {
        Write("Ebooks/Essay.epub", SyntheticMedia.Epub("An Essay", "Sam Writer"));
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title = "An Essay",
            author = "Sam Writer",
        });
        created.EnsureSuccessStatusCode();

        var scan = await ScanAsync(factory, client);

        var item = scan.Items.Should().ContainSingle().Subject;
        item.Status.Should().Be("review");
        item.LibraryMatches.Should().ContainSingle().Which.Title.Should().Be("An Essay");
        item.Concerns.Should().ContainSingle().Which.Should().Contain("already in your library");
    }

    [Fact]
    public async Task New_files_in_a_folder_that_is_already_a_book_are_not_silently_added_to_it()
    {
        Write("Title/01.mp3", SyntheticMedia.Mp3(100));
        Write("Title/02.mp3", SyntheticMedia.Mp3(100));
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        await AddLinkedBookAsync(factory, "Title", "Title/01.mp3", trackNumber: 1);

        var scan = await ScanAsync(factory, client);

        var item = scan.Items.Should().ContainSingle().Subject;
        item.Status.Should().Be("unusable");
        item.Concerns.Should().ContainSingle().Which.Should().Contain("already belong to a linked book");
    }

    [Fact]
    public async Task Results_can_be_filtered_by_status_and_paged()
    {
        for (var i = 1; i <= 5; i++)
            Write($"Ebooks/Book {i}.pdf", SyntheticMedia.Pdf());
        Write("Ebooks/Damaged.epub", [0]);
        await using var factory = new Factory(_root);
        var client = factory.CreateClient();
        await ScanAsync(factory, client);

        var page = await client.GetFromJsonAsync<ExternalMediaScanResponse>(
            ExternalMediaEndpoints.Route + "/scan?status=ready&offset=2&limit=2");

        page!.MatchingItems.Should().Be(5);
        page.Items.Select(i => i.Title).Should().Equal("Book 3", "Book 4");
        page.Summary!.Discovered.Should().Be(6);
    }

    [Fact]
    public async Task Scanning_a_folder_that_is_configured_but_not_mounted_is_refused()
    {
        await using var factory = new Factory(Path.Combine(_temp, "not-mounted"));
        var client = factory.CreateClient();

        var response = await client.PostAsync(ExternalMediaEndpoints.Route + "/scan", null);
        var status = await client.GetFromJsonAsync<ExternalMediaStatusResponse>(ExternalMediaEndpoints.Route);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        status!.Mounted.Should().BeFalse();
    }

    [Theory]
    [InlineData("GET", "/scan")]
    [InlineData("POST", "/scan")]
    [InlineData("GET", "/import")]
    [InlineData("POST", "/import")]
    public async Task Scanning_and_importing_answer_not_found_on_a_server_with_no_folder_configured(string method, string path)
    {
        await using var factory = new LibraryEndpointFactory();
        var client = factory.CreateClient();

        using var response = await client.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), ExternalMediaEndpoints.Route + path));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("external_media_not_configured");
    }

    private void Write(string relativePath, byte[] content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static IDbContextFactory<NostosDbContext> Contexts(LibraryEndpointFactory factory) =>
        factory.Services.GetRequiredService<IDbContextFactory<NostosDbContext>>();

    private static async Task<ExternalMediaScanResponse> ScanAsync(LibraryEndpointFactory factory, HttpClient client)
    {
        var started = await client.PostAsync(ExternalMediaEndpoints.Route + "/scan", null);
        started.StatusCode.Should().Be(HttpStatusCode.Accepted);
        await factory.Services.GetRequiredService<ExternalMediaScanService>().Completion
            .WaitAsync(TimeSpan.FromSeconds(30));

        return (await client.GetFromJsonAsync<ExternalMediaScanResponse>(ExternalMediaEndpoints.Route + "/scan"))!;
    }

    private static async Task<Guid> AddLinkedBookAsync(
        LibraryEndpointFactory factory,
        string title,
        string relativePath,
        int trackNumber = ExternalMediaLinkModel.PrimaryFile)
    {
        await using var db = await Contexts(factory).CreateDbContextAsync();
        var book = new EBookModel { Title = title };
        db.Books.Add(book);
        db.ExternalMediaLinks.Add(new ExternalMediaLinkModel
        {
            BookId = book.Id,
            TrackNumber = trackNumber,
            RootId = ExternalMediaOptions.DefaultRootId,
            RelativePath = relativePath,
        });
        await db.SaveChangesAsync();
        return book.Id;
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
