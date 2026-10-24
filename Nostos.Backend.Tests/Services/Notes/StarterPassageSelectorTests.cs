using FluentAssertions;
using Nostos.Backend.Services.Notes.Imports;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

public sealed class StarterPassageSelectorTests
{
    private static readonly Guid BookA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid BookB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid BookC = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly DateTime Start = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static int _next;

    private static StarterPassageCandidate Candidate(Guid book, string? text, string? note = null, int minute = 0) =>
        new(Guid.Parse($"00000000-0000-0000-0001-{Interlocked.Increment(ref _next):D12}"), book, text, note, Start.AddMinutes(minute));

    private const string Long = "The unexamined life is not worth living for a human being";

    [Fact]
    public void A_highlight_with_a_note_comes_before_one_without()
    {
        var plain = Candidate(BookA, Long, minute: 0);
        var noted = Candidate(BookA, Long, "Socrates, at his trial.", minute: 5);

        StarterPassageSelector.Pick([plain, noted], 1).Should().Equal(noted);
    }

    [Fact]
    public void A_fragment_of_a_few_words_gives_way_to_a_real_passage_even_with_a_note()
    {
        var fragment = Candidate(BookA, "Call me Ishmael.", "Opening line.");
        var passage = Candidate(BookA, Long);

        StarterPassageSelector.Pick([fragment, passage], 1).Should().Equal(passage);
    }

    [Fact]
    public void A_very_long_highlight_gives_way_to_one_of_a_comfortable_length()
    {
        var wall = Candidate(BookA, string.Join(' ', Enumerable.Repeat("word", 400)));
        var comfortable = Candidate(BookA, Long, minute: 9);

        StarterPassageSelector.Pick([wall, comfortable], 1).Should().Equal(comfortable);
    }

    [Fact]
    public void Each_book_gives_its_best_passage_before_any_book_gives_a_second()
    {
        var a1 = Candidate(BookA, Long, "Note A1.", 0);
        var a2 = Candidate(BookA, Long, "Note A2.", 1);
        var a3 = Candidate(BookA, Long, "Note A3.", 2);
        var b1 = Candidate(BookB, Long, minute: 3);
        var c1 = Candidate(BookC, Long, minute: 4);

        StarterPassageSelector.Pick([a1, a2, a3, b1, c1]).Should().Equal(a1, b1, c1);
    }

    [Fact]
    public void One_book_fills_the_draft_when_it_is_the_only_one()
    {
        var a1 = Candidate(BookA, Long, minute: 0);
        var a2 = Candidate(BookA, Long, "Kept for this.", 1);
        var a3 = Candidate(BookA, Long, minute: 2);
        var a4 = Candidate(BookA, Long, minute: 3);

        StarterPassageSelector.Pick([a1, a2, a3, a4]).Should().Equal(a2, a1, a3);
    }

    [Fact]
    public void Two_books_share_three_places_and_the_better_second_passage_takes_the_last()
    {
        var a1 = Candidate(BookA, Long, "A note.", 0);
        var a2 = Candidate(BookA, Long, "Another note.", 1);
        var b1 = Candidate(BookB, Long, minute: 2);
        var b2 = Candidate(BookB, Long, minute: 3);

        StarterPassageSelector.Pick([a1, a2, b1, b2]).Should().Equal(a1, b1, a2);
    }

    [Fact]
    public void A_note_without_highlighted_text_cannot_be_quoted_and_is_never_chosen()
    {
        var noteOnly = Candidate(BookA, "   ", "Only my own words.");
        var empty = Candidate(BookB, null, "Also mine.");
        var quoted = Candidate(BookC, "Short one.");

        StarterPassageSelector.Pick([noteOnly, empty, quoted]).Should().Equal(quoted);
    }

    [Fact]
    public void Nothing_to_choose_from_chooses_nothing()
    {
        StarterPassageSelector.Pick([]).Should().BeEmpty();
    }

    [Fact]
    public void The_same_highlights_always_give_the_same_draft()
    {
        var candidates = Enumerable.Range(0, 12)
            .Select(i => Candidate(i % 2 == 0 ? BookA : BookB, Long, i % 3 == 0 ? "Noted." : null, i))
            .ToList();

        var first = StarterPassageSelector.Pick(candidates);
        var shuffled = StarterPassageSelector.Pick(candidates.AsEnumerable().Reverse());

        shuffled.Should().Equal(first);
    }
}
