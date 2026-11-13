using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Mapping;

/// <summary>
/// The shared book-update mapping itself (issue #1612): the library command and
/// the legacy REST patch shape project onto one implementation, so they cannot
/// drift on empty-string clearing, the book's values, finished state or the
/// paper page-count rule.
///
/// The DB-backed behaviour is characterized through the service in
/// <c>BookUpdateMappingTests</c>; this file pins the seam both entry points use.
/// </summary>
public sealed class BookUpdateMappingSeamTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------
    // Both entry points agree
    // ------------------------------------------------------------------

    [Fact]
    public void Library_command_and_legacy_patch_clear_every_subtype_identifier_with_an_empty_string()
    {
        var audio = Audio("B095TNRPXD", "12:34:56", "Reader");
        var patch = new LibraryUpdateBookRequest(
            "client", "key", Guid.NewGuid(), Asin: "", Duration: "", Narrator: "").ToPatch();

        BookUpdateMapping.Apply(audio, patch, Now);

        audio.Asin.Should().BeNull("the drift this issue fixes: the legacy path used to keep \"\"");
        audio.Duration.Should().BeNull();
        audio.Narrator.Should().BeNull();

        var viaLegacy = Audio("B095TNRPXD", "12:34:56", "Reader");
        viaLegacy.Apply(new UpdateBookDto(
            Title: null, Subtitle: null, Author: null, Editor: null, Translator: null,
            Narrator: "", Description: null, Isbn: null, Asin: "", Duration: null,
            Publisher: null, PlaceOfPublication: null, PublishedDate: null, Edition: null,
            PageCount: null, Language: null, Categories: null, Series: null,
            VolumeNumber: null, Rating: null, IsFavorite: null, PersonalReview: null,
            FinishedAt: null, IsFinished: null));

        viaLegacy.Asin.Should().BeNull("the legacy entry point now clears like the command does");
        viaLegacy.Narrator.Should().BeNull();
    }

    [Fact]
    public void Both_entry_points_write_the_books_values_to_its_work_and_the_edition_exception_to_the_edition()
    {
        var viaCommand = Physical();
        BookUpdateMapping.Apply(viaCommand, new LibraryUpdateBookRequest(
            "client", "key", Guid.NewGuid(),
            Rating: 9, IsFavorite: true, PersonalReview: "Book.",
            EditionRating: 9, EditionReview: "Edition.").ToPatch(), Now);

        var viaLegacy = Physical();
        viaLegacy.Apply(new UpdateBookDto(
            Title: null, Subtitle: null, Author: null, Editor: null, Translator: null,
            Narrator: null, Description: null, Isbn: null, Asin: null, Duration: null,
            Publisher: null, PlaceOfPublication: null, PublishedDate: null, Edition: null,
            PageCount: null, Language: null, Categories: null, Series: null,
            VolumeNumber: null, Rating: 9, IsFavorite: true, PersonalReview: "Book.",
            FinishedAt: null, IsFinished: null, EditionRating: 9, EditionReview: "Edition."));

        foreach (var model in new[] { viaCommand, viaLegacy })
        {
            model.Work!.Rating.Should().Be(5, "the book's rating is clamped");
            model.Work.IsFavorite.Should().BeTrue();
            model.Work.PersonalReview.Should().Be("Book.");
            model.Progress.Rating.Should().Be(5, "the edition exception is the edition's");
            model.Progress.PersonalReview.Should().Be("Edition.");
        }

        viaCommand.Work!.Rating.Should().Be(viaLegacy.Work!.Rating);
    }

    [Fact]
    public void Both_entry_points_finish_and_unfinish_the_same_way()
    {
        var viaCommand = Physical();
        BookUpdateMapping.Apply(viaCommand, new LibraryUpdateBookRequest(
            "client", "key", Guid.NewGuid(), IsFinished: true).ToPatch(), Now);

        var viaLegacy = Physical();
        viaLegacy.Apply(new UpdateBookDto(
            Title: null, Subtitle: null, Author: null, Editor: null, Translator: null,
            Narrator: null, Description: null, Isbn: null, Asin: null, Duration: null,
            Publisher: null, PlaceOfPublication: null, PublishedDate: null, Edition: null,
            PageCount: null, Language: null, Categories: null, Series: null,
            VolumeNumber: null, Rating: null, IsFavorite: null, PersonalReview: null,
            FinishedAt: null, IsFinished: true));

        foreach (var model in new[] { viaCommand, viaLegacy })
        {
            model.Progress.FinishedAt.Should().NotBeNull();
            model.Progress.ProgressPercent.Should().Be(100);
            model.Progress.LastReadAt.Should().Be(model.Progress.FinishedAt);
        }
    }

    // ------------------------------------------------------------------
    // Field semantics of the shared mapping
    // ------------------------------------------------------------------

    [Fact]
    public void Null_leaves_a_field_alone_and_an_empty_string_clears_it()
    {
        var model = Physical();
        model.Title = "Fictions";
        model.Author = "Borges";
        model.Metadata.Subtitle = "Sub";
        model.Metadata.Series = "Library";

        BookUpdateMapping.Apply(model, EmptyPatch() with { Author = "" }, Now);

        model.Title.Should().Be("Fictions", "an untouched field stays");
        model.Author.Should().BeNull("an empty string clears");
        model.Metadata.Subtitle.Should().Be("Sub", "null leaves the field alone");
        model.Metadata.Series.Should().Be("Library");
    }

    [Fact]
    public void Finishing_keeps_an_existing_last_read_and_unfinishing_leaves_the_percentage()
    {
        var model = Physical();
        var readAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        model.Progress.LastReadAt = readAt;
        model.Progress.ProgressPercent = 40;

        BookUpdateMapping.Apply(model, EmptyPatch() with { IsFinished = true }, Now);

        model.Progress.FinishedAt.Should().Be(Now);
        model.Progress.LastReadAt.Should().Be(readAt, "finishing is not a new read");
        model.Progress.ProgressPercent.Should().Be(100);

        BookUpdateMapping.Apply(model, EmptyPatch() with { IsFinished = false }, Now);

        model.Progress.FinishedAt.Should().BeNull();
        model.Progress.ProgressPercent.Should().Be(100, "only the moment is cleared");
    }

    [Fact]
    public void An_explicit_finished_moment_wins_over_is_finished()
    {
        var model = Physical();
        var explicitDate = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        BookUpdateMapping.Apply(model, EmptyPatch() with { FinishedAt = explicitDate, IsFinished = false }, Now);

        model.Progress.FinishedAt.Should().Be(explicitDate);
        model.Progress.ProgressPercent.Should().Be(0, "the explicit date path only sets the moment");
    }

    [Fact]
    public void A_changed_paper_page_count_recomputes_progress_and_an_ebook_page_count_does_not()
    {
        var paper = Physical();
        paper.Progress.LastLocation = """{"page":100}""";
        paper.Progress.ProgressPercent = 0;

        BookUpdateMapping.Apply(paper, EmptyPatch() with { PageCount = 400 }, Now);
        paper.PageCount.Should().Be(400);
        paper.Progress.ProgressPercent.Should().Be(25);

        var ebook = new EBookModel { Title = "Digital", Work = new WorkModel() };
        ebook.Progress.LastLocation = """{"page":100}""";
        BookUpdateMapping.Apply(ebook, EmptyPatch() with { PageCount = 400 }, Now);
        ebook.Progress.ProgressPercent.Should().Be(0, "the paper page rule is the paper book's");
    }

    [Fact]
    public void Updating_a_book_without_its_work_fails_loudly_instead_of_dropping_the_books_values()
    {
        var model = new PhysicalBookModel { Title = "Fictions" };

        var act = () => BookUpdateMapping.Apply(model, EmptyPatch() with { Rating = 4 }, Now);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*without its Work loaded*");
    }

    // ------------------------------------------------------------------
    // Read mapping side of the same issue
    // ------------------------------------------------------------------

    [Fact]
    public void ToDto_reads_unreadable_chapter_json_without_a_console_logger_and_reports_to_the_given_one()
    {
        var model = Physical();
        model.FileDetails.ChaptersJson = "{ not json";

        var dto = model.ToDto();
        dto.Chapters.Should().BeNull("an unreadable document yields no chapter list");

        var logged = model.ToDto(NullLogger.Instance);
        logged.Chapters.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Builders
    // ------------------------------------------------------------------

    private static PhysicalBookModel Physical() =>
        new() { Title = "Fictions", Work = new WorkModel() };

    private static AudioBookModel Audio(string? asin, string? duration, string? narrator) =>
        new()
        {
            Title = "Spoken",
            Asin = asin,
            Duration = duration,
            Narrator = narrator,
            Work = new WorkModel(),
        };

    private static UpdateBookDto EmptyPatch() =>
        new(
            Title: null, Subtitle: null, Author: null, Editor: null, Translator: null,
            Narrator: null, Description: null, Isbn: null, Asin: null, Duration: null,
            Publisher: null, PlaceOfPublication: null, PublishedDate: null, Edition: null,
            PageCount: null, Language: null, Categories: null, Series: null,
            VolumeNumber: null, Rating: null, IsFavorite: null, PersonalReview: null,
            FinishedAt: null, IsFinished: null);
}
