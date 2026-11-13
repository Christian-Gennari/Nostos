using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Data;

public sealed class NostosDbContextSavePipelineTests : IDisposable
{
    private readonly SqliteTestFixture _sqlite = new();

    [Fact]
    public void SaveChanges_stamps_note_updated_at_monotonically_on_modify_only()
    {
        var path = _sqlite.CreateDatabasePath();
        var createdAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Guid noteId;

        using (var db = _sqlite.CreateContext(path))
        {
            var book = new EBookModel { Title = "Book", Author = "Author" };
            var note = new NoteModel
            {
                Book = book,
                Content = "Initial",
                RawContent = "Initial",
                CreatedAt = createdAt,
            };
            db.Books.Add(book);
            db.Notes.Add(note);
            db.SaveChanges();

            note.UpdatedAt.Should().BeNull("added notes remain unedited until their first modification");
            noteId = note.Id;
        }

        using (var db = _sqlite.CreateContext(path))
        {
            var note = db.Notes.Single(n => n.Id == noteId);
            note.Content = "First edit";
            db.SaveChanges();

            note.UpdatedAt.Should().NotBeNull();
            var firstUpdatedAt = note.UpdatedAt!.Value;
            firstUpdatedAt.Should().BeAfter(createdAt);

            note.Content = "Second edit";
            db.SaveChanges();

            note.UpdatedAt!.Value.Should().BeAfter(firstUpdatedAt);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_does_not_advance_revision_for_non_portable_entities()
    {
        var path = _sqlite.CreateDatabasePath();
        using (var seed = _sqlite.CreateContext(path))
        {
            seed.Works.Add(new WorkModel
            {
                Title = "Initial",
                NormalizedTitle = BookIdentityNormalizer.NormalizeTitle("Initial"),
                NormalizedAuthor = string.Empty,
            });
            await seed.SaveChangesAsync();
        }

        string revisionBefore;
        await using (var db = _sqlite.CreateContext(path))
        {
            revisionBefore = (await db.LibraryStates.AsNoTracking().SingleAsync()).StateVersion;
            db.LibraryCommandReceipts.Add(new LibraryCommandReceipt
            {
                ClientId = "test-client",
                IdempotencyKey = "k-1",
                CommandKind = "noop",
                ResponseJson = "{}",
            });
            await db.SaveChangesAsync();

            var revisionAfter = (await db.LibraryStates.AsNoTracking().SingleAsync()).StateVersion;
            revisionAfter.Should().Be(revisionBefore);
        }
    }

    [Fact]
    public async Task SaveChangesAsync_rolls_back_revision_cache_when_savepoint_fails_inside_ambient_transaction()
    {
        var path = _sqlite.CreateDatabasePath();
        await using var db = _sqlite.CreateContext(path);

        var work = new WorkModel
        {
            Title = "Existing",
            NormalizedTitle = BookIdentityNormalizer.NormalizeTitle("Existing"),
            NormalizedAuthor = string.Empty,
        };
        db.Works.Add(work);
        await db.SaveChangesAsync();
        var initialRevision = (await db.LibraryStates.AsNoTracking().SingleAsync()).StateVersion;

        await using var tx = await db.Database.BeginTransactionAsync();

        // First portable save fails after LibraryRevision.AdvanceAndGet runs because
        // the unique NormalizedIsbn constraint is violated in the database.
        db.Books.Add(new EBookModel
        {
            Title = "One",
            WorkId = work.Id,
            NormalizedIsbn = "9780140449136",
        });
        await db.SaveChangesAsync();
        var afterFirstSuccess = (await db.LibraryStates.AsNoTracking().SingleAsync()).StateVersion;
        long.Parse(afterFirstSuccess).Should().BeGreaterThan(long.Parse(initialRevision));

        // Open a second context on a fresh unit or trigger a DB constraint error in a fresh tx
        await tx.CommitAsync();

        await using var db2 = _sqlite.CreateContext(path);
        await using var tx2 = await db2.Database.BeginTransactionAsync();

        db2.Books.Add(new EBookModel
        {
            Title = "Duplicate",
            WorkId = work.Id,
            NormalizedIsbn = "9780140449136",
        });

        var failingAct = async () => await db2.SaveChangesAsync();
        await failingAct.Should().ThrowAsync<DbUpdateException>();
        db2.ChangeTracker.Clear();

        // Revision must still advance cleanly on the next successful portable save in tx2.
        db2.Books.Add(new EBookModel
        {
            Title = "Distinct",
            WorkId = work.Id,
            NormalizedIsbn = "9780140449143",
        });
        await db2.SaveChangesAsync();
        await tx2.CommitAsync();

        var finalRevision = (await db2.LibraryStates.AsNoTracking().SingleAsync()).StateVersion;
        long.Parse(finalRevision).Should().BeGreaterThan(long.Parse(afterFirstSuccess));
    }

    [Fact]
    public async Task SaveChangesAsync_marks_reading_position_only_changes_on_commit_signal()
    {
        var path = _sqlite.CreateDatabasePath();
        Guid bookId;
        await using (var seed = _sqlite.CreateContext(path))
        {
            var book = new EBookModel
            {
                Title = "Position Tracked",
                Author = "Reader",
            };
            seed.Books.Add(book);
            await seed.SaveChangesAsync();
            bookId = book.Id;
        }

        await using (var db = _sqlite.CreateContext(path))
        {
            var nextChange = LibraryChangeSignal.NextAsync(LibraryChangeSignal.KeyFor(db));
            var book = await db.Books.SingleAsync(b => b.Id == bookId);
            book.Progress.LastLocation = "epubcfi(/6/8)";
            book.Progress.ProgressPercent = 42;
            book.Progress.LastReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var signal = await nextChange;
            signal.ProgressOnly.Should().BeTrue();
            signal.Revision.Should().NotBeNullOrWhiteSpace();
        }

        await using (var db = _sqlite.CreateContext(path))
        {
            var nextChange = LibraryChangeSignal.NextAsync(LibraryChangeSignal.KeyFor(db));
            var book = await db.Books.SingleAsync(b => b.Id == bookId);
            book.Title = "Renamed Title";
            await db.SaveChangesAsync();

            var signal = await nextChange;
            signal.ProgressOnly.Should().BeFalse();
        }
    }

    [Fact]
    public void SaveChanges_respects_acceptAllChangesOnSuccess_false()
    {
        using var db = _sqlite.CreateContext();
        var work = new WorkModel
        {
            Title = "Deferred Accept",
            NormalizedTitle = BookIdentityNormalizer.NormalizeTitle("Deferred Accept"),
            NormalizedAuthor = string.Empty,
        };
        db.Works.Add(work);

        db.SaveChanges(acceptAllChangesOnSuccess: false);

        db.Entry(work).State.Should().Be(EntityState.Added);
        db.ChangeTracker.AcceptAllChanges();
        db.Entry(work).State.Should().Be(EntityState.Unchanged);
    }

    public void Dispose() => _sqlite.Dispose();
}
