using System.Globalization;
using System.Text;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Product.BookText;

/// <summary>
/// Finds a passage the user names by its first and last words ("it starts with
/// … and ends with …") in the text of a book, or of the other editions of the
/// same work (issue #1291). This is how a quote from a paper book is saved
/// exactly: the words come from the digital edition, the page from the user.
/// </summary>
public interface IBookTextQuoteFinder
{
    Task<BookTextQuoteFindResult> FindAsync(BookTextQuoteFindRequest request, CancellationToken ct = default);

    /// <summary>The exact passage behind a <see cref="BookTextQuoteCandidate.QuoteRef"/>, or null when it no longer resolves.</summary>
    Task<BookTextResolvedQuote?> ResolveAsync(string quoteRef, CancellationToken ct = default);
}

public sealed record BookTextQuoteFindRequest(
    Guid BookId,
    string StartsWith,
    string EndsWith,
    int? PageHint = null,
    string? ChapterHint = null,
    int? Choice = null);

public enum BookTextQuoteFindStatus
{
    /// <summary>One passage, ready to save.</summary>
    Found,
    /// <summary>One passage, but long enough that the user should confirm it first.</summary>
    Confirm,
    /// <summary>More than one passage matches; the user picks one.</summary>
    Choose,
    NotFound,
    /// <summary>A digital edition exists, but its text is not ready yet.</summary>
    TextNotReady,
    /// <summary>Neither the book nor another edition of it has text to search.</summary>
    NoDigitalEdition,
    BookNotFound,
}

public sealed record BookTextQuoteCandidate(
    int Number,
    string QuoteRef,
    Guid SourceBookId,
    string SourceTitle,
    string SourceFormat,
    string Text,
    int WordCount,
    string? Chapter,
    int PositionPercent);

public sealed record BookTextQuoteFindResult(
    BookTextQuoteFindStatus Status,
    IReadOnlyList<BookTextQuoteCandidate> Candidates,
    int TotalMatches);

public sealed record BookTextResolvedQuote(Guid SourceBookId, string Text);

