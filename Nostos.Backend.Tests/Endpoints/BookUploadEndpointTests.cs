using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// The resumable book-file contract (#1266): a transfer survives a dropped
/// connection, a resumed session sends only the missing parts, and the file
/// that lands in the library is byte-identical to the source.
/// </summary>
public sealed class BookUploadEndpointTests
{
    private const int PartBytes = 16 * 1024 * 1024;

    [Fact]
    public async Task Interrupted_upload_resumes_from_receipts_and_publishes_identical_bytes()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();
        var book = await CreateBookAsync(client);

        // Two parts: the resume path must skip what the server already has.
        var total = PartBytes + 4099;
        var bytes = new byte[total];
        new Random(1266).NextBytes(bytes);
        var sha = Sha256Hex(bytes);

        var created = await CreateSessionAsync(client, book.Id, bytes.Length, "resume.txt", sha);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var session = await ReadStatusAsync(created);

        using (var first = await PutPartAsync(client, book.Id, session.SessionId, 0, bytes))
            first.StatusCode.Should().Be(HttpStatusCode.OK);

        // The client goes away before the last part: no completion, no row update.
        var current = await client.GetFromJsonAsync<UploadStatusDto>(
            $"/api/books/{book.Id}/file/uploads");
        current!.SessionId.Should().Be(session.SessionId);
        current.ReceivedParts.Should().Equal(0);
        current.State.Should().Be("Receiving");

        using (var last = await PutPartAsync(client, book.Id, session.SessionId, 1, bytes))
            last.StatusCode.Should().Be(HttpStatusCode.OK);

        using var completed = await client.PostAsync(
            $"/api/books/{book.Id}/file/uploads/{session.SessionId}/complete",
            null);
        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        var final = await ReadStatusAsync(completed);
        final.State.Should().Be("Complete");
        final.ReceivedParts.Should().Equal(0, 1);

        await using (var db = OpenDb(factory.DatabasePath))
        {
            var row = await db.Books.SingleAsync(b => b.Id == book.Id);
            row.FileDetails.HasFile.Should().BeTrue();
            row.FileDetails.FileName.Should().Be("book.txt");
            var sessionRow = await db.BookUploadSessionRecords.SingleAsync();
            sessionRow.State.Should().Be(4); // Complete
            sessionRow.ActiveSessionKey.Should().BeNull();
        }

