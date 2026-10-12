using Nostos.Product.BookText;

namespace Nostos.Product.Alignment;

/// <summary>
/// A match is accepted at <paramref name="MinimumScore"/> when it beats every
/// other place in the book by <paramref name="MinimumMargin"/>. A match a
/// little below that, down to <paramref name="RelaxedScore"/>, is accepted only
/// when nothing else in the book comes within <paramref name="RelaxedMargin"/>:
/// a few misheard or differently spelled words in a passage that occurs once.
/// </summary>
public sealed record MatcherSettings(double MinimumScore = 0.85, double MinimumMargin = 0.12,
    int MinimumTokens = 8, int MaximumTokens = 24,
    double RelaxedScore = 0.80, double RelaxedMargin = 0.20)
{
    /// <summary>
    /// The window for a piece of text. One character of an unspaced script
    /// says less than a word does, so such text is matched over twice as many
    /// tokens: about the same few seconds of speech either way.
    /// </summary>
    public static MatcherSettings For(IReadOnlyCollection<AlignmentToken> tokens) =>
        AlignmentTokens.MostlyUnspaced(tokens)
            ? new MatcherSettings(MinimumTokens: 12, MaximumTokens: 48)
            : new MatcherSettings();
}

public sealed record PassageLocation(int BlockOrder, int GlobalTextOffset, BookTextSourceLocator Locator);
public sealed record PassageMatch(bool Accepted, string Reason, double Score, double RunnerUpScore,
    PassageLocation? Start, PassageLocation? End, string? Passage);

/// <summary>Lexical locator. It never receives expected locations or chapter hints.</summary>
public sealed class PassageMatcher
{
    /// <summary>A running head or page number is at most this many tokens.</summary>
    private const int MaximumFurnitureTokens = 6;

    private sealed record Token(
        string Value,
        int Block,
        int Offset,
        int Length,
        int GlobalOffset,
        int EndBlock,
        int EndOffset,
        int GlobalEndOffset);

    private sealed record Candidate(int Start, int Length, double Score);

    private readonly BookTextExtractedDocument document;
    private readonly List<Token> tokens = [];
    private readonly Dictionary<string, List<int>> positions = new(StringComparer.Ordinal);

    /// <summary>
    /// Running heads found at the top of this PDF's pages, as token keys with
    /// every number written "#". Empty for an EPUB.
    /// </summary>
    private readonly HashSet<string> furnitureHeads = new(StringComparer.Ordinal);

    /// <param name="readingAids">
    /// Per block order, the parts of the block's text that are not read aloud.
    /// Their tokens are left out; every other token keeps its true position.
    /// </param>
    public PassageMatcher(
        BookTextExtractedDocument document,
        IReadOnlyDictionary<int, IReadOnlyList<BookTextRange>>? readingAids = null)
    {
        this.document = document;

        var perBlock = new List<List<AlignmentToken>>(document.Blocks.Count);

        foreach (var block in document.Blocks)
        {
            var words = AlignmentTokens.Split(block.Text);

            if (readingAids is not null &&
                readingAids.TryGetValue(block.Order, out var aids) &&
                aids.Count > 0)
            {
                words.RemoveAll(word => aids.Any(aid =>
                    word.Index >= aid.Start &&
                    word.Index < aid.Start + aid.Length));
            }

            perBlock.Add(words);
        }

        // A PDF block is one page, and a page repeats what the book does not
        // say: its running head and its number. Left in, they sit in the middle
        // of every sentence that crosses a page.
        var furniture = document.Format == BookTextSourceFormat.Pdf
            ? FindPageFurniture(perBlock)
            : null;

        var rawTokens = new List<Token>();
        var global = 0;

        for (var blockIndex = 0; blockIndex < document.Blocks.Count; blockIndex++)
        {
            var block = document.Blocks[blockIndex];
            var words = perBlock[blockIndex];
            var from = furniture?[blockIndex].Head ?? 0;
            var to = words.Count - (furniture?[blockIndex].Foot ?? 0);

            for (var index = from; index < to; index++)
            {
                var word = words[index];

                rawTokens.Add(new Token(
                    word.Value,
                    blockIndex,
                    word.Index,
                    word.Length,
                    global + word.Index,
                    blockIndex,
                    word.Index + word.Length,
                    global + word.Index + word.Length));
            }

            global += block.Text.Length + 1;
        }

        for (var i = 0; i < rawTokens.Count; i++)
        {
            var token = rawTokens[i];

            // EPUB tokenisation is intentionally unchanged. PDF extraction
            // can split one printed word across a line or source segment.
            if (document.Format == BookTextSourceFormat.Pdf)
            {
                while (i + 1 < rawTokens.Count &&
                       CanJoinHyphenation(token, rawTokens[i + 1]))
                {
                    var next = rawTokens[++i];

                    token = token with
                    {
                        Value = token.Value + next.Value,
                        EndBlock = next.EndBlock,
                        EndOffset = next.EndOffset,
                        GlobalEndOffset = next.GlobalEndOffset,
                    };
                }
            }

            if (!positions.TryGetValue(token.Value, out var indexes))
                positions[token.Value] = indexes = [];

            indexes.Add(tokens.Count);
            tokens.Add(token);
        }
    }

