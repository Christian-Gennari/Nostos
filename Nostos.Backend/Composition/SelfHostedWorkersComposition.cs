using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Calibre;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Backend.Workers;

namespace Nostos.Backend.Composition;

/// <summary>
/// Registers SelfHosted background workers and hosted job/cleanup processors.
/// </summary>
public static class SelfHostedWorkersComposition
{
    public static IServiceCollection AddSelfHostedWorkers(this IServiceCollection services)
    {
        services.AddHostedService<CalibreUploadCleanupService>();
        services.AddHostedService<BookUploadCleanupService>();

        services.AddHostedService<SelfHostedRecoveryRestoreProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<SelfHostedActivationDispatcher>());
        services.AddHostedService<SelfHostedDerivedRebuildWorker>();
        services.AddHostedService<SelfHostedRecoveryCleanupWorker>();

        // One instance serves as the job store, the hosted worker that drains it, and
        // the IAcquisitionJobManager the endpoints talk to.
        services.AddSingleton<AcquisitionJobManager>();
        services.AddSingleton<IAcquisitionJobManager>(sp => sp.GetRequiredService<AcquisitionJobManager>());
        services.AddHostedService(sp => sp.GetRequiredService<AcquisitionJobManager>());
        services.AddHostedService<AcquisitionReconciliationWorker>();
        services.AddHostedService<TopicCleanupWorker>();
        services.AddHostedService<BackupWorker>();
        services.AddHostedService<LibraryReceiptRetentionWorker>();
        services.AddHostedService<BookTextIngestionWorker>();

        return services;
    }
}
