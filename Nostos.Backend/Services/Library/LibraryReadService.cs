using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// Read-only library operations. Keeping queries separate from mutation and
/// receipt plumbing makes the canonical LibraryService a small facade over
/// focused domain responsibilities.
/// </summary>
internal sealed class LibraryReadService(
    IDbContextFactory<NostosDbContext> contexts,
    BookLookupService lookup)
{
    private readonly IDbContextFactory<NostosDbContext> _contexts = contexts;
    private readonly BookLookupService _lookup = lookup;

    public async Task<LibraryCommandResultDto> ListBooksAsync(
        BookFilter filter,
        BookSort sort,
        string? search,
        int page,
        int pageSize,
        Guid? collectionId,
        bool? groupByWork = false,
        string? format = null,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await TryGetStateAsync(db, ct);
        var version = state?.StateVersion ?? "0";

        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);

        // The default Library is a grouped-work read model. Rows explicitly
        // waiting for a required local upload are not library items yet; a
        // Ready row with no file remains valid (metadata-only digital/physical).
        var query = CountableBooks(db);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search}%";
            query = query.Where(b =>
                EF.Functions.Like(b.Title, term) || EF.Functions.Like(b.Author, term));
        }

        if (!string.IsNullOrWhiteSpace(format))
        {
            switch (format.Trim().ToLowerInvariant())
            {
                case "audiobook":
                case "audio":
                    query = query.Where(b => b is AudioBookModel);
                    break;
                case "pdf":
                    query = query.Where(b => b.FileDetails.FileName != null && EF.Functions.Like(b.FileDetails.FileName, "%.pdf"));
                    break;
                case "ebook":
                case "epub":
                    query = query.Where(b => b is EBookModel && (b.FileDetails.FileName == null || !EF.Functions.Like(b.FileDetails.FileName, "%.pdf")));
                    break;
                case "physical":
                    query = query.Where(b => b is PhysicalBookModel);
                    break;
            }
        }

        query = filter switch
        {
            BookFilter.Favorites => query.Where(b => b.Progress.IsFavorite),
            BookFilter.Finished => query.Where(b => b.Progress.FinishedAt != null),
            BookFilter.Reading => query.Where(b => b.Progress.FinishedAt == null && b.Progress.ProgressPercent > 0),
            BookFilter.NotStarted => query.Where(b => b.Progress.ProgressPercent == 0),
            BookFilter.Unsorted => query.Where(b =>
                !db.BookCollections.Any(bc => bc.BookId == b.Id)),
            _ => query,
        };

        if (collectionId.HasValue)
        {
            // Recursive filter (collections Phase 1): selecting a parent
            // collection includes books assigned to ANY descendant. The
            // subtree is expanded in memory from the flat id/parent list
            // (no SQL CTE); the small HashSet translates to IN (...).
            var collections = await db.Collections.AsNoTracking()
                .Select(c => new CollectionModel { Id = c.Id, ParentId = c.ParentId })
                .ToListAsync(ct);
            var subtreeIds = GetSubtreeIds(collectionId.Value, collections);

            // A book matches when ANY of its memberships is in the subtree.
            query = query.Where(b =>
                db.BookCollections.Any(bc => bc.BookId == b.Id && subtreeIds.Contains(bc.CollectionId)));
        }

        // The book row is deliberately the only in-progress import surface.
        // Apply ordinary Library filters first, then restore active imports once at
        // this boundary so search/status/format/collection (and future filters)
        // cannot remove the Downloading/Transcoding card the user is watching.
        // When the status becomes Ready/Failed the row immediately returns to the
        // ordinary filtered result semantics.
        var filteredBookIds = query.Select(b => b.Id);
        query = CountableBooks(db).Where(b =>
            b.Status == BookStatus.Downloading
            || b.Status == BookStatus.Transcoding
            || filteredBookIds.Contains(b.Id));

        PaginatedResponse<BookDto> pageResult;
        int totalCount;
        if (groupByWork == true)
        {
            // Group after all book-level filters have been applied, but before
            // pagination. A work therefore occupies one page slot while the
            // primary still reflects the filtered result (for example, a
            // favorites query can select a favorite edition).
            var candidates = await query
                .Include(b => b.Work)
                .Include(b => b.BookCollections)
                .Include(b => b.Acquisition)
                .ToListAsync(ct);

            var candidateWorkIds = candidates.Select(c => c.WorkId).Distinct().ToList();
            var siblingBooks = await CountableBooks(db)
                .Include(b => b.Acquisition)
                .Where(b => candidateWorkIds.Contains(b.WorkId))
                .ToListAsync(ct);
            var siblingsByWork = siblingBooks
                .GroupBy(b => b.WorkId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var grouped = candidates
                .GroupBy(b => b.WorkId)
                .Select(g =>
                {
                    var primary = SelectPrimaryEdition(g);
                    var allEditions = siblingsByWork.GetValueOrDefault(primary.WorkId) ?? g.ToList();
                    var dto = primary.ToDto() with
                    {
                        WorkId = primary.WorkId,
                        EditionCount = allEditions.Count,
                        OtherEditions = allEditions
                            .Where(b => b.Id != primary.Id)
                            .Select(MappingExtensions.ToEditionSummary)
                            .ToList(),
                    };
                    return new GroupedBook(primary, dto);
                })
                .ToList();

            grouped = SortGroupedBooks(grouped, sort).ToList();
            totalCount = grouped.Count;
            pageResult = new PaginatedResponse<BookDto>(
                grouped
                    .Skip((safePage - 1) * safePageSize)
                    .Take(safePageSize)
                    .Select(g => g.Dto),
                totalCount,
                safePage,
                safePageSize);
        }
        else
        {
            // Imports sort FIRST, whatever the sort key is. The boundary above also
            // keeps them in the result through ordinary filters, so their own card
            // remains the single progress surface until the import settles.
            IOrderedQueryable<BookModel> ImportingFirst(IQueryable<BookModel> source) =>
                source.OrderByDescending(b =>
                    b.Status == BookStatus.Downloading || b.Status == BookStatus.Transcoding);

            query = sort switch
            {
                BookSort.Title => ImportingFirst(query).ThenBy(b => b.Title),
                BookSort.Rating => ImportingFirst(query).ThenByDescending(b => b.Progress.Rating),
                BookSort.LastRead => ImportingFirst(query)
                    .ThenByDescending(b => b.Progress.LastReadAt.HasValue)
                    .ThenByDescending(b => b.Progress.LastReadAt),
                _ => ImportingFirst(query).ThenByDescending(b => b.CreatedAt),
            };

            totalCount = await query.CountAsync(ct);
            var items = await query
                .Include(b => b.Work)
                .Include(b => b.BookCollections)
                .Include(b => b.Acquisition)
                .Skip((safePage - 1) * safePageSize)
                .Take(safePageSize)
                .ToListAsync(ct);

            var pageWorkIds = items.Select(b => b.WorkId).Distinct().ToList();
            var pageSiblings = await CountableBooks(db)
                .Include(b => b.BookCollections)
                .Include(b => b.Acquisition)
                .Where(b => pageWorkIds.Contains(b.WorkId))
                .ToListAsync(ct);
            var pageSiblingsByWork = pageSiblings
                .GroupBy(b => b.WorkId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var dtoList = items.Select(b =>
            {
                var siblings = pageSiblingsByWork.GetValueOrDefault(b.WorkId) ?? [b];
                return b.ToDto() with
                {
                    WorkId = b.WorkId,
                    EditionCount = siblings.Count,
                    OtherEditions = siblings
                        .Where(s => s.Id != b.Id)
                        .Select(MappingExtensions.ToEditionSummary)
                        .ToList()
                };
            }).ToList();

            pageResult = new PaginatedResponse<BookDto>(
                dtoList,
                totalCount,
                safePage,
                safePageSize);
        }

        return Result(LibraryReplyFormatter.BookList(totalCount), pageResult, version);
    }

    public async Task<LibraryCommandResultDto> GetStatusCountsAsync(CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await TryGetStateAsync(db, ct);
        var version = state?.StateVersion ?? "0";

        // Sidebar numbers describe the same unit as the default grouped Library:
        // one count per WorkId after applying the book-level predicate. A work
        // with two qualifying editions still occupies one visible card. This is
        // deliberately NOT title/author deduplication; WorkId is the grouping
        // contract and separate works remain separate.
        var books = CountableBooks(db);
        Task<int> CountWorksAsync(IQueryable<BookModel> source) =>
            source.Select(b => b.WorkId).Distinct().CountAsync(ct);

        var all = await CountWorksAsync(books);
        var notStarted = await CountWorksAsync(books.Where(b => b.Progress.ProgressPercent == 0));
        var reading = await CountWorksAsync(books.Where(b => b.Progress.FinishedAt == null && b.Progress.ProgressPercent > 0));
        var favorites = await CountWorksAsync(books.Where(b => b.Progress.IsFavorite));
        var finished = await CountWorksAsync(books.Where(b => b.Progress.FinishedAt != null));
        var unsorted = await CountWorksAsync(books.Where(b =>
            !db.BookCollections.Any(bc => bc.BookId == b.Id)));
        var audiobooks = await CountWorksAsync(books.OfType<AudioBookModel>());
        var pdfs = await CountWorksAsync(books.Where(
            b => b.FileDetails.FileName != null && EF.Functions.Like(b.FileDetails.FileName, "%.pdf")));
        var ebooks = await CountWorksAsync(books.OfType<EBookModel>().Where(
            b => b.FileDetails.FileName == null || !EF.Functions.Like(b.FileDetails.FileName, "%.pdf")));

        var counts = new LibraryStatusCountsDto(
            all, notStarted, reading, favorites, finished, unsorted, audiobooks, ebooks, pdfs);

        return Result(LibraryReplyFormatter.Success("Status counts retrieved."), counts, version);
    }

    public async Task<LibraryCommandResultDto> GetBookAsync(Guid bookId, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await TryGetStateAsync(db, ct);
        var version = state?.StateVersion ?? "0";

        var book = await db.Books.AsNoTracking()
            .Include(b => b.BookCollections)
            // Without this the DTO's Source is always null, so an imported book
            // would never show where it came from.
            .Include(b => b.Acquisition)
            .SingleOrDefaultAsync(b => b.Id == bookId, ct);
        if (book is null)
            return Failure("book_not_found", LibraryReplyFormatter.BookNotFound, version);

        var siblings = await db.Books.AsNoTracking()
            .Include(b => b.BookCollections)
            .Where(b => b.WorkId == book.WorkId)
            .ToListAsync(ct);

        var dto = book.ToDto() with
        {
            WorkId = book.WorkId,
            EditionCount = siblings.Count,
            OtherEditions = siblings
                .Where(s => s.Id != book.Id)
                .Select(MappingExtensions.ToEditionSummary)
                .ToList()
        };

        return Result(LibraryReplyFormatter.Book(book.Title), dto, version);
    }

    public async Task<LibraryResolveResult> ResolveBookAsync(LibraryResolveBookRequest request, CancellationToken ct = default)
    {
        var nIsbn = BookIdentityNormalizer.NormalizeIsbn(request.Isbn);
        var nAsin = BookIdentityNormalizer.NormalizeAsin(request.Asin);
        var nTitle = BookIdentityNormalizer.NormalizeTitle(request.Title);
        var nAuthor = BookIdentityNormalizer.NormalizeAuthor(request.Author);

        await using var db = await _contexts.CreateDbContextAsync(ct);
        // Read-only: never creates the state row (strict read-only contract).

        // 1. Identifier resolution first (authoritative, indexed).
        BookModel? isbnMatch = null;
        BookModel? asinMatch = null;
        if (nIsbn is not null)
            isbnMatch = await db.Books.AsNoTracking().SingleOrDefaultAsync(b => b.NormalizedIsbn == nIsbn, ct);
        if (nAsin is not null)
            asinMatch = await db.Books.AsNoTracking().SingleOrDefaultAsync(b => b.NormalizedAsin == nAsin, ct);

        if (isbnMatch is not null && asinMatch is not null && isbnMatch.Id != asinMatch.Id)
            return new LibraryResolveResult(LibraryResolution.IdentityConflict);

        if (isbnMatch is not null)
            return new LibraryResolveResult(LibraryResolution.ExactMatch, isbnMatch.ToDto());
        if (asinMatch is not null)
            return new LibraryResolveResult(LibraryResolution.ExactMatch, asinMatch.ToDto());

        // 2. Title/author resolution (in-memory; personal library scale).
        //    Exact matching requires BOTH title and author: a title-only
        //    request yields candidates, never an automatic match.
        if (!string.IsNullOrEmpty(nTitle))
        {
            var all = await db.Books.AsNoTracking()
                .Include(b => b.Acquisition)
                .ToListAsync(ct);
            var hasAuthor = !string.IsNullOrEmpty(nAuthor);
            var exact = hasAuthor
                ? all
                    .Where(b =>
                        string.Equals(BookIdentityNormalizer.NormalizeTitle(b.Title), nTitle, StringComparison.Ordinal) &&
                        string.Equals(BookIdentityNormalizer.NormalizeAuthor(b.Author), nAuthor, StringComparison.Ordinal))
                    .ToList()
                : [];

            if (exact.Count == 1)
                return new LibraryResolveResult(LibraryResolution.ExactMatch, exact[0].ToDto());

            if (exact.Count > 1)
            {
                var candidates = exact
                    .Select(b => ToCandidate(b, "exact title" + (string.IsNullOrEmpty(nAuthor) ? "" : "+author match")))
                    .ToList();
                return new LibraryResolveResult(LibraryResolution.Candidates, Candidates: candidates);
            }

            // Fuzzy: normalized containment on title or author.
            var fuzzy = all
                .Where(b =>
                {
                    var bt = BookIdentityNormalizer.NormalizeTitle(b.Title);
                    var ba = BookIdentityNormalizer.NormalizeAuthor(b.Author);
                    return (!string.IsNullOrEmpty(bt) && bt.Contains(nTitle, StringComparison.Ordinal)) ||
                           (!string.IsNullOrEmpty(nAuthor) && !string.IsNullOrEmpty(ba) &&
                            ba.Contains(nAuthor, StringComparison.Ordinal));
                })
                .Select(b => ToCandidate(b, "fuzzy title/author match"))
                .ToList();

            if (fuzzy.Count > 0)
                return new LibraryResolveResult(LibraryResolution.Candidates, Candidates: fuzzy);
        }

        // 3. Not found locally; prefill from external metadata when an ISBN is
        //    available. External metadata is NEVER treated as membership.
        CreateBookDto? prefill = null;
        string? lookupError = null;
        if (request.IncludeExternalMetadata && nIsbn is not null)
        {
            var outcome = await _lookup.LookupCombinedDetailedAsync(nIsbn, ct);
            prefill = outcome.Metadata;
            if (outcome.Failed)
                lookupError = "lookup_timeout";
        }

        return new LibraryResolveResult(LibraryResolution.NotFound, Prefill: prefill, LookupError: lookupError);
    }

    public async Task<LibraryCommandResultDto> ListCollectionsAsync(CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await TryGetStateAsync(db, ct);
        var version = state?.StateVersion ?? "0";

        var items = await db.Collections.AsNoTracking()
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .ToListAsync(ct);

        return Result(
            LibraryReplyFormatter.CollectionList(items.Count),
            items.Select(c => new CollectionDto(c.Id, c.Name, c.ParentId)).ToList(),
            version);
    }

    public async Task<LibraryCommandResultDto> ListCollectionCountsAsync(CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await TryGetStateAsync(db, ct);
        var version = state?.StateVersion ?? "0";

        // Membership stays per edition, but the Library card is per work.
        // Carry WorkId sets upward rather than summing raw row counts: if two
        // editions of one work sit in the same subtree (or in sibling child
        // collections), their ancestor badge must still say 1.
        var collections = await db.Collections.AsNoTracking()
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id)
            .Select(c => new CollectionModel { Id = c.Id, ParentId = c.ParentId })
            .ToListAsync(ct);

        var memberships = await db.BookCollections.AsNoTracking()
            .Where(bc => bc.Book.Status != BookStatus.UploadPending)
            .Select(bc => new { bc.CollectionId, bc.Book.WorkId })
            .ToListAsync(ct);

        var directWorkIds = memberships
            .GroupBy(x => x.CollectionId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.WorkId).ToHashSet());

        var workIdsByCollection = RollUpDescendantWorkIds(collections, directWorkIds);
        var items = collections
            .Select(c => new CollectionCountDto(c.Id, workIdsByCollection[c.Id].Count))
            .ToList();

        return Result(
            LibraryReplyFormatter.CollectionCountList(items.Count),
            items,
            version);
    }

    public async Task<LibraryCommandResultDto> GetCollectionAsync(Guid collectionId, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await TryGetStateAsync(db, ct);
        var version = state?.StateVersion ?? "0";

        var collection = await db.Collections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == collectionId, ct);
        if (collection is null)
            return Failure("collection_not_found", LibraryReplyFormatter.CollectionNotFound, version);

        return Result(
            LibraryReplyFormatter.Collection(collection.Name),
            new CollectionDto(collection.Id, collection.Name, collection.ParentId),
            version);
    }

    private static async Task<LibraryState?> TryGetStateAsync(NostosDbContext db, CancellationToken ct) =>
        await db.LibraryStates.AsNoTracking().SingleOrDefaultAsync(ct);

    private static LibraryCandidate ToCandidate(BookModel book, string reason) =>
        new(book.Id, book.Title, book.Author,
            book switch
            {
                PhysicalBookModel p => p.Isbn,
                EBookModel e => e.Isbn,
                _ => null,
            },
            book is AudioBookModel a ? a.Asin : null,
            reason);

    private sealed record GroupedBook(BookModel Primary, BookDto Dto);

    /// <summary>
    /// The edition that stands for a work in the list.
    ///
    /// An edition that is importing comes first: the user asked for that edition and
    /// is watching it arrive, so the work's card has to be the thing they asked for —
    /// otherwise an audiobook imported alongside a print copy of the same work would
    /// be represented by the print copy's card, with nothing on it to watch. Reading
    /// history decides again the moment the import ends.
    /// </summary>
    private static BookModel SelectPrimaryEdition(IEnumerable<BookModel> editions) => editions
        .OrderByDescending(b => b.Status == BookStatus.Downloading || b.Status == BookStatus.Transcoding)
        .ThenByDescending(b => b.Progress.LastReadAt.HasValue)
        .ThenByDescending(b => b.Progress.LastReadAt)
        .ThenByDescending(b => b.Progress.ProgressPercent)
        .ThenBy(b => b.CreatedAt)
        .ThenBy(b => b.Id)
        .First();

    private static IEnumerable<GroupedBook> SortGroupedBooks(
        IEnumerable<GroupedBook> books,
        BookSort sort) => sort switch
        {
            BookSort.Title => ImportingFirst(books).ThenBy(b => b.Primary.Title).ThenBy(b => b.Primary.Id),
            BookSort.Rating => ImportingFirst(books).ThenByDescending(b => b.Primary.Progress.Rating).ThenBy(b => b.Primary.Id),
            BookSort.LastRead => ImportingFirst(books)
                .ThenByDescending(b => b.Primary.Progress.LastReadAt.HasValue)
                .ThenByDescending(b => b.Primary.Progress.LastReadAt)
                .ThenBy(b => b.Primary.Id),
            _ => ImportingFirst(books).ThenByDescending(b => b.Primary.CreatedAt).ThenBy(b => b.Primary.Id),
        };

    /// <summary>
    /// A work counts as importing when the edition representing it is. Same reason as
    /// the ungrouped path: an edition that exists because the user just asked for it
    /// has no reading history to sort on, so every user-facing sort would bury it
    /// behind the books they have actually read.
    /// </summary>
    private static IOrderedEnumerable<GroupedBook> ImportingFirst(IEnumerable<GroupedBook> books) =>
        books.OrderByDescending(b =>
            b.Primary.Status == BookStatus.Downloading || b.Primary.Status == BookStatus.Transcoding);

    private static LibraryCommandResultDto Result(string reply, object? data, string version) =>
        new(reply, data, version);

    private static LibraryCommandResultDto Failure(string code, string reply, string version = "0",
        IReadOnlyList<LibraryCandidate>? candidates = null)
    {
        if (candidates is not null)
            return new LibraryCommandResultDto(reply,
                new LibraryConfirmationErrorDto(code, candidates), version);
        return new LibraryCommandResultDto(reply, new LibraryErrorDto(code), version);
    }

    /// <summary>
    /// Expands a collection subtree to the collection id plus every
    /// descendant id, iteratively, from the flat id/parent list (personal
    /// library scale; no recursive SQL needed).
    /// </summary>
    private static HashSet<Guid> GetSubtreeIds(
        Guid rootId,
        IReadOnlyCollection<CollectionModel> collections)
    {
        var childrenByParent = collections
            .Where(c => c.ParentId.HasValue)
            .GroupBy(c => c.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToArray());

        var result = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(rootId);

        while (pending.TryPop(out var id))
        {
            if (!result.Add(id))
                continue;

            if (childrenByParent.TryGetValue(id, out var children))
            {
                foreach (var child in children)
                    pending.Push(child);
            }
        }

        return result;
    }

    private static IQueryable<BookModel> CountableBooks(NostosDbContext db) =>
        db.Books.AsNoTracking().Where(b => b.Status != BookStatus.UploadPending);

    /// <summary>
    /// Rolls direct per-collection WORK membership upward in post-order.
    /// HashSet union is the important semantic: collection membership belongs
    /// to editions, while collection badges mirror grouped Library cards.
    /// </summary>
    private static Dictionary<Guid, HashSet<Guid>> RollUpDescendantWorkIds(
        IReadOnlyCollection<CollectionModel> collections,
        IReadOnlyDictionary<Guid, HashSet<Guid>> directWorkIds)
    {
        var workIds = collections.ToDictionary(
            c => c.Id,
            c => directWorkIds.TryGetValue(c.Id, out var ids)
                ? new HashSet<Guid>(ids)
                : []);
        var childrenByParent = collections
            .Where(c => c.ParentId.HasValue)
            .GroupBy(c => c.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToArray());

        var order = new List<Guid>(collections.Count);
        var expanded = new HashSet<Guid>();
        var visiting = new HashSet<Guid>();
        var stack = new Stack<(Guid Id, bool Expanded)>();

        foreach (var collection in collections)
        {
            if (expanded.Contains(collection.Id))
                continue;
            stack.Push((collection.Id, false));
            while (stack.TryPop(out var item))
            {
                if (item.Expanded)
                {
                    order.Add(item.Id);
                    expanded.Add(item.Id);
                    visiting.Remove(item.Id);
                    continue;
                }
                if (expanded.Contains(item.Id) || visiting.Contains(item.Id))
                    continue;

                visiting.Add(item.Id);
                stack.Push((item.Id, true));
                if (childrenByParent.TryGetValue(item.Id, out var children))
                {
                    foreach (var child in children)
                    {
                        if (!expanded.Contains(child) && !visiting.Contains(child))
                            stack.Push((child, false));
                    }
                }
            }
        }

        foreach (var id in order)
        {
            if (childrenByParent.TryGetValue(id, out var children))
            {
                foreach (var child in children)
                    workIds[id].UnionWith(workIds[child]);
            }
        }

        return workIds;
    }
}
