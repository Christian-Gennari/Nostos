using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Recovery;
using Nostos.Product.Composition;
using Xunit;

namespace Nostos.Backend.Tests.Endpoints;

public sealed class ManagedBackupEndpointsTests
{
    [Fact]
    public async Task Product_composition_registers_the_not_supported_default_catalog()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNostosProduct(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IManagedBackupCatalog>();

        Assert.IsType<NotSupportedManagedBackupCatalog>(catalog);
        await Assert.ThrowsAsync<ManagedBackupsNotSupportedException>(
            () => catalog.ListAsync());
    }

    [Fact]
    public async Task List_requires_an_authenticated_customer()
    {
        await using var host = await TestHost.StartAsync(new NotSupportedManagedBackupCatalog());

        using var response = await host.Client.GetAsync(ManagedBackupEndpoints.Route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Host_can_apply_its_stronger_product_access_policy()
    {
        var catalog = new FixtureManagedBackupCatalog(new ManagedBackupListing(14, []));
        await using var host = await TestHost.StartAsync(catalog, "ProductAccess");
        using var request = TestHost.AuthenticatedGet();

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, catalog.ListCalls);
    }

    [Fact]
    public async Task Default_catalog_fails_closed_as_not_supported()
    {
        await using var host = await TestHost.StartAsync(new NotSupportedManagedBackupCatalog());
        using var request = TestHost.AuthenticatedGet();

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_list_returns_provider_neutral_read_only_contract()
    {
        var createdAt = new DateTime(2026, 10, 7, 0, 30, 0, DateTimeKind.Utc);
        var catalog = new FixtureManagedBackupCatalog(new ManagedBackupListing(
            RetentionDays: 14,
            Backups:
            [
                new ManagedBackupSummary(
                    Guid.Parse("c7e2d932-f32d-4a22-8ca1-06a750176f9f"),
                    createdAt,
                    ArchiveBytes: 1024,
                    MediaBytes: 2048,
                    State: "completed"),
            ]));
        await using var host = await TestHost.StartAsync(catalog);
        using var request = TestHost.AuthenticatedGet();

        using var response = await host.Client.SendAsync(request);
        var listing = await response.Content.ReadFromJsonAsync<ManagedBackupListing>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(listing);
        Assert.Equal(14, listing.RetentionDays);
        var backup = Assert.Single(listing.Backups);
        Assert.Equal(Guid.Parse("c7e2d932-f32d-4a22-8ca1-06a750176f9f"), backup.Id);
        Assert.Equal(createdAt, backup.CreatedAtUtc);
        Assert.Equal(1024, backup.ArchiveBytes);
        Assert.Equal(2048, backup.MediaBytes);
        Assert.Equal("completed", backup.State);
        Assert.Equal(1, catalog.ListCalls);

        using var mutation = new HttpRequestMessage(HttpMethod.Post, ManagedBackupEndpoints.Route)
        {
            Content = JsonContent.Create(new { }),
        };
        mutation.Headers.Add("X-Test-Authenticated", "1");
        using var mutationResponse = await host.Client.SendAsync(mutation);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, mutationResponse.StatusCode);
        Assert.Equal(1, catalog.ListCalls);
    }

    private sealed class FixtureManagedBackupCatalog(ManagedBackupListing listing) : IManagedBackupCatalog
    {
        public int ListCalls { get; private set; }

        public Task<ManagedBackupListing> ListAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(listing);
        }
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TestHost(WebApplication app)
        {
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<TestHost> StartAsync(
            IManagedBackupCatalog catalog,
            string? authorizationPolicy = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddRouting();
            builder.Services.AddSingleton<IManagedBackupCatalog>(catalog);
            builder.Services
                .AddAuthentication("Test")
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            builder.Services.AddAuthorization(options =>
            {
                if (authorizationPolicy is not null)
                    options.AddPolicy(authorizationPolicy, policy => policy.RequireClaim("product_access"));
            });

            var app = builder.Build();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapManagedBackupEndpoints(authorizationPolicy);
            await app.StartAsync();
            return new TestHost(app);
        }

        public static HttpRequestMessage AuthenticatedGet()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, ManagedBackupEndpoints.Route);
            request.Headers.Add("X-Test-Authenticated", "1");
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-Authenticated"))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "customer-a")],
                Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
