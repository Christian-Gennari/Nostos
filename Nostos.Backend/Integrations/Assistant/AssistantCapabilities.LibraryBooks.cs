using System.Text.Json;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Integrations.Assistant;

public static partial class AssistantCapabilities
{
    private static IReadOnlyList<AssistantCapability> BuildLibraryBookCapabilities(ILibraryService library) =>
    [
        // ------------------------------------------------------------------
        // Read (Suggest): proposals/reads only, structurally unable to mutate.
        // ------------------------------------------------------------------

        new AssistantCapability(
            "library_resolve_book",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.LibraryRead,
            "Finds a book by ISBN, ASIN, title, or author without changing the library.",
            """
            {
              "type": "object",
              "properties": {
                "isbn": { "type": "string", "description": "The book's ISBN, when the user gave one. Leave it out when you do not know it." },
                "asin": { "type": "string", "description": "The book's ASIN, when the user gave one. Leave it out when you do not know it." },
                "title": { "type": "string", "description": "The book's title as the user said it. Use it to resolve the book by title." },
                "author": { "type": "string", "description": "The book's author as the user said it. Combine it with a title to narrow the match." },
                "includeExternalMetadata": { "type": "boolean", "description": "Set false to resolve only against the local library and skip external metadata lookups." }
              },
              "required": [],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var request = new LibraryResolveBookRequest(
                    Isbn: Str(args, "isbn"),
                    Asin: Str(args, "asin"),
                    Title: Str(args, "title"),
                    Author: Str(args, "author"),
                    IncludeExternalMetadata: Bool(args, "includeExternalMetadata") ?? true);

                var result = await library.ResolveBookAsync(request, ct);
                return AssistantToolResult.Ok(Element(result));
            }),

        new AssistantCapability(
            "library_list_books",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.LibraryRead,
            "Lists books with optional filter, sort, search, and collection restriction.",
            """
            {
              "type": "object",
              "properties": {
                "search": { "type": "string", "description": "Words to match against book titles and authors. Omit it to list everything." },
                "filter": { "type": "string", "enum": ["All", "Favorites", "Finished", "Reading", "Unsorted", "NotStarted"], "description": "Restrict the list to one shelf. Defaults to All." },
                "sort": { "type": "string", "enum": ["Recent", "Title", "Rating", "LastRead"], "description": "How to order the results. Defaults to Recent." },
                "page": { "type": "integer", "description": "The 1-based page of results to return. Defaults to 1." },
                "pageSize": { "type": "integer", "description": "How many books to return per page. Defaults to 20; the service clamps it to 100." },
                "collectionId": { "type": "string", "format": "uuid", "description": "Limit the list to one collection by its id. Omit it for the whole library." },
                "format": { "type": "string", "enum": ["audiobook", "ebook", "pdf"], "description": "Use the Library's built-in format filter. Omit it for all formats." }
              },
              "required": [],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var result = await library.ListBooksAsync(
                    filter: ValueEnum(args, "filter", BookFilter.All),
                    sort: ValueEnum(args, "sort", BookSort.Recent),
                    search: Str(args, "search"),
                    page: Num(args, "page") ?? 1,
                    pageSize: Num(args, "pageSize") ?? 20,
                    collectionId: Id(args, "collectionId"),
                    format: Str(args, "format"),
                    ct: ct);

                return LibraryResult(result);
            }),

        new AssistantCapability(
            "library_get_book",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.LibraryRead,
            "Gets one book and its current metadata and collection memberships by id.",
            """
            {
              "type": "object",
              "properties": {
                "bookId": { "type": "string", "format": "uuid", "description": "The id of the book to fetch. Required." }
              },
              "required": ["bookId"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "bookId") is not { } bookId)
                {
                    return Invalid("'bookId' is required.");
                }

                var result = await library.GetBookAsync(bookId, ct);
                return LibraryResult(result);
            }),

        new AssistantCapability(
            "library_overview",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.LibraryRead,
            "Returns a compact, complete overview of the user's books and collections for whole-library organization, recommendation, or structure questions. Prefer this over generic advice when the user asks about their library as a whole.",
            """
            {
              "type": "object",
              "properties": {},
              "required": [],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                const int pageSize = 100;
                var page = 1;
                var totalCount = 0;
                var books = new List<object>();

                while (true)
                {
                    var result = await library.ListBooksAsync(
                        filter: BookFilter.All,
                        sort: BookSort.Title,
                        search: null,
                        page: page,
                        pageSize: pageSize,
                        collectionId: null,
                        ct: ct);

                    if (result.Data is not PaginatedResponse<BookDto> batch)
                    {
                        return AssistantToolResult.Fail(
                            AssistantErrorCodes.NotFound,
                            "The library overview could not read the book list.");
                    }

                    var items = batch.Items.ToList();
                    totalCount = batch.TotalCount;
                    books.AddRange(items.Select(book => (object)new
                    {
                        book.Id,
                        book.Title,
                        book.Author,
                        book.Type,
                        book.CollectionIds,
                    }));

                    if (books.Count >= totalCount || items.Count == 0)
                    {
                        break;
                    }

                    page++;
                }

                var collections = await library.ListCollectionsAsync(ct);
                var counts = await library.GetStatusCountsAsync(ct);

                return AssistantToolResult.Ok(Element(new
                {
                    totalBooks = totalCount,
                    books,
                    collections = collections.Data,
                    statusAndFormatCounts = counts.Data,
                }));
            }),

        // ------------------------------------------------------------------
        // Act: ordinary, explicitly requested application work. These calls
        // execute immediately so the next model step can consume their result.
        // Canonical services still own validation and exact-once semantics.
        // ------------------------------------------------------------------

        new AssistantCapability(
            "library_create_or_match_book",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.OrdinaryAction,
            "Adds a book to the library or matches an existing one by canonical identity. Ambiguity is returned as confirmation_required; never guess between candidates.",
            """
            {
              "type": "object",
              "properties": {
                "type": { "type": "string", "enum": ["physical", "ebook", "audiobook"], "description": "Book type. Required." },
                "title": { "type": "string", "description": "Book title. Required." },
                "author": { "type": "string", "description": "Author when known." },
                "isbn": { "type": "string", "description": "ISBN when known; formatting is normalized by the library service." },
                "asin": { "type": "string", "description": "ASIN when known, especially for audiobooks." },
                "collectionIds": { "type": "array", "items": { "type": "string", "format": "uuid" }, "description": "Initial collection memberships. Omit to leave the book uncollected." },
                "rating": { "type": "integer", "description": "Optional rating from 0 to 5." },
                "isFavorite": { "type": "boolean", "description": "Optional favorite flag." },
                "confirmedBookId": { "type": "string", "format": "uuid", "description": "After confirmation_required, choose an existing candidate by id on a later user turn." },
                "forceCreate": { "type": "boolean", "description": "After confirmation_required, create anyway only when the user explicitly chose that outcome." }
              },
              "required": ["type", "title"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var type = Str(args, "type");
                var title = Str(args, "title");
                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(title))
                {
                    return Invalid("'type' and 'title' are required.");
                }

                if (!TryIds(args, "collectionIds", out var collectionIds))
                {
                    return Invalid("'collectionIds' must be an array of UUID strings.");
                }

                var request = new LibraryCreateBookRequest(
                    ClientId: context.ClientId ?? string.Empty,
                    IdempotencyKey: context.IdempotencyKey ?? string.Empty,
                    Type: type,
                    Title: title,
                    Author: Str(args, "author"),
                    Isbn: Str(args, "isbn"),
                    Asin: Str(args, "asin"),
                    Rating: Num(args, "rating") ?? 0,
                    IsFavorite: Bool(args, "isFavorite") ?? false,
                    ConfirmedBookId: Id(args, "confirmedBookId"),
                    ForceCreate: Bool(args, "forceCreate") ?? false,
                    CollectionIds: collectionIds);

                var result = await library.CreateOrMatchBookAsync(request, strictConfirmation: true, ct);
                return LibraryResult(result);
            }),

        new AssistantCapability(
            "library_update_book",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.OrdinaryAction,
            "Updates an existing book through the canonical library service. collectionIds is a full replacement set: read the book first and preserve memberships the user did not ask to remove.",
            """
            {
              "type": "object",
              "properties": {
                "bookId": { "type": "string", "format": "uuid", "description": "Book id. Required." },
                "title": { "type": "string", "description": "New title; omit to leave unchanged." },
                "author": { "type": "string", "description": "New author; omit to leave unchanged." },
                "collectionIds": { "type": "array", "items": { "type": "string", "format": "uuid" }, "description": "Full replacement membership set. Empty clears all memberships; omit to leave memberships unchanged." },
                "rating": { "type": "integer", "description": "New rating from 0 to 5; omit to leave unchanged." },
                "isFavorite": { "type": "boolean", "description": "New favorite flag; omit to leave unchanged." },
                "personalReview": { "type": "string", "description": "New personal review; empty string clears it." },
                "isFinished": { "type": "boolean", "description": "True marks finished; false clears the finished state." }
              },
              "required": ["bookId"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "bookId") is not { } bookId)
                {
                    return Invalid("'bookId' is required.");
                }

                if (!TryIds(args, "collectionIds", out var collectionIds))
                {
                    return Invalid("'collectionIds' must be an array of UUID strings.");
                }

                var request = new LibraryUpdateBookRequest(
                    ClientId: context.ClientId ?? string.Empty,
                    IdempotencyKey: context.IdempotencyKey ?? string.Empty,
                    BookId: bookId,
                    Title: Str(args, "title"),
                    Author: Str(args, "author"),
                    Rating: Num(args, "rating"),
                    IsFavorite: Bool(args, "isFavorite"),
                    PersonalReview: Str(args, "personalReview"),
                    IsFinished: Bool(args, "isFinished"),
                    CollectionIds: collectionIds);

                var result = await library.UpdateBookAsync(request, ct);
                return LibraryResult(result);
            }),

