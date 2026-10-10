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
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Alignment;
using Nostos.Product.Services.Ai;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class AlignmentEndpointTests
{
    private const string Passage =
        "The patient traveller carried a golden lantern through " +
        "the quiet orchard before sunrise.";

    [Fact]
    public async Task Missing_source_is_a_typed_404()
    {
        using var factory = new LibraryEndpointFactory();
        var alignment = new FakeAlignmentService();
        using var host = CreateHost(factory, alignment: alignment);
        using var client = host.CreateClient();

        var response = await LocateAsync(
            client, Guid.NewGuid(), Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_book_not_found");
        alignment.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Missing_target_is_a_typed_404()
    {
        using var factory = new LibraryEndpointFactory();
        var alignment = new FakeAlignmentService();
        using var host = CreateHost(factory, alignment: alignment);
        using var client = host.CreateClient();

        var source = await CreateBookAsync(client, "audiobook");

        var response = await LocateAsync(
            client, source, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_book_not_found");
        alignment.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Invalid_position_is_rejected_before_alignment()
    {
        using var factory = new LibraryEndpointFactory();
        var alignment = new FakeAlignmentService();
        using var host = CreateHost(factory, alignment: alignment);
        using var client = host.CreateClient();

        var response = await LocateAsync(
            client, Guid.NewGuid(), Guid.NewGuid(), 0);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_invalid_request");
        alignment.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Source_must_be_an_audiobook()
    {
        using var factory = new LibraryEndpointFactory();
        var alignment = new FakeAlignmentService();
        using var host = CreateHost(factory, alignment: alignment);
        using var client = host.CreateClient();

        var source = await CreateBookAsync(client, "ebook");
        var target = await CreateBookAsync(client, "ebook");

        var response = await LocateAsync(client, source, target);

        response.StatusCode.Should().Be(
            HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_source_not_audio");
        alignment.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Target_must_be_an_epub()
    {
        using var factory = new LibraryEndpointFactory();
        var alignment = new FakeAlignmentService();
        using var host = CreateHost(factory, alignment: alignment);
        using var client = host.CreateClient();

        var source = await CreateBookAsync(client, "audiobook");
        var target = await CreateBookAsync(client, "ebook");

        var response = await LocateAsync(client, source, target);

        response.StatusCode.Should().Be(
            HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_target_not_epub");
        alignment.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Different_works_are_rejected_before_ai_usage()
    {
        using var factory = new LibraryEndpointFactory();
        var alignment = new FakeAlignmentService();
        var usage = new RecordingUsageAccounting();

        using var host = CreateHost(
            factory, alignment: alignment, usage: usage);
        using var client = host.CreateClient();

        var (source, target) = await SeedEditionsAsync(
            client, factory, sameWork: false);

        var response = await LocateAsync(client, source, target);

        response.StatusCode.Should().Be(
            HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(response))
            .Should().Be("alignment_work_mismatch");

        alignment.CallCount.Should().Be(0);
        usage.BeginCount.Should().Be(0);
        usage.CompleteCount.Should().Be(0);
    }

    [Fact]
    public async Task Access_denial_prevents_alignment_and_usage()
    {
        using var factory = new LibraryEndpointFactory();
        var alignment = new FakeAlignmentService();
        var usage = new RecordingUsageAccounting();

        using var host = CreateHost(
            factory,
            alignment: alignment,
            usage: usage,
            allowed: false);

        using var client = host.CreateClient();
        var (source, target) = await SeedEditionsAsync(
            client, factory, sameWork: true);

        var response = await LocateAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response))
            .Should().Be(SttErrorCodes.AccessDenied);

        alignment.CallCount.Should().Be(0);
        usage.BeginCount.Should().Be(0);
    }

    [Fact]
    public async Task Located_result_returns_the_end_locator()
    {
        using var factory = new LibraryEndpointFactory();
        var alignment = new FakeAlignmentService
        {
            Result = new AlignmentLocateResult(
                Located: true,
                Reason: null,
                Score: 0.98,
                Excerpt: Passage,
                Locator: new AlignmentLocator(
                    3, "chapter-4.xhtml", null, 147)),
        };

        using var host = CreateHost(factory, alignment: alignment);
        using var client = host.CreateClient();

        var (source, target) = await SeedEditionsAsync(
            client, factory, sameWork: true);

        var response = await LocateAsync(
            client, source, target, 65);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        var body = document.RootElement;

        body.GetProperty("located").GetBoolean().Should().BeTrue();
        body.GetProperty("score").GetDouble().Should().Be(0.98);
        body.GetProperty("excerpt").GetString().Should().Be(Passage);

        var locator = body.GetProperty("locator");
        locator.GetProperty("spineIndex").GetInt32().Should().Be(3);
        locator.GetProperty("resourceHref").GetString()
            .Should().Be("chapter-4.xhtml");
        locator.GetProperty("textOffset").GetInt32().Should().Be(147);

        alignment.CallCount.Should().Be(1);
        alignment.LastPosition.Should().Be(65);
    }

    [Theory]
    [InlineData("insufficient_speech")]
    [InlineData("no_match")]
    [InlineData("weak_match")]
    [InlineData("ambiguous")]
    public async Task Matcher_rejection_is_a_normal_200(string reason)
    {
        using var factory = new LibraryEndpointFactory();
        var alignment = new FakeAlignmentService
        {
            Result = AlignmentLocateResult.Declined(reason),
        };

        using var host = CreateHost(factory, alignment: alignment);
        using var client = host.CreateClient();

        var (source, target) = await SeedEditionsAsync(
            client, factory, sameWork: true);

        var response = await LocateAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        document.RootElement.GetProperty("located")
            .GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("reason")
            .GetString().Should().Be(reason);

        alignment.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Provider_failure_is_typed_and_not_retried()
    {
        using var factory = new LibraryEndpointFactory();
        var alignment = new FakeAlignmentService
        {
            Failure = new SttException(
                SttErrorCodes.Provider,
                "The transcription provider failed."),
        };

        using var host = CreateHost(factory, alignment: alignment);
        using var client = host.CreateClient();

        var (source, target) = await SeedEditionsAsync(
            client, factory, sameWork: true);

        var response = await LocateAsync(client, source, target);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ProblemCodeAsync(response))
            .Should().Be(SttErrorCodes.Provider);
        alignment.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Usage_reservation_denial_is_typed()
    {
        using var factory = new LibraryEndpointFactory();
        var usage = new RecordingUsageAccounting
        {
            BlockReason = AiUsageBlockReason.LimitReached,
        };

        // The real alignment service is exercised separately in service tests.
        // Here we verify the endpoint maps host accounting failures.
        var alignment = new FakeAlignmentService
        {
            Failure = new AiUsageException(
                AiUsageBlockReason.LimitReached,
                "The AI allowance is exhausted."),
        };

        using var host = CreateHost(
            factory, alignment: alignment, usage: usage);
        using var client = host.CreateClient();

        var (source, target) = await SeedEditionsAsync(
            client, factory, sameWork: true);

        var response = await LocateAsync(client, source, target);

        response.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests);
        (await ProblemCodeAsync(response))
            .Should().Be("ai_usage_limit_reached");

        alignment.CallCount.Should().Be(1);
    }

    internal static WebApplicationFactory<Program> CreateHost(
        LibraryEndpointFactory factory,
        IAlignmentService? alignment = null,
        IAiUsageAccountingService? usage = null,
        bool allowed = true)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                if (alignment is not null)
                {
                    services.RemoveAll<IAlignmentService>();
                    services.AddSingleton(alignment);
                }

                if (usage is not null)
                {
                    services.RemoveAll<IAiUsageAccountingService>();
                    services.AddSingleton(usage);
                }

                services.RemoveAll<IAiAccessPolicy>();
                services.AddSingleton<IAiAccessPolicy>(
                    new TestAccessPolicy(allowed));
            });
        });
    }

    internal static async Task<(Guid Source, Guid Target)> SeedEditionsAsync(
        HttpClient client,
        LibraryEndpointFactory factory,
        bool sameWork)
    {
        var source = await CreateBookAsync(client, "audiobook");
        var target = await CreateBookAsync(client, "ebook");

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

        return (source, target);
    }

    internal static async Task<Guid> CreateBookAsync(
        HttpClient client,
        string type)
    {
        var response = await client.PostAsJsonAsync(
            "/api/books",
            new
            {
                type,
                title = $"Alignment Test {Guid.NewGuid():N}",
                author = "Test Author",
                forceCreate = true,
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var book = await response.Content.ReadFromJsonAsync<BookDto>();
        return book!.Id;
    }

    internal static Task<HttpResponseMessage> LocateAsync(
        HttpClient client,
        Guid source,
        Guid target,
        double position = 45)
    {
        return client.PostAsJsonAsync(
            $"/api/books/{source}/alignment/locate-text",
            new
            {
                positionSeconds = position,
                targetBookId = target,
            });
    }

    internal static async Task<string?> ProblemCodeAsync(
        HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("title").GetString();
    }

    private sealed class TestAccessPolicy(bool allowed) : IAiAccessPolicy
    {
        public Task<bool> IsAllowedAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(allowed);
    }

    private sealed class FakeAlignmentService : IAlignmentService
    {
        public AlignmentLocateResult Result { get; set; } =
            AlignmentLocateResult.Declined("no_match");

        public Exception? Failure { get; set; }
        public int CallCount { get; private set; }
        public double LastPosition { get; private set; }

        public Task<AlignmentLocateResult> LocateAsync(
            AudioBookModel source,
            EBookModel target,
            double positionSeconds,
            CancellationToken ct = default)
        {
            CallCount++;
            LastPosition = positionSeconds;

            if (Failure is not null)
                throw Failure;

            return Task.FromResult(Result);
        }
    }

    internal sealed class RecordingUsageAccounting : IAiUsageAccountingService
    {
        public int BeginCount { get; private set; }
        public int CompleteCount { get; private set; }
        public AiUsageBlockReason? BlockReason { get; set; }
        public TranscriptionUsage? LastUsage { get; private set; }

        public Task<AiUsageLease?> BeginSttAsync(
            CancellationToken cancellationToken = default)
        {
            BeginCount++;

            if (BlockReason is { } reason)
                throw new AiUsageException(reason, "Usage denied.");

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
