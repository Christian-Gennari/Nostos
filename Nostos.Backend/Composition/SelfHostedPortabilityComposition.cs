using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nostos.Backend.Services;
using Nostos.Backend.Services.BookText;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Services.Portability.Migration;

namespace Nostos.Backend.Composition;

/// <summary>
/// Registers SelfHosted library migration engine, configured phase availability,
/// durable import staging, library maintenance gate/marker, activation cutover,
/// recovery restore, and derived rebuild services.
/// </summary>
public static class SelfHostedPortabilityComposition
{
    public static IServiceCollection AddSelfHostedPortability(
        this IServiceCollection services,
        IConfiguration configuration,
        SelfHostedStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);

        var activationMaintenanceOptions = configuration
            .GetSection(SelfHostedActivationMaintenanceOptions.SectionName)
            .Get<SelfHostedActivationMaintenanceOptions>()
            ?? new SelfHostedActivationMaintenanceOptions();
        SelfHostedActivationMaintenanceOptions.Validate(activationMaintenanceOptions);

        services.AddOptions<SelfHostedActivationMaintenanceOptions>()
            .BindConfiguration(SelfHostedActivationMaintenanceOptions.SectionName)
            .Validate(options =>
            {
                SelfHostedActivationMaintenanceOptions.Validate(options);
                return true;
            });

        // Must follow AddNostosProduct so the SelfHosted engine store and phase
        // handlers replace the product's defaults, and must precede the
        // ConfiguredMigrationPhaseAvailability replacement below.
        MigrationEngineRegistration.AddSelfHostedMigrationEngine(services);

        // Operator-facing library-migration advertisement (issue #680 review). The
        // capability derives from the SAME phase availability the migration routes
        // consult, so this host can never offer a feature it cannot execute; the
        // LibraryMigration:Enabled key is the emergency switch (default on).
        services.AddOptions<LibraryMigrationOptions>()
            .BindConfiguration(LibraryMigrationOptions.SectionName);
        services.Replace(ServiceDescriptor.Singleton<IMigrationPhaseAvailability>(sp =>
            new ConfiguredMigrationPhaseAvailability(
                MigrationPhaseAvailabilityAll.Instance,
                sp.GetRequiredService<IOptions<LibraryMigrationOptions>>().Value)));

        // Durable prepared-import staging for restart-survivable migration jobs
        // (issue #679, Slice 4). The immediate import endpoint keeps constructing its
        // process-local scratch staging directly; this registration is what a migration
        // worker resolves. The cleanup hook retires staging areas abandoned by a crash
        // before a job ever recorded them (issue #679, Slice 9).
        services.AddScoped<IPortableImportStaging, FilePortableImportStaging>();
        services.AddScoped<IMigrationStagingCleanup, FilePortableImportStagingCleanup>();

        services.AddOptions<LibraryMaintenanceOptions>()
            .BindConfiguration(LibraryMaintenanceOptions.SectionName);
        services.AddSingleton(new LibraryMaintenanceMarker(paths.DatabasePath));
        services.AddSingleton(sp => new LibraryMaintenanceCoordinator(
            sp.GetRequiredService<IOptions<LibraryMaintenanceOptions>>().Value,
            marker: sp.GetRequiredService<LibraryMaintenanceMarker>()));
        services.AddSingleton<ILibraryMaintenanceCoordinator>(
            sp => sp.GetRequiredService<LibraryMaintenanceCoordinator>());
        services.AddSingleton<IMigrationMaintenanceGate, MigrationMaintenanceGate>();

        services.AddSingleton(new SelfHostedActivationPaths(paths.DatabasePath, paths.BooksRoot));
        services.AddSingleton<SelfHostedActivationJournalStore>();
        services.AddSingleton<ISelfHostedActivationRecoveryStep>(sp =>
            new SelfHostedActivationComponentStep(sp.GetRequiredService<SelfHostedActivationPaths>(), database: false));
        services.AddSingleton<ISelfHostedActivationRecoveryStep>(sp =>
            new SelfHostedActivationComponentStep(sp.GetRequiredService<SelfHostedActivationPaths>(), database: true));
        services.AddSingleton<SelfHostedActivationRecoveryStartupService>();

