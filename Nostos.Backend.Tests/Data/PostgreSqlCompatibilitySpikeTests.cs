using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Npgsql;
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
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(Program).Assembly.FullName))
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

            // Create the schema exactly the way the product bootstraps a fresh
            // database (DatabaseBootstrapService.EnsureReadyAsync: generated
            // model script plus the migration-history baseline). PostgreSQL is a
            // compatibility provider behind the same product model, not an
            // EF-migration target — see docs/cloud/postgresql-compatibility-spike.md
            // and PersistenceRegistration (migrations assembly is configured
            // only in the SQLite branch).
            await new DatabaseBootstrapService(db).EnsureReadyAsync();

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

            createScript.Should().Contain("CREATE TABLE \"MigrationJobRecords\"");
            createScript.Should().Contain("CREATE TABLE \"MigrationSessionRecords\"");
            createScript.Should().Contain("CREATE TABLE \"MigrationChunkReceiptRecords\"");
            createScript.Should().Contain("CREATE TABLE \"MigrationExportArtifactRecords\"");
            createScript.Should().Contain("CREATE TABLE \"MigrationStorageReservations\"");
            createScript.Should().Contain("CK_MigrationJobRecords_IdempotencyKey");
            createScript.Should().Contain("CK_MigrationSessionRecords_ChunkSize");
            createScript.Should().Contain("CK_MigrationChunkReceiptRecords_Bounds");
            createScript.Should().Contain(
                "PRIMARY KEY (\"SessionId\", \"ChunkIndex\")",
                "the composite chunk receipt key must survive provider translation");
            createScript.Should().Contain(
                "IX_MigrationJobRecords_IdempotencyKey",
                "the unique job idempotency index must survive provider translation");

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

        // --- Durable migration transfer records (issue #679) ---
        // Representative lease/session/receipt/artifact/reservation writes on
        // the real Npgsql provider, including unique keys, the composite chunk
        // primary key, integer-version concurrency, and the lease/expiry
        // predicates the later slices execute as single SQL statements.
        Guid migrationJobId;
        Guid migrationSessionId;
        Guid expiredJobId;
        Guid expiredSessionId;
        Guid expiredReservationId;
        var migrationNow = DateTime.UtcNow;

        await using (var db = new NostosDbContext(options))
        {
            var job = new MigrationJobRecord
            {
                Direction = (int)MigrationDirection.Import,
                State = (int)MigrationJobState.Transferring,
                RecoveryStatus = (int)MigrationRecoveryStatus.NotRequired,
                ProgressPhase = (int)MigrationProgressPhase.Transferring,
                ProgressBytesProcessed = 16L * 1024 * 1024,
                IdempotencyKey = "pg-spike-job-1",
                CreationPayloadHash = new string('a', 64),
                CreatedAtUtc = migrationNow,
                UpdatedAtUtc = migrationNow,
                ExpiresAtUtc = migrationNow.AddDays(7),
                AttemptNumber = 1,
                ReservedStorageBytes = 64L * 1024 * 1024,
            };
            var session = new MigrationSessionRecord
            {
                JobId = job.Id,
                Purpose = (int)MigrationSessionPurpose.Import,
                State = (int)MigrationSessionState.Receiving,
                TotalBytes = 32L * 1024 * 1024,
                ChunkSize = MigrationContractLimits.DefaultChunkBytes,
                TotalChunks = 2,
                FileIdentitySizeBytes = 32L * 1024 * 1024,
                FileIdentitySha256 = new string('b', 64),
                IdempotencyKey = "pg-spike-session-1",
                CreationPayloadHash = new string('c', 64),
                ReceivedBytes = 16L * 1024 * 1024,
                CreatedAtUtc = migrationNow,
                UpdatedAtUtc = migrationNow,
                ExpiresAtUtc = migrationNow.AddHours(24),
                StorageKey = "uploads/pg-spike/session-1/archive.part",
            };
            var expiredJob = new MigrationJobRecord
            {
                Direction = (int)MigrationDirection.Import,
                State = (int)MigrationJobState.Transferring,
                IdempotencyKey = "pg-spike-expired-job",
                CreationPayloadHash = new string('a', 64),
                CreatedAtUtc = migrationNow.AddMinutes(-20),
                UpdatedAtUtc = migrationNow.AddMinutes(-20),
                ExpiresAtUtc = migrationNow.AddMinutes(-5),
                AttemptNumber = 1,
            };
            var expiredSession = new MigrationSessionRecord
            {
                JobId = expiredJob.Id,
                Purpose = (int)MigrationSessionPurpose.Import,
                State = (int)MigrationSessionState.Expired,
                TotalBytes = 16L * 1024 * 1024,
                ChunkSize = MigrationContractLimits.DefaultChunkBytes,
                TotalChunks = 1,
                FileIdentitySizeBytes = 16L * 1024 * 1024,
                FileIdentitySha256 = new string('b', 64),
                IdempotencyKey = "pg-spike-expired-session",
                CreationPayloadHash = new string('c', 64),
                CreatedAtUtc = migrationNow.AddMinutes(-20),
                UpdatedAtUtc = migrationNow.AddMinutes(-20),
                ExpiresAtUtc = migrationNow.AddMinutes(-5),
                StorageKey = "uploads/pg-spike/expired/archive.part",
            };
            var expiredReservation = new MigrationStorageReservationRecord
            {
                Purpose = (int)MigrationSessionPurpose.Import,
                ReservedBytes = 1024,
                MaterializedBytes = 0,
                CreatedAtUtc = migrationNow.AddMinutes(-5),
                ExpiresAtUtc = migrationNow.AddMinutes(-1),
            };

            db.MigrationJobRecords.AddRange(job, expiredJob);
            db.MigrationSessionRecords.AddRange(session, expiredSession);
            db.MigrationChunkReceiptRecords.Add(new MigrationChunkReceiptRecord
            {
                SessionId = session.Id,
                ChunkIndex = 0,
                OffsetBytes = 0,
                LengthBytes = 16 * 1024 * 1024,
                Sha256 = new string('d', 64),
                ReceivedAtUtc = migrationNow,
            });
            db.MigrationExportArtifactRecords.Add(new MigrationExportArtifactRecord
            {
                JobId = job.Id,
                State = (int)MigrationExportArtifactState.Preparing,
                StorageKey = "exports/pg-spike/library.nostos",
                FileName = "library.nostos",
                ContentType = "application/vnd.nostos.portable+zip",
                SizeBytes = 0,
                CreatedAtUtc = migrationNow,
                ExpiresAtUtc = migrationNow.AddDays(7),
            });
            db.MigrationStorageReservations.AddRange(
                new MigrationStorageReservationRecord
                {
                    Purpose = (int)MigrationSessionPurpose.Import,
                    ReservedBytes = 64L * 1024 * 1024,
                    MaterializedBytes = 16L * 1024 * 1024,
                    CreatedAtUtc = migrationNow,
                    ExpiresAtUtc = migrationNow.AddMinutes(15),
                    ClaimedJobId = job.Id,
                },
                expiredReservation);

            await db.SaveChangesAsync();

            migrationJobId = job.Id;
            migrationSessionId = session.Id;
            expiredJobId = expiredJob.Id;
            expiredSessionId = expiredSession.Id;
            expiredReservationId = expiredReservation.Id;

            var reloadedSession = await db.MigrationSessionRecords
                .SingleAsync(s => s.Id == migrationSessionId);
            reloadedSession.StorageKey.Should().Be("uploads/pg-spike/session-1/archive.part");
            reloadedSession.FileIdentitySha256.Should().Be(new string('b', 64));
            reloadedSession.ExpiresAtUtc.Should().BeCloseTo(migrationNow.AddHours(24), TimeSpan.FromMilliseconds(1));
            (await db.MigrationChunkReceiptRecords.CountAsync()).Should().Be(1);
            (await db.MigrationExportArtifactRecords.CountAsync()).Should().Be(1);
            (await db.MigrationStorageReservations.CountAsync()).Should().Be(2);
        }

        await using (var db = new NostosDbContext(options))
        {
            db.MigrationJobRecords.Add(new MigrationJobRecord
            {
                IdempotencyKey = "pg-spike-job-1",
                CreationPayloadHash = new string('a', 64),
                CreatedAtUtc = migrationNow,
                UpdatedAtUtc = migrationNow,
                ExpiresAtUtc = migrationNow.AddDays(7),
                AttemptNumber = 1,
            });

            Func<Task> saveDuplicateJob = () => db.SaveChangesAsync();
            await saveDuplicateJob.Should().ThrowAsync<DbUpdateException>(
                "the unique job idempotency key must be enforced by PostgreSQL");
        }

        await using (var db = new NostosDbContext(options))
        {
            db.MigrationSessionRecords.Add(new MigrationSessionRecord
            {
                JobId = migrationJobId,
                Purpose = (int)MigrationSessionPurpose.Import,
                State = (int)MigrationSessionState.Created,
                TotalBytes = 32L * 1024 * 1024,
                ChunkSize = MigrationContractLimits.DefaultChunkBytes,
                TotalChunks = 2,
                FileIdentitySizeBytes = 32L * 1024 * 1024,
                FileIdentitySha256 = new string('f', 64),
                IdempotencyKey = "pg-spike-session-1",
                CreationPayloadHash = new string('c', 64),
                CreatedAtUtc = migrationNow,
                UpdatedAtUtc = migrationNow,
                ExpiresAtUtc = migrationNow.AddHours(24),
                StorageKey = "uploads/pg-spike/session-2/archive.part",
            });

            Func<Task> saveDuplicateSession = () => db.SaveChangesAsync();
            await saveDuplicateSession.Should().ThrowAsync<DbUpdateException>(
                "the unique (JobId, IdempotencyKey) session key must be enforced by PostgreSQL");
        }

        await using (var db = new NostosDbContext(options))
        {
            db.MigrationChunkReceiptRecords.Add(new MigrationChunkReceiptRecord
            {
                SessionId = migrationSessionId,
                ChunkIndex = 0,
                OffsetBytes = 0,
                LengthBytes = 16 * 1024 * 1024,
                Sha256 = new string('d', 64),
                ReceivedAtUtc = migrationNow,
            });

            Func<Task> saveDuplicateChunk = () => db.SaveChangesAsync();
            await saveDuplicateChunk.Should().ThrowAsync<DbUpdateException>(
                "the composite chunk receipt primary key must be enforced by PostgreSQL");
        }

        await using (var db = new NostosDbContext(options))
        {
            var leaseToken = "pg-lease-" + Guid.NewGuid().ToString("N");
            var expectedVersion = await db.MigrationJobRecords
                .Where(j => j.Id == migrationJobId)
                .Select(j => j.Version)
                .SingleAsync();

            var acquired = await db.MigrationJobRecords
                .Where(j =>
                    j.Id == migrationJobId &&
                    j.Version == expectedVersion &&
                    j.MigrationLeaseToken == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(j => j.MigrationLeaseToken, leaseToken)
                    .SetProperty(j => j.LeaseExpiresAtUtc, migrationNow.AddMinutes(5))
                    .SetProperty(j => j.Version, expectedVersion + 1));
            acquired.Should().Be(1, "the first guarded lease acquisition must win");

            var stale = await db.MigrationJobRecords
                .Where(j =>
                    j.Id == migrationJobId &&
                    j.Version == expectedVersion &&
                    j.MigrationLeaseToken == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(j => j.MigrationLeaseToken, leaseToken + "-stale")
                    .SetProperty(j => j.Version, expectedVersion + 1));
            stale.Should().Be(0, "a stale guarded lease acquisition must lose on the version predicate");
        }

        // The lease/expiry predicates later slices execute as single SQL
        // statements must translate on PostgreSQL too.
        await using (var db = new NostosDbContext(options))
        {
            var blocked = await db.MigrationJobRecords
                .Where(j => j.Id == migrationJobId
                    && (j.MigrationLeaseToken == null || j.LeaseExpiresAtUtc <= migrationNow))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(j => j.MigrationLeaseToken, "pg-lease-blocked"));
            blocked.Should().Be(0, "an unexpired lease must block a second caller");

            var reclaimInstant = migrationNow.AddMinutes(6);
            var reclaimed = await db.MigrationJobRecords
                .Where(j => j.Id == migrationJobId
                    && (j.MigrationLeaseToken == null || j.LeaseExpiresAtUtc <= reclaimInstant))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(j => j.MigrationLeaseToken, "pg-lease-reclaimed")
                    .SetProperty(j => j.LeaseExpiresAtUtc, reclaimInstant.AddMinutes(5))
                    .SetProperty(j => j.Version, j => j.Version + 1));
            reclaimed.Should().Be(1, "an expired lease must be reclaimable on PostgreSQL");

            var renewed = await db.MigrationJobRecords
                .Where(j => j.Id == migrationJobId
                    && j.MigrationLeaseToken == "pg-lease-reclaimed"
                    && j.LeaseExpiresAtUtc > migrationNow)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(j => j.LeaseExpiresAtUtc, migrationNow.AddMinutes(30)));
            renewed.Should().Be(1, "the current owner must be able to renew");

            var staleRenewal = await db.MigrationJobRecords
                .Where(j => j.Id == migrationJobId
                    && j.MigrationLeaseToken == "pg-lease-superseded"
                    && j.LeaseExpiresAtUtc > migrationNow)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(j => j.LeaseExpiresAtUtc, migrationNow.AddMinutes(30)));
            staleRenewal.Should().Be(0, "a superseded token must never renew");
        }

        await using (var db = new NostosDbContext(options))
        {
            (await db.MigrationJobRecords
                    .Where(j => j.ExpiresAtUtc < migrationNow)
                    .Select(j => j.Id)
                    .ToListAsync())
                .Should().Equal([expiredJobId], "only the expired job must be discovered");

            (await db.MigrationSessionRecords
                    .Where(s => s.ExpiresAtUtc < migrationNow)
                    .Select(s => s.Id)
                    .ToListAsync())
                .Should().Equal([expiredSessionId], "only the expired session must be discovered");

            (await db.MigrationStorageReservations
                    .Where(r => r.ReleasedAtUtc == null && r.ExpiresAtUtc < migrationNow)
                    .Select(r => r.Id)
                    .ToListAsync())
                .Should().Equal([expiredReservationId], "only the expired reservation must be swept");

            (await db.MigrationJobRecords
                    .OrderBy(j => j.UpdatedAtUtc)
                    .Select(j => j.Id)
                    .ToListAsync())
                .Should().Equal([expiredJobId, migrationJobId], "UpdatedAtUtc must order in SQL");
        }

        // Verify the PostgreSQL physical schema the product bootstrap created:
        // tables, Npgsql column mappings, unique/sweep indexes, check constraints
        // and cascading FKs.
        await using (var db = new NostosDbContext(options))
        {
            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN " +
                    "('MigrationJobRecords','MigrationSessionRecords','MigrationChunkReceiptRecords','MigrationExportArtifactRecords','MigrationStorageReservations')"))
                .Should().Be(5, "the product model bootstrap must create all five operational tables");

            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='MigrationJobRecords' " +
                    "AND column_name='Id' AND data_type='uuid'")).Should().Be(1);
            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='MigrationJobRecords' " +
                    "AND column_name='Version' AND data_type='bigint'")).Should().Be(1);
            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='MigrationJobRecords' " +
                    "AND column_name IN ('CreatedAtUtc','UpdatedAtUtc','ExpiresAtUtc','LeaseExpiresAtUtc','HeartbeatAtUtc','CancelledAtUtc','CompletedAtUtc') " +
                    "AND data_type='timestamp with time zone'")).Should().Be(7);
            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='MigrationSessionRecords' " +
                    "AND column_name='StorageKey' AND data_type='character varying' AND character_maximum_length=512")).Should().Be(1);
            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='MigrationChunkReceiptRecords' " +
                    "AND column_name='ChunkIndex' AND data_type='integer'")).Should().Be(1);
            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='MigrationStorageReservations' " +
                    "AND column_name='ReservedBytes' AND data_type='bigint'")).Should().Be(1);

            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM pg_indexes WHERE schemaname='public' AND indexname='IX_MigrationJobRecords_IdempotencyKey' " +
                    "AND indexdef LIKE '%UNIQUE%'"))
                .Should().Be(1, "the job idempotency index must be unique on PostgreSQL");
            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM pg_indexes WHERE schemaname='public' AND indexname='IX_MigrationSessionRecords_JobId_IdempotencyKey' " +
                    "AND indexdef LIKE '%UNIQUE%'"))
                .Should().Be(1, "the per-job session idempotency index must be unique on PostgreSQL");
            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM pg_indexes WHERE schemaname='public' AND indexname IN " +
                    "('IX_MigrationJobRecords_State_LeaseExpiresAtUtc','IX_MigrationJobRecords_ExpiresAtUtc','IX_MigrationJobRecords_UpdatedAtUtc'," +
                    "'IX_MigrationSessionRecords_JobId_State','IX_MigrationSessionRecords_ExpiresAtUtc'," +
                    "'IX_MigrationExportArtifactRecords_State_ExpiresAtUtc','IX_MigrationStorageReservations_ExpiresAtUtc')"))
                .Should().Be(7, "every declared worker/sweep index must exist on PostgreSQL");

            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM pg_constraint WHERE contype='c' AND conname IN " +
                    "('CK_MigrationJobRecords_ReservedStorageBytes','CK_MigrationJobRecords_AttemptNumber','CK_MigrationJobRecords_IdempotencyKey','CK_MigrationJobRecords_LeaseToken'," +
                    "'CK_MigrationSessionRecords_TotalBytes','CK_MigrationSessionRecords_ChunkSize','CK_MigrationSessionRecords_TotalChunks'," +
                    "'CK_MigrationSessionRecords_FileIdentitySize','CK_MigrationSessionRecords_ReceivedBytes'," +
                    "'CK_MigrationChunkReceiptRecords_Bounds')"))
                .Should().Be(10, "every declared check constraint must exist on PostgreSQL");

            (await ScalarCountAsync(
                    db,
                    "SELECT COUNT(*) FROM pg_constraint c JOIN pg_class t ON t.oid = c.conrelid " +
                    "WHERE c.contype='f' AND c.confdeltype='c' AND t.relname IN " +
                    "('MigrationSessionRecords','MigrationExportArtifactRecords','MigrationChunkReceiptRecords')"))
                .Should().Be(3, "all three operational relationships must cascade on PostgreSQL");
        }

        await using (var first = new NostosDbContext(options))
        await using (var second = new NostosDbContext(options))
        {
            var firstJob = await first.MigrationJobRecords.SingleAsync(j => j.Id == migrationJobId);
            var secondJob = await second.MigrationJobRecords.SingleAsync(j => j.Id == migrationJobId);

            firstJob.ProgressBytesProcessed = 20L * 1024 * 1024;
            firstJob.Version += 1;
            await first.SaveChangesAsync();

            secondJob.ProgressBytesProcessed = 30L * 1024 * 1024;
            secondJob.Version += 1;
            Func<Task> staleSave = () => second.SaveChangesAsync();
            await staleSave.Should().ThrowAsync<DbUpdateConcurrencyException>(
                "the integer Version concurrency token must be enforced by PostgreSQL");
        }

        // --- Slice 5: EfMigrationJobStore on the real Npgsql provider ---
        // The same contract-critical guarded statements the SQLite store tests
        // exercise must translate and run on PostgreSQL: idempotent create,
        // conditional lease acquire/steal-after-expiry, lease-guarded progress
        // and transition, direction-aware invalid transitions, cancel, retry
        // and recovery discovery.
        var storeClock = new ManualTimeProvider(
            new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var storeKey = "pg-store-job-" + Guid.NewGuid().ToString("N");

        await using (var db = new NostosDbContext(options))
        {
            var store = new EfMigrationJobStore(db, storeClock);

            var created = await store.CreateAsync(
                MigrationDirection.Import,
                storeKey,
                CancellationToken.None);
            created.IsConflict.Should().BeFalse();
            created.WasReplay.Should().BeFalse();
            created.Resource!.State.Should().Be(MigrationJobState.Pending);
            var storeJobId = created.Resource.Id;

            var replay = await store.CreateAsync(
                MigrationDirection.Import,
                storeKey,
                CancellationToken.None);
            replay.WasReplay.Should().BeTrue();
            replay.Resource!.Id.Should().Be(storeJobId);

            var conflict = await store.CreateAsync(
                MigrationDirection.Export,
                storeKey,
                CancellationToken.None);
            conflict.IsConflict.Should().BeTrue();
            conflict.Conflict!.Kind.Should().Be(
                MigrationIdempotencyConflictKind.KeyReusedWithDifferentPayload);

            // A real two-creator race exercises the PostgreSQL unique-violation
            // detection and replay classification on the provider itself.
            var raceKey = "pg-store-race-" + Guid.NewGuid().ToString("N");
            await using (var firstRaceDb = new NostosDbContext(options))
            await using (var secondRaceDb = new NostosDbContext(options))
            {
                var firstRaceStore = new EfMigrationJobStore(firstRaceDb, storeClock);
                var secondRaceStore = new EfMigrationJobStore(secondRaceDb, storeClock);
                var raceResults = await Task.WhenAll(
                    firstRaceStore.CreateAsync(
                        MigrationDirection.Import,
                        raceKey,
                        CancellationToken.None),
                    secondRaceStore.CreateAsync(
                        MigrationDirection.Import,
                        raceKey,
                        CancellationToken.None));

                raceResults.Count(result => result.IsConflict).Should().Be(0);
                raceResults.Count(result => !result.WasReplay).Should().Be(1);
                raceResults.Count(result => result.WasReplay).Should().Be(1);
            }

            var token = await store.TryAcquireLeaseAsync(
                storeJobId,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            token.Should().NotBeNull("the first guarded lease acquisition must win on PostgreSQL");

            var secondAcquire = await store.TryAcquireLeaseAsync(
                storeJobId,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            secondAcquire.Should().BeNull("an unexpired lease must block on PostgreSQL");

            await store.UpdateProgressAsync(
                storeJobId,
                new MigrationProgress(MigrationProgressPhase.Preparing, 5, 10),
                token!,
                CancellationToken.None);

            var preparing = await store.TransitionAsync(
                storeJobId,
                MigrationJobState.Preparing,
                token!,
                CancellationToken.None);
            preparing.State.Should().Be(MigrationJobState.Preparing);

            var wrongTokenRenew = await store.RenewLeaseAsync(
                storeJobId,
                "pg-wrong-token",
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            wrongTokenRenew.Should().BeFalse("a superseded token must never renew");

            var renewed = await store.RenewLeaseAsync(
                storeJobId,
                token!,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            renewed.Should().BeTrue("the current owner must renew on PostgreSQL");

            var illegalTransition = () => store.TransitionAsync(
                storeJobId,
                MigrationJobState.Completed,
                token!,
                CancellationToken.None);
            var illegalException =
                await illegalTransition.Should().ThrowAsync<MigrationJobStoreException>();
            illegalException.Which.Code.Should().Be(
                MigrationJobStoreErrorCodes.InvalidState,
                "import Preparing -> Completed is not an allowed transition");

            storeClock.Advance(TimeSpan.FromMinutes(6));
            var stolen = await store.TryAcquireLeaseAsync(
                storeJobId,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            stolen.Should().NotBeNull("an expired lease must be reclaimable on PostgreSQL");
            stolen.Should().NotBe(token);

            var staleRenew = await store.RenewLeaseAsync(
                storeJobId,
                token!,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            staleRenew.Should().BeFalse("the superseded owner must not renew after takeover");

            await store.ReleaseLeaseAsync(storeJobId, stolen!, CancellationToken.None);
            (await store.GetAsync(storeJobId, CancellationToken.None))!
                .LeaseToken.Should().BeNull();

            await store.CancelAsync(
                storeJobId,
                new MigrationCancelRequest("pg spike"),
                CancellationToken.None);
            (await store.GetAsync(storeJobId, CancellationToken.None))!
                .State.Should().Be(MigrationJobState.Cancelled);

            var retried = await store.RetryAsync(
                storeJobId,
                new MigrationRetryRequest(),
                CancellationToken.None);
            retried.State.Should().Be(MigrationJobState.Pending);

            var boundaryCreated = await store.CreateAsync(
                MigrationDirection.Import,
                "pg-store-boundary-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None);
            var boundaryId = boundaryCreated.Resource!.Id;
            var boundaryToken = await store.TryAcquireLeaseAsync(
                boundaryId,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            boundaryToken.Should().NotBeNull();
            await store.TransitionAsync(
                boundaryId,
                MigrationJobState.Preparing,
                boundaryToken!,
                CancellationToken.None);
            await store.TransitionAsync(
                boundaryId,
                MigrationJobState.Transferring,
                boundaryToken!,
                CancellationToken.None);
            await store.TransitionAsync(
                boundaryId,
                MigrationJobState.Validating,
                boundaryToken!,
                CancellationToken.None);
            await store.TransitionAsync(
                boundaryId,
                MigrationJobState.ReadyToActivate,
                boundaryToken!,
                CancellationToken.None);
            await store.TransitionAsync(
                boundaryId,
                MigrationJobState.Activating,
                boundaryToken!,
                CancellationToken.None);

            var cannotCancel = () => store.CancelAsync(
                boundaryId,
                new MigrationCancelRequest(),
                CancellationToken.None);
            var cannotCancelException =
                await cannotCancel.Should().ThrowAsync<MigrationJobStoreException>();
            cannotCancelException.Which.Code.Should().Be(
                MigrationJobStoreErrorCodes.CannotCancel,
                "the activation boundary is a point of no return");

            storeClock.Advance(TimeSpan.FromMinutes(6));
            var recovery = await store.GetJobsNeedingRecoveryAsync(
                storeClock.GetUtcNow(),
                CancellationToken.None);
            recovery.Should().Contain(
                job => job.Id == boundaryId,
                "a leased activating job past its lease expiry must be recoverable");
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    // Slice 3 review fix 2: PostgreSQL serializable isolation prevents the
    // over-reservation by aborting one admission with SQLSTATE 40001. The
    // capacity service must retry that abort and surface a normal rejection,
    // so two concurrent admissions end as one admitted + one rejected.
    [Fact]
    [Trait("Category", "PostgresSpike")]
    public async Task Concurrent_capacity_admissions_end_as_one_admission_and_one_rejection_on_postgresql()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        // A dedicated schema keeps this case independent of the shared public
        // schema the compatibility spike bootstraps, and keeps its tables out
        // of the other test's HasTables() bootstrap decision regardless of
        // execution order.
        var schema = "nostos_capacity_" + Guid.NewGuid().ToString("N");
        var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = schema,
        };
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseNpgsql(connectionBuilder.ConnectionString)
            .Options;

        try
        {
            await using (var setup = new NostosDbContext(options))
            {
                await setup.Database.ExecuteSqlRawAsync($"CREATE SCHEMA \"{schema}\"");
                await setup.Database.ExecuteSqlRawAsync(setup.Database.GenerateCreateScript());
            }

            var volume = new FixedTransferVolume(freeBytes: 100_000_000);
            var storageOptions = new TransferStorageOptions
            {
                DiskSafetyMarginBytes = 0,
                DiskSafetyMarginPercent = 0,
            };

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<TransferReservationResult> ReserveAsync()
            {
                await using var db = new NostosDbContext(options);
                var capacity = new TransferStorageCapacity(
                    db,
                    volume,
                    Options.Create(storageOptions),
                    timeProvider: null);
                await gate.Task;
                return await capacity.TryReserveAsync(
                    60_000_000,
                    MigrationSessionPurpose.Import,
                    TimeSpan.FromMinutes(15),
                    default);
            }

            var first = ReserveAsync();
            var second = ReserveAsync();
            gate.SetResult();
            var results = await Task.WhenAll(first, second);

            results.Count(result => result.IsAdmitted).Should().Be(
                1,
                "the serialization abort must be retried into a normal capacity rejection");

            await using var verify = new NostosDbContext(options);
            (await verify.MigrationStorageReservations.CountAsync()).Should().Be(1);
        }
        finally
        {
            try
            {
                await using var cleanup = new NostosDbContext(options);
                await cleanup.Database.ExecuteSqlRawAsync(
                    $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            }
            catch (Exception)
            {
                // Best-effort cleanup of the disposable schema; the CI
                // PostgreSQL container is discarded with the job.
            }
        }
    }

    private static async Task<long> ScalarCountAsync(NostosDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    private sealed class FixedTransferVolume(long freeBytes) : ITransferVolume
    {
        public long AvailableFreeSpaceBytes => freeBytes;

        public long TotalSizeBytes => freeBytes;
    }
}
