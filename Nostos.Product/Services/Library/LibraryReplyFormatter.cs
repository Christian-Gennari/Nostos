namespace Nostos.Backend.Services.Library;

/// <summary>
/// Terse, neutral reply strings for the library domain. No praise, no guilt,
/// no streak language — mirrors the reading reply formatter conventions.
/// </summary>
public static class LibraryReplyFormatter
{
    public static string Success(string message) => message;

    // Books
    public static string BookCreated(string title) => $"Book added: {title}.";
    public static string BookMatched(string title) => $"Already in library: {title}.";
    public static string BookUpdated(string title) => $"Book updated: {title}.";
    public static string ProgressUpdated(string title) => $"Progress updated: {title}.";
    public static string ProgressReset(string title) => $"Progress reset: {title}.";
    public static string BookDeleted(string title) => $"Book deleted: {title}.";
    public static string BookInUse(string title) => $"Book \"{title}\" is in use by notes and cannot be deleted.";
    public static string Book(string title) => $"Book: {title}.";
    public static string BookList(int count) => $"{count} book(s).";
    public static string BookNotFound => "Book not found.";
    public static string IdentityConflict => "ISBN and ASIN resolve to different books.";
    public static string DuplicateIdentifier(string title) => $"A book with this ISBN or ASIN already exists: {title}.";
    public static string ConfirmationRequired(int count) => $"Confirmation required: {count} candidate(s) match.";
    public static string MoreInformationRequired => "Confirmation required: provide an author or identifier, or force creation.";

    // External acquisition (issue #166). The book is an ordinary local book
    // afterwards, so the wording deliberately says nothing about where it came
    // from beyond the fact that it was imported.
    public static string AssetAttached(string title) => $"Imported into library: {title}.";
    public static string AssetAlreadyAttached(string title) => $"Already imported: {title}.";
    public static string AcquisitionConflict =>
        "This item is already imported into a different book.";

    // Work membership (multi-edition grouping)
    public static string WorkLinked(string title, string targetTitle) =>
        $"Linked \"{title}\" with \"{targetTitle}\" as editions.";
    public static string WorkAlreadyLinked(string title, string targetTitle) =>
        $"\"{title}\" and \"{targetTitle}\" are already editions.";
    public static string WorkUnlinked(string title) =>
        $"\"{title}\" is now its own edition set.";
    public static string WorkAlreadyStandalone(string title) =>
        $"\"{title}\" is already its own edition set.";

    // Collections
    public static string CollectionCreated(string name) => $"Collection created: {name}.";
    public static string CollectionExists(string name) => $"Collection already exists: {name}.";
    public static string CollectionRenamed(string name) => $"Collection renamed: {name}.";
    public static string CollectionMoved(string name) => $"Collection moved: {name}.";
    public static string CollectionUpdated(string name) => $"Collection updated: {name}.";
    public static string CollectionCountList(int count) => $"{count} collection count(s).";
    public static string CollectionDeleted(string name) => $"Collection deleted: {name}.";
    public static string Collection(string name) => $"Collection: {name}.";
    public static string CollectionList(int count) => $"{count} collection(s).";
    public static string CollectionNotFound => "Collection not found.";
    public static string CollectionParentNotFound => "Parent collection not found.";
    public static string CollectionNameConflict(string name) => $"A sibling collection named \"{name}\" already exists.";
    public static string CollectionCycle => "Cannot move a collection into itself or its own child.";
    public static string CollectionHasChildren => "Collection has children; move or delete them first.";
    public static string DeleteConfirmationRequired => "Confirmation is required to delete a collection.";
}
