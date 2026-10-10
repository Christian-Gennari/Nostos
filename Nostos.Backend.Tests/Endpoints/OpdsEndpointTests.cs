using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

// Coverage for Nostos's own OPDS export (issue #186). The Gutenberg fixtures
// under Tests/Fixtures cover Nostos CONSUMING an external OPDS feed; nothing
// here overlaps with them — these exercise the feed Nostos PRODUCES, which had
// no test at all before.
//
// Two defects in particular are pinned here because they were live, not
// theoretical: every audiobook was advertised as application/octet-stream and
// every cover as image/png.
public sealed class OpdsEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Opds = "http://opds-spec.org/2010/catalog";
    private static readonly XNamespace Dc = "http://purl.org/dc/terms/";

    private const string AcquisitionFeedType =
        "application/atom+xml;profile=opds-catalog;kind=acquisition";
    private const string ImageRel = "http://opds-spec.org/image";
    private const string ThumbnailRel = "http://opds-spec.org/image/thumbnail";
    private const string AcquisitionRel = "http://opds-spec.org/acquisition";

    private readonly LibraryEndpointFactory _factory;

    public OpdsEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    // ------------------------------------------------------------------
    // Feed semantics
    // ------------------------------------------------------------------

    [Fact]
    public async Task Feed_is_a_valid_acquisition_catalog_that_parses_as_atom()
    {
        var response = await Client.GetAsync("/opds/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var contentType = response.Content.Headers.ContentType!;
        contentType.MediaType.Should().Be("application/atom+xml");
        contentType.ToString().Should().Contain("profile=opds-catalog");
        contentType.ToString().Should().Contain("kind=acquisition");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().StartWith("<?xml", "the document must carry its XML declaration");

        var doc = XDocument.Parse(body);
        doc.Root!.Name.Should().Be(Atom + "feed");
        doc.Root.Attribute(XNamespace.Xmlns + "opds").Should().NotBeNull();
        doc.Root.Attribute(XNamespace.Xmlns + "dc").Should().NotBeNull();

        doc.Root.Element(Atom + "id").Should().NotBeNull();
        doc.Root.Element(Atom + "title")!.Value.Should().Be("Nostos Library");
        doc.Root.Element(Atom + "updated").Should().NotBeNull();
        doc.Root.Element(Atom + "author").Should().NotBeNull();

        // The feed's entries are publications with acquisition links, so it is
        // an acquisition feed and must say so. It used to claim
        // kind=navigation, which means "a feed of links to other feeds".
        foreach (var rel in new[] { "self", "start" })
        {
            var link = doc.Root.Elements(Atom + "link").Single(l => (string?)l.Attribute("rel") == rel);
            ((string?)link.Attribute("type")).Should().Be(AcquisitionFeedType, $"rel={rel}");
        }
    }

    [Fact]
    public async Task Feed_declaration_matches_the_encoding_it_is_served_as()
    {
        var response = await Client.GetAsync("/opds/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert on the WIRE BYTES, not on the decoded string. A string has no
        // encoding left to disagree with, so the string-based assertions above
        // stayed green while the served feed declared encoding="utf-16" over
        // UTF-8 bytes — and a strict parser over the raw response refuses that
        // document outright, which is what every OPDS client that reads the
        // response as bytes sees.
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var charset = response.Content.Headers.ContentType!.CharSet;

        charset.Should().Be("utf-8");
        Encoding.UTF8
            .GetString(bytes)
            .Should()
            .StartWith($"<?xml version=\"1.0\" encoding=\"{charset}\"?>");

        // XDocument.Load over the byte stream applies the declared encoding, so
        // it throws when the declaration lies.
        using var stream = new MemoryStream(bytes);
        XDocument.Load(stream).Root!.Name.Should().Be(Atom + "feed");
    }

    [Fact]
    public async Task Feed_updated_is_an_rfc3339_timestamp_with_an_explicit_utc_offset()
    {
        var doc = await GetFeedAsync();

        var updated = doc.Root!.Element(Atom + "updated")!.Value;
        updated.Should().EndWith("Z");
        DateTimeOffset.TryParse(updated, out _).Should().BeTrue($"'{updated}' must be a date-time");
    }

    // ------------------------------------------------------------------
    // Media types (issue #186 §2) — the accepted set, end to end
    // ------------------------------------------------------------------

    [Theory]
    // Text formats
    [InlineData("ebook", "book.epub", "application/epub+zip")]
    [InlineData("ebook", "book.pdf", "application/pdf")]
    [InlineData("ebook", "book.txt", "text/plain")]
    [InlineData("ebook", "book.mobi", "application/x-mobipocket-ebook")]
    [InlineData("ebook", "book.azw3", "application/x-mobipocket-ebook")]
    // Audiobooks — every one of these was application/octet-stream before
    [InlineData("audiobook", "book.mp3", "audio/mpeg")]
    [InlineData("audiobook", "book.m4a", "audio/mp4")]
    [InlineData("audiobook", "book.m4b", "audio/mp4")]
    // The extension is stored lowercased, but the map must not depend on case.
    // A different base name, because POST /api/books matches on the normalized
    // title (case and punctuation insensitive) — two rows named "…book.m4b"
    // would be the same book to it.
    [InlineData("audiobook", "UPPER.M4B", "audio/mp4")]
    public async Task Acquisition_link_advertises_the_real_media_type(
        string type,
        string fileName,
        string expectedMediaType
    )
    {
        var id = await SeedFileBackedBookAsync(type, $"MediaType {fileName}", fileName);

        var entry = await GetEntryAsync(id);

        var acquisition = entry.Elements(Atom + "link")
            .Single(l => (string?)l.Attribute("rel") == AcquisitionRel);
        ((string?)acquisition.Attribute("type")).Should().Be(expectedMediaType);
    }

    // ------------------------------------------------------------------
    // Cover metadata (issue #186 §3)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("cover.jpg", "image/jpeg")]
    [InlineData("cover.jpeg", "image/jpeg")]
    [InlineData("cover.png", "image/png")]
    public async Task Cover_links_advertise_the_media_type_the_stored_file_has(
        string coverFileName,
        string expectedMediaType
    )
    {
        var id = await SeedFileBackedBookAsync(
            "ebook",
            $"Cover {coverFileName}",
            "book.epub",
            coverFileName: coverFileName
        );

        var entry = await GetEntryAsync(id);

        foreach (var rel in new[] { ImageRel, ThumbnailRel })
        {
            var link = entry.Elements(Atom + "link").Single(l => (string?)l.Attribute("rel") == rel);
            ((string?)link.Attribute("type")).Should().Be(expectedMediaType, $"rel={rel}");
            ((string?)link.Attribute("href"))
                .Should()
                .Be($"http://localhost/opds/books/{id}/cover", $"rel={rel}");
        }
    }

    [Fact]
    public async Task Book_without_a_cover_exported_no_cover_links()
    {
        var id = await SeedFileBackedBookAsync("ebook", "NoCover Book", "book.epub");

        var entry = await GetEntryAsync(id);

        entry.Elements(Atom + "link")
            .Should()
            .OnlyContain(l => (string?)l.Attribute("rel") == AcquisitionRel);
    }

    // ------------------------------------------------------------------
    // Publication metadata (issue #186 §4)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Language_is_exported_as_dublin_core_and_never_as_an_xml_lang_element()
    {
        var id = await SeedFileBackedBookAsync(
            "ebook",
            "Language Book",
            "book.epub",
            language: "English"
        );

        var doc = await GetFeedAsync();
        var entry = EntryFor(doc, id);

        entry.Element(Dc + "language")!.Value.Should().Be("English");

        // The old serialization emitted `<xml:lang>` — an element in the xml
        // namespace. xml:lang is an attribute, so no client ever read it.
        doc.Descendants()
            .Where(e => e.Name == XNamespace.Xml + "lang")
            .Should()
            .BeEmpty("xml:lang is an attribute, not an element");
    }

    [Fact]
    public async Task Known_identifiers_are_exported_as_urns()
    {
        var id = await SeedFileBackedBookAsync(
            "ebook",
            "Identifier Book",
            "book.epub",
            isbn: "9780141183848"
        );

        var entry = await GetEntryAsync(id);

        entry.Element(Dc + "identifier")!.Value.Should().Be("urn:isbn:9780141183848");
    }

    [Fact]
    public async Task Entry_with_no_optional_metadata_still_serializes_as_a_usable_entry()
    {
        var id = await SeedFileBackedBookAsync(
            "ebook",
            "Minimal Book",
            "book.epub",
            withAuthor: false
        );

        var entry = await GetEntryAsync(id);

        entry.Element(Atom + "title")!.Value.Should().Be("Minimal Book");
        entry.Element(Atom + "author").Should().BeNull();
        entry.Element(Atom + "content").Should().BeNull();
        entry.Element(Dc + "language").Should().BeNull();
        entry.Elements(Atom + "link")
            .Single(l => (string?)l.Attribute("rel") == AcquisitionRel)
            .Should()
            .NotBeNull();
    }

    // ------------------------------------------------------------------
    // Eligibility and URLs
    // ------------------------------------------------------------------

    [Fact]
    public async Task Only_books_that_have_a_file_are_exported()
    {
        var withFile = await SeedFileBackedBookAsync("ebook", "HasFile Book", "book.epub");
        var withoutFile = await CreateBookAsync("ebook", "NoFile Book");

        var doc = await GetFeedAsync();

        EntryFor(doc, withFile);

        doc.Root!.Elements(Atom + "entry")
            .Select(e => (string?)e.Element(Atom + "id"))
            .Should()
            .NotContain($"urn:uuid:{withoutFile}");
    }

    [Fact]
    public async Task Acquisition_link_is_absolute_and_points_at_the_book_file_endpoint()
    {
        var id = await SeedFileBackedBookAsync("ebook", "Url Book", "book.epub");

        var entry = await GetEntryAsync(id);

        var href = (string?)entry.Elements(Atom + "link")
            .Single(l => (string?)l.Attribute("rel") == AcquisitionRel)
            .Attribute("href");

        href.Should().Be($"http://localhost/opds/books/{id}/file");
    }

    [Fact]
    public async Task Info_reports_the_catalog_url_a_reader_should_use()
    {
        var response = await Client.GetAsync("/api/opds/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var info = (await response.Content.ReadFromJsonAsync<OpdsInfoDto>())!;

        info.Enabled.Should().BeTrue();
        info.CatalogUrl.Should().Be("http://localhost/opds/");
        info.UrlSource.Should().Be("request");
        info.LocalOnly.Should().BeTrue("the test host is addressed as localhost");
    }

    [Fact]
    public async Task Host_supplied_authorization_policy_is_scoped_to_the_opds_surface()
    {
        await using var app = MapForMetadata("HostedOpds");

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        foreach (var route in new[]
        {
            "/opds/",
            "/opds/books/{id:guid}/file",
            "/opds/books/{id:guid}/cover",
        })
        {
            var endpoint = endpoints.Single(candidate => candidate.RoutePattern.RawText == route);
            endpoint.Metadata
                .GetOrderedMetadata<IAuthorizeData>()
                .Should()
                .Contain(data => data.Policy == "HostedOpds", route);
        }

        var info = endpoints.Single(candidate =>
            candidate.RoutePattern.RawText == "/api/opds/info");
        info.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Should()
            .NotContain(data => data.Policy == "HostedOpds",
                "Settings stays on the host's ordinary application authorization boundary");
    }

    [Fact]
    public async Task Default_mapping_adds_no_opds_specific_authorization_metadata()
    {
        await using var app = MapForMetadata(authorizationPolicy: null);

        var opdsEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/opds", StringComparison.Ordinal) == true)
            .ToList();

        opdsEndpoints.Should().HaveCount(6);
        opdsEndpoints.Should().OnlyContain(endpoint =>
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0);
    }

    [Fact]
    public async Task Opds_file_route_preserves_byte_range_delivery()
    {
        using var factory = new OpdsMediaFactory();
        var bytes = Encoding.ASCII.GetBytes("0123456789");
        var bookId = Guid.NewGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IBookAssetStorage>();
            await using var content = new MemoryStream(bytes, writable: false);
            await storage.SaveBookFileAsync(bookId, content, "range.epub");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/opds/books/{bookId}/file");
        request.Headers.Range = new RangeHeaderValue(2, 5);

        using var response = await factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Headers.AcceptRanges.Should().Contain("bytes");
        response.Content.Headers.ContentRange!.ToString().Should().Be("bytes 2-5/10");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(bytes[2..6]);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static WebApplication MapForMetadata(string? authorizationPolicy)
    {
        var builder = WebApplication.CreateBuilder();
        // Minimal API endpoint inference must know these handler parameters are
        // services even though metadata tests never execute the handlers.
        builder.Services.AddSingleton<IBookRepository>(_ => null!);
        builder.Services.AddSingleton<IBookAssetStorage>(_ => null!);

        var app = builder.Build();
        app.MapOpdsEndpoints(
            new OpdsOptions { Enabled = true },
            authorizationPolicy: authorizationPolicy);
        return app;
    }

    private async Task<XDocument> GetFeedAsync(string path = "/opds/")
    {
        var response = await Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path}");
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<XElement> GetEntryAsync(Guid bookId) => EntryFor(await GetFeedAsync(), bookId);

    private static XElement EntryFor(XDocument doc, Guid bookId) =>
        doc.Root!.Elements(Atom + "entry")
            .Single(e => (string?)e.Element(Atom + "id") == $"urn:uuid:{bookId}");

    private async Task<Guid> CreateBookAsync(string type, string title)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/books",
            new
            {
                type,
                title,
                forceCreate = true,
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!.Id;
    }

    /// <summary>
    /// Creates a real book through the API, then marks it file-backed in the
    /// database. The stored file itself is never written: the feed is built
    /// entirely from the row, so writing gigabytes of media into the shared
    /// Storage directory to test it would be both slow and unsafe.
    ///
    /// Titles must be unique across this class: POST /api/books is
    /// create-or-match on the normalized title+author, and a repeated title
    /// returns the existing book (200) instead of creating a second one.
    /// </summary>
    private async Task<Guid> SeedFileBackedBookAsync(
        string type,
        string title,
        string fileName,
        string? coverFileName = null,
        string? language = null,
        string? description = null,
        string? isbn = null,
        bool withAuthor = true,
        DateTime? createdAt = null
    )
    {
        var response = await Client.PostAsJsonAsync(
            "/api/books",
            new
            {
                type,
                title,
                author = withAuthor ? "Test Author" : null,
                description,
                language,
                isbn,
                forceCreate = true,
            }
        );
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var book = (await response.Content.ReadFromJsonAsync<BookDto>())!;

        await using var db = await OpenDbAsync();
        var stored = await db.Books.SingleAsync(b => b.Id == book.Id);
        stored.FileDetails.HasFile = true;
        stored.FileDetails.FileName = fileName;
        stored.FileDetails.CoverFileName = coverFileName;
        if (createdAt is not null)
            stored.CreatedAt = createdAt.Value;

        await db.SaveChangesAsync();

        return book.Id;
    }

    private async Task<NostosDbContext> OpenDbAsync()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_factory.DatabasePath}")
            .Options;
        var db = new NostosDbContext(options);
        await db.Database.OpenConnectionAsync();
        return db;
    }
}

// A page size far below the number of books the tests seed, so pagination is
// exercised without seeding hundreds of rows.
public sealed class OpdsMediaFactory : LibraryEndpointFactory
{
    private readonly string _booksRoot =
        Path.Combine(Path.GetTempPath(), $"nostos-opds-media-tests-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Storage:BooksRoot", _booksRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        try
        {
            if (Directory.Exists(_booksRoot))
                Directory.Delete(_booksRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
    }
}

public sealed class OpdsPaginationFactory : LibraryEndpointFactory
{
    public const int PageSize = 3;
    public const string PublicBaseUrl = "https://reader.example.test";

    // UseSetting (host configuration), not ConfigureAppConfiguration: with the
    // minimal hosting model those callbacks run AFTER the entry point has
    // already read `builder.Configuration`, so a value set that way never
    // reaches a top-level `Program.cs` read. UseSetting does.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Opds:PageSize", PageSize.ToString());
        builder.UseSetting("Opds:PublicBaseUrl", PublicBaseUrl);
    }
}

public sealed class OpdsPaginationTests : IClassFixture<OpdsPaginationFactory>
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    private readonly OpdsPaginationFactory _factory;

    public OpdsPaginationTests(OpdsPaginationFactory factory) => _factory = factory;

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Following_the_next_links_visits_every_downloadable_book_once()
    {
        // Distinct creation times so the expected order is unambiguous.
        var start = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 5; i++)
            await SeedAsync($"Page Book {i}", start.AddMinutes(i));

        var expected = await FileBackedIdsAsync();

        var visited = new List<Guid>();
        var path = "/opds/";
        var pages = 0;

        while (path is not null)
        {
            var response = await Client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc = XDocument.Parse(await response.Content.ReadAsStringAsync());

            var entries = doc.Root!.Elements(Atom + "entry").ToList();
            entries.Count.Should().BeLessThanOrEqualTo(OpdsPaginationFactory.PageSize);

            visited.AddRange(
                entries.Select(e =>
                    Guid.Parse(((string)e.Element(Atom + "id")!).Replace("urn:uuid:", ""))
                )
            );

            var next = doc.Root.Elements(Atom + "link")
                .SingleOrDefault(l => (string?)l.Attribute("rel") == "next");
            path = (string?)next?.Attribute("href");

            pages++;
            pages.Should().BeLessThan(50, "the next links must terminate");
        }

        expected.Count.Should().BeGreaterThan(OpdsPaginationFactory.PageSize);
        visited.Should().Equal(expected, "every page must continue where the last one stopped");
        visited.Should().HaveCount(expected.Count);
        visited.Distinct().Should().HaveCount(expected.Count, "no book may appear twice");
    }

    [Fact]
    public async Task First_page_advertises_the_collection_bounds_and_no_previous_link()
    {
        await SeedAsync("Bounds Book", DateTime.UtcNow);

        var doc = await GetFeedAsync("/opds/");
        var links = doc.Root!.Elements(Atom + "link").ToList();

        var hrefOf = new Func<string, string?>(rel =>
            (string?)links.SingleOrDefault(l => (string?)l.Attribute("rel") == rel)?.Attribute("href")
        );

        hrefOf("first").Should().Be($"{OpdsPaginationFactory.PublicBaseUrl}/opds/");
        hrefOf("last").Should().EndWith("/opds/?page=" + LastPageNumber(await FileBackedIdsAsync()));
        hrefOf("next").Should().Be($"{OpdsPaginationFactory.PublicBaseUrl}/opds/?page=2");
        hrefOf("previous").Should().BeNull("page 1 has nothing before it");
    }

    [Fact]
    public async Task Page_beyond_the_end_clamps_to_the_last_page_instead_of_returning_a_dead_link()
    {
        await SeedAsync("Clamp Book", DateTime.UtcNow);

        var expectedLastPage = LastPageNumber(await FileBackedIdsAsync());

        var doc = await GetFeedAsync("/opds/?page=9999");

        var self = (string?)doc.Root!.Elements(Atom + "link")
            .Single(l => (string?)l.Attribute("rel") == "self")
            .Attribute("href");

        self.Should().Be(ExpectedPageUrl(expectedLastPage));
        doc.Root.Elements(Atom + "link")
            .Should()
            .NotContain(l => (string?)l.Attribute("rel") == "next");
    }

    [Fact]
    public async Task Books_sharing_a_creation_timestamp_are_still_paged_exactly_once()
    {
        // Creation times are not unique — an import creates several books in
        // the same tick. Without a tiebreaker in the ordering, SQLite is free
        // to return tied rows in a different order per query, which would let
        // one book appear on two pages and another on none.
        var shared = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 5; i++)
            await SeedAsync($"Tied Book {i}", shared);

        var expected = await FileBackedIdsAsync();

        var visited = new List<Guid>();
        var path = "/opds/";
        while (path is not null)
        {
            var doc = await GetFeedAsync(path);
            visited.AddRange(
                doc.Root!.Elements(Atom + "entry")
                    .Select(e =>
                        Guid.Parse(((string)e.Element(Atom + "id")!).Replace("urn:uuid:", ""))
                    )
            );
            path = (string?)
                doc.Root.Elements(Atom + "link")
                    .SingleOrDefault(l => (string?)l.Attribute("rel") == "next")
                    ?.Attribute("href");
        }

        visited.Should().HaveCount(expected.Count);
        visited.Distinct().Should().HaveCount(expected.Count);
        visited.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Externally_visible_base_url_replaces_the_request_origin()
    {
        await SeedAsync("BaseUrl Book", DateTime.UtcNow);

        var doc = await GetFeedAsync("/opds/");

        foreach (var href in doc.Descendants(Atom + "link").Select(l => (string)l.Attribute("href")!))
            href.Should().StartWith(OpdsPaginationFactory.PublicBaseUrl + "/");
    }

    [Fact]
    public async Task Info_reports_the_configured_origin_as_the_source()
    {
        var response = await Client.GetAsync("/api/opds/info");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var info = (await response.Content.ReadFromJsonAsync<OpdsInfoDto>())!;

        info.Enabled.Should().BeTrue();
        info.UrlSource.Should().Be("configured");
        info.CatalogUrl.Should().Be($"{OpdsPaginationFactory.PublicBaseUrl}/opds/");
        info.LocalOnly.Should().BeFalse("a configured origin is taken at its word");
    }

    // ------------------------------------------------------------------

    private async Task<XDocument> GetFeedAsync(string path = "/opds/")
    {
        var response = await Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {path}");
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private string ExpectedPageUrl(int page) =>
        page <= 1
            ? $"{OpdsPaginationFactory.PublicBaseUrl}/opds/"
            : $"{OpdsPaginationFactory.PublicBaseUrl}/opds/?page={page}";

    private static int LastPageNumber(IReadOnlyCollection<Guid> ids) =>
        Math.Max(1, (ids.Count + OpdsPaginationFactory.PageSize - 1) / OpdsPaginationFactory.PageSize);

    private async Task<List<Guid>> FileBackedIdsAsync()
    {
        await using var db = await OpenDbAsync();
        return await db
            .Books.AsNoTracking()
            .Where(b => b.FileDetails.HasFile && b.FileDetails.FileName != null)
            .OrderByDescending(b => b.CreatedAt)
            .ThenBy(b => b.Id)
            .Select(b => b.Id)
            .ToListAsync();
    }

    private async Task SeedAsync(string title, DateTime createdAt)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/books",
            new
            {
                type = "ebook",
                title,
                author = "Test Author",
                forceCreate = true,
            }
        );
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var book = (await response.Content.ReadFromJsonAsync<BookDto>())!;

        await using var db = await OpenDbAsync();
        var stored = await db.Books.SingleAsync(b => b.Id == book.Id);
        stored.FileDetails.HasFile = true;
        stored.FileDetails.FileName = "book.epub";
        stored.CreatedAt = createdAt;
        await db.SaveChangesAsync();
    }

    private async Task<NostosDbContext> OpenDbAsync()
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={_factory.DatabasePath}")
            .Options;
        var db = new NostosDbContext(options);
        await db.Database.OpenConnectionAsync();
        return db;
    }
}

public sealed class OpdsDisabledFactory : LibraryEndpointFactory
{
    // UseSetting, for the same reason as OpdsPaginationFactory.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Opds:Enabled", "false");
    }
}

public sealed class OpdsDisabledTests : IClassFixture<OpdsDisabledFactory>
{
    private readonly OpdsDisabledFactory _factory;

    public OpdsDisabledTests(OpdsDisabledFactory factory) => _factory = factory;

    [Fact]
    public async Task Disabled_export_maps_no_route_and_never_answers_with_the_spa_shell()
    {
        var response = await _factory.CreateClient().GetAsync("/opds/");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
    }

    [Fact]
    public async Task Disabled_export_still_reports_itself_as_disabled_with_no_url()
    {
        // Settings has to be able to say "switched off" rather than guess at a
        // broken server, so this endpoint is mapped either way.
        var response = await _factory.CreateClient().GetAsync("/api/opds/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var info = (await response.Content.ReadFromJsonAsync<OpdsInfoDto>())!;

        info.Enabled.Should().BeFalse();
        info.CatalogUrl.Should().BeNull();
    }
}
