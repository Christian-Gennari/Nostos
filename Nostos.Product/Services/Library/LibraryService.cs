using Nostos.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Canonical library service (issue #34). REST endpoints, Ask Nostos and the
/// Angular UI all call this one service; it is the single writer for books and
/// collections. Mutations are exact-once through LibraryCommandReceipt
/// (separate from reading receipts on purpose).
///
/// It is a facade (issue #1611): reads delegate to <see cref="LibraryReadService"/>,
/// book commands to <see cref="LibraryBookCommandService"/>, reading progress to
/// <see cref="LibraryProgressService"/>, edition grouping to
/// <see cref="LibraryWorkService"/>, collections to
/// <see cref="LibraryCollectionService"/> and changesets to
/// <see cref="LibraryChangesetService"/>. The public surface and every reply
/// shape are unchanged; each collaborator owns its own transaction, gate and
/// revision advance.
/// </summary>
public sealed class LibraryService : ILibraryService, ILibraryChangesetService
{
    /// <summary>Most operations one changeset may carry.</summary>
    public const int MaxChangesetOperations = LibraryChangesetService.MaxChangesetOperations;

    /// <summary>Most book ids one changeset may name across its operations.</summary>
    public const int MaxChangesetBooks = LibraryChangesetService.MaxChangesetBooks;

    private readonly LibraryReadService _reads;
    private readonly LibraryBookCommandService _books;
    private readonly LibraryProgressService _progress;
    private readonly LibraryWorkService _works;
    private readonly LibraryCollectionService _collections;
    private readonly LibraryChangesetService _changesets;

    /// <summary>
    /// Builds the whole collaborator graph over one context factory. Hosts that
    /// construct the service by hand (Nostos-Cloud's tests, focused unit tests)
    /// keep the constructor they had before the decomposition (#1611). The
    /// product composition registers the facade with a factory over the
    /// collaborator constructor below (two satisfiable constructors would be
    /// ambiguous to the container).
    /// </summary>
    public LibraryService(IDbContextFactory<NostosDbContext> contexts, BookLookupService lookup)
        : this(CreateGraph(contexts, lookup))
    {
    }

    private LibraryService(
        (LibraryReadService Reads, LibraryBookCommandService Books, LibraryProgressService Progress,
            LibraryWorkService Works, LibraryCollectionService Collections, LibraryChangesetService Changesets) graph)
        : this(graph.Reads, graph.Books, graph.Progress, graph.Works, graph.Collections, graph.Changesets)
    {
    }

    private static (LibraryReadService, LibraryBookCommandService, LibraryProgressService,
        LibraryWorkService, LibraryCollectionService, LibraryChangesetService) CreateGraph(
        IDbContextFactory<NostosDbContext> contexts,
        BookLookupService lookup)
    {
        var mutations = new LibraryMutationExecutor(contexts);
        var books = new LibraryBookCommandService(contexts, mutations);
        var collections = new LibraryCollectionService(mutations);
        return (
            new LibraryReadService(contexts, lookup),
            books,
            new LibraryProgressService(mutations),
            new LibraryWorkService(mutations),
            collections,
            new LibraryChangesetService(mutations, books, collections));
    }

    public LibraryService(
        LibraryReadService reads,
        LibraryBookCommandService books,
        LibraryProgressService progress,
        LibraryWorkService works,
        LibraryCollectionService collections,
        LibraryChangesetService changesets)
    {
        _reads = reads;
        _books = books;
        _progress = progress;
        _works = works;
        _collections = collections;
        _changesets = changesets;
    }

    // ------------------------------------------------------------------
    // Read-only surface
    // ------------------------------------------------------------------

    public Task<LibraryCommandResultDto> ListBooksAsync(
        BookFilter filter,
        BookSort sort,
        string? search,
        int page,
        int pageSize,
        Guid? collectionId,
        bool? groupByWork = false,
        string? format = null,
        CancellationToken ct = default) =>
        _reads.ListBooksAsync(filter, sort, search, page, pageSize, collectionId, groupByWork, format, ct);

    public Task<IReadOnlyList<LibraryBookScopeEntry>> ListBookScopeAsync(
        IReadOnlyList<Guid>? bookIds,
        Guid? collectionId,
        int limit,
        CancellationToken ct = default) =>
        _reads.ListBookScopeAsync(bookIds, collectionId, limit, ct);

    public Task<IReadOnlyList<EditionSuggestionDto>> FindEditionSuggestionsAsync(
        Guid bookId,
        CancellationToken ct = default) =>
        _reads.FindEditionSuggestionsAsync(bookId, ct);

    public Task<LibraryCommandResultDto> GetStatusCountsAsync(CancellationToken ct = default) =>
        _reads.GetStatusCountsAsync(ct);

    public Task<LibraryCommandResultDto> GetBookAsync(Guid bookId, CancellationToken ct = default) =>
        _reads.GetBookAsync(bookId, ct);

    public Task<LibraryResolveResult> ResolveBookAsync(
        LibraryResolveBookRequest request,
        CancellationToken ct = default) =>
        _reads.ResolveBookAsync(request, ct);

    public Task<LibraryCommandResultDto> ListCollectionsAsync(CancellationToken ct = default) =>
        _reads.ListCollectionsAsync(ct);

    public Task<LibraryCommandResultDto> ListCollectionCountsAsync(CancellationToken ct = default) =>
        _reads.ListCollectionCountsAsync(ct);

    public Task<LibraryCommandResultDto> GetCollectionAsync(
        Guid collectionId,
        CancellationToken ct = default) =>
        _reads.GetCollectionAsync(collectionId, ct);

    // ------------------------------------------------------------------
    // Book commands (exact-once)
    // ------------------------------------------------------------------

    public Task<LibraryCommandResultDto> CreateOrMatchBookAsync(
        LibraryCreateBookRequest request,
        bool strictConfirmation,
        CancellationToken ct = default) =>
        _books.CreateOrMatchAsync(request, strictConfirmation, ct);

    public Task<LibraryCommandResultDto> UpdateBookAsync(
        LibraryUpdateBookRequest request,
        CancellationToken ct = default) =>
        _books.UpdateAsync(request, ct);

    public Task<LibraryCommandResultDto> AttachAcquiredAssetAsync(
        LibraryAttachAcquiredAssetRequest request,
        CancellationToken ct = default) =>
        _books.AttachAcquiredAssetAsync(request, ct);

    public Task<LibraryCommandResultDto> DeleteBookAsync(Guid bookId, CancellationToken ct = default) =>
        _books.DeleteAsync(bookId, ct);

    // ------------------------------------------------------------------
    // Reading progress and status (last-write-wins; same gate as commands)
    // ------------------------------------------------------------------

    public Task<LibraryCommandResultDto> UpdateProgressAsync(
        Guid bookId,
        string location,
        int percentage,
        double? playbackRate = null,
        CancellationToken ct = default) =>
        _progress.UpdateProgressAsync(bookId, location, percentage, playbackRate, ct);

    public Task<LibraryCommandResultDto> ResetProgressAsync(
        Guid bookId,
        bool allEditions = false,
        CancellationToken ct = default) =>
        _progress.ResetProgressAsync(bookId, allEditions, ct);

    public Task<LibraryCommandResultDto> SetBookStatusAsync(
        Guid bookId,
        BookStatus status,
        string? statusMessage = null,
        CancellationToken ct = default) =>
        _progress.SetBookStatusAsync(bookId, status, statusMessage, ct);

    // ------------------------------------------------------------------
    // Edition grouping
    // ------------------------------------------------------------------

    public Task<LibraryCommandResultDto> LinkWorkAsync(
        LibraryLinkWorkRequest request,
        CancellationToken ct = default) =>
        _works.LinkAsync(request, ct);

    public Task<LibraryCommandResultDto> UnlinkWorkAsync(
        LibraryUnlinkWorkRequest request,
        CancellationToken ct = default) =>
        _works.UnlinkAsync(request, ct);

    // ------------------------------------------------------------------
    // Collections
    // ------------------------------------------------------------------

    public Task<LibraryCommandResultDto> CreateCollectionAsync(
        LibraryCreateCollectionRequest request,
        CancellationToken ct = default) =>
        _collections.CreateAsync(request, ct);

    public Task<LibraryCommandResultDto> RenameCollectionAsync(
        LibraryRenameCollectionRequest request,
        CancellationToken ct = default) =>
        _collections.RenameAsync(request, ct);

    public Task<LibraryCommandResultDto> MoveCollectionAsync(
        LibraryMoveCollectionRequest request,
        CancellationToken ct = default) =>
        _collections.MoveAsync(request, ct);

    public Task<LibraryCommandResultDto> UpdateCollectionAsync(
        string clientId,
        string idempotencyKey,
        Guid collectionId,
        string name,
        Guid? parentId,
        CancellationToken ct = default) =>
        _collections.UpdateAsync(clientId, idempotencyKey, collectionId, name, parentId, ct);

    public Task<LibraryCommandResultDto> DeleteCollectionAsync(
        LibraryDeleteCollectionRequest request,
        CancellationToken ct = default) =>
        _collections.DeleteAsync(request, ct);

    public Task<LibraryCommandResultDto> ApplyBookCollectionChangesAsync(
        Guid bookId,
        IReadOnlyList<Guid> addCollectionIds,
        IReadOnlyList<Guid> removeCollectionIds,
        CancellationToken ct = default) =>
        _collections.ApplyBookCollectionChangesAsync(bookId, addCollectionIds, removeCollectionIds, ct);

    // ------------------------------------------------------------------
    // Changesets
    // ------------------------------------------------------------------

    public Task<LibraryChangesetResult> PreviewAsync(
        IReadOnlyList<LibraryChangeOp> ops,
        CancellationToken ct = default) =>
        _changesets.PreviewAsync(ops, ct);

    public Task<LibraryChangesetResult> ApplyAsync(
        IReadOnlyList<LibraryChangeOp> ops,
        string? expectedDiffHash,
        CancellationToken ct = default) =>
        _changesets.ApplyAsync(ops, expectedDiffHash, ct);

    public Task<LibraryRevertResult> RevertAsync(
        LibraryUndoSnapshot undo,
        CancellationToken ct = default) =>
        _changesets.RevertAsync(undo, ct);
}
