namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// What Readwise counts as a book, shared by the CSV export reader (#1219) and
/// the export-API mapper (#1525) so the two paths cannot drift: the same
/// categories are imported, the same ones are left out with the same reason,
/// and the same location types become anchors.
/// </summary>
internal static class ReadwiseBookRules
{
    public const string AnchorPrefix = "readwise_";
    public const string UnknownAnchor = "readwise_unknown";

    public const string ArticleReason = "A web article. Only highlights from books are imported.";
    public const string ArticleNotice = "Readwise keeps this like a web article.";
    public const string TweetReason = "A tweet. Only highlights from books are imported.";
    public const string PodcastReason = "A podcast. Only highlights from books are imported.";
    public const string OtherReason = "Not a book. Only highlights from books are imported.";
    public const string EmptyReason = "The row has no highlight or note.";
    public const string UntitledReason = "The row has no book title.";

    public static readonly HashSet<string> AnchorTypes = new(StringComparer.Ordinal)
    {
        "location",
        "page",
        "order",
    };

    /// <summary>
    /// Why an item is not from a book, or null when it is. An export that names
    /// each item's category says so outright; one that does not gives it away
    /// by its shape: a tweet thread is "Tweets From …" by an @handle, and a
    /// podcast is positioned by time.
    /// </summary>
    public static string? NotABook(
        string? category,
        string? locationType,
        string? title,
        string? author,
        string? asin)
    {
        if (category is not null)
        {
            return category switch
            {
                "books" or "book" or "supplementals" or "supplemental" or "pdfs" or "pdf" or "epubs" or "epub" => null,
                "articles" or "article" => ArticleReason,
                "tweets" or "tweet" => TweetReason,
                "podcasts" or "podcast" => PodcastReason,
                _ => OtherReason,
            };
        }

        if (asin is not null)
            return null;
        if ((title?.StartsWith("Tweets From ", StringComparison.OrdinalIgnoreCase) ?? false)
            || (author?.StartsWith('@') ?? false))
            return TweetReason;
        return locationType is "time_offset" or "time" or "timestamp" ? PodcastReason : null;
    }

    /// <summary>
    /// The note's anchor: Readwise's own location type and value when both are
    /// present and the type is one the import understands, otherwise the
    /// unknown anchor. A book can still be imported with no anchor.
    /// </summary>
    public static (string Kind, string? Value) Anchor(string? locationType, string? location)
    {
        var type = locationType?.ToLowerInvariant();
        return type is not null && AnchorTypes.Contains(type) && location is not null
            ? (AnchorPrefix + type, location)
            : (UnknownAnchor, null);
    }
}
