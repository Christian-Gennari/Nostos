using System.Net;
using System.Net.Http.Headers;
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

/// <summary>
/// The same invariants as the storage tests, through the SelfHosted host's
/// real routes: this proves the wrapper is what the endpoints are given.
/// </summary>
public sealed class ExternalMediaEndpointTests : IDisposable
{
    private readonly string _externalRoot =
        Path.Combine(Path.GetTempPath(), "nostos-external-endpoint-" + Guid.NewGuid().ToString("N"));

    public ExternalMediaEndpointTests() => Directory.CreateDirectory(_externalRoot);

    public void Dispose()
    {
        if (Directory.Exists(_externalRoot))
            Directory.Delete(_externalRoot, recursive: true);
    }

    [Fact]
    public async Task A_linked_book_streams_ranges_from_the_source_and_deleting_it_keeps_the_source()
    {
        await using var factory = new Factory(_externalRoot);
        var client = factory.CreateClient();
        var sourcePath = Path.Combine(_externalRoot, "Original Name.m4b");
        await File.WriteAllTextAsync(sourcePath, "0123456789");
        var source = SourceSnapshot.Take(sourcePath);
        var bookId = await AddLinkedBookAsync(factory, "Original Name.m4b");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/books/{bookId}/file");
        request.Headers.Range = new RangeHeaderValue(2, 5);
        using var ranged = await client.SendAsync(request);

        ranged.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await ranged.Content.ReadAsStringAsync()).Should().Be("2345");
        ranged.Content.Headers.ContentRange!.Length.Should().Be(10);
        Directory.Exists(Path.Combine(factory.BooksRootPath, bookId.ToString())).Should().BeFalse();

        using var deleted = await client.DeleteAsync($"/api/books/{bookId}");

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        source.IsUnchanged().Should().BeTrue();
        (await client.GetAsync($"/api/books/{bookId}/file")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_linked_book_whose_source_is_gone_answers_not_found_and_keeps_its_row()
    {
        await using var factory = new Factory(_externalRoot);
        var client = factory.CreateClient();
        var bookId = await AddLinkedBookAsync(factory, "never-there.m4b");

        (await client.GetAsync($"/api/books/{bookId}/file")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/books/{bookId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_self_hosted_server_offers_the_feature_and_says_whether_a_folder_is_configured()
    {
        await using var configured = new Factory(_externalRoot);
        await using var unconfigured = new LibraryEndpointFactory();

        foreach (var factory in new LibraryEndpointFactory[] { configured, unconfigured })
        {
            var capabilities = await factory.CreateClient()
                .GetFromJsonAsync<DeploymentCapabilitiesResponse>(DeploymentCapabilitiesEndpoints.Route);
            capabilities!.SupportsExternalMediaLibraries.Should().BeTrue();
        }

        var on = await configured.CreateClient()
            .GetFromJsonAsync<ExternalMediaStatusResponse>(ExternalMediaEndpoints.Route);
        var off = await unconfigured.CreateClient()
            .GetFromJsonAsync<ExternalMediaStatusResponse>(ExternalMediaEndpoints.Route);

        on.Should().BeEquivalentTo(new { Configured = true, Mounted = true });
        off.Should().BeEquivalentTo(new { Configured = false, Mounted = false, LinkedBooks = 0 });
    }

    private static async Task<Guid> AddLinkedBookAsync(Factory factory, string relativePath)
    {
        using var scope = factory.Services.CreateScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NostosDbContext>>();
        var root = scope.ServiceProvider.GetRequiredService<ExternalMediaRoot>();
        await using var db = await contexts.CreateDbContextAsync();

        var book = new AudioBookModel { Title = "Linked", Author = "Author" };
        book.FileDetails.HasFile = true;
        book.FileDetails.FileName = "book.m4b";
        db.Books.Add(book);
        db.ExternalMediaLinks.Add(new ExternalMediaLinkModel
        {
            BookId = book.Id,
            TrackNumber = ExternalMediaLinkModel.PrimaryFile,
            RootId = root.RootId,
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
