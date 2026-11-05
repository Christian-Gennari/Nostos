using FluentAssertions;
using Nostos.Backend.Services.Library;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

public sealed class EditionMatcherTests
{
    [Theory]
    [InlineData("Crime and punishment", "Crime and Punishment")]
    [InlineData("Les Misérables", "Les Miserables")]
    [InlineData("Pride & Prejudice", "Pride and Prejudice", false)]
    [InlineData("Pride and Prejudice!", "pride  and   prejudice")]
    [InlineData("Little Women", "Little Women; Or, Meg, Jo, Beth, and Amy")]
    [InlineData("Meditations: A New Translation", "Meditations")]
    [InlineData("Dune: Messiah", "Dune: Messiah")]
    [InlineData("Dune: Messiah", "Dune : messiah!")]
    [InlineData("Dune: Messiah", "Dune: Chapterhouse", false)]
    [InlineData("Dune", "Dune Messiah", false)]
    [InlineData("Ethics", "Ethics and Politics", false)]
    [InlineData("", "", false)]
    [InlineData(null, "Dune", false)]
    public void SameTitle_ignores_case_accents_punctuation_and_one_sided_subtitle(
        string? a, string? b, bool expected = true)
    {
        EditionMatcher.SameTitle(a, b).Should().Be(expected);
        EditionMatcher.SameTitle(b, a).Should().Be(expected);
    }

    [Theory]
    [InlineData("Little Women", "Little Women; Or, Meg, Jo, Beth, and Amy", true)]
    [InlineData("Frankenstein", "Frankenstein; or, The Modern Prometheus", true)]
    [InlineData("Frankenstein", "Frankenstein, Or The Modern Prometheus", true)]
    [InlineData("Crime and punishment", "Crime and Punishment", true)]
    [InlineData("Dune", "Dune: Messiah", false)]
    [InlineData("Meditations", "Meditations: A New Translation", false)]
    [InlineData("Dune", "Dune; Messiah", false)]
    [InlineData("Orwell", "Orlando", false)]
    public void Auto_grouping_ignores_only_an_or_alternative_title(string a, string b, bool expected)
    {
        EditionMatcher.IsAutoMatch(a, "Same Author", b, "Same Author").Should().Be(expected);
        EditionMatcher.IsAutoMatch(b, "Same Author", a, "Same Author").Should().Be(expected);
    }

    [Fact]
    public void A_subtitle_is_offered_never_auto_grouped()
    {
        EditionMatcher.IsAutoMatch("Dune", "Frank Herbert", "Dune: Messiah", "Frank Herbert").Should().BeFalse();
        EditionMatcher.IsSuggestion("Dune", "Frank Herbert", "Dune: Messiah", "Frank Herbert").Should().BeTrue();
    }

    [Fact]
    public void AutoMatch_joins_on_loose_title_and_case_accent_punctuation_insensitive_author()
    {
        EditionMatcher.IsAutoMatch("Little Women", "Louisa May Alcott", "Little Women; Or, Meg, Jo", "louisa may  alcott")
            .Should().BeTrue();
        EditionMatcher.IsAutoMatch("Pride and Prejudice", "Jane Austen", "Pride and Prejudice", "Jane Austen")
            .Should().BeTrue();
        EditionMatcher.IsAutoMatch("Les Misérables", "Victor Hugo", "Les Miserables", "Víctor Hugo")
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("Marcus Aurelius", "Emperor of Rome Marcus Aurelius")]
    [InlineData("Fiódor Dostoievski", "Fyodor Dostoyevsky")]
    [InlineData("Mary Shelly", "Mary Shelley")]
    [InlineData("Austen, Jane", "Jane Austen")]
    public void AutoMatch_never_joins_on_fuzzy_authors(string a, string b) =>
        EditionMatcher.IsAutoMatch("Same Title", a, "Same Title", b).Should().BeFalse();

    [Theory]
    [InlineData("Marcus Aurelius", "Emperor of Rome Marcus Aurelius")]
    [InlineData("Fiódor Dostoievski", "Fyodor Dostoyevsky")]
    [InlineData("Mary Shelly", "Mary Shelley")]
    [InlineData("Austen, Jane", "Jane Austen")]
    [InlineData("Jane Austen", "Jane Austen")]
    public void Suggestion_offers_a_link_for_authors_spelled_differently(string a, string b)
    {
        EditionMatcher.IsSuggestion("Meditations", a, "Meditations", b).Should().BeTrue();
        EditionMatcher.IsSuggestion("Meditations", b, "Meditations", a).Should().BeTrue();
    }

    [Theory]
    [InlineData("Jane Austen", "Jane Eyre")]
    [InlineData("Jane Austen", "John Austin")]
    [InlineData("A. Smith", "B. Smith")]
    [InlineData("Plato", "Aristotle")]
    public void Suggestion_ignores_different_authors(string a, string b) =>
        EditionMatcher.IsSuggestion("Same Title", a, "Same Title", b).Should().BeFalse();

    [Fact]
    public void Suggestion_needs_the_title_to_agree_and_both_authors_present()
    {
        EditionMatcher.IsSuggestion("Emma", "Jane Austen", "Persuasion", "Jane Austen").Should().BeFalse();
        EditionMatcher.IsSuggestion("Emma", null, "Emma", "Jane Austen").Should().BeFalse();
        EditionMatcher.IsSuggestion("Emma", "Jane Austen", "Emma", "").Should().BeFalse();
        EditionMatcher.IsSuggestion("Crime and punishment", "Fiódor Dostoievski", "Crime and Punishment", "Fyodor Dostoyevsky")
            .Should().BeTrue();
    }
}
