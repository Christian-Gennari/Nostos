using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;
using Xunit;

namespace Nostos.Backend.Tests.Backup;

// Real backup -> restore integration tests (Task 3): the full BackupService
// runs against a real temporary SQLite database and a real content root, so
// The .nostos archive, vacuumed database and the file-copy restore path
// are all exercised for real. IncludeBookFiles=false keeps the tests free
// of book file payloads.
[Collection(BackupIsolationCollection.Name)]
public sealed class BackupServiceTests
{
    private const string DistinctiveCaptureText = "Distinctive secret capture text, unique: quixotic-42";

    private static readonly JsonSerializerOptions TestJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task BackupArchive_IncludesWhitelistedMetadata_WithExactValues()
    {
        using var h = BackupHarness.Create();
        await SeedBooksAndNotesScenarioAsync(h);

        var created = await h.Service.CreateBackupAsync();
        created.Status.Should().Be(BackupStatus.Completed);

        var archivePath = h.Service.GetLocalArchivePath(created.Id);
        archivePath.Should().NotBeNull();

        using var zip = ZipFile.OpenRead(archivePath!);
        zip.GetEntry("metadata/notes.json").Should().NotBeNull("the .nostos archive must include metadata/notes.json");
        zip.GetEntry("metadata/topics.json").Should().NotBeNull();
        zip.GetEntry("metadata/collections.json").Should().NotBeNull();
        zip.GetEntry("metadata/writings.json").Should().NotBeNull();
        zip.GetEntry("database/nostos.db").Should().NotBeNull();
        zip.GetEntry("manifest.json").Should().NotBeNull();
        zip.Entries.Should().NotContain(e => e.FullName.StartsWith("books/"), "IncludeBookFiles=false must not copy book files");
    }

