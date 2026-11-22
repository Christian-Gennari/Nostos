using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// What “named” means for a topic the reader already gave a kind (issue #1770
/// review). A substring match marked the entry point's own prefilled request as
/// naming topics called Man, Topic, Kind and US, so their reader kinds were
/// replaced by the classification they were meant to be left out of.
/// </summary>
public sealed class AssistantNamedTopicsTests
{
    /// <summary>The composer's prefilled sort request, word for word.</summary>
    private const string SortRequest =
        "Give a kind to my topics that have none. Use the kinds I have; propose a new kind only if many topics need it.";

    [Theory]
    [InlineData("Man")]
    [InlineData("Topic")]
    [InlineData("Kind")]
    [InlineData("US")]
    public void The_prefilled_request_names_no_topic(string topicName)
    {
        AssistantNamedTopics.Names(SortRequest, topicName).Should().BeFalse();
    }

    [Fact]
    public void A_topic_inside_a_longer_word_is_not_named()
    {
        AssistantNamedTopics.Names("A reading list, in particular the Vienna school.", "Art")
            .Should().BeFalse();
        AssistantNamedTopics.Names("He walked toward the library.", "War").Should().BeFalse();
    }

    [Fact]
    public void A_request_scaffolding_word_is_never_taken_as_a_topic_name()
    {
        var kindTopic = Guid.NewGuid();
        var freire = Guid.NewGuid();
        var topics = new Dictionary<Guid, Nostos.Backend.Data.Models.TopicModel>
        {
            [kindTopic] = new() { Id = kindTopic, Topic = "Kind" },
            [freire] = new() { Id = freire, Topic = "Freire" },
        };

        // "a kind" is the request talking about kinds, even if a model lists it.
        AssistantNamedTopics.Names("Give my topics a kind.", "Kind").Should().BeFalse();
        AssistantNamedTopics.Verified([kindTopic], topics, "Give my topics a kind.").Should().BeEmpty();

        // A real name the model lists is confirmed by the words being there.
        AssistantNamedTopics.Verified([freire], topics, "Freire is an Idea.").Should().Equal(freire);
    }

    [Fact]
    public void A_whole_word_is_named_whatever_its_case()
    {
        AssistantNamedTopics.Names("Freire is an Idea, not a Person.", "Freire").Should().BeTrue();
        AssistantNamedTopics.Names("Put freire among the Ideas.", "Freire").Should().BeTrue();
        AssistantNamedTopics.Names("Move “Paulo Freire” to Person.", "Paulo Freire").Should().BeTrue();
    }

    [Fact]
    public void Accents_are_folded_the_way_the_library_folds_names()
    {
        AssistantNamedTopics.Names("Café is a Place.", "Café").Should().BeTrue();
        AssistantNamedTopics.Names("café is a Place.", "Café").Should().BeTrue();
        // Composed and decomposed forms are the same name.
        AssistantNamedTopics.Names("Cafe\u0301 is a Place.", "Café").Should().BeTrue();
        // Folding is not accent stripping: "cafe" is a different name.
        AssistantNamedTopics.Names("The cafe on the corner.", "Café").Should().BeFalse();
    }

    [Fact]
    public void Punctuation_around_a_name_does_not_stop_it_being_named()
    {
        AssistantNamedTopics.Names("Sort (Kierkegaard), please.", "Kierkegaard").Should().BeTrue();
        AssistantNamedTopics.Names("Is Gent—the city—a Place?", "Gent").Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("US")]
    [InlineData("AI")]
    [InlineData("A")]
    [InlineData("42")]
    public void An_empty_or_too_short_name_is_never_named(string topicName)
    {
        AssistantNamedTopics.Names("US AI A 42", topicName).Should().BeFalse();
    }

    [Fact]
    public void A_topic_the_model_named_is_kept_only_when_the_words_agree()
    {
        var freire = Guid.NewGuid();
        var montessori = Guid.NewGuid();
        var topics = new Dictionary<Guid, Nostos.Backend.Data.Models.TopicModel>
        {
            [freire] = new() { Id = freire, Topic = "Freire" },
            [montessori] = new() { Id = montessori, Topic = "Montessori" },
        };

        var verified = AssistantNamedTopics.Verified(
            [freire, montessori, Guid.NewGuid()],
            topics,
            "Freire is an Idea.");

        verified.Should().Equal(freire);
    }

    [Fact]
    public void A_model_passed_id_verifies_a_short_or_numeric_name_the_words_contain()
    {
        var ai = Guid.NewGuid();
        var nineteenEightyFour = Guid.NewGuid();
        var topics = new Dictionary<Guid, Nostos.Backend.Data.Models.TopicModel>
        {
            [ai] = new() { Id = ai, Topic = "AI" },
            [nineteenEightyFour] = new() { Id = nineteenEightyFour, Topic = "1984" },
        };

        // The three-letter minimum is a guessing rule. Once the model has named
        // the id, the whole token in the reader's text is enough.
        AssistantNamedTopics.Names("Sort AI and 1984 into kinds.", "AI").Should().BeFalse();
        AssistantNamedTopics.Verified([ai, nineteenEightyFour], topics, "Sort AI and 1984 into kinds.")
            .Should().BeEquivalentTo([ai, nineteenEightyFour]);

        // Still a whole token, not a substring of a longer one.
        AssistantNamedTopics.Verified([ai], topics, "AIs are models.").Should().BeEmpty();
    }

    [Fact]
    public void A_model_passed_id_whose_name_is_not_in_the_text_is_refused()
    {
        var freire = Guid.NewGuid();
        var topics = new Dictionary<Guid, Nostos.Backend.Data.Models.TopicModel>
        {
            [freire] = new() { Id = freire, Topic = "Freire" },
        };

        AssistantNamedTopics.Verified([freire], topics, "Give the unkinded topics a kind.").Should().BeEmpty();
    }

    [Fact]
    public void An_omitted_named_list_never_guesses_from_the_prose()
    {
        var freire = Guid.NewGuid();
        var topics = new Dictionary<Guid, Nostos.Backend.Data.Models.TopicModel>
        {
            [freire] = new() { Id = freire, Topic = "Freire" },
        };

        // The prose contains the name, but the model did not say the reader
        // named it: an omitted list means no topic was named, so the reader
        // kind is protected.
        AssistantNamedTopics.Verified(null, topics, "Freire is an Idea.").Should().BeEmpty();
    }
}
