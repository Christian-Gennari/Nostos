using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Data;

// Issue #835: AddBookTracks must be purely additive. The rest of the suite
// builds its schema with EnsureCreated, which never runs a migration, so the
// migration itself is exercised here against a real SQLite file: existing
// books keep every value, and the new column arrives empty.
public sealed class BookTracksMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261006225351_AddProviderPreferences";
    private const string Column = "FileDetails_TracksJson";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"nostos-tracks-migration-{Guid.NewGuid():N}");

    [Fact]
    public async Task Additive_migration_upgrades_and_downgrades_a_real_sqlite_database()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "library.db");
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={path};Pooling=False",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;

        string migrationId;

        // 1. A database at the current schema with a complete history, holding
        //    books as they exist today.
        await using (var db = new NostosDbContext(options))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
            migrationId = db.Database.GetMigrations().Single(id => id.Contains("AddBookTracks"));
            db.Database.GetMigrations().Last().Should().Be(migrationId, "this is the newest migration");

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
            foreach (var id in db.Database.GetMigrations())
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")));

            var work = new WorkModel
            {
                Id = Guid.NewGuid(),
                Title = "Kept Work",
                NormalizedTitle = "KEPT WORK",
                NormalizedAuthor = string.Empty,
            };
            db.Works.Add(work);
            db.Books.Add(new AudioBookModel
            {
                Id = Guid.NewGuid(),
                WorkId = work.Id,
                Work = work,
                Title = "Single File Audiobook",
                Duration = "10:00:00",
                Progress = new ReadingProgress { LastLocation = "1234.5", ProgressPercent = 40 },
                FileDetails = new FileInfoDetails
                {
                    HasFile = true,
                    FileName = "book.m4b",
                    CoverFileName = "cover.jpg",
                    ChaptersJson = "[{\"title\":\"One\",\"startTime\":0}]",
                },
            });
            db.Books.Add(new EBookModel
            {
                Id = Guid.NewGuid(),
                WorkId = work.Id,
                Work = work,
                Title = "An Ebook",
                FileDetails = new FileInfoDetails { HasFile = true, FileName = "book.epub" },
            });
            await db.SaveChangesAsync();
        }

        // 2. Roll back to the schema users have today: the column is gone and
        //    nothing else about the books has changed.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            (await db.Database.GetPendingMigrationsAsync()).Should().Equal(migrationId);
        }

        (await ColumnsAsync(path)).Should().NotContain(Column);
        var before = await SnapshotAsync(path);
        before.Should().HaveCount(2);

        // 3. Apply the migration.
        await using (var db = new NostosDbContext(options))
        {
            await db.Database.MigrateAsync();
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }

        (await ColumnsAsync(path)).Should().Contain(Column);
        (await SnapshotAsync(path)).Should().Equal(before, "every existing book keeps every value");

        // 4. Existing books are single-file books, and the model works.
        await using (var db = new NostosDbContext(options))
        {
            var books = await db.Books.AsNoTracking().OrderBy(b => b.Title).ToListAsync();
            books.Should().OnlyContain(b => b.FileDetails.TracksJson == null);
            books.Select(b => b.FileDetails.FileName).Should().Equal("book.epub", "book.m4b");

            var audio = await db.Books.SingleAsync(b => b.Title == "Single File Audiobook");
            audio.FileDetails.TracksJson = "[]";
            await db.SaveChangesAsync();
        }

        // 5. And it rolls back again without disturbing the rest.
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        (await ColumnsAsync(path)).Should().NotContain(Column);
        (await SnapshotAsync(path)).Should().Equal(before);
    }

    private static async Task<List<string>> ColumnsAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"Books\")";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        return columns;
    }

    /// <summary>Every column of every book except the one this migration owns, read without the model.</summary>
    private static async Task<List<string>> SnapshotAsync(string path)
    {
        var columns = (await ColumnsAsync(path)).Where(name => name != Column).Order(StringComparer.Ordinal).ToList();
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(c => $"\"{c}\""))} FROM \"Books\" ORDER BY \"Id\"";
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join(
                "|",
                Enumerable.Range(0, reader.FieldCount)
                    .Select(i => $"{columns[i]}={(reader.IsDBNull(i) ? "<null>" : reader.GetValue(i))}")));
        }

        return rows;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }
}
