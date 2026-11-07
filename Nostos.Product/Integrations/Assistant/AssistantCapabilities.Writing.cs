using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.ReadingList;
using Nostos.Backend.Services.Writings;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Studio drafts and the To read list (issue #1468). Reads are
/// <see cref="AssistantTrustClass.Suggest"/>; creating a draft and adding a book
/// to To read are <see cref="AssistantTrustClass.Act"/>. Both creates only ever
/// add: a draft is always a new document, never written over an existing one.
/// Every call goes through the same repository or service the REST endpoints use.
/// </summary>
public static partial class AssistantCapabilities
{
    /// <summary>The Studio address that opens one document.</summary>
    internal static string WritingLink(Guid id) => $"/studio?writingId={id}";

    internal const string ReadingListLink = "/library/to-read";

    private const int MaxWritingsListed = 200;
    private const int MaxWritingContentChars = 60_000;
    private const int MaxWritingNameLength = 200;
    private const int MaxCreatedWritingChars = 200_000;

    private static IReadOnlyList<AssistantCapability> BuildWritingCapabilities(
        IWritingRepository writings) =>
    [
        new AssistantCapability(
            "writings_list",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.Writing,
            "Lists the user's Studio drafts: documents and the folders they sit in, most recently edited first, each with its folder path. A query matches document and folder names only, not what a document says; read a document with writings_read.",
            """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "Optional words that must all appear in the document's name or folder path (\"essay memory\"). Omit to list everything." },
                "type": { "type": "string", "enum": ["Document", "Folder"], "description": "Optional: only documents, or only folders." },
                "limit": { "type": "integer", "minimum": 1, "maximum": 200, "description": "The maximum number of items to return. Defaults to 50." }
              },
              "required": [],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var limit = Num(args, "limit");
                if (limit is < 1 or > MaxWritingsListed)
                    return Invalid($"'limit' must be between 1 and {MaxWritingsListed}.");

                var type = Str(args, "type");
                if (type is not null && !Enum.TryParse<WritingType>(type, ignoreCase: true, out _))
                    return Invalid("'type' must be 'Document' or 'Folder'.");

                // The flat list the Studio tree loads; it never carries content.
                var all = await writings.GetListAsync();
                var paths = WritingPaths(all);
                var terms = (Str(args, "query") ?? string.Empty)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                var matches = all
                    .Where(w => type is null || string.Equals(w.Type, type, StringComparison.OrdinalIgnoreCase))
                    .Where(w => terms.All(term =>
                        paths[w.Id].Contains(term, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(w => w.UpdatedAt)
                    .ThenBy(w => paths[w.Id], StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return AssistantToolResult.Ok(Element(new
                {
                    total = matches.Count,
                    items = matches.Take(limit ?? 50).Select(w => new
                    {
                        id = w.Id,
                        name = w.Name,
                        type = w.Type,
                        parentId = w.ParentId,
                        path = paths[w.Id],
                        updatedAt = w.UpdatedAt,
                        link = IsDocument(w.Type) ? WritingLink(w.Id) : null,
                    }),
                }));
            }),

        new AssistantCapability(
            "writings_read",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.Writing,
            "Reads one Studio document as Markdown, with its folder path and a link that opens it in the Studio. Live citations appear as Markdown links to nostos:cite/… addresses; keep them as they are when quoting the draft back.",
            """
            {
              "type": "object",
              "properties": {
                "writingId": { "type": "string", "format": "uuid", "description": "The id of the document, from writings_list. Required." }
              },
              "required": ["writingId"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                if (Id(args, "writingId") is not { } writingId)
                    return Invalid("'writingId' is required.");

                var writing = await writings.GetByIdAsync(writingId);
                if (writing is null)
                    return AssistantToolResult.Fail(
                        AssistantErrorCodes.NotFound,
                        $"Studio document {writingId} not found.");

                if (writing.Type != WritingType.Document)
                    return Invalid("That is a folder. List its documents with writings_list and read one of them.");

                var paths = WritingPaths(await writings.GetListAsync());
                var content = writing.Content ?? string.Empty;
                var truncated = content.Length > MaxWritingContentChars;

                return AssistantToolResult.Ok(Element(new
                {
                    id = writing.Id,
                    name = writing.Name,
                    parentId = writing.ParentId,
                    path = paths.GetValueOrDefault(writing.Id, writing.Name),
                    updatedAt = writing.UpdatedAt,
                    link = WritingLink(writing.Id),
                    characters = content.Length,
                    truncated,
                    content = truncated ? content[..MaxWritingContentChars] : content,
                }));
            }),

        new AssistantCapability(
            "writings_create",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.Writing,
            "Creates a new Studio document from Markdown when the user asks for a draft to be written down. It always makes a new document and never changes an existing one. Keep citations as Markdown links to nostos:cite/<bookId>?… exactly as tool results gave them, so they stay live in the Studio. Returns the new document's id and a link that opens it.",
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "The document's title, as the user would recognise it in the Studio. Required." },
                "content": { "type": "string", "description": "The document as Markdown. Citations stay Markdown links to their nostos:cite/… address. Omit for an empty document." },
                "folderId": { "type": "string", "format": "uuid", "description": "Optional: the id of an existing Studio folder to create it in, from writings_list. Omit for the top level." }
              },
              "required": ["name"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var name = Str(args, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    return Invalid("'name' is required.");
                if (name.Length > MaxWritingNameLength)
                    return Invalid($"'name' is limited to {MaxWritingNameLength} characters.");

                var content = Str(args, "content") ?? string.Empty;
                if (content.Length > MaxCreatedWritingChars)
                    return Invalid($"'content' is limited to {MaxCreatedWritingChars} characters.");

                var folderValue = Property(args, "folderId");
                Guid? folderId = null;
                if (folderValue.ValueKind is not (System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null))
                {
                    if (Id(args, "folderId") is not { } parsed)
                        return Invalid("'folderId' must be the id of a Studio folder.");

                    var folder = await writings.GetByIdAsync(parsed);
                    if (folder is null)
                        return AssistantToolResult.Fail(
                            AssistantErrorCodes.NotFound,
                            $"Studio folder {parsed} not found.");
                    if (folder.Type != WritingType.Folder)
                        return Invalid("'folderId' names a document, not a folder.");
                    folderId = parsed;
                }

                // Exactly what the Studio does: create the document, then save
                // its content, which rebuilds the notes it cites (issue #1162).
                var writing = new WritingModel
                {
                    Name = name,
                    Type = WritingType.Document,
                    ParentId = folderId,
                    Content = content,
                };
                await writings.AddAsync(writing);
                writing.UpdatedAt = DateTime.UtcNow;
                await writings.SaveContentAsync(writing, CancellationToken.None);

                var paths = WritingPaths(await writings.GetListAsync());
                return AssistantToolResult.Ok(Element(new
                {
                    id = writing.Id,
                    name = writing.Name,
                    parentId = writing.ParentId,
                    path = paths.GetValueOrDefault(writing.Id, writing.Name),
                    link = WritingLink(writing.Id),
                    citations = WritingCitationIndex.Citations(content).Count,
                    characters = content.Length,
                }));
            }),
    ];

    private static IReadOnlyList<AssistantCapability> BuildReadingListCapabilities(
        IReadingListService readingList) =>
    [
        new AssistantCapability(
            "reading_list_list",
            AssistantTrustClass.Suggest,
            AssistantCapabilityCategory.LibraryRead,
            "Lists the user's To read list: books they want to read and do not have yet. \"Next up\" comes first, in the order they chose, then \"Someday\", newest first. An entry with ownedBookId is probably already in the library.",
            """
            {
              "type": "object",
              "properties": {
                "shelf": { "type": "string", "enum": ["all", "next", "someday"], "description": "Optional: only \"next\" (Next up) or only \"someday\". Defaults to all." },
                "limit": { "type": "integer", "minimum": 1, "maximum": 200, "description": "The maximum number of entries to return. Defaults to 50." }
              },
              "required": [],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var limit = Num(args, "limit");
                if (limit is < 1 or > 200)
                    return Invalid("'limit' must be between 1 and 200.");

                var shelf = (Str(args, "shelf") ?? "all").Trim().ToLowerInvariant();
                if (shelf is not ("all" or "next" or "someday"))
                    return Invalid("'shelf' must be 'all', 'next' or 'someday'.");

                var entries = await readingList.ListAsync(ct);
                var chosen = entries
                    .Where(e => shelf == "all"
                        || (shelf == "next") == (e.NextPosition is not null))
                    .ToList();

                return AssistantToolResult.Ok(Element(new
                {
                    total = entries.Count,
                    nextUpCount = entries.Count(e => e.NextPosition is not null),
                    returned = Math.Min(chosen.Count, limit ?? 50),
                    link = ReadingListLink,
                    entries = chosen.Take(limit ?? 50).Select(ReadingListEntry),
                }));
            }),

        new AssistantCapability(
            "reading_list_add",
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.OrdinaryAction,
            "Puts a book on the user's To read list when they ask for it. A book already on the list is not added twice: the existing entry comes back with duplicate=true. Use library_resolve_book first if the user may already own it.",
            """
            {
              "type": "object",
              "properties": {
                "title": { "type": "string", "description": "The book's title. Required." },
                "author": { "type": "string", "description": "The author, when known." },
                "isbn": { "type": "string", "description": "An ISBN-10 or ISBN-13, only when the user or a tool result gave one. Never invent one." },
                "publishedDate": { "type": "string", "description": "The publication year or date, when known." },
                "why": { "type": "string", "description": "The user's own reason for wanting to read it, in their words. Omit when they gave none." },
                "recommendedBy": { "type": "string", "description": "Who or what recommended it, in the user's words." },
                "readNext": { "type": "boolean", "description": "True only when the user wants it on Next up rather than Someday." }
              },
              "required": ["title"],
              "additionalProperties": true
            }
            """,
            async (context, args, ct) =>
            {
                var title = Str(args, "title")?.Trim();
                if (string.IsNullOrWhiteSpace(title))
                    return Invalid("'title' is required.");

                var author = Str(args, "author");
                var isbn = Str(args, "isbn");

                // Idempotent where the list allows: the same book (same ISBN, or
                // the same title by the same author) is never a second entry, so
                // a retried call or a repeated request changes nothing.
                var existing = FindReadingListEntry(await readingList.ListAsync(ct), title, author, isbn);
                if (existing is not null)
                {
                    return AssistantToolResult.Ok(Element(new
                    {
                        duplicate = true,
                        link = ReadingListLink,
                        entry = ReadingListEntry(existing),
                    }));
                }

                var result = await readingList.CreateAsync(
                    new SaveReadingListEntryDto(
                        Title: title,
                        Author: author,
                        Isbn: isbn,
                        PublishedDate: Str(args, "publishedDate"),
                        Why: Str(args, "why"),
                        RecommendedBy: Str(args, "recommendedBy"),
                        ReadNext: Bool(args, "readNext") ?? false),
                    CancellationToken.None);

                return result.Success
                    ? AssistantToolResult.Ok(Element(new
                    {
                        duplicate = false,
                        link = ReadingListLink,
                        entry = ReadingListEntry(result.Value!),
                    }))
                    : AssistantToolResult.Fail(
                        result.ErrorCode ?? AssistantErrorCodes.InvalidArguments,
                        result.ErrorMessage ?? "The book could not be added to To read.");
            }),
    ];

    private static object ReadingListEntry(ReadingListEntryDto entry) => new
    {
        id = entry.Id,
        title = entry.Title,
        author = entry.Author,
        isbn = entry.Isbn,
        publishedDate = entry.PublishedDate,
        why = entry.Why,
        recommendedBy = entry.RecommendedBy,
        shelf = entry.NextPosition is null ? "someday" : "next",
        nextPosition = entry.NextPosition is { } slot ? slot + 1 : (int?)null,
        ownedBookId = entry.OwnedBookId,
        addedAt = entry.CreatedAt,
    };

    /// <summary>
    /// The entry that is already this book: an exact ISBN, or the same title
    /// whose author matches or is missing on either side, as the list's own
    /// "already in the library" hint compares them.
    /// </summary>
    private static ReadingListEntryDto? FindReadingListEntry(
        IReadOnlyList<ReadingListEntryDto> entries, string title, string? author, string? isbn)
    {
        if (BookIdentityNormalizer.NormalizeIsbn(isbn) is { } normalizedIsbn
            && entries.FirstOrDefault(e => e.Isbn == normalizedIsbn) is { } byIsbn)
            return byIsbn;

        var normalizedTitle = BookIdentityNormalizer.NormalizeTitle(title);
        var normalizedAuthor = BookIdentityNormalizer.NormalizeAuthor(author);
        return entries.FirstOrDefault(e =>
        {
            if (BookIdentityNormalizer.NormalizeTitle(e.Title) != normalizedTitle)
                return false;
            var entryAuthor = BookIdentityNormalizer.NormalizeAuthor(e.Author);
            return normalizedAuthor.Length == 0 || entryAuthor.Length == 0 || entryAuthor == normalizedAuthor;
        });
    }

    private static bool IsDocument(string type) =>
        string.Equals(type, nameof(WritingType.Document), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every item's folder path ("Essays / Memory / Draft one"). A parent that
    /// is missing or loops ends the path rather than failing the read.
    /// </summary>
    private static Dictionary<Guid, string> WritingPaths(IReadOnlyList<WritingDto> all)
    {
        var byId = all.ToDictionary(w => w.Id);
        var paths = new Dictionary<Guid, string>(all.Count);
        foreach (var item in all)
        {
            var parts = new List<string> { item.Name };
            var seen = new HashSet<Guid> { item.Id };
            var parentId = item.ParentId;
            while (parentId is { } id && seen.Add(id) && byId.TryGetValue(id, out var parent))
            {
                parts.Add(parent.Name);
                parentId = parent.ParentId;
            }

            parts.Reverse();
            paths[item.Id] = string.Join(" / ", parts);
        }

        return paths;
    }
}
