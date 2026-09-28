namespace Nostos.Backend.Tests.Knowledge.Quality;

/// <summary>
/// Predefined query cases for the retrieval comparison (issue #566).
/// Keys ("G7", "D8a", …) resolve to note ids through
/// <see cref="RetrievalQualityCorpus.CorpusIds"/>; passages resolve to
/// (book, ordinal) pairs. Gold sets are fixed before measuring.
/// </summary>
internal sealed record RetrievalQualityCase(
    string Id,
    string Description,
    string Query,
    IReadOnlyList<string> GoldNoteKeys,
    IReadOnlyList<string> DistractorNoteKeys,
    string? GoldConceptKey,
    IReadOnlyList<(string BookKey, int Ordinal)> GoldPassages);

internal static class RetrievalQualityCases
{
    public static readonly RetrievalQualityCase C7LowOverlap = new(
        Id: "C7",
        Description: "Low lexical overlap: the question shares one content token "
            + "with the gold note and reformulates the rest "
            + "(\"reading replaces thinking\" vs borrowed opinions).",
        Query: "reading replaces thinking",
        GoldNoteKeys: ["G7"],
        DistractorNoteKeys: ["D7"],
        GoldConceptKey: "BorrowedJudgment",
        GoldPassages: []);

    public static readonly RetrievalQualityCase C8Distractors = new(
        Id: "C8",
        Description: "Distractor retrieval: two notes share surface-term subsets "
            + "of the query while the gold note carries every term.",
        Query: "harbor lighthouse storm",
        GoldNoteKeys: ["G8"],
        DistractorNoteKeys: ["D8a", "D8b"],
        GoldConceptKey: "SafePassage",
        GoldPassages: []);

    public static readonly RetrievalQualityCase C9CrossBook = new(
        Id: "C9",
        Description: "Cross-book evidence: the practice (Gray Ledger) and its "
            + "qualification/tension (Red Ledger) must both be retrievable, "
            + "as must both indexed passages.",
        Query: "dawn launching tides",
        GoldNoteKeys: ["G9a", "G9b"],
        DistractorNoteKeys: ["D9"],
        GoldConceptKey: "EarlyWater",
        GoldPassages: [("GrayBook", 0), ("RedBook", 1)]);

    public static readonly RetrievalQualityCase C11FollowUp = new(
        Id: "C11",
        Description: "Follow-up on the qualification: \"neap tides bar\" must stay "
            + "tied to the exact Red Ledger note and passage via stable handles.",
        Query: "neap tides bar",
        GoldNoteKeys: ["G9b"],
        DistractorNoteKeys: ["D9"],
        GoldConceptKey: "NarrowMargin",
        GoldPassages: [("RedBook", 1)]);

    public static IReadOnlyList<RetrievalQualityCase> All =>
        [C7LowOverlap, C8Distractors, C9CrossBook, C11FollowUp];

    public static Guid NoteKey(
        RetrievalQualityCorpus.CorpusIds ids, string key) => key switch
    {
        "G7" => ids.G7,
        "D7" => ids.D7,
        "G8" => ids.G8,
        "D8a" => ids.D8a,
        "D8b" => ids.D8b,
        "G9a" => ids.G9a,
        "G9b" => ids.G9b,
        "D9" => ids.D9,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    public static Guid ConceptKey(
        RetrievalQualityCorpus.CorpusIds ids, string key) => key switch
    {
        "BorrowedJudgment" => ids.BorrowedJudgment,
        "SafePassage" => ids.SafePassage,
        "MarketDay" => ids.MarketDay,
        "EarlyWater" => ids.EarlyWater,
        "NarrowMargin" => ids.NarrowMargin,
        "VineyardHours" => ids.VineyardHours,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    public static Guid BookKey(
        RetrievalQualityCorpus.CorpusIds ids, string key) => key switch
    {
        "GrayBook" => ids.GrayBook,
        "RedBook" => ids.RedBook,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };
}