public sealed class BookTextQuoteFinder(
    IBookTextIndex index,
    ILibraryService library) : IBookTextQuoteFinder
{
    /// <summary>Above this length a single match is shown for confirmation before it is saved.</summary>
    public const int ConfirmAboveChars = 2500;
    public const int MaxQuoteChars = 6000;
    public const int MaxChoices = 5;
    private const int NeighborRadius = 2;
    private const int MaxCandidateChunks = 24;
    private const double HintTolerance = 0.12;

    public async Task<BookTextQuoteFindResult> FindAsync(
        BookTextQuoteFindRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startPhrase = QuoteMatcher.Tokenize(request.StartsWith);
        var endPhrase = QuoteMatcher.Tokenize(request.EndsWith);
        if (startPhrase.Count == 0 || endPhrase.Count == 0)
            return Empty(BookTextQuoteFindStatus.NotFound);

        if ((await library.GetBookAsync(request.BookId, ct)).Data is not BookDto book)
            return Empty(BookTextQuoteFindStatus.BookNotFound);

        var sources = await ReadySourcesAsync(book, ct);
        if (sources.Ready.Count == 0)
        {
            return Empty(sources.AnyPending
                ? BookTextQuoteFindStatus.TextNotReady
                : BookTextQuoteFindStatus.NoDigitalEdition);
        }

        var matches = new List<Match>();
        var seenText = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources.Ready)
        {
            foreach (var match in await FindInSourceAsync(source, request, startPhrase, endPhrase, ct))
            {
                // Two editions of one work hold the same passage: that is one
                // quote, not a choice. The first source (the open book, then
                // EPUB before PDF) supplies it.
                if (seenText.Add(match.Fingerprint))
                    matches.Add(match);
            }
        }

        if (matches.Count == 0)
            return Empty(BookTextQuoteFindStatus.NotFound);

        var ordered = Narrow(matches, request, book.PageCount);
        var candidates = ordered
            .Select((match, i) => match.ToCandidate(i + 1))
            .ToList();

        if (request.Choice is { } choice)
        {
            return choice >= 1 && choice <= candidates.Count
                ? new BookTextQuoteFindResult(BookTextQuoteFindStatus.Found, [candidates[choice - 1]], candidates.Count)
                : new BookTextQuoteFindResult(BookTextQuoteFindStatus.Choose, candidates.Take(MaxChoices).ToList(), candidates.Count);
        }

        if (candidates.Count > 1)
            return new BookTextQuoteFindResult(BookTextQuoteFindStatus.Choose, candidates.Take(MaxChoices).ToList(), candidates.Count);

        var only = candidates[0];
        return new BookTextQuoteFindResult(
            only.Text.Length > ConfirmAboveChars ? BookTextQuoteFindStatus.Confirm : BookTextQuoteFindStatus.Found,
            candidates,
            1);
    }

    public async Task<BookTextResolvedQuote?> ResolveAsync(string quoteRef, CancellationToken ct = default)
    {
        if (!QuoteRef.TryParse(quoteRef, out var parsed))
            return null;

        var state = await index.GetStateAsync(parsed.BookId, ct);
        if (state is not { Status: BookTextIngestionStatus.Ready, SourceSha256: { } sha, ExtractorVersion: { } version }
            || !sha.StartsWith(parsed.ShaPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var chunks = await LoadRangeAsync(parsed.BookId, sha, version, parsed.StartOrdinal, parsed.EndOrdinal, ct);
        if (chunks.Count != parsed.EndOrdinal - parsed.StartOrdinal + 1)
            return null;

        var window = QuoteWindow.Join(chunks);
        if (parsed.Start < 0 || parsed.End > window.Text.Length || parsed.End <= parsed.Start)
            return null;

        return new BookTextResolvedQuote(parsed.BookId, QuoteMatcher.Clean(window.Text[parsed.Start..parsed.End]));
    }

    /// <summary>
    /// Chunks <paramref name="from"/>..<paramref name="to"/>. The index only
    /// hands out neighbours of a chunk (and radius 0 may be nothing at all), so
    /// the range is read around its first chunk and, if that one is missing,
    /// around the next.
    /// </summary>
    private async Task<List<BookTextIndexedChunk>> LoadRangeAsync(
        Guid bookId, string sha, string version, int from, int to, CancellationToken ct)
    {
        var radius = to - from + 1;
        var found = new Dictionary<int, BookTextIndexedChunk>();
        foreach (var center in new[] { from, from + 1 })
        {
            foreach (var chunk in await index.GetNeighborsAsync(bookId, sha, version, center, radius, ct))
            {
                if (chunk.Ordinal >= from && chunk.Ordinal <= to)
                    found.TryAdd(chunk.Ordinal, chunk);
            }

            if (found.Count == to - from + 1)
                break;
        }

        return found.Values.OrderBy(chunk => chunk.Ordinal).ToList();
    }

    private static BookTextQuoteFindResult Empty(BookTextQuoteFindStatus status) => new(status, [], 0);

    private sealed record Source(BookDto Book, BookTextIngestionState State);

    private async Task<(List<Source> Ready, bool AnyPending)> ReadySourcesAsync(BookDto book, CancellationToken ct)
    {
        var editions = new List<BookDto> { book };
        foreach (var edition in (book.OtherEditions ?? [])
            .Where(edition => edition.HasFile)
            .OrderBy(edition => FormatRank(edition.Type))
            .ThenBy(edition => edition.Id))
        {
            if ((await library.GetBookAsync(edition.Id, ct)).Data is BookDto other)
                editions.Add(other);
        }

        var ready = new List<Source>();
        var anyPending = false;
        foreach (var edition in editions.Where(edition => edition.HasFile))
        {
            var state = await index.GetStateAsync(edition.Id, ct);
            if (state is null)
                continue;
            if (state.Status == BookTextIngestionStatus.Ready)
                ready.Add(new Source(edition, state));
            else if (state.Status is BookTextIngestionStatus.Pending or BookTextIngestionStatus.Processing)
                anyPending = true;
        }

        return (ready, anyPending);
    }

    private static int FormatRank(string? type) => type?.ToLowerInvariant() switch
    {
        "epub" => 0,
        "pdf" => 1,
        _ => 2,
    };

    private async Task<List<Match>> FindInSourceAsync(
        Source source,
        BookTextQuoteFindRequest request,
        IReadOnlyList<QuoteToken> startPhrase,
        IReadOnlyList<QuoteToken> endPhrase,
        CancellationToken ct)
    {
        var bookIds = new[] { source.Book.Id };
        var hits = new Dictionary<int, BookTextIndexedChunk>();
        async Task Collect(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || hits.Count >= MaxCandidateChunks)
                return;
            foreach (var hit in await index.SearchAsync(query, bookIds, MaxCandidateChunks, ct))
            {
                if (hits.Count >= MaxCandidateChunks)
                    break;
                hits.TryAdd(hit.Chunk.Ordinal, hit.Chunk);
            }
        }

        await Collect(request.StartsWith);
        await Collect(request.EndsWith);
        if (hits.Count == 0)
        {
            // Speech-to-text gets a word wrong now and then, and the full-text
            // search wants every word. The rarest-looking words still find it.
            foreach (var word in startPhrase.Concat(endPhrase)
                .Select(token => token.Value)
                .Where(word => word.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(word => word.Length)
                .Take(4))
            {
                await Collect(word);
            }
        }

        var state = source.State;
        var matches = new Dictionary<(int, int), Match>();
        foreach (var hit in hits.Values.OrderBy(chunk => chunk.Ordinal))
        {
            var chunks = (await index.GetNeighborsAsync(
                    hit.BookId, hit.SourceSha256, hit.ExtractorVersion, hit.Ordinal, NeighborRadius, ct))
                .Append(hit)
                .GroupBy(chunk => chunk.Ordinal)
                .Select(group => group.First())
                .OrderBy(chunk => chunk.Ordinal)
                .ToList();
            var window = QuoteWindow.Join(chunks);

            foreach (var span in QuoteMatcher.FindSpans(window.Text, startPhrase, endPhrase, MaxQuoteChars))
            {
                var first = window.ChunkAt(span.Start);
                var last = window.ChunkAt(span.End - 1, from: first);
                var shift = window.Starts[first];
                var reference = new QuoteRef(
                    source.Book.Id,
                    hit.SourceSha256[..Math.Min(12, hit.SourceSha256.Length)],
                    chunks[first].Ordinal,
                    chunks[last].Ordinal,
                    span.Start - shift,
                    span.End - shift);
                var key = (chunks[first].Ordinal, span.Start - shift);
                if (matches.ContainsKey(key))
                    continue;

                var text = QuoteMatcher.Clean(window.Text[span.Start..span.End]);
                var position = state.ChunkCount > 0
                    ? (chunks[first].Ordinal + 0.5) / state.ChunkCount
                    : 0;
                matches[key] = new Match(
                    reference,
                    source.Book,
                    text,
                    chunks[first].HeadingPath,
                    Math.Clamp(position, 0, 1),
                    span.Score,
                    (chunks[last].Ordinal, span.End - window.Starts[last]));
            }
        }

        // Matches that share an end are one passage: the best-matching start
        // wins (a longer phrase also matches a word late), and of equally good
        // starts the latest, which is what "starts with" points at.
        return matches.Values
            .GroupBy(match => match.EndKey)
            .Select(group => group
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Text.Length)
                .First())
            .OrderBy(match => match.Ref.StartOrdinal)
            .ThenBy(match => match.Ref.Start)
            .ToList();
    }

    /// <summary>
    /// A page or chapter the user gave narrows several matches down. Paper and
    /// digital pages differ, so a page is only a rough position in the book.
    /// It never removes the last remaining match.
    /// </summary>
    private static List<Match> Narrow(List<Match> matches, BookTextQuoteFindRequest request, int? pageCount)
    {
        var ordered = matches.OrderByDescending(match => match.Score >= 0.999).ToList();
        if (ordered.Count <= 1)
            return ordered;

        if (!string.IsNullOrWhiteSpace(request.ChapterHint))
        {
            var inChapter = ordered.Where(match => QuoteMatcher.ChapterMatches(match.Headings, request.ChapterHint!)).ToList();
            if (inChapter.Count > 0)
                ordered = inChapter;
        }

        if (ordered.Count > 1 && request.PageHint is > 0 && pageCount is > 0)
        {
            var target = Math.Clamp((double)request.PageHint.Value / pageCount.Value, 0, 1);
            var near = ordered
                .Where(match => Math.Abs(match.Position - target) <= HintTolerance)
                .OrderBy(match => Math.Abs(match.Position - target))
                .ToList();
            if (near.Count > 0)
                ordered = near;
        }

        // Exact matches beat fuzzy ones only when there are both.
        if (ordered.Count > 1 && ordered.Any(match => match.Score >= 0.999) && ordered.Any(match => match.Score < 0.999))
            ordered = ordered.Where(match => match.Score >= 0.999).ToList();

        return ordered;
    }

    private sealed record Match(
        QuoteRef Ref,
        BookDto Book,
        string Text,
        IReadOnlyList<string> Headings,
        double Position,
        double Score,
        (int Ordinal, int Offset) EndKey)
    {
        public string Fingerprint => string.Join(' ', QuoteMatcher.Tokenize(Text).Select(token => token.Value));

        public BookTextQuoteCandidate ToCandidate(int number) => new(
            number,
            Ref.ToString(),
            Book.Id,
            Book.Title,
            Book.Type,
            Text,
            QuoteMatcher.Tokenize(Text).Count,
            Headings.Count > 0 ? Headings[^1] : null,
            (int)Math.Round(Position * 100));
    }
}

