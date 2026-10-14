using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

/// <summary>
/// Full-host coverage for the read-aloud endpoints.
///
/// The reader falls back to the device voice on any failure, so what matters
/// here is that every failure is typed, that a host without a provider answers
/// "not available" rather than an error, and that usage is reserved before the
/// provider is called and settled whatever happens.
/// </summary>
public sealed class ReadAloudEndpointTests
{
    // ------------------------------------------------------------------
    // Status
    // ------------------------------------------------------------------

    [Fact]
    public async Task Status_lists_the_voices_for_the_books_language()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, new FakeReadAloudProvider());
        using var client = host.CreateClient();

        var status = await client.GetFromJsonAsync<ReadAloudEndpoints.ReadAloudStatusResponse>(
            $"{ReadAloudEndpoints.StatusRoute}?language=en-GB");

        status!.Available.Should().BeTrue();
        status.Managed.Should().BeFalse();
        status.DefaultVoice.Should().Be("bf_emma");
        status.Voices.Should().NotBeEmpty().And.OnlyContain(voice => voice.Language == "en-GB");
        status.Reason.Should().BeNull();
    }

    [Fact]
    public async Task Status_is_unavailable_for_a_language_no_voice_reads()
    {
        const string language = "sv";
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, new FakeReadAloudProvider());
        using var client = host.CreateClient();

        var status = await client.GetFromJsonAsync<ReadAloudEndpoints.ReadAloudStatusResponse>(
            $"{ReadAloudEndpoints.StatusRoute}?language={language}");

        status!.Available.Should().BeFalse();
        status.DefaultVoice.Should().BeNull();
        status.Voices.Should().BeEmpty();
        status.Reason.Should().Be(ReadAloudErrorCodes.UnsupportedLanguage);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Status_offers_every_voice_and_chooses_none_when_the_book_does_not_say_its_language(
        string? language)
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, new FakeReadAloudProvider());
        using var client = host.CreateClient();

        var route = language is null
            ? ReadAloudEndpoints.StatusRoute
            : $"{ReadAloudEndpoints.StatusRoute}?language={language}";
        var status = await client.GetFromJsonAsync<ReadAloudEndpoints.ReadAloudStatusResponse>(route);

        status!.Available.Should().BeTrue();
        status.DefaultVoice.Should().BeNull();
        status.Voices.Should().HaveCount(KokoroVoices.All.Count);
        status.Reason.Should().Be(ReadAloudErrorCodes.LanguageUnknown);
    }

    [Fact]
    public async Task A_host_without_a_provider_answers_unavailable_not_an_error()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider: null);
        using var client = host.CreateClient();

        var status = await client.GetFromJsonAsync<ReadAloudEndpoints.ReadAloudStatusResponse>(
            $"{ReadAloudEndpoints.StatusRoute}?language=en");
        status!.Available.Should().BeFalse();
        status.Reason.Should().Be(ReadAloudErrorCodes.Disabled);

        var response = await PostAsync(client, "It is a truth universally acknowledged.", language: "en");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemTitleAsync(response)).Should().Be(ReadAloudErrorCodes.Disabled);
    }

    [Fact]
    public async Task Status_is_unavailable_when_the_host_policy_denies_access()
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, new FakeReadAloudProvider(), entitled: false);
        using var client = host.CreateClient();

        var status = await client.GetFromJsonAsync<ReadAloudEndpoints.ReadAloudStatusResponse>(
            $"{ReadAloudEndpoints.StatusRoute}?language=en");

        status!.Available.Should().BeFalse();
        status.Reason.Should().Be(ReadAloudErrorCodes.AccessDenied);
    }

    [Theory]
    [InlineData(false, true, ReadAloudErrorCodes.Disabled)]
    [InlineData(true, false, ReadAloudErrorCodes.NotConfigured)]
    public async Task Status_says_why_an_unusable_provider_is_unavailable(bool enabled, bool configured, string reason)
    {
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, new FakeReadAloudProvider { Enabled = enabled, Configured = configured });
        using var client = host.CreateClient();

        var status = await client.GetFromJsonAsync<ReadAloudEndpoints.ReadAloudStatusResponse>(
            $"{ReadAloudEndpoints.StatusRoute}?language=en");

        status!.Available.Should().BeFalse();
        status.Reason.Should().Be(reason);
    }

    // ------------------------------------------------------------------
    // Speech
    // ------------------------------------------------------------------

    [Fact]
    public async Task Speech_returns_the_audio_and_settles_usage_by_characters()
    {
        var provider = new FakeReadAloudProvider();
        var usage = new RecordingUsage();
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider, usage: usage);
        using var client = host.CreateClient();

        const string text = "It is a truth universally acknowledged.";
        var response = await PostAsync(client, $"  {text}  ", language: "en-GB");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("audio/mpeg");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(provider.Audio);

        provider.Requests.Should().ContainSingle()
            .Which.Should().Be(new ReadAloudSpeechRequest(text, "bf_emma"));
        usage.Reserved.Should().Equal(text.Length);
        usage.Settled.Should().ContainSingle()
            .Which.Should().Be(new SpeechSynthesisUsage(1, text.Length, "Completed"));
    }

    [Fact]
    public async Task A_named_voice_is_used_when_the_host_offers_it()
    {
        var provider = new FakeReadAloudProvider();
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider);
        using var client = host.CreateClient();

        await PostAsync(client, "Mr. Bennet made no answer.", language: "en", voice: "bm_george");

        provider.Requests.Should().ContainSingle().Which.Voice.Should().Be("bm_george");
    }

    [Fact]
    public async Task An_unknown_voice_is_never_passed_to_the_provider()
    {
        var provider = new FakeReadAloudProvider();
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider);
        using var client = host.CreateClient();

        await PostAsync(client, "Mr. Bennet made no answer.", language: "it", voice: "../../etc/passwd");

        provider.Requests.Should().ContainSingle().Which.Voice.Should().Be("if_sara");
    }

    [Fact]
    public async Task A_language_no_voice_reads_is_a_typed_422_and_costs_nothing()
    {
        var provider = new FakeReadAloudProvider();
        var usage = new RecordingUsage();
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider, usage: usage);
        using var client = host.CreateClient();

        var response = await PostAsync(client, "Det var en afton i början av maj.", language: "sv");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ProblemTitleAsync(response)).Should().Be(ReadAloudErrorCodes.UnsupportedLanguage);
        provider.Requests.Should().BeEmpty();
        usage.Reserved.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_text_is_a_typed_400(string text)
    {
        var provider = new FakeReadAloudProvider();
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider);
        using var client = host.CreateClient();

        var response = await PostAsync(client, text, language: "en");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ProblemTitleAsync(response)).Should().Be(ReadAloudErrorCodes.InvalidRequest);
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Text_over_the_limit_is_rejected_before_the_provider_is_called()
    {
        var provider = new FakeReadAloudProvider { MaxCharacters = 20 };
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider);
        using var client = host.CreateClient();

        var response = await PostAsync(client, new string('a', 21), language: "en");

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await ProblemTitleAsync(response)).Should().Be(ReadAloudErrorCodes.TooLong);
        provider.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, true, ReadAloudErrorCodes.Disabled)]
    [InlineData(true, false, ReadAloudErrorCodes.NotConfigured)]
    public async Task An_unusable_provider_is_a_typed_503(bool enabled, bool configured, string code)
    {
        var provider = new FakeReadAloudProvider { Enabled = enabled, Configured = configured };
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider);
        using var client = host.CreateClient();

        var response = await PostAsync(client, "Mr. Bennet made no answer.", language: "en");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemTitleAsync(response)).Should().Be(code);
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_denied_request_is_a_typed_403()
    {
        var provider = new FakeReadAloudProvider();
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider, entitled: false);
        using var client = host.CreateClient();

        var response = await PostAsync(client, "Mr. Bennet made no answer.", language: "en");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ProblemTitleAsync(response)).Should().Be(ReadAloudErrorCodes.AccessDenied);
        provider.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_exhausted_allowance_is_the_shared_429_and_the_provider_is_not_called()
    {
        var provider = new FakeReadAloudProvider();
        var usage = new RecordingUsage
        {
            Block = new AiUsageException(AiUsageBlockReason.LimitReached, "The monthly allowance is used up."),
        };
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider, usage: usage);
        using var client = host.CreateClient();

        var response = await PostAsync(client, "Mr. Bennet made no answer.", language: "en");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ProblemTitleAsync(response)).Should().Be("ai_usage_limit_reached");
        provider.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ReadAloudErrorCodes.RateLimited, HttpStatusCode.TooManyRequests)]
    [InlineData(ReadAloudErrorCodes.Timeout, HttpStatusCode.GatewayTimeout)]
    [InlineData(ReadAloudErrorCodes.Permission, HttpStatusCode.BadGateway)]
    [InlineData(ReadAloudErrorCodes.Provider, HttpStatusCode.BadGateway)]
    [InlineData(ReadAloudErrorCodes.InvalidResponse, HttpStatusCode.BadGateway)]
    public async Task A_provider_failure_is_typed_and_settled_with_no_characters(
        string code,
        HttpStatusCode expected)
    {
        var provider = new FakeReadAloudProvider { Failure = new ReadAloudException(code, "upstream said no") };
        var usage = new RecordingUsage();
        using var factory = new LibraryEndpointFactory();
        using var host = CreateHost(factory, provider, usage: usage);
        using var client = host.CreateClient();

        var response = await PostAsync(client, "Mr. Bennet made no answer.", language: "en");

        response.StatusCode.Should().Be(expected);
        (await ProblemTitleAsync(response)).Should().Be(code);
        usage.Settled.Should().ContainSingle()
            .Which.Should().Be(new SpeechSynthesisUsage(1, 0, "ProviderError"));
    }

    // ------------------------------------------------------------------
    // Harness
    // ------------------------------------------------------------------

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string text,
        string? language = null,
        string? voice = null) =>
        client.PostAsJsonAsync(
            ReadAloudEndpoints.SpeechRoute,
            new ReadAloudEndpoints.ReadAloudSpeechBody(text, voice, language));

    private static async Task<string?> ProblemTitleAsync(HttpResponseMessage response)
    {
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        return document.GetProperty("title").GetString();
    }

    private static WebApplicationFactory<Program> CreateHost(
        LibraryEndpointFactory factory,
        FakeReadAloudProvider? provider,
        RecordingUsage? usage = null,
        bool entitled = true) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IReadAloudProvider>();
                if (provider is not null)
                    services.AddSingleton<IReadAloudProvider>(provider);

                if (usage is not null)
                {
                    services.RemoveAll<IAiUsageAccountingService>();
                    services.AddSingleton<IAiUsageAccountingService>(usage);
                }

                if (!entitled)
                {
                    services.RemoveAll<IAiAccessPolicy>();
                    services.AddSingleton<IAiAccessPolicy, DenyAccessPolicy>();
                }
            });
        });

    private sealed class DenyAccessPolicy : IAiAccessPolicy
    {
        public Task<bool> IsAllowedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeReadAloudProvider : IReadAloudProvider
    {
        public bool Enabled { get; set; } = true;
        public bool Configured { get; set; } = true;
        public int MaxCharacters { get; set; } = 1200;
        public Exception? Failure { get; set; }
        public byte[] Audio { get; } = [0x49, 0x44, 0x33, 0x04, 0x00];
        public List<ReadAloudSpeechRequest> Requests { get; } = [];

        public IReadOnlyList<ReadAloudVoice> Voices => KokoroVoices.All;

        public Task<ReadAloudAvailability> GetAvailabilityAsync(CancellationToken ct = default) =>
            Task.FromResult(new ReadAloudAvailability(Enabled, Configured, Managed: false, MaxCharacters));

        public Task<ReadAloudAudio> SynthesizeAsync(
            ReadAloudSpeechRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Failure is null
                ? Task.FromResult(new ReadAloudAudio(Audio, "audio/mpeg"))
                : Task.FromException<ReadAloudAudio>(Failure);
        }
    }

    private sealed class RecordingUsage : IAiUsageAccountingService
    {
        public AiUsageException? Block { get; set; }
        public List<int> Reserved { get; } = [];
        public List<SpeechSynthesisUsage> Settled { get; } = [];

        public Task<AiUsageLease?> BeginTtsAsync(int characters, CancellationToken cancellationToken = default)
        {
            if (Block is not null)
                return Task.FromException<AiUsageLease?>(Block);
            Reserved.Add(characters);
            return Task.FromResult<AiUsageLease?>(new AiUsageLease(Guid.NewGuid(), AiUsageKind.Tts));
        }

        public Task CompleteTtsAsync(
            AiUsageLease? lease,
            SpeechSynthesisUsage usage,
            CancellationToken cancellationToken = default)
        {
            lease.Should().NotBeNull();
            Settled.Add(usage);
            return Task.CompletedTask;
        }

        public Task<AiUsageLease?> BeginLlmTurnAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AiUsageLease?>(null);

        public Task CompleteLlmTurnAsync(
            AiUsageLease? lease,
            LlmProviderUsage usage,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AiUsageLease?> BeginSttAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AiUsageLease?>(null);

        public Task CompleteSttAsync(
            AiUsageLease? lease,
            TranscriptionUsage usage,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
