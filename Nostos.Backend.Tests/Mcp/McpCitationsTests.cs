using FluentAssertions;
using Nostos.Backend.Integrations.Mcp;
using Xunit;

namespace Nostos.Backend.Tests.Mcp;

public sealed class McpCitationsTests
{
    private static readonly Guid Book = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void A_citation_link_is_the_Studios_own_with_reserved_characters_escaped_as_it_escapes_them()
    {
        McpCitations.CiteHref(Book, 42, null, "quote", "abc").Should().Be($"nostos:cite/{Book:D}?p=42&as=quote&src=abc");
        McpCitations.CiteHref(Book, null, "c:Loomings (1)!", "quote", null)
            .Should().Be($"nostos:cite/{Book:D}?loc=c%3ALoomings%20%281%29%21&as=quote");
        McpCitations.CiteHref(Book, null, null, null, null).Should().Be($"nostos:cite/{Book:D}");
    }

    [Theory]
    [InlineData("pdf_page", "12", true, 12, null, "p. 12")]
    [InlineData("pdf_page", "12", false, null, null, null)]
    [InlineData("physical_page", "7", false, 7, null, "p. 7")]
    [InlineData("audio_timestamp", "3725.4", true, null, "t:1:02:05", "1:02:05")]
    [InlineData("audio_timestamp", "65", true, null, "t:1:05", "1:05")]
    [InlineData("audio_timestamp", "65", false, null, null, null)]
    [InlineData("epub_cfi", "epubcfi(/6/4!/4/2)", true, null, null, null)]
    public void A_notes_place_is_cited_only_when_Nostos_knows_it(
        string kind, string value, bool verified, int? page, string? loc, string? label)
    {
        McpCitations.NoteLocator(kind, value, verified).Should().Be((page, loc, label));
    }

    [Fact]
    public void Link_text_is_one_line_with_markdown_and_brackets_escaped()
    {
        McpCitations.EscapeText("A *bold*\n claim [sic] with_under `code` \\ end")
            .Should().Be(@"A \*bold\* claim \[sic\] with\_under \`code\` \\ end");
    }

    [Fact]
    public void A_chapter_locator_follows_the_Studios_rules()
    {
        McpCitations.CleanLoc("c:  Loomings ").Should().Be("c: Loomings");
        McpCitations.CleanLoc("x:Loomings").Should().BeNull();
        McpCitations.CleanLoc("c:" + new string('a', 400)).Should().BeNull();
    }

    [Fact]
    public void The_same_note_resent_has_the_same_key_and_a_different_note_does_not()
    {
        var key = NostosMcpTools.IdempotencyKey(Book, "A  thought", null, null, ["Time", "memory"]);

        NostosMcpTools.IdempotencyKey(Book, " A thought ", null, null, ["Memory", "time"]).Should().Be(key);
        NostosMcpTools.IdempotencyKey(Book, "Another thought", null, null, ["Time", "memory"]).Should().NotBe(key);
        NostosMcpTools.IdempotencyKey(Guid.NewGuid(), "A thought", null, null, ["Time", "memory"]).Should().NotBe(key);
    }
}
