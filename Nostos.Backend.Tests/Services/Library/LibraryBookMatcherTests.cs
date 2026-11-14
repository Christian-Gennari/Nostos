using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Services.Library;

/// <summary>
/// The identity policy behind create-or-match (issue #1611): explicit
/// confirmation, then identifier, then identifier/type consistency, then
/// title/author. Deciding (rather than writing) is what makes the order
/// assertable.
/// </summary>
public sealed class LibraryBookMatcherTests
{
    private const string IsbnBorges = "9780141183848";
    private const string IsbnOther = "9780306406157";
    private const string AsinExample = "B095TNRPXD";

    [Fact]
    public async Task Confirmed_book_is_used_without_consulting_identity()
    {
        using var graph = new LibraryTestGraph();
        var seeded = await SeedAsync(graph, "Fictions", "Borges", type: "physical");

        var decision = await DecideAsync(graph, Request("physical", "Completely Different"),
            confirmedBookId: seeded.BookId);

        decision.Kind.Should().Be(LibraryMatchKind.Matched);
        decision.Matched!.Id.Should().Be(seeded.BookId);
    }

    [Fact]
    public async Task Confirmed_book_that_does_not_exist_is_a_typed_failure()
    {
        using var graph = new LibraryTestGraph();

        var decision = await DecideAsync(graph, Request("physical", "Fictions"), confirmedBookId: Guid.NewGuid());

        decision.Kind.Should().Be(LibraryMatchKind.Failure);
        decision.ErrorCode.Should().Be("book_not_found");
    }

    [Fact]
    public async Task Matching_isbn_wins_even_when_the_title_differs()
    {
        using var graph = new LibraryTestGraph();
        var seeded = await SeedAsync(graph, "Fictions", "Borges", type: "physical", isbn: IsbnBorges);

        var decision = await DecideAsync(graph, Request("physical", "A Different Title", "Someone", isbn: IsbnBorges));

        decision.Kind.Should().Be(LibraryMatchKind.Matched);
        decision.Matched!.Id.Should().Be(seeded.BookId);
    }

    [Fact]
    public async Task Isbn_and_asin_pointing_at_different_books_is_an_identity_conflict()
    {
        using var graph = new LibraryTestGraph();
        await SeedAsync(graph, "Paper", "Author", type: "physical", isbn: IsbnBorges);
        await SeedAsync(graph, "Spoken", "Author", type: "audiobook", asin: AsinExample);

        var decision = await DecideAsync(graph,
            Request("physical", "Both", "Author", isbn: IsbnBorges, asin: AsinExample));

        decision.Kind.Should().Be(LibraryMatchKind.Failure);
        decision.ErrorCode.Should().Be("identity_conflict");
    }

    [Fact]
    public async Task An_audiobook_may_not_carry_an_isbn()
    {
        using var graph = new LibraryTestGraph();

        var decision = await DecideAsync(graph,
            Request("audiobook", "Spoken", "Author", isbn: IsbnBorges));

        decision.Kind.Should().Be(LibraryMatchKind.Failure);
        decision.ErrorCode.Should().Be("invalid_book_identity");
        decision.Reply.Should().Be("Audiobooks use ASIN, not ISBN.");
    }

    [Fact]
    public async Task An_ebook_may_not_carry_an_asin()
    {
        using var graph = new LibraryTestGraph();

        var decision = await DecideAsync(graph,
            Request("ebook", "Digital", "Author", asin: AsinExample));

        decision.Kind.Should().Be(LibraryMatchKind.Failure);
        decision.ErrorCode.Should().Be("invalid_book_identity");
        decision.Reply.Should().Be("Only audiobooks use ASIN.");
    }

    [Fact]
    public async Task Exact_title_and_author_matches_the_single_same_type_edition()
    {
        using var graph = new LibraryTestGraph();
        var seeded = await SeedAsync(graph, "Fictions", "Jorge Luis Borges", type: "physical");

        var decision = await DecideAsync(graph, Request("physical", "Fictions", "Jorge Luis Borges"));

        decision.Kind.Should().Be(LibraryMatchKind.Matched);
        decision.Matched!.Id.Should().Be(seeded.BookId);
    }

