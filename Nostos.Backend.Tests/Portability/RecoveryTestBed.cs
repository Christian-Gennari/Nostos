using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Real temp directories, real SQLite files and a fake clock for the Slice 6
/// recovery tests. The database and media roots intentionally live in separate
/// sibling directories so per-volume accounting can be exercised explicitly.
/// </summary>
internal sealed class RecoveryTestBed : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"nostos-681-recovery-{Guid.NewGuid():N}");
    internal Guid JobId { get; } = Guid.NewGuid();
    internal Guid OperationId { get; } = Guid.NewGuid();
    internal string Revision { get; } = "revision-1";
    internal RecoveryClock Clock { get; } = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    internal TransferStorageOptions Options { get; } = new() { DiskSafetyMarginBytes = 0, DiskSafetyMarginPercent = 0 };
    internal FakeTransferVolume TransferVolume { get; } = new()
    {
        AvailableFreeSpaceBytes = 1_000_000_000,
        TotalSizeBytes = 1_000_000_000,
    };

    internal FakeVolumeSpaceProbe Probe { get; } = new();
    internal SelfHostedActivationPaths Paths { get; }
    internal LibraryMaintenanceCoordinator Gate { get; } = new(new LibraryMaintenanceOptions
    {
        DrainTimeout = TimeSpan.FromSeconds(5),
    });

    internal SelfHostedActivationJournalStore Journals { get; }
    internal SelfHostedRecoveryManifestStore Manifests { get; }
    internal DbContextOptions<NostosDbContext> DbOptions { get; }

    internal RecoveryTestBed()
    {
        Directory.CreateDirectory(Path.Combine(Root, "db-volume"));
        Directory.CreateDirectory(Path.Combine(Root, "media-volume"));
        Paths = new SelfHostedActivationPaths(
            Path.Combine(Root, "db-volume", "nostos.db"),
            Path.Combine(Root, "media-volume", "library"));
        Paths.Prepare(JobId);
        DbOptions = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={Paths.LiveDatabase};Pooling=False")
            .Options;
        Journals = new SelfHostedActivationJournalStore(Paths, Gate);
        Manifests = new SelfHostedRecoveryManifestStore(Paths);
        Probe.Set(Paths.LiveDatabase, 1_000_000_000, 1_000_000_000);
        Probe.Set(Paths.LiveMedia, 1_000_000_000, 1_000_000_000);
    }

    internal async Task SeedLiveLibraryAsync()
    {
        await CreateDatabaseAsync(Paths.LiveDatabase, "original");
        CreateMedia(Paths.LiveMedia, "original");
        await CreateDatabaseAsync(Paths.CandidateDatabase(JobId), "candidate");
        CreateMedia(Paths.CandidateMedia(JobId), "candidate");
    }

    internal NostosDbContext OpenDatabase() => new(DbOptions);

    internal SelfHostedMigrationRecoveryService CreateService(NostosDbContext? db = null)
    {
        db ??= OpenDatabase();
        var activationCapacity = new SelfHostedActivationCapacity(Probe, Microsoft.Extensions.Options.Options.Create(Options));
        var capacity = new TransferStorageCapacity(db, TransferVolume,
            Microsoft.Extensions.Options.Options.Create(Options), Clock);
        return new SelfHostedMigrationRecoveryService(Paths, Manifests, Journals, activationCapacity,
            capacity, db, Gate, Microsoft.Extensions.Options.Options.Create(Options), Clock);
    }

    internal SelfHostedActivationJournal SeedJournal(
        Guid? jobId = null,
        Guid? operationId = null,
        string? revision = null,
        SelfHostedActivationPhase phase = SelfHostedActivationPhase.CandidatePrepared)
    {
        var journal = new SelfHostedActivationJournal(
            jobId ?? JobId,
            operationId ?? OperationId,
            phase,
            revision ?? Revision,
            RetainPreviousLibrary: true,
            Clock.GetUtcNow());
        Journals.Write(journal);
        return journal;
    }

    internal async Task<SelfHostedActivationJournal> AdvanceJournalAsync(
        SelfHostedActivationJournal journal,
        SelfHostedActivationPhase phase,
        IAsyncDisposable lease)
    {
        var route = new[]
        {
            SelfHostedActivationPhase.CandidatePrepared,
            SelfHostedActivationPhase.ExclusiveEntered,
            SelfHostedActivationPhase.DatabaseCheckpointed,
            SelfHostedActivationPhase.CutoverPrepared,
            SelfHostedActivationPhase.PreviousMediaRetained,
            SelfHostedActivationPhase.PreviousDatabaseRetained,
            SelfHostedActivationPhase.CandidateMediaActivated,
            SelfHostedActivationPhase.CandidateDatabaseActivated,
            SelfHostedActivationPhase.PostActivationVerified,
            SelfHostedActivationPhase.Committed,
        };
        var current = Array.IndexOf(route, journal.Phase);
        var target = Array.IndexOf(route, phase);
        if (current < 0 || target < current)
            throw new InvalidOperationException($"Cannot advance from {journal.Phase} to {phase}.");
        for (var index = current + 1; index <= target; index++)
            journal = Journals.Advance(journal.JobId, route[index], lease);
        await Task.CompletedTask;
        return journal;
    }

    /// <summary>The candidate database is a clone of the live host database, including reservation rows.</summary>
    internal void CloneLiveDatabaseToCandidate()
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
            File.Delete(Paths.LiveDatabase + suffix);
        File.Copy(Paths.LiveDatabase, Paths.CandidateDatabase(JobId), overwrite: true);
    }

    /// <summary>Runs the Slice 3 startup reconciler exactly as a restart would.</summary>
    internal async Task ReconcileAsync()
    {
        var marker = new LibraryMaintenanceMarker(Paths.LiveDatabase);
        var gate = new LibraryMaintenanceCoordinator(marker: marker);
        var store = new SelfHostedActivationJournalStore(Paths, gate);
        var engine = new SelfHostedActivationRecoveryStartupService(Paths, store, gate,
        [
            new SelfHostedActivationComponentStep(Paths, database: false),
            new SelfHostedActivationComponentStep(Paths, database: true),
        ]);
        await engine.ReconcileIncompleteAsync();
        gate.IsMaintenanceActive.Should().BeFalse();
        File.Exists(marker.MarkerPath).Should().BeFalse();
    }

    internal void CreatePreviousMaterial(Guid jobId, string databaseGeneration, params string[] mediaFiles)
    {
        Paths.PrepareRecovery(jobId);
        var database = Paths.PreviousDatabase(jobId);
        CreateRawDatabase(database, databaseGeneration);
        var media = Paths.PreviousMedia(jobId);
        Directory.CreateDirectory(media);
        foreach (var relative in mediaFiles)
        {
            var target = Path.Combine(media, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, relative);
        }
    }

    internal static async Task CreateDatabaseAsync(string path, string generation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        await using var db = new NostosDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlAsync($"CREATE TABLE IF NOT EXISTS Generation(Value TEXT NOT NULL)");
        await db.Database.ExecuteSqlAsync($"DELETE FROM Generation");
        await db.Database.ExecuteSqlAsync($"INSERT INTO Generation VALUES ({generation})");
        await db.Database.ExecuteSqlAsync(
            $"CREATE TABLE IF NOT EXISTS __EFMigrationsHistory(MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL)");
        await db.Database.ExecuteSqlAsync(
            $"INSERT OR REPLACE INTO __EFMigrationsHistory VALUES ({"20260101000000_Initial"}, {"10.0.0"})");
    }

    internal static void CreateRawDatabase(string path, string generation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS Generation(Value TEXT NOT NULL); DELETE FROM Generation; INSERT INTO Generation VALUES ($value);";
        command.Parameters.AddWithValue("$value", generation);
        command.ExecuteNonQuery();
    }

    internal static string DatabaseGeneration(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Generation";
        return (string)command.ExecuteScalar()!;
    }

    internal static void CreateMedia(string path, string generation)
    {
        Directory.CreateDirectory(path);
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Directory.CreateDirectory(Path.Combine(path, first.ToString("N")));
        Directory.CreateDirectory(Path.Combine(path, second.ToString("N")));
        File.WriteAllText(Path.Combine(path, first.ToString("N"), "book.epub"), $"{generation}-book-a");
        File.WriteAllText(Path.Combine(path, first.ToString("N"), "cover.jpg"), $"{generation}-cover-a");
        File.WriteAllText(Path.Combine(path, first.ToString("N"), "cover-thumb-160.webp"), $"{generation}-thumb-a");
        File.WriteAllText(Path.Combine(path, second.ToString("N"), "book.epub"), $"{generation}-book-b");
        File.WriteAllText(Path.Combine(path, second.ToString("N"), "book.epub.partial"), $"{generation}-partial-b");
    }

    internal static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the disposable fixture.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class RecoveryClock(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; private set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}

internal sealed class FakeTransferVolume : ITransferVolume
{
    public long AvailableFreeSpaceBytes { get; set; }

    public long TotalSizeBytes { get; set; }
}

internal sealed class FakeVolumeSpaceProbe : ISelfHostedVolumeSpaceProbe
{
    private readonly Dictionary<string, string> _volumeByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Free, long Total)> _volumes = new(StringComparer.Ordinal);

    public void Set(string path, long free, long total, string? volume = null)
    {
        var full = Path.GetFullPath(path);
        var key = volume ?? full;
        _volumeByPath[full] = key;
        _volumes[key] = (free, total);
    }

    public long AvailableFreeSpaceBytes(string path) => Lookup(path).Free;

    public long TotalSizeBytes(string path) => Lookup(path).Total;

    public bool AreSameVolume(string first, string second) => VolumeKey(first) == VolumeKey(second);

    private (long Free, long Total) Lookup(string path) => _volumes[VolumeKey(path)];

    private string VolumeKey(string path)
    {
        var full = Path.GetFullPath(path);
        return _volumeByPath.TryGetValue(full, out var key)
            ? key
            : throw new InvalidOperationException($"No fake volume configured for '{full}'.");
    }
}
