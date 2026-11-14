using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// Hosts that build the service by hand (Nostos-Cloud's boundary tests) keep the
/// two-argument constructor after the #1611 decomposition: it wires the whole
/// collaborator graph over one context factory.
/// </summary>
public sealed class LibraryServiceConvenienceConstructorTests
{
    [Fact]
    public async Task Two_argument_constructor_creates_reads_and_updates_progress()
    {
        using var graph = new LibraryTestGraph();
        var lookup = new BookLookupService(
            new NoopHttpClientFactory(),
            NullLogger<BookLookupService>.Instance);
        ILibraryService library = new LibraryService(graph.Contexts, lookup);

        var created = await library.CreateOrMatchBookAsync(
            new LibraryCreateBookRequest(
                "test-client",
                $"k-{Guid.NewGuid():N}",
                "physical",
                "Hand Built",
                Author: "Author A"),
            strictConfirmation: false);
        var bookId = ((LibraryCreateOrMatchResultDto)created.Data!).BookId!.Value;

        var before = await graph.CommittedVersionAsync();
        await library.UpdateProgressAsync(bookId, "p-10", 10);

        (await graph.CommittedVersionAsync()).Should().Be(before + 1);
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
