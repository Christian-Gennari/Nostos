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
/// A transcription that is slow to answer is asked a second time. The requests
/// here are answered by hand, so which one is used, which is cancelled and what
/// is accounted for are all observed rather than timed.
/// </summary>
public sealed class AlignmentHedgedTranscriptionTests
{
    private const string Sentence =
        "the lighthouse keeper counted seven grey gulls above the harbour " +
        "before the morning ferry rounded the northern breakwater at last";

    private const string Noise = "zz";

    private static readonly TimeSpan Hedge = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task By_default_a_slow_transcription_is_never_asked_twice()
    {
        var bench = new Bench(hedgeAfter: null);

        var locating = bench.LocateAsync();
        await bench.Transcriber.RequestAsync(1);
        await Task.Delay(Hedge * 3);

        bench.Transcriber.Requests.Should().Be(1);

        bench.Transcriber.Answer(1, Sentence);
        (await locating).Located.Should().BeTrue();
        bench.Usage.Settled.Should().Equal("Completed");
    }

    [Fact]
    public async Task A_quick_answer_is_not_asked_twice()
    {
        // Patience far longer than the test: a stalled machine cannot make
        // this answer look slow.
        var bench = new Bench(TimeSpan.FromSeconds(60));

        var locating = bench.LocateAsync();
        await bench.Transcriber.RequestAsync(1);
        bench.Transcriber.Answer(1, Sentence);

        (await locating).Located.Should().BeTrue();

        bench.Transcriber.Requests.Should().Be(1);
        bench.Usage.Settled.Should().Equal("Completed");
    }

    [Fact]
    public async Task A_slow_request_is_asked_again_and_the_second_answer_is_used()
    {
        var bench = new Bench(Hedge);

        var locating = bench.LocateAsync();
        await bench.Transcriber.RequestAsync(2);
        bench.Transcriber.Answer(2, Sentence);

        (await locating).Located.Should().BeTrue();

        bench.Transcriber.Requests.Should().Be(2);
        bench.Transcriber.WasCancelled(1).Should().BeTrue();
        bench.Usage.Settled.Should().Equal("Cancelled", "Completed");
    }

    [Fact]
    public async Task The_first_request_still_wins_when_it_answers_before_the_second()
    {
        var bench = new Bench(Hedge);

        var locating = bench.LocateAsync();
        await bench.Transcriber.RequestAsync(2);
        bench.Transcriber.Answer(1, Sentence);

        (await locating).Located.Should().BeTrue();

        bench.Transcriber.WasCancelled(2).Should().BeTrue();
        bench.Usage.Settled.Should().Equal("Completed", "Cancelled");
    }

    [Fact]
    public async Task Without_allowance_for_a_second_request_the_first_one_stands()
    {
        var bench = new Bench(Hedge);
        bench.Usage.DenyFrom = 2;

        var locating = bench.LocateAsync();
        await bench.Transcriber.RequestAsync(1);
        await Task.Delay(Hedge * 3);

        bench.Transcriber.Requests.Should().Be(1);

        bench.Transcriber.Answer(1, Sentence);
        (await locating).Located.Should().BeTrue();
        bench.Usage.Settled.Should().Equal("Completed");
    }

    [Fact]
    public async Task A_second_request_that_fails_leaves_the_first_to_answer()
    {
        var bench = new Bench(Hedge);

        var locating = bench.LocateAsync();
        await bench.Transcriber.RequestAsync(2);
        bench.Transcriber.Fail(2, new SttException(SttErrorCodes.RateLimited, "second"));
        bench.Transcriber.Answer(1, Sentence);

        (await locating).Located.Should().BeTrue();
        bench.Usage.Settled.Should().Equal("Completed", "ProviderError");
    }

