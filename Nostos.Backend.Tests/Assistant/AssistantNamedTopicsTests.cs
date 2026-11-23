using FluentAssertions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Integrations.Assistant;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>
/// What “named” means for a topic the reader already gave a kind (issue #1770
/// review). A substring match marked the entry point's own prefilled request as
/// naming topics called Man, Topic, Kind and US, so their reader kinds were
/// replaced by the classification they were meant to be left out of.
///
/// Every case goes through <see cref="AssistantNamedTopics.Verified"/>, the one
/// path production uses: the model passes ids and each is checked against the
/// reader's own words.
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
        Verified(SortRequest, Named(topicName)).Should().BeEmpty();
    }

    [Fact]
    public void A_topic_inside_a_longer_word_is_not_named()
    {
        Verified("A reading list, in particular the Vienna school.", Named("Art"))
            .Should().BeEmpty();
        Verified("He walked toward the library.", Named("War")).Should().BeEmpty();
    }

    [Fact]
    public void A_request_scaffolding_word_is_never_taken_as_a_topic_name()
    {
        // "a kind" is the request talking about kinds, even if a model lists it.
        Verified("Give my topics a kind.", Named("Kind")).Should().BeEmpty();
    }

    [Fact]
    public void A_whole_word_is_named_whatever_its_case()
    {
        Verified("Freire is an Idea, not a Person.", Named("Freire")).Should().HaveCount(1);
        Verified("Put freire among the Ideas.", Named("Freire")).Should().HaveCount(1);
    }

    [Fact]
    public void Accents_are_folded_the_way_the_library_folds_names()
    {
        Verified("Café is a Place.", Named("Café")).Should().HaveCount(1);
        Verified("café is a Place.", Named("Café")).Should().HaveCount(1);
        // Composed and decomposed forms are the same name.
        Verified("Cafe\u0301 is a Place.", Named("Café")).Should().HaveCount(1);
        // Folding is not accent stripping: "cafe" is a different name.
        Verified("The cafe on the corner.", Named("Café")).Should().BeEmpty();
    }

    [Fact]
    public void Punctuation_around_a_name_does_not_stop_it_being_named()
    {
        Verified("Sort (Kierkegaard), please.", Named("Kierkegaard")).Should().HaveCount(1);
        Verified("Is Gent—the city—a Place?", Named("Gent")).Should().HaveCount(1);
    }

    [Theory]
    [InlineData("Paulo  Freire")]
    [InlineData("Paulo\tFreire")]
    [InlineData("Paulo\nFreire")]
    [InlineData("Paulo\r\nFreire")]
    public void Any_whitespace_between_the_words_of_a_name_still_names_it(string userText)
    {
        // A name of more than one word is matched as the one phrase it is, with
        // whatever whitespace the reader's own text has between the words.
        Verified(userText, Named("Paulo Freire")).Should().HaveCount(1);
    }

    [Fact]
    public void The_words_of_a_multi_word_name_must_still_be_one_whole_phrase()
    {
        // Every word present, but not together: the phrase is not in the text.
        Verified("Paulo wrote about Freire.", Named("Paulo Freire")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_is_never_named(string topicName)
    {
        Verified("US AI A 42", Named(topicName)).Should().BeEmpty();
    }

    [Fact]
    public void A_topic_the_model_named_is_kept_only_when_the_words_agree()
    {
        var freire = Guid.NewGuid();
        var montessori = Guid.NewGuid();

        var verified = Verified(
            "Freire is an Idea.",
            NamedIds((freire, "Freire"), (montessori, "Montessori")));

        verified.Should().Equal(freire);
    }

    [Fact]
    public void A_model_passed_id_verifies_a_short_or_numeric_name_the_words_contain()
    {
        var ai = Guid.NewGuid();
        var nineteenEightyFour = Guid.NewGuid();
        var montessori = Guid.NewGuid();

        // There is no minimum length: the model named the id, so the whole
        // token in the reader's text is enough, however short or numeric.
        Verified(
                "Sort AI and 1984 into kinds.",
                NamedIds(
                    (ai, "AI"),
                    (nineteenEightyFour, "1984"),
                    (montessori, "Montessori")))
            .Should()
            .BeEquivalentTo([ai, nineteenEightyFour]);

        // Still a whole token, not a substring of a longer one.
        Verified("AIs are models.", NamedIds((ai, "AI"))).Should().BeEmpty();
    }

    [Fact]
    public void A_model_passed_id_whose_name_is_not_in_the_text_is_refused()
    {
        Verified("Give the unkinded topics a kind.", Named("Freire")).Should().BeEmpty();
    }

    [Fact]
    public void An_omitted_named_list_never_guesses_from_the_prose()
    {
        // The prose contains the name, but the model did not say the reader
        // named it: an omitted list means no topic was named, so the reader
        // kind is protected.
        AssistantNamedTopics
            .Verified(null, Topics(Named("Freire")), topic => topic.Topic, "Freire is an Idea.")
            .Should()
            .BeEmpty();
    }

    // ------------------------------------------------------------------

    /// <summary>One topic the model says was named, under a name it gives.</summary>
    private static (Guid[] Ids, Dictionary<Guid, TopicModel> Topics) Named(string name) =>
        NamedIds((Guid.NewGuid(), name));

    /// <summary>Topics with fixed ids the model says were named.</summary>
    private static (Guid[] Ids, Dictionary<Guid, TopicModel> Topics) NamedIds(
        params (Guid Id, string Name)[] topics) =>
        (
            topics.Select(topic => topic.Id).ToArray(),
            topics.ToDictionary(
                topic => topic.Id,
                topic => new TopicModel { Id = topic.Id, Topic = topic.Name }));

    private static Dictionary<Guid, TopicModel> Topics(
        (Guid[] Ids, Dictionary<Guid, TopicModel> Topics) named) => named.Topics;

    private static IReadOnlySet<Guid> Verified(
        string userText,
        (Guid[] Ids, Dictionary<Guid, TopicModel> Topics) named) =>
        AssistantNamedTopics.Verified(named.Ids, named.Topics, topic => topic.Topic, userText);
}
