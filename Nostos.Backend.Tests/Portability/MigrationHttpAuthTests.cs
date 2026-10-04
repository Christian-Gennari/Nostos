using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Backend.Services.Portability.Transfers;
using Nostos.Backend.Tests.Support;
using Nostos.Product.Composition;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// An authenticated host configures the optional migration authorization
/// policy: every route requires it, and an unknown selector still answers the
/// identical typed 404 shape with no ownership information.
/// </summary>
public sealed class MigrationHttpAuthTests
{
    private const string PolicyName = "migration-owner";

    [Fact]
    public async Task Every_migration_route_challenges_unauthenticated_requests()
    {
        await using var host = await AuthHost.StartAsync();
        var job = Guid.NewGuid();

        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Post, "/api/portability/migration/preflight"),
            (HttpMethod.Post, "/api/portability/migration/jobs"),
            (HttpMethod.Get, $"/api/portability/migration/jobs/{job}"),
            (HttpMethod.Post, $"/api/portability/migration/jobs/{job}/cancel"),
            (HttpMethod.Post, $"/api/portability/migration/jobs/{job}/retry"),
            (HttpMethod.Post, $"/api/portability/migration/jobs/{job}/upload-session"),
            (HttpMethod.Get, $"/api/portability/migration/jobs/{job}/upload-session"),
            (HttpMethod.Put, $"/api/portability/migration/jobs/{job}/upload-session/chunks/0"),
            (HttpMethod.Post, $"/api/portability/migration/jobs/{job}/upload-session/complete"),
        };

        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = new ByteArrayContent([]),
            };
            using var response = await host.Client.SendAsync(request);
            response.StatusCode.Should().Be(
                HttpStatusCode.Unauthorized,
                $"{method} {path} must require the configured policy");
        }
    }

    [Fact]
    public async Task Authenticated_unknown_selectors_return_the_identical_typed_404()
    {
        await using var host = await AuthHost.StartAsync();
        var unknown = Guid.NewGuid();

        using var jobRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/portability/migration/jobs/{unknown}");
        jobRequest.Headers.Add("X-Test-Authenticated", "1");
        using var jobResponse = await host.Client.SendAsync(jobRequest);
        jobResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertNotFoundShape(await jobResponse.Content.ReadAsStringAsync());

        using var chunkRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/portability/migration/jobs/{unknown}/upload-session/chunks/0")
        {
            Content = new ByteArrayContent([1, 2, 3]),
        };
        chunkRequest.Headers.Add("X-Test-Authenticated", "1");
        chunkRequest.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-2/3");
        chunkRequest.Content.Headers.TryAddWithoutValidation("X-Nostos-Chunk-SHA256", new string('a', 64));
        using var chunkResponse = await host.Client.SendAsync(chunkRequest);
        chunkResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertNotFoundShape(await chunkResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Authenticated_requests_reach_the_handler_normally()
    {
        await using var host = await AuthHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/portability/migration/preflight")
        {
            Content = JsonContent.Create(new
            {
                incomingCounts = new
                {
                    works = 0,
                    books = 0,
                    notes = 0,
                    topics = 0,
                    noteTopics = 0,
                    writings = 0,
                    writingNotes = 0,
                    collections = 0,
                    collectionMemberships = 0,
                    acquisitions = 0,
                    assistantSettings = 0,
                    noteImportBookLinks = 0,
                    mediaEntries = 0,
                },
                declaredArchiveBytes = 1024,
                declaredMediaBytes = 0,
                maxSingleEntryBytes = 1024,
                declaredFormatVersion = 1,
                declaredDataVersion = 1,
                isOperationalBackup = false,
            }),
        };
        request.Headers.Add("X-Test-Authenticated", "1");
        using var response = await host.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("evaluation").GetProperty("decision").GetString()
            .Should().Be("AllowedEmpty");
    }

    private static void AssertNotFoundShape(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().Equal("error", "message");
        document.RootElement.GetProperty("error").GetString().Should().Be("migration_not_found");
        payload.Should().NotContain("owner-a").And.NotContain("account");
    }

    private sealed class AuthHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _root;

        private AuthHost(WebApplication app, string root)
        {
            _app = app;
            _root = root;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<AuthHost> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "nostos-migration-auth-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "auth.db");
            LibraryEndpointBootstrap.EnsureSchemaAndHistory(databasePath);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = root,
                EnvironmentName = "Testing",
            });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddRouting();
            builder.Services
                .AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
            builder.Services.AddAuthorization(options =>
                options.AddPolicy(PolicyName, policy => policy.RequireAuthenticatedUser()));
            builder.Services.AddDbContext<NostosDbContext>(options => options.UseSqlite(
                $"Data Source={databasePath};Pooling=False"));
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton(Options.Create(new TransferStorageOptions
            {
                DiskSafetyMarginBytes = 0,
                DiskSafetyMarginPercent = 0,
            }));
            builder.Services.AddSingleton(new TransferPathResolver(
                TransferPathResolver.EnsureRootDirectory(Path.Combine(root, "transfers"))));
            builder.Services.AddSingleton<ITransferVolume>(new MigrationEngineHarness.TestVolume());
            builder.Services.AddScoped<ITransferStorageCapacity, TransferStorageCapacity>();
            builder.Services.AddScoped<IMigrationJobStore, EfMigrationJobStore>();
            builder.Services.AddSingleton<MigrationFileMutex>();
            builder.Services.AddSingleton<MigrationJobCancellationRegistry>();
            builder.Services.AddSingleton<IMigrationMaintenanceGate, NoopMaintenanceGate>();
            builder.Services.AddScoped<FileMigrationUploadStore>();
            builder.Services.AddScoped<MigrationTransferCleanup>();
            builder.Services.AddScoped<SelfHostedMigrationTransferService>();
            builder.Services.AddScoped<ISelfHostedMigrationUploads>(
                provider => provider.GetRequiredService<SelfHostedMigrationTransferService>());
            builder.Services.AddScoped<IMigrationTransferService>(
                provider => provider.GetRequiredService<SelfHostedMigrationTransferService>());
            builder.Services.AddScoped<IMigrationPreflightService, SelfHostedMigrationPreflightService>();
            builder.Services.AddScoped<SelfHostedMigrationJobService>();
            builder.Services.AddSingleton<IMigrationPhaseAvailability, AlwaysAvailable>();

            var app = builder.Build();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapMigrationEndpoints(new NostosProductEndpointPolicies(
                MigrationAuthorizationPolicy: PolicyName));
            await app.StartAsync();
            return new AuthHost(app, root);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class AlwaysAvailable : IMigrationPhaseAvailability
    {
        public bool IsAvailable(MigrationDirection direction) => true;
    }

    private sealed class NoopMaintenanceGate : IMigrationMaintenanceGate
    {
        public bool IsMaintenanceRequested => false;

        public ValueTask<IAsyncDisposable> EnterAsync(CancellationToken ct) =>
            ValueTask.FromResult<IAsyncDisposable>(new NoopLease());

        private sealed class NoopLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-Authenticated"))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "owner-a")],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
