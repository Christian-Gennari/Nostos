using FluentAssertions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services.ExternalMedia;
using Xunit;

namespace Nostos.Backend.Tests.ExternalMedia;

public sealed class ExternalMediaRootTests : IDisposable
{
    private readonly ExternalMediaTestBed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Theory]
    [InlineData("book.m4b")]
    [InlineData("Author/Title/01 - Opening.mp3")]
    [InlineData("Författare/Ö.epub")]
    [InlineData("a..b/c...mp3")]
    public void Paths_strictly_below_the_root_are_valid(string path) =>
        ExternalMediaRelativePath.IsValid(path).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("../outside/secret.mp3")]
    [InlineData("a/../../outside/secret.mp3")]
    [InlineData("a/./b.mp3")]
    [InlineData("a//b.mp3")]
    [InlineData("a/b.mp3/")]
    [InlineData("a\\..\\b.mp3")]
    [InlineData("a/b\0.mp3")]
    [InlineData("a/b\n.mp3")]
    public void Paths_that_could_leave_the_root_are_invalid(string? path) =>
        ExternalMediaRelativePath.IsValid(path).Should().BeFalse();

    [Fact]
    public void A_path_longer_than_the_stored_column_is_invalid() =>
        ExternalMediaRelativePath.IsValid(new string('a', 1025)).Should().BeFalse();

    [Fact]
    public void A_file_below_the_root_is_found_and_opened_for_reading_only()
    {
        _bed.WriteSource("Author/Title/book.m4b", "audio");

        var file = _bed.Root.Find(_bed.Root.RootId, "Author/Title/book.m4b");
        using var stream = _bed.Root.OpenRead(_bed.Root.RootId, "Author/Title/book.m4b");

        file!.Length.Should().Be(5);
        stream!.CanRead.Should().BeTrue();
        stream.CanWrite.Should().BeFalse();
    }

    [Theory]
    [InlineData("../outside/secret.mp3")]
    [InlineData("Author/../../outside/secret.mp3")]
    [InlineData("/outside/secret.mp3")]
    public void A_traversal_reference_reads_nothing(string reference)
    {
        File.WriteAllText(Path.Combine(_bed.Outside, "secret.mp3"), "secret");
        Directory.CreateDirectory(Path.Combine(_bed.ExternalRoot, "Author"));

        _bed.Root.Find(_bed.Root.RootId, reference).Should().BeNull();
        _bed.Root.OpenRead(_bed.Root.RootId, reference).Should().BeNull();
    }

    [Fact]
    public void An_absolute_path_to_a_real_file_reads_nothing()
    {
        var secret = Path.Combine(_bed.Outside, "secret.mp3");
        File.WriteAllText(secret, "secret");

        _bed.Root.OpenRead(_bed.Root.RootId, secret).Should().BeNull();
    }

    [Fact]
    public void A_symlinked_file_that_escapes_the_root_reads_nothing()
    {
        var secret = Path.Combine(_bed.Outside, "secret.mp3");
        File.WriteAllText(secret, "secret");
        File.CreateSymbolicLink(Path.Combine(_bed.ExternalRoot, "link.mp3"), secret);

        _bed.Root.Find(_bed.Root.RootId, "link.mp3").Should().BeNull();
        _bed.Root.OpenRead(_bed.Root.RootId, "link.mp3").Should().BeNull();
    }

    [Fact]
    public void A_file_reached_through_a_symlinked_directory_reads_nothing()
    {
        File.WriteAllText(Path.Combine(_bed.Outside, "secret.mp3"), "secret");
        Directory.CreateSymbolicLink(Path.Combine(_bed.ExternalRoot, "Shelf"), _bed.Outside);

        _bed.Root.Find(_bed.Root.RootId, "Shelf/secret.mp3").Should().BeNull();
        _bed.Root.OpenRead(_bed.Root.RootId, "Shelf/secret.mp3").Should().BeNull();
    }

    [Fact]
    public void A_symlink_that_stays_inside_the_root_is_still_refused()
    {
        _bed.WriteSource("real.mp3", "audio");
        File.CreateSymbolicLink(
            Path.Combine(_bed.ExternalRoot, "alias.mp3"),
            Path.Combine(_bed.ExternalRoot, "real.mp3"));

        _bed.Root.OpenRead(_bed.Root.RootId, "alias.mp3").Should().BeNull();
    }

    [Fact]
    public void A_directory_is_not_a_file()
    {
        Directory.CreateDirectory(Path.Combine(_bed.ExternalRoot, "Author"));

        _bed.Root.Find(_bed.Root.RootId, "Author").Should().BeNull();
        _bed.Root.OpenRead(_bed.Root.RootId, "Author").Should().BeNull();
    }

    [Fact]
    public void A_reference_for_another_root_reads_nothing()
    {
        _bed.WriteSource("book.m4b", "audio");

        _bed.Root.OpenRead("some-other-root", "book.m4b").Should().BeNull();
    }

    [Fact]
    public void An_unconfigured_root_is_unavailable_and_reads_nothing()
    {
        var root = new ExternalMediaRoot(Options.Create(new ExternalMediaOptions()));

        root.IsConfigured.Should().BeFalse();
        root.IsMounted.Should().BeFalse();
        root.OpenRead(root.RootId, "book.m4b").Should().BeNull();
    }

    [Fact]
    public void A_configured_root_that_is_not_mounted_is_configured_but_not_mounted()
    {
        var root = new ExternalMediaRoot(Options.Create(new ExternalMediaOptions
        {
            Root = Path.Combine(_bed.Outside, "not-mounted"),
        }));

        root.IsConfigured.Should().BeTrue();
        root.IsMounted.Should().BeFalse();
        root.OpenRead(root.RootId, "book.m4b").Should().BeNull();
    }

    [Fact]
    public void A_relative_root_is_a_configuration_error()
    {
        var act = () => new ExternalMediaRoot(Options.Create(new ExternalMediaOptions { Root = "media" }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*absolute path*");
    }
}