        var stored = Path.Combine(factory.BooksRootPath, book.Id.ToString(), "book.txt");
        File.Exists(stored).Should().BeTrue();
        Sha256Hex(await File.ReadAllBytesAsync(stored)).Should().Be(sha);
    }

    [Fact]
    public async Task Repeating_a_received_part_is_idempotent()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();
        var book = await CreateBookAsync(client);
        var bytes = Encoding.ASCII.GetBytes("hello resumable world");
        var sha = Sha256Hex(bytes);

        var created = await CreateSessionAsync(client, book.Id, bytes.Length, "small.txt", sha);
        var session = await ReadStatusAsync(created);

        using (var first = await PutPartAsync(client, book.Id, session.SessionId, 0, bytes))
            first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var second = await PutPartAsync(client, book.Id, session.SessionId, 0, bytes);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await second.Content.ReadFromJsonAsync<PartResultDto>();
        result!.AlreadyPresent.Should().BeTrue();
    }

    [Fact]
    public async Task Part_with_wrong_hash_is_rejected_without_a_receipt()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();
        var book = await CreateBookAsync(client);
        var bytes = Encoding.ASCII.GetBytes("checksum matters");
        var sha = Sha256Hex(bytes);

        var created = await CreateSessionAsync(client, book.Id, bytes.Length, "small.txt", sha);
        var session = await ReadStatusAsync(created);

        var bad = new string('0', 64);
        using var response = await PutPartAsync(client, book.Id, session.SessionId, 0, bytes, bad);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("book_upload_part_hash_mismatch");

        var current = await client.GetFromJsonAsync<UploadStatusDto>(
            $"/api/books/{book.Id}/file/uploads");
        current!.ReceivedParts.Should().BeEmpty();
    }

    [Fact]
    public async Task Different_file_replaces_an_interrupted_session()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();
        var book = await CreateBookAsync(client);

        var first = Encoding.ASCII.GetBytes("first draft");
        var created = await CreateSessionAsync(
            client, book.Id, first.Length, "draft.txt", Sha256Hex(first));
        var session = await ReadStatusAsync(created);
        using (var part = await PutPartAsync(client, book.Id, session.SessionId, 0, first))
            part.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = Encoding.ASCII.GetBytes("second, final draft");
        using var replaced = await CreateSessionAsync(
            client, book.Id, second.Length, "draft.txt", Sha256Hex(second));
        replaced.StatusCode.Should().Be(HttpStatusCode.Created);
        var replacement = await ReadStatusAsync(replaced);
        replacement.SessionId.Should().NotBe(session.SessionId);
        replacement.ReceivedParts.Should().BeEmpty();

        await using var db = OpenDb(factory.DatabasePath);
        (await db.BookUploadSessionRecords.CountAsync(s => s.ActiveSessionKey != null))
            .Should().Be(1);
    }

    [Fact]
    public async Task Unknown_session_is_not_found()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();
        var book = await CreateBookAsync(client);

        using var response = await client.GetAsync(
            $"/api/books/{book.Id}/file/uploads/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Legacy_whole_request_upload_still_works()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();
        var book = await CreateBookAsync(client);
        var bytes = Encoding.ASCII.GetBytes("legacy upload path");

        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "legacy.txt");

        using var response = await client.PostAsync($"/api/books/{book.Id}/file", form);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = Path.Combine(factory.BooksRootPath, book.Id.ToString(), "book.txt");
        File.Exists(stored).Should().BeTrue();
        Sha256Hex(await File.ReadAllBytesAsync(stored)).Should().Be(Sha256Hex(bytes));
    }

    [Fact]
    public async Task Capability_advertises_the_contract_on_this_host()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();

        var capabilities = await client.GetFromJsonAsync<JsonElement>(
            "/api/runtime/capabilities");
        capabilities.GetProperty("supportsResumableBookUploads").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_the_book_discards_its_unfinished_upload()
    {
        using var factory = new LibraryEndpointFactory();
        using var client = factory.CreateClient();
        var book = await CreateBookAsync(client);
        var bytes = Encoding.ASCII.GetBytes("will be discarded");

        var created = await CreateSessionAsync(
            client, book.Id, bytes.Length, "discard.txt", Sha256Hex(bytes));
        var session = await ReadStatusAsync(created);

        using var deleted = await client.DeleteAsync($"/api/books/{book.Id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var db = OpenDb(factory.DatabasePath))
        {
            (await db.BookUploadSessionRecords.CountAsync()).Should().Be(0);
        }

        var staging = Path.Combine(
            factory.StorageTempRoot,
            "transfers",
            "book-uploads",
            session.SessionId.ToString("N"));
        Directory.Exists(staging).Should().BeFalse();
    }

    private static async Task<BookDto> CreateBookAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title = $"Resumable {Guid.NewGuid():N}",
            author = "Test Author",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private static Task<HttpResponseMessage> CreateSessionAsync(
        HttpClient client,
        Guid bookId,
        long totalBytes,
        string fileName,
        string sha) =>
        client.PostAsJsonAsync($"/api/books/{bookId}/file/uploads", new
        {
            totalBytes,
            fileName,
            contentType = "text/plain",
            sha256 = sha,
        });

    private static async Task<UploadStatusDto> ReadStatusAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UploadStatusDto>())!;
    }

    private static async Task<HttpResponseMessage> PutPartAsync(
        HttpClient client,
        Guid bookId,
        Guid sessionId,
        int partIndex,
        byte[] allBytes,
        string? partShaOverride = null)
    {
        var offset = (long)partIndex * PartBytes;
        var length = (int)Math.Min(PartBytes, allBytes.Length - offset);
        var part = allBytes.AsMemory((int)offset, length).ToArray();
        var partSha = partShaOverride ?? Sha256Hex(part);

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/books/{bookId}/file/uploads/{sessionId}/parts/{partIndex}");
        var content = new ByteArrayContent(part);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.TryAddWithoutValidation(
            "Content-Range",
            $"bytes {offset}-{offset + length - 1}/{allBytes.Length}");
        content.Headers.TryAddWithoutValidation("X-Nostos-Chunk-SHA256", partSha);
        request.Content = content;
        return await client.SendAsync(request);
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static NostosDbContext OpenDb(string path) =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}").Options);

    private sealed record UploadStatusDto(
        Guid SessionId,
        string State,
        int[] ReceivedParts);

    private sealed record PartResultDto(Guid SessionId, int PartIndex, bool AlreadyPresent);
}
