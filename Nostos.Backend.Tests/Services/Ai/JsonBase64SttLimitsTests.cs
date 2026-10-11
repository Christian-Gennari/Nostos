using System.Net;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Services.Ai;

/// <summary>
/// Regression coverage for bounded JSON transcription requests and responses.
/// </summary>
public sealed class JsonBase64SttLimitsTests
{
    private const int ResponseLimit = 2 * 1024 * 1024;

    [Fact]
    public async Task Response_larger_than_limit_is_a_typed_failure()
    {
        var handler = new StubHttpMessageHandler();
        handler.Register("/v4/ai/transcription-model", _ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"text\":\"" + new string('x', ResponseLimit) + "\"}"),
            });

        var provider = CreateProvider(handler);

        var act = () => provider.TranscribeWithConfigAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            null,
            Config());

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(SttErrorCodes.InvalidResponse);
        exception.Message.Should().Contain("size limit");
        handler.RecordedRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Response_without_content_length_is_still_bounded()
    {
        var handler = new StubHttpMessageHandler();

        handler.Register("/v4/ai/transcription-model", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);

            response.Content = new StreamContent(
                new MemoryStream(
                    Encoding.UTF8.GetBytes(
                        "{\"text\":\""
                        + new string('x', ResponseLimit)
                        + "\"}")));

            return response;
        });

        var provider = CreateProvider(handler);

        var act = () => provider.TranscribeWithConfigAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            null,
            Config());

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(SttErrorCodes.InvalidResponse);
        exception.Message.Should().Contain("size limit");
    }

    [Fact]
    public async Task Response_exactly_at_limit_is_accepted()
    {
        const string prefix = "{\"text\":\"";
        const string suffix = "\"}";

        var textLength = ResponseLimit
            - Encoding.UTF8.GetByteCount(prefix)
            - Encoding.UTF8.GetByteCount(suffix);

        var responseBody = prefix + new string('a', textLength) + suffix;

        Encoding.UTF8.GetByteCount(responseBody).Should().Be(ResponseLimit);

        var handler = new StubHttpMessageHandler();
        handler.Register("/v4/ai/transcription-model", _ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json"),
            });

        var provider = CreateProvider(handler);

        var result = await provider.TranscribeWithConfigAsync(
            new MemoryStream([1, 2, 3]),
            "clip.wav",
            "audio/wav",
            null,
            Config());

        result.Text.Should().HaveLength(textLength);
    }

    [Fact]
    public async Task Oversized_nonseekable_audio_is_rejected_without_sending()
    {
        var handler = new StubHttpMessageHandler();
        var provider = CreateProvider(handler, maxUploadBytes: 4);

        using var audio = new NonSeekableReadStream(
            new MemoryStream([1, 2, 3, 4, 5]));

        var act = () => provider.TranscribeWithConfigAsync(
            audio,
            "clip.wav",
            "audio/wav",
            null,
            Config());

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(SttErrorCodes.TooLarge);
        handler.RecordedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Oversized_seekable_audio_is_rejected_before_reading()
    {
        var handler = new StubHttpMessageHandler();
        var provider = CreateProvider(handler, maxUploadBytes: 4);

        using var audio = new TrackingMemoryStream([1, 2, 3, 4, 5]);

        var act = () => provider.TranscribeWithConfigAsync(
            audio,
            "clip.wav",
            "audio/wav",
            null,
            Config());

        var exception =
            (await act.Should().ThrowAsync<SttException>()).Which;

        exception.Code.Should().Be(SttErrorCodes.TooLarge);
        audio.ReadCount.Should().Be(0);
        handler.RecordedRequests.Should().BeEmpty();
    }

    private static EffectiveAiProviderConfig Config() => new(
        Enabled: true,
        BaseUrl: "https://transcription.example.test",
        Model: "alignment-test-model",
        ApiKeyEnvironmentVariable: "TEST_ALIGNMENT_TOKEN",
        ApiKey: "test-key",
        KeyFromServerEnv: false);

    private static JsonBase64SttProvider CreateProvider(
        StubHttpMessageHandler handler,
        long maxUploadBytes = 26_214_400) =>
        new(
            new StubHttpClientFactory(
                handler,
                new Uri("https://transcription.example.test")),
            new SpeechOptions { MaxUploadBytes = maxUploadBytes });

    private sealed class NonSeekableReadStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count) =>
            inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrackingMemoryStream(byte[] buffer)
        : MemoryStream(buffer, writable: false)
    {
        public int ReadCount { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.ReadAsync(destination, cancellationToken);
        }
    }
}
