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
using Nostos.Backend.Services.Library;
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
    internal string RevisionToken { get; private set; } = string.Empty;
    internal Dictionary<string, List<string>> OriginalAllTables { get; private set; } = null!;
    internal string OriginalDatabaseSha256 { get; private set; } = string.Empty;
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

        OriginalAllTables = ActivationBuildFixture.DumpAllTables(liveDatabase);
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
        OriginalDatabaseSha256 = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(TemplateDatabase)));
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

        // The host state above may contain portable rows, so it advanced the
        // revision through the save pipeline. Stamp the fixture's authoritative
        // revision in its own save: a save that only changes LibraryState is not
        // a portable mutation, so the value persists (issue #679 Slice 11).
        state.StateVersion = OriginalRevision;
        state.UpdatedAt = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);
        db.SaveChanges();

        // The job's stored destination revision is the same opaque provider token
        // preflight hands to the client; activation reads the current token only
        // through the provider.
        RevisionToken = new LibraryStateDestinationRevisionProvider(db)
            .GetCurrentAsync(default).GetAwaiter().GetResult();

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
            DestinationRevision = RevisionToken,
            PreparedStagingId = Prepared.Metadata.StagingId.Value,
            PreparedImportMetadataJson = "{}",
            ReservationId = ReservationId,
            ReservedStorageBytes = Prepared.Metadata.ArchiveBytes + Prepared.Metadata.MediaBytes + 1_000_000,
            Version = 3,
        });
        db.SaveChanges();

        SeedUnrelatedOperationalRows(db);
        db.SaveChanges();
    }

    /// <summary>
    /// Seeds unrelated migration jobs with their own session, chunk receipt,
    /// artifact and reservation rows. The restored-original comparison must
    /// keep every one of these byte-identical, so the row-scoped exclusion for
    /// the activating job is genuinely proven.
    /// </summary>
    private static void SeedUnrelatedOperationalRows(NostosDbContext db)
    {
        var pendingExport = new MigrationJobRecord
        {
            Id = Guid.NewGuid(),
            Direction = (int)MigrationDirection.Export,
            State = (int)MigrationJobState.Pending,
            RecoveryStatus = (int)MigrationRecoveryStatus.NotRequired,
            CreatedAtUtc = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc),
            IdempotencyKey = "unrelated-pending-export",
            CreationPayloadHash = new string('b', 64),
            ExpiresAtUtc = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
            AttemptNumber = 1,
            Version = 2,
        };
        var completedExport = new MigrationJobRecord
        {
            Id = Guid.NewGuid(),
            Direction = (int)MigrationDirection.Export,
            State = (int)MigrationJobState.Completed,
            RecoveryStatus = (int)MigrationRecoveryStatus.NotRequired,
            CreatedAtUtc = new DateTime(2026, 10, 1, 7, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 10, 1, 7, 45, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 10, 1, 7, 45, 0, DateTimeKind.Utc),
            IdempotencyKey = "unrelated-completed-export",
            CreationPayloadHash = new string('c', 64),
            ExpiresAtUtc = new DateTime(2026, 10, 11, 7, 0, 0, DateTimeKind.Utc),
            AttemptNumber = 1,
            Version = 5,
        };
        db.MigrationJobRecords.AddRange(pendingExport, completedExport);

        var session = new MigrationSessionRecord
        {
            Id = Guid.NewGuid(),
            JobId = pendingExport.Id,
            Purpose = (int)MigrationSessionPurpose.Export,
            State = (int)MigrationSessionState.Complete,
            TotalBytes = 2048,
            ChunkSize = MigrationContractLimits.DefaultChunkBytes,
            TotalChunks = 1,
            FileIdentitySizeBytes = 2048,
            FileIdentitySha256 = new string('d', 64),
            ClientFingerprint = "unrelated-client",
            IdempotencyKey = "unrelated-session",
            CreationPayloadHash = new string('e', 64),
            ReceivedBytes = 2048,
            CreatedAtUtc = new DateTime(2026, 10, 2, 9, 1, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 10, 2, 9, 2, 0, DateTimeKind.Utc),
            ExpiresAtUtc = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 10, 2, 9, 2, 0, DateTimeKind.Utc),
            StorageKey = "migration/sessions/unrelated-session",
            Version = 1,
        };
        db.MigrationSessionRecords.Add(session);
        db.MigrationChunkReceiptRecords.Add(new MigrationChunkReceiptRecord
        {
            SessionId = session.Id,
            ChunkIndex = 0,
            OffsetBytes = 0,
            LengthBytes = 2048,
            Sha256 = new string('f', 64),
            ReceivedAtUtc = new DateTime(2026, 10, 2, 9, 2, 0, DateTimeKind.Utc),
        });
        db.MigrationStorageReservations.Add(new MigrationStorageReservationRecord
        {
            Id = Guid.NewGuid(),
            Purpose = (int)MigrationSessionPurpose.Export,
            ReservedBytes = 4096,
            MaterializedBytes = 2048,
            CreatedAtUtc = new DateTime(2026, 10, 2, 9, 0, 30, DateTimeKind.Utc),
            ExpiresAtUtc = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
            ClaimedJobId = pendingExport.Id,
            Version = 1,
        });
        db.MigrationExportArtifactRecords.Add(new MigrationExportArtifactRecord
        {
            JobId = completedExport.Id,
            State = (int)MigrationExportArtifactState.Available,
            StorageKey = "migration/exports/unrelated.nostos",
            FileName = "unrelated.nostos",
            ContentType = "application/zip",
            SizeBytes = 8192,
            Sha256 = new string('1', 64),
            CreatedAtUtc = new DateTime(2026, 10, 1, 7, 1, 0, DateTimeKind.Utc),
            AvailableAtUtc = new DateTime(2026, 10, 1, 7, 2, 0, DateTimeKind.Utc),
            ExpiresAtUtc = new DateTime(2026, 10, 8, 7, 0, 0, DateTimeKind.Utc),
            Version = 1,
        });
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
    internal string RevisionToken => _template.RevisionToken;
    internal Dictionary<string, List<string>> OriginalAllTables => _template.OriginalAllTables;
    internal Dictionary<string, List<string>> OriginalPortable => _template.OriginalPortable;
    internal Dictionary<string, string> OriginalMedia => _template.OriginalMedia;
    internal Dictionary<string, string> ExpectedImportedMedia => _template.ExpectedImportedMedia;
    internal string OriginalDatabaseSha256 => _template.OriginalDatabaseSha256;
    internal string TransferRoot { get; }
    internal string StagingRoot => _ownStagingRoot ?? _template.StagingRoot;

    /// <summary>Test seam: overrides the current destination revision token.</summary>
    internal Func<CancellationToken, Task<string>>? RevisionOverride { get; set; }

    /// <summary>Test seam: throws during the media component's rollback rename.</summary>
    internal Action? MediaRecoveryRenameFailure { get; set; }

    /// <summary>Test seam: throws during the database component's rollback rename.</summary>
    internal Action? DatabaseRecoveryRenameFailure { get; set; }

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

        BuildMaintenance();
        BuildHost();
    }

    private void BuildMaintenance()
    {
        // Production always constructs the coordinator with the durable marker,
        // so a sticky fail-closed state can survive a runtime rollback failure.
        var marker = new LibraryMaintenanceMarker(Paths.LiveDatabase);
        Maintenance = new LibraryMaintenanceCoordinator(
            new LibraryMaintenanceOptions { DrainTimeout = _drainTimeout }, clock: Clock, marker: marker);
        Maintenance.InitializeAfterRecovery();
        Journals = new SelfHostedActivationJournalStore(Paths, Maintenance);
        Manifests = new SelfHostedRecoveryManifestStore(Paths);
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
            new SelfHostedActivationComponentStep(Paths, database: false,
                afterRenameForTesting: () => MediaRecoveryRenameFailure?.Invoke()));
        services.AddSingleton<ISelfHostedActivationRecoveryStep>(
            new SelfHostedActivationComponentStep(Paths, database: true,
                afterRenameForTesting: () => DatabaseRecoveryRenameFailure?.Invoke()));
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
        services.AddScoped<ILibraryDestinationRevisionProvider>(sp =>
            new SwitchableRevisionProvider(
                new LibraryStateDestinationRevisionProvider(sp.GetRequiredService<NostosDbContext>()),
                this));
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
            new MigrationActivateRequest(RevisionToken, confirm),
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
        BuildMaintenance();
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

    /// <summary>
    /// Compares the restored original generation with the pre-activation logical
    /// dump: every table, every row and every column, plus the media SHA-256 and
    /// path set. Exactly ONE row is excluded by key: the tested activation job's
    /// own <c>MigrationJobRecords</c> row, whose State/lease/UpdatedAt/Version
    /// the protocol legitimately changes (callers assert its expected state
    /// explicitly). Every other row must be identical, including the unrelated
    /// migration jobs, sessions, chunk receipts, artifacts and reservations the
    /// fixture seeds. There is no whole-table exclusion. No SQLite-file hash is
    /// asserted: the job's own lease/state writes mean the main database file is
    /// not untouched.
    /// </summary>
    internal void AssertOriginalGeneration()
    {
        var actual = ActivationBuildFixture.DumpAllTables(Paths.LiveDatabase);
        var expected = OriginalAllTables;
        actual.Keys.Should().BeEquivalentTo(expected.Keys, "no table may appear or disappear");

        var expectedJobs = expected["MigrationJobRecords"];
        var actualJobs = actual["MigrationJobRecords"];
        expectedJobs.Count(IsActivationJobRow).Should().Be(1, "the fixture seeds exactly one activation job");
        actualJobs.Count(IsActivationJobRow).Should().Be(1, "the activation job row must still exist");
        (expectedJobs.Count - 1).Should().BeGreaterThan(
            0, "the fixture must seed at least one unrelated migration job for the row-scoped exclusion to be meaningful");

        foreach (var table in expected.Keys)
        {
            var expectedRows = table == "MigrationJobRecords"
                ? expectedJobs.Where(row => !IsActivationJobRow(row)).ToList()
                : expected[table];
            var actualRows = table == "MigrationJobRecords"
                ? actualJobs.Where(row => !IsActivationJobRow(row)).ToList()
                : actual[table];
            actualRows.Should().BeEquivalentTo(expectedRows,
                $"table {table} must be logically identical to the original except the activation job's own row");
        }

        ActivationBuildFixture.MediaSnapshot(Paths.LiveMedia)
            .Should().BeEquivalentTo(OriginalMedia, "the media root must be identical to the original");
    }

    private bool IsActivationJobRow(string row) =>
        string.Equals(row.Split('|', 2)[0], JobId.ToString(), StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Tampering seam: corrupts the SQLite database bytes so integrity checks fail.</summary>
    internal static void CorruptDatabaseByte(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xFF; // the "SQLite format 3" magic: any open or integrity check fails
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>SHA-256 of every file under the bed root, for freeze assertions.</summary>
    internal Dictionary<string, string> SnapshotActivationTree() =>
        Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(Root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    /// <summary>
    /// Adds structurally valid but database-unindexed media (a GUID folder with a
    /// book file) to the live root, modelling orphan user files.
    /// </summary>
    internal void WriteOrphanMedia()
    {
        var folder = Path.Combine(Paths.LiveMedia, Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "book.epub"), "ORPHAN-USER-MEDIA");
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

    /// <summary>Delegates to the real provider unless a test overrides the token.</summary>
    private sealed class SwitchableRevisionProvider(
        ILibraryDestinationRevisionProvider inner,
        ActivationCoordinatorTestBed bed) : ILibraryDestinationRevisionProvider
    {
        public Task<string> GetCurrentAsync(CancellationToken ct) =>
            bed.RevisionOverride is { } overridden
                ? overridden(ct)
                : inner.GetCurrentAsync(ct);
    }
}
