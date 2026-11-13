using FluentAssertions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Notes;
using Xunit;

namespace Nostos.Backend.Tests.Services.Notes;

/// <summary>
/// The note read models extracted from NoteService (issue #1619): the row the
/// Brain shows and the shape the review flow reads.
/// </summary>
public sealed class NoteProjectorTests
{
    private readonly NoteProjector _projector = new(new NoteSnippetFormatter());

    [Fact]
    public void A_search_hit_carries_the_book_and_its_edition_count()
    {
        var work = Guid.NewGuid();
        var book = new EBookModel { Id = Guid.NewGuid(), Title = "A Book", Author = "An Author", WorkId = work };
        var note = Note(book, content: "about the will");

        var hit = _projector.ToSearchHit(note, "will", new Dictionary<Guid, int> { [work] = 3 });

        hit.BookTitle.Should().Be("A Book");
        hit.BookAuthor.Should().Be("An Author");
        hit.EditionCount.Should().Be(3);
        hit.WorkId.Should().Be(work);
        hit.Snippet.Should().Be("about the will");
    }

    [Fact]
    public void A_note_with_no_book_is_one_edition_and_no_book_fields()
    {
        var note = Note(book: null, content: "a note with no book");

        var hit = _projector.ToSearchHit(note, null);

        hit.BookTitle.Should().BeNull();
        hit.BookAuthor.Should().BeNull();
        hit.BookFormat.Should().BeNull();
        hit.EditionCount.Should().Be(1);
        hit.WorkId.Should().BeNull();
    }

    [Fact]
    public void A_work_missing_from_the_counts_is_one_edition()
    {
        var book = new EBookModel { Id = Guid.NewGuid(), Title = "A Book", WorkId = Guid.NewGuid() };

        _projector.ToSearchHit(Note(book, "a note"), null, new Dictionary<Guid, int>())
            .EditionCount.Should().Be(1);
    }

    [Fact]
    public void A_passage_travels_as_its_opening()
    {
        var book = new EBookModel { Id = Guid.NewGuid(), Title = "A Book", WorkId = Guid.NewGuid() };
        var longText = string.Join(' ', Enumerable.Repeat("word", 400));
        var note = Note(book, content: "why I kept it");
        note.Kind = NoteKinds.Passage;
        note.SelectedText = longText;
        note.PassageTitle = "A chapter";
        note.PassageEnd = "end";

        var hit = _projector.ToSearchHit(note, null);

        hit.SelectedText.Should().Be(PassageText.Opening(longText));
        hit.SelectedText!.Length.Should().BeLessThan(longText.Length);
        hit.PassageTitle.Should().Be("A chapter");
        hit.PassageWordCount.Should().Be(400);
    }

    [Fact]
    public void Topic_names_are_sorted_and_links_without_a_topic_are_ignored()
    {
        var note = Note(book: null, content: "a note");
        note.NoteTopics.Add(new NoteTopicModel { Topic = new TopicModel { Topic = "Virtue" } });
        note.NoteTopics.Add(new NoteTopicModel { Topic = new TopicModel { Topic = "Courage" } });
        note.NoteTopics.Add(new NoteTopicModel { TopicId = Guid.NewGuid(), Topic = null! });

        _projector.ToSearchHit(note, null).TopicNames.Should().Equal("Courage", "Virtue");
    }

    [Theory]
    [InlineData("audiobook")]
    [InlineData("physical")]
    [InlineData("ebook")]
    [InlineData("pdf")]
    public void The_format_glyph_names_the_edition(string expected)
    {
        BookModel book = expected switch
        {
            "audiobook" => new AudioBookModel(),
            "physical" => new PhysicalBookModel(),
            _ => new EBookModel(),
        };
        book.Id = Guid.NewGuid();
        book.Title = "A Book";
        book.FileDetails.FileName = expected == "pdf" ? "a-book.pdf" : "a-book.epub";

        _projector.FormatOf(book).Should().Be(expected);
        _projector.FormatOf(null).Should().BeNull();
    }

    [Fact]
    public void A_review_reports_sorted_names_and_every_topic_id()
    {
        var book = new EBookModel { Id = Guid.NewGuid(), Title = "A Book", WorkId = Guid.NewGuid() };
        var note = Note(book, content: "a note");
        note.SelectedText = "a quote";
        var virtue = new TopicModel { Topic = "Virtue" };
        var courage = new TopicModel { Topic = "Courage" };
        note.NoteTopics.Add(new NoteTopicModel { TopicId = virtue.Id, Topic = virtue });
        note.NoteTopics.Add(new NoteTopicModel { TopicId = courage.Id, Topic = courage });

        var review = _projector.ToReview(note);

        review.NoteId.Should().Be(note.Id);
        review.BookTitle.Should().Be("A Book");
        review.Content.Should().Be("a note");
        review.SelectedText.Should().Be("a quote");
        review.TopicNames.Should().Equal("Courage", "Virtue");
        review.TopicIds.Should().BeEquivalentTo([virtue.Id, courage.Id]);
    }

    private static NoteModel Note(BookModel? book, string content) => new()
    {
        Id = Guid.NewGuid(),
        BookId = book?.Id ?? Guid.NewGuid(),
        Book = book,
        Content = content,
    };
}