        new AssistantCapability(
            "library_set_book_collections_bulk",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.Organization,
            "Replaces collection memberships for multiple books in one bounded action. Use this after inspecting library_overview when reorganizing several books; every item still goes through the canonical library service.",
            """
            {
              "type": "object",
              "properties": {
                "updates": {
                  "type": "array",
                  "description": "One to 100 membership replacements. Each item must contain bookId and the complete collectionIds set that book should have after the change.",
                  "items": {
                    "type": "object",
                    "properties": {
                      "bookId": { "type": "string", "format": "uuid" },
                      "collectionIds": { "type": "array", "items": { "type": "string", "format": "uuid" } }
                    },
                    "required": ["bookId", "collectionIds"]
                  }
                }
              },
              "required": ["updates"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var updates = Property(args, "updates");
                if (updates.ValueKind != JsonValueKind.Array)
                {
                    return Invalid("'updates' must be an array.");
                }

                var items = updates.EnumerateArray().ToList();
                if (items.Count is < 1 or > 100)
                {
                    return Invalid("'updates' must contain between 1 and 100 books.");
                }

                var outcomes = new List<object>(items.Count);
                for (var index = 0; index < items.Count; index++)
                {
                    var item = items[index];
                    if (Id(item, "bookId") is not { } bookId
                        || !TryIds(item, "collectionIds", out var collectionIds)
                        || collectionIds is null)
                    {
                        return Invalid($"updates[{index}] must contain a valid 'bookId' and 'collectionIds' array.");
                    }

                    var result = await library.UpdateBookAsync(
                        new LibraryUpdateBookRequest(
                            ClientId: context.ClientId ?? string.Empty,
                            IdempotencyKey: $"{context.IdempotencyKey}:{index}",
                            BookId: bookId,
                            CollectionIds: collectionIds),
                        ct);

                    var mapped = LibraryResult(result);
                    outcomes.Add(new
                    {
                        bookId,
                        result.Reply,
                        result.Data,
                        result.StateVersion,
                        result.Duplicate,
                    });

                    if (!mapped.Success)
                    {
                        return AssistantToolResult.Fail(
                            mapped.ErrorCode ?? "bulk_update_failed",
                            mapped.ErrorMessage ?? result.Reply,
                            Element(new
                            {
                                applied = index,
                                failedBookId = bookId,
                                outcomes,
                            }));
                    }
                }

                return AssistantToolResult.Ok(Element(new
                {
                    updated = outcomes.Count,
                    outcomes,
                }));
            }),

    ];
}