    private bool CanJoinHyphenation(Token first, Token second)
    {
        var firstBlock = document.Blocks[first.EndBlock];
        var secondBlock = document.Blocks[second.Block];

        if (second.Offset >= secondBlock.Text.Length ||
            !char.IsLower(secondBlock.Text[second.Offset]))
            return false;

        if (first.EndOffset >= firstBlock.Text.Length ||
            firstBlock.Text[first.EndOffset] != '-')
            return false;

        var afterHyphen = first.EndOffset + 1;

        if (first.EndBlock == second.Block)
        {
            var between = firstBlock.Text.AsSpan(
                afterHyphen,
                second.Offset - afterHyphen);

            if (!between.IsEmpty &&
                !between.ToString().All(char.IsWhiteSpace))
                return false;

            // The PDF extractor may normalize a line break to whitespace.
            // Only join a trailing hyphen followed by a lower-case word;
            // ordinary hyphenated words without intervening whitespace
            // remain separate.
            return !between.IsEmpty ||
                HasSegmentBoundary(
                    firstBlock,
                    afterHyphen,
                    second.Offset);
        }

        // A token may finish at the end of one extracted block and
        // continue at the beginning of the next.
        if (second.Block != first.EndBlock + 1 ||
            !firstBlock.Text.AsSpan(afterHyphen).ToString()
                .All(char.IsWhiteSpace) ||
            !secondBlock.Text.AsSpan(0, second.Offset).ToString()
                .All(char.IsWhiteSpace))
            return false;

        return true;
    }

    private static bool HasSegmentBoundary(
        BookTextArtifactBlock block,
        int from,
        int to)
    {
        return block.SourceSegments.Any(segment =>
        {
            var boundary = segment.TextStart + segment.TextLength;
            return boundary >= from && boundary <= to &&
                   boundary > 0 && boundary < block.Text.Length;
        });
    }

    private static bool IsNumber(string token) => char.IsDigit(token[0]);

    private static string FurnitureKey(IEnumerable<AlignmentToken> tokens) =>
        string.Join(' ', tokens.Select(token => IsNumber(token.Value) ? "#" : token.Value));

    /// <summary>
    /// How many leading and trailing tokens of each page are page furniture.
    /// A head is the longest opening run that many pages share once numbers are
    /// set aside; a foot is a closing page number where pages usually end in one.
    /// A single shared opening word is ordinary prose, not a head.
    /// </summary>
    private (int Head, int Foot)[] FindPageFurniture(List<List<AlignmentToken>> pages)
    {
        var result = new (int Head, int Foot)[pages.Count];
        var threshold = Math.Max(3, (int)Math.Ceiling(pages.Count * 0.025));

        if (pages.Count < threshold * 2)
            return result;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var page in pages)
        {
            for (var length = 1;
                 length <= Math.Min(MaximumFurnitureTokens, page.Count);
                 length++)
            {
                var key = FurnitureKey(page.Take(length));
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        var numberedFeet = pages.Count(page =>
            page.Count > 0 && IsNumber(page[^1].Value)) >= pages.Count / 2;

        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];

            for (var length = Math.Min(MaximumFurnitureTokens, page.Count);
                 length >= 1;
                 length--)
            {
                var key = FurnitureKey(page.Take(length));

                if (counts[key] < threshold ||
                    (length == 1 && key != "#"))
                    continue;

                // A page that is nothing but its head has no text to protect.
                if (length < page.Count)
                {
                    result[index].Head = length;
                    furnitureHeads.Add(key);
                }

                break;
            }

            if (numberedFeet &&
                page.Count > result[index].Head + 1 &&
                IsNumber(page[^1].Value))
                result[index].Foot = 1;
        }