/// <summary>
/// "q1-{book}-{sha}-{firstChunk}-{lastChunk}-{start}-{end}": the passage's
/// place in one indexed revision of a book. Offsets are into the joined text of
/// chunks firstChunk..lastChunk, so the ref resolves to the same words for as
/// long as that revision is the indexed one.
/// </summary>
internal readonly record struct QuoteRef(
    Guid BookId,
    string ShaPrefix,
    int StartOrdinal,
    int EndOrdinal,
    int Start,
    int End)
{
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"q1-{BookId:N}-{ShaPrefix}-{StartOrdinal}-{EndOrdinal}-{Start}-{End}");

    public static bool TryParse(string? value, out QuoteRef parsed)
    {
        parsed = default;
        var parts = value?.Trim().Split('-');
        if (parts is not { Length: 7 } || parts[0] != "q1"
            || !Guid.TryParseExact(parts[1], "N", out var bookId)
            || parts[2].Length == 0 || !parts[2].All(char.IsAsciiHexDigit))
        {
            return false;
        }

        var numbers = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i + 3], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        if (numbers[1] < numbers[0] || numbers[1] - numbers[0] > 8)
            return false;

        parsed = new QuoteRef(bookId, parts[2], numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }
}

/// <summary>Consecutive chunks joined back into running text.</summary>
internal sealed record QuoteWindow(string Text, IReadOnlyList<int> Starts, IReadOnlyList<int> Ends)
{
    /// <summary>
    /// Chunks that split one long block repeat a little of the previous one;
    /// that overlap is joined once. Other chunks are separate blocks.
    /// <see cref="Starts"/> is where each chunk's own text begins, overlap included.
    /// </summary>
    public static QuoteWindow Join(IReadOnlyList<BookTextIndexedChunk> chunks)
    {
        var text = new StringBuilder();
        var starts = new List<int>(chunks.Count);
        var ends = new List<int>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i].Text;
            if (i == 0)
            {
                starts.Add(0);
                text.Append(chunk);
                ends.Add(text.Length);
                continue;
            }

