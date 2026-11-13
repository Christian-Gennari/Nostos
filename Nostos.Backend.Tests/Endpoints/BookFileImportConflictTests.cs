using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>Provider acquisitions and manual writes must never race on one book.</summary>
public sealed class BookFileImportConflictTests
    : IClassFixture<LibraryEndpointFactory>, IAsyncLifetime
{
    private readonly LibraryEndpointFactory _factory;

    public BookFileImportConflictTests(LibraryEndpointFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetStateAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(BookStatus.Downloading)]
    [InlineData(BookStatus.Transcoding)]
    public async Task Upload_to_importing_book_is_conflict_without_writing_any_file(BookStatus status)
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);

        await using (var db = OpenDb(_factory.DatabasePath))
        {
            var row = await db.Books.SingleAsync(b => b.Id == book.Id);
            row.Status = status;
            await db.SaveChangesAsync();
        }

        using var response = await UploadAsync(client, book.Id);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("book_import_in_progress");

        await using (var db = OpenDb(_factory.DatabasePath))
        {
            var saved = await db.Books.SingleAsync(b => b.Id == book.Id);
            saved.Status.Should().Be(status);
            saved.FileDetails.HasFile.Should().BeFalse();
            saved.FileDetails.FileName.Should().BeNull();
        }
        Directory.Exists(Path.Combine(_factory.BooksRootPath, book.Id.ToString()))
            .Should().BeFalse("rejection must precede writing to durable storage");
    }

    [Fact]
    public async Task Upload_rejected_while_acquisition_owns_a_ready_book()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);
        var gate = _factory.Services.GetRequiredService<BookFileMutationGate>();

        using (await gate.EnterAsync(book.Id, CancellationToken.None))
        {
            using var rejected = await UploadAsync(client, book.Id);
            rejected.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        // A conflicting request must not change even a Ready book's metadata.
        await using var db = OpenDb(_factory.DatabasePath);
        var saved = await db.Books.SingleAsync(b => b.Id == book.Id);
        saved.Status.Should().Be(BookStatus.Ready);
        saved.FileDetails.HasFile.Should().BeFalse();
        saved.FileDetails.FileName.Should().BeNull();

        // Releasing the owner allows a later, deliberate upload attempt.
        using var after = gate.TryEnter(book.Id);
        after.Should().NotBeNull();
    }

    [Fact]
    public async Task Upload_to_book_with_a_file_is_refused_unless_replace_is_asked_for()
    {
        using var client = _factory.CreateClient();
        var book = await CreateBookAsync(client);

        await using (var db = OpenDb(_factory.DatabasePath))
        {
            var row = await db.Books.SingleAsync(b => b.Id == book.Id);
            row.FileDetails.HasFile = true;
            row.FileDetails.FileName = "original.pdf";
            await db.SaveChangesAsync();
        }

        using var refused = await UploadAsync(client, book.Id);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("book_has_file");

        await using (var db = OpenDb(_factory.DatabasePath))
        {
            var saved = await db.Books.SingleAsync(b => b.Id == book.Id);
            saved.FileDetails.FileName.Should().Be("original.pdf");
        }

        using var replaced = await UploadAsync(client, book.Id, "?replace=true");
        replaced.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var db = OpenDb(_factory.DatabasePath))
        {
            var saved = await db.Books.SingleAsync(b => b.Id == book.Id);
            saved.FileDetails.FileName.Should().NotBe("original.pdf");
        }
    }

    [Fact]
    public async Task Book_list_reports_its_page_size_ceiling()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/books?pageSize=200");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Max-Page-Size").Should().ContainSingle().Which.Should().Be("100");
        var page = await response.Content.ReadFromJsonAsync<PaginatedResponse<BookDto>>();
        page!.PageSize.Should().Be(100);
    }

    [Fact]
    public async Task Gate_is_per_book_and_a_waiter_can_take_over_after_release()
    {
        var gate = new BookFileMutationGate();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var lease = await gate.EnterAsync(first, CancellationToken.None);
        gate.TryEnter(first).Should().BeNull();
        using (gate.TryEnter(second))
        {
            gate.TryEnter(second).Should().BeNull();
        }

        var waiter = gate.EnterAsync(first, CancellationToken.None);
        waiter.IsCompleted.Should().BeFalse();
        lease.Dispose();
        using var next = await waiter;
        gate.TryEnter(first).Should().BeNull();
    }

    private static async Task<BookDto> CreateBookAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/books", new
        {
            type = "ebook",
            title = $"Import conflict {Guid.NewGuid():N}",
            author = "Test Author",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<BookDto>())!;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid bookId, string query = "")
    {
        using var content = new MultipartFormDataContent();
        using var bytes = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7\\nnot a real PDF"));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(bytes, "file", "conflicting.pdf");
        return await client.PostAsync($"/api/books/{bookId}/file{query}", content);
    }

    private static NostosDbContext OpenDb(string path) =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path}").Options);
}