    [Fact]
    public async Task RestoreBackup_RoundTripsBooksAndNotes_WithIntegrityAndNoForeignKeyViolations()
    {
        using var h = BackupHarness.Create();
        await SeedBooksAndNotesScenarioAsync(h);

        var created = await h.Service.CreateBackupAsync();
        created.Status.Should().Be(BackupStatus.Completed);
        var backupId = created.Id;

        // Destructively mutate the live database
        await using (var db = h.NewDbContext())
        {
            db.Notes.RemoveRange(db.Notes);
            db.Books.RemoveRange(db.Books);
            await db.SaveChangesAsync();

            (await db.Notes.CountAsync()).Should().Be(0);
            (await db.Books.CountAsync()).Should().Be(0);
        }

        var restored = await h.Service.RestoreBackupAsync(backupId);
        restored.Success.Should().BeTrue(restored.Message);

        await using var after = h.NewDbContext();
        (await after.Books.CountAsync()).Should().Be(2);
        (await after.Notes.CountAsync()).Should().Be(1);

        // Physical integrity of the restored database.
        var connection = after.Database.GetDbConnection();
        await connection.OpenAsync();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA integrity_check";
            ((string)(await cmd.ExecuteScalarAsync())!).Should().Be("ok");
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_key_check";
            using var reader = await cmd.ExecuteReaderAsync();
            var violations = new List<string>();
            while (await reader.ReadAsync())
                violations.Add(reader.GetString(0));
            violations.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task RestoreBackup_WithConfiguredDatabasePath_ReplacesThatDatabaseAndLeavesContentRootDecoyUntouched()
    {
        var volume = Path.Combine(
            Path.GetTempPath(),
            $"nostos-backup-configured-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(volume);
        var configuredDbPath = Path.Combine(volume, "library.db");

        try
        {
            using var h = BackupHarness.Create(databasePath: configuredDbPath);
            await SeedBooksAndNotesScenarioAsync(h);

            var created = await h.Service.CreateBackupAsync();
            created.Status.Should().Be(BackupStatus.Completed);

            // Change the real configured database after the backup so a
            // successful restore has an observable database-level effect.
            await using (var db = h.NewDbContext())
            {
                db.Notes.RemoveRange(db.Notes);
                db.Books.RemoveRange(db.Books);
                await db.SaveChangesAsync();

                (await db.Notes.CountAsync()).Should().Be(0);
                (await db.Books.CountAsync()).Should().Be(0);
            }

            // The historical default path is deliberately a valid decoy file.
            // The pre-fix implementation overwrote this file, reported success,
            // and left the configured database untouched.
            var decoyPath = Path.Combine(h.ContentRoot, "nostos.db");
            var decoyBytes = "CONTENT-ROOT-DECOY-755"u8.ToArray();
            await File.WriteAllBytesAsync(decoyPath, decoyBytes);

            var restored = await h.Service.RestoreBackupAsync(created.Id);
            restored.Success.Should().BeTrue(restored.Message);

            await using (var after = h.NewDbContext())
            {
                (await after.Books.CountAsync()).Should().Be(2);
                (await after.Notes.CountAsync()).Should().Be(1);
            }

            (await File.ReadAllBytesAsync(decoyPath)).Should().Equal(decoyBytes);

            Directory.GetFiles(volume, "library.db.pre-restore-*")
                .Should().ContainSingle(
                    "the safety copy must be created beside the configured live database");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(volume))
                Directory.Delete(volume, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_RefusesArchiveWithChecksumMismatch_LeavingLiveDatabaseUntouched()
    {
        using var h = BackupHarness.Create();
        await SeedBooksAndNotesScenarioAsync(h);

        var created = await h.Service.CreateBackupAsync();
        created.Status.Should().Be(BackupStatus.Completed);
        var archivePath = h.Service.GetLocalArchivePath(created.Id)!;

        var liveDbBytes = await File.ReadAllBytesAsync(h.DatabasePath);

        // Tamper with the archive after the fact.
        await File.AppendAllTextAsync(archivePath, "tampered-after-backup");

        var result = await h.Service.RestoreBackupAsync(created.Id);
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("integrity");

        // The live database was never touched and remains fully readable.
        (await File.ReadAllBytesAsync(h.DatabasePath)).Should().Equal(liveDbBytes);
        h.Settings.IsInMaintenanceMode.Should().BeFalse();
        await using var db = h.NewDbContext();
        (await db.Books.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Restore_FailureAfterEnteringMaintenance_ExitsMaintenanceAndRollsBackWithoutTouchingLiveDatabase()
    {
        using var h = BackupHarness.Create();
        await SeedBooksAndNotesScenarioAsync(h);

        var created = await h.Service.CreateBackupAsync();
        created.Status.Should().Be(BackupStatus.Completed);
        var archivePath = h.Service.GetLocalArchivePath(created.Id)!;

        var liveDbBytes = await File.ReadAllBytesAsync(h.DatabasePath);

        // Corrupt the zip structure (truncate the end-of-central-directory
        // record) and re-stamp the record's manifest checksum so the archive
        // passes integrity verification and fails later — inside maintenance
        // mode — when the zip is extracted.
        var corrupted = await File.ReadAllBytesAsync(archivePath);
        var truncated = corrupted[..^64];
        await File.WriteAllBytesAsync(archivePath, truncated);
        var corruptedChecksum = Convert.ToHexString(SHA256.HashData(truncated)).ToLowerInvariant();

        await using (var recordDb = h.NewDbContext())
        {
            var record = await recordDb.BackupRecords.SingleAsync(r => r.Id == created.Id);
            var manifest = JsonSerializer.Deserialize<BackupManifestDto>(record.ManifestJson!, TestJsonOpts)!;
            record.ManifestJson = JsonSerializer.Serialize(
                manifest with { Checksum = corruptedChecksum }, TestJsonOpts);
            await recordDb.SaveChangesAsync();
        }

        var result = await h.Service.RestoreBackupAsync(created.Id);
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Restore failed");

        // Maintenance mode is always exited and the failed restore left the
        // live database byte-for-byte untouched (rollback by non-replacement).
        h.Settings.IsInMaintenanceMode.Should().BeFalse();
        (await File.ReadAllBytesAsync(h.DatabasePath)).Should().Equal(liveDbBytes);
        await using var db = h.NewDbContext();
        (await db.Books.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task CreateBackup_FailsClosed_WhenArchiveFinalizationFails_MarkingRecordFailedWithoutArchive()
    {
        using var h = BackupHarness.Create();
        await SeedBooksAndNotesScenarioAsync(h);

        // Block LocalBackupDir creation so finalizing a fully built archive
        // throws. Metadata serialization failures inside BuildArchiveAsync
        // take the identical path: the exception aborts the backup, the
        // record is marked Failed, and no archive survives (fail closed).
        var storageDir = Path.Combine(h.ContentRoot, "Storage");
        Directory.CreateDirectory(storageDir);
        var backupsPath = Path.Combine(storageDir, "backups");
        await File.WriteAllTextAsync(backupsPath, "blocking file");

        var result = await h.Service.CreateBackupAsync();
        result.Status.Should().Be(BackupStatus.Failed);

        await using var db = h.NewDbContext();
        var record = await db.BackupRecords.SingleAsync(r => r.Id == result.Id);
        record.Status.Should().Be(BackupStatus.Failed);
        record.ErrorMessage.Should().NotBeNullOrEmpty();
        record.LocalArchivePath.Should().BeNull();

        h.Service.GetLocalArchivePath(result.Id).Should().BeNull();
        Directory.Exists(Path.Combine(Path.GetTempPath(), $"nostos-backup-{result.Id}")).Should().BeFalse();
    }

    // --- Seeding -----------------------------------------------------------

    private static async Task SeedBooksAndNotesScenarioAsync(BackupHarness h)
    {
        var candideId = await AddBookAsync(h, "Candide", "Voltaire");
        var ethicsId = await AddBookAsync(h, "Nicomachean Ethics", "Aristotle");

        await using var db = h.NewDbContext();
        db.Notes.Add(new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = candideId,
            Content = "All is for the best in the best of all possible worlds.",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddBookAsync(BackupHarness h, string title, string? author)
    {
        await using var db = h.NewDbContext();
        var book = new PhysicalBookModel { Title = title, Author = author };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    [Fact]
    public async Task RestoreBackup_RoundTripsBookFiles_WhenIncludeBookFiles()
    {
        using var h = BackupHarness.Create(includeBookFiles: true);

        var bookDir = Path.Combine(h.ContentRoot, "Storage", "books", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bookDir);
        var payload = "EPUB-FILE-PAYLOAD-quixotic-42"u8.ToArray();
        var bookFile = Path.Combine(bookDir, "book.epub");
        await File.WriteAllBytesAsync(bookFile, payload);

        var created = await h.Service.CreateBackupAsync();
        created.Status.Should().Be(BackupStatus.Completed);

        // Destructively remove the live file (simulates a lost book file).
        File.Delete(bookFile);

        var restored = await h.Service.RestoreBackupAsync(created.Id);
        restored.Success.Should().BeTrue(restored.Message);

        File.Exists(bookFile).Should().BeTrue();
        (await File.ReadAllBytesAsync(bookFile)).Should().Equal(payload);
    }

    [Fact]
    public async Task ImportedAudiobook_SurvivesBackupAndRestore_WithItsMediaAndProvenance()
    {
        // An imported LibriVox recording is an ordinary local audiobook, so it
        // has to survive a backup like one: the .m4b itself and the row saying
        // where it came from. A backup that kept the audio but dropped the
        // provenance would silently lose the source; one that kept the row but
        // dropped the file would restore a book that cannot be played.
        using var h = BackupHarness.Create(includeBookFiles: true);

        Guid bookId;
        await using (var db = h.NewDbContext())
        {
            var book = new PhysicalBookModel { Title = "Spirits of the Dead", Author = "Edgar Allan Poe" };
            db.Books.Add(book);

            db.BookAcquisitions.Add(new BookAcquisitionModel
            {
                BookId = book.Id,
                ProviderId = "librivox",
                ProviderDisplayName = "LibriVox",
                ExternalId = "8260",
                AssetId = "m4b",
                AssetFormat = "librivox-mp3-sections",
                ImportedExtension = ".m4b",
                SourceUrl = "https://librivox.org/spirits-of-the-dead-by-edgar-allan-poe/",
                RightsStatement = "LibriVox recordings are in the public domain.",
                AcquiredAt = new DateTime(2026, 9, 17, 21, 28, 0, DateTimeKind.Utc),
            });

            await db.SaveChangesAsync();
            bookId = book.Id;
        }

        var bookDir = Path.Combine(h.ContentRoot, "Storage", "books", bookId.ToString());
        Directory.CreateDirectory(bookDir);
        var payload = "M4B-AUDIO-PAYLOAD-overnight-168"u8.ToArray();
        var bookFile = Path.Combine(bookDir, "book.m4b");
        await File.WriteAllBytesAsync(bookFile, payload);

        var created = await h.Service.CreateBackupAsync();
        created.Status.Should().Be(BackupStatus.Completed);

        var archivePath = h.Service.GetLocalArchivePath(created.Id);
        archivePath.Should().NotBeNull();

        using (var zip = ZipFile.OpenRead(archivePath!))
        {
            zip.GetEntry($"books/{bookId}/book.m4b")
                .Should().NotBeNull("the imported recording itself must be in the archive");

            var booksEntry = zip.GetEntry("metadata/books.json");
            booksEntry.Should().NotBeNull();

            using var reader = new StreamReader(booksEntry!.Open());
            var booksJson = await reader.ReadToEndAsync();

            booksJson.Should().Contain("librivox", "provenance is what says where the file came from");
            booksJson.Should().Contain("8260");
            booksJson.Should().Contain("librivox-mp3-sections");
        }

        // Lose the recording, then restore.
        File.Delete(bookFile);

        var restored = await h.Service.RestoreBackupAsync(created.Id);
        restored.Success.Should().BeTrue(restored.Message);

        File.Exists(bookFile).Should().BeTrue("restore must put the audiobook file back");
        (await File.ReadAllBytesAsync(bookFile)).Should().Equal(payload);

        await using (var db = h.NewDbContext())
        {
            var provenance = await db.BookAcquisitions.SingleAsync(a => a.BookId == bookId);

            provenance.ProviderId.Should().Be("librivox");
            provenance.ExternalId.Should().Be("8260");
            provenance.AssetFormat.Should().Be("librivox-mp3-sections");
            provenance.ImportedExtension.Should().Be(".m4b");
        }
    }

    // --- Where the archives live (#172) ------------------------------------
    //
    // The backup directory used to be built from the content root while the book
    // files were resolved from Storage:BooksRoot, so a relocated library left its
    // archives behind — and a worktree, whose Storage is a symlink to the shared
    // tree, wrote them into the real install. These pin the resolved location and
    // that reads follow writes.

    [Theory]
    // Unset: beside the library. With the default books root that is exactly the
    // historical <contentRoot>/Storage/backups, so nothing moves for an install
    // that never configured a storage root.
    [InlineData(null, null, "/srv/nostos/Storage/backups")]
    // A library on another volume takes its archives with it.
    [InlineData("/mnt/library/books", null, "/mnt/library/backups")]
    // An explicit root wins, and a relative one resolves against the content root.
    [InlineData("/mnt/library/books", "/mnt/archives", "/mnt/archives")]
    [InlineData("/mnt/library/books", "vault", "/srv/nostos/vault")]
    public void ResolveBackupsRoot_PlacesArchivesPredictably(
        string? booksRoot, string? backupsRoot, string expected)
    {
        const string contentRoot = "/srv/nostos";
        var options = new FileStorageOptions { BooksRoot = booksRoot, BackupsRoot = backupsRoot };

        var resolvedBooks = FileStorageOptions.ResolveBooksRoot(contentRoot, options);
        var resolvedBackups = FileStorageOptions.ResolveBackupsRoot(contentRoot, resolvedBooks, options);

        resolvedBackups.Should().Be(Path.GetFullPath(expected));
    }

    [Fact]
    public void ResolveBackupsRoot_HandlesABooksRootWithNoParent()
    {
        // A books root at a filesystem root has no directory name; the resolution
        // must still produce a usable path rather than throwing.
        var resolved = FileStorageOptions.ResolveBackupsRoot(
            "/srv/nostos", "/library", new FileStorageOptions { BooksRoot = "/library" });

        resolved.Should().Be(Path.GetFullPath("/backups"));
    }

    [Fact]
    public async Task CreateBackup_WithDefaultStorage_StillWritesToTheHistoricalLocation()
    {
        // The regression guard: an existing deployment must not have its archives
        // silently move.
        using var h = BackupHarness.Create();

        var created = await h.Service.CreateBackupAsync();
        created.Status.Should().Be(BackupStatus.Completed);

        var archivePath = h.Service.GetLocalArchivePath(created.Id);
        archivePath.Should().Be(Path.Combine(h.ContentRoot, "Storage", "backups", $"{created.Id}.nostos"));
        File.Exists(archivePath!).Should().BeTrue();
    }

    [Fact]
    public async Task CreateBackup_WithRelocatedLibrary_PutsArchivesBesideTheLibrary()
    {
        // The defect: a library on another volume left its backups under the
        // application, which on a container is the ephemeral layer.
        var volume = Path.Combine(Path.GetTempPath(), $"nostos-172-volume-{Guid.NewGuid():N}");
        var booksRoot = Path.Combine(volume, "books");
        Directory.CreateDirectory(booksRoot);

        try
        {
            using var h = BackupHarness.Create(fileStorage: new FileStorageOptions { BooksRoot = booksRoot });

            var created = await h.Service.CreateBackupAsync();
            created.Status.Should().Be(BackupStatus.Completed);

            var archivePath = h.Service.GetLocalArchivePath(created.Id);
            archivePath.Should().Be(Path.Combine(volume, "backups", $"{created.Id}.nostos"));
            File.Exists(archivePath!).Should().BeTrue();

            archivePath!.Should().NotStartWith(Path.Combine(h.ContentRoot, "Storage"));
        }
        finally
        {
            if (Directory.Exists(volume))
                Directory.Delete(volume, recursive: true);
        }
    }

    [Fact]
    public async Task CreateBackup_WithConfiguredBackupsRoot_UsesIt()
    {
        var archives = Path.Combine(Path.GetTempPath(), $"nostos-172-archives-{Guid.NewGuid():N}");

        try
        {
            using var h = BackupHarness.Create(fileStorage: new FileStorageOptions { BackupsRoot = archives });

            var created = await h.Service.CreateBackupAsync();
            created.Status.Should().Be(BackupStatus.Completed);

            var archivePath = h.Service.GetLocalArchivePath(created.Id);
            archivePath.Should().Be(Path.Combine(archives, $"{created.Id}.nostos"));
            File.Exists(archivePath!).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(archives))
                Directory.Delete(archives, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreBackup_WithRelocatedLibrary_FindsTheArchiveItWrote()
    {
        // What makes the location a correctness issue rather than cosmetics:
        // a write that goes somewhere the read does not look is a lost backup.
        var volume = Path.Combine(Path.GetTempPath(), $"nostos-172-roundtrip-{Guid.NewGuid():N}");
        var booksRoot = Path.Combine(volume, "books");
        Directory.CreateDirectory(booksRoot);

        try
        {
            using var h = BackupHarness.Create(
                includeBookFiles: true,
                fileStorage: new FileStorageOptions { BooksRoot = booksRoot });

            var bookDir = Path.Combine(booksRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(bookDir);
            var payload = "RELOCATED-LIBRARY-PAYLOAD-172"u8.ToArray();
            var bookFile = Path.Combine(bookDir, "book.epub");
            await File.WriteAllBytesAsync(bookFile, payload);

            var created = await h.Service.CreateBackupAsync();
            created.Status.Should().Be(BackupStatus.Completed);

            File.Delete(bookFile);

            var restored = await h.Service.RestoreBackupAsync(created.Id);
            restored.Success.Should().BeTrue(restored.Message);

            File.Exists(bookFile).Should().BeTrue("a backup written beside the library must be found there on restore");
            (await File.ReadAllBytesAsync(bookFile)).Should().Equal(payload);
        }
        finally
        {
            if (Directory.Exists(volume))
                Directory.Delete(volume, recursive: true);
        }
    }

    // --- Pruning only removes what this deployment owns ---------------------
    //
    // BackupRecords.LocalArchivePath is a persisted ABSOLUTE path, so a row can
    // name a file anywhere. Retention used to File.Delete it unguarded. Because
    // a development worktree is seeded with a copy of the production database,
    // that deleted a real archive off production's disk — the incident these
    // tests exist to prevent from recurring.

    [Fact]
    public async Task EnforceMaxBackups_NeverDeletesAnArchiveOutsideItsOwnDirectory()
    {
        var foreign = Path.Combine(Path.GetTempPath(), $"nostos-172-foreign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(foreign);
        var foreignArchive = Path.Combine(foreign, "someone-elses.nostos");
        await File.WriteAllTextAsync(foreignArchive, "ANOTHER-INSTALLS-ARCHIVE");

        try
        {
            // maxBackups: 1, so retention has to prune and the foreign rows are
            // the oldest available candidates.
            using var h = BackupHarness.Create(maxBackups: 1);

            // A path that shares the backup directory's name prefix but is not
            // inside it: "/…/backups-old" must not pass as "/…/backups".
            var siblingDir = Path.Combine(h.ContentRoot, "Storage", "backups-old");
            Directory.CreateDirectory(siblingDir);
            var siblingArchive = Path.Combine(siblingDir, "old-archive.nostos");
            await File.WriteAllTextAsync(siblingArchive, "EARLIER-LOCATION-ARCHIVE");

            await using (var db = h.NewDbContext())
            {
                db.BackupRecords.Add(new BackupRecord
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow.AddDays(-40),
                    SizeBytes = 26,
                    Provider = "Local",
                    Status = BackupStatus.Completed,
                    LocalArchivePath = foreignArchive,
                });
                db.BackupRecords.Add(new BackupRecord
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow.AddDays(-30),
                    SizeBytes = 23,
                    Provider = "Local",
                    Status = BackupStatus.Completed,
                    LocalArchivePath = siblingArchive,
                });
                await db.SaveChangesAsync();
            }

            var created = await h.Service.CreateBackupAsync();
            created.Status.Should().Be(BackupStatus.Completed);

            File.Exists(foreignArchive).Should()
                .BeTrue("an archive in another directory is not this deployment's to delete");
            (await File.ReadAllTextAsync(foreignArchive)).Should().Be("ANOTHER-INSTALLS-ARCHIVE");

            File.Exists(siblingArchive).Should()
                .BeTrue("the directory prefix must be compared with its separator, so 'backups-old' is not inside 'backups'");
            (await File.ReadAllTextAsync(siblingArchive)).Should().Be("EARLIER-LOCATION-ARCHIVE");

            // The rows are forgotten so retention does not report them forever;
            // only the FILES are sacred.
            await using (var db = h.NewDbContext())
            {
                db.BackupRecords.Should().NotContain(r => r.LocalArchivePath == foreignArchive);
                db.BackupRecords.Should().NotContain(r => r.LocalArchivePath == siblingArchive);
            }
        }
        finally
        {
            if (Directory.Exists(foreign))
                Directory.Delete(foreign, recursive: true);
        }
    }

    [Fact]
    public async Task EnforceMaxBackups_StillPrunesArchivesItOwns()
    {
        // The guard must not quietly disable retention for the normal case.
        using var h = BackupHarness.Create(maxBackups: 1);

        var first = await h.Service.CreateBackupAsync();
        first.Status.Should().Be(BackupStatus.Completed);
        var firstPath = Path.Combine(h.ContentRoot, "Storage", "backups", $"{first.Id}.nostos");
        File.Exists(firstPath).Should().BeTrue();

        // Ordering is by CreatedAt, so make the second unambiguously newer.
        await Task.Delay(50);
        var second = await h.Service.CreateBackupAsync();
        second.Status.Should().Be(BackupStatus.Completed);

        File.Exists(firstPath).Should().BeFalse("retention must still reclaim archives it owns");
        File.Exists(Path.Combine(h.ContentRoot, "Storage", "backups", $"{second.Id}.nostos"))
            .Should().BeTrue("the most recent archive is always kept");
    }

    [Fact]
    public async Task DeleteBackupRecord_LeavesAnArchiveOutsideTheDirectoryAlone()
    {
        var foreign = Path.Combine(Path.GetTempPath(), $"nostos-172-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(foreign);
        var foreignArchive = Path.Combine(foreign, "kept.nostos");
        await File.WriteAllTextAsync(foreignArchive, "KEEP-ME");

        try
        {
            using var h = BackupHarness.Create();
            var recordId = Guid.NewGuid();

            await using (var db = h.NewDbContext())
            {
                db.BackupRecords.Add(new BackupRecord
                {
                    Id = recordId,
                    CreatedAt = DateTime.UtcNow,
                    SizeBytes = 7,
                    Provider = "Local",
                    Status = BackupStatus.Completed,
                    LocalArchivePath = foreignArchive,
                });
                await db.SaveChangesAsync();
            }

            await h.Service.DeleteBackupRecordAsync(recordId);

            File.Exists(foreignArchive).Should()
                .BeTrue("deleting a record must not reach outside this deployment's backup directory");

            await using (var db = h.NewDbContext())
                db.BackupRecords.Should().NotContain(r => r.Id == recordId, "the record itself is still removed");
        }
        finally
        {
            if (Directory.Exists(foreign))
                Directory.Delete(foreign, recursive: true);
        }
    }

    // --- Harness -----------------------------------------------------------

    [Fact]
    public async Task ImportExistingBackups_UsesManifestTimestamp_NotUtcNow()
    {
        using var h = BackupHarness.Create();
        var backupDir = Path.Combine(h.ContentRoot, "Storage", "backups");
        Directory.CreateDirectory(backupDir);

        var archivePath = Path.Combine(backupDir, $"{Guid.NewGuid():N}.nostos");
        var manifestTimestamp = new DateTime(2026, 6, 15, 8, 30, 0, DateTimeKind.Utc);
        // Mirror production: archives are written with camelCase JsonOpts
        // (BackupService line 158), so the test manifest must be camelCase too.
        var manifestJsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var dbEntry = zip.CreateEntry("database/nostos.db");
            await using (var s = dbEntry.Open())
            await using (var w = new StreamWriter(s))
                await w.WriteAsync("not-a-real-db");

            var manifestEntry = zip.CreateEntry("manifest.json");
            await using (var s = manifestEntry.Open())
            {
                var json = JsonSerializer.Serialize(new BackupManifestDto(
                    Version: "1",
                    Timestamp: manifestTimestamp,
                    DatabaseSizeBytes: 0,
                    BookFileCount: 0,
                    TotalSizeBytes: 0,
                    Checksum: ""), manifestJsonOpts);
                await s.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json));
            }
        }
        // The file's write time must NOT win over the manifest timestamp.
        File.SetLastWriteTimeUtc(archivePath, new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc));

        var imported = await h.Service.ImportExistingBackupsAsync();

        imported.Should().ContainSingle();
        imported[0].CreatedAt.Should().Be(manifestTimestamp);
    }

    [Fact]
    public async Task ImportExistingBackups_FallsBackToFileWriteTime_WhenNoManifest()
    {
        using var h = BackupHarness.Create();
        var backupDir = Path.Combine(h.ContentRoot, "Storage", "backups");
        Directory.CreateDirectory(backupDir);

        var archivePath = Path.Combine(backupDir, $"{Guid.NewGuid():N}.nostos");
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var dbEntry = zip.CreateEntry("database/nostos.db");
            await using (var s = dbEntry.Open())
            await using (var w = new StreamWriter(s))
                await w.WriteAsync("not-a-real-db");
        }
        var writeTime = new DateTime(2026, 7, 20, 9, 15, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(archivePath, writeTime);

        var imported = await h.Service.ImportExistingBackupsAsync();

        imported.Should().ContainSingle();
        imported[0].CreatedAt.Should().Be(writeTime);
    }

    private sealed class BackupHarness : IDisposable
    {
        public string ContentRoot { get; }
        public string DatabasePath { get; }
        public DbContextOptions<NostosDbContext> Options { get; }

        private readonly ServiceProvider _provider;

        private BackupHarness(
            string contentRoot,
            string databasePath,
            DbContextOptions<NostosDbContext> options,
            ServiceProvider provider)
        {
            ContentRoot = contentRoot;
            DatabasePath = databasePath;
            Options = options;
            _provider = provider;
        }

        public BackupService Service => _provider.GetRequiredService<BackupService>();
        public BackupSettingsProvider Settings => _provider.GetRequiredService<BackupSettingsProvider>();
        public IDbContextFactory<NostosDbContext> DbFactory => _provider.GetRequiredService<IDbContextFactory<NostosDbContext>>();

        public NostosDbContext NewDbContext() => new(Options);

        public static BackupHarness Create(
            bool includeBookFiles = false,
            FileStorageOptions? fileStorage = null,
            int maxBackups = 10,
            string? databasePath = null)
        {
            var contentRoot = Path.Combine(Path.GetTempPath(), $"nostos-backup-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(contentRoot);

            var resolvedDatabasePath = Path.GetFullPath(
                databasePath ?? Path.Combine(contentRoot, "nostos.db"));
            var databaseDirectory = Path.GetDirectoryName(resolvedDatabasePath);
            if (!string.IsNullOrWhiteSpace(databaseDirectory))
                Directory.CreateDirectory(databaseDirectory);

            var options = new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={resolvedDatabasePath}")
                .Options;

            var env = new TestWebHostEnvironment
            {
                ApplicationName = "Nostos.Backend",
                EnvironmentName = "Test",
                ContentRootPath = contentRoot,
                WebRootPath = Path.Combine(contentRoot, "wwwroot"),
            };

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IWebHostEnvironment>(env);
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new BackupSettings
            {
                IsEnabled = true,
                Provider = "Local",
                IntervalHours = 168,
                MaxBackups = maxBackups,
                IncludeBookFiles = includeBookFiles,
            }));
            services.AddSingleton(options);
            services.AddScoped<NostosDbContext>(sp =>
                new NostosDbContext(sp.GetRequiredService<DbContextOptions<NostosDbContext>>()));
            services.AddSingleton<IDbContextFactory<NostosDbContext>>(sp =>
                new TestContextFactory(sp.GetRequiredService<DbContextOptions<NostosDbContext>>()));
            services.AddSingleton<BackupSettingsProvider>();

            // One options instance shared by the storage service and the backup
            // service, exactly as Program.cs wires it, so a test can prove the
            // two agree about where things live.
            var storageOptions = Microsoft.Extensions.Options.Options.Create(
                fileStorage ?? new FileStorageOptions());
            services.AddSingleton<IOptions<FileStorageOptions>>(storageOptions);
            services.AddSingleton<IFileStorageService>(sp =>
                new FileStorageService(
                    sp.GetRequiredService<IWebHostEnvironment>(),
                    sp.GetRequiredService<IOptions<FileStorageOptions>>(),
                    sp.GetRequiredService<ILogger<FileStorageService>>()));
            services.AddSingleton<BackupService>();
            var provider = services.BuildServiceProvider();

            using (var db = provider.GetRequiredService<IDbContextFactory<NostosDbContext>>().CreateDbContext())
                db.Database.EnsureCreated();

            return new BackupHarness(contentRoot, resolvedDatabasePath, options, provider);
        }

        public void Dispose()
        {
            _provider.Dispose();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(ContentRoot, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    private sealed class TestContextFactory(DbContextOptions<NostosDbContext> options) : IDbContextFactory<NostosDbContext>
    {
        public NostosDbContext CreateDbContext() => new(options);

        public Task<NostosDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(Path.GetTempPath());

        public string WebRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider WebRootFileProvider { get; set; } =
            new PhysicalFileProvider(Path.GetTempPath());
    }
}