            var overlap = Overlap(text, chunk);
            if (overlap > 0)
            {
                starts.Add(text.Length - overlap);
                text.Append(chunk, overlap, chunk.Length - overlap);
            }
            else
            {
                text.Append("\n\n");
                starts.Add(text.Length);
                text.Append(chunk);
            }

            ends.Add(text.Length);
        }

        return new QuoteWindow(text.ToString(), starts, ends);
    }

    private static int Overlap(StringBuilder previous, string next)
    {
        var max = Math.Min(Math.Min(previous.Length, next.Length), 600);
        for (var length = max; length >= 24; length--)
        {
            var matches = true;
            for (var j = 0; j < length && matches; j++)
                matches = previous[previous.Length - length + j] == next[j];
            if (matches)
                return length;
        }

        return 0;
    }

    /// <summary>The last chunk that begins at or before <paramref name="offset"/>.</summary>
    public int ChunkAt(int offset, int from = 0)
    {
        var found = from;
        for (var i = from; i < Starts.Count; i++)
        {
            if (Starts[i] <= offset)
                found = i;
            else
                break;
        }

        // The end of a quote may sit in the overlap a later chunk repeats; the
        // earliest chunk that still contains it keeps the ref small.
        for (var i = from; i <= found; i++)
        {
            if (Ends[i] > offset)
                return i;
        }

        return found;
    }
}

