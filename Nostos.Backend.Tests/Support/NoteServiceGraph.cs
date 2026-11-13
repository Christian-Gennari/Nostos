using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Notes;

namespace Nostos.Backend.Tests.Support;

/// <summary>
/// Builds <see cref="NoteService"/> and the collaborators it delegates to over
/// one scoped context, the way the web host's composition wires them (issue
/// #1619). The same context backs every repository and the processor, so a
/// create/update stays one unit of work exactly as in the web host.
/// </summary>
public static class NoteServiceGraph
{
    public static NoteService Create(NostosDbContext db, IThoughtProcessor thoughts)
    {
        var notes = new NoteRepository(db);
        var books = new BookRepository(db);
        var topics = new TopicRepository(db);
        var processor = new NoteProcessorService(topics);
        var snippets = new NoteSnippetFormatter();
        var projector = new NoteProjector(snippets);
        var editions = new NoteEditionCounter(books);
        var reads = new NoteReadService(notes, projector, editions);

        return new NoteService(
            notes,
            books,
            processor,
            db,
            reads,
            new NoteSearchService(notes, projector, snippets, editions),
            new NoteTopicLinkService(notes, topics, processor, reads),
            new NoteProcessingLifecycleService(
                notes,
                processor,
                thoughts,
                reads,
                NullLogger<NoteProcessingLifecycleService>.Instance));
    }
}
