using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Data;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Alignment;
using Nostos.Product.BookText;
using Nostos.Product.Services.Ai;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// Full-host coverage for audiobook-to-EPUB alignment.
///
/// No real transcription, ffmpeg invocation or book-storage read occurs.
/// Each test has its own SQLite database and isolated test storage root.
/// </summary>
public sealed class AlignmentEndpointTests
{
    private const string Passage =
        "The patient traveller carried a golden lantern through " +
        "the quiet orchard before sunrise.";

    [Fact]
    public async Task Missing_source_returns_404_without_transcription()
    {
        using var harness = new Harness();
        using var client = harness.Client();

        var response = await PostAsync(
            client, Guid.NewGuid(), Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_book_not_found");

        harness.Transcriber.CallCount.Should().Be(0);
        harness.Cutter.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Missing_target_returns_404_without_transcription()
    {
        using var harness = new Harness();
        using var client = harness.Client();

        var (source, _) = await SeedPairAsync(client, harness);

        var response = await PostAsync(
            client, source, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_book_not_found");

        harness.Transcriber.CallCount.Should().Be(0);
        harness.Cutter.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Different_works_are_rejected_before_provider_usage()
    {
        using var harness = new Harness();
        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(
            client, harness, sameWork: false);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_work_mismatch");

        harness.Transcriber.CallCount.Should().Be(0);
        harness.Cutter.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Non_audio_source_is_rejected()
    {
        using var harness = new Harness();
        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        var response = await PostAsync(client, target, source);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_source_not_audio");

        harness.Transcriber.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Target_must_be_an_epub()
    {
        using var harness = new Harness();
        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        await harness.SetTargetFileNameAsync(target, "book.pdf");

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_target_not_epub");

        harness.Transcriber.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Missing_source_media_is_a_typed_failure()
    {
        using var harness = new Harness();
        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        await harness.SetHasFileAsync(source, false);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_source_media_missing");

        harness.Transcriber.CallCount.Should().Be(0);
        harness.Cutter.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Invalid_positions_do_not_start_alignment(double position)
    {
        using var harness = new Harness();
        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        // System.Text.Json cannot serialize non-finite numbers by default.
        // Send these values as JSON strings to verify that the endpoint rejects
        // them, or treats them as invalid JSON, without invoking transcription.
        using var content = double.IsFinite(position)
            ? JsonContent.Create(new
            {
                positionSeconds = position,
                targetBookId = target,
            })
            : new StringContent(
                $$"""
                {"positionSeconds":"{{position}}","targetBookId":"{{target}}"}
                """,
                System.Text.Encoding.UTF8,
                "application/json");

        var response = await client.PostAsync(Route(source), content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        harness.Transcriber.CallCount.Should().Be(0);
        harness.Cutter.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Access_denial_stops_before_clip_cutting_and_usage()
    {
        using var harness = new Harness { Allowed = false };
        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response))
            .Should().Be(SttErrorCodes.AccessDenied);

        harness.Cutter.CallCount.Should().Be(0);
        harness.Transcriber.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Missing_target_media_is_rejected_before_transcription()
    {
        using var harness = new Harness();
        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        await harness.SetHasFileAsync(target, false);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_target_not_epub");

        harness.Documents.CallCount.Should().Be(0);
        harness.Cutter.CallCount.Should().Be(0);
        harness.Transcriber.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
        harness.Usage.CompleteCount.Should().Be(0);
    }

    [Fact]
    public async Task Invalid_target_id_is_rejected_without_accessing_media()
    {
        using var harness = new Harness();
        using var client = harness.Client();

        var (source, _) = await SeedPairAsync(client, harness);

        var response = await PostAsync(client, source, Guid.Empty);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_invalid_request");

        harness.Documents.CallCount.Should().Be(0);
        harness.Cutter.CallCount.Should().Be(0);
        harness.Transcriber.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Missing_target_media_is_rejected_before_transcription()
    {
        using var harness = new Harness();
        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        await harness.SetHasFileAsync(target, false);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_target_not_epub");

        harness.Documents.CallCount.Should().Be(0);
        harness.Cutter.CallCount.Should().Be(0);
        harness.Transcriber.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
        harness.Usage.CompleteCount.Should().Be(0);
    }

    [Fact]
    public async Task Invalid_target_id_is_rejected_without_accessing_media()
    {
        using var harness = new Harness();
        using var client = harness.Client();

        var (source, _) = await SeedPairAsync(client, harness);

        var response = await PostAsync(client, source, Guid.Empty);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_invalid_request");

        harness.Documents.CallCount.Should().Be(0);
        harness.Cutter.CallCount.Should().Be(0);
        harness.Transcriber.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Located_result_uses_end_locator_and_accounts_once()
    {
        using var harness = new Harness();
        harness.Transcriber.Result = new SttResult(Passage, "en", 19.5);

        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        var response = await PostAsync(
            client, source, target, position: 65);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var root = body.RootElement;

        root.GetProperty("located").GetBoolean().Should().BeTrue();
        root.GetProperty("score").GetDouble().Should().BeApproximately(
            1.0, 0.000001);

        root.GetProperty("excerpt").GetString()
            .Should().Contain("golden lantern");

        var locator = root.GetProperty("locator");

        locator.GetProperty("spineIndex").GetInt32().Should().Be(2);
        locator.GetProperty("resourceHref").GetString()
            .Should().Be("chapter-3.xhtml");

        // The listener is at the END of the recognized passage.
        // The source resource begins at text offset 100.
        locator.GetProperty("textOffset").GetInt32()
            .Should().Be(100 + Passage.Length);

        harness.Cutter.CallCount.Should().Be(1);
        harness.Cutter.LastPositionSeconds.Should().Be(65);
        harness.Cutter.LastClipSeconds.Should().Be(20);

        harness.Transcriber.CallCount.Should().Be(1);
        harness.Transcriber.LastFileName.Should().Be("alignment.wav");
        harness.Transcriber.LastContentType.Should().Be("audio/wav");
        harness.Transcriber.LastLanguageHint.Should().Be("en");

        harness.Documents.CallCount.Should().Be(1);

        harness.Usage.BeginCount.Should().Be(1);
        harness.Usage.CompleteCount.Should().Be(1);

        harness.Usage.LastUsage.Should().Be(
            new TranscriptionUsage(1, 19.5, "Completed"));
    }

    [Theory]
    [InlineData("", "insufficient_speech")]
    [InlineData("a chapter title", "insufficient_speech")]
    [InlineData(
        "Every sailor aboard the ship feared the storm approaching across the northern ocean.",
        "no_match")]
    public async Task Matcher_decline_is_200_and_still_accounts_one_transcription(
        string transcript,
        string expectedReason)
    {
        using var harness = new Harness();
        harness.Transcriber.Result = new SttResult(
            transcript, "en", 18);

        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        body.RootElement.GetProperty("located")
            .GetBoolean().Should().BeFalse();

        body.RootElement.GetProperty("reason")
            .GetString().Should().Be(expectedReason);

        body.RootElement.TryGetProperty("locator", out _)
            .Should().BeFalse();

        harness.Cutter.CallCount.Should().Be(1);
        harness.Transcriber.CallCount.Should().Be(1);

        harness.Usage.BeginCount.Should().Be(1);
        harness.Usage.CompleteCount.Should().Be(1);

        harness.Usage.LastUsage.Should().Be(
            new TranscriptionUsage(1, 18, "Completed"));
    }

    [Fact]
    public async Task Provider_failure_is_typed_and_is_never_retried()
    {
        using var harness = new Harness();

        harness.Transcriber.Failure = new SttException(
            SttErrorCodes.Provider,
            "The transcription gateway is unavailable.");

        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ProblemCodeAsync(response))
            .Should().Be(SttErrorCodes.Provider);

        harness.Cutter.CallCount.Should().Be(1);
        harness.Transcriber.CallCount.Should().Be(1);

        harness.Usage.BeginCount.Should().Be(1);
        harness.Usage.CompleteCount.Should().Be(1);
        harness.Usage.LastUsage.Should().Be(
            new TranscriptionUsage(1, null, "ProviderError"));
    }

    [Fact]
    public async Task Unavailable_transcriber_does_not_cut_audio_or_reserve_usage()
    {
        using var harness = new Harness();

        harness.Transcriber.Available = false;

        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_transcription_unavailable");

        harness.Cutter.CallCount.Should().Be(0);
        harness.Transcriber.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Usage_limit_denial_never_calls_transcriber()
    {
        using var harness = new Harness();

        harness.Usage.BeginFailure = new AiUsageException(
            AiUsageBlockReason.LimitReached,
            "The transcription allowance is exhausted.");

        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ProblemCodeAsync(response))
            .Should().Be("ai_usage_limit_reached");

        harness.Usage.BeginCount.Should().Be(1);
        harness.Usage.CompleteCount.Should().Be(0);
        harness.Transcriber.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Provider_timeout_is_reported_once_without_retrying()
    {
        using var harness = new Harness();

        harness.Transcriber.Failure = SttException.TimedOut();

        using var client = harness.Client();
        var (source, target) = await SeedPairAsync(client, harness);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        (await ProblemCodeAsync(response))
            .Should().Be(SttErrorCodes.Timeout);

        harness.Cutter.CallCount.Should().Be(1);
        harness.Transcriber.CallCount.Should().Be(1);

        harness.Usage.BeginCount.Should().Be(1);
        harness.Usage.CompleteCount.Should().Be(1);
        harness.Usage.LastUsage.Should().Be(
            new TranscriptionUsage(1, null, "ProviderError"));
    }

    [Fact]
    public async Task Provider_timeout_is_reported_once_without_retrying()
    {
        using var harness = new Harness();

        harness.Transcriber.Failure = SttException.TimedOut();

        using var client = harness.Client();
        var (source, target) = await SeedPairAsync(client, harness);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        (await ProblemCodeAsync(response))
            .Should().Be(SttErrorCodes.Timeout);

        harness.Cutter.CallCount.Should().Be(1);
        harness.Transcriber.CallCount.Should().Be(1);

        harness.Usage.BeginCount.Should().Be(1);
        harness.Usage.CompleteCount.Should().Be(1);
        harness.Usage.LastUsage.Should().Be(
            new TranscriptionUsage(1, null, "ProviderError"));
    }

    [Fact]
    public async Task Clip_failure_does_not_reserve_provider_usage()
    {
        using var harness = new Harness();

        harness.Cutter.Failure = new AudioClipException(
            AudioClipErrorCodes.Unavailable,
            "ffmpeg is unavailable.");

        using var client = harness.Client();

        var (source, target) = await SeedPairAsync(client, harness);

        var response = await PostAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemCodeAsync(response))
            .Should().Be(AudioClipErrorCodes.Unavailable);

        harness.Cutter.CallCount.Should().Be(1);
        harness.Transcriber.CallCount.Should().Be(0);
        harness.Usage.BeginCount.Should().Be(0);
    }

    private static string Route(Guid source) =>
        $"/api/books/{source}/alignment/locate-text";

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        Guid source,
        Guid target,
        double position = 65) =>
        client.PostAsJsonAsync(Route(source), new
        {
            positionSeconds = position,
            targetBookId = target,
        });

    private static async Task<string?> ProblemCodeAsync(
        HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("title").GetString();
    }

    private static async Task<(Guid Source, Guid Target)> SeedPairAsync(
        HttpClient client,
        Harness harness,
        bool sameWork = true)
    {
        // Distinct titles prevent the library's create-or-match behavior from
        // implicitly treating these editions as the same record.
        var source = await CreateBookAsync(
            client, "audiobook", "Alignment Audio Edition");

        var target = await CreateBookAsync(
            client, "ebook", "Alignment EPUB Edition");

        await harness.SeedMediaAsync(source, target, sameWork);

        return (source, target);
    }

    private static async Task<Guid> CreateBookAsync(
        HttpClient client,
        string type,
        string title)
    {
        var response = await client.PostAsJsonAsync(
            "/api/books",
            new
            {
                type,
                title,
                author = "Alignment Test Author",
                forceCreate = true,
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var book = await response.Content.ReadFromJsonAsync<BookDto>();
        book.Should().NotBeNull();

        return book!.Id;
    }

    private sealed class Harness : IDisposable
    {
        private readonly LibraryEndpointFactory factory = new();
        private readonly WebApplicationFactory<Program> host;

        public bool Allowed { get; set; } = true;

        public FakeAlignmentTranscriber Transcriber { get; } = new();
        public FakeAudioClipCutter Cutter { get; } = new();
        public FakeAlignmentDocuments Documents { get; } = new();
        public RecordingUsageAccounting Usage { get; } = new();

        public Harness()
        {
            host = factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IAlignmentTranscriber>();
                    services.AddSingleton<IAlignmentTranscriber>(Transcriber);

                    services.RemoveAll<IAudioClipCutter>();
                    services.AddSingleton<IAudioClipCutter>(Cutter);

                    services.RemoveAll<IAlignmentDocumentProvider>();
                    services.AddSingleton<IAlignmentDocumentProvider>(Documents);

                    services.RemoveAll<IAiAccessPolicy>();
                    services.AddSingleton<IAiAccessPolicy>(
                        new ConfigurableAccessPolicy(() => Allowed));

                    services.RemoveAll<IAiUsageAccountingService>();
                    services.AddSingleton<IAiUsageAccountingService>(Usage);
                });
            });
        }

        public HttpClient Client() => host.CreateClient();

        public async Task SeedMediaAsync(
            Guid source,
            Guid target,
            bool sameWork)
        {
            var options = new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={factory.DatabasePath}")
                .Options;

            await using var db = new NostosDbContext(options);

            var audio = await db.Books.SingleAsync(x => x.Id == source);
            var epub = await db.Books.SingleAsync(x => x.Id == target);

            audio.FileDetails.HasFile = true;
            audio.FileDetails.FileName = "book.mp3";
            audio.Metadata.Language = "en";

            epub.FileDetails.HasFile = true;
            epub.FileDetails.FileName = "book.epub";

            if (sameWork)
                epub.WorkId = audio.WorkId;

            await db.SaveChangesAsync();
        }

        public async Task SetHasFileAsync(Guid id, bool hasFile)
        {
            var options = new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={factory.DatabasePath}")
                .Options;

            await using var db = new NostosDbContext(options);

            var book = await db.Books.SingleAsync(x => x.Id == id);
            book.FileDetails.HasFile = hasFile;

            await db.SaveChangesAsync();
        }

        public async Task SetTargetFileNameAsync(
            Guid id,
            string fileName)
        {
            var options = new DbContextOptionsBuilder<NostosDbContext>()
                .UseSqlite($"Data Source={factory.DatabasePath}")
                .Options;

            await using var db = new NostosDbContext(options);

            var book = await db.Books.SingleAsync(x => x.Id == id);
            book.FileDetails.FileName = fileName;

            await db.SaveChangesAsync();
        }

        public void Dispose()
        {
            host.Dispose();
            factory.Dispose();
        }
    }

    private sealed class ConfigurableAccessPolicy(
        Func<bool> allowed) : IAiAccessPolicy
    {
        public Task<bool> IsAllowedAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed());
    }

    private sealed class FakeAudioClipCutter : IAudioClipCutter
    {
        public int CallCount { get; private set; }
        public double LastPositionSeconds { get; private set; }
        public double LastClipSeconds { get; private set; }

        public AudioClipException? Failure { get; set; }

        public Task<AudioClip> CutAsync(
            Nostos.Backend.Data.Models.AudioBookModel book,
            double positionSeconds,
            double clipSeconds,
            CancellationToken ct = default)
        {
            CallCount++;
            LastPositionSeconds = positionSeconds;
            LastClipSeconds = clipSeconds;

            if (Failure is not null)
                throw Failure;

            // The fake transcriber does not decode this stream.
            return Task.FromResult(new AudioClip(
                new MemoryStream([1, 2, 3, 4]),
                Math.Min(positionSeconds, clipSeconds)));
        }
    }

    private sealed class FakeAlignmentTranscriber : IAlignmentTranscriber
    {
        public bool Available { get; set; } = true;

        public SttResult Result { get; set; } =
            new(Passage, "en", 19.5);

        public SttException? Failure { get; set; }

        public int CallCount { get; private set; }
        public string? LastFileName { get; private set; }
        public string? LastContentType { get; private set; }
        public string? LastLanguageHint { get; private set; }

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
            LastFileName = fileName;
            LastContentType = contentType;
            LastLanguageHint = languageHint;

            using var buffer = new MemoryStream();
            await audio.CopyToAsync(buffer, ct);

            if (Failure is not null)
                throw Failure;

            return Result;
        }
    }

    private sealed class FakeAlignmentDocuments : IAlignmentDocumentProvider
    {
        public int CallCount { get; private set; }

        public Task<BookTextExtractedDocument> GetAsync(
            Guid bookId,
            IBookAssetStorage storage,
            IBookTextExtractor extractor,
            CancellationToken ct = default)
        {
            CallCount++;

            var document = new BookTextExtractedDocument(
                BookTextSourceFormat.Epub,
                [
                    new BookTextArtifactBlock(
                        0,
                        Passage,
                        [],
                        [
                            new BookTextSourceSegment(
                                0,
                                Passage.Length,
                                new EpubBookTextSourceLocator(
                                    SpineIndex: 2,
                                    ResourceHref: "chapter-3.xhtml",
                                    StartTextOffset: 100,
                                    EndTextOffset: 100 + Passage.Length)),
                        ]),
                ]);

            return Task.FromResult(document);
        }
    }

    private sealed class RecordingUsageAccounting :
        IAiUsageAccountingService
    {
        public int BeginCount { get; private set; }
        public int CompleteCount { get; private set; }

        public TranscriptionUsage? LastUsage { get; private set; }

        public AiUsageException? BeginFailure { get; set; }

        public Task<AiUsageLease?> BeginSttAsync(
            CancellationToken cancellationToken = default)
        {
            BeginCount++;

            if (BeginFailure is not null)
                throw BeginFailure;

            return Task.FromResult<AiUsageLease?>(
                new AiUsageLease(Guid.NewGuid(), AiUsageKind.Stt));
        }

        public Task CompleteSttAsync(
            AiUsageLease? lease,
            TranscriptionUsage usage,
            CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            LastUsage = usage;
            return Task.CompletedTask;
        }

        public Task<AiUsageLease?> BeginLlmTurnAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AiUsageLease?>(null);

        public Task CompleteLlmTurnAsync(
            AiUsageLease? lease,
            LlmProviderUsage usage,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
