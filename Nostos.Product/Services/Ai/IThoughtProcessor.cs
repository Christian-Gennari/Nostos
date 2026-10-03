namespace Nostos.Backend.Services.Ai;

/// <summary>
/// The three user-controlled post-processing modes (issue #262 §7, §8) and the
/// one rule that governs all of them:
///
/// <para><b>Clarify the user's thought; do not improve the user's argument.</b></para>
///
/// The ids are the exact strings stored on <c>Notes.ProcessingMode</c> and sent
/// on the wire; they are part of the contract and are never renamed.
/// </summary>
public static class ThoughtProcessingModes
{
    /// <summary>The default. The user's expressed wording, unchanged, with no provider call.</summary>
    public const string Verbatim = "verbatim";

    /// <summary>Remove filler/disfluency, fix grammar and punctuation, smooth phrasing; meaning, stance, uncertainty and voice survive.</summary>
    public const string LightPolish = "light_polish";

    /// <summary>Reorganise and tighten so the intended idea is clearer, without introducing anything the user did not express.</summary>
    public const string Clarify = "clarify";

    /// <summary>The closed set, in presentation order.</summary>
    public static readonly IReadOnlyList<string> All = [Verbatim, LightPolish, Clarify];

    /// <summary>True when the mode is absent or is exactly <see cref="Verbatim"/>. Those never call the provider.</summary>
    public static bool IsVerbatim(string? mode) =>
        string.IsNullOrWhiteSpace(mode)
        || string.Equals(mode.Trim(), Verbatim, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the mode is one of the three supported ids (case-insensitive).</summary>
    public static bool IsSupported(string? mode) =>
        All.Any(candidate => string.Equals(candidate, mode?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The canonical lower-case id. An absent or unrecognised value normalises to
    /// <see cref="Verbatim"/>: an instruction we do not understand is a no-op,
    /// never a guess at what the caller meant, and never a rewrite.
    /// </summary>
    public static string Normalize(string? mode) =>
        All.FirstOrDefault(candidate => string.Equals(candidate, mode?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Verbatim;
}

/// <summary>
/// The outcome of one processing pass. <see cref="Mode"/> is the mode the stored
/// text ACTUALLY reflects — a request that fell back to the raw transcript
/// reports <see cref="ThoughtProcessingModes.Verbatim"/>, because that is what
/// the text now is.
/// </summary>
/// <param name="Text">The text to store. For a fallback this is the raw transcript, unchanged.</param>
/// <param name="Mode">The effective mode of <paramref name="Text"/>.</param>
/// <param name="ProviderCalled">True when an LLM round trip was made; false for the verbatim short-circuit.</param>
/// <param name="FellBackToRaw">True when the provider was called but produced no usable text, so the raw transcript was kept.</param>
public sealed record ThoughtProcessingResult(
    string Text,
    string Mode,
    bool ProviderCalled,
    bool FellBackToRaw = false);

/// <summary>
/// Applies one post-processing mode to the user's own thought text (issue #262
/// §7, §8).
///
/// The seam is deliberately narrow: the processor receives ONLY the user's
/// thought. A quote (<c>SelectedText</c>, the passage from the book) is never
/// passed here, so no mode can rewrite it. A verbatim (or absent, or unknown)
/// mode is a no-op that makes no provider call.
/// </summary>
public interface IThoughtProcessor
{
    /// <summary>
    /// Applies <paramref name="mode"/> to <paramref name="text"/> and returns the
    /// text to store. The caller keeps the original raw transcript separately;
    /// this method never discards it.
    /// </summary>
    Task<ThoughtProcessingResult> ProcessAsync(
        string text,
        string mode,
        CancellationToken ct = default);
}
