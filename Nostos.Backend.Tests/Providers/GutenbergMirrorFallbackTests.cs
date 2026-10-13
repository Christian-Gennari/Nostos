using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// gutenberg.org answering 5xx must not fail an import while its mirror still
/// serves the file.
/// </summary>
public sealed class GutenbergMirrorFallbackTests : IDisposable
{
    private const string EpubPath = "/cache/epub/51356/pg51356-images-3.epub";
    private static readonly Uri Primary = new("https://www.gutenberg.org" + EpubPath);
    private static readonly byte[] Payload = [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nostos-mirror-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("https://www.gutenberg.org/cache/epub/51356/pg51356-images-3.epub", "https://gutenberg.pglaf.org/cache/epub/51356/pg51356-images-3.epub")]
    [InlineData("https://gutenberg.org/cache/epub/1342/pg1342.cover.medium.jpg", "https://gutenberg.pglaf.org/cache/epub/1342/pg1342.cover.medium.jpg")]
    public void MirrorFor_GeneratedFile_KeepsThePathOnTheMirrorHost(string url, string expected)
    {
        var provider = CreateProvider();

        provider.MirrorFor(new Uri(url)).Should().Be(new Uri(expected));
    }

    [Theory]
    [InlineData("https://www.gutenberg.org/ebooks/51356.opds")]
    [InlineData("https://www.gutenberg.org/ebooks/51356.epub3.images")]
    [InlineData("https://gutenberg.pglaf.org/cache/epub/51356/pg51356.epub")]
    [InlineData("https://example.com/cache/epub/51356/pg51356.epub")]
    public void MirrorFor_AnythingElse_HasNoMirror(string url)
    {
        var provider = CreateProvider();

        provider.MirrorFor(new Uri(url)).Should().BeNull();
    }

    [Fact]
    public void AllowedHosts_AdmitTheMirror_ButNotTheRestOfItsDomain()
    {
        var provider = CreateProvider();

        ProviderHostPolicy.IsAllowed("gutenberg.pglaf.org", provider.AllowedHosts).Should().BeTrue();
        ProviderHostPolicy.IsAllowed("pglaf.org", provider.AllowedHosts).Should().BeFalse();
        ProviderHostPolicy.IsAllowed("www.pglaf.org", provider.AllowedHosts).Should().BeFalse();
    }

    [Fact]
    public async Task Download_WhenGutenbergAnswers504_FetchesTheSameFileFromTheMirror()
    {
        var handler = new StubHttpMessageHandler().Register(EpubPath, request =>
            request.RequestUri!.Host == GutenbergCatalog.MirrorHost
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }
                : new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
        var downloader = CreateDownloader(handler);
        var destination = Path.Combine(_directory, "book.epub");

        var written = await downloader.DownloadAsync(
            Primary, destination, CreateProvider(), long.MaxValue, progress: null, CancellationToken.None);

        written.Should().Be(Payload.Length);
        File.ReadAllBytes(destination).Should().Equal(Payload);
        handler.RecordedRequests.Select(r => r.RequestUri!.Host)
            .Should().BeEquivalentTo(["www.gutenberg.org", GutenbergCatalog.MirrorHost]);
    }

    [Fact]
    public async Task Download_WhenGutenbergIsHealthy_NeverTouchesTheMirror()
    {
        var handler = new StubHttpMessageHandler().Register(EpubPath, _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        var downloader = CreateDownloader(handler);

        await downloader.DownloadAsync(
            Primary, Path.Combine(_directory, "book.epub"), CreateProvider(), long.MaxValue, progress: null, CancellationToken.None);

        handler.RecordedRequests.Select(r => r.RequestUri!.Host).Should().Equal("www.gutenberg.org");
    }

    [Fact]
    public async Task Download_WhenGutenbergHasNoSuchFile_DoesNotAskTheMirror()
    {
        var handler = new StubHttpMessageHandler();
        var downloader = CreateDownloader(handler);

        var act = () => downloader.DownloadAsync(
            Primary, Path.Combine(_directory, "book.epub"), CreateProvider(), long.MaxValue, progress: null, CancellationToken.None);

        (await act.Should().ThrowAsync<ProviderDownloadException>())
            .Which.Code.Should().Be(ProviderDownloadException.NotFound);
        handler.RecordedRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task Download_WithOneConfiguredAttempt_StillGivesTheMirrorItsTurn()
    {
        var handler = new StubHttpMessageHandler().Register(EpubPath, request =>
            request.RequestUri!.Host == GutenbergCatalog.MirrorHost
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var downloader = CreateDownloader(handler, attempts: 1);

        var bytes = await downloader.DownloadBytesAsync(Primary, CreateProvider(), 1024, CancellationToken.None);

        bytes.Should().Equal(Payload);
    }

    private static GutenbergProvider CreateProvider() =>
        new(new StubHttpClientFactory(), NullLogger<GutenbergProvider>.Instance);

    private static ProviderContentDownloader CreateDownloader(StubHttpMessageHandler handler, int attempts = 3) =>
        new(
            new StubHttpClientFactory(handler),
            Options.Create(new AcquisitionOptions { DownloadAttempts = attempts }),
            NullLogger<ProviderContentDownloader>.Instance);
}
