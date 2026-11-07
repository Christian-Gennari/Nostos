// Guard for #1474: the deterministic bed shares one host across scenarios,
// so it must prove that each scenario still starts from the seeded library.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Data;
using Xunit;

// Same serial collection as the campaign: both set the process-wide key
// variable the scripted host reads.
[Collection(QualityBedTimingCollection.Name)]
public sealed class QualityBedIsolationTests
{
    [Fact]
    public async Task A_scenario_never_sees_the_previous_scenarios_writes()
    {
        var key = QualityRunner.DeterministicKeyVariable;
        var previous = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "qb-dummy-not-a-secret");
        try
        {
            using var shared = QualityBedSharedHost.CreateDeterministic(key);
            var databasePath = shared.Host.Factory.DatabasePath;
            var strayFile = Path.Combine(shared.Host.Factory.BooksRootPath, "stray-from-c6.txt");

            string seeded;
            using (shared.Lease())
                seeded = Fingerprint(databasePath);

            // C6 captures a note through the real turn route.
            var c6 = QualityCatalogue.All.Single(s => s.Id == "C6");
            var outcome = await QualityRunner.RunScenarioAsync(
                c6, "quality-scripted", 0, QualityModes.Deterministic, shared.Lease);
            Assert.Empty(outcome.Failures);
            Assert.True(
                Guid.TryParse(outcome.Turns[^1].Response?.CapturedNoteId, out var capturedId),
                "C6 should capture a note");

            // The write is real and still there until the next lease, and the
            // scenario also leaves a file in the storage root.
            Assert.True(await NoteExistsAsync(shared.Host, capturedId));
            Assert.NotEqual(seeded, Fingerprint(databasePath));
            Directory.CreateDirectory(Path.GetDirectoryName(strayFile)!);
            await File.WriteAllTextAsync(strayFile, "left behind by the previous scenario");

            using var next = shared.Lease();

            // Byte-for-byte the seeded rows in every table (FTS shadow tables
            // and book-text state included), as the host itself sees them.
            Assert.Equal(seeded, Fingerprint(databasePath));
            Assert.False(await NoteExistsAsync(next, capturedId));
            Assert.Equal(21, await NoteCountAsync(next));
            Assert.False(File.Exists(strayFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    private static async Task<bool> NoteExistsAsync(QualityBedHost host, Guid noteId)
    {
        using var scope = host.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NostosDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Notes.AsNoTracking().AnyAsync(n => n.Id == noteId);
    }

    private static async Task<int> NoteCountAsync(QualityBedHost host)
    {
        using var scope = host.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NostosDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Notes.CountAsync();
    }

    /// <summary>A hash of every row of every table, independent of row order.</summary>
    private static string Fingerprint(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = list.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        var all = new StringBuilder();
        foreach (var table in tables)
        {
            var rows = new List<string>();
            using var select = connection.CreateCommand();
            select.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\"";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var row = new StringBuilder();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.GetValue(i);
                    row.Append(value is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(value))
                        .Append('\u001f');
                }

                rows.Add(row.ToString());
            }

            rows.Sort(StringComparer.Ordinal);
            all.Append(table).Append('\u001e').Append(rows.Count).Append('\u001e');
            foreach (var row in rows)
                all.Append(row).Append('\u001e');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all.ToString())));
    }
}
