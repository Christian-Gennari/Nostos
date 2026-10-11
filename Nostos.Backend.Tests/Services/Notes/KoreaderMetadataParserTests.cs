using FluentAssertions;
using Nostos.Backend.Services.Notes.Imports;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

public sealed class KoreaderMetadataParserTests
{
    [Fact]
    public void Pdf_sidecar_keeps_every_highlight_past_its_nested_position_tables()
    {
        var document = KoreaderMetadataParser.Parse(Fixture("metadata.pdf.lua"));

        document.Title.Should().Be("Synthetic Reader PDF");
        document.PartialMd5.Should().Be("0123456789abcdef0123456789abcdef");
        document.Annotations.Should().HaveCount(2);

        var first = document.Annotations[0];
        first.Text.Should().Be("A synthetic PDF highlight.");
        first.PageNumber.Should().Be("14");
        first.PositionStart.Should().BeNull();
        first.PositionEnd.Should().BeNull();
        KoreaderNoteImportService.Anchor(first).Should().Be(("koreader_page", "14"));

        var second = document.Annotations[1];
        second.Text.Should().Be("A second synthetic PDF highlight.");
        second.Note.Should().Be("A thought about the second passage.");
        KoreaderNoteImportService.Anchor(second).Should().Be(("koreader_page", "15"));

        // The line-based reader stored this one as its note alone, without the text.
        KoreaderNoteImportService
            .EarlierShapes(second, second.Text, second.Note!)
            .Should().ContainSingle()
            .Which.Should().Be(new ImportedNoteShape("koreader_page", "15", null, "A thought about the second passage."));
    }

    [Fact]
    public void Pdf_without_embedded_metadata_is_named_by_what_the_device_shows()
    {
        var document = KoreaderMetadataParser.Parse(Fixture("metadata.pdf-untitled.lua"));

        document.Title.Should().Be("Ada Reader - Synthetic Reader PDF");
        document.Author.Should().BeNull("\"N/A\" is KOReader's placeholder, not an author");
        document.Annotations.Should().HaveCount(2);
    }

    [Fact]
    public void File_name_names_the_book_when_nothing_else_does()
    {
        var document = KoreaderMetadataParser.Parse(
            "return { [\"doc_path\"] = \"/mnt/onboard/Books/Some Book.v2.pdf\", [\"doc_props\"] = {} }");

        document.Title.Should().Be("Some Book.v2");
    }

    [Fact]
    public void Highlight_across_a_paragraph_break_keeps_its_whole_text()
    {
        var document = KoreaderMetadataParser.Parse(Fixture("metadata.multiline.lua"));

        document.Annotations.Should().HaveCount(3);
        document.Annotations[0].Text.Should().Be("The end of one paragraph.\nThe start of the next.");
        document.Annotations[0].Note.Should().Be("She said \"wait\" here.");
        document.Annotations[0].PositionEnd.Should().Be("/body/DocFragment[3]/body/p[5]/text().21");
        document.Annotations[1].Text.Should().Be("A later highlight.");
    }

    [Fact]
    public void Epub_sidecar_reads_as_before()
    {
        var document = KoreaderMetadataParser.Parse(Fixture("metadata.lua"));

        document.Title.Should().Be("Synthetic Reader Book");
        document.Author.Should().Be("Ada Reader");
        document.Annotations.Should().HaveCount(2);
        KoreaderNoteImportService.Anchor(document.Annotations[0]).Should().Be((
            "koreader_xpointer",
            "/body/DocFragment[3]/body/p[4]/text().0|/body/DocFragment[3]/body/p[4]/text().33"));
        KoreaderNoteImportService.Anchor(document.Annotations[1]).Should().Be(("koreader_page", "27"));
    }

    [Fact]
    public void Page_bookmark_in_a_reflowable_book_keeps_its_xpointer_not_its_screen_count()
    {
        var bookmark = KoreaderMetadataParser.Parse(Fixture("metadata.multiline.lua")).Annotations[2];

        KoreaderNoteImportService.Anchor(bookmark).Should().Be((
            "koreader_xpointer",
            "/body/DocFragment[3]/body/p[12]/text().0|"));
    }

    [Theory]
    [InlineData("return {")]
    [InlineData("return { [\"doc_props\"] = { [\"title\"] = \"Unclosed }")]
    [InlineData("SQLite format 3")]
    [InlineData("return { [\"doc_props\"] = {}, [\"stats\"] = { [\"title\"] = \"N/A\" } }")]
    public void Malformed_input_is_a_format_error(string source)
    {
        var parse = () => KoreaderMetadataParser.Parse(source);

        parse.Should().Throw<FormatException>();
    }

    [Fact]
    public void Runaway_nesting_is_refused_rather_than_recursed()
    {
        var parse = () => KoreaderMetadataParser.Parse("return " + new string('{', 200_000));

        parse.Should().Throw<FormatException>();
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "koreader", name));
}
