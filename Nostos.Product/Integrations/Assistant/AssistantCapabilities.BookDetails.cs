using System.Text.Json;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

public static partial class AssistantCapabilities
{
    /// <summary>The MCP-only capability behind <c>update_book</c> (issue #1490).</summary>
    internal const string EditBookDetailsName = "library_edit_book_details";

    /// <summary>
    /// The book details an outside app may fill in or correct, with how each is
    /// read from a <see cref="BookDto"/>. Everything here is plain metadata the
    /// book page edits through <c>PUT /api/books/{id}</c>; a page count is left
    /// out because the update path cannot clear one, so it could not be undone.
    /// </summary>
    internal static readonly IReadOnlyList<(string Name, Func<BookDto, string?> Read)> EditableBookFields =
    [
        ("title", b => b.Title),
        ("subtitle", b => b.Subtitle),
        ("author", b => b.Author),
        ("description", b => b.Description),
        ("publishedDate", b => b.PublishedDate),
        ("isbn", b => b.Isbn),
        ("publisher", b => b.Publisher),
        ("language", b => b.Language),
        ("series", b => b.Series),
        ("volumeNumber", b => b.VolumeNumber),
    ];

    /// <summary>
    /// Edits a book's details for a connected app. Unlike Ask Nostos's
    /// <c>library_update_book</c>, which stages the edit for the change policy,
    /// this runs at once, so it is careful instead: a field that already has a
    /// value is kept unless the call says <c>replace</c>, and the result names
    /// the value every changed field had before, so the change can be undone.
    /// Only offered on MCP (see <see cref="AssistantCapabilities.McpExposed"/>):
    /// Ask Nostos keeps its reviewed path.
    /// </summary>
    private static AssistantCapability BuildEditBookDetailsCapability(ILibraryService library) =>
        new(
            EditBookDetailsName,
            AssistantTrustClass.Act,
            AssistantCapabilityCategory.OrdinaryAction,
            "Fills in or corrects one book's details. A field that already has a value changes only with 'replace'. Returns each changed field's old value.",
            """
            {
              "type": "object",
              "properties": {
                "bookId": { "type": "string", "format": "uuid", "description": "The book. Required." },
                "title": { "type": "string", "description": "The title." },
                "subtitle": { "type": "string", "description": "The subtitle." },
                "author": { "type": "string", "description": "The author or authors." },
                "description": { "type": "string", "description": "The description." },
                "publishedDate": { "type": "string", "description": "The year or date of publication." },
                "isbn": { "type": "string", "description": "The ISBN." },
                "publisher": { "type": "string", "description": "The publisher." },
                "language": { "type": "string", "description": "The language." },
                "series": { "type": "string", "description": "The series." },
                "volumeNumber": { "type": "string", "description": "The book's number in its series." },
                "replace": { "type": "boolean", "description": "True to change fields that already have a value. An empty string then clears a field." }
              },
              "required": ["bookId"],
              "additionalProperties": true
            }
            """,
            (context, args, ct) => EditBookDetailsAsync(library, context, args, ct))
        {
            Surfaces = AssistantSurface.Mcp,
        };

    private static async Task<AssistantToolResult> EditBookDetailsAsync(
        ILibraryService library,
        AssistantToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        if (Id(args, "bookId") is not { } bookId)
            return Invalid("'bookId' is required.");

        var given = EditableBookFields
            .Select(field => (field.Name, field.Read, Value: Str(args, field.Name)))
            .Where(field => field.Value is not null)
            .ToList();
        if (given.Count == 0)
            return Invalid("Give at least one detail to fill in.");
        if (given.Any(field => field.Name == "title" && string.IsNullOrWhiteSpace(field.Value)))
            return Invalid("A book always has a title. Give the new title, or leave 'title' out.");

        var current = await library.GetBookAsync(bookId, ct);
        if (current.Data is not BookDto book)
            return LibraryResult(current);

        var replace = Bool(args, "replace") ?? false;
        var to = new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = new List<BookFieldChange>();
        var kept = new List<BookFieldKept>();
        var unchanged = new List<string>();
        foreach (var (name, read, value) in given)
        {
            var wanted = value!.Trim();
            var had = Blank(read(book));
            if (SameDetail(name, had, wanted))
                unchanged.Add(name);
            else if (had is not null && !replace)
                kept.Add(new BookFieldKept(name, had, wanted));
            else
            {
                to[name] = wanted;
                changed.Add(new BookFieldChange(name, had, Blank(wanted)));
            }
        }

        if (to.Count == 0)
            return AssistantToolResult.Ok(Element(new BookDetailsEdit("unchanged", book, changed, kept, unchanged)));

        // The key is the caller's (derived from what it asked for) plus the
        // library revision it read: a resend that races the first call replays
        // its receipt, while the same request made later, after someone changed
        // the book back, is a new edit rather than a stale replay.
        var request = new LibraryUpdateBookRequest(
            ClientId: context.ClientId ?? string.Empty,
            IdempotencyKey: string.IsNullOrEmpty(context.IdempotencyKey)
                ? string.Empty
                : $"{context.IdempotencyKey}:{current.StateVersion}",
            BookId: bookId,
            Title: to.GetValueOrDefault("title"),
            Subtitle: to.GetValueOrDefault("subtitle"),
            Author: to.GetValueOrDefault("author"),
            Description: to.GetValueOrDefault("description"),
            Isbn: to.GetValueOrDefault("isbn"),
            Publisher: to.GetValueOrDefault("publisher"),
            PublishedDate: to.GetValueOrDefault("publishedDate"),
            Language: to.GetValueOrDefault("language"),
            Series: to.GetValueOrDefault("series"),
            VolumeNumber: to.GetValueOrDefault("volumeNumber"));

        var result = await library.UpdateBookAsync(request, ct);
        if (result.Data is not BookDto updated)
            return LibraryResult(result);

        return AssistantToolResult.Ok(Element(new BookDetailsEdit("updated", updated, changed, kept, unchanged)));
    }

    /// <summary>Two values of one detail say the same thing: equal words, or the same ISBN however it is written.</summary>
    private static bool SameDetail(string name, string? had, string wanted)
    {
        var want = Blank(wanted);
        if (had is null || want is null)
            return had is null && want is null;
        if (name == "isbn"
            && BookIdentityNormalizer.NormalizeIsbn(had) is { } a
            && BookIdentityNormalizer.NormalizeIsbn(want) is { } b)
            return a == b;
        return string.Equals(had, want, StringComparison.Ordinal);
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What <c>library_edit_book_details</c> did: <c>updated</c> or <c>unchanged</c>.</summary>
public sealed record BookDetailsEdit(
    string Outcome,
    BookDto Book,
    IReadOnlyList<BookFieldChange> Changed,
    IReadOnlyList<BookFieldKept> Kept,
    IReadOnlyList<string> Unchanged);

/// <summary>One detail that changed; <c>From</c> null means it was empty, <c>To</c> null that it was cleared.</summary>
public sealed record BookFieldChange(string Field, string? From, string? To);

/// <summary>One detail the call gave but that kept the value the book already had (no <c>replace</c>).</summary>
public sealed record BookFieldKept(string Field, string Value, string Given);
