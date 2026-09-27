using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Composition;
using Xunit;

namespace Nostos.Backend.Tests.Services;

public sealed class BookLookupServiceTests
{
    private const string Isbn = "9780141183848";

    [Fact]
    public async Task OpenLibrary_success_survives_GoogleBooks_failure()
    {
        var handler = Handler(
            _ => Json(OpenLibraryMatch),
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var outcome = await Service(handler).LookupCombinedDetailedAsync(Isbn);

        outcome.Metadata.Should().NotBeNull();
        outcome.Metadata!.Title.Should().Be("Fictions");
        outcome.Failed.Should().BeTrue();
    }


    [Fact]
    public async Task OpenLibrary_empty_legacy_result_falls_back_to_canonical_isbn_endpoint()
    {
        var handler = Handler(
            request =>
                request.RequestUri?.AbsolutePath switch
                {
                    "/api/books" => Json("{}"),
                    var path when path == $"/isbn/{Isbn}.json" => Json(OpenLibraryCanonicalMatch),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                },
            _ => Json("""{"items":[]}"""));

        var outcome = await Service(handler).LookupCombinedDetailedAsync(Isbn);

        outcome.Metadata.Should().NotBeNull();
        outcome.Metadata!.Title.Should().Be("Fictions (canonical)");
        outcome.Metadata.Author.Should().Be("Jorge Luis Borges");
        outcome.Metadata.Publisher.Should().Be("Penguin");
        outcome.Metadata.PlaceOfPublication.Should().Be("London");
        outcome.Metadata.PublishedDate.Should().Be("2000");
        outcome.Metadata.PageCount.Should().Be(176);
        outcome.Metadata.Edition.Should().Be("Penguin Classics");
        outcome.Metadata.Language.Should().Be("eng");
        outcome.Metadata.Categories.Should().Be("Fiction, Short stories");
        outcome.Failed.Should().BeFalse();
        handler.RecordedRequestPaths.Should().Contain($"/isbn/{Isbn}.json");
    }

    [Fact]
    public async Task OpenLibrary_legacy_404_falls_back_to_canonical_isbn_endpoint()
    {
        var handler = Handler(
            request =>
                request.RequestUri?.AbsolutePath switch
                {
                    "/api/books" => new HttpResponseMessage(HttpStatusCode.NotFound),
                    var path when path == $"/isbn/{Isbn}.json" => Json(OpenLibraryCanonicalKeyOnlyMatch),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                },
            _ => Json("""{"items":[]}"""));

        var outcome = await Service(handler).LookupCombinedDetailedAsync(Isbn);

        outcome.Metadata.Should().NotBeNull();
        outcome.Metadata!.Title.Should().Be("Fictions (canonical key)");
        outcome.Metadata.Author.Should().Be("/authors/OL13640A");
        outcome.Failed.Should().BeFalse();
        handler.RecordedRequestPaths.Should().Contain($"/isbn/{Isbn}.json");
    }

    [Fact]
    public async Task OpenLibrary_legacy_rate_limit_is_not_downgraded_to_not_found()
    {
        var handler = Handler(
            request =>
                request.RequestUri?.AbsolutePath switch
                {
                    "/api/books" => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
                    var path when path == $"/isbn/{Isbn}.json" =>
                        new HttpResponseMessage(HttpStatusCode.NotFound),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                },
            _ => Json("""{"items":[]}"""));

        var outcome = await Service(handler).LookupCombinedDetailedAsync(Isbn);

        outcome.Metadata.Should().BeNull();
        outcome.Failed.Should().BeTrue();
        handler.RecordedRequestPaths.Should().Contain($"/isbn/{Isbn}.json");
    }

    [Fact]
    public async Task OpenLibrary_404_on_both_endpoints_is_a_genuine_not_found()
    {
        var handler = Handler(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => Json("""{"items":[]}"""));

        var outcome = await Service(handler).LookupCombinedDetailedAsync(Isbn);

        outcome.Metadata.Should().BeNull();
        outcome.Failed.Should().BeFalse();
    }

    [Fact]
    public async Task GoogleBooks_success_survives_OpenLibrary_failure()
    {
        var handler = Handler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Json(GoogleBooksMatch));

        var outcome = await Service(handler).LookupCombinedDetailedAsync(Isbn);

        outcome.Metadata.Should().NotBeNull();
        outcome.Metadata!.Title.Should().Be("Fictions from Google");
        outcome.Failed.Should().BeTrue();
    }

