namespace Nostos.Backend.Search;

/// <summary>
/// One ranked candidate list entering a fusion, best candidate first.
/// <see cref="Weight"/> scales every contribution this list makes.
/// </summary>
public sealed record RankedList<TKey>(IReadOnlyList<TKey> Keys, double Weight = 1d)
    where TKey : notnull;

/// <summary>A fused candidate and the score it earned across all lists.</summary>
public readonly record struct FusedRank<TKey>(TKey Key, double Score)
    where TKey : notnull;

/// <summary>
/// Reciprocal Rank Fusion (issue #683): merges ranked lists whose scores are not
/// comparable — bm25 and cosine similarity here — using ranks alone.
///
/// <c>score(candidate) = Σ weight / (k + rank)</c> over every list that
/// contains the candidate, with <c>rank</c> starting at 1. A candidate found by
/// several channels therefore outranks one found by a single channel at the
/// same position, and <c>k</c> damps how much the very top ranks dominate.
/// </summary>
public static class ReciprocalRankFusion
{
    /// <summary>The constant from the original RRF paper (Cormack et al., 2009).</summary>
    public const int DefaultK = 60;

    /// <summary>
    /// Fuses <paramref name="lists"/> into one list, best first. The result is
    /// deterministic: equal scores keep first-seen order, so an earlier list
    /// wins a tie against a later one. A key repeated within one list counts
    /// once, at its best rank.
    /// </summary>
    public static IReadOnlyList<FusedRank<TKey>> Fuse<TKey>(
        IReadOnlyList<RankedList<TKey>> lists,
        int k = DefaultK,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentOutOfRangeException.ThrowIfNegative(k);

        var scores = new Dictionary<TKey, double>(comparer);
        var order = new List<TKey>();

        foreach (var list in lists)
        {
            if (list.Weight <= 0d || !double.IsFinite(list.Weight))
                continue;

            var seen = new HashSet<TKey>(comparer);
            foreach (var key in list.Keys)
            {
                if (!seen.Add(key))
                    continue;

                var contribution = list.Weight / (k + seen.Count);
                if (scores.TryGetValue(key, out var score))
                {
                    scores[key] = score + contribution;
                }
                else
                {
                    scores[key] = contribution;
                    order.Add(key);
                }
            }
        }

        // OrderByDescending is a stable sort, which is what makes ties
        // deterministic.
        return order
            .Select(key => new FusedRank<TKey>(key, scores[key]))
            .OrderByDescending(item => item.Score)
            .ToList();
    }
}
