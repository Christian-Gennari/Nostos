using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Data;

// Slice 2 of issue #679: the AddDurableMigrationTransferJobs migration must be
// provably additive on a real SQLite file. This builds a database at the
// previous migration, inserts representative user data, copies the file,
// migrates the copy, and proves both that the copy gained exactly the five new
// empty tables and that the original file was never touched. It finishes by
// rolling the disposable copy back and proving only the new tables disappear.
public sealed class MigrationSchemaUpgradeTests : IDisposable
{
    private const string PreviousMigration = "20261003093414_RenameConceptsToTopics";

    private static readonly string[] NewTables =
    [
        "MigrationJobRecords",
        "MigrationSessionRecords",
        "MigrationChunkReceiptRecords",
        "MigrationExportArtifactRecords",
        "MigrationStorageReservations",
    ];

    private readonly List<string> _directories = new();

    [Fact]
    public async Task Additive_migration_upgrades_and_downgrades_a_real_sqlite_database_copy()
    {
        var directory = NewDirectory();
        var originalPath = Path.Combine(directory, "original.db");
        var copyPath = Path.Combine(directory, "copy.db");
        var originalOptions = FileOptions(originalPath);

        string currentMigrationId;

        // 1. Build a database whose schema (and history) stops at the previous
        //    migration. This repository has no initial CreateInitial migration
        //    (its oldest retained migration alters the legacy Books table), so
        //    the supported fresh-database path is the same one
        //    DatabaseBootstrapService uses: EnsureCreated plus a complete
        //    history baseline, then roll the disposable file back one step.
        await using (var db = new NostosDbContext(originalOptions))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
            currentMigrationId = db.Database.GetMigrations()
                .Single(id => id.Contains("AddDurableMigrationTransferJobs"));

            var historyRepository = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(historyRepository.GetCreateScript());
            foreach (var migrationId in db.Database.GetMigrations())
            {
                await db.Database.ExecuteSqlRawAsync(
                    historyRepository.GetInsertScript(new HistoryRow(migrationId, "10.0.0")));
            }
        }

