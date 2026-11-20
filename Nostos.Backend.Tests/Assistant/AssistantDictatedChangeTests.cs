using FluentAssertions;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services.Library;
using Xunit;

namespace Nostos.Backend.Tests.Assistant;

/// <summary>When a staged change is only what the user dictated (issue #1745).</summary>
public sealed class AssistantDictatedChangeTests
{
    private static readonly Guid Book = Guid.NewGuid();

    private static AssistantStagedStep Step(LibraryChangeOp op) => new(op, "step", []);

    [Theory]
    [InlineData("What have I noted about this book, and rate it 5 stars.", 5, true)]
    [InlineData("Rate it five stars", 5, true)]
    [InlineData("Ge den betyg 4", 4, true)]
    [InlineData("Rate it 5 stars", 1, false)]
    [InlineData("Rate it 15 stars", 5, false)]
    [InlineData("What have I noted about this book?", 5, false)]
    [InlineData("Make the rating 5.5", 5, false)]
    public void A_rating_is_dictated_when_the_message_names_it(string message, int rating, bool expected) =>
        AssistantDictatedChange.Covers([Step(new UpdateBookOp(Book, Rating: rating))], message).Should().Be(expected);

    [Fact]
    public void Clearing_a_rating_needs_the_user_to_say_so() =>
        AssistantDictatedChange.Covers([Step(new UpdateBookOp(Book, Rating: 0))], "Clear the rating on this one")
            .Should().BeTrue();

    [Fact]
    public void Favourite_and_finished_need_their_words() =>
        (AssistantDictatedChange.Covers([Step(new UpdateBookOp(Book, IsFavorite: true))], "Mark it as a favourite")
            && AssistantDictatedChange.Covers([Step(new UpdateBookOp(Book, IsFinished: true))], "I finished it")
            && !AssistantDictatedChange.Covers([Step(new UpdateBookOp(Book, IsFavorite: true))], "Summarise my notes")
            && !AssistantDictatedChange.Covers([Step(new UpdateBookOp(Book, IsFinished: true))], "Summarise my notes"))
            .Should().BeTrue();

    [Fact]
    public void Free_text_is_never_dictated()
    {
        const string message = "Rate it 5 stars and change the description";
        AssistantDictatedChange.Covers([Step(new UpdateBookOp(Book, Rating: 5, Description: "From a note"))], message)
            .Should().BeFalse();
        AssistantDictatedChange.Covers([Step(new UpdateBookOp(Book, Title: "New title"))], message)
            .Should().BeFalse();
    }

    [Fact]
    public void Other_steps_or_other_books_are_not_covered()
    {
        const string message = "Rate it 5 stars";
        AssistantDictatedChange.Covers(
            [Step(new UpdateBookOp(Book, Rating: 5)), Step(new CreateCollectionOp("a", "A"))], message).Should().BeFalse();
        AssistantDictatedChange.Covers(
            [Step(new UpdateBookOp(Book, Rating: 5)), Step(new UpdateBookOp(Guid.NewGuid(), Rating: 5))], message).Should().BeFalse();
        AssistantDictatedChange.Covers([], message).Should().BeFalse();
    }
}
