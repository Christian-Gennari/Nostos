using System.Text.Json;

namespace Nostos.Backend.Tests.Knowledge.Quality;

/// <summary>
/// Metric computation and machine-readable reporting for the comparison.
/// Recall is measured over the case's gold set; contamination counts case
/// distractors inside the top-k. A missing gold hit ranks 0.
/// </summary>
internal static class RetrievalQualityMetrics
{
    public sealed record ChannelMetrics(
        IReadOnlyList<string> Order,
        double RecallAt1,
        double RecallAt3,
        double RecallAt6,
        int FirstGoldRank,
        int ContaminationAt3,
        double ContaminationFractionAt3,
        int ContaminationAt6,
        double ContaminationFractionAt6);

    public sealed record StrategyReport(
        string Strategy,
        ChannelMetrics Notes,
        ChannelMetrics Topics,
        ChannelMetrics Passages);

    public sealed record CaseReport(
        string Case,
        string Query,
        string Description,
        IReadOnlyList<string> QueryVariants,
        StrategyReport MultiQuery,
        StrategyReport LegacyLiteral);

    public static ChannelMetrics Measure<T>(
        IReadOnlyList<T> ranked,
        Func<T, string> keyOf,
        IReadOnlySet<string> gold,
        IReadOnlySet<string> distractors)
        where T : notnull
    {
        var order = ranked.Select(keyOf).ToList();

        static double Recall(IReadOnlyList<string> top, IReadOnlySet<string> goldSet) =>
            goldSet.Count == 0 ? 1d : (double)top.Count(goldSet.Contains) / goldSet.Count;

        var first = 0;
        for (var i = 0; i < order.Count; i++)
        {
            if (gold.Contains(order[i]))
            {
                first = i + 1;
                break;
            }
        }

        var top3 = order.Take(3).ToList();
        var top6 = order.Take(6).ToList();
        var c3 = top3.Count(distractors.Contains);
        var c6 = top6.Count(distractors.Contains);

        return new ChannelMetrics(
            order,
            Recall(order.Take(1).ToList(), gold),
            Recall(top3, gold),
            Recall(top6, gold),
            first,
            c3,
            top3.Count == 0 ? 0d : (double)c3 / top3.Count,
            c6,
            top6.Count == 0 ? 0d : (double)c6 / top6.Count);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
    };

    public static string ToJson(CaseReport report) =>
        JsonSerializer.Serialize(report, Json);

    /// <summary>
    /// Writes the report to a temp run dir (never committed) and returns the
    /// path. Each case overwrites its own file so a run stays reproducible.
    /// </summary>
    public static string WriteRunFile(CaseReport report)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nostos-retrieval-quality");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{report.Case.ToLowerInvariant()}.json");
        File.WriteAllText(path, ToJson(report) + Environment.NewLine);
        return path;
    }
}
