using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Data;

/// <summary>
/// Runs the real <c>MoveBookValuesToWork</c> migration (tests otherwise use
/// EnsureCreated, which never executes one) over a database in the previous
/// schema, and replays <see cref="BookValueRollUp"/> over the same rows: the SQL
/// roll-up and the C# rule must give the same book, edition by edition.
/// </summary>
public sealed class MoveBookValuesToWorkMigrationTests : IClassFixture<SqliteTestFixture>
{
    private const string TargetMigration = "MoveBookValuesToWork";
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestFixture _fixture;

    public MoveBookValuesToWorkMigrationTests(SqliteTestFixture fixture) => _fixture = fixture;

    private sealed record PreEdition(Guid Id, Guid WorkId, int Rating, string? Review, bool Favourite,
        DateTime? LastReadAt, DateTime CreatedAt);

    [Fact]
    public async Task Up_rolls_every_work_up_exactly_as_the_shared_rule_does()
    {
        var pre = Scenario(seed: 7, works: 60);
        var path = await PreviousSchemaAsync(pre);

        await using (var db = Open(path))
            await db.Database.MigrateAsync();

        await using var after = Open(path);
        var works = await after.Works.AsNoTracking().ToDictionaryAsync(w => w.Id);
        var editions = await after.Books.AsNoTracking().ToDictionaryAsync(b => b.Id);
        var mismatches = new List<string>();
        foreach (var group in pre.GroupBy(e => e.WorkId))
        {
            var expected = BookValueRollUp.Combine(group.Select(Side));
            var work = works[group.Key];
            if ((work.Rating, work.PersonalReview, work.IsFavorite) != (expected.Rating, expected.Review, expected.IsFavorite))
                mismatches.Add($"work {group.Key}: {(work.Rating, work.PersonalReview, work.IsFavorite)} != {(expected.Rating, expected.Review, expected.IsFavorite)}");
            foreach (var edition in group)
            {
                var row = editions[edition.Id];
                var wantRating = BookValueRollUp.EditionRatingAfter(edition.Rating, expected.Rating);
                var wantReview = BookValueRollUp.EditionReviewAfter(edition.Review, expected.Review);
                if ((row.Progress.Rating, row.Progress.PersonalReview) != (wantRating, wantReview))
                    mismatches.Add($"edition {edition.Id}: {(row.Progress.Rating, row.Progress.PersonalReview)} != {(wantRating, wantReview)}");
            }
        }

        mismatches.Should().BeEmpty();
        pre.Count(e => e.Rating > 0).Should().BeGreaterThan(20, "the scenario really exercises ratings");
        pre.GroupBy(e => e.WorkId).Count(g => g.Select(e => e.Rating).Where(r => r > 0).Distinct().Count() > 1)
            .Should().BeGreaterThan(3, "and genuinely disagreeing works");
        (await ColumnsAsync(path, "Books")).Should().NotContain("Progress_IsFavorite");
    }

    [Fact]
    public async Task Down_restores_every_edition_as_it_read_and_a_second_up_gives_the_same_books()
    {
        var pre = Scenario(seed: 11, works: 40);
        var path = await PreviousSchemaAsync(pre);
        await using (var db = Open(path))
            await db.Database.MigrateAsync();
        var firstUp = await StateAsync(path);

        await using (var db = Open(path))
            await db.GetService<IMigrator>().MigrateAsync(await PreviousMigrationAsync(db));

        (await ColumnsAsync(path, "Works")).Should().NotContain("Rating");
        var down = await ReadPerEditionAsync(path);
        var works = pre.GroupBy(e => e.WorkId).ToDictionary(
            g => g.Key, g => BookValueRollUp.Combine(g.Select(Side)));
        foreach (var edition in pre)
        {
            var book = works[edition.WorkId];
            var (rating, review, favourite) = down[edition.Id];
            rating.Should().Be(edition.Rating > 0 ? edition.Rating : book.Rating, "own rating, else the book's");
            (review ?? string.Empty).Should().Be(
                !string.IsNullOrEmpty(edition.Review) ? edition.Review : book.Review ?? string.Empty);
            favourite.Should().Be(book.IsFavorite, "every edition of a favourite book is a favourite again");
        }

        await using (var db = Open(path))
            await db.Database.MigrateAsync();
        (await StateAsync(path)).Should().BeEquivalentTo(firstUp, "up, down and up again is stable");
    }

    [Fact]
    public async Task The_guard_refuses_to_drop_the_column_when_a_favourite_has_no_work_to_land_on()
    {
        var pre = Scenario(seed: 3, works: 6);
        var path = await PreviousSchemaAsync(pre);
        var victim = pre.First(e => e.Favourite || e.Rating > 0);
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_keys = OFF; UPDATE Books SET WorkId = '{Guid.NewGuid():D}' WHERE Id = '{victim.Id.ToString("D").ToUpperInvariant()}'";
            await command.ExecuteNonQueryAsync();
        }

