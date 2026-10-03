using System;
using System.Threading;
using System.Threading.Tasks;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Canonical library domain service. REST endpoints, MCP tools and the UI all
/// call this one service; no surface computes or caches authoritative library
/// state. Mutations are exact-once through LibraryCommandReceipt.
/// </summary>
public interface ILibraryService
{
    // --- Read-only ---
    Task<LibraryCommandResultDto> ListBooksAsync(
        BookFilter filter,
        BookSort sort,
        string? search,
        int page,
        int pageSize,
        Guid? collectionId,
        bool? groupByWork = false,
        string? format = null,
        CancellationToken ct = default);

    Task<LibraryCommandResultDto> GetStatusCountsAsync(CancellationToken ct = default);

    Task<LibraryCommandResultDto> GetBookAsync(Guid bookId, CancellationToken ct = default);

    Task<LibraryResolveResult> ResolveBookAsync(LibraryResolveBookRequest request, CancellationToken ct = default);

    Task<LibraryCommandResultDto> ListCollectionsAsync(CancellationToken ct = default);

    /// <summary>
    /// REST-only descendant-inclusive book counts for every collection
    /// (sidebar). One grouped query for direct counts, then a post-order
    /// rollup in memory; never one query per collection.
    /// </summary>
    Task<LibraryCommandResultDto> ListCollectionCountsAsync(CancellationToken ct = default);

    Task<LibraryCommandResultDto> GetCollectionAsync(Guid collectionId, CancellationToken ct = default);

    // --- Mutations (all exact-once) ---
    /// <summary>
    /// Create or match a book by normalized identity. When
    /// <paramref name="strictConfirmation"/> is true (MCP), ambiguity returns
    /// confirmation_required instead of creating; when false (REST, preserving
    /// legacy UI behavior), ambiguity auto-creates while exact matches still
    /// match.
    /// </summary>
    Task<LibraryCommandResultDto> CreateOrMatchBookAsync(
        LibraryCreateBookRequest request,
        bool strictConfirmation,
        CancellationToken ct = default);

    /// <summary>
    /// Records that a book's file has been written to storage by the acquisition
    /// layer, together with where it came from.
    ///
    /// One receipt-guarded, transactional mutation that sets the file details and
    /// inserts the provenance row: doing it in two calls, or by writing through a
    /// repository, is exactly how a book ends up with a file nobody can trace or
    /// provenance pointing at a file that was never stored.
    /// </summary>
    Task<LibraryCommandResultDto> AttachAcquiredAssetAsync(
        LibraryAttachAcquiredAssetRequest request,
        CancellationToken ct = default);

    Task<LibraryCommandResultDto> UpdateBookAsync(LibraryUpdateBookRequest request, CancellationToken ct = default);

    /// <summary>
    /// Manual multi-edition override: merge <c>BookId</c>'s work into
    /// <c>TargetBookId</c>'s work, so both books (and everything already
    /// grouped with either of them) become editions of one work. Metadata is
    /// deliberately NOT consulted — differing title/author is the case this
    /// exists for.
    ///
    /// Rejects a self-link. A request whose two books already share a work —
    /// or that names the same pair twice in either order — succeeds as a
    /// no-op with no stateVersion bump. The target's work survives; the
    /// source work is deleted when the merge empties it.
    /// </summary>
    Task<LibraryCommandResultDto> LinkWorkAsync(LibraryLinkWorkRequest request, CancellationToken ct = default);

    /// <summary>
    /// Manual multi-edition override: move <c>BookId</c> out of its work into
    /// a NEW work carrying that book's own current title/author identity. The
    /// book always ends up with a valid work — never a null or empty WorkId.
    /// A book already alone in its work is a successful no-op.
    /// </summary>
    Task<LibraryCommandResultDto> UnlinkWorkAsync(LibraryUnlinkWorkRequest request, CancellationToken ct = default);

    /// <summary>
    /// High-frequency progress update. Deliberately NOT receipt-guarded
    /// (last-write-wins, idempotent by nature); validates 0..100 and keeps
    /// FinishedAt aligned with the percentage.
    /// </summary>
    Task<LibraryCommandResultDto> UpdateProgressAsync(Guid bookId, string location, int percentage, CancellationToken ct = default);

    /// <summary>
    /// Resets a book's reading progress to the canonical "not started" state
    /// (LastLocation null, ProgressPercent 0, FinishedAt null, LastReadAt
    /// null). Explicit reset intent — deliberately NOT a 0% progress update,
    /// which would set LastReadAt to now and make the book look recently
    /// read. Like UpdateProgressAsync, deliberately NOT receipt-guarded
    /// (last-write-wins, idempotent by nature). An already-reset book
    /// succeeds as a no-op with no state-version change.
    /// </summary>
    Task<LibraryCommandResultDto> ResetProgressAsync(Guid bookId, CancellationToken ct = default);

    /// <summary>
    /// Deletes the library row first (reading/notes FKs reject in-use books
    /// with book_in_use before any file is touched); the caller removes
    /// storage files only after this succeeds.
    /// </summary>
    Task<LibraryCommandResultDto> DeleteBookAsync(Guid bookId, CancellationToken ct = default);

    /// <summary>
    /// Update the life-cycle status of an in-flight or completed book import.
    /// High-frequency / internal milestone update, not receipt-guarded.
    /// </summary>
    Task<LibraryCommandResultDto> SetBookStatusAsync(
        Guid bookId,
        BookStatus status,
        string? statusMessage = null,
        CancellationToken ct = default);

    Task<LibraryCommandResultDto> CreateCollectionAsync(LibraryCreateCollectionRequest request, CancellationToken ct = default);

    Task<LibraryCommandResultDto> RenameCollectionAsync(LibraryRenameCollectionRequest request, CancellationToken ct = default);

    Task<LibraryCommandResultDto> MoveCollectionAsync(LibraryMoveCollectionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Atomic full-replacement update (collections Phase 1): renames and/or
    /// moves a collection in ONE transaction with ONE receipt and at most
    /// ONE stateVersion bump. Missing/invalid name, invalid parent, cycles
    /// and normalized sibling collisions reject the whole update. When name
    /// and parent are both unchanged the operation succeeds as a no-op
    /// (receipt written, stateVersion untouched).
    /// </summary>
    Task<LibraryCommandResultDto> UpdateCollectionAsync(
        string clientId,
        string idempotencyKey,
        Guid collectionId,
        string name,
        Guid? parentId,
        CancellationToken ct = default);

    Task<LibraryCommandResultDto> DeleteCollectionAsync(LibraryDeleteCollectionRequest request, CancellationToken ct = default);
}
