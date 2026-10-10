using FluentAssertions;
using Nostos.Product.Alignment;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class AlignmentResourceLimitTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(640_000)]
    [InlineData(FfmpegAudioClipCutter.MaxPcmBytes)]
    public void Pcm_within_limit_is_accepted(long bytes)
    {
        Action action = () =>
            FfmpegAudioClipCutter.ValidatePcmLength(bytes);

        action.Should().NotThrow();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(FfmpegAudioClipCutter.MaxPcmBytes + 1)]
    [InlineData(long.MaxValue)]
    public void Oversized_or_invalid_pcm_is_rejected(long bytes)
    {
        Action action = () =>
            FfmpegAudioClipCutter.ValidatePcmLength(bytes);

        action.Should().Throw<AudioClipException>()
            .Which.Code.Should().Be(AudioClipErrorCodes.DecodeFailed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200L * 1024 * 1024)]
    public void Epub_within_default_limit_is_accepted(long bytes)
    {
        Action action = () =>
            AlignmentDocumentCache.ValidateEpubLength(
                bytes, 200L * 1024 * 1024);

        action.Should().NotThrow();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(200L * 1024 * 1024 + 1)]
    [InlineData(long.MaxValue)]
    public void Oversized_epub_is_rejected(long bytes)
    {
        Action action = () =>
            AlignmentDocumentCache.ValidateEpubLength(
                bytes, 200L * 1024 * 1024);

        action.Should().Throw<AlignmentException>()
            .Which.Code.Should().Be("alignment_target_too_large");
    }

    [Fact]
    public void Invalid_epub_limit_is_rejected()
    {
        Action action = () =>
            AlignmentDocumentCache.ValidateEpubLength(100, 0);

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("01:00:00", 3600)]
    [InlineData("00:01:00", 60)]
    [InlineData("25:00:00", 90000)]
    public void Single_file_accepts_position_at_known_end(
        string duration,
        double position)
    {
        Action action = () =>
            FfmpegAudioClipCutter.ValidateSingleFilePosition(
                duration, position);

        action.Should().NotThrow();
    }

    [Theory]
    [InlineData("01:00:00", 3600.001)]
    [InlineData("00:01:00", 61)]
    [InlineData("25:00:00", 90001)]
    public void Single_file_rejects_position_after_known_end(
        string duration,
        double position)
    {
        Action action = () =>
            FfmpegAudioClipCutter.ValidateSingleFilePosition(
                duration, position);

        action.Should().Throw<AudioClipException>()
            .Which.Code.Should().Be(AudioClipErrorCodes.InvalidPosition);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void Unknown_duration_does_not_reject_valid_position(
        string? duration)
    {
        Action action = () =>
            FfmpegAudioClipCutter.ValidateSingleFilePosition(
                duration, 7200);

        action.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_single_file_positions_are_rejected(
        double position)
    {
        Action action = () =>
            FfmpegAudioClipCutter.ValidateSingleFilePosition(
                null, position);

        action.Should().Throw<AudioClipException>()
            .Which.Code.Should().Be(AudioClipErrorCodes.InvalidPosition);
    }
}
