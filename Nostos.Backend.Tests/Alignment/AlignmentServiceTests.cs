using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Endpoints;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Alignment;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Alignment;

public sealed class AlignmentServiceTests
{
    private const string Passage =
        "The patient traveller carried a golden lantern through " +
        "the quiet orchard before sunrise.";

    [Fact]
    public async Task Located_handoff_uses_real_matcher_and_accounts_once()
    {
        var transcriber = new FakeTranscriber
        {
            Result = new SttResult(Passage, "en", 20),
        };
        var cutter = new FakeCutter();
        var usage = new AlignmentEndpointTests.RecordingUsageAccounting();

        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(
            factory, transcriber, cutter, usage);
        using var client = host.CreateClient();

        var (source, target) = await SeedAsync(client, factory);

        var response = await AlignmentEndpointTests.LocateAsync(
            client, source, target, 65);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LocateResponse>();

        body!.Located.Should().BeTrue();
        body.Score.Should().BeGreaterThanOrEqualTo(0.85);
        body.Locator.Should().NotBeNull();
        body.Locator!.SpineIndex.Should().Be(2);
        body.Locator.ResourceHref.Should().Be("chapter-3.xhtml");
        body.Locator.TextOffset.Should().BeGreaterThan(100);

        transcriber.CallCount.Should().Be(1);
        transcriber.LastLanguage.Should().Be("en");
        transcriber.LastFileName.Should().Be("alignment.wav");
        transcriber.LastContentType.Should().Be("audio/wav");

        cutter.CallCount.Should().Be(1);
        cutter.LastPosition.Should().Be(65);
        cutter.LastDuration.Should().Be(20);

        usage.BeginCount.Should().Be(1);
        usage.CompleteCount.Should().Be(1);
        usage.LastUsage!.UpstreamRequestCount.Should().Be(1);
        usage.LastUsage.ResultCategory.Should().Be("Completed");
    }

