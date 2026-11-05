using FluentAssertions;
using Nostos.Backend.Services.ExternalMedia;
using Xunit;

namespace Nostos.Backend.Tests.ExternalMedia;

public sealed class ExternalMediaNamingTests
{
    [Theory]
    [InlineData("Author/Book/chapter_01_the_beginning.mp3", "chapter 01 the beginning")]
    [InlineData("Docs/My__Report  final.pdf", "My Report final")]
    [InlineData("Plain Title.epub", "Plain Title")]
    [InlineData("___.mp3", "___")]
    public void File_name_titles_read_as_text(string path, string expected) =>
        ExternalMediaNaming.TitleFromFileName(path).Should().Be(expected);
}
