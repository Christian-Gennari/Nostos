using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Xunit;

namespace Nostos.Backend.Tests.Data;

// #1587 renames the Stylos sync tables to Carrel. The suite builds schema with
// EnsureCreated and never runs migrations (AGENTS §7), so these tests run the
// real migrations on a real SQLite file: rows written under the old names
// survive both directions, and no constraint keeps the old name.
public sealed class CarrelRenameMigrationTests : IDisposable
{
    private const string AddMigration = "20261112073447_AddStylosSync";

    private static readonly string[] Suffixes = { "Devices", "Files", "LibraryItems", "Pairings", "SyncStates" };

    private readonly List<string> _directories = new();

    [Fact]
    public async Task Rename_moves_rows_and_constraints_to_Carrel_and_back()
    {
        var path = Path.Combine(NewDirectory(), "carrel.db");
        var options = Options(path);

        // A database that already has the old Stylos tables: schema and history
        // stop at AddStylosSync.
        await CreateAtHeadAsync(options);
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(AddMigration);
        }

        (await TablesAsync(path)).Should().Contain(Suffixes.Select(s => "Stylos" + s));
        await SeedAsync(options, "Stylos");

        string renameId;
        await using (var db = new NostosDbContext(options))
        {
            renameId = db.Database.GetMigrations().Single(id => id.Contains("RenameStylosSyncToCarrel"));
            await db.GetService<IMigrator>().MigrateAsync(renameId);
            (await db.Database.GetPendingMigrationsAsync()).Should().NotContain(renameId);
        }

