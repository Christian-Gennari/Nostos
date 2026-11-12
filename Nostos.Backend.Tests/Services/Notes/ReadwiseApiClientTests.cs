using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// The Readwise API client (issue #1525): token validation, cursor paging,
/// updatedAfter only on the first page, and Retry-After on 429. HTTP is
/// intercepted; no test talks to Readwise.
/// </summary>
public sealed class ReadwiseApiClientTests
{
    private const string Token = "test-token-not-a-secret";

    [Fact]
    public async Task A_204_from_the_auth_endpoint_validates_the_token()
    {
        var handler = new StubHttpMessageHandler().SetFallback(
            _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = Client(handler);

        await client.ValidateTokenAsync(Token);

        handler.RecordedRequestPaths.Should().Equal("/api/v2/auth/");
        handler.RecordedRequests.Single().Headers.Authorization!.ToString()
            .Should().Be($"Token {Token}");
    }

    [Fact]
    public async Task A_401_from_the_auth_endpoint_is_an_invalid_token()
    {
        var handler = new StubHttpMessageHandler().SetFallback(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = Client(handler);

        var act = () => client.ValidateTokenAsync(Token);

        await act.Should().ThrowAsync<ReadwiseInvalidTokenException>();
    }

    [Fact]
    public async Task Export_pages_with_the_cursor_and_sends_updatedAfter()
    {
        var handler = new StubHttpMessageHandler().SetFallback(request =>
        {
            var query = request.RequestUri!.Query;
            return query.Contains("pageCursor=", StringComparison.Ordinal)
                ? Json(Page("Second", cursor: null))
                : Json(Page("First", cursor: "cursor-2"));
        });
        var client = Client(handler);

        var export = await client.ExportAsync(Token, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        export.Books.Select(book => book.Title).Should().Equal("First", "Second");
        handler.RecordedRequests.Should().HaveCount(2);

        var first = handler.RecordedRequests.Single(
            request => !request.RequestUri!.Query.Contains("pageCursor=", StringComparison.Ordinal)).RequestUri!;
        first.Query.Should().Contain("updatedAfter=2024-01-01T00%3A00%3A00Z");
        var second = handler.RecordedRequests.Single(
            request => request.RequestUri!.Query.Contains("pageCursor=", StringComparison.Ordinal)).RequestUri!;
        second.Query.Should().Contain("pageCursor=cursor-2");
        // Readwise's own examples pass updatedAfter alongside the cursor.
        second.Query.Should().Contain("updatedAfter=2024-01-01T00%3A00%3A00Z");
        handler.RecordedRequests.Should().OnlyContain(
            request => request.RequestUri!.ToString().Contains(Token) == false,
            "the token must stay in the Authorization header");
    }

    [Fact]
    public async Task A_429_is_retried_after_the_retry_after_delay()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var handler = new StubHttpMessageHandler().SetFallback(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return limited;
            }

            return Json(Page("After a wait", cursor: null));
        });
        var client = Client(handler, delays.Add);

        var export = await client.ExportAsync(Token, updatedAfter: null);

        export.Books.Single().Title.Should().Be("After a wait");
        attempts.Should().Be(2);
        delays.Should().Equal(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_429_that_never_clears_is_reported_with_its_retry_after()
    {
        var handler = new StubHttpMessageHandler().SetFallback(_ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return limited;
        });
        var client = Client(handler, _ => { });

        var act = () => client.ExportAsync(Token, updatedAfter: null);

        var exception = await act.Should().ThrowAsync<ReadwiseRateLimitedException>();
        exception.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_non_json_success_body_is_a_format_failure_not_a_crash()
    {
        var handler = new StubHttpMessageHandler().SetFallback(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>not json</html>", System.Text.Encoding.UTF8, "text/html"),
            });
        var client = Client(handler);

        var act = () => client.ExportAsync(Token, updatedAfter: null);

        var exception = await act.Should().ThrowAsync<ReadwiseFormatException>();
        exception.Which.Message.Should().Contain("$");
        exception.Which.Message.Should().NotContain("html");
    }

    [Fact]
    public async Task A_401_during_export_is_an_invalid_token()
    {
        var handler = new StubHttpMessageHandler().SetFallback(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = Client(handler);

        var act = () => client.ExportAsync(Token, updatedAfter: null);

        await act.Should().ThrowAsync<ReadwiseInvalidTokenException>();
    }

    private static ReadwiseApiClient Client(
        StubHttpMessageHandler handler,
        Action<TimeSpan>? onDelay = null) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(new ReadwiseOptions
            {
                BaseUrl = "https://readwise.test",
                MaxRateLimitRetries = 2,
            }),
            NullLogger<ReadwiseApiClient>.Instance,
            delay: (value, _) =>
            {
                onDelay?.Invoke(value);
                return Task.CompletedTask;
            });

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private static string Page(string title, string? cursor) =>
        $$"""
        {
          "count": 1,
          "nextPageCursor": {{(cursor is null ? "null" : $"\"{cursor}\"")}},
          "results": [{
            "user_book_id": 1,
            "is_deleted": false,
            "title": "{{title}}",
            "author": "An Author",
            "category": "books",
            "asin": null,
            "highlights": [{
              "is_deleted": false,
              "is_discard": false,
              "text": "A line.",
              "note": null,
              "location": 1,
              "location_type": "page",
              "highlighted_at": null
            }]
          }]
        }
        """;
}
