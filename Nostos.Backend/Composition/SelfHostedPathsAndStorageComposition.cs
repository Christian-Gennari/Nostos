using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Health;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Services;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Services.BookUpload;
using Nostos.Backend.Services.Calibre;
using Nostos.Backend.Services.ExternalMedia;
using Nostos.Backend.Services.Library.Imports;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Product.BookText;

namespace Nostos.Backend.Composition;

/// <summary>
/// Registers SelfHosted deployment identity, pre-product local book-text and
/// staging overrides, canonical storage/database paths, persistence, local book
/// storage, external media, Calibre import/upload, resumable book uploads,
/// backup services, and SelfHosted readiness health checks.
/// </summary>
public static class SelfHostedPathsAndStorageComposition
{
    /// <summary>
    /// Must be called before <c>AddNostosProduct</c> so SelfHosted concrete
    /// acquisition-staging and local SQLite/filesystem book-text persistence win
    /// over the product's <c>TryAdd</c> no-op fallbacks.
    /// </summary>
    public static IServiceCollection AddSelfHostedCoreOverrides(this IServiceCollection services)
    {
        var deployment = DeploymentDescriptor.For(DeploymentMode.SelfHosted);
        services.AddSingleton(deployment);

        // Preserve SelfHosted's same-volume staging behavior without exposing the
        // local filesystem contract to Nostos.Product.
        services.AddSingleton<
            IAcquisitionWorkingRootProvider,
            SelfHostedAcquisitionWorkingRootProvider>();

        // SelfHosted owns the concrete local book-text persistence boundary. Register
        // these before AddNostosProduct so its TryAdd no-op fallbacks are never selected.
        services.AddScoped<SqliteBookTextIndex>();
        services.AddScoped<IBookTextIndex>(sp => sp.GetRequiredService<SqliteBookTextIndex>());
        services.AddScoped<IBookTextEmbeddingIndex>(sp => sp.GetRequiredService<SqliteBookTextIndex>());
        services.AddScoped<IBookDerivedArtifactStorage, FileBookTextArtifactStorage>();
        services.AddScoped<IBookTextIngestionScheduler, BookTextIngestionScheduler>();
        // Scheduling a book wakes the in-process ingestion worker instead of it polling.
        services.AddSingleton<BookTextWorkSignal>();

        return services;
    }

    /// <summary>
    /// Resolves all SelfHosted storage/DB paths once, binds storage and backup
    /// options via <c>AddOptions&lt;T&gt;().BindConfiguration(...)</c>, and
    /// registers persistence, media/upload stores, backups, and health checks.
    /// </summary>
    public static SelfHostedStoragePaths AddSelfHostedPathsAndStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        var paths = SelfHostedStoragePaths.Resolve(configuration, contentRootPath);
        services.AddSingleton(paths);

        services.AddOptions<BackupSettings>()
            .BindConfiguration("BackupSettings");

        // Where locally stored book files live. Configurable so a test host (or a
        // deployment with a dedicated media volume) can point it somewhere else —
        // `Storage/books` under the content root stays the default.
        services.AddOptions<FileStorageOptions>()
            .BindConfiguration(FileStorageOptions.SectionName);

        // Transfer storage for library migration jobs (issue #679, Slice 3). The root
        // is validated before startup and created when missing; every transfer service
        // resolves paths through this one resolver, and admission accounting measures
        // the volume that actually holds it.
        services.AddOptions<TransferStorageOptions>()
            .BindConfiguration(TransferStorageOptions.SectionName)
            .Validate(options =>
            {
                TransferStorageOptions.Validate(options);
                return true;
            });

        TransferPathResolver.EnsureRootDirectory(paths.TransferRoot);
        services.AddSingleton(new TransferPathResolver(paths.TransferRoot));
        services.AddSingleton<ITransferVolume>(new DriveInfoTransferVolume(paths.TransferRoot));
        services.AddScoped<ITransferStorageCapacity, TransferStorageCapacity>();

        services.AddNostosPersistence(paths.DatabasePath);
        services.AddScoped<IDatabaseBootstrapService, DatabaseBootstrapService>();

        // Services Dependency Injection
        services.AddSingleton<FileStorageService>();
        services.AddSingleton<IFileStorageService>(
            sp => sp.GetRequiredService<FileStorageService>());

