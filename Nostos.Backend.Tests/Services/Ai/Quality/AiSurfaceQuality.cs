// What "good" means for embeddings, speech-to-text and read aloud (#1466),
// as code. docs/ask-nostos-v3-quality-gate.md §11 states the thresholds; the
// scorers here are what checks them, offline against fakes and, when opted
// in, against a real endpoint (AiSurfaceLiveProbeTests).
namespace Nostos.Backend.Tests.Services.Ai.Quality;

using System.Globalization;
using System.Text;

/// <summary>
/// One retrieval question: a reader's paraphrase and the passage it means,
/// among passages that share words with the question but not its meaning.
/// </summary>
internal sealed record EmbeddingGoldCase(string Query, int GoldIndex);

internal static class EmbeddingQuality
{
    /// <summary>
    /// Passages from the quality-bed library's world (invented words, no
    /// copyrighted text). Several share surface words on purpose.
    /// </summary>
    public static readonly string[] Passages =
    [
        "Ropes coil best when they are dry: lay each rope clockwise upon the thwart, flakes falling left to right.",
        "The lighthouse keeper said the only way through a long night watch is to give the lamp your whole attention.",
        "We walked the harbor every evening with a lantern, and that walk was where I did my real thinking.",
        "Harbor dues rose again this spring; the lantern tax alone cost the fleet a week of catch.",
        "Repair is listening before it is fixing: plane with the grain and stop when the wood sings.",
        "The ferry crossing moved upstream after the spring flood of the black year.",
        "Porridge on the stove before first light, and the kettle singing while the house still slept.",
        "Salt whitened the rails by morning; the spray had found every seam in the dark rain.",
    ];

    public static readonly EmbeddingGoldCase[] Cases =
    [
        new("how should lines be stored so they don't tangle", 0),
        new("staying focused through the night", 1),
        new("where do I do my best thinking", 2),
        new("what did the fees cost the boats", 3),
        new("fixing wood means paying attention to it first", 4),
        new("the river crossing changed place after high water", 5),
        new("an early breakfast before dawn", 6),
        new("sea spray dried white on the boat", 7),
    ];

    /// <summary>Thresholds a configured embedding model must meet on <see cref="Cases"/>.</summary>
    public const double MinRecallAt1 = 0.75;
    public const double MinMeanReciprocalRank = 0.85;

    /// <summary>A second, same-text call may differ by at most this cosine distance.</summary>
    public const double MaxRepeatDrift = 0.001;

    public sealed record Score(double RecallAt1, double MeanReciprocalRank, IReadOnlyList<int> GoldRanks)
    {
        public bool Good => RecallAt1 >= MinRecallAt1 && MeanReciprocalRank >= MinMeanReciprocalRank;
    }

    public static Score Evaluate(IReadOnlyList<float[]> passageVectors, IReadOnlyList<float[]> queryVectors)
    {
        var ranks = new List<int>();
        for (var q = 0; q < Cases.Length; q++)
        {
            var scores = passageVectors.Select(p => Cosine(queryVectors[q], p)).ToArray();
            var gold = scores[Cases[q].GoldIndex];
            // Ties count against the gold passage: a model that cannot tell
            // passages apart must not score as if it could.
            ranks.Add(1 + scores.Where((s, i) => i != Cases[q].GoldIndex && s >= gold).Count());
        }

        return new Score(
            ranks.Count(r => r == 1) / (double)ranks.Count,
            ranks.Average(r => 1.0 / r),
            ranks);
    }

    /// <summary>Cosine similarity; a zero vector has no direction and scores 0.</summary>
    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }
}

internal static class TranscriptQuality
{
    /// <summary>Clean English dictation: at most this word error rate.</summary>
    public const double MaxWordErrorRate = 0.15;

    /// <summary>
    /// Word error rate: word-level edit distance over the reference length,
    /// after lower-casing and dropping punctuation, so "Coil it." and
    /// "coil it" are the same transcript.
    /// </summary>
    public static double WordErrorRate(string reference, string hypothesis)
    {
        var r = Words(reference);
        var h = Words(hypothesis);
        if (r.Length == 0)
            return h.Length == 0 ? 0 : 1;

        var previous = Enumerable.Range(0, h.Length + 1).ToArray();
        for (var i = 1; i <= r.Length; i++)
        {
            var current = new int[h.Length + 1];
            current[0] = i;
            for (var j = 1; j <= h.Length; j++)
            {
                var substitution = previous[j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }

            previous = current;
        }

        return previous[h.Length] / (double)r.Length;
    }

    public static string[] Words(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormC).ToLower(CultureInfo.InvariantCulture))
            builder.Append(char.IsLetterOrDigit(c) || c == '\'' ? c : ' ');
        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>What read aloud must hand the player: real MP3 of a sensible length.</summary>
internal static class SpeechAudioQuality
{
    /// <summary>Speech runs at roughly 10–25 characters a second.</summary>
    public const double MinCharsPerSecond = 8;
    public const double MaxCharsPerSecond = 30;

    private static readonly int[] Mpeg1Layer3Kbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] Mpeg2Layer3Kbps = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
    private static readonly int[] Mpeg1Rates = [44100, 48000, 32000, 0];

    /// <summary>
    /// Walks MPEG Layer III frames after an optional ID3v2 tag. Null when the
    /// bytes are not MP3 (an HTML error page served as audio/mpeg, a WAV,
    /// silence of zero frames); otherwise the frame count and duration.
    /// </summary>
    public static (int Frames, double Seconds)? ReadMp3(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        if (bytes.Length >= 10 && bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3')
            offset = 10 + ((bytes[6] & 0x7F) << 21 | (bytes[7] & 0x7F) << 14 | (bytes[8] & 0x7F) << 7 | (bytes[9] & 0x7F));

        var frames = 0;
        var seconds = 0.0;
        while (offset + 4 <= bytes.Length)
        {
            if (bytes[offset] != 0xFF || (bytes[offset + 1] & 0xE0) != 0xE0)
                break;
            var version = (bytes[offset + 1] >> 3) & 0x03; // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5
            var layer = (bytes[offset + 1] >> 1) & 0x03;   // 1 = Layer III
            var bitrateIndex = bytes[offset + 2] >> 4;
            var rateIndex = (bytes[offset + 2] >> 2) & 0x03;
            var padding = (bytes[offset + 2] >> 1) & 0x01;
            if (layer != 1 || version == 1 || rateIndex == 3)
                break;
            var mpeg1 = version == 3;
            var kbps = (mpeg1 ? Mpeg1Layer3Kbps : Mpeg2Layer3Kbps)[bitrateIndex];
            var rate = Mpeg1Rates[rateIndex] / (mpeg1 ? 1 : version == 2 ? 2 : 4);
            if (kbps == 0)
                break;
            var samples = mpeg1 ? 1152 : 576;
            var length = samples / 8 * kbps * 1000 / rate + padding;
            if (offset + length > bytes.Length)
                break;
            frames++;
            seconds += samples / (double)rate;
            offset += length;
        }

        return frames >= 2 ? (frames, seconds) : null;
    }

    public static string? Check(ReadOnlySpan<byte> audio, string text)
    {
        if (ReadMp3(audio) is not { } mp3)
            return "the audio is not MP3 (fewer than two Layer III frames)";
        var rate = text.Length / mp3.Seconds;
        return rate is >= MinCharsPerSecond and <= MaxCharsPerSecond
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"{mp3.Seconds:F1} s of audio for {text.Length} characters ({rate:F1} chars/s, expected {MinCharsPerSecond}-{MaxCharsPerSecond})");
    }
}
