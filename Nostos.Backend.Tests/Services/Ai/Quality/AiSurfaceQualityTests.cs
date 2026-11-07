namespace Nostos.Backend.Tests.Services.Ai.Quality;

using System.Text;
using FluentAssertions;
using Xunit;

/// <summary>
/// Offline proof that the embedding, transcript and speech-audio checks
/// measure what docs/ask-nostos-v3-quality-gate.md §11 says (issue #1466):
/// a perfect model passes, a broken one fails, and the gold set cannot be
/// passed by word matching alone.
/// </summary>
public sealed class AiSurfaceQualityTests
{
    // ---- Embeddings -------------------------------------------------------

    [Fact]
    public void A_model_that_puts_each_question_on_its_passage_is_good()
    {
        var passages = EmbeddingQuality.Passages.Select((_, i) => OneHot(i)).ToList();
        var queries = EmbeddingQuality.Cases.Select(c => OneHot(c.GoldIndex)).ToList();

        var score = EmbeddingQuality.Evaluate(passages, queries);

        score.RecallAt1.Should().Be(1);
        score.MeanReciprocalRank.Should().Be(1);
        score.Good.Should().BeTrue();
    }

    [Fact]
    public void An_endpoint_that_returns_one_vector_for_everything_is_not_good()
    {
        // A proxy or cache answering every input with the same vector looks
        // healthy to the transport checks; only ranking exposes it.
        var same = new float[] { 0.3f, 0.1f, 0.9f };
        var passages = EmbeddingQuality.Passages.Select(_ => same).ToList();
        var queries = EmbeddingQuality.Cases.Select(_ => same).ToList();

        var score = EmbeddingQuality.Evaluate(passages, queries);

        score.RecallAt1.Should().Be(0);
        score.Good.Should().BeFalse();
    }

    [Fact]
    public void Zero_vectors_are_not_good()
    {
        var zero = new float[8];
        var score = EmbeddingQuality.Evaluate(
            EmbeddingQuality.Passages.Select(_ => zero).ToList(),
            EmbeddingQuality.Cases.Select(_ => zero).ToList());

        score.Good.Should().BeFalse();
    }

    [Fact]
    public void Word_matching_alone_does_not_pass_the_gold_set()
    {
        // The questions are paraphrases. If a bag of words passed, the set
        // would not tell a semantic model from lexical search.
        var vocabulary = EmbeddingQuality.Passages
            .Concat(EmbeddingQuality.Cases.Select(c => c.Query))
            .SelectMany(TranscriptQuality.Words)
            .Distinct(StringComparer.Ordinal)
            .Select((word, index) => (word, index))
            .ToDictionary(pair => pair.word, pair => pair.index, StringComparer.Ordinal);
        var passages = EmbeddingQuality.Passages.Select(p => BagOfWords(p, vocabulary)).ToList();
        var queries = EmbeddingQuality.Cases.Select(c => BagOfWords(c.Query, vocabulary)).ToList();

        var score = EmbeddingQuality.Evaluate(passages, queries);

        score.Good.Should().BeFalse($"lexical recall@1 {score.RecallAt1:F2}, MRR {score.MeanReciprocalRank:F2}");
    }

    // ---- Speech-to-text ---------------------------------------------------

    [Theory]
    [InlineData("Coil each rope clockwise.", "coil each rope clockwise", 0)]
    [InlineData("the south jetty at dawn", "the north jetty at dawn", 0.2)]
    [InlineData("the south jetty at dawn", "the south jetty", 0.4)]
    [InlineData("kvällspromenaden går förbi fyren", "Kvällspromenaden går förbi fyren.", 0)]
    [InlineData("", "", 0)]
    public void Word_error_rate_ignores_case_and_punctuation_only(string reference, string hypothesis, double expected)
    {
        TranscriptQuality.WordErrorRate(reference, hypothesis).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void A_dropped_self_correction_is_an_error_not_a_tidy_up()
    {
        // C17's dictation: losing "no wait" is a quality failure even though
        // the cleaned sentence reads better.
        var wer = TranscriptQuality.WordErrorRate(
            "the north jetty no wait the south jetty at dawn",
            "the south jetty at dawn");

        wer.Should().BeGreaterThan(TranscriptQuality.MaxWordErrorRate);
    }

    // ---- Read aloud -------------------------------------------------------

    [Fact]
    public void Mp3_frames_are_counted_and_timed()
    {
        // 38 MPEG-1 Layer III frames at 44.1 kHz: 38 x 1152 / 44100 = 0.993 s.
        var audio = Mp3Frames(38);

        var mp3 = SpeechAudioQuality.ReadMp3(audio);

        mp3.Should().NotBeNull();
        mp3!.Value.Frames.Should().Be(38);
        mp3.Value.Seconds.Should().BeApproximately(0.993, 0.001);
    }

    [Fact]
    public void An_id3_tag_before_the_frames_is_skipped()
    {
        var tag = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 5, 1, 2, 3, 4, 5 };

        SpeechAudioQuality.ReadMp3([.. tag, .. Mp3Frames(3)])!.Value.Frames.Should().Be(3);
    }

    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("RIFF....WAVEfmt ")]
    [InlineData("")]
    public void Bytes_that_are_not_mp3_are_refused(string body)
    {
        SpeechAudioQuality.ReadMp3(Encoding.ASCII.GetBytes(body)).Should().BeNull();
    }

    [Fact]
    public void One_frame_is_not_speech()
    {
        SpeechAudioQuality.ReadMp3(Mp3Frames(1)).Should().BeNull();
    }

    [Fact]
    public void Audio_far_too_short_or_long_for_its_text_is_flagged()
    {
        const string sentence = "Coil each rope clockwise and flake it left to right."; // 52 chars

        SpeechAudioQuality.Check(Mp3Frames(115), sentence).Should().BeNull(); // ~3.0 s
        SpeechAudioQuality.Check(Mp3Frames(10), sentence).Should().Contain("chars/s"); // ~0.26 s
        SpeechAudioQuality.Check(Mp3Frames(400), sentence).Should().Contain("chars/s"); // ~10.4 s
    }

    // ---- Fakes ------------------------------------------------------------

    private static float[] OneHot(int index)
    {
        var vector = new float[EmbeddingQuality.Passages.Length];
        vector[index] = 1;
        return vector;
    }

    private static float[] BagOfWords(string text, IReadOnlyDictionary<string, int> vocabulary)
    {
        var vector = new float[vocabulary.Count];
        foreach (var word in TranscriptQuality.Words(text))
            vector[vocabulary[word]] += 1;
        return vector;
    }

    /// <summary>MPEG-1 Layer III, 128 kbps, 44.1 kHz, no padding: 417-byte frames.</summary>
    private static byte[] Mp3Frames(int count)
    {
        var frame = new byte[417];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x00;
        return Enumerable.Repeat(frame, count).SelectMany(f => f).ToArray();
    }
}
