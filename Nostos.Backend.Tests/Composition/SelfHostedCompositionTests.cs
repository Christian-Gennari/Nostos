using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nostos.Backend.Composition;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Services.Library.Imports;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Product.BookText;
using Nostos.Product.Composition;
using Nostos.Product.Services.Ai;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Composition;

public sealed class SelfHostedCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nostos-composition-{Guid.NewGuid():N}");

    public SelfHostedCompositionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    [Fact]
    public void StoragePaths_Resolve_computes_canonical_paths_and_rejects_unsafe_calibre_root()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PersistenceRegistration.DatabasePathConfigurationKey] = Path.Combine("data", "custom.db"),
            ["Storage:BooksRoot"] = Path.Combine("data", "books"),
            ["Storage:TransferPath"] = Path.Combine("data", "transfers"),
        }).Build();

        var resolved = SelfHostedStoragePaths.Resolve(config, _root);

        resolved.ContentRootPath.Should().Be(Path.GetFullPath(_root));
        resolved.DatabasePath.Should().Be(Path.GetFullPath(Path.Combine(_root, "data", "custom.db")));
        resolved.BooksRoot.Should().Be(Path.GetFullPath(Path.Combine(_root, "data", "books")));
        resolved.TransferRoot.Should().Be(Path.GetFullPath(Path.Combine(_root, "data", "transfers")));
        resolved.CalibreUploadRoot.Should().Be(Path.GetFullPath(Path.Combine(_root, "data", "transfers", "calibre-uploads")));

        var unsafeConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CalibreUpload:RootPath"] = _root,
        }).Build();

        Action unsafeAct = () => SelfHostedStoragePaths.Resolve(unsafeConfig, _root);
        unsafeAct.Should().Throw<InvalidOperationException>()
            .WithMessage("*CalibreUpload:RootPath*");
    }

    [Fact]
    public void BackupSettingsProvider_uses_injected_SelfHostedStoragePaths_without_configuration()
    {
        var dbDir = Path.Combine(_root, "volume");
        var storagePaths = new SelfHostedStoragePaths(
            _root,
            Path.Combine(dbDir, "nostos.db"),
            Path.Combine(dbDir, "books"),
            Path.Combine(dbDir, "transfers"),
            Path.Combine(dbDir, "transfers", "calibre-uploads"));

        var provider = new BackupSettingsProvider(
            new TestEnv { ContentRootPath = _root },
            Options.Create(new BackupSettings { IsEnabled = true, IntervalHours = 24 }),
            maintenance: null,
            configuration: null,
            storagePaths: storagePaths);

        provider.Update(s => s.IntervalHours = 12);

        File.Exists(Path.Combine(dbDir, "backup-settings.json")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "backup-settings.json")).Should().BeFalse();
    }

    [Fact]
    public void Host_composition_preserves_pre_and_post_product_DI_invariants_and_single_path_resolution()
    {
        var dbPath = Path.Combine(_root, "db", "nostos.db");
        var booksRoot = Path.Combine(_root, "media", "books");
        var transferRoot = Path.Combine(_root, "media", "transfers");
        var calibreUploadRoot = Path.Combine(_root, "media", "calibre-uploads");

        using var factory = new CompositionHostFactory(
            _root,
            dbPath,
            booksRoot,
            transferRoot,
            calibreUploadRoot);

        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // Pre-AddNostosProduct overrides must win over product TryAdd no-op defaults.
        sp.GetRequiredService<IAcquisitionWorkingRootProvider>()
            .Should().BeOfType<SelfHostedAcquisitionWorkingRootProvider>();
        sp.GetRequiredService<IBookTextIndex>()
            .Should().BeOfType<SqliteBookTextIndex>();
        sp.GetRequiredService<IBookTextEmbeddingIndex>()
            .Should().BeOfType<SqliteBookTextIndex>();
        sp.GetRequiredService<IBookDerivedArtifactStorage>()
            .Should().BeOfType<FileBookTextArtifactStorage>();
        sp.GetRequiredService<IBookTextIngestionScheduler>()
            .Should().BeOfType<BookTextIngestionScheduler>();
        sp.GetRequiredService<IEmbeddingProvider>()
            .Should().BeOfType<OpenAiCompatibleEmbeddingProvider>();

        // Post-AddNostosProduct replacements must win over product/engine defaults.
        var availability = sp.GetRequiredService<IMigrationPhaseAvailability>();
        availability.Should().BeOfType<ConfiguredMigrationPhaseAvailability>();
        availability.IsAvailable(MigrationDirection.Import).Should().BeFalse();
        availability.IsAvailable(MigrationDirection.Export).Should().BeFalse();

        // Single canonical path resolution shared across persistence, activation, transfer, Calibre, and backups.
        var paths = sp.GetRequiredService<SelfHostedStoragePaths>();
        paths.DatabasePath.Should().Be(Path.GetFullPath(dbPath));
        paths.BooksRoot.Should().Be(Path.GetFullPath(booksRoot));
        paths.TransferRoot.Should().Be(Path.GetFullPath(transferRoot));
        paths.CalibreUploadRoot.Should().Be(Path.GetFullPath(calibreUploadRoot));

        var marker = sp.GetRequiredService<LibraryMaintenanceMarker>();
        marker.MarkerPath.Should().Be(Path.Combine(Path.GetDirectoryName(paths.DatabasePath)!, ".nostos-activation", "maintenance.json"));

        var activationPaths = sp.GetRequiredService<SelfHostedActivationPaths>();
        activationPaths.LiveDatabase.Should().Be(paths.DatabasePath);
        activationPaths.LiveMedia.Should().Be(paths.BooksRoot);

        sp.GetRequiredService<TransferPathResolver>().RootPath.Should().Be(paths.TransferRoot);
        sp.GetRequiredService<CalibreUploadOptions>().RootPath.Should().Be(paths.CalibreUploadRoot);
        sp.GetRequiredService<IOptions<CalibreUploadOptions>>().Value.RootPath.Should().Be(paths.CalibreUploadRoot);

        // Options binding consistency.
        sp.GetRequiredService<ReadAloudOptions>().RequestTimeoutSeconds.Should().Be(42);
        sp.GetRequiredService<IOptions<ReadAloudOptions>>().Value.RequestTimeoutSeconds.Should().Be(42);
        sp.GetRequiredService<IOptions<SelfHostedActivationMaintenanceOptions>>().Value.StartupDelaySeconds.Should().Be(7);

        // Persistence factory points at the resolved DB path.
        using var db = sp.GetRequiredService<IDbContextFactory<NostosDbContext>>().CreateDbContext();
        db.Database.GetDbConnection().ConnectionString.Should().Contain(paths.DatabasePath);
    }

    [Fact]
    public void ActivationDrillHook_wires_observer_only_when_enabled_and_requires_marker_file()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        var paths = SelfHostedStoragePaths.Resolve(config, _root);
        services.AddSingleton(paths);
        services.AddSelfHostedCoreOverrides();
        services.AddSelfHostedEmbeddingProvider();
        services.AddNostosProduct(config);
        services.AddSelfHostedPathsAndStorage(config, _root);
        services.AddSelfHostedPortability(config, paths);

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<SelfHostedActivationDispatcher>();

        SelfHostedActivationDrillHook.ConfigureIfEnabled(provider, _ => null);
        dispatcher.CoordinatorCreatedForTesting.Should().BeNull();
        dispatcher.RestoreStepObserverForTesting.Should().BeNull();

        Action missingMarker = () => SelfHostedActivationDrillHook.ConfigureIfEnabled(
            provider,
            key => key == SelfHostedActivationDrillHook.StopAtEnvironmentVariable ? "CutoverPrepared" : null);
        missingMarker.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{SelfHostedActivationDrillHook.MarkerFileEnvironmentVariable}*");

        var markerFile = Path.Combine(_root, "drill.marker");
        var releaseFile = Path.Combine(_root, "drill.release");
        File.WriteAllText(releaseFile, "ok");

        SelfHostedActivationDrillHook.ConfigureIfEnabled(
            provider,
            key => key switch
            {
                SelfHostedActivationDrillHook.StopAtEnvironmentVariable => "CutoverPrepared",
                SelfHostedActivationDrillHook.MarkerFileEnvironmentVariable => markerFile,
                SelfHostedActivationDrillHook.ReleaseFileEnvironmentVariable => releaseFile,
                _ => null,
            });

        dispatcher.CoordinatorCreatedForTesting.Should().NotBeNull();
        dispatcher.RestoreStepObserverForTesting.Should().NotBeNull();

        dispatcher.RestoreStepObserverForTesting!("OtherStep");
        File.Exists(markerFile).Should().BeFalse();

        dispatcher.RestoreStepObserverForTesting!("CutoverPrepared");
        File.ReadAllText(markerFile).Should().Be("CutoverPrepared");
    }

    private sealed class CompositionHostFactory(
        string root,
        string dbPath,
        string booksRoot,
        string transferRoot,
        string calibreUploadRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(root);
            builder.UseSetting(PersistenceRegistration.DatabasePathConfigurationKey, dbPath);
            builder.UseSetting("Storage:BooksRoot", booksRoot);
            builder.UseSetting("Storage:TransferPath", transferRoot);
            builder.UseSetting("Storage:BackupsRoot", Path.Combine(root, "backups"));
            builder.UseSetting("CalibreUpload:RootPath", calibreUploadRoot);
            builder.UseSetting("LibraryMigration:Enabled", "false");
            builder.UseSetting("ReadAloud:RequestTimeoutSeconds", "42");
            builder.UseSetting("ActivationMaintenance:StartupDelaySeconds", "7");
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray())
                    services.Remove(descriptor);
            });
        }
    }

    private sealed class TestEnv : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Nostos.Backend";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
