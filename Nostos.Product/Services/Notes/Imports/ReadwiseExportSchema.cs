using System.Text.Json;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// One field the mapper reads whose shape is not what the API documents.
/// Only the JSON path and the two types are carried: never a value, a title
/// or a highlight.
/// </summary>
public sealed record ReadwiseSchemaIssue(string Path, string Expected, string Actual);

/// <summary>
/// The shape check for Readwise's export API (issue #1525). The weekly canary
/// runs this against the first page of the live export, and every import runs
/// it before mapping, so a field we read disappearing or changing type fails
/// with a path instead of importing silently wrong data.
///
/// <para>
/// Readwise's documented response has one page object
/// (<c>count</c>, <c>nextPageCursor</c>, <c>results</c>); each result is a user
/// book and carries the category and highlights. <c>nextPageCursor</c> is
/// documented as a string, but the live API currently returns a number, so
/// both are accepted as the opaque cursor we pass back.
/// </para>
/// </summary>
public static class ReadwiseExportSchema
{
    public const string RootPath = "$";
    public const string CountPath = "$.count";
    public const string NextPageCursorPath = "$.nextPageCursor";
    public const string ResultsPath = "$.results";

    /// <summary>Every issue found, in document order. Empty means the page is usable.</summary>
    public static IReadOnlyList<ReadwiseSchemaIssue> Check(JsonElement root)
    {
        var issues = new List<ReadwiseSchemaIssue>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new(RootPath, "object", TypeName(root.ValueKind)));
            return issues;
        }

        Require(root, RootPath, "count", JsonValueKind.Number, issues);
        Require(root, RootPath, "nextPageCursor", [JsonValueKind.String, JsonValueKind.Number, JsonValueKind.Null], issues);
        if (!Require(root, RootPath, "results", JsonValueKind.Array, issues))
            return issues;

        var bookIndex = 0;
        foreach (var book in root.GetProperty("results").EnumerateArray())
        {
            var bookPath = $"{ResultsPath}[{bookIndex++}]";
            if (book.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new(bookPath, "object", TypeName(book.ValueKind)));
                continue;
            }

            Require(book, bookPath, "user_book_id", JsonValueKind.Number, issues);
            Require(book, bookPath, "title", [JsonValueKind.String, JsonValueKind.Null], issues);
            Require(book, bookPath, "author", [JsonValueKind.String, JsonValueKind.Null], issues);
            Require(book, bookPath, "is_deleted", [JsonValueKind.True, JsonValueKind.False], issues);
            Require(book, bookPath, "category", [JsonValueKind.String, JsonValueKind.Null], issues);
            Require(book, bookPath, "asin", [JsonValueKind.String, JsonValueKind.Null], issues);
            if (!Require(book, bookPath, "highlights", JsonValueKind.Array, issues))
                continue;

            var highlightIndex = 0;
            foreach (var highlight in book.GetProperty("highlights").EnumerateArray())
            {
                var highlightPath = $"{bookPath}.highlights[{highlightIndex++}]";
                if (highlight.ValueKind != JsonValueKind.Object)
                {
                    issues.Add(new(highlightPath, "object", TypeName(highlight.ValueKind)));
                    continue;
                }

                Require(highlight, highlightPath, "is_deleted", [JsonValueKind.True, JsonValueKind.False], issues);
                Require(highlight, highlightPath, "is_discard", [JsonValueKind.True, JsonValueKind.False], issues);
                Require(highlight, highlightPath, "text", [JsonValueKind.String, JsonValueKind.Null], issues);
                Require(highlight, highlightPath, "note", [JsonValueKind.String, JsonValueKind.Null], issues);
                Require(highlight, highlightPath, "location", [JsonValueKind.Number, JsonValueKind.Null], issues);
                Require(highlight, highlightPath, "location_type", [JsonValueKind.String, JsonValueKind.Null], issues);
                Require(highlight, highlightPath, "highlighted_at", [JsonValueKind.String, JsonValueKind.Null], issues);
            }
        }

        return issues;
    }

    /// <summary>The shape as one log-safe sentence: paths and types only.</summary>
    public static string Describe(IReadOnlyList<ReadwiseSchemaIssue> issues) =>
        string.Join(
            "; ",
            issues.Take(10).Select(issue => $"{issue.Path} expected {issue.Expected}, found {issue.Actual}"))
        + (issues.Count > 10 ? $"; and {issues.Count - 10} more" : string.Empty);

    private static bool Require(
        JsonElement parent,
        string parentPath,
        string property,
        JsonValueKind expected,
        List<ReadwiseSchemaIssue> issues) =>
        Require(parent, parentPath, property, [expected], issues);

    private static bool Require(
        JsonElement parent,
        string parentPath,
        string property,
        IReadOnlyCollection<JsonValueKind> expected,
        List<ReadwiseSchemaIssue> issues)
    {
        var path = $"{parentPath}.{property}";
        if (!parent.TryGetProperty(property, out var value))
        {
            issues.Add(new(path, Name(expected), "missing"));
            return false;
        }

        if (!expected.Contains(value.ValueKind))
        {
            issues.Add(new(path, Name(expected), TypeName(value.ValueKind)));
            return false;
        }

        return true;
    }

    private static string Name(IReadOnlyCollection<JsonValueKind> kinds)
    {
        if (kinds.Count == 1)
            return TypeName(kinds.First());
        return string.Join(" or ", kinds.Select(TypeName));
    }

    private static string TypeName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };
}

/// <summary>
/// The export no longer has a field the mapper reads. The message is safe to
/// return to the owner and log: it carries paths and types, never content.
/// </summary>
public sealed class ReadwiseFormatException : Exception
{
    public ReadwiseFormatException(IReadOnlyList<ReadwiseSchemaIssue> issues)
        : base("Readwise's export format has changed. " + ReadwiseExportSchema.Describe(issues))
    {
        Issues = issues;
    }

    public IReadOnlyList<ReadwiseSchemaIssue> Issues { get; }
}
