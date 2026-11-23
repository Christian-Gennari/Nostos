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

        if (counts.TopicKindsCreated > 0)
        {
            var kinds = diff.TopicKinds.Where(kind => kind.Kind == "created").Select(kind => $"“{kind.Name}”").ToList();
            reasons.Add(kinds.Count == 1
                ? $"Creates the topic kind {kinds[0]}, so you decide what it means."
                : $"Creates {kinds.Count} topic kinds, so you decide what they mean.");
        }

        if (counts.TopicKindChanges > options.AutoApplyTopicKinds)
            reasons.Add(TopicKindReason(counts));

        if (readUntrustedContent)
            reasons.Add("It was worked out from book text or notes, so you decide.");

        if (turnIncomplete)
            reasons.Add("I stopped before I could finish, so this may be incomplete.");

        return new AssistantChangeDecision(reasons.Count > 0, reasons);
    }

    private static string Books(int count) => count == 1 ? "1 book" : $"{count} books";

    private static string Topics(int count) => count == 1 ? "1 topic" : $"{count} topics";

    /// <summary>
    /// What the kind changes do, in their own words: giving a kind, changing
    /// one and removing one are three different acts, and a clear is not a gift.
    /// A host that predates the split (no per-kind counts) falls back to the
    /// neutral "changes the kind of N topics".
    /// </summary>
    private static string TopicKindReason(LibraryChangesetCountsDto counts)
    {
        var parts = new List<string>();
        if (counts.TopicKindsGiven > 0)
            parts.Add($"gives {Topics(counts.TopicKindsGiven)} a kind");
        if (counts.TopicKindsChanged > 0)
            parts.Add($"changes the kind of {Topics(counts.TopicKindsChanged)}");
        if (counts.TopicKindsCleared > 0)
            parts.Add($"removes the kind from {Topics(counts.TopicKindsCleared)}");

        var sentence = parts.Count switch
        {
            0 => $"changes the kind of {Topics(counts.TopicKindChanges)}",
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}",
        };
        return char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".";
    }
}
