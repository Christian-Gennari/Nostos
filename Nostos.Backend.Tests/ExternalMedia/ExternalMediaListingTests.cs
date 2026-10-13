using FluentAssertions;
using Nostos.Backend.Services.ExternalMedia;
using Xunit;

namespace Nostos.Backend.Tests.ExternalMedia;

public sealed class ExternalMediaListingTests : IDisposable
{
    private readonly ExternalMediaTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public void Lists_every_file_below_the_root_as_an_openable_reference()
    {
        _bed.WriteSource("Author/Title/01.mp3", "a");
        _bed.WriteSource("Author/Title/02.mp3", "bb");
        _bed.WriteSource("Loose.epub", "ccc");

        var listing = _bed.Root.List(100, CancellationToken.None);

        listing.Files.Select(f => (f.RelativePath, f.Length)).Should().BeEquivalentTo(
        [
            ("Author/Title/01.mp3", 1L),
            ("Author/Title/02.mp3", 2L),
            ("Loose.epub", 3L),
        ]);
        listing.Files.Should().OnlyContain(f => _bed.Root.Find(_bed.Root.RootId, f.RelativePath) != null);
        listing.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Symlinks_are_counted_and_never_followed()
    {
        File.WriteAllText(Path.Combine(_bed.Outside, "secret.mp3"), "secret");
        File.CreateSymbolicLink(
            Path.Combine(_bed.ExternalRoot, "link.mp3"),
            Path.Combine(_bed.Outside, "secret.mp3"));
        Directory.CreateSymbolicLink(Path.Combine(_bed.ExternalRoot, "Shelf"), _bed.Outside);
        _bed.WriteSource("real.mp3", "a");

        var listing = _bed.Root.List(100, CancellationToken.None);

        listing.Files.Select(f => f.RelativePath).Should().Equal("real.mp3");
        listing.SkippedLinks.Should().Be(2);
    }

    [Fact]
    public void Hidden_entries_are_passed_over()
    {
        _bed.WriteSource(".trash/old.mp3", "a");
        _bed.WriteSource("Title/.DS_Store", "a");
        _bed.WriteSource("Title/01.mp3", "a");

        _bed.Root.List(100, CancellationToken.None).Files.Select(f => f.RelativePath)
            .Should().Equal("Title/01.mp3");
    }

    [Fact]
    public void A_name_that_cannot_be_a_reference_is_counted_and_left_out()
    {
        _bed.WriteSource("Title/01.mp3", "a");
        File.WriteAllText(Path.Combine(_bed.ExternalRoot, "Title", "bad\\name.mp3"), "a");

        var listing = _bed.Root.List(100, CancellationToken.None);

        listing.Files.Select(f => f.RelativePath).Should().Equal("Title/01.mp3");
        listing.UnusableNames.Should().Be(1);
    }

    [Fact]
    public void The_walk_stops_at_its_file_limit_and_says_so()
    {
        for (var i = 0; i < 5; i++)
            _bed.WriteSource($"Title/{i}.mp3", "a");

        var listing = _bed.Root.List(3, CancellationToken.None);

        listing.Files.Should().HaveCount(3);
        listing.Truncated.Should().BeTrue();
    }

    [Fact]
    public void An_unmounted_root_lists_nothing()
    {
        using var bed = new ExternalMediaTestBed(configureRoot: false);

        bed.Root.List(100, CancellationToken.None).Files.Should().BeEmpty();
    }

    [Fact]
    public void The_real_describer_reads_tags_duration_and_ebook_metadata_without_touching_the_files()
    {
        var mp3 = Path.Combine(_bed.ExternalRoot, "tagged.mp3");
        File.WriteAllBytes(mp3, SyntheticMedia.Mp3(400, album: "The Album", artist: "An Author", title: "Chapter 1"));
        File.WriteAllBytes(Path.Combine(_bed.ExternalRoot, "book.epub"), SyntheticMedia.Epub("An Ebook", "A Writer"));
        File.WriteAllBytes(Path.Combine(_bed.ExternalRoot, "paper.pdf"), SyntheticMedia.Pdf());
        File.WriteAllText(Path.Combine(_bed.ExternalRoot, "broken.epub"), "not a zip");
        File.WriteAllText(Path.Combine(_bed.ExternalRoot, "broken.mp3"), "not audio");
        var before = SourceSnapshot.Take(mp3);
        var describer = new ExternalMediaDescriber(_bed.Root);
        var rootId = _bed.Root.RootId;

        var audio = describer.Describe(rootId, "tagged.mp3");
        var epub = describer.Describe(rootId, "book.epub");

        audio.Should().BeEquivalentTo(new
        {
            Readable = true,
            Album = "The Album",
            Author = "An Author",
            Title = "Chapter 1",
        });
        (audio.DurationMs / 1000d).Should().BeApproximately(SyntheticMedia.Mp3Seconds(400), 0.1);
        epub.Should().BeEquivalentTo(new { Readable = true, Title = "An Ebook", Author = "A Writer", Language = "en" });
        describer.Describe(rootId, "paper.pdf").Readable.Should().BeTrue();
        describer.Describe(rootId, "broken.epub").Readable.Should().BeFalse();
        describer.Describe(rootId, "broken.mp3").Readable.Should().BeFalse();
        describer.Describe(rootId, "absent.mp3").Readable.Should().BeFalse();
        describer.Describe(rootId, "../outside/x.mp3").Readable.Should().BeFalse();
        before.IsUnchanged().Should().BeTrue();
    }
}
