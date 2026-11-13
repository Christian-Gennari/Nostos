using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Providers.Contracts;
using Nostos.Backend.Providers.Gutenberg;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Providers;

public sealed class ProviderContentDownloaderTests : IDisposable
{
    private const string EpubPath = "/cache/epub/51356/pg51356-images-3.epub";
    private static readonly Uri GutenbergPrimary = new("https://www.gutenberg.org" + EpubPath);
    private static readonly Uri SingleHostUrl = new("https://ws-export.wmcloud.org/?lang=en&format=epub&page=Test");
    private static readonly byte[] Payload = "PK\x03\x04test-ebook-payload-bytes"u8.ToArray();

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "nostos-downloader-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task DownloadAsync_WhenHeadersThrowHttpRequestException_RetriesAndSavesFile()
    {
        var calls = 0;
        var handler = new SequenceHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new HttpRequestException("connection reset by peer");

            return Task.FromResult(OkBytes(Payload));
        });

        var destination = Path.Combine(_directory, "book.epub");
        var written = await CreateDownloader(handler, attempts: 2).DownloadAsync(
            SingleHostUrl,
            destination,
            new SingleHostPolicy(),
            long.MaxValue,
            progress: null,
            CancellationToken.None);

        written.Should().Be(Payload.Length);
        File.ReadAllBytes(destination).Should().Equal(Payload);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task DownloadAsync_WhenHeadersThrowIOException_RetriesAndSavesFile()
    {
        var calls = 0;
        var handler = new SequenceHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new IOException("TLS handshake read failure");

            return Task.FromResult(OkBytes(Payload));
        });

        var destination = Path.Combine(_directory, "book.epub");
        var written = await CreateDownloader(handler, attempts: 2).DownloadAsync(
            SingleHostUrl,
            destination,
            new SingleHostPolicy(),
            long.MaxValue,
            progress: null,
            CancellationToken.None);

        written.Should().Be(Payload.Length);
        File.ReadAllBytes(destination).Should().Equal(Payload);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task DownloadAsync_WhenBodyThrowsIOExceptionMidStream_CleansPartialFileRetriesAndSavesFile()
    {
        var calls = 0;
        var handler = new SequenceHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ChunkThenThrowContent(
                        Payload[..6],
                        () => new IOException("unexpected EOF mid-stream")),
                });
            }

            return Task.FromResult(OkBytes(Payload));
        });

        var destination = Path.Combine(_directory, "book.epub");
        var written = await CreateDownloader(handler, attempts: 2).DownloadAsync(
            SingleHostUrl,
            destination,
            new SingleHostPolicy(),
            long.MaxValue,
            progress: null,
            CancellationToken.None);

        written.Should().Be(Payload.Length);
        File.ReadAllBytes(destination).Should().Equal(Payload);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task DownloadAsync_WhenBodyThrowsHttpRequestExceptionMidStream_RetriesAndSavesFile()
    {
        var calls = 0;
        var handler = new SequenceHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ChunkThenThrowContent(
                        Payload[..6],
                        () => new HttpRequestException("response ended prematurely")),
                });
            }

            return Task.FromResult(OkBytes(Payload));
        });

        var destination = Path.Combine(_directory, "book.epub");
        var written = await CreateDownloader(handler, attempts: 2).DownloadAsync(
            SingleHostUrl,
            destination,
            new SingleHostPolicy(),
            long.MaxValue,
            progress: null,
            CancellationToken.None);

        written.Should().Be(Payload.Length);
        File.ReadAllBytes(destination).Should().Equal(Payload);
        calls.Should().Be(2);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DownloadAsync_WhenTransientErrorsExhaustAttempts_ThrowsRetryableFailedAndLeavesNoPartialFile(
        bool useIOException,
        bool failMidStream)
    {
        var calls = 0;
        var handler = new SequenceHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            Exception CreateException() => useIOException
                ? new IOException("transient I/O failure")
                : new HttpRequestException("transient HTTP failure");

            if (!failMidStream)
                throw CreateException();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ChunkThenThrowContent(Payload[..4], CreateException),
            });
        });

        var destination = Path.Combine(_directory, "book.epub");
        var act = () => CreateDownloader(handler, attempts: 3).DownloadAsync(
            SingleHostUrl,
            destination,
            new SingleHostPolicy(),
            long.MaxValue,
            progress: null,
            CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ProviderDownloadException>()).Which;
        ex.Code.Should().Be(ProviderDownloadException.Failed);
        ex.Retryable.Should().BeTrue();
        calls.Should().Be(3);
        File.Exists(destination).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DownloadAsync_WhenPrimaryThrowsTransientException_FailsOverToMirror(
        bool useIOException,
        bool failMidStream)
    {
        var hosts = new List<string>();
        var handler = new SequenceHandler((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == GutenbergCatalog.MirrorHost)
                return Task.FromResult(OkBytes(Payload));

            Exception CreateException() => useIOException
                ? new IOException("primary reset")
                : new HttpRequestException("primary unreachable");

            if (!failMidStream)
                throw CreateException();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ChunkThenThrowContent(Payload[..5], CreateException),
            });
        });

        var destination = Path.Combine(_directory, "book.epub");
        var written = await CreateDownloader(handler, attempts: 1).DownloadAsync(
            GutenbergPrimary,
            destination,
            CreateGutenbergPolicy(),
            long.MaxValue,
            progress: null,
            CancellationToken.None);

        written.Should().Be(Payload.Length);
        File.ReadAllBytes(destination).Should().Equal(Payload);
        hosts.Should().Equal("www.gutenberg.org", GutenbergCatalog.MirrorHost);
    }

    [Fact]
    public async Task DownloadAsync_WhenCallerCancelsMidStream_PropagatesOperationCanceledExceptionWithoutRetry()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var handler = new SequenceHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ChunkThenThrowContent(Payload[..4], () =>
                {
                    cts.Cancel();
                    return new OperationCanceledException(cts.Token);
                }),
            });
        });

        var destination = Path.Combine(_directory, "book.epub");
        var act = () => CreateDownloader(handler, attempts: 3).DownloadAsync(
            SingleHostUrl,
            destination,
            new SingleHostPolicy(),
            long.MaxValue,
            progress: null,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1);
        File.Exists(destination).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DownloadBytesAsync_WhenTransientErrorsOccur_RetriesAndReturnsBytes(
        bool useIOException,
        bool failMidStream)
    {
        var calls = 0;
        var handler = new SequenceHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Exception CreateException() => useIOException
                    ? new IOException("stream drop")
                    : new HttpRequestException("socket drop");

                if (!failMidStream)
                    throw CreateException();

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ChunkThenThrowContent(Payload[..4], CreateException),
                });
            }

            return Task.FromResult(OkBytes(Payload));
        });

        var bytes = await CreateDownloader(handler, attempts: 2).DownloadBytesAsync(
            SingleHostUrl,
            new SingleHostPolicy(),
            1024,
            CancellationToken.None);

        bytes.Should().Equal(Payload);
        calls.Should().Be(2);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DownloadBytesAsync_WhenPrimaryFailsWithTransientException_FailsOverToMirror(
        bool useIOException,
        bool failMidStream)
    {
        var hosts = new List<string>();
        var handler = new SequenceHandler((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.Host == GutenbergCatalog.MirrorHost)
                return Task.FromResult(OkBytes(Payload));

            Exception CreateException() => useIOException
                ? new IOException("primary stream abort")
                : new HttpRequestException("primary connect failure");

            if (!failMidStream)
                throw CreateException();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ChunkThenThrowContent(Payload[..4], CreateException),
            });
        });

        var bytes = await CreateDownloader(handler, attempts: 1).DownloadBytesAsync(
            GutenbergPrimary,
            CreateGutenbergPolicy(),
            1024,
            CancellationToken.None);

        bytes.Should().Equal(Payload);
        hosts.Should().Equal("www.gutenberg.org", GutenbergCatalog.MirrorHost);
    }

    [Fact]
    public async Task DownloadBytesAsync_WhenTransientErrorsExhaustAttempts_ThrowsRetryableFailed()
    {
        var calls = 0;
        var handler = new SequenceHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ChunkThenThrowContent(Payload[..3], () => new IOException("broken pipe")),
            });
        });

        var act = () => CreateDownloader(handler, attempts: 2).DownloadBytesAsync(
            SingleHostUrl,
            new SingleHostPolicy(),
            1024,
            CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ProviderDownloadException>()).Which;
        ex.Code.Should().Be(ProviderDownloadException.Failed);
        ex.Retryable.Should().BeTrue();
        calls.Should().Be(2);
    }

    [Fact]
    public async Task DownloadBytesAsync_WhenCallerCancelsMidStream_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var handler = new SequenceHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ChunkThenThrowContent(Payload[..3], () =>
                {
                    cts.Cancel();
                    return new OperationCanceledException(cts.Token);
                }),
            });
        });

        var act = () => CreateDownloader(handler, attempts: 3).DownloadBytesAsync(
            SingleHostUrl,
            new SingleHostPolicy(),
            1024,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1);
    }

    private static HttpResponseMessage OkBytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static GutenbergProvider CreateGutenbergPolicy() =>
        new(new StubHttpClientFactory(), NullLogger<GutenbergProvider>.Instance);

    private static ProviderContentDownloader CreateDownloader(
        HttpMessageHandler handler,
        int attempts,
        int timeoutSeconds = 30) =>
        new(
            new HandlerFactory(handler),
            Options.Create(new AcquisitionOptions
            {
                DownloadAttempts = attempts,
                DownloadTimeoutSeconds = timeoutSeconds,
            }),
            NullLogger<ProviderContentDownloader>.Instance)
        {
            InitialRetryDelay = TimeSpan.Zero,
        };

    private sealed class SingleHostPolicy : IProviderDownloadPolicy
    {
        public IReadOnlyList<string> AllowedHosts => ["ws-export.wmcloud.org"];
        public long MaxBytesPerPart => 1024 * 1024;
        public long MaxTotalBytes => 1024 * 1024;
        public int MaxParts => 1;
    }

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

    private sealed class ChunkThenThrowContent(byte[] initialBytes, Func<Exception> exceptionFactory) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new ChunkThenThrowStream(initialBytes, exceptionFactory));
    }

    private sealed class ChunkThenThrowStream(byte[] initialBytes, Func<Exception> exceptionFactory) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < initialBytes.Length)
            {
                var copy = Math.Min(count, initialBytes.Length - _offset);
                Array.Copy(initialBytes, _offset, buffer, offset, copy);
                _offset += copy;
                return copy;
            }

            throw exceptionFactory();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < initialBytes.Length)
            {
                var copy = Math.Min(buffer.Length, initialBytes.Length - _offset);
                initialBytes.AsSpan(_offset, copy).CopyTo(buffer.Span);
                _offset += copy;
                return ValueTask.FromResult(copy);
            }

            return ValueTask.FromException<int>(exceptionFactory());
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
