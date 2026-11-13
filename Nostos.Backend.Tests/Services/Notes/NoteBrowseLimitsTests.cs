using FluentAssertions;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Services.Notes;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// The two list rules extracted from NoteService (issue #1619): a page is
/// bounded, and an unknown kind matches nothing rather than everything.
/// </summary>
public sealed class NoteBrowseLimitsTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(25, 25)]
    [InlineData(200, 200)]
    [InlineData(500, 200)]
    public void A_page_size_is_clamped_between_one_and_two_hundred(int requested, int expected)
    {
        NoteBrowseLimits.Clamp(requested).Should().Be(expected);
    }

    [Fact]
    public void A_blank_kind_is_no_filter()
    {
        NoteBrowseLimits.Normalized(new NoteBrowseQuery(Kind: "  "))
            .Kind.Should().BeNull();
    }

    [Fact]
    public void A_known_kind_is_normalised_to_its_canonical_name()
    {
        NoteBrowseLimits.Normalized(new NoteBrowseQuery(Kind: "PASSAGE"))
            .Kind.Should().Be(NoteKinds.Passage);
    }

    [Fact]
    public void An_unknown_kind_matches_nothing()
    {
        NoteBrowseLimits.Normalized(new NoteBrowseQuery(Kind: "poem"))
            .Kind.Should().Be("unknown");
    }

    [Fact]
    public void The_other_filters_are_untouched()
    {
        var bookId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        var normalized = NoteBrowseLimits.Normalized(new NoteBrowseQuery(
            Text: "will",
            BookId: bookId,
            WithoutTopics: true,
            Kind: "note",
            TopicId: topicId,
            Order: NoteBrowseOrder.Book));

        normalized.Text.Should().Be("will");
        normalized.BookId.Should().Be(bookId);
        normalized.WithoutTopics.Should().BeTrue();
        normalized.Kind.Should().Be(NoteKinds.Note);
        normalized.TopicId.Should().Be(topicId);
        normalized.Order.Should().Be(NoteBrowseOrder.Book);
    }
}
