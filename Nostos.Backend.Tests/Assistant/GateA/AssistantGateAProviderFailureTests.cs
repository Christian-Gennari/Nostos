using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Tests.Support;
using Nostos.Shared.Dtos;
using Xunit;

namespace Nostos.Backend.Tests.Assistant.GateA;

/// <summary>
/// What a person sees in Ask Nostos when the AI endpoint fails (issue #1466).
/// The REAL <see cref="OpenAiCompatibleLlmProvider"/> talks to a loopback
/// socket through the streamed turn route, so this proves the whole chain —
/// transport, typed exception, endpoint mapping — rather than injecting an
/// already-typed exception as quality-bed C15 does. Every failure must end the
/// stream with one <c>failed</c> event in plain words, with the right retry
/// advice, and with nothing from the upstream body or the key in it.
/// </summary>
public sealed class AssistantGateAProviderFailureTests
{
    private const string KeyVariable = "NOSTOS_GATEA_PROVIDER_FAILURE_KEY";
    private const string KeyValue = "sk-gatea-sentinel-not-a-secret";

    public static TheoryData<string, string, string, bool> Failures => new()
    {
        // Misconfigured SelfHosted key.
        { "401", LlmErrorCodes.Permission, "The AI service did not accept its key. Check the AI provider in Settings.", false },
        { "429", LlmErrorCodes.RateLimited, "Ask Nostos is temporarily busy. Try again shortly.", true },
        { "500", LlmErrorCodes.Provider, "The AI service could not complete this turn. Try again.", true },
        // Misconfigured SelfHosted base URL: the server's own 404 page.
        { "404-html", LlmErrorCodes.Provider, "The AI service could not complete this turn. Try again.", true },
        // Misconfigured SelfHosted base URL: a web UI that answers 200.
        { "200-html", LlmErrorCodes.InvalidResponse, "The AI service could not complete this turn. Try again.", true },
        // Nothing listening at the configured address.
        { "closed", LlmErrorCodes.Provider, "The AI service could not complete this turn. Try again.", true },
        // Headers, then silence.
        { "stall", LlmErrorCodes.Timeout, "Ask Nostos took too long to answer. Try again.", true },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failing_ai_endpoint_ends_the_turn_in_plain_words(
        string mode, string code, string message, bool retryable)
    {
        await using var server = Server(mode);
        var baseUrl = server?.BaseUrl ?? $"http://127.0.0.1:{LoopbackHttpServer.ClosedPort()}/v1";

        var previous = Environment.GetEnvironmentVariable(KeyVariable);
        Environment.SetEnvironmentVariable(KeyVariable, KeyValue);
        try
        {
            using var factory = new LibraryEndpointFactory();
            using var host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Assistant:Enabled", "true");
                builder.UseSetting("Assistant:BaseUrl", baseUrl);
                builder.UseSetting("Assistant:ApiKeyEnvironmentVariable", KeyVariable);
                builder.UseSetting("Assistant:Model", "failure-path-model");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ILlmProvider>();
                    services.AddSingleton<ILlmProvider>(provider => new OpenAiCompatibleLlmProvider(
                        new TimeoutHttpClientFactory(TimeSpan.FromSeconds(2)),
                        provider.GetRequiredService<IAiProviderConfigResolver>(),
                        provider.GetRequiredService<ILogger<OpenAiCompatibleLlmProvider>>()));
                });
            });
            using var client = host.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var response = await client.PostAsJsonAsync(
                AssistantEndpoints.StreamTurnRoute,
                new AssistantTurnRequest(
                    $"conversation-{mode}",
                    $"turn-{mode}",
                    "What did I note about ropes?",
                    new AssistantContextDto("second-brain", "/second-brain"),
                    ConversationId: $"conversation-{mode}",
                    TurnId: $"turn-{mode}"));

            response.IsSuccessStatusCode.Should().BeTrue("a provider failure is an event, not an HTTP error");
            var body = await response.Content.ReadAsStringAsync();
            var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => JsonSerializer.Deserialize<AssistantTurnEventDto>(
                    line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .ToList();

            var terminal = events[^1];
            terminal.Kind.Should().Be(AssistantTurnEventKinds.Failed);
            terminal.Failure!.Code.Should().Be(code);
            terminal.Failure.Message.Should().Be(message);
            terminal.Failure.Retryable.Should().Be(retryable);
            events.Count(e => e.Kind == AssistantTurnEventKinds.Failed).Should().Be(1);

            // Nothing upstream or secret reaches the person.
            body.Should().NotContain(KeyValue);
            body.Should().NotContain(KeyVariable);
            body.Should().NotContain("<html", "an upstream page never reaches the panel");
            body.Should().NotContain("upstream says no");
        }
        finally
        {
            Environment.SetEnvironmentVariable(KeyVariable, previous);
        }
    }

    [Fact]
    public async Task The_owner_finds_the_upstream_detail_in_the_log_and_the_api_client_gets_plain_words()
    {
        // A SelfHosted base URL without /v1: the panel says "try again", so
        // the HTTP status has to be somewhere the owner can find it.
        await using var server = Server("404-html")!;
        var logs = new CapturingLoggerProvider();

        var previous = Environment.GetEnvironmentVariable(KeyVariable);
        Environment.SetEnvironmentVariable(KeyVariable, KeyValue);
        try
        {
            using var factory = new LibraryEndpointFactory();
            using var host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Assistant:Enabled", "true");
                builder.UseSetting("Assistant:BaseUrl", server.BaseUrl);
                builder.UseSetting("Assistant:ApiKeyEnvironmentVariable", KeyVariable);
                builder.UseSetting("Assistant:Model", "failure-path-model");
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            });
            using var client = host.CreateClient();

            var response = await client.PostAsJsonAsync(
                AssistantEndpoints.TurnRoute,
                new AssistantTurnRequest(
                    "conversation-rest", "turn-rest", "What did I note about ropes?",
                    new AssistantContextDto("second-brain", "/second-brain")));

            ((int)response.StatusCode).Should().Be(502);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            problem.GetProperty("title").GetString().Should().Be(LlmErrorCodes.Provider);
            problem.GetProperty("detail").GetString()
                .Should().Be("The AI service could not complete this turn. Try again.");

            var warning = logs.Messages.Should()
                .Contain(m => m.Contains("Ask Nostos provider call failed", StringComparison.Ordinal)).Which;
            warning.Should().Contain("HTTP 404");
            logs.Messages.Should().NotContain(m => m.Contains(KeyValue, StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(KeyVariable, previous);
        }
    }

    [Fact]
    public async Task On_a_managed_deployment_a_refused_key_is_unavailable_not_advice_to_check_settings()
    {
        await using var server = Server("401")!;
        var previous = Environment.GetEnvironmentVariable(KeyVariable);
        Environment.SetEnvironmentVariable(KeyVariable, KeyValue);
        try
        {
            using var factory = new LibraryEndpointFactory();
            using var host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Assistant:Enabled", "true");
                builder.UseSetting("Assistant:BaseUrl", server.BaseUrl);
                builder.UseSetting("Assistant:ApiKeyEnvironmentVariable", KeyVariable);
                builder.UseSetting("Assistant:Model", "failure-path-model");
                builder.ConfigureServices(Managed);
            });
            using var client = host.CreateClient();

            var body = await (await client.PostAsJsonAsync(
                AssistantEndpoints.StreamTurnRoute,
                new AssistantTurnRequest(
                    "conversation-managed", "turn-managed", "What did I note about ropes?",
                    new AssistantContextDto("second-brain", "/second-brain")))).Content.ReadAsStringAsync();
            var terminal = JsonSerializer.Deserialize<AssistantTurnEventDto>(
                body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1],
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

            terminal.Failure!.Code.Should().Be(LlmErrorCodes.Permission);
            terminal.Failure.Message.Should().Be("Ask Nostos is temporarily unavailable.");
            terminal.Failure.Retryable.Should().BeFalse();
            body.Should().NotContain("Settings");
        }
        finally
        {
            Environment.SetEnvironmentVariable(KeyVariable, previous);
        }
    }

    [Theory]
    [InlineData(false, "Ask Nostos needs an AI provider configured in Settings.")]
    [InlineData(true, "Ask Nostos is temporarily unavailable.")]
    public async Task Without_a_key_the_stream_says_what_this_deployment_can_do_about_it(bool managed, string message)
    {
        const string missing = "NOSTOS_GATEA_KEY_THAT_IS_NEVER_SET";
        using var factory = new LibraryEndpointFactory();
        using var host = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Assistant:Enabled", "true");
            builder.UseSetting("Assistant:BaseUrl", "http://127.0.0.1:9/v1");
            builder.UseSetting("Assistant:ApiKeyEnvironmentVariable", missing);
            builder.UseSetting("Assistant:Model", "failure-path-model");
            if (managed)
                builder.ConfigureServices(Managed);
        });
        using var client = host.CreateClient();

        var stream = await (await client.PostAsJsonAsync(
            AssistantEndpoints.StreamTurnRoute,
            new AssistantTurnRequest(
                "conversation-nokey", "turn-nokey", "Hello",
                new AssistantContextDto("second-brain", "/second-brain")))).Content.ReadAsStringAsync();
        var rest = await client.PostAsJsonAsync(
            AssistantEndpoints.TurnRoute,
            new AssistantTurnRequest(
                "conversation-nokey-rest", "turn-nokey-rest", "Hello",
                new AssistantContextDto("second-brain", "/second-brain")));
        var problem = await rest.Content.ReadFromJsonAsync<JsonElement>();

        var terminal = JsonSerializer.Deserialize<AssistantTurnEventDto>(
            stream.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1],
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        terminal.Failure!.Code.Should().Be(LlmErrorCodes.NotConfigured);
        terminal.Failure.Message.Should().Be(message);
        problem.GetProperty("title").GetString().Should().Be(LlmErrorCodes.NotConfigured);
        if (managed)
        {
            problem.GetProperty("detail").GetString().Should().Be(message);
            (stream + problem.GetRawText()).Should().NotContain(missing, "an operator's variable name never reaches a managed user");
        }
    }

    /// <summary>The host runs the AI; the user has no provider setting (Nostos Cloud).</summary>
    private static void Managed(IServiceCollection services)
    {
        services.RemoveAll<Nostos.Backend.Configuration.DeploymentDescriptor>();
        var selfHosted = Nostos.Backend.Configuration.DeploymentDescriptor.For(Nostos.Backend.Configuration.DeploymentMode.SelfHosted);
        services.AddSingleton(selfHosted with
        {
            Capabilities = selfHosted.Capabilities with { CanConfigureAiProvider = false, ManagedAi = true },
        });
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose()
        {
        }

        private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    messages.Enqueue(formatter(state, exception));
            }
        }
    }

    private static LoopbackHttpServer? Server(string mode) => mode switch
    {
        "401" => LoopbackHttpServer.Responding(401, """{"error":{"message":"upstream says no"}}"""),
        "429" => LoopbackHttpServer.Responding(429, """{"error":{"message":"upstream says no"}}"""),
        "500" => LoopbackHttpServer.Responding(500, """{"error":{"message":"upstream says no"}}"""),
        "404-html" => LoopbackHttpServer.Responding(404, "<html><body>Not Found</body></html>", "text/html"),
        "200-html" => LoopbackHttpServer.Responding(200, "<!doctype html><html><body>Open WebUI</body></html>", "text/html"),
        "stall" => LoopbackHttpServer.Start(async (_, stream, ct) =>
        {
            await LoopbackHttpServer.WritePartialAsync(stream, 400, "{\"choices", ct);
            await Task.Delay(Timeout.Infinite, ct);
        }),
        _ => null,
    };
}