    [Fact]
    public async Task Both_working_providers_with_no_match_is_not_a_failure()
    {
        var handler = Handler(
            _ => Json("{}"),
            _ => Json("""{"items":[]}"""));

        var outcome = await Service(handler).LookupCombinedDetailedAsync(Isbn);

        outcome.Metadata.Should().BeNull();
        outcome.Failed.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Provider_http_failure_is_recorded(HttpStatusCode statusCode)
    {
        var handler = Handler(
            _ => Json("{}"),
            _ => new HttpResponseMessage(statusCode));

        var outcome = await Service(handler).LookupCombinedDetailedAsync(Isbn);

        outcome.Metadata.Should().BeNull();
        outcome.Failed.Should().BeTrue();
    }

    [Fact]
    public async Task Provider_timeout_is_recorded_without_hiding_working_sibling()
    {
        var handler = Handler(
            _ => Json("{}"),
            _ => throw new TaskCanceledException("Simulated provider timeout."));

        var outcome = await Service(handler).LookupCombinedDetailedAsync(Isbn);

        outcome.Metadata.Should().BeNull();
        outcome.Failed.Should().BeTrue();
    }

    [Fact]
    public void Named_client_identifies_Nostos()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNostosProduct(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(BookLookupService.HttpClientName);

        client.Timeout.Should().Be(TimeSpan.FromSeconds(15));
        client.DefaultRequestHeaders.UserAgent.ToString().Should().Contain("Nostos/1.0");
        client.DefaultRequestHeaders.UserAgent.ToString()
            .Should().Contain("github.com/Christian-Gennari/Nostos-Rebirth");
    }

    private static BookLookupService Service(StubHttpMessageHandler handler) =>
        new(
            new StubHttpClientFactory(handler),
            NullLogger<BookLookupService>.Instance);

    private static StubHttpMessageHandler Handler(
        Func<HttpRequestMessage, HttpResponseMessage> openLibrary,
        Func<HttpRequestMessage, HttpResponseMessage> googleBooks) =>
        new StubHttpMessageHandler().SetFallback(request =>
            request.RequestUri?.Host switch
            {
                "openlibrary.org" => openLibrary(request),
                "www.googleapis.com" => googleBooks(request),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private const string OpenLibraryMatch =
        """
        {
          "ISBN:9780141183848": {
            "title": "Fictions",
            "authors": [{ "name": "Jorge Luis Borges" }],
            "publishers": [{ "name": "Penguin" }],
            "publish_places": [{ "name": "London" }],
            "publish_date": "2000",
            "number_of_pages": 176
          }
        }
        """;

    private const string OpenLibraryCanonicalMatch =
        """
        {
          "title": "Fictions (canonical)",
          "authors": [{ "key": "/authors/OL13640A", "name": "Jorge Luis Borges" }],
          "publishers": ["Penguin"],
          "publish_places": ["London"],
          "publish_date": "2000",
          "number_of_pages": 176,
          "edition_name": "Penguin Classics",
          "languages": [{ "key": "/languages/eng" }],
          "subjects": ["Fiction", "Short stories"]
        }
        """;

    private const string OpenLibraryCanonicalKeyOnlyMatch =
        """
        {
          "title": "Fictions (canonical key)",
          "authors": [{ "key": "/authors/OL13640A" }],
          "publishers": ["Penguin"],
          "publish_date": "2000",
          "number_of_pages": 176
        }
        """;

    private const string GoogleBooksMatch =
        """
        {
          "items": [{
            "volumeInfo": {
              "title": "Fictions from Google",
              "authors": ["Jorge Luis Borges"],
              "publisher": "Penguin",
              "publishedDate": "2000",
              "pageCount": 176,
              "language": "en",
              "categories": ["Fiction"]
            }
          }]
        }
        """;
}
