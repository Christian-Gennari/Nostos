using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Product.Alignment;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

/// <summary>
/// Runs the whole text → audio search against a simulated narration: a book of
/// known words, each spoken at a known second, and a transcriber that returns
/// exactly the words inside the clip it is given. The true time of every
/// sentence is therefore known, which no real recording offers.
/// </summary>
public sealed class AlignmentNarrationSimulationTests
{
    private const double LeadIn = 3;

    [Theory]
    [InlineData(2.6)] // an ordinary narrator: 52 words to a clip
    [InlineData(1.7)] // a slow one: 34 words, the two windows overlap
    public async Task Playback_starts_just_before_the_sentence_never_after_it(double wordsPerSecond)
    {
        var narration = new Narration(wordsPerSecond);

        foreach (var sentence in new[] { 40, 111, 197, 263, 318 })
        {
            var result = await narration.LocateAsync(sentence);

            result.Located.Should().BeTrue($"sentence {sentence} is in the book");

            var early = narration.SentenceStartSeconds(sentence) - result.PositionSeconds!.Value;

            // The lead-in is deliberate; the timing itself is within two seconds.
            early.Should().BeInRange(LeadIn - 2, LeadIn + 2, $"sentence {sentence}");
        }
    }

    [Fact]
    public async Task A_clip_whose_last_words_were_misheard_is_still_read()
    {
        // The transcriber invents its last six words, as one does over a
        // chapter's closing silence; the opening and middle still place the clip.
        var narration = new Narration(2.6) { GarbleLastWords = 6 };

        var result = await narration.LocateAsync(150);

        result.Located.Should().BeTrue();

        var early = narration.SentenceStartSeconds(150) - result.PositionSeconds!.Value;
        early.Should().BeInRange(LeadIn - 2.5, LeadIn + 2.5);
    }

    [Fact]
    public async Task A_clip_placed_only_by_its_closing_words_is_timed_from_those_words()
    {
        // Everything but the last 24 words of each clip is misheard, so a clip
        // is known only by some eight seconds of speech at its end. Spreading
        // that text over the whole clip started playback up to ten seconds early.
        var narration = new Narration(2.6) { GarbleAllButLastWords = 24 };

        foreach (var sentence in new[] { 75, 140, 222, 301 })
        {
            var result = await narration.LocateAsync(sentence);

            result.Located.Should().BeTrue($"sentence {sentence} is in the book");

            var early = narration.SentenceStartSeconds(sentence) - result.PositionSeconds!.Value;
            early.Should().BeInRange(LeadIn - 2.5, LeadIn + 2.5, $"sentence {sentence}");
        }
    }

    [Fact]
    public async Task A_traditional_chinese_book_is_found_from_a_simplified_transcript()
    {
        var narration = new Narration(4.5, Narration.Script.Chinese);

        foreach (var sentence in new[] { 60, 170 })
        {
            var result = await narration.LocateAsync(sentence);

            result.Located.Should().BeTrue($"sentence {sentence} is in the book");

            var early = narration.SentenceStartSeconds(sentence) - result.PositionSeconds!.Value;
            early.Should().BeInRange(LeadIn - 2, LeadIn + 2, $"sentence {sentence}");
        }
    }

    private sealed class Narration
    {
        public enum Script { English, Chinese }

        private readonly List<(string Printed, string Spoken, double Start, double End)> words = [];
        private readonly List<int> sentenceFirstWord = [];
        private readonly List<string> sentenceText = [];
        private readonly BookTextExtractedDocument document;
        private readonly Script script;

        public int GarbleLastWords { get; init; }
        public int GarbleAllButLastWords { get; init; }

