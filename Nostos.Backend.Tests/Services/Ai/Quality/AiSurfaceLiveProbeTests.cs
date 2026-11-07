namespace Nostos.Backend.Tests.Services.Ai.Quality;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Configuration;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Services.Ai;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The §11 quality bars against a real endpoint. No-ops unless
/// NOSTOS_AI_PROBE_LIVE=1, like the live quality bed, so CI stays offline.
/// Each probe also needs its own endpoint:
///
///   embeddings  NOSTOS_PROBE_EMBED_BASE_URL, NOSTOS_PROBE_EMBED_MODEL, NOSTOS_PROBE_EMBED_KEY
///   read aloud  NOSTOS_PROBE_TTS_BASE_URL, NOSTOS_PROBE_TTS_MODEL, NOSTOS_PROBE_TTS_VOICE (key optional: NOSTOS_PROBE_TTS_KEY)
///   STT         NOSTOS_PROBE_STT_BASE_URL, NOSTOS_PROBE_STT_MODEL, NOSTOS_PROBE_STT_KEY
///               (transcribes what read aloud spoke, so it needs both)
///
/// A probe whose endpoint is not set is skipped with a line in the output.
/// One run costs a few cents at most: 16 short embeddings, 3 sentences.
/// </summary>
public sealed class AiSurfaceLiveProbeTests(ITestOutputHelper output)
{
    private static readonly string[] Sentences =
    [
        "Coil each rope clockwise and flake it from left to right across the thwart.",
        "The keeper said a long night watch asks for your whole attention.",
        "Repair is listening before it is fixing, so plane with the grain.",
    ];

    private static bool Live =>
        string.Equals(Environment.GetEnvironmentVariable("NOSTOS_AI_PROBE_LIVE"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task Embeddings_rank_paraphrased_questions_onto_their_passages()
    {
        if (!Live || Config("EMBED") is not { } config)
        {
            output.WriteLine("skipped: set NOSTOS_AI_PROBE_LIVE=1 and the NOSTOS_PROBE_EMBED_* endpoint");
            return;
        }

        var provider = new OpenAiCompatibleEmbeddingProvider(
            new TimeoutHttpClientFactory(TimeSpan.FromSeconds(60)),
            new StubAiProviderConfigResolver { Embedding = config },
            NullLogger<OpenAiCompatibleEmbeddingProvider>.Instance);

        var passages = await provider.EmbedAsync(EmbeddingQuality.Passages);
        var queries = await provider.EmbedAsync(EmbeddingQuality.Cases.Select(c => c.Query).ToList());
        var again = await provider.EmbedAsync([EmbeddingQuality.Passages[0]]);

        var score = EmbeddingQuality.Evaluate(passages.Vectors, queries.Vectors);
        var drift = 1 - EmbeddingQuality.Cosine(passages.Vectors[0], again.Vectors[0]);
        output.WriteLine(
            $"{config.Model}: recall@1 {score.RecallAt1:F2}, MRR {score.MeanReciprocalRank:F2}, " +
            $"gold ranks [{string.Join(",", score.GoldRanks)}], repeat drift {drift:E2}, {passages.Vectors[0].Length} dims");

        score.Good.Should().BeTrue(
            $"recall@1 >= {EmbeddingQuality.MinRecallAt1} and MRR >= {EmbeddingQuality.MinMeanReciprocalRank}");
        drift.Should().BeLessThanOrEqualTo(EmbeddingQuality.MaxRepeatDrift);
    }

    [Fact]
    public async Task Read_aloud_speaks_mp3_that_speech_to_text_reads_back()
    {
        if (!Live || Config("TTS", keyRequired: false) is not { } tts)
        {
            output.WriteLine("skipped: set NOSTOS_AI_PROBE_LIVE=1 and the NOSTOS_PROBE_TTS_* endpoint");
            return;
        }

        var cache = Path.Combine(Path.GetTempPath(), $"nostos-probe-tts-{Guid.NewGuid():N}");
        var options = new ReadAloudOptions { CacheDirectory = cache, CacheMaxBytes = 0 };
        var speaker = new OpenAiCompatibleReadAloudProvider(
            new TimeoutHttpClientFactory(TimeSpan.FromSeconds(60)),
            options,
            new StubAiProviderConfigResolver { Tts = tts },
            new ReadAloudAudioCache(options, NullLogger<ReadAloudAudioCache>.Instance),
            NullLogger<OpenAiCompatibleReadAloudProvider>.Instance);
        var stt = Config("STT");
        var listener = stt is null
            ? null
            : new OpenAiCompatibleSttProvider(
                new TimeoutHttpClientFactory(TimeSpan.FromSeconds(120)),
                new SpeechOptions(),
                new StubAiProviderConfigResolver { Stt = stt },
                NullLogger<OpenAiCompatibleSttProvider>.Instance);
        if (listener is null)
            output.WriteLine("STT round trip skipped: NOSTOS_PROBE_STT_* not set");

        var voice = Environment.GetEnvironmentVariable("NOSTOS_PROBE_TTS_VOICE") ?? "af_heart";
        var problems = new List<string>();
        foreach (var sentence in Sentences)
        {
            var audio = await speaker.SynthesizeAsync(new ReadAloudSpeechRequest(sentence, voice));
            var mp3 = SpeechAudioQuality.ReadMp3(audio.Audio);
            output.WriteLine($"{tts.Model}/{voice}: {audio.Audio.Length} bytes, {mp3?.Seconds:F1} s for {sentence.Length} chars");
            if (SpeechAudioQuality.Check(audio.Audio, sentence) is { } problem)
                problems.Add($"\"{sentence}\": {problem}");

            if (listener is null)
                continue;
            using var stream = new MemoryStream(audio.Audio);
            var heard = await listener.TranscribeAsync(stream, "probe.mp3", "audio/mpeg", "en");
            var wer = TranscriptQuality.WordErrorRate(sentence, heard.Text);
            output.WriteLine($"  {stt!.Model} heard \"{heard.Text}\" (WER {wer:F2})");
            if (wer > TranscriptQuality.MaxWordErrorRate)
                problems.Add($"\"{sentence}\" came back as \"{heard.Text}\" (WER {wer:F2})");
        }

        problems.Should().BeEmpty();
    }

    private static EffectiveAiProviderConfig? Config(string surface, bool keyRequired = true)
    {
        var baseUrl = Environment.GetEnvironmentVariable($"NOSTOS_PROBE_{surface}_BASE_URL");
        var model = Environment.GetEnvironmentVariable($"NOSTOS_PROBE_{surface}_MODEL");
        var key = Environment.GetEnvironmentVariable($"NOSTOS_PROBE_{surface}_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model)
            || (keyRequired && string.IsNullOrWhiteSpace(key)))
            return null;
        return new EffectiveAiProviderConfig(
            Enabled: true,
            BaseUrl: baseUrl,
            Model: model,
            ApiKeyEnvironmentVariable: $"NOSTOS_PROBE_{surface}_KEY",
            ApiKey: key,
            KeyFromServerEnv: true);
    }
}
