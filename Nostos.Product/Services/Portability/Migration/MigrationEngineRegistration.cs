using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>SelfHosted-only upload/worker composition; no hosted adapter or HTTP endpoints.</summary>
/// <remarks>
/// <para>
/// This registration is separable from the provider-neutral product composition:
/// a host that supplies its own migration adapter simply does not call
/// <see cref="AddSelfHostedMigrationEngine"/> and registers its own
/// <see cref="IMigrationJobStore"/>, <see cref="IPortableImportStaging"/> and
/// transfer services instead. The SelfHosted executable calls it after
/// <see cref="Nostos.Product.Composition.NostosProductComposition.AddNostosProduct"/>
/// so its engine store and phase handlers win over the product defaults.
/// </para>
/// <para>
/// Registering the engine also requires the host's transfer storage and staging
/// registrations (transfer path resolver, import staging, staging cleanup and
/// the migration maintenance gate); the engine consumes them but does not
/// create them.
/// </para>
/// </remarks>
public static class MigrationEngineRegistration
{
    /// <summary>
    /// Registers the SelfHosted migration engine: the durable job store, the
    /// upload/transfer services, the real import/export phase handlers, the job
    /// worker and the transfer cleanup worker.
    /// </summary>
    /// <remarks>
    /// Do not call this from a host that supplies its own migration adapter; see
    /// the class remarks for the separable composition.
    /// </remarks>
    public static IServiceCollection AddSelfHostedMigrationEngine(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        // Job creation refuses directions without a real phase handler. Slices
        // 9/10 register the real handlers and report both directions available.
        services.TryAddSingleton<IMigrationPhaseAvailability>(MigrationPhaseAvailabilityAll.Instance);
        services.AddScoped<ILibraryDestinationRevisionProvider, LibraryStateDestinationRevisionProvider>();
        services.AddSingleton<MigrationJobCancellationRegistry>();
        services.AddSingleton<MigrationProcessingSlots>();
        services.AddSingleton<MigrationFileMutex>();
        services.AddScoped<EfMigrationJobStore>();
        services.AddScoped<IMigrationJobStore, MigrationEngineJobStore>();
        services.AddScoped<FileMigrationUploadStore>();
        services.AddScoped<MigrationTransferCleanup>();
        services.AddSingleton<LegacyPortabilityScratchCleanup>();
        services.AddSingleton<MigrationLegacyScratchSweep>();
        services.AddScoped<SelfHostedMigrationTransferService>();
        services.AddScoped<IMigrationTransferService>(s => s.GetRequiredService<SelfHostedMigrationTransferService>());
        services.AddScoped<ISelfHostedMigrationUploads>(s => s.GetRequiredService<SelfHostedMigrationTransferService>());
        // Slice 8 transport orchestration. Job creation refuses directions whose
        // phase handler is not wired; Slices 9/10 register the real handlers and
        // the matching availability below.
        services.AddScoped<IMigrationPreflightService, SelfHostedMigrationPreflightService>();
        services.AddScoped<SelfHostedMigrationJobService>();
        services.AddScoped<IMigrationPhaseHandler, ImportPreparationPhaseHandler>();
        services.AddScoped<IMigrationPhaseHandler, ExportArtifactPhaseHandler>();
        services.AddScoped<MigrationJobProcessor>();
        services.AddHostedService(s => new MigrationJobWorker(s.GetRequiredService<IServiceScopeFactory>(),
            s.GetRequiredService<TimeProvider>(), s.GetRequiredService<MigrationJobCancellationRegistry>(),
            s.GetRequiredService<MigrationProcessingSlots>(), s.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MigrationJobWorker>>(), s.GetRequiredService<IMigrationMaintenanceGate>()));
        services.AddHostedService<MigrationTransferCleanupWorker>();
        return services;
    }
}