        public Narration(double wordsPerSecond, Script script = Script.English)
        {
            this.script = script;

            var random = new Random(844);
            var blocks = new List<BookTextArtifactBlock>();
            var paragraph = new StringBuilder();
            var resourceOffset = 0;
            var clock = 12.0; // a spoken title before the text begins

            for (var sentence = 0; sentence < 360; sentence++)
            {
                // Narrators speed up and slow down by the sentence.
                var pace = wordsPerSecond * (0.8 + 0.4 * random.NextDouble());
                var length = 9 + random.Next(8);
                var text = new StringBuilder();

                sentenceFirstWord.Add(words.Count);

                for (var i = 0; i < length; i++)
                {
                    var (printed, spoken) = script == Script.Chinese
                        ? ChineseWord(random)
                        : EnglishWord(random);

                    words.Add((printed, spoken, clock, clock + 1 / pace));
                    clock += 1 / pace;

                    if (script == Script.English && i > 0) text.Append(' ');
                    text.Append(i == 0 && script == Script.English
                        ? char.ToUpperInvariant(printed[0]) + printed[1..]
                        : printed);
                }

                text.Append(script == Script.Chinese ? "。" : ".");
                clock += 0.4; // the breath between sentences
                sentenceText.Add(text.ToString());

                if (paragraph.Length > 0 && script == Script.English) paragraph.Append(' ');
                paragraph.Append(text);

                if (sentence % 6 == 5)
                {
                    var block = paragraph.ToString();
                    blocks.Add(new BookTextArtifactBlock(
                        blocks.Count,
                        block,
                        [],
                        [
                            new BookTextSourceSegment(
                                0,
                                block.Length,
                                new EpubBookTextSourceLocator(
                                    0,
                                    "book.xhtml",
                                    StartTextOffset: resourceOffset,
                                    EndTextOffset: resourceOffset + block.Length)),
                        ]));

                    resourceOffset += block.Length + 1;
                    paragraph.Clear();
                }
            }

            DurationSeconds = clock + 10;
            document = new BookTextExtractedDocument(BookTextSourceFormat.Epub, blocks);
        }

        public double DurationSeconds { get; }

        public double SentenceStartSeconds(int sentence) =>
            words[sentenceFirstWord[sentence]].Start;

        public Task<AlignmentLocateAudioResult> LocateAsync(int sentence)
        {
            var total = (int)Math.Floor(DurationSeconds);

            var audio = new AudioBookModel
            {
                Title = "Audio",
                WorkId = Guid.NewGuid(),
                Duration = $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}",
            };
            audio.FileDetails.HasFile = true;
            audio.FileDetails.FileName = "book.mp3";

            var text = new EBookModel { Title = "Text", WorkId = audio.WorkId };
            text.FileDetails.HasFile = true;
            text.FileDetails.FileName = "book.epub";

            var options = new AlignmentOptions { LeadInSeconds = LeadIn };
            options.NormalizeProbeSettings();

            var service = new AlignmentService(
                new Cutter(),
                new Transcriber(this),
                null!,
                [new EpubBookTextExtractor()],
                new Documents(document),
                options,
                NoOpAiUsageAccountingService.Instance,
                new NoAnchors(),
                new NoRevision(),
                NullLogger<AlignmentService>.Instance);

            // What the reader sends: the page from this sentence on.
            var excerpt = string.Join(
                script == Script.Chinese ? "" : " ",
                sentenceText.Skip(sentence).Take(4));

            return service.LocateAudioAsync(text, audio, excerpt[..Math.Min(280, excerpt.Length)]);
        }

        /// <summary>What a transcriber hears between two seconds of the narration.</summary>
        private string Heard(double from, double to)
        {
            var spoken = words
                .Where(word => word.Start >= from && word.Start < to)
                .Select(word => word.Spoken)
                .ToList();

            for (var i = 1; i <= GarbleLastWords && i <= spoken.Count; i++)
                spoken[^i] = "zzgarble" + i;

            if (GarbleAllButLastWords > 0)
            {
                for (var i = 0; i < spoken.Count - GarbleAllButLastWords; i++)
                    spoken[i] = "zzgarble" + i;
            }

            return string.Join(script == Script.Chinese ? "" : " ", spoken);
        }

        private static (string, string) EnglishWord(Random random)
        {
            const string consonants = "bcdfghjklmnprstvwz";
            const string vowels = "aeiou";
            var word = new StringBuilder();
            var syllables = 2 + random.Next(3);

            for (var i = 0; i < syllables; i++)
            {
                word.Append(consonants[random.Next(consonants.Length)]);
                word.Append(vowels[random.Next(vowels.Length)]);
            }

            return (word.ToString(), word.ToString());
        }

