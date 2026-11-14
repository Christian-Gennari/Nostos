using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Services;
using Nostos.Backend.Services.BookUpload;
using Nostos.Backend.Services.Knowledge;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Library.Imports;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Notes.Imports;
using Nostos.Backend.Services.ReadingList;
using Nostos.Backend.Services.Recovery;

namespace Nostos.Product.Composition;

internal static class NostosLibraryServices
{
    internal static IServiceCollection AddNostosLibraryServices(
        this IServiceCollection services)
    {
        services.AddScoped<IBookRepository, BookRepository>();
        services.AddScoped<INoteRepository, NoteRepository>();
        services.AddScoped<ITopicRepository, TopicRepository>();
        services.AddScoped<IWritingRepository, WritingRepository>();

        // The legacy single-POST upload commits through this on every host,
        // Cloud included, so it is registered here rather than per host (#1266).
        services.TryAddScoped<BookFileCommit>();
        services.TryAddScoped<BookTrackUpload>();

        services.AddScoped<BookLookupService>();
        // LibraryService is the public facade; reads, book commands, progress,
        // edition grouping, collections and changesets are focused collaborators
        // (issue #1611). TryAdd keeps a host's own registration authoritative.
        services.TryAddScoped<LibraryMutationExecutor>();
        services.TryAddScoped<LibraryReadService>();
        services.TryAddScoped<LibraryBookCommandService>();
        services.TryAddScoped<LibraryProgressService>();
        services.TryAddScoped<LibraryWorkService>();
        services.TryAddScoped<LibraryCollectionService>();
        services.TryAddScoped<LibraryChangesetService>();
        services.AddScoped<ILibraryService, LibraryService>();
        services.AddScoped<ILibraryChangesetService, LibraryService>();
        services.AddScoped<LibraryReceiptRetentionService>();
        services.AddScoped<MediaMetadataService>();
        services.AddScoped<AudiobookPackageService>();
        services.AddScoped<NoteProcessorService>();
        // NoteService is the command surface; its reads, search, topic linking
        // and raw-transcript lifecycle are focused collaborators (issue #1619).
        // TryAdd keeps a host's own registration authoritative.
        services.TryAddScoped<NoteSnippetFormatter>();
        services.TryAddScoped<NoteProjector>();
        services.TryAddScoped<NoteEditionCounter>();
        services.TryAddScoped<NoteReadService>();
        services.TryAddScoped<NoteSearchService>();
        services.TryAddScoped<NoteTopicLinkService>();
        services.TryAddScoped<NoteProcessingLifecycleService>();
        services.AddScoped<INoteService, NoteService>();
        services.AddScoped<IReadingListService, ReadingListService>();
        services.AddScoped<KoreaderNoteImportService>();
        services.AddScoped<HighlightImportService>();
        services.AddScoped<ReadwiseApiClient>();
        services.AddScoped<ReadwiseConnectionService>();
        services.AddScoped<ReadwiseImportService>();
        services.AddScoped<ReadingHistoryImportService>();
        services.AddScoped<CalibreImportService>();
        // One import at a time per process; the host registers the source that
        // stands behind it (a folder, an uploaded library, or both).
        services.TryAddSingleton<CalibreUploadOwnerScope>();
        services.TryAddSingleton<CalibreImportJob>();
        services.AddScoped<IKnowledgeRetrievalService, KnowledgeRetrievalService>();

        return services;
    }
}