    [Fact]
    public async Task Exact_title_and_author_with_two_same_type_editions_asks_in_strict_mode()
    {
        using var graph = new LibraryTestGraph();
        await SeedAsync(graph, "Fictions", "Borges", type: "physical", workTitle: "Fictions");
        await SeedAsync(graph, "Fictions", "Borges", type: "physical", workTitle: "Fictions");

        var decision = await DecideAsync(graph, Request("physical", "Fictions", "Borges"), strict: true);

        decision.Kind.Should().Be(LibraryMatchKind.ConfirmationRequired);
        decision.Candidates!.Count.Should().Be(2);
        decision.Candidates.Should().OnlyContain(c => c.MatchReason.Contains("exact title"));
    }

    [Fact]
    public async Task Exact_title_and_author_with_two_same_type_editions_creates_in_legacy_mode()
    {
        using var graph = new LibraryTestGraph();
        var first = await SeedAsync(graph, "Fictions", "Borges", type: "physical", workTitle: "Fictions");
        await SeedAsync(graph, "Fictions", "Borges", type: "physical", workTitle: "Fictions");

        var decision = await DecideAsync(graph, Request("physical", "Fictions", "Borges"), strict: false);

        decision.Kind.Should().Be(LibraryMatchKind.Create);
        decision.TargetWorkId.Should().Be(first.WorkId, "the new edition joins the work it matched");
    }

    [Fact]
    public async Task A_different_type_is_a_new_edition_of_the_matched_work()
    {
        using var graph = new LibraryTestGraph();
        var seeded = await SeedAsync(graph, "Fictions", "Borges", type: "physical");

        var decision = await DecideAsync(graph, Request("ebook", "Fictions", "Borges"));

        decision.Kind.Should().Be(LibraryMatchKind.Create);
        decision.TargetWorkId.Should().Be(seeded.WorkId);
    }

    [Fact]
    public async Task An_existing_edition_waiting_for_its_file_is_resumed()
    {
        using var graph = new LibraryTestGraph();
        var seeded = await SeedAsync(graph, "Fictions", "Borges", type: "ebook", status: BookStatus.UploadPending);

        var decision = await DecideAsync(graph,
            Request("ebook", "Fictions", "Borges", fileUploadExpected: true));

        decision.Kind.Should().Be(LibraryMatchKind.Matched);
        decision.Matched!.Id.Should().Be(seeded.BookId);
    }

    [Fact]
    public async Task An_edition_that_already_has_its_file_is_not_replaced_by_a_new_upload()
    {
        using var graph = new LibraryTestGraph();
        var seeded = await SeedAsync(graph, "Fictions", "Borges", type: "ebook",
            status: BookStatus.Ready, hasFile: true);

        var decision = await DecideAsync(graph,
            Request("ebook", "Fictions", "Borges", fileUploadExpected: true));

        decision.Kind.Should().Be(LibraryMatchKind.Create);
        decision.TargetWorkId.Should().Be(seeded.WorkId);
    }

    [Fact]
    public async Task A_title_only_request_finds_fuzzy_candidates_before_creating()
    {
        using var graph = new LibraryTestGraph();
        await SeedAsync(graph, "The Aleph and Other Stories", "Borges", type: "physical");

        var decision = await DecideAsync(graph, Request("physical", "Aleph"), strict: true);

        decision.Kind.Should().Be(LibraryMatchKind.ConfirmationRequired);
        decision.Candidates!.Single().MatchReason.Should().Be("fuzzy title match");
    }

    [Fact]
    public async Task A_bare_title_in_strict_mode_asks_for_more_information()
    {
        using var graph = new LibraryTestGraph();

        var decision = await DecideAsync(graph, Request("physical", "Something New"), strict: true);

        decision.Kind.Should().Be(LibraryMatchKind.Failure);
        decision.ErrorCode.Should().Be("confirmation_required");
        decision.Reply.Should().Be(LibraryReplyFormatter.MoreInformationRequired);
    }

