using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// The library change feed is what lets an open screen show a change made
/// somewhere else — Ask Nostos, another tab — without a reload. These tests
/// drive it over real HTTP against the real save pipeline: the revision has to
/// move because <c>NostosDbContext</c> advanced it, not because the test did.
/// </summary>
public sealed partial class LibraryChangesEndpointTests : IClassFixture<LibraryEndpointFactory>
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly LibraryEndpointFactory _factory;

    public LibraryChangesEndpointTests(LibraryEndpointFactory factory) => _factory = factory;

    [Fact]
    public async Task Stream_announces_the_current_revision_first_with_proxy_safe_headers()
    {
        using var factory = FastFeed();
        using var client = factory.CreateClient();
        using var response = await OpenAsync(client);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
        response.Headers.GetValues("X-Accel-Buffering").Should().ContainSingle().Which.Should().Be("no");

        await using var feed = await Feed.OpenAsync(response);
        var revisions = await feed.ReadRevisionsAsync(count: 1, Budget);

        revisions.Should().ContainSingle().Which.Should().MatchRegex("^[0-9]+$");
        feed.Text.Should().Contain("retry: ");
    }

    [Fact]
    public async Task A_write_from_another_client_is_announced_with_a_larger_revision()
    {
        using var factory = FastFeed();
        using var watcher = factory.CreateClient();
        using var writer = factory.CreateClient();
        using var response = await OpenAsync(watcher);
        await using var feed = await Feed.OpenAsync(response);

        var before = (await feed.ReadRevisionsAsync(count: 1, Budget)).Single();

        var created = await writer.PostAsJsonAsync(
            "/api/collections",
            new CreateCollectionDto($"Feed {Guid.NewGuid():N}", null));
        created.EnsureSuccessStatusCode();

        var after = (await feed.ReadRevisionsAsync(count: 1, Budget)).Single();

        long.Parse(after).Should().BeGreaterThan(long.Parse(before));
    }

    [Fact]
    public async Task An_unchanged_library_is_announced_once_and_then_kept_alive()
    {
        using var factory = FastFeed(heartbeatSeconds: 1);
        using var client = factory.CreateClient();
        using var response = await OpenAsync(client);
        await using var feed = await Feed.OpenAsync(response);

        await feed.ReadUntilAsync(text => text.Contains(": ping"), TimeSpan.FromSeconds(6));

        // Polling continued for over a second without a write, and the feed
        // stayed quiet apart from the heartbeat: the revision is not re-sent.
        RevisionPattern().Matches(feed.Text).Should().HaveCount(1);
    }

    [Fact]
    public async Task Stream_ends_at_its_lifetime_so_the_client_reconnects()
    {
        using var factory = FastFeed(lifetimeSeconds: 1);
        using var client = factory.CreateClient();
        using var response = await OpenAsync(client);
        await using var feed = await Feed.OpenAsync(response);

        var ended = await feed.ReadToEndAsync(TimeSpan.FromSeconds(6));

        ended.Should().BeTrue("a bounded stream must close on its own");
        RevisionPattern().Matches(feed.Text).Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_write_wakes_the_feed_without_waiting_for_the_slow_re_read()
    {
        // The re-read is ten minutes away: only the commit signal can announce
        // this write inside the budget.
        using var factory = FastFeed(pollMilliseconds: 600_000);
        using var watcher = factory.CreateClient();
        using var writer = factory.CreateClient();
        using var response = await OpenAsync(watcher);
        await using var feed = await Feed.OpenAsync(response);
        var before = (await feed.ReadRevisionsAsync(count: 1, Budget)).Single();

        (await writer.PostAsJsonAsync("/api/collections", new CreateCollectionDto($"Signal {Guid.NewGuid():N}", null)))
            .EnsureSuccessStatusCode();

        var after = (await feed.ReadRevisionsAsync(count: 1, Budget)).Single();
        long.Parse(after).Should().BeGreaterThan(long.Parse(before));
        feed.Text.Should().NotContain("progressOnly", "a new collection is not a reading position");
    }

    [Fact]
    public async Task A_reading_position_save_is_announced_as_progress_only()
    {
        using var factory = FastFeed(pollMilliseconds: 600_000);
        using var watcher = factory.CreateClient();
        using var writer = factory.CreateClient();
        var created = await writer.PostAsJsonAsync("/api/books", new { type = "ebook", title = $"Feed Book {Guid.NewGuid():N}" });
        var book = (await created.Content.ReadFromJsonAsync<BookDto>())!;

        using var response = await OpenAsync(watcher);
        await using var feed = await Feed.OpenAsync(response);
        await feed.ReadRevisionsAsync(count: 1, Budget);

        (await writer.PutAsJsonAsync($"/api/books/{book.Id}/progress", new { location = "epubcfi(/6/4)", percentage = 12 }))
            .EnsureSuccessStatusCode();
        await feed.ReadRevisionsAsync(count: 1, Budget);
        LastFrame(feed.Text).Should().Contain("\"progressOnly\":true");

        // Finishing the book changes its status, not just its position.
        (await writer.PutAsJsonAsync($"/api/books/{book.Id}/progress", new { location = "end", percentage = 100 }))
            .EnsureSuccessStatusCode();
        await feed.ReadRevisionsAsync(count: 1, Budget);
        LastFrame(feed.Text).Should().NotContain("progressOnly");
    }

    [Fact]
    public async Task An_open_feed_does_not_hold_off_maintenance_and_ends_when_it_starts()
    {
        // Before: the stream held the request-wide lease for its whole life, so
        // a restore or an activation waited out the drain timeout and failed.
        using var factory = FastFeed(pollMilliseconds: 600_000);
        using var client = factory.CreateClient();
        using var response = await OpenAsync(client);
        await using var feed = await Feed.OpenAsync(response);
        await feed.ReadRevisionsAsync(count: 1, Budget);

        var maintenance = factory.Services.GetRequiredService<ILibraryMaintenanceCoordinator>();
        var entered = maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.BackupRestore);
        var exclusive = await entered.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // A write in another tab wakes the feed, which sees maintenance and ends.
            LibraryChangeSignal.Publish(
                await KeyOfAsync(factory),
                new LibraryChange(ProgressOnly: false));
            (await feed.ReadToEndAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        }
        finally
        {
            await exclusive.DisposeAsync();
        }
    }

    private static async Task<string> KeyOfAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<NostosDbContext>>();
        await using var db = await contexts.CreateDbContextAsync();
        return LibraryChangeSignal.KeyFor(db);
    }

    private static string LastFrame(string text) =>
        RevisionPattern().Matches(text).Last().Value;

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> FastFeed(
        int heartbeatSeconds = 15,
        int lifetimeSeconds = 600,
        int pollMilliseconds = 50) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [LibraryChangesEndpoints.PollMillisecondsKey] = pollMilliseconds.ToString(),
                    [LibraryChangesEndpoints.HeartbeatSecondsKey] = heartbeatSeconds.ToString(),
                    [LibraryChangesEndpoints.MaxLifetimeSecondsKey] = lifetimeSeconds.ToString(),
                })));

    private static Task<HttpResponseMessage> OpenAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, LibraryChangesEndpoints.Route);
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    [GeneratedRegex("event: library\ndata: \\{\"stateVersion\":\"([0-9]+)\"(,\"progressOnly\":true)?\\}")]
    private static partial Regex RevisionPattern();

    /// <summary>
    /// An open event stream read incrementally. A stream has no end, so every
    /// read is bounded by a wall-clock budget instead of awaiting the body.
    /// </summary>
    private sealed class Feed : IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly StringBuilder _text = new();
        private readonly byte[] _buffer = new byte[2048];
        private int _consumedRevisions;

        private Feed(Stream stream) => _stream = stream;

        public string Text => _text.ToString();

        public static async Task<Feed> OpenAsync(HttpResponseMessage response) =>
            new(await response.Content.ReadAsStreamAsync());

        /// <summary>Returns the next <paramref name="count"/> announced revisions not yet returned.</summary>
        public async Task<IReadOnlyList<string>> ReadRevisionsAsync(int count, TimeSpan budget)
        {
            await ReadUntilAsync(
                text => RevisionPattern().Matches(text).Count >= _consumedRevisions + count,
                budget);

            var revisions = RevisionPattern().Matches(Text)
                .Skip(_consumedRevisions)
                .Take(count)
                .Select(match => match.Groups[1].Value)
                .ToList();

            _consumedRevisions += revisions.Count;
            return revisions;
        }

        public async Task ReadUntilAsync(Func<string, bool> done, TimeSpan budget)
        {
            using var cts = new CancellationTokenSource(budget);
            while (!done(Text))
            {
                var read = await _stream.ReadAsync(_buffer, cts.Token);
                if (read <= 0)
                    throw new InvalidOperationException($"The stream ended early. Received: {Text}");

                _text.Append(Encoding.UTF8.GetString(_buffer, 0, read));
            }
        }

        /// <summary>True when the server closed the stream within the budget.</summary>
        public async Task<bool> ReadToEndAsync(TimeSpan budget)
        {
            using var cts = new CancellationTokenSource(budget);
            try
            {
                while (true)
                {
                    var read = await _stream.ReadAsync(_buffer, cts.Token);
                    if (read <= 0)
                        return true;

                    _text.Append(Encoding.UTF8.GetString(_buffer, 0, read));
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }
}