    [Fact]
    public async Task When_both_requests_fail_the_first_failure_is_reported()
    {
        var bench = new Bench(Hedge);

        var locating = bench.LocateAsync();
        await bench.Transcriber.RequestAsync(2);
        bench.Transcriber.Fail(2, new SttException(SttErrorCodes.RateLimited, "second"));
        bench.Transcriber.Fail(1, new SttException(SttErrorCodes.Timeout, "first"));

        var failure = await locating.Invoking(task => task)
            .Should().ThrowAsync<SttException>();

        failure.Which.Message.Should().Be("first");
        bench.Usage.Settled.Should().Equal("ProviderError", "ProviderError");
    }

    [Fact]
    public async Task The_answer_that_arrives_is_the_one_that_is_used()
    {
        // The second request hears nothing usable: that, not the slower first
        // request, decides the outcome.
        var bench = new Bench(Hedge);

        var locating = bench.LocateAsync();
        await bench.Transcriber.RequestAsync(2);
        bench.Transcriber.Answer(2, Noise);

        (await locating).Located.Should().BeFalse();
        bench.Transcriber.WasCancelled(1).Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_the_search_cancels_both_requests_and_settles_both_leases()
    {
        var bench = new Bench(Hedge);
        using var cancel = new CancellationTokenSource();

        var locating = bench.LocateAsync(cancel.Token);
        await bench.Transcriber.RequestAsync(2);
        await cancel.CancelAsync();

        await locating.Invoking(task => task)
            .Should().ThrowAsync<OperationCanceledException>();

        bench.Transcriber.WasCancelled(1).Should().BeTrue();
        bench.Transcriber.WasCancelled(2).Should().BeTrue();
        bench.Usage.Settled.Should().Equal("Cancelled", "Cancelled");
    }

    [Fact]
    public async Task The_usage_service_is_never_used_from_two_tasks_at_once()
    {
        var bench = new Bench(Hedge);
        bench.Usage.Slow = true;

        var locating = bench.LocateAsync();
        await bench.Transcriber.RequestAsync(2);
        bench.Transcriber.Answer(2, Sentence);
        await locating;

        bench.Usage.Overlapped.Should().BeFalse();
        bench.Usage.Begun.Should().Be(2);
        bench.Usage.Settled.Should().HaveCount(2);
    }

    private sealed class Bench
    {
        private readonly AlignmentService service;
        private readonly AudioBookModel audio;
        private readonly EBookModel text;

        public ScriptedTranscriber Transcriber { get; } = new();
        public RecordingUsage Usage { get; } = new();

        public Bench(TimeSpan? hedgeAfter)
        {
            audio = new AudioBookModel
            {
                Title = "Audio",
                WorkId = Guid.NewGuid(),
                Duration = "0:10:00",
            };
            audio.FileDetails.HasFile = true;
            audio.FileDetails.FileName = "book.mp3";

            text = new EBookModel { Title = "Text", WorkId = audio.WorkId };
            text.FileDetails.HasFile = true;
            text.FileDetails.FileName = "book.epub";

            var options = new AlignmentOptions();
            if (hedgeAfter is { } hedge) options.HedgeAfterSeconds = hedge.TotalSeconds;
            options.NormalizeProbeSettings();

            var block = "An opening line that is not the one. " + Sentence + ". And a closing line.";
            var document = new BookTextExtractedDocument(
                BookTextSourceFormat.Epub,
                [
                    new BookTextArtifactBlock(
                        0,
                        block,
                        [],
                        [
                            new BookTextSourceSegment(
                                0,
                                block.Length,
                                new EpubBookTextSourceLocator(
                                    0, "book.xhtml", StartTextOffset: 0, EndTextOffset: block.Length)),
                        ]),
                ]);

            service = new AlignmentService(
                new Cutter(),
                Transcriber,
                null!,
                [new EpubBookTextExtractor()],
                new Documents(document),
                options,
                Usage,
                new NoAnchors(),
                new NoRevision(),
                NullLogger<AlignmentService>.Instance);
        }

        public Task<AlignmentLocateResult> LocateAsync(CancellationToken ct = default) =>
            Task.Run(() => service.LocateAsync(audio, text, 120, ct), CancellationToken.None);
    }

    private sealed class ScriptedTranscriber : IAlignmentTranscriber
    {
        private readonly object sync = new();
        private readonly List<TaskCompletionSource<SttResult>> answers = [];
        private readonly List<bool> cancelled = [];
        private TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Requests { get { lock (sync) return answers.Count; } }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
            Task.FromResult(true);

        public async Task<SttResult> TranscribeAsync(
            Stream audio, string fileName, string? contentType, string? languageHint,
            CancellationToken ct = default)
        {
            // Every request is given the whole clip, not what another left unread.
            audio.Length.Should().Be(Cutter.ClipBytes);
            audio.Position.Should().Be(0);

            TaskCompletionSource<SttResult> answer;
            int index;

            lock (sync)
            {
                answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
                answers.Add(answer);
                cancelled.Add(false);
                index = answers.Count - 1;
                arrived.TrySetResult();
                arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            await using var registration = ct.Register(() =>
            {
                lock (sync) cancelled[index] = true;
                answer.TrySetCanceled(ct);
            });

            return await answer.Task;
        }

        /// <summary>Waits until this many requests have been made.</summary>
        public async Task RequestAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

            while (true)
            {
                Task wait;

                lock (sync)
                {
                    if (answers.Count >= count) return;
                    wait = arrived.Task;
                }

                await wait.WaitAsync(deadline - DateTime.UtcNow);
            }
        }

        public void Answer(int request, string heard)
        {
            lock (sync) answers[request - 1].TrySetResult(new SttResult(heard, "en", 20));
        }

        public void Fail(int request, Exception failure)
        {
            lock (sync) answers[request - 1].TrySetException(failure);
        }

        public bool WasCancelled(int request)
        {
            lock (sync) return cancelled[request - 1];
        }
    }

    private sealed class RecordingUsage : IAiUsageAccountingService
    {
        private int active;
        private readonly List<(Guid Id, string Category)> settled = [];
        private readonly List<Guid> begun = [];

        public int DenyFrom { get; set; } = int.MaxValue;
        public bool Slow { get; set; }
        public bool Overlapped { get; private set; }
        public int Begun => begun.Count;

        /// <summary>How each lease ended, in the order the leases were opened.</summary>
        public IReadOnlyList<string> Settled =>
            begun.Where(id => settled.Any(entry => entry.Id == id))
                .Select(id => settled.First(entry => entry.Id == id).Category)
                .ToList();

        public async Task<AiUsageLease?> BeginSttAsync(CancellationToken cancellationToken = default)
        {
            await EnterAsync();

            try
            {
                if (begun.Count + 1 >= DenyFrom)
                    throw new AiUsageException(AiUsageBlockReason.RateLimited, "No more.");

                var lease = new AiUsageLease(Guid.NewGuid(), AiUsageKind.Stt);
                begun.Add(lease.ReservationId);
                return lease;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        public async Task CompleteSttAsync(
            AiUsageLease? lease, TranscriptionUsage usage, CancellationToken cancellationToken = default)
        {
            await EnterAsync();
            settled.Add((lease!.ReservationId, usage.ResultCategory));
            Interlocked.Decrement(ref active);
        }

        private async Task EnterAsync()
        {
            if (Interlocked.Increment(ref active) > 1) Overlapped = true;
            if (Slow) await Task.Delay(40);
        }

        public Task<AiUsageLease?> BeginLlmTurnAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AiUsageLease?>(null);

        public Task CompleteLlmTurnAsync(
            AiUsageLease? lease, LlmProviderUsage usage, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class Cutter : IAudioClipCutter
    {
        public const int ClipBytes = 64;

        public Task<AudioClip> CutAsync(
            AudioBookModel book, double positionSeconds, double clipSeconds, CancellationToken ct = default) =>
            Task.FromResult(new AudioClip(new MemoryStream(new byte[ClipBytes]), clipSeconds));
    }

    private sealed class Documents(BookTextExtractedDocument document) : IAlignmentDocumentProvider
    {
        public Task<AlignmentDocumentSnapshot> GetAsync(
            Guid bookId, IBookAssetStorage storage, IBookTextExtractor extractor, CancellationToken ct = default) =>
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
