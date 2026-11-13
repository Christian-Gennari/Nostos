using Nostos.Backend.Data.Models;

namespace Nostos.Product.Alignment;

public sealed class AlignmentException(
    string code,
    int statusCode,
    string message) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed class AlignmentOptions
{
    public const string SectionName = "Alignment";

    public double ClipSeconds { get; set; } = 20;
    public double ProbeSeconds { get; set; } = 20;
    public int MaxProbes { get; set; } = 5;
    public int MatchToleranceCharacters { get; set; } = 40;
    public int ConsistencyToleranceCharacters { get; set; } = 40;
    // Measured live: a located start can fall a few seconds after the sentence
    // when the narrator is quicker than the clip maths assumes. Starting a little
    // early costs nothing; starting late loses the sentence.
    public double LeadInSeconds { get; set; } = 3.0;
    public double MaximumLocalRateRatio { get; set; } = 5;

    // Historical configuration name. Applies to both EPUB and PDF.
    // Keep the existing Alignment:MaxEpubBytes binding.
    public long MaxEpubBytes { get; set; } =
        200L * 1024 * 1024;

    public long MaxCachedCharacters { get; set; } =
        20_000_000;

    /// <summary>
    /// When a transcription has not answered after this many seconds, the same
    /// clip is asked for a second time and the first answer is used. 0, the
    /// default, never asks twice. It suits a hosted transcriber whose answers
    /// are usually quick and occasionally very slow; a transcriber that is
    /// simply slow would only be given twice the work.
    /// </summary>
    public double HedgeAfterSeconds { get; set; }

    /// <summary>
    /// Limits the cost and size of reverse-alignment probes, including
    /// when options are constructed outside the normal DI registration.
    /// </summary>
    public void NormalizeProbeSettings()
    {
        MaxProbes = Math.Clamp(MaxProbes, 1, 8);

        ProbeSeconds = double.IsNaN(ProbeSeconds)
            ? 20
            : Math.Clamp(ProbeSeconds, 5, 30);
    }

    /// <summary>
    /// Returns a copy with reverse-search probe and lead-in settings
    /// normalized so callers can bound search options without dropping any
    /// configured properties.
    /// </summary>
    public AlignmentOptions CloneNormalizedForSearch()
    {
        var copy = (AlignmentOptions)MemberwiseClone();
        copy.NormalizeProbeSettings();

        if (!double.IsFinite(copy.LeadInSeconds) ||
            copy.LeadInSeconds < 0)
            copy.LeadInSeconds = 0;

        copy.LeadInSeconds = Math.Min(
            copy.LeadInSeconds,
            copy.ProbeSeconds);

        return copy;
    }
}

public sealed record AlignmentLocator(
    int SpineIndex,
    string ResourceHref,
    string? Cfi,
    int? TextOffset);

public sealed record AlignmentPdfLocator(
    int PageIndex,
    int PageNumber,
    string? PageLabel,
    int TextOffset);

public sealed record AlignmentLocateResult(
    bool Located,
    string? Reason,
    double? Score,
    string? Excerpt,
    AlignmentLocator? Locator,
    bool AnchorStored = false,
    AlignmentPdfLocator? Pdf = null,
    string? SourceText = null)
{
    public static AlignmentLocateResult Declined(string reason) =>
        new(false, reason, null, null, null);
}

public sealed record AlignmentLocateAudioResult(
    bool Located,
    string? Reason,
    double? PositionSeconds,
    double? ApproximatePositionSeconds,
    int Probes,
    double? Score,
    bool AnchorStored)
{
    public static AlignmentLocateAudioResult Declined(
        string reason,
        int probes = 0,
        double? approximatePositionSeconds = null) =>
        new(
            false,
            reason,
            null,
            approximatePositionSeconds,
            probes,
            null,
            false);
}

public interface IAlignmentService
{
    Task<AlignmentLocateResult> LocateAsync(
        AudioBookModel source,
        EBookModel target,
        double positionSeconds,
        CancellationToken ct = default);

    Task<AlignmentLocateAudioResult> LocateAudioAsync(
        EBookModel source,
        AudioBookModel target,
        string excerpt,
        CancellationToken ct = default);
}
