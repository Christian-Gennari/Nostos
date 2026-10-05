using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Activation;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Real in-process SelfHosted host (full <c>Program</c> pipeline) over the same
/// files an activation test bed just switched, so the recovery endpoints serve
/// a genuine retained copy created by the real activation coordinator.
/// </summary>
internal sealed class RecoveryHttpHarness : IAsyncDisposable
{
    internal ActivationCoordinatorTestBed Bed { get; }

    private WebApplicationFactory<Program>? _factory;

    internal HttpClient Client { get; private set; } = null!;

    private RecoveryHttpHarness(ActivationCoordinatorTestBed bed)
    {
        Bed = bed;
    }

    internal static async Task<RecoveryHttpHarness> StartAsync()
    {
        var bed = await ActivationCoordinatorTestBed.CreateAsync(populated: true);
        (await bed.ActivateAsync(confirm: true)).Outcome.Should().Be(SelfHostedActivationOutcome.Completed);
        var harness = new RecoveryHttpHarness(bed);
        harness._factory = new HostFactory(harness);
        harness._factory.UseKestrel(0);
        harness.Client = harness._factory.CreateClient();
        return harness;
    }

    internal T GetService<T>() where T : notnull => _factory!.Services.GetRequiredService<T>();

    internal async Task<string> RevisionTokenAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILibraryDestinationRevisionProvider>()
            .GetCurrentAsync(default);
    }

    internal async Task AwaitRestoreAsync(Guid recoveryId)
    {
        var dispatcher = GetService<SelfHostedActivationDispatcher>();
        await dispatcher.AwaitRestoreFinishedAsync(recoveryId, default);
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await Client.SendAsync(request);
        return (response.StatusCode, await MigrationHttpHarness.ReadJsonAsync(response));
    }

    internal async Task<(HttpStatusCode Status, JsonDocument Body)> PostJsonAsync<T>(
        string path,
        T payload)
    {
        using var content = JsonContent.Create(payload, options: MigrationHttpHarness.Json);
        return await SendAsync(HttpMethod.Post, path, content);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        await Bed.DisposeAsync();
    }

    private sealed class HostFactory(RecoveryHttpHarness harness) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseContentRoot(harness.Bed.Root);
            builder.UseSetting("Persistence:DatabasePath", harness.Bed.Paths.LiveDatabase);
            builder.UseSetting("Storage:BooksRoot", harness.Bed.Paths.LiveMedia);
            builder.UseSetting("Storage:TransferPath", harness.Bed.TransferRoot);
            builder.UseSetting("Storage:BackupsRoot", Path.Combine(harness.Bed.Root, "backups"));
            builder.UseSetting("Storage:DiskSafetyMarginBytes", "0");
            builder.UseSetting("Storage:DiskSafetyMarginPercent", "0");
            builder.UseSetting("Mcp:Enabled", "false");
        }
    }
}
