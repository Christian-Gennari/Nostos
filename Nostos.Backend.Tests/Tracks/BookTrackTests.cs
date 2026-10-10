using FluentAssertions;
using Nostos.Backend.Services;
using Xunit;

namespace Nostos.Backend.Tests.Tracks;

public sealed class BookTrackTests
{
    [Theory]
    [InlineData(1, ".mp3", "track-0001.mp3")]
    [InlineData(42, ".M4A", "track-0042.m4a")]
    [InlineData(2000, ".m4b", "track-2000.m4b")]
    public void Canonical_file_name_is_prefix_padded_number_and_lowercase_extension(
        int number, string extension, string expected)
    {
        BookTrackFormats.CanonicalFileName(number, extension).Should().Be(expected);
        BookTrackFormats.IsCanonicalFileName(expected, number).Should().BeTrue();
        BookTrackFormats.TryParseCanonicalFileName(expected, out var parsed).Should().BeTrue();
        parsed.Should().Be(number);
    }

    [Theory]
    [InlineData("0001.mp3")]            // no prefix
    [InlineData("track-1.mp3")]         // not padded
    [InlineData("track-00001.mp3")]     // too many digits
    [InlineData("track-0000.mp3")]      // numbering starts at 1
    [InlineData("track-2001.mp3")]      // above the ceiling
    [InlineData("track-0001.MP3")]      // extension is stored lowercase
    [InlineData("track-0001.epub")]     // not audio
    [InlineData("Track-0001.mp3")]      // prefix is case-sensitive
    [InlineData("track-0001.mp3.partial")]
    [InlineData("book.mp3")]
    public void Anything_else_is_not_a_canonical_track_name(string fileName)
    {
        BookTrackFormats.TryParseCanonicalFileName(fileName, out _).Should().BeFalse();
    }

    [Fact]
    public void Unsupported_track_extension_is_refused()
    {
        var act = () => BookTrackFormats.CanonicalFileName(1, ".epub");
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Track_list_round_trips_and_sorts_by_number()
    {
        var tracks = new List<BookTrack>
        {
            Track(2, "Two"),
            Track(1, "One"),
        };

        var parsed = BookTrackList.Parse(BookTrackList.Serialize(tracks));

        parsed.Select(track => track.Number).Should().Equal(1, 2);
        parsed[0].Should().Be(Track(1, "One"));
        BookTrackList.TotalDurationMs(parsed).Should().Be(180_000);
        BookTrackList.TotalBytes(parsed).Should().Be(2_000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"number\":1}")]
    public void A_missing_or_unreadable_document_is_a_single_file_book(string? json)
    {
        BookTrackList.Parse(json).Should().BeEmpty();
    }

    [Fact]
    public void Validation_accepts_a_contiguous_canonical_list()
    {
        BookTrackList.Validate([Track(1, "One"), Track(2, null)]).Should().BeNull();
    }

    [Fact]
    public void Validation_rejects_gaps_wrong_names_and_unmeasured_tracks()
    {
        BookTrackList.Validate([]).Should().NotBeNull();
        BookTrackList.Validate([Track(1, "One"), Track(3, "Three")]).Should().Contain("contiguously");
        BookTrackList.Validate([Track(1, "One") with { FileName = "track-0002.mp3" }])
            .Should().Contain("non-canonical");
        BookTrackList.Validate([Track(1, "One") with { Bytes = 0 }]).Should().Contain("size or duration");
        BookTrackList.Validate([Track(1, "One") with { DurationMs = 0 }]).Should().Contain("size or duration");
    }

    [Theory]
    [InlineData("Chapter 1: The Beginning", 54, "07 - Chapter 1- The Beginning.mp3")]
    [InlineData("  a/b\\c  ", 9, "07 - a-b-c.mp3")]
    [InlineData("What? \"Why\" <now>|*", 120, "007 - What Why now.mp3")]
    [InlineData("Första kapitlet", 3, "07 - Första kapitlet.mp3")]
    [InlineData("Trailing dots...", 3, "07 - Trailing dots.mp3")]
    [InlineData(null, 3, "07.mp3")]
    [InlineData("   ", 3, "07.mp3")]
    public void Download_names_sort_in_listening_order_and_are_safe_on_any_filesystem(
        string? title, int trackCount, string expected)
    {
        var track = Track(7, title);

        AudiobookPackageNaming.TrackEntryName(track, trackCount).Should().Be(expected);
    }

    [Fact]
    public void A_very_long_title_is_cut_without_splitting_a_character()
    {
        var title = new string('a', 79) + "\U0001F3A7" + "tail";

        var name = AudiobookPackageNaming.TrackEntryName(Track(1, title), 2);

        name.Should().Be("01 - " + new string('a', 79) + ".mp3");
    }

    private static BookTrack Track(int number, string? title) =>
        new(number, BookTrackFormats.CanonicalFileName(number, ".mp3"), "audio/mpeg", 90_000, 1_000, 0xABCDEF01, title);
}