        return result;
    }

    /// <summary>Locates the LAST words of a transcript.</summary>
    public PassageMatch Locate(string transcript) =>
        Locate(transcript, null);

    public PassageMatch Locate(string transcript, MatcherSettings? settings)
    {
        var spoken = AlignmentTokens.Split(transcript);
        settings ??= MatcherSettings.For(spoken);
        Validate(settings);

        return Locate(
            spoken.Select(token => token.Value)
                .TakeLast(settings.MaximumTokens)
                .ToArray(),
            settings);
    }

    /// <summary>
    /// Locates the FIRST words of an excerpt read off a page. A PDF page opens
    /// with its running head, which the matcher has set aside, so the same head
    /// is dropped from the excerpt before it is matched.
    /// </summary>
    public PassageMatch LocateOpening(string excerpt, MatcherSettings? settings = null)
    {
        var words = AlignmentTokens.Split(excerpt);
        settings ??= MatcherSettings.For(words);
        Validate(settings);

        for (var length = Math.Min(MaximumFurnitureTokens, words.Count - 1);
             length >= 1 && furnitureHeads.Count > 0;
             length--)
        {
            if (!furnitureHeads.Contains(FurnitureKey(words.Take(length))))
                continue;

            words.RemoveRange(0, length);
            break;
        }

        return Locate(
            words.Select(token => token.Value)
                .Take(settings.MaximumTokens)
                .ToArray(),
            settings);
    }

    /// <summary>Locates a run of tokens exactly as given.</summary>
    public PassageMatch LocateTokens(
        IEnumerable<AlignmentToken> window,
        MatcherSettings settings)
    {
        Validate(settings);

        return Locate(
            window.Select(token => token.Value)
                .Take(settings.MaximumTokens)
                .ToArray(),
            settings);
    }

    private static void Validate(MatcherSettings settings)
    {
        if (settings.MinimumScore is < 0 or > 1 || settings.MinimumMargin is < 0 or > 1
            || settings.RelaxedScore is < 0 or > 1 || settings.RelaxedMargin is < 0 or > 1
            || settings.RelaxedScore > settings.MinimumScore
            || settings.RelaxedMargin < settings.MinimumMargin
            || settings.MinimumTokens < 2 || settings.MaximumTokens < settings.MinimumTokens)
            throw new ArgumentException("Invalid matcher settings.");
    }

    /// <summary>
    /// Two different tokens that are probably one word: a scanned page's
    /// misread letter, or a spelling the narrator's edition does not share.
    /// Short words are left alone; "then" and "than" are different words.
    /// </summary>
    private static bool Near(string first, string second)
    {
        if (first.Length < 5 || second.Length < 5 ||
            IsNumber(first) || IsNumber(second))
            return false;

        var longer = Math.Max(first.Length, second.Length);
        var allowed = longer <= 6 ? 1 : longer <= 10 ? 2 : 3;

        if (Math.Abs(first.Length - second.Length) > allowed)
            return false;

        Span<int> row = stackalloc int[second.Length + 1];
        Span<int> next = stackalloc int[second.Length + 1];

        for (var j = 0; j <= second.Length; j++)
            row[j] = j;

        for (var i = 1; i <= first.Length; i++)
        {
            next[0] = i;
            var best = next[0];

            for (var j = 1; j <= second.Length; j++)
            {
                next[j] = Math.Min(
                    Math.Min(row[j] + 1, next[j - 1] + 1),
                    row[j - 1] + (first[i - 1] == second[j - 1] ? 0 : 1));

                best = Math.Min(best, next[j]);
            }

            if (best > allowed)
                return false;

            var swap = row;
            row = next;
            next = swap;
        }

        return row[second.Length] <= allowed;
    }

    private PassageMatch Locate(string[] query, MatcherSettings settings)
    {
        if (query.Length < settings.MinimumTokens)
            return Rejected("insufficient_speech");

        var exact = new List<Candidate>();

        if (positions.TryGetValue(query[0], out var starts))
        {
            foreach (var start in starts)
            {
                if (start + query.Length <= tokens.Count &&
                    query.Select((value, i) =>
                        value == tokens[start + i].Value).All(x => x))
                {
                    exact.Add(new Candidate(start, query.Length, 1));
                }
            }
        }

        if (exact.Count > 1)
            return Rejected("ambiguous", 1, 1);

        // Enumerate likely starts from matching tokens. The ±edit allowance
        // admits insertions/deletions.
        var edits = (int)Math.Ceiling(
            query.Length * (1 - settings.RelaxedScore)) + 1;

        var candidateStarts = new HashSet<int>();

        foreach (var seed in query
            .Select((word, i) => (word, i))
            .DistinctBy(x => x.word)
            .OrderBy(x => positions.TryGetValue(x.word, out var indexes)
                ? indexes.Count
                : int.MaxValue)
            .Take(4))
        {
            if (!positions.TryGetValue(seed.word, out var indexes))
                continue;

            foreach (var index in indexes)
            {
                for (var delta = -edits; delta <= edits; delta++)
                {
                    if (index - seed.i + delta >= 0)
                        candidateStarts.Add(index - seed.i + delta);
                }
            }
        }

        var candidates = new List<Candidate>(exact);

        // Costs are in half-edits: a different word costs 2, a near miss 1.
        var near = new Dictionary<(int, string), bool>();

        int Substitution(int queryIndex, string token)
        {
            if (query[queryIndex] == token)
                return 0;

            if (token.Length > 64 || query[queryIndex].Length > 64)
                return 2;

            if (!near.TryGetValue((queryIndex, token), out var close))
                near[(queryIndex, token)] = close = Near(query[queryIndex], token);

            return close ? 1 : 2;
        }

        foreach (var start in candidateStarts)
        {
            var maximumLength = Math.Min(
                query.Length + edits,
                tokens.Count - start);

            if (maximumLength < query.Length - edits)
                continue;

            var row = new int[maximumLength + 1];

            for (var j = 0; j <= maximumLength; j++)
                row[j] = j * 2;

            for (var i = 1; i <= query.Length; i++)
            {
                var next = new int[maximumLength + 1];
                next[0] = i * 2;

                for (var j = 1; j <= maximumLength; j++)
                {
                    next[j] = Math.Min(
                        Math.Min(row[j] + 2, next[j - 1] + 2),
                        row[j - 1] +
                        Substitution(i - 1, tokens[start + j - 1].Value));
                }

                row = next;
            }

            for (var length = Math.Max(
                     settings.MinimumTokens,
                     query.Length - edits);
                 length <= maximumLength;
                 length++)
            {
                var score = 1 - row[length] /
                    (2d * Math.Max(query.Length, length));

                if (score >= settings.RelaxedScore - settings.RelaxedMargin)
                    candidates.Add(new Candidate(start, length, score));
            }
        }

        var ordered = candidates
            .OrderByDescending(x => x.Score)
            .ThenBy(x => Math.Abs(x.Length - query.Length))
            .ThenBy(x => x.Start)
            .ToList();

        if (ordered.Count == 0)
            return Rejected("no_match");

        var best = ordered[0];

        // Overlapping windows describe the same passage, not independent alternatives.
        var runner = ordered.FirstOrDefault(x =>
            x.Start + x.Length <= best.Start ||
            x.Start >= best.Start + best.Length);

        var runnerScore = runner?.Score ?? 0;

        if (best.Score < settings.RelaxedScore)
            return Rejected("weak_match", best.Score, runnerScore);

        var margin = best.Score - runnerScore;

        if (best.Score < settings.MinimumScore)
        {
            if (margin < settings.RelaxedMargin)
                return Rejected("weak_match", best.Score, runnerScore);
        }
        else if (margin < settings.MinimumMargin)
        {
            return Rejected("ambiguous", best.Score, runnerScore);
        }

        var first = tokens[best.Start];
        var last = tokens[best.Start + best.Length - 1];

        var passage = string.Join(
            " ",
            tokens.Skip(best.Start)
                .Take(best.Length)
                .Select(x => x.Value));

        return new PassageMatch(
            true,
            "matched",
            best.Score,
            runnerScore,
            Location(first, false),
            Location(last, true),
            passage);
    }

    private PassageLocation Location(Token token, bool end)
    {
        var blockIndex = end ? token.EndBlock : token.Block;
        var block = document.Blocks[blockIndex];
        var offset = end ? token.EndOffset : token.Offset;
        var character = end ? offset - 1 : offset;

        var segment = block.SourceSegments.FirstOrDefault(x =>
            character >= x.TextStart &&
            character < x.TextStart + x.TextLength)
            ?? throw new InvalidDataException(
                "Extracted token has no source provenance.");

        var sourceOffset = offset - segment.TextStart;

        BookTextSourceLocator locator = segment.Locator switch
        {
            EpubBookTextSourceLocator epub => epub with
            {
                StartTextOffset =
                    (epub.StartTextOffset ?? 0) + sourceOffset,
                EndTextOffset =
                    (epub.StartTextOffset ?? 0) + sourceOffset,
            },

            PdfBookTextSourceLocator pdf => pdf with
            {
                StartTextOffset =
                    (pdf.StartTextOffset ?? 0) + sourceOffset,
                EndTextOffset =
                    (pdf.StartTextOffset ?? 0) + sourceOffset,
            },

            _ => throw new InvalidDataException(
                "The extracted token has unsupported source provenance."),
        };

        return new PassageLocation(
            block.Order,
            end ? token.GlobalEndOffset : token.GlobalOffset,
            locator);
    }

    private static PassageMatch Rejected(
        string reason,
        double score = 0,
        double runner = 0) =>
        new(false, reason, score, runner, null, null, null);
}
