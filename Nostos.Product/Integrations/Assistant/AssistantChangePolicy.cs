using Nostos.Backend.Configuration;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>Whether a turn's changes apply at once (with undo) or wait for review, and why.</summary>
public sealed record AssistantChangeDecision(bool RequiresReview, IReadOnlyList<string> Reasons);

/// <summary>
/// What a turn's library changes need before they happen, judged on what they
/// would actually do (the previewed diff), never on which tool proposed them.
///
/// Everyday edits apply at once and can be undone: renaming, creating,
/// filing a handful of books, marking one finished. A change waits for the
/// user's review when it takes something away from many books, deletes a
/// collection that holds books, touches more books than a person checks at a
/// glance, follows from text the user did not write, or comes from a turn that
/// stopped before it finished. The limits are configuration, and this is a
/// pure function so every decision is reproducible.
/// </summary>
public static class AssistantChangePolicy
{
    public static AssistantChangeDecision Evaluate(
        LibraryChangesetDiffDto diff,
        AssistantOptions options,
        bool readUntrustedContent,
        bool turnIncomplete)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(options);

        var reasons = new List<string>();
        var counts = diff.Counts;

        var deletedWithBooks = diff.Collections.Where(c => c.Kind == "deleted" && c.BookCount > 0).ToList();
        if (deletedWithBooks.Count > 0)
        {
            var books = deletedWithBooks.Sum(c => c.BookCount);
            reasons.Add(deletedWithBooks.Count == 1
                ? $"Deletes “{deletedWithBooks[0].Name}”, which holds {Books(books)}."
                : $"Deletes {deletedWithBooks.Count} collections that hold {Books(books)} between them.");
        }

        if (counts.CollectionsDeleted > options.AutoApplyMaxEmptyCollectionDeletes
            && deletedWithBooks.Count == 0)
        {
            reasons.Add($"Deletes {counts.CollectionsDeleted} collections.");
        }

        if (counts.BooksLosingMembership > options.AutoApplyMaxBooksLosingMembership)
            reasons.Add($"Takes {Books(counts.BooksLosingMembership)} out of collections.");

        if (counts.BooksTouched > options.AutoApplyMaxBooksTouched)
            reasons.Add($"Changes {Books(counts.BooksTouched)}.");

        if (counts.BookEdits > options.AutoApplyMaxBookEdits)
            reasons.Add($"Edits the details of {Books(counts.BookEdits)}.");

        if (readUntrustedContent)
            reasons.Add("It was worked out from book text or notes, so you decide.");

        if (turnIncomplete)
            reasons.Add("I stopped before I could finish, so this may be incomplete.");

        return new AssistantChangeDecision(reasons.Count > 0, reasons);
    }

    private static string Books(int count) => count == 1 ? "1 book" : $"{count} books";
}
