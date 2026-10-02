using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Services.Notes.Imports;

/// <summary>
/// Pairs a book on the device with a book in the library, and says how sure
/// it is. Only an identity (ISBN, or the same title by the same author) is
/// <c>exact</c>. Everything looser — a subtitle, a leading article, a
/// transliterated surname, a shared title — is a <c>suggested</c> resemblance
/// that the owner confirms, so being generous here cannot misfile notes.
/// </summary>
internal static class HighlightImportMatcher
{
    public const string Exact = "exact";
    public const string Suggested = "suggested";
    public const string None = "none";

    private const double SuggestionThreshold = 0.6;
    private const int MaxCandidates = 3;

    private static readonly HashSet<string> TitleStopWords =
        new(StringComparer.Ordinal) { "THE", "A", "AN", "AND", "OR", "OF" };

    public static (string Match, IReadOnlyList<HighlightImportCandidate> Candidates) Match(
        ImportSourceBook source,
        IReadOnlyList<ImportLibraryBook> library)
    {
        var isbn = BookIdentityNormalizer.NormalizeIsbn(source.Isbn);
        var title = BookIdentityNormalizer.NormalizeTitle(source.Title);
        var titleTokens = TitleTokens(title);
        var authorTokens = AuthorTokens(source.Author);

        var exact = new List<HighlightImportCandidate>();
        var scored = new List<(double Score, HighlightImportCandidate Candidate)>();

        foreach (var book in library)
        {
            if (isbn is not null && book.NormalizedIsbn == isbn)
            {
                exact.Add(Candidate(book, "Same ISBN"));
                continue;
            }

            var bookTitle = BookIdentityNormalizer.NormalizeTitle(book.Title);
            if (title.Length == 0 || bookTitle.Length == 0)
                continue;

            var bookAuthorTokens = AuthorTokens(book.Author);
            var sameAuthor = authorTokens.Count > 0 && authorTokens.SetEquals(bookAuthorTokens);
            if (bookTitle == title && sameAuthor)
            {
                exact.Add(Candidate(book, "Same title and author"));
                continue;
            }

            var titleScore = TitleScore(title, titleTokens, bookTitle, TitleTokens(bookTitle));
            if (titleScore < SuggestionThreshold)
                continue;

            var authorScore = AuthorScore(authorTokens, bookAuthorTokens);
            var score = titleScore * 0.7 + authorScore * 0.3;
            if (score < SuggestionThreshold)
                continue;

            var reason = (bookTitle == title, authorScore) switch
            {
                (true, 0) => "Same title, different author",
                (true, _) => "Same title, similar author",
                (false, 0) => "Similar title, different author",
                (false, 1) => "Similar title, same author",
                _ => "Similar title and author",
            };
            scored.Add((score, Candidate(book, reason)));
        }

        // Two editions of one book are both "exact": the owner picks.
        if (exact.Count == 1)
            return (Exact, exact);

        var candidates = exact
            .Concat(scored.OrderByDescending(entry => entry.Score).Select(entry => entry.Candidate))
            .Take(MaxCandidates)
            .ToList();

        return (candidates.Count == 0 ? None : Suggested, candidates);
    }

    private static HighlightImportCandidate Candidate(ImportLibraryBook book, string reason) =>
        new(book.Id, book.Title, book.Author, reason);

    private static double TitleScore(
        string title,
        HashSet<string> tokens,
        string otherTitle,
        HashSet<string> otherTokens)
    {
        if (title == otherTitle)
            return 1;
        if (tokens.Count == 0 || otherTokens.Count == 0)
            return 0;
        if (tokens.SetEquals(otherTokens))
            return 0.95;

        // "Devils" against "The Possessed; or, The Devils", or a title against
        // the same title with its subtitle.
        var (smaller, larger) = tokens.Count <= otherTokens.Count ? (tokens, otherTokens) : (otherTokens, tokens);
        var contained = smaller.IsSubsetOf(larger) ? 0.85 : 0;

        var shared = tokens.Count(otherTokens.Contains);
        var jaccard = (double)shared / (tokens.Count + otherTokens.Count - shared);

        return Math.Max(Math.Max(contained, jaccard), Similarity(title, otherTitle));
    }

    private static double AuthorScore(HashSet<string> tokens, HashSet<string> otherTokens)
    {
        // An unknown author neither supports nor contradicts the title.
        if (tokens.Count == 0 || otherTokens.Count == 0)
            return 0.5;
        if (tokens.SetEquals(otherTokens))
            return 1;

        // A shared name, or a surname one transliteration apart
        // (Dostoevsky / Dostoyevsky).
        foreach (var token in tokens)
        {
            foreach (var other in otherTokens)
            {
                if (token == other || (token.Length >= 5 && other.Length >= 5 && Similarity(token, other) >= 0.8))
                    return 0.8;
            }
        }

        return 0;
    }

    private static HashSet<string> TitleTokens(string normalizedTitle) =>
        normalizedTitle
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !TitleStopWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);

    // Order, punctuation and initials are noise: "Mann, Thomas", "Thomas Mann"
    // and "T. Mann" should not be told apart by them.
    private static HashSet<string> AuthorTokens(string? author) =>
        BookIdentityNormalizer.NormalizeTitle(author)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 1)
            .ToHashSet(StringComparer.Ordinal);

    private static double Similarity(string a, string b)
    {
        var longest = Math.Max(a.Length, b.Length);
        return longest == 0 ? 1 : 1 - (double)Distance(a, b) / longest;
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
