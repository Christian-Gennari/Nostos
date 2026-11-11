using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// Readwise through its export API, end to end (issue #1525): connect a token,
/// preview the mapped page, commit through the existing import batch, repeat it
/// as duplicates, undo it, and disconnect. Readwise is a loopback HTTP server;
/// no test calls the real service.
/// </summary>
public sealed class ReadwiseImportEndpointTests
{
    private const string Token = "endpoint-test-token";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Connect_preview_commit_repeat_undo_and_disconnect_share_the_import_flow()
    {
        var export = PageWithNoNextCursor();
        string? seenAuthorization = null;
        var exportRequests = new List<string>();
        await using var readwise = LoopbackHttpServer.Start(async (request, stream, ct) =>
        {
            if (request.RequestLine.StartsWith("GET /api/v2/auth/", StringComparison.Ordinal))
            {
                seenAuthorization = request.Authorization;
                await LoopbackHttpServer.WriteResponseAsync(stream, 204, "", "application/json", ct);
                return;
            }

            if (request.RequestLine.StartsWith("GET /api/v2/export/", StringComparison.Ordinal))
            {
                exportRequests.Add(request.RequestLine);
                await LoopbackHttpServer.WriteResponseAsync(stream, 200, export, "application/json", ct);
                return;
            }

            await LoopbackHttpServer.WriteResponseAsync(stream, 404, "", "application/json", ct);
        });

        using var factory = new ReadwiseEndpointFactory($"http://127.0.0.1:{readwise.Port}");
        var client = factory.CreateClient();

        // --- Connect ---------------------------------------------------------
        var connect = await client.PostAsJsonAsync(
            "/api/notes/imports/readwise/connect",
            new { token = Token });
        connect.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await connect.Content.ReadFromJsonAsync<ReadwiseConnectionStatusDto>(Json);
        status!.Connected.Should().BeTrue();
        (await connect.Content.ReadAsStringAsync()).Should().NotContain(Token);
        seenAuthorization.Should().Be($"Token {Token}");

        (await client.GetFromJsonAsync<ReadwiseConnectionStatusDto>(
            "/api/notes/imports/readwise", Json))!.Connected.Should().BeTrue();

        // --- Preview ---------------------------------------------------------
        var previewResponse = await client.PostAsync("/api/notes/imports/readwise/preview", null);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var previewBody = await previewResponse.Content.ReadAsStringAsync();
        previewBody.Should().NotContain(Token);
        var preview = JsonSerializer.Deserialize<HighlightImportPreview>(previewBody, Json)!;
        preview.Source.Should().Be("readwise");
        preview.Books.Should().ContainSingle();
        var book = preview.Books.Single();
        book.Title.Should().Be("Synthetic Title 4");
        book.Author.Should().Be("Synthetic Author 3");
        book.Match.Should().Be("none");
        book.BookId.Should().BeNull();
        book.AnnotationCount.Should().Be(2);
        book.NewCount.Should().Be(2);

        preview.Skipped!.GroupBy(entry => entry.Reason)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count))
            .Should().BeEquivalentTo(new Dictionary<string, int>
            {
                [ReadwiseBookRules.ArticleReason] = 3,
                [ReadwiseBookRules.TweetReason] = 1,
                [ReadwiseBookRules.PodcastReason] = 1,
            });

        await using (var before = CreateContext(factory.DatabasePath))
        {
            (await before.Notes.CountAsync()).Should().Be(0);
            (await before.NoteImportBookLinks.CountAsync()).Should().Be(0);
        }

        // --- Commit ----------------------------------------------------------
        var first = await CommitAsync(client, book.SourceKey);
        first.BatchId.Should().NotBeNull();
        var imported = first.Books.Single();
        imported.Status.Should().Be("imported");
        imported.ImportedCount.Should().Be(2);
        imported.Created.Should().BeTrue();
        imported.BookId.Should().NotBeNull();

        await using (var db = CreateContext(factory.DatabasePath))
        {
            var batch = await db.NoteImportBatches.Include(item => item.Notes).SingleAsync();
            batch.Source.Should().Be("readwise");
            batch.FileName.Should().Be("Readwise");
            batch.Notes.Should().HaveCount(2);

            var notes = await db.Notes.AsNoTracking()
                .Where(note => note.BookId == imported.BookId)
                .ToListAsync();
            notes.Should().HaveCount(2);
            notes.Should().AllSatisfy(note =>
            {
                note.SourceAnchorKind.Should().Be("readwise_page");
                note.AnchorVerified.Should().BeFalse();
                note.CaptureSource.Should().Be("import");
            });

            var connection = await db.ReadwiseConnections.SingleAsync();
            connection.TokenEncrypted.Should().NotContain(Token);
            connection.UpdatedAfterUtc.Should().NotBeNull();
            connection.LastImportedAtUtc.Should().NotBeNull();

            var link = await db.NoteImportBookLinks.SingleAsync();
            link.Source.Should().Be("readwise");
            link.SourceKey.Should().Be(book.SourceKey);
            link.BookId.Should().Be(imported.BookId!.Value);
        }

        // --- The next import is a dedupe, not a second copy -------------------
        var second = await CommitAsync(client, book.SourceKey, imported.BookId);
        second.Books.Single().ImportedCount.Should().Be(0);
        second.Books.Single().DuplicateCount.Should().Be(2);
        exportRequests.Should().HaveCount(3);
        exportRequests[0].Should().NotContain("updatedAfter=", "the first preview has no watermark");
        exportRequests[1].Should().NotContain("updatedAfter=", "the first commit still starts from the beginning");
        exportRequests[2].Should().Contain("updatedAfter=", "the next import asks only for changes");

        // --- Undo takes back exactly the batch's notes ------------------------
        (await client.DeleteAsync($"/api/notes/imports/batches/{first.BatchId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await using (var db = CreateContext(factory.DatabasePath))
        {
            (await db.Notes.CountAsync()).Should().Be(0);
            (await db.NoteImportBatches.CountAsync()).Should().Be(0);
        }

        // --- Disconnect -------------------------------------------------------
        var disconnect = await client.DeleteAsync("/api/notes/imports/readwise");
        disconnect.StatusCode.Should().Be(HttpStatusCode.OK);
        (await disconnect.Content.ReadFromJsonAsync<ReadwiseConnectionStatusDto>(Json))!
            .Connected.Should().BeFalse();
        await using (var db = CreateContext(factory.DatabasePath))
        {
            (await db.ReadwiseConnections.CountAsync()).Should().Be(0);
        }

        (await client.PostAsync("/api/notes/imports/readwise/preview", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_empty_token_is_a_bad_request_and_never_reaches_readwise()
    {
        await using var readwise = LoopbackHttpServer.Start((_, stream, ct) =>
            LoopbackHttpServer.WriteResponseAsync(stream, 204, "", "application/json", ct));
        using var factory = new ReadwiseEndpointFactory($"http://127.0.0.1:{readwise.Port}");
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/notes/imports/readwise/connect",
            new { token = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        readwise.Requests.Should().Be(0);
    }

    private static async Task<HighlightImportResult> CommitAsync(
        HttpClient client,
        string sourceKey,
        Guid? bookId = null)
    {
        var decision = bookId is null
            ? (object)new { sourceKey, create = true }
            : new { sourceKey, bookId };
        var response = await client.PostAsJsonAsync(
            "/api/notes/imports/readwise/commit",
            new { decisions = new[] { decision } });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<HighlightImportResult>(Json))!;
    }

    private static string PageWithNoNextCursor()
    {
        var node = JsonNode.Parse(File.ReadAllText(ImportFixtures.ReadwiseApiExport))!;
        node["nextPageCursor"] = null;
        return node.ToJsonString();
    }

    private static NostosDbContext CreateContext(string path)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new NostosDbContext(options);
    }

    private sealed class ReadwiseEndpointFactory(string baseUrl) : LibraryEndpointFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Readwise:BaseUrl"] = baseUrl,
                }));
        }
    }
}
