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

        // The favourite is the book's, so it filters the same way grouped or not.
        // Reading status is calculated per book (work) when the list is grouped:
        // a finished EPUB with an untouched audiobook is Finished, not Not started.
        // The flat list is a list of editions, so there each edition keeps its own
        // progress filters.
        var byWork = groupByWork == true;
        query = filter switch
        {
            BookFilter.Favorites => query.Where(b => b.Work!.IsFavorite),
            BookFilter.Finished => byWork
                ? WorkReadingStatus.WhereFinished(query, db)
                : query.Where(b => b.Progress.FinishedAt != null),
            BookFilter.Reading => byWork
                ? WorkReadingStatus.WhereReading(query, db)
                : query.Where(b => b.Progress.FinishedAt == null && b.Progress.ProgressPercent > 0),
            BookFilter.NotStarted => byWork
                ? WorkReadingStatus.WhereNotStarted(query, db)
                : query.Where(b => b.Progress.ProgressPercent == 0),
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

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Matched in memory, after the other filters have narrowed the set:
            // SQL LIKE is case-sensitive on PostgreSQL and folds only ASCII on
            // SQLite, and neither forgives a typo. Only id/title/author are read.
            var searchable = await query
                .Select(b => new { b.Id, b.Title, b.Author })
                .ToListAsync(ct);
            var matchedIds = LibrarySearchMatcher
                .Match(search, searchable, b => b.Title, b => b.Author)
                .Select(b => b.Id)
                .ToList();
            query = query.Where(b => matchedIds.Contains(b.Id));
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
            //
            // Grouping, choosing each work's primary edition, sorting and paging
            // need only a few columns, so they run on a narrow projection of the
            // whole filtered set. Full books, with their collections, provenance
            // and sibling editions, are loaded for the one page that is returned:
            // materialising the whole library per page was what made the default
            // (grouped) Library slow on a large library.
            var candidates = await query
                .Select(b => new GroupCandidate(
                    b.Id,
                    b.WorkId,
                    b.Status,
                    b.Title,
                    b.CreatedAt,
                    b.Progress.LastReadAt,
                    b.Progress.ProgressPercent,
                    b.Work!.Rating))
                .ToListAsync(ct);

            var primaries = SortGroupedBooks(
                    candidates.GroupBy(c => c.WorkId).Select(SelectPrimaryEdition),
                    sort)
                .ToList();
            totalCount = primaries.Count;
            var pageIds = primaries
                .Skip((safePage - 1) * safePageSize)
                .Take(safePageSize)
                .Select(p => p.Id)
                .ToList();

            var pageBooks = (await CountableBooks(db)
                    .Where(b => pageIds.Contains(b.Id))
                    .Select(BookReadShapes.WithoutLocations)
                    .ToListAsync(ct))
                .ToDictionary(b => b.Id);

            var pageWorkIds = pageBooks.Values.Select(b => b.WorkId).Distinct().ToList();
            var siblingsByWork = (await CountableBooks(db)
                    .Where(b => pageWorkIds.Contains(b.WorkId))
                    .Select(BookReadShapes.WithoutLocations)
                    .ToListAsync(ct))
                .GroupBy(b => b.WorkId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var dtos = pageIds
                .Where(pageBooks.ContainsKey)
                .Select(id =>
                {
                    var primary = pageBooks[id];
                    var allEditions = siblingsByWork.GetValueOrDefault(primary.WorkId) ?? [primary];
                    return primary.ToDto() with
                    {
                        WorkId = primary.WorkId,
                        EditionCount = allEditions.Count,
                        OtherEditions = allEditions
                            .Where(b => b.Id != primary.Id)
                            .Select(MappingExtensions.ToEditionSummary)
                            .ToList(),
                    };
                })
                .ToList();

            pageResult = new PaginatedResponse<BookDto>(
                dtos,
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
                BookSort.Rating => ImportingFirst(query).ThenByDescending(b => b.Work!.Rating),
                BookSort.LastRead => ImportingFirst(query)
                    .ThenByDescending(b => b.Progress.LastReadAt.HasValue)
                    .ThenByDescending(b => b.Progress.LastReadAt),
                _ => ImportingFirst(query).ThenByDescending(b => b.CreatedAt),
            };

            totalCount = await query.CountAsync(ct);
            var items = await query
                .Skip((safePage - 1) * safePageSize)
                .Take(safePageSize)
                .Select(BookReadShapes.WithoutLocations)
                .ToListAsync(ct);

            var pageWorkIds = items.Select(b => b.WorkId).Distinct().ToList();
            var pageSiblings = await CountableBooks(db)
                .Where(b => pageWorkIds.Contains(b.WorkId))
                .Select(BookReadShapes.WithoutLocations)
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

    /// <summary>
    /// One narrow query for the books a text search covers. Rows and order match
    /// paging <see cref="ListBooksAsync"/> with <c>BookFilter.All</c>, flat, by
    /// title (active imports first, and kept through the collection filter), so
    /// a search sees the same books as before without loading a single full
    /// book DTO, its siblings or its collections.
    /// </summary>
    public async Task<IReadOnlyList<LibraryBookScopeEntry>> ListBookScopeAsync(
        IReadOnlyList<Guid>? bookIds,
        Guid? collectionId,
        int limit,
        CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);

        if (bookIds is { Count: > 0 })
        {
            var wanted = bookIds.Distinct().Take(32).ToList();
            var found = (await db.Books.AsNoTracking()
                    .Where(b => wanted.Contains(b.Id))
                    .Select(b => new LibraryBookScopeEntry(
                        b.Id,
                        b.Title,
                        b.Author,
                        b.FileDetails.FileName,
                        b.FileDetails.HasFile))
                    .ToListAsync(ct))
                .ToDictionary(entry => entry.Id);
            return wanted.Where(found.ContainsKey).Select(id => found[id]).ToList();
        }

        var query = CountableBooks(db);
        if (collectionId.HasValue)
        {
            var collections = await db.Collections.AsNoTracking()
                .Select(c => new CollectionModel { Id = c.Id, ParentId = c.ParentId })
                .ToListAsync(ct);
            var subtreeIds = GetSubtreeIds(collectionId.Value, collections);
            var inCollection = query
                .Where(b => db.BookCollections.Any(bc =>
                    bc.BookId == b.Id && subtreeIds.Contains(bc.CollectionId)))
                .Select(b => b.Id);
            query = CountableBooks(db).Where(b =>
                b.Status == BookStatus.Downloading
                || b.Status == BookStatus.Transcoding
                || inCollection.Contains(b.Id));
        }

        return await query
            .OrderByDescending(b =>
                b.Status == BookStatus.Downloading || b.Status == BookStatus.Transcoding)
            .ThenBy(b => b.Title)
            .ThenBy(b => b.Id)
            .Take(Math.Max(0, limit))
            .Select(b => new LibraryBookScopeEntry(
                b.Id,
                b.Title,
                b.Author,
                b.FileDetails.FileName,
                b.FileDetails.HasFile))
            .ToListAsync(ct);
    }

    public async Task<LibraryCommandResultDto> GetStatusCountsAsync(CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await TryGetStateAsync(db, ct);
        var version = state?.StateVersion ?? "0";

        // Sidebar numbers describe the same unit as the default grouped Library:
        // one count per WorkId. Favourite and reading status are the book's
        // (calculated from all of its editions), so a work with two qualifying
        // editions still occupies one visible card. This is
        // deliberately NOT title/author deduplication; WorkId is the grouping
        // contract and separate works remain separate.
        // One statement instead of nine round trips. EF cannot read the owned
        // FileDetails of a GROUP BY row, so rather than a single-pass
        // COUNT(DISTINCT CASE ...) the nine unchanged per-predicate counts are
        // scalar subqueries of a one-row source. A library with no countable
        // book yields no row, which is all zeros.
        var books = CountableBooks(db);

        var counts = await db.Books.AsNoTracking()
            .Take(1)
            .Select(_ => new LibraryStatusCountsDto(
                books.Select(b => b.WorkId).Distinct().Count(),
                books.Where(WorkReadingStatus.IsNotStarted(db)).Select(b => b.WorkId).Distinct().Count(),
                books.Where(WorkReadingStatus.IsReading(db)).Select(b => b.WorkId).Distinct().Count(),
                books.Where(b => b.Work!.IsFavorite).Select(b => b.WorkId).Distinct().Count(),
                books.Where(WorkReadingStatus.IsFinished(db)).Select(b => b.WorkId).Distinct().Count(),
                books.Where(b => !db.BookCollections.Any(bc => bc.BookId == b.Id))
                    .Select(b => b.WorkId).Distinct().Count(),
                books.OfType<AudioBookModel>().Select(b => b.WorkId).Distinct().Count(),
                books.OfType<EBookModel>()
                    .Where(b => b.FileDetails.FileName == null || !EF.Functions.Like(b.FileDetails.FileName, "%.pdf"))
                    .Select(b => b.WorkId).Distinct().Count(),
                books.Where(b => b.FileDetails.FileName != null && EF.Functions.Like(b.FileDetails.FileName, "%.pdf"))
                    .Select(b => b.WorkId).Distinct().Count()))
            .FirstOrDefaultAsync(ct)
            ?? new LibraryStatusCountsDto(0, 0, 0, 0, 0, 0, 0, 0, 0);

        return Result(LibraryReplyFormatter.Success("Status counts retrieved."), counts, version);
    }

    public async Task<LibraryCommandResultDto> GetBookAsync(Guid bookId, CancellationToken ct = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(ct);
        var state = await TryGetStateAsync(db, ct);
        var version = state?.StateVersion ?? "0";

        var book = await db.Books.AsNoTracking()
            .Where(b => b.Id == bookId)
            .Select(BookReadShapes.WithoutLocations)
            .SingleOrDefaultAsync(ct);
        if (book is null)
            return Failure("book_not_found", LibraryReplyFormatter.BookNotFound, version);

        var siblings = await db.Books.AsNoTracking()
            .Where(b => b.WorkId == book.WorkId)
            .Select(BookReadShapes.WithoutLocations)
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
            isbnMatch = await db.Books.AsNoTracking()
                .Where(b => b.NormalizedIsbn == nIsbn)
                .Select(BookReadShapes.WithoutLocations)
                .SingleOrDefaultAsync(ct);
        if (nAsin is not null)
            asinMatch = await db.Books.AsNoTracking()
                .Where(b => b.NormalizedAsin == nAsin)
                .Select(BookReadShapes.WithoutLocations)
                .SingleOrDefaultAsync(ct);

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
                .Select(BookReadShapes.WithoutLocations)
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

    /// <summary>The columns that group, rank and sort a book in the grouped list.</summary>
    private sealed record GroupCandidate(
        Guid Id,
        Guid WorkId,
        BookStatus Status,
        string Title,
        DateTime CreatedAt,
        DateTime? LastReadAt,
        int ProgressPercent,
        int Rating);

    /// <summary>
    /// The edition that stands for a work in the list.
    ///
    /// An edition that is importing comes first: the user asked for that edition and
    /// is watching it arrive, so the work's card has to be the thing they asked for —
    /// otherwise an audiobook imported alongside a print copy of the same work would
    /// be represented by the print copy's card, with nothing on it to watch. Reading
    /// history decides again the moment the import ends.
    /// </summary>
    private static GroupCandidate SelectPrimaryEdition(IEnumerable<GroupCandidate> editions) => editions
        .OrderByDescending(b => b.Status == BookStatus.Downloading || b.Status == BookStatus.Transcoding)
        .ThenByDescending(b => b.LastReadAt.HasValue)
        .ThenByDescending(b => b.LastReadAt)
        .ThenByDescending(b => b.ProgressPercent)
        .ThenBy(b => b.CreatedAt)
        .ThenBy(b => b.Id)
        .First();

    private static IEnumerable<GroupCandidate> SortGroupedBooks(
        IEnumerable<GroupCandidate> books,
        BookSort sort) => sort switch
        {
            BookSort.Title => ImportingFirst(books).ThenBy(b => b.Title).ThenBy(b => b.Id),
            BookSort.Rating => ImportingFirst(books).ThenByDescending(b => b.Rating).ThenBy(b => b.Id),
            BookSort.LastRead => ImportingFirst(books)
                .ThenByDescending(b => b.LastReadAt.HasValue)
                .ThenByDescending(b => b.LastReadAt)
                .ThenBy(b => b.Id),
            _ => ImportingFirst(books).ThenByDescending(b => b.CreatedAt).ThenBy(b => b.Id),
        };

    /// <summary>
    /// A work counts as importing when the edition representing it is. Same reason as
    /// the ungrouped path: an edition that exists because the user just asked for it
    /// has no reading history to sort on, so every user-facing sort would bury it
    /// behind the books they have actually read.
    /// </summary>
    private static IOrderedEnumerable<GroupCandidate> ImportingFirst(IEnumerable<GroupCandidate> books) =>
        books.OrderByDescending(b =>
            b.Status == BookStatus.Downloading || b.Status == BookStatus.Transcoding);

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
