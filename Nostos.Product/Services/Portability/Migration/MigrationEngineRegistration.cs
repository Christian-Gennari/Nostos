using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>SelfHosted-only upload/worker composition; no hosted adapter or HTTP endpoints.</summary>
public static class MigrationEngineRegistration
{
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