        await using (var db = new NostosDbContext(originalOptions))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            (await db.Database.GetAppliedMigrationsAsync())
                .Should().NotContain(currentMigrationId);
            (await db.Database.GetPendingMigrationsAsync())
                .Should().StartWith(currentMigrationId, "the durable-transfer migration is the next pending one");
        }

        await LaterBookColumns.RestoreAsync(originalOptions);

        // 2. Populate representative preexisting library/notes/writing/
        //    settings/backup data.
        await SeedLegacyDataAsync(originalOptions);
        var originalSnapshot = await ReadSnapshotAsync(originalOptions);

        // Settle any WAL before the byte-for-byte capture and copy.
        await CheckpointAsync(originalPath);
        var originalLength = new FileInfo(originalPath).Length;
        var originalHash = Sha256File(originalPath);

        // 3. Physically copy the closed database file.
        File.Copy(originalPath, copyPath, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(originalPath + suffix))
                File.Copy(originalPath + suffix, copyPath + suffix, overwrite: true);
        }

        // 4. On the copy, this migration is the next pending one; apply up to
        //    it (later migrations, such as #774's AddProviderPreferences, have
        //    their own upgrade test).
        var copyOptions = FileOptions(copyPath);
        await using (var db = new NostosDbContext(copyOptions))
        {
            (await db.Database.GetPendingMigrationsAsync())
                .Should().StartWith([currentMigrationId], "the durable-transfer migration is the next pending one");
            await db.GetService<IMigrator>().MigrateAsync(currentMigrationId);
            (await db.Database.GetAppliedMigrationsAsync())
                .Should().Contain(currentMigrationId);
        }

        // 5. Preexisting rows are intact and unchanged; the new tables exist
        //    and are empty.
        (await ReadSnapshotAsync(copyOptions)).Should().Be(originalSnapshot);
        await using (var db = new NostosDbContext(copyOptions))
        {
            (await db.MigrationJobRecords.CountAsync()).Should().Be(0);
            (await db.MigrationSessionRecords.CountAsync()).Should().Be(0);
            (await db.MigrationChunkReceiptRecords.CountAsync()).Should().Be(0);
            (await db.MigrationExportArtifactRecords.CountAsync()).Should().Be(0);
            (await db.MigrationStorageReservations.CountAsync()).Should().Be(0);

            // 6. Representative CRUD against the upgraded schema.
            var job = new MigrationJobRecord
            {
                Direction = 0,
                State = 2,
                RecoveryStatus = 0,
                ProgressPhase = 2,
                ProgressBytesProcessed = 8 * 1024 * 1024,
                IdempotencyKey = "upgrade-test-job",
                CreationPayloadHash = new string('a', 64),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
                MigrationLeaseToken = "lease-token",
                LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
                AttemptNumber = 1,
                ReservedStorageBytes = 32 * 1024 * 1024,
            };
            var session = new MigrationSessionRecord
            {
                JobId = job.Id,
                Purpose = 0,
                State = 1,
                TotalBytes = 32 * 1024 * 1024,
                ChunkSize = 16 * 1024 * 1024,
                TotalChunks = 2,
                FileIdentitySizeBytes = 32 * 1024 * 1024,
                FileIdentitySha256 = new string('b', 64),
                IdempotencyKey = "upgrade-test-session",
                CreationPayloadHash = new string('c', 64),
                ReceivedBytes = 16 * 1024 * 1024,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(24),
                StorageKey = "uploads/job-upgrade/session-1/archive.part",
            };
            var receipt = new MigrationChunkReceiptRecord
            {
                SessionId = session.Id,
                ChunkIndex = 0,
                OffsetBytes = 0,
                LengthBytes = 16 * 1024 * 1024,
                Sha256 = new string('d', 64),
                ReceivedAtUtc = DateTime.UtcNow,
            };
            var artifact = new MigrationExportArtifactRecord
            {
                JobId = job.Id,
                State = 1,
                StorageKey = "exports/job-upgrade/library.nostos",
                FileName = "library.nostos",
                ContentType = "application/vnd.nostos.portable+zip",
                SizeBytes = 1024,
                Sha256 = new string('e', 64),
                CreatedAtUtc = DateTime.UtcNow,
                AvailableAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
            };
            var reservation = new MigrationStorageReservationRecord
            {
                Purpose = 0,
                ReservedBytes = 64 * 1024 * 1024,
                MaterializedBytes = 16 * 1024 * 1024,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
                ClaimedJobId = job.Id,
            };

            db.MigrationJobRecords.Add(job);
            db.MigrationSessionRecords.Add(session);
            db.MigrationChunkReceiptRecords.Add(receipt);
            db.MigrationExportArtifactRecords.Add(artifact);
            db.MigrationStorageReservations.Add(reservation);
            await db.SaveChangesAsync();

            (await db.MigrationJobRecords.CountAsync()).Should().Be(1);
            (await db.MigrationSessionRecords.CountAsync()).Should().Be(1);
            (await db.MigrationChunkReceiptRecords.CountAsync()).Should().Be(1);
            (await db.MigrationExportArtifactRecords.CountAsync()).Should().Be(1);
            (await db.MigrationStorageReservations.CountAsync()).Should().Be(1);

            var reloaded = await db.MigrationSessionRecords.SingleAsync();
            reloaded.StorageKey.Should().Be("uploads/job-upgrade/session-1/archive.part");
            reloaded.FileIdentitySha256.Should().Be(new string('b', 64));
        }

        // 6b. The migrated table shape must equal the shape the current model
        //     creates (column types/nullability/defaults, PKs, FKs, unique and
        //     ordinary indexes, check constraints), so a regenerated migration
        //     cannot silently drift from the model.
        var modelPath = Path.Combine(directory, "model.db");
        await using (var db = new NostosDbContext(FileOptions(modelPath)))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
        }

        (await NewTableSchemaAsync(copyPath)).Should().Equal(
            await NewTableSchemaAsync(modelPath),
            "the migrated schema must match the schema generated from the current model");

        // 7. The original file was not touched: same bytes, same legacy data,
        //    and none of the new tables.
        new FileInfo(originalPath).Length.Should().Be(originalLength);
        Sha256File(originalPath).Should().Be(originalHash);
        (await ReadSnapshotAsync(originalOptions)).Should().Be(originalSnapshot);
        (await TableNamesAsync(originalPath)).Should().NotIntersectWith(NewTables);

        // 8. Roll the disposable copy back to the previous migration; only the
        //    five new tables disappear and legacy rows remain.
        await using (var db = new NostosDbContext(copyOptions))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        (await TableNamesAsync(copyPath)).Should().NotIntersectWith(NewTables);
        (await ReadSnapshotAsync(copyOptions)).Should().Be(originalSnapshot);
    }

    private static async Task SeedLegacyDataAsync(DbContextOptions<NostosDbContext> options)
    {
        await using var db = new NostosDbContext(options);
        var now = new DateTime(2026, 1, 5, 6, 7, 8, DateTimeKind.Utc);

        var work = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Legacy Work",
            Author = "Legacy Author",
            NormalizedTitle = "LEGACY WORK",
            NormalizedAuthor = "LEGACY AUTHOR",
            CreatedAt = now,
        };
        var book = new EBookModel
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Title = "Legacy Book",
            Author = "Legacy Author",
            CreatedAt = now,
            PageCount = 123,
        };
        var collection = new CollectionModel { Id = Guid.NewGuid(), Name = "Legacy Collection" };
        var note = new NoteModel
        {
            Id = Guid.NewGuid(),
            Book = book,
            Content = "Legacy note content",
            RawContent = "Legacy note content",
            CaptureSource = "text",
            ProcessingMode = "verbatim",
            SourceAnchorKind = "unknown",
            CreatedAt = now,
        };
        var topic = new TopicModel { Id = Guid.NewGuid(), Topic = "Legacy Topic" };
        var folder = new WritingModel
        {
            Id = Guid.NewGuid(),
            Name = "Legacy Folder",
            Type = WritingType.Folder,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var document = new WritingModel
        {
            Id = Guid.NewGuid(),
            Name = "Legacy Writing",
            Type = WritingType.Document,
            Content = "<p>Legacy prose</p>",
            ParentId = folder.Id,
            Parent = folder,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Works.Add(work);
        db.Books.Add(book);
        db.Collections.Add(collection);
        db.BookCollections.Add(new BookCollectionModel
        {
            BookId = book.Id,
            Book = book,
            CollectionId = collection.Id,
            Collection = collection,
            AddedAt = now,
        });
        db.Notes.Add(note);
        db.Topics.Add(topic);
        db.NoteTopics.Add(new NoteTopicModel { NoteId = note.Id, Note = note, TopicId = topic.Id, Topic = topic });
        db.Writings.AddRange(folder, document);
        db.WritingNotes.Add(new WritingNoteModel
        {
            WritingId = document.Id,
            Writing = document,
            NoteId = note.Id,
            Note = note,
            AddedAt = now,
        });
        db.LibraryStates.Add(new LibraryState
        {
            Id = LibraryState.WellKnownId,
            SingletonSlot = LibraryState.SingletonSentinel,
            StateVersion = "7",
            UpdatedAt = now,
        });
        db.LibraryCommandReceipts.Add(new LibraryCommandReceipt
        {
            ClientId = "upgrade-test",
            IdempotencyKey = "legacy-library-1",
            CommandKind = "create",
            ResponseJson = "{}",
            CreatedAt = now,
        });
        db.NoteCommandReceipts.Add(new NoteCommandReceipt
        {
            ClientId = "upgrade-test",
            IdempotencyKey = "legacy-note-1",
            Command = "capture",
            ResultJson = "{}",
            CreatedAtUtc = now,
        });
        db.BackupRecords.Add(new BackupRecord
        {
            SizeBytes = 2048,
            CreatedAt = now,
            Provider = BackupProvider.Local,
            Status = BackupStatus.Completed,
        });
        db.AssistantSettings.Add(new AssistantSettingsModel
        {
            CaptureProcessingMode = "light_polish",
            UpdatedAtUtc = now,
        });

        await db.SaveChangesAsync();
    }

    private static async Task<LibrarySnapshot> ReadSnapshotAsync(
        DbContextOptions<NostosDbContext> options)
    {
        await using var db = new NostosDbContext(options);
        return new LibrarySnapshot(
            Works: await db.Works.CountAsync(),
            Books: await db.Books.CountAsync(),
            Notes: await db.Notes.CountAsync(),
            Topics: await db.Topics.CountAsync(),
            NoteTopics: await db.NoteTopics.CountAsync(),
            Collections: await db.Collections.CountAsync(),
            BookCollections: await db.BookCollections.CountAsync(),
            Writings: await db.Writings.CountAsync(),
            WritingNotes: await db.WritingNotes.CountAsync(),
            LibraryStates: await db.LibraryStates.CountAsync(),
            LibraryCommandReceipts: await db.LibraryCommandReceipts.CountAsync(),
            NoteCommandReceipts: await db.NoteCommandReceipts.CountAsync(),
            BackupRecords: await db.BackupRecords.CountAsync(),
            AssistantSettings: await db.AssistantSettings.CountAsync(),
            BookTitle: (await db.Books.SingleAsync()).Title,
            NoteContent: (await db.Notes.SingleAsync()).Content,
            WritingContent: (await db.Writings.SingleAsync(w => w.Type == WritingType.Document)).Content
                ?? string.Empty);
    }

    // Canonical, order-independent fingerprint of the five new tables: every
    // column (type, nullability, default, PK position), FK (target and delete
    // action), index (uniqueness, origin, columns) and check-constraint name.
    private static async Task<List<string>> NewTableSchemaAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        var lines = new List<string>();

        foreach (var table in NewTables)
        {
            var tableSql = await ScalarTextAsync(
                connection,
                "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = @name",
                table);

            var checks = Regex.Matches(tableSql ?? string.Empty, @"CK_[A-Za-z0-9_]+")
                .Select(match => match.Value)
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal);
            lines.Add($"{table} checks: {string.Join(",", checks)}");

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA table_info(\"{table}\")";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    lines.Add(
                        $"{table} column: {reader.GetString(1)}|{reader.GetString(2)}|" +
                        $"notnull={reader.GetInt32(3)}|default={reader.GetValue(4)}|pk={reader.GetInt32(5)}");
                }
            }

            var indexNames = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA index_list(\"{table}\")";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var indexName = reader.GetString(1);
                    var unique = reader.GetInt32(2);
                    var origin = reader.GetString(3);
                    indexNames.Add($"{table} index: {indexName}|unique={unique}|origin={origin}");
                }
            }

            foreach (var indexLine in indexNames)
            {
                var indexName = indexLine.Split('|')[0].Replace($"{table} index: ", string.Empty);
                var columns = new List<string>();
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"PRAGMA index_info(\"{indexName}\")";
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        columns.Add(reader.GetString(2));
                }

                lines.Add($"{indexLine}|{string.Join(",", columns)}");
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA foreign_key_list(\"{table}\")";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    lines.Add(
                        $"{table} fk: {reader.GetString(3)}->{reader.GetString(2)}.{reader.GetString(4)}|" +
                        $"ondelete={reader.GetString(6)}");
                }
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static async Task<string?> ScalarTextAsync(
        SqliteConnection connection,
        string sql,
        string parameter)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@name", parameter);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<List<string>> TableNamesAsync(string path)
    {
        var names = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"name\" FROM \"sqlite_master\" WHERE \"type\" = 'table'";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task CheckpointAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static DbContextOptions<NostosDbContext> FileOptions(string path) =>
        new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={path}",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;

    private string NewDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"nostos-679-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    private sealed record LibrarySnapshot(
        int Works,
        int Books,
        int Notes,
        int Topics,
        int NoteTopics,
        int Collections,
        int BookCollections,
        int Writings,
        int WritingNotes,
        int LibraryStates,
        int LibraryCommandReceipts,
        int NoteCommandReceipts,
        int BackupRecords,
        int AssistantSettings,
        string BookTitle,
        string NoteContent,
        string WritingContent);
}
