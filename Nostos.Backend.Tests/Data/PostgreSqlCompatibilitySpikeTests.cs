using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Data;

public sealed class PostgreSqlCompatibilitySpikeTests
{
    private const string ConnectionStringEnvironmentVariable = "NOSTOS_POSTGRES_SPIKE_CONNECTION";

    [Fact]
    [Trait("Category", "PostgresSpike")]
    public async Task Current_model_and_representative_workflows_run_on_postgresql()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // The ordinary test suite stays zero-infrastructure. The dedicated
            // PostgreSQL workflow sets this variable and therefore exercises the
            // real provider/database path.
            return;
        }

        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        var createdAt = new DateTime(2026, 9, 22, 12, 34, 56, DateTimeKind.Utc);
        var lastReadAt = createdAt.AddMinutes(15);
        const string normalizedIsbn = "9780140455113";
        const string normalizedAsin = "B000REPUBL1C";

        Guid physicalId;
        Guid ebookId;
        Guid audioId;
        Guid collectionId;
        Guid noteId;
        Guid topicId;
        Guid writingFolderId;
        Guid writingDocumentId;
        Guid sharedWorkId;

        await using (var db = new NostosDbContext(options))
        {
            (await db.Database.CanConnectAsync()).Should().BeTrue();

            var created = await db.Database.EnsureCreatedAsync();
            created.Should().BeTrue(
                "the dedicated spike database must start empty so the current model is what creates it");

            var createScript = db.Database.GenerateCreateScript();
            createScript.Should().Contain("CREATE TABLE \"Books\"");
            createScript.Should().Contain("CREATE TABLE \"WritingNotes\"");
            createScript.Should().Contain("uuid");
            createScript.Should().Contain("timestamp with time zone");
            createScript.Should().Contain("CK_LibraryCommandReceipts_Bounds");
            createScript.Should().Contain("CK_NoteCommandReceipts_Bounds");
            createScript.Should().Contain(
                "WHERE \"NormalizedIsbn\" IS NOT NULL",
                "the filtered unique identity index must survive provider translation");
            createScript.Should().Contain(
                "WHERE \"NormalizedAsin\" IS NOT NULL",
                "the filtered unique identity index must survive provider translation");

            var physical = new PhysicalBookModel
            {
                Title = "The Republic",
                Author = "Plato",
                CreatedAt = createdAt,
                PageCount = 416,
            };
            var ebook = new EBookModel
            {
                Title = "The Republic",
                Author = "Plato",
                CreatedAt = createdAt,
                Isbn = normalizedIsbn,
                NormalizedIsbn = normalizedIsbn,
                Metadata =
                {
                    Publisher = "Penguin Classics",
                    Translator = "Desmond Lee",
                },
                Progress =
                {
                    LastLocation = "epubcfi(/6/4)",
                    ProgressPercent = 17,
                    LastReadAt = lastReadAt,
                },
            };
            var audio = new AudioBookModel
            {
                Title = "The Republic",
                Author = "Plato",
                CreatedAt = createdAt,
                Asin = normalizedAsin,
                NormalizedAsin = normalizedAsin,
                Duration = "13:42:00",
                Narrator = "Sample Narrator",
            };

            db.Books.AddRange(physical, ebook, audio);
            await db.SaveChangesAsync();

            physical.WorkId.Should().NotBeEmpty();
            ebook.WorkId.Should().Be(physical.WorkId);
            audio.WorkId.Should().Be(physical.WorkId);
            (await db.Works.CountAsync()).Should().Be(1);

            sharedWorkId = physical.WorkId;
            physicalId = physical.Id;
            ebookId = ebook.Id;
            audioId = audio.Id;

            var collection = new CollectionModel { Name = "Classics" };
            var note = new NoteModel
            {
                Book = ebook,
                Content = "Justice is treated as an order of the soul.",
                RawContent = "Justice is treated as an order of the soul.",
                CaptureSource = "text",
                ProcessingMode = "verbatim",
                SourceAnchorKind = "epub_cfi",
                SourceAnchorValue = "epubcfi(/6/4)",
                AnchorVerified = true,
                CreatedAt = createdAt,
            };
            var topic = new TopicModel { Topic = "Justice" };
            var writingFolder = new WritingModel
            {
                Name = "Republic notes",
                Type = WritingType.Folder,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            };
            var writingDocument = new WritingModel
            {
                Name = "Justice draft",
                Type = WritingType.Document,
                Content = "# Justice\n\nA first draft.",
                Parent = writingFolder,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            };

            db.Collections.Add(collection);
            db.BookCollections.Add(new BookCollectionModel
            {
                Book = ebook,
                Collection = collection,
                AddedAt = createdAt,
            });
            db.Notes.Add(note);
            db.Topics.Add(topic);
            db.NoteTopics.Add(new NoteTopicModel
            {
                Note = note,
                Topic = topic,
            });
            db.Writings.AddRange(writingFolder, writingDocument);
            db.WritingNotes.Add(new WritingNoteModel
            {
                Writing = writingDocument,
                Note = note,
                AddedAt = createdAt,
            });
            db.LibraryStates.Add(new LibraryState
            {
                Id = LibraryState.WellKnownId,
                SingletonSlot = LibraryState.SingletonSentinel,
                StateVersion = "1",
                UpdatedAt = createdAt,
            });
            db.LibraryCommandReceipts.Add(new LibraryCommandReceipt
            {
                ClientId = "postgres-spike",
                IdempotencyKey = "library-1",
                CommandKind = "create",
                ResponseJson = "{}",
                CreatedAt = createdAt,
            });
            db.NoteCommandReceipts.Add(new NoteCommandReceipt
            {
                ClientId = "postgres-spike",
                IdempotencyKey = "note-1",
                Command = "capture",
                ResultJson = "{}",
                CreatedAtUtc = createdAt,
            });

            await db.SaveChangesAsync();

            collectionId = collection.Id;
            noteId = note.Id;
            topicId = topic.Id;
            writingFolderId = writingFolder.Id;
            writingDocumentId = writingDocument.Id;
        }

        await using (var db = new NostosDbContext(options))
        {
            var books = await db.Books
                .Include(b => b.Work)
                .Include(b => b.BookCollections)
                .Where(b => b.Title.Contains("Republic"))
                .OrderBy(b => b.CreatedAt)
                .ToListAsync();

            books.Should().HaveCount(3);
            books.Select(b => b.WorkId).Distinct().Should().ContainSingle().Which.Should().Be(sharedWorkId);
            books.Should().ContainSingle(b => b.Id == physicalId && b is PhysicalBookModel);
            books.Should().ContainSingle(b => b.Id == ebookId && b is EBookModel);
            books.Should().ContainSingle(b => b.Id == audioId && b is AudioBookModel);

            var reloadedEbook = books.OfType<EBookModel>().Single(b => b.Id == ebookId);
            reloadedEbook.Metadata.Publisher.Should().Be("Penguin Classics");
            reloadedEbook.Progress.ProgressPercent.Should().Be(17);
            reloadedEbook.Progress.LastReadAt.Should().Be(lastReadAt);
            reloadedEbook.Progress.LastReadAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
            reloadedEbook.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
            reloadedEbook.BookCollections.Should().ContainSingle(x => x.CollectionId == collectionId);

            var reloadedNote = await db.Notes
                .Include(n => n.NoteTopics)
                .ThenInclude(nc => nc.Topic)
                .SingleAsync(n => n.Id == noteId);
            reloadedNote.BookId.Should().Be(ebookId);
            reloadedNote.NoteTopics.Should().ContainSingle();
            reloadedNote.NoteTopics.Single().TopicId.Should().Be(topicId);
            reloadedNote.NoteTopics.Single().Topic.Topic.Should().Be("Justice");

            var writingDocument = await db.Writings.SingleAsync(w => w.Id == writingDocumentId);
            writingDocument.ParentId.Should().Be(writingFolderId);
            writingDocument.Content.Should().Contain("first draft");

            var keptNotes = await db.WritingNotes
                .Include(wn => wn.Note)
                .ThenInclude(n => n.Book)
                .Where(wn => wn.WritingId == writingDocumentId)
                .ToListAsync();
            keptNotes.Should().ContainSingle();
            keptNotes.Single().NoteId.Should().Be(noteId);
            keptNotes.Single().AddedAt.Should().Be(createdAt);

            (await db.LibraryCommandReceipts.CountAsync(r =>
                    r.ClientId == "postgres-spike" && r.IdempotencyKey == "library-1"))
                .Should().Be(1);
            (await db.NoteCommandReceipts.CountAsync(r =>
                    r.ClientId == "postgres-spike" && r.IdempotencyKey == "note-1"))
                .Should().Be(1);

            (await db.Books.CountAsync(b => b.NormalizedIsbn == null))
                .Should().BeGreaterThanOrEqualTo(2,
                    "the filtered unique ISBN index must allow multiple NULL values");
        }

        await using (var db = new NostosDbContext(options))
        {
            var duplicate = new EBookModel
            {
                Title = "Duplicate ISBN probe",
                Author = "Nostos",
                NormalizedIsbn = normalizedIsbn,
            };
            db.Books.Add(duplicate);

            Func<Task> saveDuplicate = () => db.SaveChangesAsync();
            await saveDuplicate.Should().ThrowAsync<DbUpdateException>(
                "the filtered unique ISBN index must reject a second non-null identity");
        }

        await using (var db = new NostosDbContext(options))
        {
            var nonEmptyCollection = await db.Collections.SingleAsync(c => c.Id == collectionId);
            db.Collections.Remove(nonEmptyCollection);

            Func<Task> deleteNonEmptyCollection = () => db.SaveChangesAsync();
            await deleteNonEmptyCollection.Should().ThrowAsync<DbUpdateException>(
                "collection membership uses a restrictive FK and must be enforced by PostgreSQL");
        }

        await using (var db = new NostosDbContext(options))
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Topics.Add(new TopicModel { Topic = "Rolled back topic" });
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using (var db = new NostosDbContext(options))
        {
            (await db.Topics.AnyAsync(c => c.Topic == "Rolled back topic"))
                .Should().BeFalse("a rolled-back PostgreSQL transaction must not leak writes");

            var folder = await db.Writings.SingleAsync(w => w.Id == writingFolderId);
            db.Writings.Remove(folder);
            await db.SaveChangesAsync();

            (await db.Writings.AnyAsync(w => w.Id == writingDocumentId))
                .Should().BeFalse("the configured writing-tree cascade must be enforced by PostgreSQL");
            (await db.WritingNotes.AnyAsync(wn => wn.WritingId == writingDocumentId))
                .Should().BeFalse("deleting writing tree cascades to its kept note memberships on PostgreSQL");
        }

        await using (var db = new NostosDbContext(options))
        {
            var noteCascadeDoc = new WritingModel
            {
                Name = "Note cascade test doc",
                Type = WritingType.Document,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
            };
            db.Writings.Add(noteCascadeDoc);
            db.WritingNotes.Add(new WritingNoteModel
            {
                Writing = noteCascadeDoc,
                NoteId = noteId,
                AddedAt = createdAt,
            });
            await db.SaveChangesAsync();

            // Deleting the note cascades away WritingNotes on PostgreSQL without touching the writing
            var noteToDelete = await db.Notes.SingleAsync(n => n.Id == noteId);
            db.Notes.Remove(noteToDelete);
            await db.SaveChangesAsync();

            (await db.WritingNotes.AnyAsync(wn => wn.WritingId == noteCascadeDoc.Id)).Should().BeFalse(
                "deleting note cascades to its WritingNotes on PostgreSQL");
            (await db.Writings.AnyAsync(w => w.Id == noteCascadeDoc.Id)).Should().BeTrue();
        }
    }
}
