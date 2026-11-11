using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Wikisource;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

/// <summary>
/// WS Export builds a PDF on request. Live checks (#1397) showed it answering in
/// 4 to 12 s, but one request stalled for over 120 s. A stall must end with a
/// retry or a clear message, never a hang or a generic failure.
/// </summary>
public sealed class WikisourcePdfDownloadTests : IDisposable
{
    private static readonly Uri PdfUrl = new("https://ws-export.wmcloud.org/?lang=en&format=pdf&page=Advanced_Australia");
    private static readonly byte[] Payload = "%PDF-1.4\n"u8.ToArray();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nostos-wspdf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Download_WhenFirstPdfRequestStalls_RetriesAndSavesThePdf()
    {
        var calls = 0;
        var handler = new SequenceHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                await Task.Delay(Timeout.Infinite, ct);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
        });

        var written = await CreateDownloader(handler, attempts: 2).DownloadAsync(
            PdfUrl, Path.Combine(_directory, "book.pdf"), CreateProvider(), long.MaxValue, progress: null, CancellationToken.None);

        written.Should().Be(Payload.Length);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task Download_WhenEveryPdfRequestStalls_ReportsATimeoutThatNamesTheSource()
    {
        var handler = new SequenceHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var act = () => CreateDownloader(handler, attempts: 1).DownloadAsync(
            PdfUrl, Path.Combine(_directory, "book.pdf"), CreateProvider(), long.MaxValue, progress: null, CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ProviderDownloadException>()).Which;
        ex.Code.Should().Be(ProviderDownloadException.Timeout);
        ex.Message.Should().Contain("ws-export.wmcloud.org").And.Contain("1 seconds");
    }

    [Fact]
    public async Task Download_WhenTheCallerCancels_StaysACancellation()
    {
        var handler = new SequenceHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // The per-attempt deadline must sit far beyond the caller's 200 ms, or a
        // busy CI runner can fire the 1 s deadline timer first and the download
        // reports a timeout instead of the caller's cancellation.
        var act = () => CreateDownloader(handler, attempts: 1, timeoutSeconds: 60).DownloadAsync(
            PdfUrl, Path.Combine(_directory, "book.pdf"), CreateProvider(), long.MaxValue, progress: null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static WikisourceProvider CreateProvider() => new(new StubHttpClientFactory());

    private static ProviderContentDownloader CreateDownloader(HttpMessageHandler handler, int attempts, int timeoutSeconds = 1) =>
        new(
            new HandlerFactory(handler),
            Options.Create(new AcquisitionOptions { DownloadAttempts = attempts, DownloadTimeoutSeconds = timeoutSeconds }),
            NullLogger<ProviderContentDownloader>.Instance);

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class SequenceHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request, ct);
    }
}
