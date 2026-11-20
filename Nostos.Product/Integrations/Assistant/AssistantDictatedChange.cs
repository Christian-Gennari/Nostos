using System.Text.RegularExpressions;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// Whether a turn's staged changes are only what the user dictated in their own
/// message (issue #1745). Reading notes in the same turn ("what have I noted
/// about this book, and rate it 5 stars") must not make a plain, explicitly
/// requested edit look as if it was worked out from that text.
///
/// The rule is narrow on purpose, because it lifts the prompt-injection guard
/// for these steps: every step is an edit of one single book, it sets only
/// values the user's words can name (a rating, favourite, finished), never
/// free text, and the user's message actually says each value.
/// </summary>
internal static partial class AssistantDictatedChange
{
    private static readonly string[] NumberWords = ["zero", "one", "two", "three", "four", "five"];

    [GeneratedRegex(@"\b(rate|rated|rating|stars?|betyg|stj[äa]rn\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RatingWord();

    [GeneratedRegex(@"\b(clear|remove|reset|unrate)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClearWord();

    [GeneratedRegex(@"\b(favou?rit\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FavouriteWord();

    // "read" or "done" alone is too common ("what have I read?") to count as an
    // instruction; only "finished" or an explicit "mark it (as) read/done" does.
    [GeneratedRegex(@"\b(finish\w*|mark(?:ed)?\s+(?:it|this(?:\s+book)?|the\s+book)?\s*(?:as\s+)?(?:read|done)|utl[äa]st|l[äa]st\s+ut|klar\s+med)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FinishedWord();

    public static bool Covers(IReadOnlyList<AssistantStagedStep> steps, string? userMessage)
    {
        if (steps.Count == 0 || string.IsNullOrWhiteSpace(userMessage))
            return false;

        var updates = new List<Nostos.Backend.Services.Library.UpdateBookOp>();
        foreach (var step in steps)
        {
            if (step.Op is not Nostos.Backend.Services.Library.UpdateBookOp update)
                return false;
            updates.Add(update);
        }

        if (updates.Select(u => u.BookId).Distinct().Count() != 1)
            return false;

        return updates.All(u => IsDictated(u, userMessage));
    }

    private static bool IsDictated(Nostos.Backend.Services.Library.UpdateBookOp op, string message)
    {
        if (op.Title is not null || op.Author is not null || op.Description is not null || op.PersonalReview is not null)
            return false;

        var any = false;
        foreach (var rating in new[] { op.Rating, op.EditionRating })
        {
            if (rating is not { } value)
                continue;
            any = true;
            if (!RatingWord().IsMatch(message) || !(NamesNumber(message, value) || (value == 0 && ClearWord().IsMatch(message))))
                return false;
        }

        if (op.IsFavorite is not null)
        {
            any = true;
            if (!FavouriteWord().IsMatch(message))
                return false;
        }

        if (op.IsFinished is not null)
        {
            any = true;
            if (!FinishedWord().IsMatch(message))
                return false;
        }

        return any;
    }

    private static bool NamesNumber(string message, int value)
    {
        if (value is < 0 or > 5)
            return false;
        return Regex.IsMatch(message, $@"(?<![\d.]){value}(?![\d.])")
            || Regex.IsMatch(message, $@"\b{NumberWords[value]}\b", RegexOptions.IgnoreCase);
    }
}
