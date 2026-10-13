using FluentAssertions;
using Nostos.Backend.Services.Library;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

public sealed class LibrarySearchMatcherTests
{
    private sealed record Book(string Title, string? Author);

    private static readonly Book[] Shelf =
    [
        new("The Hobbit", "J.R.R. Tolkien"),
        new("Wuthering Heights", "Emily Brontë"),
        new("Catch-22", "Joseph Heller"),
        new("Röda rummet", "August Strindberg"),
        new("The Lord of the Rings", "J.R.R. Tolkien"),
        new("King Lear", null),
    ];

    private static string[] Search(string? search) =>
        LibrarySearchMatcher.Match(search, Shelf, b => b.Title, b => b.Author)
            .Select(b => b.Title)
            .ToArray();

    [Theory]
    [InlineData("hobbit")]
    [InlineData("HOBBIT")]
    [InlineData("hObBiT")]
    [InlineData("  hobbit  ")]
    public void Case_and_surrounding_space_do_not_matter(string search) =>
        Search(search).Should().Equal("The Hobbit");

    [Theory]
    [InlineData("bronte", "Wuthering Heights")]
    [InlineData("BRONTË", "Wuthering Heights")]
    [InlineData("roda rummet", "Röda rummet")]
    [InlineData("RÖDA", "Röda rummet")]
    public void Accents_do_not_matter(string search, string expected) =>
        Search(search).Should().Equal(expected);

    [Theory]
    [InlineData("catch 22")]
    [InlineData("catch-22")]
    [InlineData("heller catch")]
    public void Punctuation_and_word_order_do_not_matter(string search) =>
        Search(search).Should().Equal("Catch-22");

    [Fact]
    public void Words_may_come_from_title_and_author_together() =>
        Search("tolkien rings").Should().Equal("The Lord of the Rings");

    [Fact]
    public void A_partial_word_still_matches_as_it_did_with_LIKE() =>
        Search("tolk").Should().Equal("The Hobbit", "The Lord of the Rings");

    [Theory]
    [InlineData("hobit", "The Hobbit")]          // dropped letter
    [InlineData("hobbti", "The Hobbit")]         // swapped neighbours
    [InlineData("wuthring", "Wuthering Heights")] // dropped letter, long word
    [InlineData("strindbreg", "Röda rummet")]    // swap in the author
    [InlineData("wutering hei", "Wuthering Heights")] // typo plus a word still being typed
    public void A_typo_still_finds_the_book(string search, string expected) =>
        Search(search).Should().Equal(expected);

    [Fact]
    public void Typo_tolerance_is_only_a_fallback()
    {
        // "king" is one letter from "ring(s)", but a literal hit exists, so the
        // near miss must not be added to it.
        Search("king").Should().Equal("King Lear");
    }

    [Theory]
    [InlineData("ling")]   // one edit from "king" and "ring", wrong first letter
    [InlineData("xyzzy")]
    [InlineData("hob xyzzy")] // every word has to match
    public void Unrelated_text_matches_nothing(string search) =>
        Search(search).Should().BeEmpty();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("%")] // was a LIKE wildcard that matched every book
    [InlineData("_")]
    public void A_search_with_no_letters_or_digits_matches_nothing(string? search) =>
        Search(search).Should().BeEmpty();
}
