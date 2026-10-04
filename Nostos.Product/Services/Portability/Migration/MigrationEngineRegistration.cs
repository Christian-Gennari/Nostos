using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>SelfHosted-only upload/worker composition; no hosted adapter or HTTP endpoints.</summary>
public static class MigrationEngineRegistration
{
    public static IServiceCollection AddSelfHostedMigrationEngine(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<MigrationJobCancellationRegistry>();
        services.AddSingleton<MigrationProcessingSlots>();
        services.AddSingleton<MigrationFileMutex>();
        services.AddScoped<EfMigrationJobStore>();
        services.AddScoped<IMigrationJobStore, MigrationEngineJobStore>();
        services.AddScoped<FileMigrationUploadStore>();
        services.AddScoped<MigrationTransferCleanup>();
        services.AddScoped<SelfHostedMigrationTransferService>();
        services.AddScoped<IMigrationTransferService>(s => s.GetRequiredService<SelfHostedMigrationTransferService>());
        services.AddScoped<ISelfHostedMigrationUploads>(s => s.GetRequiredService<SelfHostedMigrationTransferService>());
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
