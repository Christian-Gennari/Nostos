using FluentAssertions;
using Nostos.Backend.Services.Writings;
using Xunit;

namespace Nostos.Backend.Tests.Services.Writings;

// Issue #1162: the parser that reads which notes a writing cites. It mirrors
// the Studio's parseCitationHref and must never throw on odd content.
public sealed class WritingCitationIndexTests
{
    private static readonly Guid Book = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NoteA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid NoteB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static string Cite(string text, string query) => $"[{text}](nostos:cite/{Book}{query})";

    [Fact]
    public void Reads_page_role_and_source_like_the_studio()
    {
        var parsed = WritingCitationIndex.ParseHref($"nostos:cite/{Book}?p=42&as=quote&src={NoteA}");

        parsed.Should().Be(new WritingCitationIndex.CitationRef(Book.ToString(), 42, NoteA.ToString(), "quote"));
    }

    [Theory]
    [InlineData("nostos:cite/")]
    [InlineData("nostos:cite/?src=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("nostos:cite/bad id with spaces?src=x")]
    [InlineData("https://example.com/?src=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("nostos:cites/abc")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_address_is_not_a_citation(string? href)
    {
        WritingCitationIndex.ParseHref(href).Should().BeNull();
    }

    [Fact]
    public void Odd_values_are_dropped_not_errors()
    {
        var parsed = WritingCitationIndex.ParseHref($"nostos:cite/{Book}?p=-3&as=shout&src=%ZZ&src={NoteA}");

        parsed.Should().NotBeNull();
        parsed!.Page.Should().BeNull();
        parsed.As.Should().BeNull("an unknown role is no role");
        parsed.Src.Should().BeNull("the first src wins, as URLSearchParams.get, and %ZZ is not an id");
    }

    [Fact]
    public void Malformed_links_and_invalid_src_ids_are_ignored()
    {
        var content = string.Join("\n\n",
            $"Half deleted [(Voltaire, 1992](nostos:cite/{Book}?src={NoteA}",
            $"Missing bracket (Voltaire)](nostos:cite/{Book}?src={NoteA})",
            $"Bare address nostos:cite/{Book}?src={NoteA}",
            Cite("(Not a guid)", "?src=not-a-guid"),
            Cite("(Empty guid)", $"?src={Guid.Empty}"),
            Cite("(Pasted from elsewhere)", "?src=<script>"),
            "[broken](nostos:cite/??&&==)");

        WritingCitationIndex.CitedNoteIds(content).Should().BeEmpty();
    }

    [Fact]
    public void A_citation_without_src_or_a_bibliography_entry_never_counts()
    {
        var content = string.Join("\n\n",
            Cite("(Voltaire, 1992, p. 42)", "?p=42"),
            $"[^1]: {Cite("Voltaire, *Candide* (Knopf, 1992).", $"?as=bib&src={NoteA}")}");

        WritingCitationIndex.CitedNoteIds(content).Should().BeEmpty();
    }

    [Fact]
    public void The_same_note_cited_several_times_counts_once()
    {
        var content = string.Join(" ",
            Cite("(Voltaire, 1992, p. 1)", $"?p=1&as=quote&src={NoteA}"),
            Cite("(Voltaire, 1992, p. 2)", $"?p=2&as=note&src={NoteA}"),
            Cite("(Voltaire, 1992)", $"?as=reference&src={NoteA.ToString().ToUpperInvariant()}"),
            Cite("(Leibniz)", $"?src={NoteB}"));

        WritingCitationIndex.CitedNoteIds(content).Should().BeEquivalentTo([NoteA, NoteB]);
    }

    [Fact]
    public void Escaped_text_and_an_html_round_trip_still_read()
    {
        var content = $"[\\[sic\\] *Candide*](nostos:cite/{Book}?p=3&amp;src={NoteA})";

        WritingCitationIndex.CitedNoteIds(content).Should().Equal(NoteA);
    }

    [Fact]
    public void A_citation_shown_as_code_is_not_a_use()
    {
        var content = string.Join("\n",
            "```markdown",
            Cite("(In a fence)", $"?src={NoteA}"),
            "```",
            $"Inline `{Cite("(In code)", $"?src={NoteA}")}` too.",
            Cite("(Real)", $"?src={NoteB}"));

        WritingCitationIndex.CitedNoteIds(content).Should().Equal(NoteB);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No citations here, only [[Optimism]] and [a link](https://example.com).")]
    public void Content_without_citations_cites_nothing(string? content)
    {
        WritingCitationIndex.CitedNoteIds(content).Should().BeEmpty();
    }
}