    [Fact]
    public async Task Force_create_skips_title_matching()
    {
        using var graph = new LibraryTestGraph();
        await SeedAsync(graph, "Fictions", "Borges", type: "physical");

        var decision = await DecideAsync(graph,
            Request("physical", "Fictions", "Borges", forceCreate: true), strict: true);

        decision.Kind.Should().Be(LibraryMatchKind.Create);
        decision.TargetWorkId.Should().BeNull();
    }

    [Theory]
    [InlineData("", "physical", "invalid_book_identity")]
    [InlineData("   ", "physical", "invalid_book_identity")]
    [InlineData("A Title", "vinyl", "invalid_book_identity")]
    public void Identity_rejects_requests_that_cannot_name_a_book(
        string title,
        string type,
        string expectedCode)
    {
        LibraryBookIdentity.TryCreate(Request(type, title), out _, out var code, out _)
            .Should().BeFalse();
        code.Should().Be(expectedCode);
    }

    [Fact]
    public void Identity_defaults_to_ebook_when_no_type_is_given()
    {
        LibraryBookIdentity.TryCreate(Request(string.Empty, "A Title"), out var identity, out _, out _)
            .Should().BeTrue();
        identity.Type.Should().Be("ebook");
    }

    private static LibraryCreateBookRequest Request(
        string type,
        string title,
        string? author = null,
        string? isbn = null,
        string? asin = null,
        bool forceCreate = false,
        bool fileUploadExpected = false) =>
        new("test-client", $"k-{Guid.NewGuid():N}", type, title,
            Author: author,
            Isbn: isbn,
            Asin: asin,
            ForceCreate: forceCreate,
            FileUploadExpected: fileUploadExpected);

    private static async Task<LibraryMatchDecision> DecideAsync(
        LibraryTestGraph graph,
        LibraryCreateBookRequest request,
        bool strict = false,
        Guid? confirmedBookId = null)
    {
        request = request with { ConfirmedBookId = confirmedBookId };
        LibraryBookIdentity.TryCreate(request, out var identity, out _, out _).Should().BeTrue();

        await using var db = await graph.Contexts.CreateDbContextAsync();
        return await LibraryBookMatcher.DecideAsync(db, request, identity, strict, CancellationToken.None);
    }

    private sealed record SeededBook(Guid BookId, Guid WorkId);

    /// <summary>
    /// Seeds a row directly: the matcher is the code under test, so its input
    /// cannot come from create-or-match itself.
    /// </summary>
    private static async Task<SeededBook> SeedAsync(
        LibraryTestGraph graph,
        string title,
        string? author,
        string type,
        string? isbn = null,
        string? asin = null,
        string? workTitle = null,
        BookStatus status = BookStatus.Ready,
        bool hasFile = false)
    {
        await using var db = await graph.Contexts.CreateDbContextAsync();
        var work = await db.Works.FirstOrDefaultAsync(w => w.Title == (workTitle ?? title) && w.Author == author);
        if (work is null)
        {
            work = new WorkModel
            {
                Title = workTitle ?? title,
                Author = author,
                NormalizedTitle = BookIdentityNormalizer.NormalizeTitle(workTitle ?? title),
                NormalizedAuthor = string.IsNullOrEmpty(author) ? null : BookIdentityNormalizer.NormalizeAuthor(author),
            };
            db.Works.Add(work);
        }

        BookModel book = type switch
        {
            "audiobook" => new AudioBookModel { Asin = asin },
            "ebook" => new EBookModel { Isbn = isbn },
            _ => new PhysicalBookModel { Isbn = isbn },
        };
        book.Title = title;
        book.Author = author;
        book.Status = status;
        book.FileDetails.HasFile = hasFile;
        book.NormalizedIsbn = BookIdentityNormalizer.NormalizeIsbn(isbn);
        book.NormalizedAsin = BookIdentityNormalizer.NormalizeAsin(asin);
        book.WorkId = work.Id;
        book.Work = work;
        db.Books.Add(book);
        await db.SaveChangesAsync();

        return new SeededBook(book.Id, work.Id);
    }
}