        // --- #681 SLICE 7: END-TO-END ACTIVATION COORDINATOR ---
        // The coordinator joins the job admission, candidate builders, recovery
        // retention and the journaled cutover. It is scoped because it resolves the
        // scoped recovery/job services from fresh scopes at the activation boundary;
        // post-commit work resolves new scopes so it executes against the newly
        // activated database, never a pre-switch connection identity.
        services.AddSingleton<SelfHostedRecoveryManifestStore>();
        services.AddSingleton<ISelfHostedActivationRecoveryStep>(sp =>
            new SelfHostedActivationEmptyRetentionStep(
                sp.GetRequiredService<SelfHostedActivationPaths>(),
                sp.GetRequiredService<SelfHostedRecoveryManifestStore>()));
        services.AddSingleton<ISelfHostedVolumeSpaceProbe, VolumeSpaceProbe>();
        services.AddScoped<SelfHostedActivationCapacity>();
        services.AddScoped<ISelfHostedActivationCandidateMediaBuilder, SelfHostedActivationCandidateMediaBuilder>();
        services.AddScoped<ISelfHostedActivationDatabaseBuilder, SelfHostedActivationDatabaseBuilder>();
        services.AddSingleton<ISelfHostedSqliteLifecycle, SelfHostedSqliteLifecycle>();
        services.AddSingleton<IPortableLibraryVerifier, PortableLibraryVerifier>();
        services.AddScoped<SelfHostedMigrationRecoveryService>();
        services.AddScoped<ISelfHostedRecoverySnapshots>(sp => sp.GetRequiredService<SelfHostedMigrationRecoveryService>());
        services.AddScoped<ISelfHostedRecoveryCatalog>(sp => sp.GetRequiredService<SelfHostedMigrationRecoveryService>());
        services.AddScoped<ISelfHostedRecoveryCleanup>(sp => sp.GetRequiredService<SelfHostedMigrationRecoveryService>());
        services.AddScoped<SelfHostedActivationCoordinator>();
        services.AddScoped<IMigrationActivationService>(sp => sp.GetRequiredService<SelfHostedActivationCoordinator>());

        // --- #681 SLICE 9: RECOVERY RESTORE ("RESTORE PREVIOUS LIBRARY") ---
        // Restore execution shares the Slice 8 library-switch dispatcher, so an
        // activation and a restore can never run concurrently; the processor scans once
        // at startup (after the activation reconciler and database bootstrap) and
        // periodically thereafter, enqueueing durably claimed restores.
        services.AddSingleton<ISelfHostedRecoverySchemaMigrator, SelfHostedRecoverySchemaMigrator>();
        services.AddScoped<SelfHostedRecoveryRestoreCoordinator>();
        services.AddScoped<SelfHostedRecoveryRestoreHostService>();
        services.AddScoped<ISelfHostedRecoveryRestore>(
            sp => sp.GetRequiredService<SelfHostedRecoveryRestoreHostService>());

        // #681 SLICE 8: HTTP activation driver. The singleton admits one background
        // run per job and keeps the in-memory status snapshot that stays answerable
        // while exclusive maintenance has the live database closed.
        services.AddSingleton<SelfHostedActivationDispatcher>();
        services.AddSingleton<IMigrationActivationDispatcher>(
            sp => sp.GetRequiredService<SelfHostedActivationDispatcher>());

        // #681 SLICE 10: scheduled recovery expiry cleanup, activation orphan sweep and
        // the restart-safe post-activation derived rebuild. All passes take the shared
        // operation lease, so none can run inside or race an exclusive cutover window.
        services.AddScoped<IBookTextDerivedReset, SqliteBookTextDerivedReset>();
        services.AddScoped<IBookTextDerivedScheduler, StrictBookTextDerivedScheduler>();
        services.AddScoped<SelfHostedActivationOrphanSweep>();
        services.AddSingleton<SelfHostedDerivedRebuildService>();

        return services;
    }
}
