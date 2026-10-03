using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Data;

public sealed class NostosDbContextWorkAssignmentTests : IDisposable
{
    private readonly SqliteTestFixture _sqlite = new();

    [Fact]
    public void SaveChanges_reuses_persisted_work_that_is_not_already_tracked()
    {
        var path = _sqlite.CreateDatabasePath();
        var workId = SeedWork(path, "Meditations", "Marcus Aurelius");

        using var db = _sqlite.CreateContext(path);
        db.Works.Local.Should().BeEmpty();

        var book = new PhysicalBookModel
        {
            Title = "Meditations",
            Author = "Marcus Aurelius",
        };
        db.Books.Add(book);

        db.SaveChanges();

        book.WorkId.Should().Be(workId);
        db.Works.Count().Should().Be(1);
    }

    [Fact]
    public async Task SaveChangesAsync_reuses_the_same_persisted_work()
    {
        var path = _sqlite.CreateDatabasePath();
        var workId = SeedWork(path, "Meditations", "Marcus Aurelius");

        await using var db = _sqlite.CreateContext(path);
        db.Works.Local.Should().BeEmpty();

        var book = new PhysicalBookModel
        {
            Title = "Meditations",
            Author = "Marcus Aurelius",
        };
        db.Books.Add(book);

        await db.SaveChangesAsync();

        book.WorkId.Should().Be(workId);
        (await db.Works.CountAsync()).Should().Be(1);
    }

    [Fact]
    public void SaveChanges_prefers_a_matching_tracked_work_from_the_same_unit_of_work()
    {
        using var db = _sqlite.CreateContext();

        var work = CreateWork("The Republic", "Plato");
        var book = new PhysicalBookModel
        {
            Title = "The Republic",
            Author = "Plato",
        };

        db.Works.Add(work);
        db.Books.Add(book);

        db.SaveChanges();

        book.WorkId.Should().Be(work.Id);
        db.Works.Count().Should().Be(1);
    }

    [Fact]
    public void SaveChanges_creates_a_work_from_a_directly_added_book_when_none_matches()
    {
        var path = _sqlite.CreateDatabasePath();
        SeedWork(path, "Meditations", "Marcus Aurelius");

        using var db = _sqlite.CreateContext(path);
        var createdAt = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var book = new PhysicalBookModel
        {
            Title = "The Republic",
            Author = "Plato",
            CreatedAt = createdAt,
        };
        db.Books.Add(book);

        db.SaveChanges();

        book.WorkId.Should().NotBe(Guid.Empty);
        var work = db.Works.Single(w => w.Id == book.WorkId);
        work.Title.Should().Be("The Republic");
        work.Author.Should().Be("Plato");
        work.NormalizedTitle.Should().Be(BookIdentityNormalizer.NormalizeTitle("The Republic"));
        work.NormalizedAuthor.Should().Be(BookIdentityNormalizer.NormalizeAuthor("Plato"));
        work.CreatedAt.Should().Be(createdAt);
        db.Works.Count().Should().Be(2);
    }

    [Fact]
    public async Task SaveChangesAsync_shares_one_new_work_between_matching_books_in_the_same_save()
    {
        await using var db = _sqlite.CreateContext();

        var physical = new PhysicalBookModel { Title = "Walden", Author = "Henry David Thoreau" };
        var other = new PhysicalBookModel { Title = "Walden", Author = "Henry David Thoreau" };
        var different = new PhysicalBookModel { Title = "Middlemarch", Author = "George Eliot" };
        db.Books.AddRange(physical, other, different);

        await db.SaveChangesAsync();

        other.WorkId.Should().Be(physical.WorkId);
        different.WorkId.Should().NotBe(physical.WorkId);
        (await db.Works.CountAsync()).Should().Be(2);
    }

    [Fact]
    public void SaveChanges_keeps_an_explicitly_assigned_work_even_when_identity_differs()
    {
        using var db = _sqlite.CreateContext();

        var work = CreateWork("The Republic", "Plato");
        db.Works.Add(work);
        var book = new PhysicalBookModel
        {
            Title = "Politeia",
            Author = "Platon",
            WorkId = work.Id,
        };
        db.Books.Add(book);

        db.SaveChanges();

        book.WorkId.Should().Be(work.Id);
        db.Works.Count().Should().Be(1);
    }

    private Guid SeedWork(string path, string title, string author)
    {
        using var db = _sqlite.CreateContext(path);
        var work = CreateWork(title, author);
        db.Works.Add(work);
        db.SaveChanges();
        return work.Id;
    }

    private static WorkModel CreateWork(string title, string author) =>
        new()
        {
            Title = title,
            Author = author,
            NormalizedTitle = BookIdentityNormalizer.NormalizeTitle(title),
            NormalizedAuthor = BookIdentityNormalizer.NormalizeAuthor(author),
        };

    public void Dispose() => _sqlite.Dispose();
}
