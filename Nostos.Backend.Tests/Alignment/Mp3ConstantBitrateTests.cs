using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Product.Alignment;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class Mp3ConstantBitrateTests
{
    // MPEG 2 Layer III at 22 050 Hz, the shape of a 64 kbps audiobook.
    private static byte[] Frame(int bitrateIndex)
    {
        var header = new byte[] { 0xFF, 0xF3, (byte)(bitrateIndex << 4), 0xC4 };
        var length = Mp3ConstantBitrate.ParseFrame(header)!.Value.Length;
        var frame = new byte[length];
        header.CopyTo(frame, 0);
        return frame;
    }

    private static byte[] File(
        int frames,
        Func<int, int> bitrateIndexAt,
        byte[]? prefix = null,
        byte[]? firstFramePayload = null)
    {
        using var stream = new MemoryStream();
        stream.Write(prefix ?? []);

        for (var i = 0; i < frames; i++)
        {
            var frame = Frame(bitrateIndexAt(i));

            if (i == 0 && firstFramePayload is not null)
                firstFramePayload.CopyTo(frame, 36);

            stream.Write(frame);
        }

        return stream.ToArray();
    }

    private static double Seconds(int frames) => frames * 576 / 22050d;

    private static Task<bool> Probe(
        byte[] file,
        double? seconds,
        string tag,
        List<StorageByteRange>? reads = null) =>
        Mp3ConstantBitrate.IsConstantAsync(
            new StoredAssetInfo(
                "book.mp3", "audio/mpeg", file.Length, tag, DateTimeOffset.UnixEpoch),
            seconds,
            (range, _) =>
            {
                reads?.Add(range);

                return Task.FromResult<StoredAssetRead?>(new StoredAssetRead(
                    new StoredAssetInfo(
                        "book.mp3", "audio/mpeg", file.Length, tag, DateTimeOffset.UnixEpoch),
                    new MemoryStream(
                        file, (int)range.Start, (int)range.Length, writable: false),
                    range));
            });

    [Fact]
    public async Task A_file_of_equal_frames_that_accounts_for_its_duration_is_constant()
    {
        var reads = new List<StorageByteRange>();
        var file = File(4000, _ => 8);

        (await Probe(file, Seconds(4000), "constant", reads)).Should().BeTrue();

        // The whole point: a few small reads, never the file.
        reads.Sum(range => range.Length).Should().BeLessThanOrEqualTo(
            (Mp3ConstantBitrate.Windows + 1) * Mp3ConstantBitrate.WindowBytes);
    }

    [Fact]
    public async Task An_id3_tag_before_the_audio_is_stepped_over()
    {
        // "ID3", version 2.4, no flags, a 3000-byte body in synchsafe form.
        var tag = new byte[3010];
        new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0x17, 0x38 }
            .CopyTo(tag, 0);

        Mp3ConstantBitrate.Id3v2Length(tag).Should().Be(3010);

        var file = File(4000, _ => 8, prefix: tag);

        (await Probe(file, Seconds(4000), "tagged")).Should().BeTrue();
    }

    [Fact]
    public async Task One_window_at_another_bitrate_is_not_constant()
    {
        var file = File(4000, i => i is > 2000 and < 2400 ? 4 : 8);

        (await Probe(file, Seconds(4000), "one-window")).Should().BeFalse();
    }

    [Fact]
    public async Task Frames_that_alternate_bitrates_are_not_constant()
    {
        var file = File(4000, i => i % 2 == 0 ? 8 : 6);

        (await Probe(file, Seconds(4000), "alternating")).Should().BeFalse();
    }

    [Fact]
    public async Task A_variable_bitrate_header_is_not_constant_even_when_the_samples_agree()
    {
        var file = File(4000, _ => 8, firstFramePayload: "Xing"u8.ToArray());

        (await Probe(file, Seconds(4000), "xing")).Should().BeFalse();
    }

    [Fact]
    public async Task The_constant_bitrate_form_of_that_header_is_accepted()
    {
        var file = File(4000, _ => 8, firstFramePayload: "Info"u8.ToArray());

        (await Probe(file, Seconds(4000), "info")).Should().BeTrue();
    }

    [Fact]
    public async Task An_info_frame_at_another_bitrate_is_not_audio()
    {
        // What ffmpeg's own encoder writes: one 56 kbps header frame, then 64.
        var file = File(
            4000, i => i == 0 ? 7 : 8, firstFramePayload: "Info"u8.ToArray());

        (await Probe(file, Seconds(3999), "info-other")).Should().BeTrue();
    }

    [Fact]
    public async Task A_first_frame_at_another_bitrate_without_that_header_is_not_constant()
    {
        var file = File(4000, i => i == 0 ? 7 : 8);

        (await Probe(file, Seconds(4000), "first-other")).Should().BeFalse();
    }

    [Fact]
    public async Task Equal_samples_that_do_not_account_for_the_duration_are_not_constant()
    {
        // Sampled windows agree, but the file is 20% shorter than this bitrate
        // would make it: most of it was encoded at something else.
        var file = File(4000, _ => 8);

        (await Probe(file, Seconds(4000) * 1.2, "wrong-duration")).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    public async Task An_unknown_duration_is_not_constant(double? seconds)
    {
        var file = File(4000, _ => 8);

        (await Probe(file, seconds, "no-duration")).Should().BeFalse();
    }

    [Fact]
    public async Task Bytes_that_are_not_frames_are_not_constant()
    {
        var file = new byte[400_000];
        new Random(3).NextBytes(file);

        (await Probe(file, 100, "noise")).Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_read_is_not_constant_and_is_asked_again()
    {
        var file = File(4000, _ => 8);
        var info = new StoredAssetInfo(
            "book.mp3", "audio/mpeg", file.Length, "flaky", DateTimeOffset.UnixEpoch);

        var failed = await Mp3ConstantBitrate.IsConstantAsync(
            info,
            Seconds(4000),
            (_, _) => throw new IOException("storage is away"));

        failed.Should().BeFalse();
        (await Probe(file, Seconds(4000), "flaky")).Should().BeTrue();
    }

    [Fact]
    public async Task The_answer_is_remembered_for_one_stored_revision()
    {
        var reads = new List<StorageByteRange>();
        var file = File(4000, _ => 8);

        await Probe(file, Seconds(4000), "remembered", reads);
        var first = reads.Count;
        await Probe(file, Seconds(4000), "remembered", reads);

        reads.Count.Should().Be(first);
    }

    [Theory]
    [InlineData("book.mp3", true)]
    [InlineData("001.MP3", true)]
    [InlineData("book.m4b", false)]
    [InlineData(null, false)]
    public void Only_mp3_is_considered(string? fileName, bool expected) =>
        Mp3ConstantBitrate.IsMp3(fileName).Should().Be(expected);

    [Fact]
    public void Free_format_and_other_layers_are_not_frames()
    {
        Mp3ConstantBitrate.ParseFrame([0xFF, 0xF3, 0x00, 0xC4]).Should().BeNull();
        Mp3ConstantBitrate.ParseFrame([0xFF, 0xF5, 0x80, 0xC4]).Should().BeNull();
        Mp3ConstantBitrate.ParseFrame([0xFF, 0xF3, 0x80, 0xC4])!.Value
            .Should().Be(new Mp3ConstantBitrate.Frame(2, 22050, 64, 208));
    }

    [Fact]
    public void The_fast_seek_flag_precedes_the_input_only_when_asked()
    {
        var url = new Uri("http://127.0.0.1:5/x");

        var exact = FfmpegAudioClipCutter.DecodeArguments(url, 12.5, 20, fastSeek: false);
        var fast = FfmpegAudioClipCutter.DecodeArguments(url, 12.5, 20, fastSeek: true);

        exact.Should().NotContain("+fastseek");
        fast.Except(exact).Should().BeEquivalentTo(["-fflags", "+fastseek"]);

        Array.IndexOf(fast, "+fastseek").Should().BeLessThan(Array.IndexOf(fast, "-i"));
        fast[Array.IndexOf(fast, "-ss") + 1].Should().Be("12.5");
    }
}