    [Fact]
    public async Task Declined_match_still_accounts_one_transcription()
    {
        var transcriber = new FakeTranscriber
        {
            Result = new SttResult("a chapter title", "en", 20),
        };
        var cutter = new FakeCutter();
        var usage = new AlignmentEndpointTests.RecordingUsageAccounting();

        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(
            factory, transcriber, cutter, usage);
        using var client = host.CreateClient();

        var (source, target) = await SeedAsync(client, factory);

        var response = await AlignmentEndpointTests.LocateAsync(
            client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LocateResponse>();

        body!.Located.Should().BeFalse();
        body.Reason.Should().Be("insufficient_speech");

        transcriber.CallCount.Should().Be(1);
        cutter.CallCount.Should().Be(1);
        usage.BeginCount.Should().Be(1);
        usage.CompleteCount.Should().Be(1);
        usage.LastUsage!.ResultCategory.Should().Be("Completed");
    }

    [Fact]
    public async Task Provider_error_is_accounted_once_and_never_retried()
    {
        var transcriber = new FakeTranscriber
        {
            Failure = new SttException(
                SttErrorCodes.Provider,
                "The transcription gateway failed."),
        };
        var cutter = new FakeCutter();
        var usage = new AlignmentEndpointTests.RecordingUsageAccounting();

        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(
            factory, transcriber, cutter, usage);
        using var client = host.CreateClient();

        var (source, target) = await SeedAsync(client, factory);

        var response = await AlignmentEndpointTests.LocateAsync(
            client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await AlignmentEndpointTests.ProblemCodeAsync(response))
            .Should().Be(SttErrorCodes.Provider);

        transcriber.CallCount.Should().Be(1);
        cutter.CallCount.Should().Be(1);

        usage.BeginCount.Should().Be(1);
        usage.CompleteCount.Should().Be(1);
        usage.LastUsage!.UpstreamRequestCount.Should().Be(1);
        usage.LastUsage.ResultCategory.Should().Be("ProviderError");
    }

    [Fact]
    public async Task Usage_denial_does_not_call_provider()
    {
        var transcriber = new FakeTranscriber();
        var cutter = new FakeCutter();
        var usage = new AlignmentEndpointTests.RecordingUsageAccounting
        {
            BlockReason = AiUsageBlockReason.LimitReached,
        };

        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(
            factory, transcriber, cutter, usage);
        using var client = host.CreateClient();

        var (source, target) = await SeedAsync(client, factory);

        var response = await AlignmentEndpointTests.LocateAsync(
            client, source, target);

        response.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests);
        (await AlignmentEndpointTests.ProblemCodeAsync(response))
            .Should().Be("ai_usage_limit_reached");

        usage.BeginCount.Should().Be(1);
        usage.CompleteCount.Should().Be(0);
        transcriber.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Unavailable_transcription_does_not_cut_or_reserve()
    {
        var transcriber = new FakeTranscriber { Available = false };
        var cutter = new FakeCutter();
        var usage = new AlignmentEndpointTests.RecordingUsageAccounting();

        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(
            factory, transcriber, cutter, usage);
        using var client = host.CreateClient();

        var (source, target) = await SeedAsync(client, factory);

        var response = await AlignmentEndpointTests.LocateAsync(
            client, source, target);

        response.StatusCode.Should().Be(
            HttpStatusCode.ServiceUnavailable);
        (await AlignmentEndpointTests.ProblemCodeAsync(response))
            .Should().Be("alignment_transcription_unavailable");

        cutter.CallCount.Should().Be(0);
        transcriber.CallCount.Should().Be(0);
        usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Repeated_handoffs_reuse_the_extracted_document()
    {
        var transcriber = new FakeTranscriber
        {
            Result = new SttResult(Passage, "en", 20),
        };
        var cutter = new FakeCutter();
        var usage = new AlignmentEndpointTests.RecordingUsageAccounting();
        var extractor = new FakeEpubExtractor();

        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(
            factory, transcriber, cutter, usage, extractor);
        using var client = host.CreateClient();

        var (source, target) = await SeedAsync(client, factory);

        var first = await AlignmentEndpointTests.LocateAsync(
            client, source, target, 45);
        var second = await AlignmentEndpointTests.LocateAsync(
            client, source, target, 55);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        extractor.CallCount.Should().Be(1);
        transcriber.CallCount.Should().Be(2);
        usage.BeginCount.Should().Be(2);
        usage.CompleteCount.Should().Be(2);
    }

    private static WebApplicationFactory<Program> CreateHost(
        LibraryEndpointFactory factory,
        FakeTranscriber transcriber,
        FakeCutter cutter,
        AlignmentEndpointTests.RecordingUsageAccounting usage,
        FakeEpubExtractor? extractor = null)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAlignmentTranscriber>();
                services.AddSingleton<IAlignmentTranscriber>(transcriber);

                services.RemoveAll<IAudioClipCutter>();
                services.AddSingleton<IAudioClipCutter>(cutter);

                services.RemoveAll<IAiUsageAccountingService>();
                services.AddSingleton<IAiUsageAccountingService>(usage);

                services.RemoveAll<IBookTextExtractor>();
                services.AddSingleton<IBookTextExtractor>(
                    extractor ?? new FakeEpubExtractor());
            });
        });
    }

    private static async Task<(Guid Source, Guid Target)> SeedAsync(
        HttpClient client,
        LibraryEndpointFactory factory)
    {
        var ids = await AlignmentEndpointTests.SeedEditionsAsync(
            client, factory, sameWork: true);

        // The test host points to this unique temporary root.
        // Never use Nostos.Backend/Storage.
        var epubDirectory = Path.Combine(
            factory.BooksRootPath, ids.Target.ToString());

        Directory.CreateDirectory(epubDirectory);

        await File.WriteAllBytesAsync(
            Path.Combine(epubDirectory, "book.epub"),
            [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]);

        return ids;
    }

    private sealed record LocateResponse(
        bool Located,
        string? Reason,
        double? Score,
        string? Excerpt,
        AlignmentLocator? Locator);

    private sealed class FakeTranscriber : IAlignmentTranscriber
    {
        public bool Available { get; set; } = true;
        public int CallCount { get; private set; }
        public string? LastLanguage { get; private set; }
        public string? LastFileName { get; private set; }
        public string? LastContentType { get; private set; }

        public SttResult Result { get; set; } =
            new(Passage, "en", 20);

        public SttException? Failure { get; set; }

        public Task<bool> IsAvailableAsync(
            CancellationToken ct = default) =>
            Task.FromResult(Available);

        public async Task<SttResult> TranscribeAsync(
            Stream audio,
            string fileName,
            string? contentType,
            string? languageHint,
            CancellationToken ct = default)
        {
            CallCount++;
            LastLanguage = languageHint;
            LastFileName = fileName;
            LastContentType = contentType;

            using var buffer = new MemoryStream();
            await audio.CopyToAsync(buffer, ct);
            buffer.Length.Should().BeGreaterThan(0);

            if (Failure is not null)
                throw Failure;

            return Result;
        }
    }

    private sealed class FakeCutter : IAudioClipCutter
    {
        public int CallCount { get; private set; }
        public double LastPosition { get; private set; }
        public double LastDuration { get; private set; }

        public Task<AudioClip> CutAsync(
            AudioBookModel book,
            double positionSeconds,
            double clipSeconds,
            CancellationToken ct = default)
        {
            CallCount++;
            LastPosition = positionSeconds;
            LastDuration = clipSeconds;

            // The transcriber is fake, so only stream ownership and data
            // flow matter here. No ffmpeg process is started.
            var stream = new MemoryStream(
                [0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4],
                writable: false);

            return Task.FromResult(new AudioClip(
                stream, Math.Min(positionSeconds, clipSeconds)));
        }
    }

    private sealed class FakeEpubExtractor : IBookTextExtractor
    {
        public int CallCount { get; private set; }

        public BookTextSourceFormat Format => BookTextSourceFormat.Epub;

        public Task<BookTextExtractedDocument> ExtractAsync(
            Stream source,
            CancellationToken ct = default)
        {
            CallCount++;

            var block = new BookTextArtifactBlock(
                0,
                Passage,
                [],
                [
                    new BookTextSourceSegment(
                        0,
                        Passage.Length,
                        new EpubBookTextSourceLocator(
                            2,
                            "chapter-3.xhtml",
                            StartTextOffset: 100,
                            EndTextOffset: 100 + Passage.Length)),
                ]);

            return Task.FromResult(
                new BookTextExtractedDocument(
                    BookTextSourceFormat.Epub,
                    [block]));
        }
    }
}
