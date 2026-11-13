using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Integrations.Assistant;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.ReadingList;
using Nostos.Product.BookText;

namespace Nostos.Product.Composition;

internal static class NostosAssistantServices
{
    internal static IServiceCollection AddNostosAssistantServices(
        this IServiceCollection services)
    {
        services.AddSingleton<IThoughtProcessor, ThoughtProcessor>();
        services.AddSingleton<AssistantChangesetStore>();
        // SelfHosted has one library per process. A multi-tenant host registers
        // its own owner so changesets, continuations and stoppable turns are
        // kept (and bounded) per account.
        services.TryAddScoped<IAssistantOwner>(_ => SingleAssistantOwner.Instance);
        services.AddScoped<AssistantChangesetCoordinator>();
        services.AddSingleton<AssistantContinuationStore>();
        services.AddSingleton<AssistantTurnExecutionRegistry>();
        services.AddScoped<AssistantOrchestrator>();

        services.AddScoped(sp => new AssistantCapabilityRegistry(AssistantCapabilities.Build(
            sp.GetRequiredService<INoteService>(),
            sp.GetRequiredService<ILibraryService>(),
            sp.GetRequiredService<ITopicRepository>(),
            sp.GetRequiredService<IKnowledgeRetrievalService>(),
            sp.GetRequiredService<IBookTextSearchService>(),
            sp.GetRequiredService<IBookTextQuoteFinder>(),
            sp.GetRequiredService<IWritingRepository>(),
            sp.GetRequiredService<IReadingListService>(),
            sp.GetRequiredService<IBookPassageAnchors>())));

        return services;
    }
}