        // External media (issue #843): book storage is wrapped so a file Nostos does
        // not own is read in place from the one administrator-mounted folder. The
        // wrapper is registered even when no folder is configured, so replacing or
        // removing a book's media always forgets a reference left by an earlier
        // configuration.
        services.AddOptions<ExternalMediaOptions>()
            .BindConfiguration(ExternalMediaOptions.SectionName);
        services.AddSingleton<ExternalMediaRoot>();
        services.AddSingleton<IExternalMediaAvailability>(
            sp => sp.GetRequiredService<ExternalMediaRoot>());
        services.AddSingleton<ExternalMediaLinkStore>();
        services.AddSingleton(sp => new ExternalMediaBookStorage(
            sp.GetRequiredService<FileStorageService>(),
            sp.GetRequiredService<FileStorageService>(),
            sp.GetRequiredService<ExternalMediaRoot>(),
            sp.GetRequiredService<ExternalMediaLinkStore>()));
        services.AddSingleton<IExternalMediaDescriber, ExternalMediaDescriber>();
        services.AddSingleton<ExternalMediaScanService>();
        services.AddSingleton<ExternalMediaImportService>();
        services.AddSingleton<IBookAssetStorage>(
            sp => sp.GetRequiredService<ExternalMediaBookStorage>());
        services.AddSingleton<IBookTrackStorage>(
            sp => sp.GetRequiredService<ExternalMediaBookStorage>());

        // Calibre library import (issue #1527): the same read-only folder discipline as
        // external media, with a separate administrator-named root so a Calibre library
        // is not also offered as a set of loose media folders. The import itself is
        // host-agnostic (Nostos.Product); only this folder source is SelfHosted.
        services.AddOptions<CalibreOptions>()
            .BindConfiguration(CalibreOptions.SectionName);
        services.AddSingleton<CalibreFolderSource>();
        services.AddSingleton<ICalibreLibraryAvailability>(
            sp => sp.GetRequiredService<CalibreFolderSource>());

        // A zipped Calibre library (#1572). The tenant's scratch area lives beside the
        // migration transfer root, in its own namespace, so cleanup here can never
        // touch an in-flight library migration. The same resumable contract the
        // browser uses for library transfer is used for the zip: server-owned 16 MiB
        // parts, a SHA-256 receipt per part, and a whole-file check before extraction.
        services.AddOptions<CalibreUploadOptions>()
            .BindConfiguration(CalibreUploadOptions.SectionName)
            .PostConfigure(options =>
            {
                CalibreUploadOptions.Validate(options);
                options.RootPath = paths.CalibreUploadRoot;
            });
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<CalibreUploadOptions>>().Value);
        services.AddSingleton<ICalibreUploadLimitsProvider, SelfHostedCalibreUploadLimitsProvider>();
        services.AddScoped<ICalibreUploadOwnerProvider, SelfHostedCalibreUploadOwnerProvider>();
        services.AddSingleton<ICalibreUploadStore>(sp => new FileCalibreUploadStore(
            paths.CalibreUploadRoot,
            sp.GetRequiredService<ILogger<FileCalibreUploadStore>>()));
        services.AddScoped<CalibreUploadService>();
        services.AddScoped<ICalibreUploadAvailability>(
            sp => sp.GetRequiredService<CalibreUploadService>());
        services.AddScoped<CalibreUploadSource>();
        services.AddScoped<CalibreImportSourceRouter>();
        services.AddScoped<ICalibreLibrarySource>(
            sp => sp.GetRequiredService<CalibreImportSourceRouter>());

        // Resumable book-file uploads (#1266). The staging store lives beside the
        // migration transfer root but in its own namespace. The capability endpoint
        // advertises exactly this registration, and the route group is mapped only
        // because of it — a host without the store never offers the contract.
        services.AddSingleton<BookUploadWriteGate>();
        services.AddSingleton<IBookFileUploadStore, LocalBookFileUploadStore>();
        services.AddScoped<IBookUploadOwnerProvider, SelfHostedBookUploadOwnerProvider>();
        services.AddScoped<BookUploadService>();

        services.AddNostosSelfHostedHealthChecks();

        services.AddSingleton<BackupSettingsProvider>();
        services.AddScoped<IBackupService, BackupService>();

        return paths;
    }
}
