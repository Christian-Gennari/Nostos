using FluentAssertions;
using Nostos.Backend.Services;
using Xunit;

namespace Nostos.Backend.Tests.Services;

public sealed class TopicReferencesTests
{
    [Theory]
    [InlineData("", "virtue", "[[virtue]]")]
    [InlineData("A thought.", "virtue", "A thought.\n\n[[virtue]]")]
    [InlineData("A thought.\n\n", "virtue", "A thought.\n\n[[virtue]]")]
    [InlineData("A thought.\n\n[[ethics]]", "virtue", "A thought.\n\n[[ethics]]\n\n[[virtue]]")]
    public void Append_adds_the_reference_as_its_own_paragraph(string content, string name, string expected) =>
        TopicReferences.Append(content, name).Should().Be(expected);

    [Theory]
    [InlineData("Explores [[Virtue]] here.", "virtue")]
    [InlineData("[[ virtue ]]", "Virtue")]
    public void Append_leaves_a_note_that_already_declares_the_topic(string content, string name) =>
        TopicReferences.Append(content, name).Should().Be(content);

    [Theory]
    [InlineData("")]
    [InlineData("A thought.")]
    [InlineData("A thought.\n\n[[ethics]]")]
    [InlineData("[[ethics]]\n\nA thought.")]
    [InlineData("First.\n\nSecond paragraph.")]
    public void Removing_what_append_added_gives_back_the_original(string content)
    {
        var linked = TopicReferences.Append(content, "virtue");

        TopicReferences.Remove(linked, "virtue").Should().Be(content.TrimEnd());
    }

    [Fact]
    public void Remove_turns_a_reference_inside_prose_into_plain_words()
    {
        TopicReferences.Remove("This explores [[Stoicism]] and [[Virtue Ethics]].", "stoicism")
            .Should().Be("This explores Stoicism and [[Virtue Ethics]].");
    }

    [Fact]
    public void Remove_keeps_the_words_of_a_labelled_list()
    {
        TopicReferences.Remove("A note.\nConcepts: [[virtue]], [[mean]], [[choice]]", "mean")
            .Should().Be("A note.\nConcepts: [[virtue]], mean, [[choice]]");
    }

    [Theory]
    [InlineData("A note.\n\n[[a]] [[b]] [[c]]", "b", "A note.\n\n[[a]] [[c]]")]
    [InlineData("A note.\n\n[[a]], [[b]]", "b", "A note.\n\n[[a]]")]
    [InlineData("A note.\n\n[[a]], [[b]]", "a", "A note.\n\n[[b]]")]
    [InlineData("A note.\n\n[[a]]\n\n[[b]]\n\nMore.", "a", "A note.\n\n[[b]]\n\nMore.")]
    [InlineData("[[a]]\n\nA note.", "a", "A note.")]
    [InlineData("Line one\n[[a]]\nLine two", "a", "Line one\nLine two")]
    public void Remove_drops_a_reference_from_a_line_of_references(string content, string name, string expected) =>
        TopicReferences.Remove(content, name).Should().Be(expected);

    [Fact]
    public void Remove_matches_every_occurrence_trimmed_and_case_insensitively()
    {
        TopicReferences.Remove("[[ Virtue ]] is [[virtue]].\n\n[[VIRTUE]]", "virtue")
            .Should().Be("Virtue is virtue.");
    }

    [Fact]
    public void Remove_leaves_a_note_without_the_reference_untouched()
    {
        const string content = "Nothing linked here.\n\n[[ethics]]";

        TopicReferences.Remove(content, "virtue").Should().BeSameAs(content);
    }

    [Fact]
    public void Rename_rewrites_references_in_place()
    {
        TopicReferences.Rename("On [[virtue]].\n\n[[virtue]] [[mean]]", "Virtue", "arete")
            .Should().Be("On [[arete]].\n\n[[arete]] [[mean]]");
    }

    [Fact]
    public void Rename_into_a_topic_the_note_already_declares_does_not_repeat_it_on_a_reference_line()
    {
        TopicReferences.Rename("A note.\n\n[[Stoics]] [[Stoicism]]", "Stoics", "Stoicism")
            .Should().Be("A note.\n\n[[Stoicism]]");
    }

    [Fact]
    public void Names_lists_each_declared_topic_once()
    {
        TopicReferences.Names("[[a]] and [[ A ]] and [[b]] and [[ ]]")
            .Should().Equal("a", "b");
    }
}
