using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services;
using Nostos.Backend.Tests.Mcp;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Configuration;

public sealed class TestStorageIsolationTests
{
    [Fact]
    public async Task LibraryEndpointFactory_WritesOnlyToDisposableStorage_AndDeletesItOnDispose()
    {
        var bookId = Guid.NewGuid();
        string tempRoot;
        string defaultBookFolder;

        using (var factory = new LibraryEndpointFactory())
        {
            tempRoot = factory.StorageTempRoot;

            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();

            var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
            var defaultRoot = FileStorageOptions.ResolveBooksRoot(
                env.ContentRootPath,
                new FileStorageOptions());
            defaultBookFolder = Path.Combine(defaultRoot, bookId.ToString());

            storage.StorageRoot.Should().Be(Path.GetFullPath(factory.BooksRootPath));
            storage.StorageRoot.Should().NotBe(defaultRoot);

            await using var content = new MemoryStream(Encoding.UTF8.GetBytes("isolated fixture"));
            var saved = await storage.SaveBookFileAsync(bookId, content, "fixture.txt");

            File.Exists(saved).Should().BeTrue();
            Directory.Exists(defaultBookFolder).Should().BeFalse(
                "full-host backend tests must never write fixture books to the application's default Storage/books tree");
        }

        Directory.Exists(tempRoot).Should().BeFalse(
            "disposing the full-host test factory must remove its complete disposable storage root");
    }

    [Fact]
    public void McpHttpFactory_UsesDisposableStorage_AndDeletesItOnDispose()
    {
        string tempRoot;

        using (var factory = new McpHttpFactory(enabled: false))
        {
            tempRoot = factory.StorageTempRoot;

            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();

            var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
            var defaultRoot = FileStorageOptions.ResolveBooksRoot(
                env.ContentRootPath,
                new FileStorageOptions());

            storage.StorageRoot.Should().Be(Path.GetFullPath(factory.BooksRootPath));
            storage.StorageRoot.Should().NotBe(defaultRoot);
        }

        Directory.Exists(tempRoot).Should().BeFalse(
            "disposing the MCP test factory must remove its complete disposable storage root");
    }
}
