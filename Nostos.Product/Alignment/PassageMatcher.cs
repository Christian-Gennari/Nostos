using System.Text.RegularExpressions;
using Nostos.Product.BookText;

namespace Nostos.Product.Alignment;

public sealed record MatcherSettings(double MinimumScore = 0.85, double MinimumMargin = 0.12,
    int MinimumTokens = 8, int MaximumTokens = 24);

public sealed record PassageLocation(int BlockOrder, int GlobalTextOffset, BookTextSourceLocator Locator);
public sealed record PassageMatch(bool Accepted, string Reason, double Score, double RunnerUpScore,
    PassageLocation? Start, PassageLocation? End, string? Passage);

/// <summary>Lexical locator. It never receives expected locations or chapter hints.</summary>
public sealed class PassageMatcher
{
    private static readonly Regex Words = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

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

    public PassageMatcher(BookTextExtractedDocument document)
    {
        this.document = document;

        var rawTokens = new List<Token>();
        var global = 0;

        for (var blockIndex = 0; blockIndex < document.Blocks.Count; blockIndex++)
        {
            var block = document.Blocks[blockIndex];

            foreach (Match word in Words.Matches(block.Text))
            {
                rawTokens.Add(new Token(
                    word.Value.ToLowerInvariant(),
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

    public PassageMatch Locate(string transcript, MatcherSettings settings)
    {
        if (settings.MinimumScore is < 0 or > 1 || settings.MinimumMargin is < 0 or > 1
            || settings.MinimumTokens < 2 || settings.MaximumTokens < settings.MinimumTokens)
            throw new ArgumentException("Invalid matcher settings.");

        var query = Words.Matches(transcript)
            .Select(x => x.Value.ToLowerInvariant())
            .TakeLast(settings.MaximumTokens)
            .ToArray();

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
            query.Length * (1 - settings.MinimumScore)) + 1;

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

        foreach (var start in candidateStarts)
        {
            var maximumLength = Math.Min(
                query.Length + edits,
                tokens.Count - start);

            if (maximumLength < query.Length - edits)
                continue;

            var row = Enumerable.Range(
                0, maximumLength + 1).ToArray();

            for (var i = 1; i <= query.Length; i++)
            {
                var next = new int[maximumLength + 1];
                next[0] = i;

                for (var j = 1; j <= maximumLength; j++)
                {
                    next[j] = Math.Min(
                        Math.Min(row[j] + 1, next[j - 1] + 1),
                        row[j - 1] +
                        (query[i - 1] == tokens[start + j - 1].Value
                            ? 0
                            : 1));
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
                    (double)Math.Max(query.Length, length);

                if (score >= settings.MinimumScore - settings.MinimumMargin)
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

        if (best.Score < settings.MinimumScore)
            return Rejected("weak_match", best.Score, runnerScore);

        if (best.Score - runnerScore < settings.MinimumMargin)
            return Rejected("ambiguous", best.Score, runnerScore);

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
