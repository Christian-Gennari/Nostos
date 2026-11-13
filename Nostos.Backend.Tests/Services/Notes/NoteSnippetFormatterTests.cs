using FluentAssertions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Notes;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// The snippet rules extracted from NoteService (issue #1619): a row shows the
/// fragment around the match, preferring the quotation, and flattens whitespace
/// so a multi-line note reads as one line.
/// </summary>
public sealed class NoteSnippetFormatterTests
{
    private readonly NoteSnippetFormatter _formatter = new();

    [Fact]
    public void Blank_text_has_no_snippet()
    {
        _formatter.Snippet("   ", null, null).Should().BeNull();
        _formatter.Snippet(string.Empty, string.Empty, "term").Should().BeNull();
    }

    [Fact]
    public void The_quotation_is_preferred_over_the_note_body()
    {
        _formatter.Snippet("the note body", "the quoted passage", null)
            .Should().Be("the quoted passage");
    }

    [Fact]
    public void Short_text_is_flattened_and_returned_whole()
    {
        _formatter.Snippet("  one\n\n two\tthree  ", null, null)
            .Should().Be("one two three");
    }

    [Fact]
    public void A_long_text_with_no_match_shows_its_opening()
    {
        var content = new string('a', 300);

        var snippet = _formatter.Snippet(content, null, "absent");

        snippet.Should().Be(new string('a', 157) + "…");
    }

    [Fact]
    public void A_long_text_is_centred_on_the_match()
    {
        var content = new string('x', 150) + "needle" + new string('y', 150);

        var snippet = _formatter.Snippet(content, null, "needle")!;

        snippet.Should().StartWith("…").And.EndWith("…");
        snippet.Should().Contain("needle");
        snippet.Length.Should().Be(159);
    }

    [Fact]
    public void A_term_at_the_start_is_not_padded_with_an_ellipsis()
    {
        var content = "needle" + new string('y', 300);

        var snippet = _formatter.Snippet(content, null, "needle")!;

        snippet.Should().StartWith("needle").And.EndWith("…");
    }

    [Fact]
    public void The_recorded_term_is_kept_when_the_note_holds_it()
    {
        var note = Note(content: "The will to power", selectedText: null);

        _formatter.TermFor(note, "will to power", ["will", "power"])
            .Should().Be("will to power");
    }

    [Fact]
    public void A_term_the_note_does_not_hold_falls_back_to_a_token_it_does()
    {
        var note = Note(content: "The will to power", selectedText: null);

        _formatter.TermFor(note, "schopenhauer will", ["schopenhauer", "will"])
            .Should().Be("will");
    }

    [Fact]
    public void The_recorded_term_survives_when_no_token_is_in_the_note()
    {
        var note = Note(content: "The will to power", selectedText: null);

        _formatter.TermFor(note, "schopenhauer", ["schopenhauer"])
            .Should().Be("schopenhauer");
    }

    [Fact]
    public void A_token_found_only_in_the_quotation_still_counts()
    {
        var note = Note(content: "a note", selectedText: "the quoted passage");

        _formatter.TermFor(note, "absent phrase", ["quoted"])
            .Should().Be("quoted");
    }

    private static NoteModel Note(string content, string? selectedText) => new()
    {
        Id = Guid.NewGuid(),
        BookId = Guid.NewGuid(),
        Content = content,
        SelectedText = selectedText,
    };
}
