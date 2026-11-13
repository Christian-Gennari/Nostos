using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Tests.Services.Ai;

namespace Nostos.Backend.Tests.Support;

/// <summary>
/// The note service and each collaborator it delegates to (issue #1619), over
/// one real SQLite context: the shape the web host's composition builds, so a
/// test can exercise one collaborator without going through the facade.
/// </summary>
public sealed class NoteServiceTestContext : IDisposable
{
    public NoteServiceTestContext(string databasePath, IThoughtProcessor? thoughts = null)
    {
        var options = new DbContextOptionsBuilder<NostosDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        Db = new NostosDbContext(options);
        Db.Database.EnsureCreated();

        Notes = new NoteRepository(Db);
        Books = new BookRepository(Db);
        Topics = new TopicRepository(Db);
        Processor = new NoteProcessorService(Topics);
        Snippets = new NoteSnippetFormatter();
        Projector = new NoteProjector(Snippets);
        Editions = new NoteEditionCounter(Books);
        Reads = new NoteReadService(Notes, Projector, Editions);
        Search = new NoteSearchService(Notes, Projector, Snippets, Editions);
        Links = new NoteTopicLinkService(Notes, Topics, Processor, Reads);
        Lifecycle = new NoteProcessingLifecycleService(
            Notes,
            Processor,
            thoughts ?? new FakeThoughtProcessor(),
            Reads,
            NullLogger<NoteProcessingLifecycleService>.Instance);
        Service = new NoteService(Notes, Books, Processor, Db, Reads, Search, Links, Lifecycle);
    }

    public NostosDbContext Db { get; }
    public INoteRepository Notes { get; }
    public IBookRepository Books { get; }
    public ITopicRepository Topics { get; }
    public NoteProcessorService Processor { get; }
    public NoteSnippetFormatter Snippets { get; }
    public NoteProjector Projector { get; }
    public NoteEditionCounter Editions { get; }
    public NoteReadService Reads { get; }
    public NoteSearchService Search { get; }
    public NoteTopicLinkService Links { get; }
    public NoteProcessingLifecycleService Lifecycle { get; }
    public NoteService Service { get; }

    public void Dispose() => Db.Dispose();

    /// <summary>Adds one work (a book, not an edition) and saves it.</summary>
    public async Task<WorkModel> SeedWorkAsync(string title, Guid? id = null, string? author = null)
    {
        var work = new WorkModel { Id = id ?? Guid.NewGuid(), Title = title, Author = author };
        Db.Works.Add(work);
        await Db.SaveChangesAsync();
        return work;
    }

    /// <summary>Adds one edition under a work — created when none is given — and saves it.</summary>
    public async Task<TBook> SeedBookAsync<TBook>(string title, Guid? workId = null, string? author = null)
        where TBook : BookModel, new()
    {
        var work = workId is { } id
            ? Db.Works.Local.FirstOrDefault(candidate => candidate.Id == id)
                ?? await Db.Works.FindAsync(id)
                ?? await SeedWorkAsync(title, id, author)
            : await SeedWorkAsync(title, author: author);

        var book = new TBook { Id = Guid.NewGuid(), Title = title, Author = author, WorkId = work.Id };
        Db.Books.Add(book);
        await Db.SaveChangesAsync();
        return book;
    }

    /// <summary>Adds one note in a book and saves it.</summary>
    public async Task<NoteModel> SeedNoteAsync(
        Guid bookId, string content, string? raw = null, string mode = "verbatim")
    {
        var note = new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Content = content,
            RawContent = raw,
            ProcessingMode = mode,
            CreatedAt = DateTime.UtcNow,
        };
        Db.Notes.Add(note);
        await Db.SaveChangesAsync();
        return note;
    }
}
