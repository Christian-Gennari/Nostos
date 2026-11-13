using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Data;

/// <summary>
/// One backend place enforces "a physical book has no file" (#332): the context
/// itself, so any future code path that would create the shape fails at save
/// instead of writing a row that shows paper controls beside a working Read
/// button. There is deliberately no second guard in the UI.
/// </summary>
public sealed class PhysicalBookHasNoFileTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public PhysicalBookHasNoFileTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Saving_a_physical_book_with_a_file_throws()
    {
        await using var db = _fixture.CreateContext();
        db.Books.Add(new PhysicalBookModel
        {
            Id = Guid.NewGuid(),
            Title = "A paper book with a PDF",
            Work = NewWork(),
            FileDetails = new FileInfoDetails { HasFile = true, FileName = "book.pdf" },
        });

        var act = async () => await db.SaveChangesAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*is physical and carries a file*metadata-only*");
        (await db.Books.CountAsync()).Should().Be(0, "nothing was written");
    }

    [Fact]
    public async Task Turning_on_a_file_for_a_physical_book_throws()
    {
        await using var db = _fixture.CreateContext();
        var book = await SeedPhysicalBookAsync(db);

        book.FileDetails.HasFile = true;
        book.FileDetails.FileName = "book.epub";

        var act = async () => await db.SaveChangesAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_metadata_only_physical_book_saves_and_keeps_its_details()
    {
        await using var db = _fixture.CreateContext();
        var book = await SeedPhysicalBookAsync(db);

        book.Title = "Renamed paper book";
        book.Progress.ProgressPercent = 12;
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var stored = await db.Books.AsNoTracking().SingleAsync(b => b.Id == book.Id);
        stored.Should().BeOfType<PhysicalBookModel>();
        stored.Title.Should().Be("Renamed paper book");
        stored.Progress.ProgressPercent.Should().Be(12);
        stored.FileDetails.HasFile.Should().BeFalse();
    }

    [Fact]
    public async Task An_unrelated_edit_to_a_row_that_already_carried_a_file_still_saves()
    {
        // A row the migration left physical because its reading position was
        // ambiguous keeps its file. Editing its title must not fail: the shape
        // predates the rule, and the rule is about writes that create it.
        await using var db = _fixture.CreateContext();
        var work = NewWork();
        db.Works.Add(work);
        await db.SaveChangesAsync();

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Books" ("Id", "BookType", "Title", "CreatedAt", "WorkId",
                "FileDetails_HasFile", "FileDetails_FileName",
                "PhysicalBookModel_Isbn", "PhysicalBookModel_PageCount",
                "Progress_ProgressPercent", "Progress_Rating", "Status")
            VALUES ({0}, 'physical', 'Left alone', '2026-01-01 00:00:00', {1},
                1, 'book.pdf', '9780198752431', 677, 0, 0, 0)
            """,
            id.ToString("D").ToUpperInvariant(),
            work.Id.ToString("D").ToUpperInvariant());

        var legacy = await db.Books.SingleAsync(b => b.Id == id);
        legacy.Title = "Renamed, still physical";
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var stored = await db.Books.AsNoTracking().SingleAsync(b => b.Id == id);
        stored.Should().BeOfType<PhysicalBookModel>();
        stored.Title.Should().Be("Renamed, still physical");
        stored.FileDetails.HasFile.Should().BeTrue("a row the user must decide about keeps its file");
    }

    private static async Task<PhysicalBookModel> SeedPhysicalBookAsync(NostosDbContext db)
    {
        var book = new PhysicalBookModel
        {
            Id = Guid.NewGuid(),
            Title = "A real paper book",
            Work = NewWork(),
            Isbn = "9780140442274",
            PageCount = 240,
        };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book;
    }

    private static WorkModel NewWork() => new()
    {
        Id = Guid.NewGuid(),
        Title = "A book",
        NormalizedTitle = "A BOOK " + Guid.NewGuid().ToString("N"),
        NormalizedAuthor = string.Empty,
    };
}
