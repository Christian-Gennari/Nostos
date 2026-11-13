using System.Net;
using System.Net.Http.Headers;

namespace Nostos.Backend.Tests.Support;

internal static class TransientHttpTestHelpers
{
    public static HttpResponseMessage ChunkThenThrowXmlResponse(string prefix, Func<Exception> exceptionFactory) =>
        ChunkThenThrowResponse(System.Text.Encoding.UTF8.GetBytes(prefix), "application/atom+xml", exceptionFactory);

    public static HttpResponseMessage ChunkThenThrowJsonResponse(string prefix, Func<Exception> exceptionFactory) =>
        ChunkThenThrowResponse(System.Text.Encoding.UTF8.GetBytes(prefix), "application/json", exceptionFactory);

    public static HttpResponseMessage ChunkThenThrowResponse(
        byte[] initialBytes,
        string mediaType,
        Func<Exception> exceptionFactory)
    {
        var content = new ChunkThenThrowContent(initialBytes, exceptionFactory);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    public static IHttpClientFactory CreateTimeoutFactory(Uri baseAddress, TimeSpan timeout) =>
        new TimeoutHttpClientFactory(baseAddress, timeout);

    private sealed class TimeoutHttpClientFactory(Uri baseAddress, TimeSpan timeout) : IHttpClientFactory
    {
        private readonly InfiniteDelayHandler _handler = new();

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false)
            {
                BaseAddress = baseAddress,
                Timeout = timeout,
            };
    }

    private sealed class InfiniteDelayHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
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
