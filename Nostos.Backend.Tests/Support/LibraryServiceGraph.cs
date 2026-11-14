using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Tests.Support;

/// <summary>
/// Builds <see cref="LibraryService"/> and the collaborators it delegates to,
/// the way the web host's composition wires them (issue #1611). The facade takes
/// its collaborators instead of building them, so a test that needs one of them
/// directly can construct the same graph here rather than guessing its
/// dependencies.
/// </summary>
public static class LibraryServiceGraph
{
    /// <summary>The facade's collaborators, individually reachable for unit tests.</summary>
    public sealed record Parts(
        LibraryReadService Reads,
        LibraryBookCommandService Books,
        LibraryProgressService Progress,
        LibraryWorkService Works,
        LibraryCollectionService Collections,
        LibraryChangesetService Changesets)
    {
        public LibraryService Service => new(Reads, Books, Progress, Works, Collections, Changesets);
    }

    public static Parts Build(
        IDbContextFactory<NostosDbContext> contexts,
        BookLookupService lookup)
    {
        var mutations = new LibraryMutationExecutor(contexts);
        var books = new LibraryBookCommandService(contexts, mutations);
        var collections = new LibraryCollectionService(mutations);

        return new Parts(
            new LibraryReadService(contexts, lookup),
            books,
            new LibraryProgressService(mutations),
            new LibraryWorkService(mutations),
            collections,
            new LibraryChangesetService(mutations, books, collections));
    }

    public static LibraryService Create(
        IDbContextFactory<NostosDbContext> contexts,
        BookLookupService lookup) => Build(contexts, lookup).Service;
}