        await using var db = Open(path);
        var act = async () => await db.Database.MigrateAsync();

        await act.Should().ThrowAsync<Exception>().Where(e => e.ToString().Contains("CHECK constraint failed"));
        (await ColumnsAsync(path, "Books")).Should().Contain("Progress_IsFavorite", "nothing was dropped");
    }

    // ------------------------------------------------------------------

    private static BookValueSide Side(PreEdition e) =>
        new(e.Id, e.LastReadAt, e.CreatedAt, e.Rating, e.Review, e.Favourite);

    /// <summary>
    /// Works of one to four editions with a mix of agreeing, one-sided and
    /// differing values, equal read times and equal creation times, so every
    /// branch of the rule (and its tie-breaks) is hit.
    /// </summary>
    private static List<PreEdition> Scenario(int seed, int works)
    {
        var random = new Random(seed);
        string?[] reviews = [null, null, "", "Slow and rich.", "Overrated.", "Read it twice."];
        var list = new List<PreEdition>();
        for (var w = 0; w < works; w++)
        {
            var workId = Guid.NewGuid();
            for (var e = random.Next(1, 5); e > 0; e--)
            {
                list.Add(new PreEdition(
                    Guid.NewGuid(), workId,
                    random.Next(0, 3) == 0 ? 0 : random.Next(1, 6),
                    reviews[random.Next(reviews.Length)],
                    random.Next(0, 6) == 0,
                    random.Next(0, 3) == 0 ? null : Origin.AddDays(random.Next(0, 4)),
                    Origin.AddDays(random.Next(0, 3))));
            }
        }

        return list;
    }

    private NostosDbContext Open(string path) => new(new DbContextOptionsBuilder<NostosDbContext>()
        .UseSqlite($"Data Source={path}", sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
        .Options);

    /// <summary>A database as the schema before this migration had it, holding <paramref name="pre"/>.</summary>
    private async Task<string> PreviousSchemaAsync(IReadOnlyList<PreEdition> pre)
    {
        var path = _fixture.CreateDatabasePath();
        await using (var db = Open(path))
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var group in pre.GroupBy(e => e.WorkId))
            {
                db.Works.Add(new WorkModel
                {
                    Id = group.Key, Title = "T" + group.Key, NormalizedTitle = "T" + group.Key,
                    CreatedAt = Origin,
                });
                foreach (var e in group)
                {
                    db.Books.Add(new EBookModel
                    {
                        Id = e.Id, WorkId = e.WorkId, Title = "T" + e.WorkId, CreatedAt = e.CreatedAt,
                        Progress = new ReadingProgress
                        {
                            Rating = e.Rating,
                            PersonalReview = e.Review,
                            LastReadAt = e.LastReadAt,
                        },
                    });
                }
            }

            await db.SaveChangesAsync();

            // Step back to the previous schema: the favourite lives on the edition
            // again, and the work has no values.
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Books\" ADD COLUMN \"Progress_IsFavorite\" INTEGER NOT NULL DEFAULT 0");
            foreach (var e in pre.Where(e => e.Favourite))
                await db.Database.ExecuteSqlRawAsync(
                    $"UPDATE \"Books\" SET \"Progress_IsFavorite\" = 1 WHERE \"Id\" = '{e.Id.ToString("D").ToUpperInvariant()}'");
            foreach (var column in new[] { "Rating", "IsFavorite", "PersonalReview" })
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"Works\" DROP COLUMN \"{column}\"");

            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
            foreach (var id in db.Database.GetMigrations().Where(id => !id.EndsWith(TargetMigration, StringComparison.Ordinal)))
                await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.0")));
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private static async Task<string> PreviousMigrationAsync(NostosDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(id => id.EndsWith(TargetMigration, StringComparison.Ordinal));
        await Task.CompletedTask;
        return all[index - 1];
    }

    private static async Task<List<string>> ColumnsAsync(string path, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<Dictionary<Guid, (int Rating, string? Review, bool Favourite)>> ReadPerEditionAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT \"Id\", \"Progress_Rating\", \"Progress_PersonalReview\", \"Progress_IsFavorite\" FROM \"Books\"";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new Dictionary<Guid, (int, string?, bool)>();
        while (await reader.ReadAsync())
            rows[reader.GetGuid(0)] = (reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3) == 1);
        return rows;
    }

    private async Task<string> StateAsync(string path)
    {
        await using var db = Open(path);
        var works = await db.Works.AsNoTracking().OrderBy(w => w.Id)
            .Select(w => $"{w.Id}|{w.Rating}|{w.PersonalReview}|{w.IsFavorite}").ToListAsync();
        var books = await db.Books.AsNoTracking().OrderBy(b => b.Id)
            .Select(b => $"{b.Id}|{b.Progress.Rating}|{b.Progress.PersonalReview}").ToListAsync();
        return string.Join("\n", works) + "\n--\n" + string.Join("\n", books);
    }
}
