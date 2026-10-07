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

// #774 AddProviderPreferences must be provably additive on a real SQLite file.
// The suite builds schema with EnsureCreated and never runs migrations
// (AGENTS §7), so this test builds a database at the previous migration, seeds
// representative settings/library data, copies the closed file, migrates the
// copy, and proves the copy gained exactly one empty table while every prior
// row survived — then rolls the copy back and proves only that table disappears.
public sealed class ProviderPreferencesMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261004130543_AddDurableMigrationTransferJobs";
    private const string NewTable = "ProviderPreferences";

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
        //    migration: EnsureCreated from the current model, stamp the full
        //    history, then roll the disposable file back one step.
        await using (var db = new NostosDbContext(originalOptions))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
            currentMigrationId = db.Database.GetMigrations()
                .Single(id => id.Contains("AddProviderPreferences"));

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
            (await db.Database.GetAppliedMigrationsAsync()).Should().NotContain(currentMigrationId);
            (await db.Database.GetPendingMigrationsAsync()).Should().Equal([currentMigrationId]);
        }

        // 2. Seed representative preexisting library/settings data, then capture
        //    the values a destructive migration would quietly take with it.
        await SeedAsync(originalOptions);
        var originalSnapshot = await ReadSnapshotAsync(originalOptions);

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

        // 4. On the copy, exactly the provider-preferences migration is pending,
        //    then apply it.
        var copyOptions = FileOptions(copyPath);
        await using (var db = new NostosDbContext(copyOptions))
        {
            (await db.Database.GetPendingMigrationsAsync())
                .Should().Equal([currentMigrationId], "the new migration is the only pending one");
            await db.Database.MigrateAsync();
            (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(currentMigrationId);
        }

        // 5. Prior rows are intact and unchanged; the new table exists and is
        //    empty — no seeded default row that could freeze a provider's state.
        (await ReadSnapshotAsync(copyOptions)).Should().Be(originalSnapshot);
        await using (var db = new NostosDbContext(copyOptions))
        {
            (await db.ProviderPreferences.CountAsync()).Should().Be(0);
        }

        // 6. The migrated table shape must equal the shape the current model
        //    creates, so a regenerated migration cannot silently drift.
        var modelPath = Path.Combine(directory, "model.db");
        await using (var db = new NostosDbContext(FileOptions(modelPath)))
        {
            (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
        }

        (await TableSchemaAsync(copyPath)).Should().Equal(
            await TableSchemaAsync(modelPath),
            "the migrated schema must match the schema generated from the current model");

        // 7. The original file was not touched: same bytes, same data, no new table.
        new FileInfo(originalPath).Length.Should().Be(originalLength);
        Sha256File(originalPath).Should().Be(originalHash);
        (await ReadSnapshotAsync(originalOptions)).Should().Be(originalSnapshot);
        (await TableNamesAsync(originalPath)).Should().NotContain(NewTable);

        // 8. Roll the disposable copy back; the table disappears and every prior
        //    row remains. A downgrade intentionally discards provider choices.
        await using (var db = new NostosDbContext(copyOptions))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }

        (await TableNamesAsync(copyPath)).Should().NotContain(NewTable);
        (await ReadSnapshotAsync(copyOptions)).Should().Be(originalSnapshot);
    }

    private static async Task SeedAsync(DbContextOptions<NostosDbContext> options)
    {
        await using var db = new NostosDbContext(options);
        var now = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

        var work = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Kept Work",
            Author = "Kept Author",
            NormalizedTitle = "KEPT WORK",
            NormalizedAuthor = "KEPT AUTHOR",
            CreatedAt = now,
        };
        var book = new EBookModel
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Title = "Kept Book",
            Author = "Kept Author",
            CreatedAt = now,
            PageCount = 321,
        };
        var collection = new CollectionModel { Id = Guid.NewGuid(), Name = "Kept Collection" };

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
        db.LibraryStates.Add(new LibraryState
        {
            Id = LibraryState.WellKnownId,
            SingletonSlot = LibraryState.SingletonSentinel,
            StateVersion = "9",
            UpdatedAt = now,
        });
        db.AssistantSettings.Add(new AssistantSettingsModel
        {
            CaptureProcessingMode = "light_polish",
            UpdatedAtUtc = now,
        });

        await db.SaveChangesAsync();
    }

    private static async Task<SettingsSnapshot> ReadSnapshotAsync(
        DbContextOptions<NostosDbContext> options)
    {
        await using var db = new NostosDbContext(options);
        return new SettingsSnapshot(
            Works: await db.Works.CountAsync(),
            Books: await db.Books.CountAsync(),
            Collections: await db.Collections.CountAsync(),
            BookCollections: await db.BookCollections.CountAsync(),
            LibraryStates: await db.LibraryStates.CountAsync(),
            AssistantSettings: await db.AssistantSettings.CountAsync(),
            BookTitle: (await db.Books.SingleAsync()).Title,
            CaptureProcessingMode: (await db.AssistantSettings.SingleAsync()).CaptureProcessingMode,
            StateVersion: (await db.LibraryStates.SingleAsync()).StateVersion);
    }

    // Canonical, order-independent fingerprint of the new table: every column
    // (type, nullability, default, PK position) and the CHECK constraint names.
    private static async Task<List<string>> TableSchemaAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        var lines = new List<string>();

        var tableSql = await ScalarTextAsync(
            connection,
            "SELECT \"sql\" FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = @name",
            NewTable);

        var checks = Regex.Matches(tableSql ?? string.Empty, @"CK_[A-Za-z0-9_]+")
            .Select(match => match.Value)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal);
        lines.Add($"{NewTable} checks: {string.Join(",", checks)}");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info(\"{NewTable}\")";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(
                    $"{NewTable} column: {reader.GetString(1)}|{reader.GetString(2)}|" +
                    $"notnull={reader.GetInt32(3)}|default={reader.GetValue(4)}|pk={reader.GetInt32(5)}");
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
        command.CommandText = "SELECT \"name\" FROM \"sqlite_master\" WHERE \"type\" = 'table'";
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
        var directory = Path.Combine(Path.GetTempPath(), $"nostos-774-upgrade-{Guid.NewGuid():N}");
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

    private sealed record SettingsSnapshot(
        int Works,
        int Books,
        int Collections,
        int BookCollections,
        int LibraryStates,
        int AssistantSettings,
        string BookTitle,
        string? CaptureProcessingMode,
        string StateVersion);
}
