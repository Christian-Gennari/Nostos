using FluentAssertions;
using Nostos.Backend.Services.Library;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// The rule that gives a legacy physical book with a file its digital type,
/// shared by the <c>ConvertLegacyPhysicalBooksWithFiles</c> migration (whose SQL
/// is asserted against this in the migration test) and the portable restore.
/// </summary>
public sealed class LegacyPhysicalFileBookTests
{
    [Theory]
    [InlineData("book.pdf", LegacyPhysicalFileBook.Resolution.Ebook)]
    [InlineData("book.PDF", LegacyPhysicalFileBook.Resolution.Ebook)]
    [InlineData("book.epub", LegacyPhysicalFileBook.Resolution.Ebook)]
    [InlineData("book.TXT", LegacyPhysicalFileBook.Resolution.Ebook)]
    [InlineData("book.mobi", LegacyPhysicalFileBook.Resolution.Ebook)]
    [InlineData("book.azw3", LegacyPhysicalFileBook.Resolution.Ebook)]
    [InlineData("WolfSusanne_2010_Meaning-in-Life.pdf", LegacyPhysicalFileBook.Resolution.Ebook)]
    [InlineData("book.mp3", LegacyPhysicalFileBook.Resolution.Audiobook)]
    [InlineData("book.M4A", LegacyPhysicalFileBook.Resolution.Audiobook)]
    [InlineData("book.m4b", LegacyPhysicalFileBook.Resolution.Audiobook)]
    public void A_known_book_extension_decides_the_format(string fileName, LegacyPhysicalFileBook.Resolution expected) =>
        LegacyPhysicalFileBook.Resolve(fileName, lastLocation: null).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("book.bin")]
    [InlineData("book")]
    [InlineData("book.pdf.exe")]
    public void A_name_without_a_supported_book_extension_is_not_guessed(string? fileName) =>
        LegacyPhysicalFileBook.Resolve(fileName, lastLocation: null)
            .Should().Be(LegacyPhysicalFileBook.Resolution.LeavePhysical);

    [Theory]
    [InlineData("{\"pageNumber\":122,\"yPercent\":0,\"rects\":[]}", "book.pdf", LegacyPhysicalFileBook.Resolution.Ebook)]
    [InlineData("epubcfi(/6/24!/4/66/3:716)", "book.epub", LegacyPhysicalFileBook.Resolution.Ebook)]
    [InlineData("3721.5", "book.m4b", LegacyPhysicalFileBook.Resolution.Audiobook)]
    [InlineData(null, "book.epub", LegacyPhysicalFileBook.Resolution.Ebook)]
    public void A_digital_reading_position_does_not_stop_the_conversion(
        string? lastLocation,
        string fileName,
        LegacyPhysicalFileBook.Resolution expected) =>
        LegacyPhysicalFileBook.Resolve(fileName, lastLocation).Should().Be(expected);

    [Theory]
    [InlineData("{\"page\":143}")]
    [InlineData("{\"page\":0}")]
    [InlineData("{ \"page\" : 143 }")]
    public void Paper_progress_keeps_the_row_physical(string lastLocation)
    {
        LegacyPhysicalFileBook.IsPaperProgress(lastLocation).Should().BeTrue();
        LegacyPhysicalFileBook.Resolve("book.pdf", lastLocation)
            .Should().Be(LegacyPhysicalFileBook.Resolution.LeavePhysical);
    }

    [Theory]
    [InlineData("{\"pageNumber\":12}")]
    [InlineData("{\"pages\":12}")]
    [InlineData("epubcfi(/6/4)")]
    [InlineData("")]
    [InlineData(null)]
    public void Digital_progress_is_not_mistaken_for_paper(string? lastLocation) =>
        LegacyPhysicalFileBook.IsPaperProgress(lastLocation).Should().BeFalse();

    [Fact]
    public void A_restore_converts_by_the_file_even_when_the_position_is_paper()
    {
        // The import has no third option: it must not refuse an older archive,
        // and it must not leave a physical book holding a file. The position
        // travels untouched.
        LegacyPhysicalFileBook.ResolveForRestore("book.pdf")
            .Should().Be(LegacyPhysicalFileBook.Resolution.Ebook);
        LegacyPhysicalFileBook.ResolveForRestore("book.mp3")
            .Should().Be(LegacyPhysicalFileBook.Resolution.Audiobook);
        LegacyPhysicalFileBook.ResolveForRestore("book.bin")
            .Should().Be(LegacyPhysicalFileBook.Resolution.LeavePhysical);
    }
}
