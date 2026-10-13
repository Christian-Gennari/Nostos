using FluentAssertions;
using Nostos.Backend.Services;
using Nostos.Backend.Services.ExternalMedia;
using Xunit;

namespace Nostos.Backend.Tests.ExternalMedia;

public sealed class ExternalMediaGroupingTests
{
    private static ExternalMediaGrouping Group(params string[] paths) =>
        ExternalMediaGrouper.Group(paths
            .Select(path => new ExternalMediaFile(path, 100, DateTimeOffset.UnixEpoch))
            .ToList());

    [Theory]
    [InlineData("2 - Intro.mp3", "10 - End.mp3")]
    [InlineData("Track 9.mp3", "Track 10.mp3")]
    [InlineData("001.mp3", "2.mp3")]
    [InlineData("a.mp3", "B.mp3")]
    [InlineData("Part 1 - 02.mp3", "Part 1 - 11.mp3")]
    public void Names_order_the_way_a_person_reads_them(string first, string second)
    {
        NaturalOrder.Compare(first, second).Should().BeNegative();
        NaturalOrder.Compare(second, first).Should().BePositive();
    }

    [Fact]
    public void Names_that_differ_only_by_leading_zeros_still_have_one_order()
    {
        NaturalOrder.Compare("01.mp3", "1.mp3").Should().NotBe(0);
        NaturalOrder.Compare("01.mp3", "1.mp3").Should().Be(-NaturalOrder.Compare("1.mp3", "01.mp3"));
    }

    [Fact]
    public void Every_ebook_and_every_m4b_is_a_book_of_its_own()
    {
        var grouping = Group("Shelf/One.epub", "Shelf/Two.pdf", "Shelf/Three.m4b", "Shelf/Four.m4b");

        grouping.Candidates.Select(c => (c.Key, c.Kind, c.Layout)).Should().BeEquivalentTo(
        [
            ("file:Shelf/One.epub", "ebook", "file"),
            ("file:Shelf/Two.pdf", "ebook", "file"),
            ("file:Shelf/Three.m4b", "audiobook", "file"),
            ("file:Shelf/Four.m4b", "audiobook", "file"),
        ]);
        grouping.Candidates.Should().OnlyContain(c => c.Concerns.Count == 0);
    }

    [Fact]
    public void A_folder_of_audio_files_is_one_book_with_tracks_in_natural_order()
    {
        var grouping = Group(
            "Author/Title/10 - End.mp3",
            "Author/Title/2 - Middle.mp3",
            "Author/Title/1 - Start.mp3",
            "Author/Title/cover.jpg");

        var book = grouping.Candidates.Should().ContainSingle().Subject;
        book.Key.Should().Be("tracks:Author/Title");
        book.Layout.Should().Be("tracks");
        book.Files.Select(f => f.RelativePath).Should().Equal(
            "Author/Title/1 - Start.mp3",
            "Author/Title/2 - Middle.mp3",
            "Author/Title/10 - End.mp3");
        book.Concerns.Should().BeEmpty();
        book.TotalBytes.Should().Be(300);
    }

    [Fact]
    public void A_folder_with_one_audio_file_is_a_single_file_book()
    {
        var book = Group("Author/Title/Title.mp3").Candidates.Should().ContainSingle().Subject;

        book.Key.Should().Be("file:Author/Title/Title.mp3");
        book.Layout.Should().Be("file");
    }

    [Fact]
    public void Tracks_beside_an_m4b_are_proposed_with_a_concern()
    {
        var grouping = Group("Title/Title.m4b", "Title/01.mp3", "Title/02.mp3");

        grouping.Candidates.Should().HaveCount(2);
        grouping.Candidates.Single(c => c.Layout == "tracks").Concerns.Should().ContainSingle()
            .Which.Should().Contain("M4B");
        grouping.Candidates.Single(c => c.Layout == "file").Concerns.Should().BeEmpty();
    }

    [Fact]
    public void Several_audio_files_directly_in_the_linked_folder_are_not_assumed_to_be_one_book()
    {
        var book = Group("One.mp3", "Two.mp3").Candidates.Should().ContainSingle().Subject;

        book.Key.Should().Be("tracks:");
        book.Concerns.Should().ContainSingle().Which.Should().Contain("separate books");
    }

    [Fact]
    public void Disc_folders_are_proposed_separately_and_flagged()
    {
        var grouping = Group("Title/CD 1/01.mp3", "Title/CD 1/02.mp3", "Title/Disc2/01.mp3", "Title/Disc2/02.mp3");

        grouping.Candidates.Should().HaveCount(2);
        grouping.Candidates.Should().OnlyContain(c => c.Concerns.Any(concern => concern.Contains("disc")));
    }

    [Fact]
    public void A_folder_with_its_own_tracks_and_audio_subfolders_is_flagged()
    {
        var grouping = Group("Title/01.mp3", "Title/02.mp3", "Title/Extras/01.mp3", "Title/Extras/02.mp3");

        grouping.Candidates.Single(c => c.Key == "tracks:Title").Concerns
            .Should().ContainSingle().Which.Should().Contain("subfolders");
        grouping.Candidates.Single(c => c.Key == "tracks:Title/Extras").Concerns.Should().BeEmpty();
    }

    [Fact]
    public void A_sibling_folder_with_a_longer_name_is_not_a_subfolder()
    {
        var grouping = Group("Title/01.mp3", "Title/02.mp3", "Title 2/01.mp3", "Title 2/02.mp3");

        grouping.Candidates.Should().OnlyContain(c => c.Concerns.Count == 0);
    }

    [Fact]
    public void A_folder_with_more_tracks_than_a_book_can_hold_is_blocked()
    {
        var paths = Enumerable.Range(1, BookTrackList.MaxTracks + 1).Select(n => $"Huge/{n}.mp3").ToArray();

        var book = Group(paths).Candidates.Should().ContainSingle().Subject;

        book.Blocked.Should().BeTrue();
    }

    [Fact]
    public void Unsupported_media_is_reported_and_other_files_are_ignored()
    {
        var grouping = Group("Title/book.flac", "Title/notes.txt", "Title/cover.jpg", "Title/metadata.json");

        grouping.Candidates.Should().BeEmpty();
        grouping.UnsupportedMedia.Should().Equal("Title/book.flac");
    }

    [Fact]
    public void Grouping_the_same_listing_twice_gives_the_same_keys_in_the_same_order()
    {
        string[] paths = ["b/2.mp3", "b/1.mp3", "a.epub", "c/x.m4b"];

        Group(paths).Candidates.Select(c => c.Key)
            .Should().Equal(Group(paths.Reverse().ToArray()).Candidates.Select(c => c.Key));
    }
}
