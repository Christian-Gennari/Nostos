namespace Nostos.Product.BookText;

public sealed class BookTextOptions
{
    public const string SectionName = "BookText";

    public long MaxSourceBytes { get; set; } = 512L * 1024 * 1024;
    public int TargetChunkChars { get; set; } = 1500;
    public int MaxChunkChars { get; set; } = 2500;
    public int OverlapChars { get; set; } = 180;
    public int MaxSearchCandidates { get; set; } = 24;
    public int MaxPassages { get; set; } = 8;
    public int MaxPassageChars { get; set; } = 1800;
    public int MaxTotalPassageChars { get; set; } = 12000;
    public int NeighborRadius { get; set; } = 1;
    public int StaleProcessingMinutes { get; set; } = 15;

    // Passage embeddings (issue #683). Only used when the host has an active
    // embedding provider; with none, these are inert.
    public int EmbeddingBatchSize { get; set; } = 16;
    public int EmbeddingIdlePollSeconds { get; set; } = 30;
    public int EmbeddingFailureBackoffSeconds { get; set; } = 300;
}
