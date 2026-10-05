using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Product.BookText;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Real temp directories, real SQLite files and a deterministic clock for the
/// Slice 10 maintenance workers: recovery expiry cleanup, activation orphan
/// sweep and the post-activation derived rebuild.
/// </summary>
internal sealed class ActivationMaintenanceTestBed : IDisposable
{
    private static readonly Guid FirstBook = Guid.Parse("11111111-1111-1111-1111-111111111111");

    internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"nostos-681-maintenance-{Guid.NewGuid():N}");
    internal RecoveryClock Clock { get; } = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
    internal SelfHostedActivationPaths Paths { get; }
    internal LibraryMaintenanceCoordinator Gate { get; } =
        new(new LibraryMaintenanceOptions { DrainTimeout = TimeSpan.FromSeconds(5) });
    internal SelfHostedActivationJournalStore Journals { get; }
    internal SelfHostedRecoveryManifestStore Manifests { get; }
    internal TransferStorageOptions StorageOptions { get; } = new()
    {
        DiskSafetyMarginBytes = 0,
        DiskSafetyMarginPercent = 0,
    };

    internal SelfHostedActivationMaintenanceOptions MaintenanceOptions { get; } = new();
    internal FakeTransferVolume TransferVolume { get; } = new()
    {
        AvailableFreeSpaceBytes = 1_000_000_000,
        TotalSizeBytes = 1_000_000_000,
    };

    internal FakeVolumeSpaceProbe Probe { get; } = new();
    internal DerivedSchedulerProbe SchedulerProbe { get; } = new();
    internal FailableDerivedArtifactStorage ArtifactStorage { get; } = new();
    internal ServiceProvider Services { get; }

    internal ActivationMaintenanceTestBed()
    {
        Directory.CreateDirectory(Path.Combine(Root, "db-volume"));
        Directory.CreateDirectory(Path.Combine(Root, "media-volume"));
        Paths = new SelfHostedActivationPaths(
            Path.Combine(Root, "db-volume", "nostos.db"),
            Path.Combine(Root, "media-volume", "library"));
        Journals = new SelfHostedActivationJournalStore(Paths, Gate);
        Manifests = new SelfHostedRecoveryManifestStore(Paths);
        Probe.Set(Paths.LiveDatabase, 1_000_000_000, 1_000_000_000, "db");
        Probe.Set(Paths.LiveMedia, 1_000_000_000, 1_000_000_000, "media");
        RecoveryTestBed.CreateDatabaseAsync(Paths.LiveDatabase, "active").GetAwaiter().GetResult();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Clock);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(Gate);
        services.AddSingleton<ILibraryMaintenanceCoordinator>(Gate);
        services.AddSingleton(Paths);
        services.AddSingleton(Journals);
        services.AddSingleton(Manifests);
        services.AddSingleton(Options.Create(StorageOptions));
        services.AddSingleton(Options.Create(MaintenanceOptions));
        services.AddSingleton<ITransferVolume>(TransferVolume);
        services.AddSingleton<ISelfHostedVolumeSpaceProbe>(Probe);
        services.AddDbContextFactory<NostosDbContext>(options =>
            options.UseSqlite($"Data Source={Paths.LiveDatabase};Pooling=False"));
        services.AddScoped<ITransferStorageCapacity, TransferStorageCapacity>();
        services.AddScoped<SelfHostedActivationCapacity>();
        services.AddScoped(sp => new SelfHostedMigrationRecoveryService(
            Paths,
            Manifests,
            Journals,
            sp.GetRequiredService<SelfHostedActivationCapacity>(),
            sp.GetRequiredService<ITransferStorageCapacity>(),
            sp.GetRequiredService<NostosDbContext>(),
            Gate,
            sp.GetRequiredService<IOptions<TransferStorageOptions>>(),
            Clock));
        services.AddScoped<ISelfHostedRecoveryCleanup>(sp =>
            sp.GetRequiredService<SelfHostedMigrationRecoveryService>());
        services.AddScoped<SelfHostedActivationOrphanSweep>();
        services.AddScoped<SqliteBookTextIndex>();
        services.AddScoped<IBookTextIndex>(sp => sp.GetRequiredService<SqliteBookTextIndex>());
        services.AddScoped<IBookTextDerivedReset, SqliteBookTextDerivedReset>();
        services.AddSingleton<IBookDerivedArtifactStorage>(ArtifactStorage);
        services.AddScoped<IBookTextIngestionScheduler, BookTextIngestionScheduler>();
        services.AddScoped<IBookTextDerivedScheduler>(sp => new RecordingDerivedScheduler(
            new StrictBookTextDerivedScheduler(sp.GetRequiredService<IBookTextIndex>(), ArtifactStorage),
            SchedulerProbe));
        services.AddSingleton<SelfHostedDerivedRebuildService>();
        Services = services.BuildServiceProvider();
    }

    internal NostosDbContext OpenDatabase() =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={Paths.LiveDatabase};Pooling=False")
            .Options);

    internal SelfHostedRecoveryCleanupWorker CreateCleanupWorker() =>
        new(Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(MaintenanceOptions), Clock,
            Gate, Manifests, NullLogger<SelfHostedRecoveryCleanupWorker>.Instance);

    internal SelfHostedActivationOrphanSweep CreateSweep() =>
        new(Paths, Journals, Manifests, OpenDatabase(), Options.Create(MaintenanceOptions), Clock,
            NullLogger<SelfHostedActivationOrphanSweep>.Instance);

    internal SelfHostedDerivedRebuildService CreateRebuildService() =>
        new(Paths, Journals, Gate, Services.GetRequiredService<IServiceScopeFactory>(), Clock,
            NullLogger<SelfHostedDerivedRebuildService>.Instance);

    internal SelfHostedDerivedRebuildWorker CreateRebuildWorker(SelfHostedDerivedRebuildService service) =>
        new(service, Options.Create(MaintenanceOptions), Clock,
            NullLogger<SelfHostedDerivedRebuildWorker>.Instance);

    internal async Task SeedJobAsync(
        Guid jobId,
        MigrationJobState state,
        MigrationRecoveryStatus recoveryStatus = MigrationRecoveryStatus.NotRequired,
        DateTime? leaseExpiresAtUtc = null,
        string? leaseToken = null)
    {
        await using var db = OpenDatabase();
        db.MigrationJobRecords.Add(new MigrationJobRecord
        {
            Id = jobId,
            Direction = (int)MigrationDirection.Import,
            State = (int)state,
            RecoveryStatus = (int)recoveryStatus,
            ExpiresAtUtc = Clock.UtcNow.UtcDateTime.AddDays(1),
            LeaseExpiresAtUtc = leaseExpiresAtUtc,
            MigrationLeaseToken = leaseToken,
            IdempotencyKey = jobId.ToString("N"),
        });
        await db.SaveChangesAsync();
    }

    internal async Task<MigrationJobRecord> ReadJobAsync(Guid jobId)
    {
        await using var db = OpenDatabase();
        return await db.MigrationJobRecords.AsNoTracking().SingleAsync(job => job.Id == jobId);
    }

    internal void SeedJournal(Guid jobId, Guid? operationId = null, SelfHostedActivationPhase phase = SelfHostedActivationPhase.CandidatePrepared)
    {
        Journals.Write(new SelfHostedActivationJournal(
            jobId, operationId ?? Guid.NewGuid(), phase, "revision-1", true, Clock.GetUtcNow()));
    }

    internal void SeedCommittedResolvedJournal(Guid jobId, Guid? operationId = null)
    {
        var directory = Path.GetDirectoryName(Paths.ResolvedJournal(jobId))!;
        Directory.CreateDirectory(directory);
        var journal = new SelfHostedActivationJournal(
            jobId, operationId ?? Guid.NewGuid(), SelfHostedActivationPhase.Committed,
            "revision-1", true, Clock.GetUtcNow());
        File.WriteAllText(Paths.ResolvedJournal(jobId), SelfHostedActivationDocument.Encode(journal));
    }

    internal (Guid JobId, Guid ReservationId) SeedExpiredCopy(
        DateTimeOffset createdAt,
        MigrationRecoveryStatus status = MigrationRecoveryStatus.Available,
        Guid? jobId = null)
    {
        jobId ??= Guid.NewGuid();
        CreatePreviousMaterial(jobId.Value);
        using var db = OpenDatabase();
        var capacity = new TransferStorageCapacity(db, TransferVolume, Options.Create(StorageOptions), Clock);
        var reserved = capacity
            .TryReserveAsync(1_234, MigrationSessionPurpose.Import, TimeSpan.FromMinutes(15), default)
            .GetAwaiter().GetResult();
        capacity.ClaimAsync(reserved.ReservationId!.Value, jobId.Value, default).GetAwaiter().GetResult();
        var manifest = new SelfHostedRecoveryManifest(
            jobId.Value,
            Guid.NewGuid(),
            createdAt,
            SelfHostedRecoveryManifest.Expiry(createdAt),
            status,
            "revision-1",
            new MigrationExistingCounts(Books: 1),
            100,
            200,
            new string('a', 64),
            [new RecoveryMediaDescriptor(FirstBook, "book", ".epub", 100, new string('b', 64))],
            DatabaseSchemaVersion: "20260101000000_Initial",
            DatabaseMigrationCount: 1,
            DatabaseRetained: true,
            MediaRetained: true,
            RetentionReservationId: reserved.ReservationId);
        Manifests.Write(manifest);
        return (jobId.Value, reserved.ReservationId.Value);
    }

    internal void CreatePreviousMaterial(Guid jobId)
    {
        Paths.PrepareRecovery(jobId);
        RecoveryTestBed.CreateRawDatabase(Paths.PreviousDatabase(jobId), "previous");
        var media = Paths.PreviousMedia(jobId);
        Directory.CreateDirectory(Path.Combine(media, FirstBook.ToString("N")));
        File.WriteAllText(Path.Combine(media, FirstBook.ToString("N"), "book.epub"), "previous-book");
    }

    internal void SeedCandidateDatabase(Guid jobId, DateTime writeTimeUtc)
    {
        var path = Paths.CandidateDatabase(jobId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "candidate");
        File.SetLastWriteTimeUtc(path, writeTimeUtc);
    }

    internal void SeedCandidateMedia(Guid jobId, DateTime writeTimeUtc)
    {
        var root = Paths.CandidateMedia(jobId);
        Directory.CreateDirectory(Path.Combine(root, FirstBook.ToString("N")));
        File.WriteAllText(Path.Combine(root, FirstBook.ToString("N"), "book.epub"), "candidate-book");
        SetTreeWriteTimeUtc(root, writeTimeUtc);
    }

    internal static void SetTreeWriteTimeUtc(string path, DateTime writeTimeUtc)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, writeTimeUtc);
        }

        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
            .OrderByDescending(directory => directory.Length))
        {
            Directory.SetLastWriteTimeUtc(directory, writeTimeUtc);
        }

        Directory.SetLastWriteTimeUtc(path, writeTimeUtc);
    }

    internal async Task SeedBookAsync(Guid bookId, string fileName)
    {
        await using var db = OpenDatabase();
        db.Books.Add(new EBookModel
        {
            Id = bookId,
            Title = "Imported Book",
            FileDetails = new FileInfoDetails { HasFile = true, FileName = fileName },
        });
        await db.SaveChangesAsync();
    }

    internal async Task<long> CountDerivedRowsAsync(string table)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Paths.LiveDatabase, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    internal async Task EnsureBookTextSchemaAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IBookTextIndex>().EnsureSchemaAsync();
    }

    internal string DerivedRebuildMarkerPath(Guid jobId) =>
        Path.Combine(Path.GetDirectoryName(Paths.Journal(jobId))!, "derived.rebuilt.json");

    internal string DerivedResetRecordPath(Guid jobId) =>
        Path.Combine(Path.GetDirectoryName(Paths.Journal(jobId))!, "derived.reset.json");

    /// <summary>Runs the real guarded retry transition; returns null on success.</summary>
    internal async Task<MigrationJobStoreException?> TryRetryAsync(Guid jobId)
    {
        await using var db = OpenDatabase();
        var store = new EfMigrationJobStore(db, Clock);
        try
        {
            await store.RetryAsync(jobId, new MigrationRetryRequest(), CancellationToken.None);
            return null;
        }
        catch (MigrationJobStoreException exception)
        {
            return exception;
        }
    }

    /// <summary>
    /// Marks a book as fully ingested with a preserved chunk, modelling progress
    /// made after the rebuild wiped the derived state.
    /// </summary>
    internal async Task MarkDerivedStateReadyAsync(Guid bookId)
    {
        var version = BookTextArtifactSchema.CurrentExtractorVersion;
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Paths.LiveDatabase, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO BookTextIngestionStates
                (BookId, Status, SourceFileName, Format, SourceSha256, ExtractorVersion,
                 ErrorCode, ErrorMessage, Attempts, ChunkCount, CharacterCount, UpdatedAtUtc)
            VALUES
                ($book, 'Ready', 'book.epub', 'Epub', $hash, $version,
                 NULL, NULL, 1, 1, 10, '2026-01-01T00:00:00.0000000+00:00')
            ON CONFLICT(BookId) DO UPDATE SET
                Status='Ready',
                SourceSha256=excluded.SourceSha256,
                ExtractorVersion=excluded.ExtractorVersion,
                ChunkCount=1;
            INSERT INTO BookTextChunks
                (Id, BookId, SourceSha256, ExtractorVersion, Format, Ordinal,
                 Text, HeadingPathJson, SourceSegmentsJson)
            VALUES
                ($chunk, $book, $hash, $version, 'Epub', 0, 'preserved text', '[]', '[]');
            """;
        command.Parameters.AddWithValue("$book", bookId.ToString("D"));
        command.Parameters.AddWithValue("$chunk", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$hash", new string('c', 64));
        command.Parameters.AddWithValue("$version", version);
        await command.ExecuteNonQueryAsync();
    }

    internal async Task<long> CountChunksForBookAsync(Guid bookId)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Paths.LiveDatabase, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM BookTextChunks WHERE BookId=$book;";
        command.Parameters.AddWithValue("$book", bookId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    internal async Task<string?> ReadDerivedStateStatusAsync(Guid bookId)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Paths.LiveDatabase, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM BookTextIngestionStates WHERE BookId=$book;";
        command.Parameters.AddWithValue("$book", bookId.ToString("D"));
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }

    internal SelfHostedRecoveryManifest BuildManifest(
        Guid jobId,
        DateTimeOffset createdAt,
        MigrationRecoveryStatus status = MigrationRecoveryStatus.Available,
        Guid? reservationId = null) =>
        new(
            jobId,
            Guid.NewGuid(),
            createdAt,
            SelfHostedRecoveryManifest.Expiry(createdAt),
            status,
            "revision-1",
            new MigrationExistingCounts(Books: 1),
            100,
            200,
            new string('a', 64),
            [new RecoveryMediaDescriptor(FirstBook, "book", ".epub", 100, new string('b', 64))],
            DatabaseSchemaVersion: "20260101000000_Initial",
            DatabaseMigrationCount: 1,
            DatabaseRetained: true,
            MediaRetained: true,
            RetentionReservationId: reservationId);

    internal void WriteManifestBytes(Guid jobId, byte[] bytes)
    {
        Paths.PrepareRecovery(jobId);
        File.WriteAllBytes(Paths.RecoveryManifest(jobId), bytes);
    }

    public void Dispose()
    {
        Services.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Records every strict scheduling attempt across rebuild passes.</summary>
internal sealed class DerivedSchedulerProbe
{
    internal List<Guid> Calls { get; } = [];
}

internal sealed class RecordingDerivedScheduler(
    IBookTextDerivedScheduler inner,
    DerivedSchedulerProbe probe) : IBookTextDerivedScheduler
{
    public Task<DerivedScheduleOutcome> ScheduleAsync(
        Guid bookId,
        string fileName,
        CancellationToken ct = default)
    {
        probe.Calls.Add(bookId);
        return inner.ScheduleAsync(bookId, fileName, ct);
    }
}

/// <summary>Derived-artifact storage whose cleanup can be made to fail for chosen books.</summary>
internal sealed class FailableDerivedArtifactStorage : IBookDerivedArtifactStorage
{
    internal HashSet<Guid> FailFor { get; } = [];
    internal List<Guid> Deleted { get; } = [];

    public Task WriteAsync(
        BookTextSourceRevision revision,
        Func<Stream, CancellationToken, Task> writer,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteBookArtifactsAsync(Guid bookId, CancellationToken ct = default)
    {
        Deleted.Add(bookId);
        if (FailFor.Contains(bookId))
        {
            throw new IOException("simulated derived-artifact cleanup failure");
        }

        return Task.CompletedTask;
    }

    public Task PruneBookArtifactsAsync(
        Guid bookId,
        BookTextSourceRevision keep,
        CancellationToken ct = default) => Task.CompletedTask;
}