        await AssertSchemaAsync(path, "Carrel", "Stylos");
        await AssertRowsAsync(options, "Carrel");
        var device = await FirstDeviceAsync(options);
        device.Should().Be("X250");

        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(AddMigration);
        }

        await AssertSchemaAsync(path, "Stylos", "Carrel");
        await AssertRowsAsync(options, "Stylos");

        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(renameId);
        }

        await AssertSchemaAsync(path, "Carrel", "Stylos");
        await AssertRowsAsync(options, "Carrel");
    }

    [Fact]
    public async Task Migrated_tables_match_the_ones_the_model_creates_on_a_fresh_database()
    {
        var freshPath = Path.Combine(NewDirectory(), "fresh.db");
        var migratedPath = Path.Combine(NewDirectory(), "migrated.db");

        // Fresh install: the model builds the tables itself.
        await CreateAtHeadAsync(Options(freshPath));
        await AssertSchemaAsync(freshPath, "Carrel", "Stylos");

        // Upgrade: down to the old tables, then forward again.
        var options = Options(migratedPath);
        await CreateAtHeadAsync(options);
        await using (var db = new NostosDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync(AddMigration);
        }

        await AssertSchemaAsync(migratedPath, "Stylos", "Carrel");
        await using (var db = new NostosDbContext(options))
        {
            await db.Database.MigrateAsync();
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }

        await AssertSchemaAsync(migratedPath, "Carrel", "Stylos");
        (await DdlAsync(migratedPath)).Should().BeEquivalentTo(await DdlAsync(freshPath));
    }

    private static async Task CreateAtHeadAsync(DbContextOptions<NostosDbContext> options)
    {
        await using var db = new NostosDbContext(options);
        (await db.Database.EnsureCreatedAsync()).Should().BeTrue();
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
        foreach (var id in db.Database.GetMigrations())
        {
            await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")));
        }
    }

    // The Carrel tables and their indexes, whitespace and quoting flattened.
    private static async Task<List<string>> DdlAsync(string path) =>
        (await ScalarsAsync(path, "SELECT sql FROM sqlite_master WHERE name LIKE '%Carrel%' AND sql IS NOT NULL"))
        .Select(sql => new string(sql.Where(c => !char.IsWhiteSpace(c) && c != '"' && c != '`').ToArray()))
        .ToList();

    private static DbContextOptions<NostosDbContext> Options(string path) =>
        new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite(
                $"Data Source={path};Pooling=False",
                sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
            .Options;

    private static async Task SeedAsync(DbContextOptions<NostosDbContext> options, string prefix)
    {
        await using var db = new NostosDbContext(options);
        var hash = new string('a', 64);
        var created = "2026-11-01 00:00:00";
        await db.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO {prefix}Devices (Id, OwnerKey, Name, PublicKey, TokenHash, CreatedAtUtc)
            VALUES ('11111111-1111-1111-1111-111111111111', 'owner', 'X250', 'pk', '{hash}', '{created}');
            INSERT INTO {prefix}Files (FileId, OwnerKey, DraftId, Path, Hash, Rev, Deleted, ModifiedAtUtc, Cursor)
            VALUES ('22222222-2222-2222-2222-222222222222', 'owner', '33333333-3333-3333-3333-333333333333', 'a.md', '{hash}', 3, 0, '{created}', 7);
            INSERT INTO {prefix}LibraryItems (OwnerKey, NoteId, Cursor, Deleted, Signature, ModifiedAtUtc)
            VALUES ('owner', '44444444-4444-4444-4444-444444444444', 5, 0, '{hash}', '{created}');
            INSERT INTO {prefix}Pairings (Id, Code, DeviceName, DevicePublicKey, Status, CreatedAtUtc, ExpiresAtUtc)
            VALUES ('55555555-5555-5555-5555-555555555555', 'ABC-123', 'X250', 'pk', 'pending', '{created}', '{created}');
            INSERT INTO {prefix}SyncStates (OwnerKey, FilesCursor, LibraryCursor, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('owner', 7, 5, '{created}', '{created}');
            """);
    }

    private static async Task AssertRowsAsync(DbContextOptions<NostosDbContext> options, string prefix)
    {
        await using var db = new NostosDbContext(options);
        foreach (var (suffix, expected) in new[]
                 {
                     ("Devices", 1L), ("Files", 1L), ("LibraryItems", 1L), ("Pairings", 1L), ("SyncStates", 1L),
                 })
        {
            var count = await db.Database
                .SqlQueryRaw<long>($"SELECT COUNT(*) AS \"Value\" FROM \"{prefix}{suffix}\"")
                .SingleAsync();
            count.Should().Be(expected, $"{prefix}{suffix} keeps its row");
        }

        var cursor = await db.Database
            .SqlQueryRaw<long>($"SELECT \"FilesCursor\" AS \"Value\" FROM \"{prefix}SyncStates\"")
            .SingleAsync();
        cursor.Should().Be(7);
    }

    private static async Task<string> FirstDeviceAsync(DbContextOptions<NostosDbContext> options)
    {
        await using var db = new NostosDbContext(options);
        return (await db.CarrelDevices.SingleAsync()).Name;
    }

    private static async Task AssertSchemaAsync(string path, string present, string absent)
    {
        var tables = await TablesAsync(path);
        tables.Should().Contain(Suffixes.Select(s => present + s));
        tables.Should().NotContain(Suffixes.Select(s => absent + s));

        var objects = await ScalarsAsync(path, "SELECT name FROM sqlite_master WHERE name LIKE '%" + absent + "%'");
        objects.Should().BeEmpty($"no index or table keeps the {absent} name");
        var ddl = await ScalarsAsync(path, "SELECT sql FROM sqlite_master WHERE sql LIKE '%" + absent + "%'");
        ddl.Should().BeEmpty($"no constraint keeps the {absent} name");
        var indexes = await ScalarsAsync(path, "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'IX_" + present + "%'");
        indexes.Should().HaveCount(8);
    }

    private static async Task<List<string>> TablesAsync(string path) =>
        await ScalarsAsync(path, "SELECT name FROM sqlite_master WHERE type = 'table'");

    private static async Task<List<string>> ScalarsAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }

    private string NewDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("nostos-carrel-rename-migration-").FullName;
        _directories.Add(directory);
        return directory;
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
