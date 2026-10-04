using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Immutable, process-wide activation source shared by every test bed copy: a
/// migrated host database seeded with the activation job and a real prepared
/// import staged on disk. Building it once keeps the crash matrix (one bed per
/// boundary) and the 10x loop cheap; every bed copies the database bytes so
/// cases cannot contaminate each other.
/// </summary>
internal sealed class ActivationCoordinatorTemplate : IDisposable
{
    internal const string OriginalRevision = "41";
    internal const long AdvancedRevision = 42;

    private static readonly Lazy<ActivationCoordinatorTemplate> PopulatedTemplate =
        new(() => Create(populated: true), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ActivationCoordinatorTemplate> EmptyTemplate =
        new(() => Create(populated: false), LazyThreadSafetyMode.ExecutionAndPublication);

    static ActivationCoordinatorTemplate()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (PopulatedTemplate.IsValueCreated) PopulatedTemplate.Value.Dispose();
            if (EmptyTemplate.IsValueCreated) EmptyTemplate.Value.Dispose();
        };
    }

    internal static ActivationCoordinatorTemplate For(bool populated) =>
        populated ? PopulatedTemplate.Value : EmptyTemplate.Value;

    internal string Root { get; }
    internal bool Populated { get; }
    internal string ArchivePath { get; }
    internal Guid JobId { get; } = Guid.Parse("6a3490c2d6fd4a63a2f9b67ad56b31c1");
    internal Guid ReservationId { get; private set; }
    internal string TemplateDatabase { get; }
    internal string TemplateMedia { get; }
    internal string StagingRoot { get; }
    internal LocalPortableImportStaging Staging { get; }
    internal IPreparedPortableImport Prepared { get; private set; } = null!;
    internal PortablePreparedImportVerification ExpectedHandle { get; private set; } = null!;
    internal PortableFixtureIds SourceIds { get; private set; } = null!;
    internal Dictionary<string, List<string>> OriginalPortable { get; private set; } = null!;
    internal Dictionary<string, string> OriginalMedia { get; private set; } = null!;
    internal Dictionary<string, string> ExpectedImportedMedia { get; private set; } = null!;
    internal DateTimeOffset BaseTime { get; } = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    internal RecoveryClock Clock { get; } = new(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));

    private ActivationCoordinatorTemplate(bool populated)
    {
        Populated = populated;
        Root = Path.Combine(Path.GetTempPath(), $"nostos-681-template-{Guid.NewGuid():N}");
        ArchivePath = Path.Combine(Root, "source.nostos");
        TemplateDatabase = Path.Combine(Root, "template.db");
        TemplateMedia = Path.Combine(Root, "template-media");
        StagingRoot = Path.Combine(Root, "staging");
        Staging = new LocalPortableImportStaging(StagingRoot);
        Directory.CreateDirectory(Path.Combine(Root, "db-volume"));
        Directory.CreateDirectory(TemplateMedia);
    }

    private static ActivationCoordinatorTemplate Create(bool populated)
    {
        var template = new ActivationCoordinatorTemplate(populated);
        template.Initialize();
        return template;
    }

    private void Initialize()
    {
        var liveDatabase = Path.Combine(Root, "db-volume", "live.db");
        ActivationBuildFixture.BootstrapAsync(liveDatabase).GetAwaiter().GetResult();
        if (Populated)
        {
            CreateLiveMedia();
        }

        var source = LocalPortableTestLibrary.CreateAsync().GetAwaiter().GetResult();
        try
        {
            SourceIds = PortableArchiveTestSupport.PopulateRepresentativeAsync(source.Db, source.Storage)
                .GetAwaiter().GetResult();
            var archive = SelfHostedActivationTestSupport.ExportArchiveAsync(source).GetAwaiter().GetResult();
            File.WriteAllBytes(ArchivePath, archive);
            var archiveSource = new FilePortableArchiveSource(ArchivePath);
            try
            {
                Prepared = new PortableArchiveReader()
                    .PrepareImportAsync(archiveSource, Staging).GetAwaiter().GetResult();
            }
            finally
            {
                archiveSource.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        ExpectedHandle = new PortableLibraryVerifier()
            .VerifyPreparedImportAsync(Staging, Prepared, default).GetAwaiter().GetResult();
        ExpectedHandle.Passed.Should().BeTrue();
        ExpectedImportedMedia = ReadStagedMediaSnapshotAsync().GetAwaiter().GetResult();

        SeedHostState(liveDatabase);

        OriginalPortable = DumpPortable(liveDatabase);
        OriginalMedia = ActivationBuildFixture.MediaSnapshot(TemplateMedia);

        // Publish a self-contained template database; the live file may carry a
        // WAL while the source context is open.
        using (var connection = SelfHostedSqliteFile.Open(liveDatabase, readOnly: false))
        {
            SelfHostedSqliteFile.CheckpointTruncate(connection);
        }

        SelfHostedSqliteFile.ClearPools();
        File.Copy(liveDatabase, TemplateDatabase, overwrite: true);
        File.Delete(liveDatabase);
        File.Delete(liveDatabase + "-wal");
        File.Delete(liveDatabase + "-shm");
    }

    private void CreateLiveMedia()
    {
        var bookId = Guid.NewGuid();
        var folder = Path.Combine(TemplateMedia, bookId.ToString());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "book.epub"), "LIVE-ONLY-BOOK");
        File.WriteAllText(Path.Combine(folder, "cover.jpg"), "LIVE-ONLY-COVER");
        File.WriteAllText(Path.Combine(folder, "cover-thumb-96.webp"), "LIVE-ONLY-THUMB");
    }

    private void SeedHostState(string liveDatabase)
    {
        using var db = new NostosDbContext(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={liveDatabase};Pooling=False")
            .Options);
        var state = db.LibraryStates.Single();
        state.StateVersion = OriginalRevision;
        state.UpdatedAt = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
        db.BackupRecords.Add(new BackupRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc),
            SizeBytes = 4096,
            Status = BackupStatus.Completed,
            Provider = BackupProvider.Local,
            LocalArchivePath = "Storage/backups/live.nostos",
            ManifestJson = "{\"version\":1}",
            IncludeBookFiles = true,
        });
        if (Populated)
        {
            db.Collections.Add(new CollectionModel
            {
                Id = Guid.NewGuid(),
                Name = "LIVE-ONLY-COLLECTION",
            });
        }

        db.SaveChanges();

        var options = new TransferStorageOptions { DiskSafetyMarginBytes = 0, DiskSafetyMarginPercent = 0 };
        var volume = new FakeTransferVolume
        {
            AvailableFreeSpaceBytes = 20L * 1024 * 1024 * 1024,
            TotalSizeBytes = 20L * 1024 * 1024 * 1024,
        };
        var capacity = new TransferStorageCapacity(db, volume, Options.Create(options), Clock);
        var reserved = capacity.TryReserveAsync(
            Prepared.Metadata.ArchiveBytes + Prepared.Metadata.MediaBytes + 1_000_000,
            MigrationSessionPurpose.Import,
            TimeSpan.FromHours(24),
            default).GetAwaiter().GetResult();
        reserved.IsAdmitted.Should().BeTrue();
        ReservationId = reserved.ReservationId!.Value;
        capacity.ClaimAsync(ReservationId, JobId, default).GetAwaiter().GetResult();

        db.MigrationJobRecords.Add(new MigrationJobRecord
        {
            Id = JobId,
            Direction = (int)MigrationDirection.Import,
            State = (int)MigrationJobState.ReadyToActivate,
            RecoveryStatus = (int)MigrationRecoveryStatus.Pending,
            ProgressPhase = (int)MigrationProgressPhase.PreparingActivation,
            CreatedAtUtc = new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 10, 3, 8, 30, 0, DateTimeKind.Utc),
            IdempotencyKey = "activation-coordinator-job",
            CreationPayloadHash = new string('a', 64),
            ExpiresAtUtc = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc),
            AttemptNumber = 1,
            DestinationRevision = OriginalRevision,
            PreparedStagingId = Prepared.Metadata.StagingId.Value,
            PreparedImportMetadataJson = "{}",
            ReservationId = ReservationId,
            ReservedStorageBytes = Prepared.Metadata.ArchiveBytes + Prepared.Metadata.MediaBytes + 1_000_000,
            Version = 3,
        });
        db.SaveChanges();
    }

    private async Task<Dictionary<string, string>> ReadStagedMediaSnapshotAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in Prepared.Media)
        {
            var descriptor = item.Descriptor;
            var extension = Path.GetExtension(descriptor.FileName).ToLowerInvariant();
            var relative = Path.Combine(descriptor.BookId.ToString(), descriptor.Kind + extension);
            await using var stream = await Staging.OpenMediaReadAsync(
                Prepared.Metadata.StagingId, item.Reference, default);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            result[relative] = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
        }

        return result;
    }

    internal static Dictionary<string, List<string>> DumpPortable(string path)
    {
        var all = ActivationBuildFixture.DumpAllTables(path);
        return ActivationCoordinatorTestBed.PortableTableNames.ToDictionary(name => name, name => all[name]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// One isolated host copy: real live database bytes copied from the template, a
/// real live media root, real DI services, and a fresh maintenance generation
/// over the identical files.
/// </summary>
internal sealed class ActivationCoordinatorTestBed : IAsyncDisposable
{
    private readonly ActivationCoordinatorTemplate _template;
    private readonly TimeSpan _drainTimeout;
    private readonly List<ServiceProvider> _providers = [];

    internal string Root { get; }
    internal Guid JobId => _template.JobId;
    internal bool Populated => _template.Populated;
    internal SelfHostedActivationPaths Paths { get; }
    internal RecoveryClock Clock { get; }
    internal TransferStorageOptions StorageOptions { get; } = new()
    {
        DiskSafetyMarginBytes = 0,
        DiskSafetyMarginPercent = 0,
    };

    internal FakeTransferVolume TransferVolume { get; } = new()
    {
        AvailableFreeSpaceBytes = 20L * 1024 * 1024 * 1024,
        TotalSizeBytes = 20L * 1024 * 1024 * 1024,
    };

    internal FakeVolumeSpaceProbe Probe { get; } = new();
    internal IPreparedPortableImport Prepared => _prepared ?? _template.Prepared;
    internal PortablePreparedImportVerification ExpectedHandle => _expected ?? _template.ExpectedHandle;
    internal PortableFixtureIds SourceIds => _template.SourceIds;
    internal Guid ReservationId => _template.ReservationId;
    internal Dictionary<string, List<string>> OriginalPortable => _template.OriginalPortable;
    internal Dictionary<string, string> OriginalMedia => _template.OriginalMedia;
    internal Dictionary<string, string> ExpectedImportedMedia => _template.ExpectedImportedMedia;
    internal string TransferRoot { get; }
    internal string StagingRoot => _ownStagingRoot ?? _template.StagingRoot;

    private IPreparedPortableImport? _prepared;
    private PortablePreparedImportVerification? _expected;
    private LocalPortableImportStaging? _ownStaging;
    private string? _ownStagingRoot;

    internal LibraryMaintenanceCoordinator Maintenance { get; private set; } = null!;
    internal SelfHostedActivationJournalStore Journals { get; private set; } = null!;
    internal SelfHostedRecoveryManifestStore Manifests { get; private set; } = null!;
    internal ServiceProvider Host { get; private set; } = null!;

    private ActivationCoordinatorTestBed(ActivationCoordinatorTemplate template, TimeSpan drainTimeout)
    {
        _template = template;
        _drainTimeout = drainTimeout;
        Clock = new RecoveryClock(template.BaseTime);
        Root = Path.Combine(Path.GetTempPath(), $"nostos-681-activation-{Guid.NewGuid():N}");
        TransferRoot = Path.Combine(Root, "transfer-volume", "transfers");
        Directory.CreateDirectory(Path.Combine(Root, "db-volume"));
        Directory.CreateDirectory(Path.Combine(Root, "media-volume"));
        Directory.CreateDirectory(TransferRoot);
        Paths = new SelfHostedActivationPaths(
            Path.Combine(Root, "db-volume", "nostos.db"),
            Path.Combine(Root, "media-volume", "library"));
    }

    internal static async Task<ActivationCoordinatorTestBed> CreateAsync(
        bool populated,
        TimeSpan? drainTimeout = null,
        bool freshStaging = false)
    {
        var bed = new ActivationCoordinatorTestBed(
            ActivationCoordinatorTemplate.For(populated),
            drainTimeout ?? TimeSpan.FromSeconds(5));
        await bed.InitializeAsync(freshStaging);
        return bed;
    }

    private async Task InitializeAsync(bool freshStaging)
    {
        File.Copy(_template.TemplateDatabase, Paths.LiveDatabase, overwrite: true);
        CopyDirectory(_template.TemplateMedia, Paths.LiveMedia);
        if (freshStaging)
        {
            // The corruption tests need a staging area they may damage without
            // invalidating the process-wide template every other test shares.
            _ownStagingRoot = Path.Combine(Root, "staging");
            _ownStaging = new LocalPortableImportStaging(_ownStagingRoot);
            var archiveSource = new FilePortableArchiveSource(_template.ArchivePath);
            try
            {
                _prepared = await new PortableArchiveReader().PrepareImportAsync(archiveSource, _ownStaging);
            }
            finally
            {
                await archiveSource.DisposeAsync();
            }

            _expected = await new PortableLibraryVerifier()
                .VerifyPreparedImportAsync(_ownStaging, _prepared, default);
            _expected.Passed.Should().BeTrue();
        }

        Probe.Set(Paths.LiveDatabase, 10L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024, "db");
        Probe.Set(Paths.LiveMedia, 10L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024, "media");
        Probe.Set(TransferRoot, 10L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024, "transfer");
        await RestartHostAsync();
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    internal NostosDbContext OpenDatabase() =>
        new(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={Paths.LiveDatabase};Pooling=False")
            .Options);

    /// <summary>Rebuilds the host services as a fresh process over the same files.</summary>
    internal async Task RestartHostAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
            _providers.Remove(Host);
        }

        Maintenance = new LibraryMaintenanceCoordinator(
            new LibraryMaintenanceOptions { DrainTimeout = _drainTimeout }, clock: Clock);
        Journals = new SelfHostedActivationJournalStore(Paths, Maintenance);
        Manifests = new SelfHostedRecoveryManifestStore(Paths);
        BuildHost();
    }

    private void BuildHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Clock);
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(Maintenance);
        services.AddSingleton<ILibraryMaintenanceCoordinator>(Maintenance);
        services.AddSingleton<IMigrationMaintenanceGate, MigrationMaintenanceGate>();
        services.AddDbContextFactory<NostosDbContext>(options =>
            options.UseSqlite($"Data Source={Paths.LiveDatabase};Pooling=False"));
        services.AddSingleton(Options.Create(StorageOptions));
        services.AddSingleton<ITransferVolume>(TransferVolume);
        services.AddScoped<ITransferStorageCapacity, TransferStorageCapacity>();
        services.AddSingleton(new TransferPathResolver(TransferRoot));
        services.AddSingleton<IPortableImportStaging>(_ownStaging is null
            ? _template.Staging
            : _ownStaging);
        services.AddSingleton(Paths);
        services.AddSingleton(Journals);
        services.AddSingleton(Manifests);
        services.AddSingleton<ISelfHostedActivationRecoveryStep>(
            new SelfHostedActivationComponentStep(Paths, database: false));
        services.AddSingleton<ISelfHostedActivationRecoveryStep>(
            new SelfHostedActivationComponentStep(Paths, database: true));
        services.AddSingleton<ISelfHostedActivationRecoveryStep>(
            new SelfHostedActivationEmptyRetentionStep(Paths, Manifests));
        services.AddSingleton<SelfHostedActivationRecoveryStartupService>();
        services.AddSingleton<ISelfHostedVolumeSpaceProbe>(Probe);
        services.AddScoped<SelfHostedActivationCapacity>();
        services.AddScoped<ISelfHostedActivationCandidateMediaBuilder>(sp =>
            new SelfHostedActivationCandidateMediaBuilder(
                Paths, sp.GetRequiredService<IPortableImportStaging>()));
        services.AddScoped<ISelfHostedActivationDatabaseBuilder>(sp =>
            new SelfHostedActivationDatabaseBuilder(
                Paths, sp.GetRequiredService<IPortableImportStaging>(), Maintenance, Clock));
        services.AddSingleton<ISelfHostedSqliteLifecycle, SelfHostedSqliteLifecycle>();
        services.AddSingleton<IPortableLibraryVerifier, PortableLibraryVerifier>();
        services.AddSingleton<IBookAssetStorage>(new FileStorageService(
            new PortableTestWebHostEnvironment(Root),
            Options.Create(new FileStorageOptions { BooksRoot = Paths.LiveMedia }),
            NullLogger<FileStorageService>.Instance));
        services.AddScoped(sp => new SelfHostedMigrationRecoveryService(
            Paths,
            Manifests,
            Journals,
            sp.GetRequiredService<SelfHostedActivationCapacity>(),
            sp.GetRequiredService<ITransferStorageCapacity>(),
            sp.GetRequiredService<NostosDbContext>(),
            Maintenance,
            sp.GetRequiredService<IOptions<TransferStorageOptions>>(),
            Clock));
        services.AddScoped<ISelfHostedRecoverySnapshots>(sp =>
            sp.GetRequiredService<SelfHostedMigrationRecoveryService>());
        services.AddSingleton<MigrationFileMutex>();
        services.AddScoped<EfMigrationJobStore>();
        services.AddScoped<IMigrationJobStore, MigrationEngineJobStore>();
        services.AddScoped<SelfHostedActivationCoordinator>();
        services.AddScoped<IMigrationActivationService>(sp =>
            sp.GetRequiredService<SelfHostedActivationCoordinator>());

        Host = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = false,
            ValidateOnBuild = false,
        });
        _providers.Add(Host);
    }

    internal async Task<SelfHostedActivationResult> ActivateAsync(
        bool confirm = true,
        Action<string>? observer = null,
        CancellationToken ct = default)
    {
        await using var scope = Host.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<SelfHostedActivationCoordinator>();
        coordinator.StepObserverForTesting = observer;
        return await coordinator.ActivateAsync(
            JobId,
            new MigrationActivateRequest(ActivationCoordinatorTemplate.OriginalRevision, confirm),
            ct);
    }

    /// <summary>The production interface entry point, with no explicit request object.</summary>
    internal async Task ActivatePublicAsync(CancellationToken ct = default)
    {
        await using var scope = Host.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMigrationActivationService>().ActivateAsync(JobId, ct);
    }

    /// <summary>Runs the production startup reconciler with a fresh host generation.</summary>
    internal Task ReconcileAsync() => ReconcileWithStepsAsync(mediaStep: true, databaseStep: true, emptyDiscardStep: true);

    /// <summary>
    /// Runs the reconciler with a chosen subset of steps. Used by the
    /// crash-matrix sensitivity checks that prove each component step is what
    /// prevents a mixed generation from being admitted.
    /// </summary>
    internal async Task ReconcileWithStepsAsync(bool mediaStep, bool databaseStep, bool emptyDiscardStep)
    {
        var marker = new LibraryMaintenanceMarker(Paths.LiveDatabase);
        var gate = new LibraryMaintenanceCoordinator(marker: marker);
        var journals = new SelfHostedActivationJournalStore(Paths, gate);
        var manifests = new SelfHostedRecoveryManifestStore(Paths);
        var steps = new List<ISelfHostedActivationRecoveryStep>();
        if (mediaStep) steps.Add(new SelfHostedActivationComponentStep(Paths, database: false));
        if (databaseStep) steps.Add(new SelfHostedActivationComponentStep(Paths, database: true));
        if (emptyDiscardStep) steps.Add(new SelfHostedActivationEmptyRetentionStep(Paths, manifests));
        var engine = new SelfHostedActivationRecoveryStartupService(Paths, journals, gate, steps);
        await engine.ReconcileIncompleteAsync();
        gate.IsMaintenanceActive.Should().BeFalse();
        File.Exists(marker.MarkerPath).Should().BeFalse();
    }

    /// <summary>Disposes the crashed host, reconciles, and builds a fresh host.</summary>
    internal async Task RecoverHostAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
            _providers.Remove(Host);
        }

        await ReconcileAsync();
        Maintenance = new LibraryMaintenanceCoordinator(
            new LibraryMaintenanceOptions { DrainTimeout = _drainTimeout }, clock: Clock);
        Journals = new SelfHostedActivationJournalStore(Paths, Maintenance);
        Manifests = new SelfHostedRecoveryManifestStore(Paths);
        BuildHost();
    }

    internal static IReadOnlyList<string> PortableTableNames { get; } = BuildPortableTableNames();

    private static string[] BuildPortableTableNames()
    {
        using var db = new NostosDbContext(new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        var types = new[]
        {
            typeof(WorkModel), typeof(BookModel), typeof(CollectionModel), typeof(BookCollectionModel),
            typeof(NoteModel), typeof(TopicModel), typeof(NoteTopicModel), typeof(WritingModel),
            typeof(WritingNoteModel), typeof(BookAcquisitionModel), typeof(NoteImportBookLink),
            typeof(AssistantSettingsModel),
        };
        return types.Select(type => db.Model.FindEntityType(type)!.GetTableName()!).ToArray();
    }

    internal void AssertOriginalGeneration()
    {
        ActivationCoordinatorTemplate.DumpPortable(Paths.LiveDatabase)
            .Should().BeEquivalentTo(OriginalPortable, "the portable library must be byte-identical to the original");
        ActivationBuildFixture.MediaSnapshot(Paths.LiveMedia)
            .Should().BeEquivalentTo(OriginalMedia, "the media root must be byte-identical to the original");
    }

    internal async Task AssertImportedGenerationAsync()
    {
        await using var scope = Host.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var report = await new PortableLibraryVerifier()
            .VerifyCandidateAsync(db, Paths.LiveMedia, Prepared, ExpectedHandle, default);
        report.Passed.Should().BeTrue(
            "the activated generation must be exactly the verified import: {0}",
            string.Join(", ", report.Failures.Select(failure => failure.Code)));
        ActivationBuildFixture.MediaSnapshot(Paths.LiveMedia)
            .Should().BeEquivalentTo(ExpectedImportedMedia, "only the imported primary media may remain active");
    }

    internal async Task<MigrationJobRecord> ReadJobAsync()
    {
        await using var scope = Host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NostosDbContext>()
            .MigrationJobRecords.AsNoTracking().SingleAsync(job => job.Id == JobId);
    }

    internal async Task<long> CurrentRevisionAsync()
    {
        await using var scope = Host.CreateAsyncScope();
        var value = await scope.ServiceProvider.GetRequiredService<NostosDbContext>()
            .LibraryStates.AsNoTracking().Select(state => state.StateVersion).SingleAsync();
        return long.Parse(value);
    }

    internal async Task WritePortableRevisionBumpAsync()
    {
        await using var db = OpenDatabase();
        db.Collections.Add(new CollectionModel { Id = Guid.NewGuid(), Name = "WRITE-DURING-ACTIVATION" });
        var state = await db.LibraryStates.SingleAsync();
        state.StateVersion = (long.Parse(state.StateVersion) + 1).ToString();
        await db.SaveChangesAsync();
    }

    internal async Task WriteHostRecordAsync()
    {
        await using var db = OpenDatabase();
        db.BackupRecords.Add(new BackupRecord
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            SizeBytes = 128,
            Status = BackupStatus.Completed,
            Provider = BackupProvider.Local,
            LocalArchivePath = "Storage/backups/during-activation.nostos",
            ManifestJson = "{}",
            IncludeBookFiles = true,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Tampering seam: flips one byte of the first file under a candidate root.</summary>
    internal static void CorruptFirstFile(string root)
    {
        var file = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).First();
        var bytes = File.ReadAllBytes(file);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(file, bytes);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers.ToArray())
        {
            try
            {
                await provider.DisposeAsync();
            }
            catch
            {
                // Test cleanup only.
            }
        }

        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