        // Printed in Traditional characters, spoken (transcribed) in Simplified.
        private static (string, string) ChineseWord(Random random)
        {
            const string traditional = "這個時候說話讓聽見學習愛國東車書長門開關馬鳥魚龍風雲電話機場樂園萬與專業叢東絲丟兩嚴喪豐臨為麗舉義烏樂喬習鄉書買亂爭";
            const string simplified = "这个时候说话让听见学习爱国东车书长门开关马鸟鱼龙风云电话机场乐园万与专业丛东丝丢两严丧丰临为丽举义乌乐乔习乡书买乱争";
            const string shared = "我你他的了在是有人山水火木土天地日月大小上下中心手口目耳一二三四五六七八九十";

            var printed = new StringBuilder();
            var spoken = new StringBuilder();

            for (var i = 0; i < 2; i++)
            {
                if (random.Next(3) == 0)
                {
                    var index = random.Next(shared.Length);
                    printed.Append(shared[index]);
                    spoken.Append(shared[index]);
                }
                else
                {
                    var index = random.Next(traditional.Length);
                    printed.Append(traditional[index]);
                    spoken.Append(simplified[index]);
                }
            }

            return (printed.ToString(), spoken.ToString());
        }

        private sealed class Cutter : IAudioClipCutter
        {
            // The clip is described, not decoded: its two ends, as the product cuts it.
            public Task<AudioClip> CutAsync(
                AudioBookModel book,
                double positionSeconds,
                double clipSeconds,
                CancellationToken ct = default)
            {
                var start = Math.Max(0, positionSeconds - clipSeconds);
                var bytes = new byte[16];
                BitConverter.TryWriteBytes(bytes.AsSpan(0, 8), start);
                BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), positionSeconds);

                return Task.FromResult(new AudioClip(
                    new MemoryStream(bytes),
                    positionSeconds - start));
            }
        }

        private sealed class Transcriber(Narration narration) : IAlignmentTranscriber
        {
            public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
                Task.FromResult(true);

            public Task<SttResult> TranscribeAsync(
                Stream audio,
                string fileName,
                string? contentType,
                string? languageHint,
                CancellationToken ct = default)
            {
                var bytes = new byte[16];
                audio.ReadExactly(bytes);
                var from = BitConverter.ToDouble(bytes, 0);
                var to = BitConverter.ToDouble(bytes, 8);

                return Task.FromResult(new SttResult(
                    narration.Heard(from, to), null, to - from));
            }
        }

        private sealed class Documents(BookTextExtractedDocument document) : IAlignmentDocumentProvider
        {
            public Task<AlignmentDocumentSnapshot> GetAsync(
                Guid bookId,
                IBookAssetStorage storage,
                IBookTextExtractor extractor,
                CancellationToken ct = default) =>
                Task.FromResult(new AlignmentDocumentSnapshot(document, "text-revision"));
        }

        private sealed class NoRevision : IAlignmentAudioRevisionProvider
        {
            public Task<string?> GetAsync(AudioBookModel book, CancellationToken ct = default) =>
                Task.FromResult<string?>(null);
        }

        private sealed class NoAnchors : IAlignmentAnchorStore
        {
            public Task<bool> SaveAsync(AlignmentAnchorInput anchor, CancellationToken ct = default) =>
                Task.FromResult(false);

            public Task<IReadOnlyList<AlignmentAnchorModel>> GetNearbyAsync(
                Guid audioBookId, Guid textBookId, string audioRevision, string textRevision,
                long audioPositionMs, long radiusMs, int limit = 100, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<AlignmentAnchorModel>>([]);

            public Task<AlignmentAnchorModel?> AtOrBeforeTextAsync(
                Guid audioBookId, Guid textBookId, string audioRevision, string textRevision,
                int textGlobalOffset, CancellationToken ct = default) =>
                Task.FromResult<AlignmentAnchorModel?>(null);

            public Task<AlignmentAnchorModel?> AtOrAfterTextAsync(
                Guid audioBookId, Guid textBookId, string audioRevision, string textRevision,
                int textGlobalOffset, CancellationToken ct = default) =>
                Task.FromResult<AlignmentAnchorModel?>(null);

            public Task<AlignmentAnchorModel?> AtOrBeforeAudioAsync(
                Guid audioBookId, Guid textBookId, string audioRevision, string textRevision,
                long audioPositionMs, CancellationToken ct = default) =>
                Task.FromResult<AlignmentAnchorModel?>(null);

            public Task<AlignmentAnchorModel?> AtOrAfterAudioAsync(
                Guid audioBookId, Guid textBookId, string audioRevision, string textRevision,
                long audioPositionMs, CancellationToken ct = default) =>
                Task.FromResult<AlignmentAnchorModel?>(null);
        }
    }
}