public readonly record struct QuoteToken(string Value, int Start, int End);

public readonly record struct QuoteSpan(int Start, int End, double Score);

/// <summary>
/// Word-level matching that tolerates what dictation gets wrong: case,
/// punctuation, accents, a misheard letter or two in a word, and a dropped or
/// extra word in a longer phrase.
/// </summary>
public static class QuoteMatcher
{
    private const string OpeningMarks = "\"'“‘«„(";
    private const string ClosingMarks = ".,;:!?…\"'”’»)]—–";

    public static IReadOnlyList<QuoteToken> Tokenize(string? text)
    {
        var tokens = new List<QuoteToken>();
        if (string.IsNullOrEmpty(text))
            return tokens;

        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            var value = new StringBuilder();
            while (i < text.Length
                && (char.IsLetterOrDigit(text[i])
                    || (IsApostrophe(text[i]) && i + 1 < text.Length && char.IsLetter(text[i + 1]))))
            {
                if (char.IsLetterOrDigit(text[i]))
                    value.Append(text[i]);
                i++;
            }

            tokens.Add(new QuoteToken(Fold(value.ToString()), start, i));
        }

        return tokens;
    }

    private static bool IsApostrophe(char c) => c is '\'' or '’';

    private static string Fold(string word)
    {
        var decomposed = word.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return builder.ToString();
    }

    public static bool SameWord(string a, string b)
    {
        if (a == b)
            return true;
        var shorter = Math.Min(a.Length, b.Length);
        if (shorter < 4)
            return false;
        var allowed = shorter >= 7 ? 2 : 1;
        return Math.Abs(a.Length - b.Length) <= allowed && Distance(a, b, allowed) <= allowed;
    }

    private static int Distance(string a, string b, int cap)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > cap)
                return cap + 1;
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// Every passage in <paramref name="text"/> that opens with the start phrase
    /// and closes with the first end phrase after it, at most
    /// <paramref name="maxChars"/> long.
    /// </summary>
    public static IReadOnlyList<QuoteSpan> FindSpans(
        string text,
        IReadOnlyList<QuoteToken> startPhrase,
        IReadOnlyList<QuoteToken> endPhrase,
        int maxChars)
    {
        var spans = new List<QuoteSpan>();
        if (startPhrase.Count == 0 || endPhrase.Count == 0)
            return spans;

        var tokens = Tokenize(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (MatchForward(tokens, i, startPhrase) is not { } start)
                continue;

            for (var j = i + Math.Max(0, start.Length - endPhrase.Count); j < tokens.Count; j++)
            {
                if (tokens[j].Start - tokens[i].Start > maxChars)
                    break;
                if (MatchForward(tokens, j, endPhrase) is not { } end)
                    continue;

                if (j + end.Length < i + start.Length)
                    continue;

                var lastToken = tokens[j + end.Length - 1];

                var from = tokens[i].Start;
                while (from > 0 && OpeningMarks.Contains(text[from - 1]))
                    from--;
                var to = lastToken.End;
                while (to < text.Length && ClosingMarks.Contains(text[to]))
                    to++;

                if (to - from <= maxChars)
                    spans.Add(new QuoteSpan(from, to, (start.Score + end.Score) / 2));
                break;
            }
        }

        return spans;
    }

    private readonly record struct PhraseMatch(int Length, double Score);

    /// <summary>
    /// The phrase read at <paramref name="at"/>. Up to four words, every word
    /// must be there (a misheard letter or two is fine), first and last on the
    /// edges. A longer phrase may miss a word, or have one more or fewer.
    /// </summary>
    private static PhraseMatch? MatchForward(IReadOnlyList<QuoteToken> tokens, int at, IReadOnlyList<QuoteToken> phrase)
    {
        var n = phrase.Count;
        var strictEdges = n < 5;
        if (strictEdges && !SameWord(tokens[at].Value, phrase[0].Value))
            return null;

        var slack = n >= 5 ? 1 : 0;
        var needed = n <= 4 ? 1.0 : n < 8 ? (n - 1.0) / n : 0.8;
        PhraseMatch? best = null;
        for (var length = Math.Max(1, n - slack); length <= n + slack; length++)
        {
            if (at + length > tokens.Count)
                break;
            if (strictEdges && !SameWord(tokens[at + length - 1].Value, phrase[^1].Value))
                continue;
            if (!strictEdges && !SameWord(tokens[at].Value, phrase[0].Value) && !SameWord(tokens[at].Value, phrase[1].Value))
                continue;

            var common = CommonInOrder(tokens, at, length, phrase);
            var score = (double)common / Math.Max(n, length);
            if (score + 1e-9 < needed)
                continue;
            if (best is null || score > best.Value.Score || (score == best.Value.Score && length == n))
                best = new PhraseMatch(length, score);
        }

        return best;
    }

    private static int CommonInOrder(IReadOnlyList<QuoteToken> tokens, int at, int length, IReadOnlyList<QuoteToken> phrase)
    {
        var table = new int[length + 1, phrase.Count + 1];
        for (var i = 1; i <= length; i++)
        {
            for (var j = 1; j <= phrase.Count; j++)
            {
                table[i, j] = SameWord(tokens[at + i - 1].Value, phrase[j - 1].Value)
                    ? table[i - 1, j - 1] + 1
                    : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table[length, phrase.Count];
    }

    /// <summary>Whitespace collapsed, paragraph breaks kept.</summary>
    public static string Clean(string text)
    {
        var paragraphs = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(paragraph => string.Join(' ', paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
            .Where(paragraph => paragraph.Length > 0);
        return string.Join("\n\n", paragraphs);
    }

    /// <summary>"chapter 3", "3", "III" or a title word against a heading path.</summary>
    public static bool ChapterMatches(IReadOnlyList<string> headings, string hint)
    {
        var wanted = Tokenize(hint)
            .Select(token => token.Value)
            .Where(word => word is not ("chapter" or "kapitel" or "ch" or "kap" or "part" or "del"))
            .ToList();
        if (wanted.Count == 0)
            return false;

        foreach (var heading in headings)
        {
            var words = Tokenize(heading).Select(token => token.Value).ToList();
            var numbers = words.Select(RomanOrNumber).Where(number => number > 0).ToHashSet();
            var all = wanted.All(word =>
                words.Contains(word)
                || (RomanOrNumber(word) is > 0 and var number && numbers.Contains(number)));
            if (all)
                return true;
        }

        return false;
    }

    private static int RomanOrNumber(string word)
    {
        if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return number;
        var total = 0;
        var previous = 0;
        foreach (var c in word.Reverse())
        {
            var value = c switch { 'i' => 1, 'v' => 5, 'x' => 10, 'l' => 50, 'c' => 100, _ => -1 };
            if (value < 0)
                return 0;
            total += value < previous ? -value : value;
            previous = Math.Max(previous, value);
        }

        return total;
    }
}
