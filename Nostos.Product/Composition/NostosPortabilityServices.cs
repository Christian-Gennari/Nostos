using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Markdown;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Recovery;

namespace Nostos.Product.Composition;

internal static class NostosPortabilityServices
{
    internal static IServiceCollection AddNostosPortabilityServices(
        this IServiceCollection services)
    {
        services.AddScoped<IPortableArchiveService, PortableArchiveService>();
        services.TryAddScoped<IPortableArchiveExporter, DefaultPortableArchiveExporter>();
        services.TryAddScoped<IMarkdownNotesExporter, MarkdownNotesExporter>();
        services.TryAddScoped<IManagedBackupCatalog, NotSupportedManagedBackupCatalog>();
        services.TryAddScoped<IManagedBackupRestorer, NotSupportedManagedBackupRestorer>();
        services.TryAddScoped<IMigrationJobStore, EfMigrationJobStore>();
        // Migration job creation is refused until a host wires a real phase
        // handler. A host with no engine (for example one that maps product
        // endpoints with MapMigrationTransferEndpoints = false) reports no
        // availability, so the capability endpoint can never advertise a
        // feature this host cannot execute. The SelfHosted engine replaces
        // this with its real Slice 9/10 handlers.
        services.TryAddSingleton<IMigrationPhaseAvailability>(MigrationPhaseAvailabilityNone.Instance);
        services.TryAddScoped<ILibraryDestinationRevisionProvider, LibraryStateDestinationRevisionProvider>();
        services.AddScoped<PortableArchiveReader>();
        // The public prepared-import seam resolves through the reader's optional
        // dependencies so it works in a host that supplies neither logging nor a
        // time provider.
        services.TryAddScoped<IPortableImportPreparer>(sp => new PortableArchiveReader(
            sp.GetService<ILogger<PortableArchiveReader>>(),
            sp.GetService<TimeProvider>()));

        return services;
    }
}
