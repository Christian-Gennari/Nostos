using System.Net;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Providers.Acquisition;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Migration;
using Nostos.Product.Composition;
using Nostos.Product.Services.Ai;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// The public seams a hosted adapter in another assembly needs to reuse the
/// product's migration engine: the prepared-import entry point, the verifier and
/// the streamed export entry point. The test assembly is a friend of
/// <c>Nostos.Product</c>, so "usable from outside" is proven by reflection over
/// the public surface plus an end-to-end call through the public interface only.
/// </summary>
public sealed class PortableHostedAdapterSeamTests
{
    [Fact]
    public void Prepared_import_verifier_and_export_seams_are_public()
    {
        typeof(IPortableImportPreparer).IsPublic.Should().BeTrue();
        typeof(IPreparedPortableImport).IsPublic.Should().BeTrue();
        typeof(PreparedPortableImportMetadata).IsPublic.Should().BeTrue();
        typeof(PortableArchiveProgress).IsPublic.Should().BeTrue();
        typeof(IPortableArchiveSource).IsPublic.Should().BeTrue();
        typeof(IPortableImportStaging).IsPublic.Should().BeTrue();

        var prepare = typeof(IPortableImportPreparer)
            .GetMethod(nameof(IPortableImportPreparer.PrepareImportAsync))!;
        prepare.IsPublic.Should().BeTrue();
        prepare.ReturnType.Should().Be(typeof(Task<IPreparedPortableImport>));
        prepare.GetParameters().Select(parameter => parameter.ParameterType).Should().Equal(
            typeof(IPortableArchiveSource),
            typeof(IPortableImportStaging),
            typeof(IProgress<PortableArchiveProgress>),
            typeof(CancellationToken));
        AssertSignatureIsPublic(prepare);

        typeof(IPortableLibraryVerifier).IsPublic.Should().BeTrue();
        typeof(PortablePreparedImportVerification).IsPublic.Should().BeTrue();
        typeof(PortableLibraryVerificationReport).IsPublic.Should().BeTrue();
        typeof(PortableLibraryVerificationFailure).IsPublic.Should().BeTrue();
        typeof(PortableLibraryVerifier).IsPublic.Should().BeTrue();
        typeof(PortableLibraryVerifier).GetConstructor(Type.EmptyTypes).Should().NotBeNull();
        foreach (var method in typeof(IPortableLibraryVerifier).GetMethods())
            AssertSignatureIsPublic(method);

        typeof(IPortableArchiveService).IsPublic.Should().BeTrue();
        typeof(IPortableArchiveSink).IsPublic.Should().BeTrue();
        typeof(PortableExportResult).IsPublic.Should().BeTrue();
        foreach (var method in typeof(IPortableArchiveService).GetMethods())
            AssertSignatureIsPublic(method);
    }

    [Fact]
    public async Task Prepared_import_seam_runs_end_to_end_through_the_public_interface()
    {
        await using var library = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(
            library.Db,
            library.Storage);
        using var archive = new MemoryStream();
        await library.Portability().ExportAsync(archive);
        var bytes = archive.ToArray();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNostosProduct(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var preparer = provider.GetRequiredService<IPortableImportPreparer>();

        var store = new InMemoryPortableImportStagingStore();
        await using var staging = new InMemoryPortableImportStaging(store);
        await using var source = new RangePortableArchiveSource(
            bytes.LongLength,
            (offset, buffer, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, bytes.Length - offset);
                bytes.AsMemory((int)offset, count).CopyTo(buffer);
                return ValueTask.FromResult(count);
            });

        var prepared = await preparer.PrepareImportAsync(source, staging);

        prepared.Metadata.IntegrityVerified.Should().BeTrue();
        prepared.Metadata.MediaFiles.Should().Be(5);

        var report = await new PortableLibraryVerifier()
            .VerifyPreparedImportAsync(staging, prepared);
        report.Passed.Should().BeTrue(
            string.Join("; ", report.Failures.Select(failure => failure.Code)));
    }

    [Fact]
    public void Product_composition_without_selfhosted_engine_uses_host_job_store()
    {
        var hostStore = new StubMigrationJobStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMigrationJobStore>(hostStore);
        services.AddNostosProduct(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMigrationJobStore>().Should().BeSameAs(hostStore);
        provider.GetRequiredService<IPortableImportPreparer>().Should().NotBeNull();
        provider.GetService<IPortableImportStaging>().Should().BeNull(
            "prepared-import staging is a host responsibility");
        provider.GetService<SelfHostedMigrationJobService>().Should().BeNull(
            "the SelfHosted migration engine is registered separately");
        provider.GetServices<IHostedService>().Select(service => service.GetType())
            .Should().NotContain(new[]
            {
                typeof(MigrationJobWorker),
                typeof(MigrationTransferCleanupWorker),
            });
    }

    [Fact]
    public async Task Host_without_selfhosted_migration_engine_starts_with_migration_transfer_opted_out()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(DeploymentDescriptor.For(DeploymentMode.SelfHosted));
        builder.Services.AddDbContextFactory<NostosDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        builder.Services.AddSingleton<IBookAssetStorage, ThrowingBookAssetStorage>();
        builder.Services.AddSingleton<IAiProviderSettingsService, ThrowingAiProviderSettingsService>();
        builder.Services.AddSingleton<IAiProviderConfigResolver>(sp =>
            sp.GetRequiredService<IAiProviderSettingsService>());
        builder.Services.AddSingleton<IAiAccessPolicy, AllowAllAiAccessPolicy>();
        builder.Services.AddSingleton<IAiUsageAccountingService>(NoOpAiUsageAccountingService.Instance);
        builder.Services.AddSingleton<ISTtProvider, ThrowingSttProvider>();
        builder.Services.AddSingleton<IAcquisitionJobManager, ThrowingAcquisitionJobManager>();

        var product = builder.Services.AddNostosProduct(builder.Configuration);

        await using var app = builder.Build();
        app.MapNostosProductEndpoints(
            product.Opds,
            new NostosProductEndpointPolicies(MapMigrationTransferEndpoints: false));

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var capabilities = await client.GetAsync(DeploymentCapabilitiesEndpoints.Route);
        capabilities.StatusCode.Should().Be(HttpStatusCode.OK);

        using var preflight = await client.PostAsync(
            "/api/portability/migration/preflight",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        preflight.StatusCode.Should().Be(HttpStatusCode.NotFound);

        app.Services.GetService<SelfHostedMigrationJobService>().Should().BeNull();
        app.Services.GetServices<IHostedService>().Select(service => service.GetType())
            .Should().NotContain(new[]
            {
                typeof(MigrationJobWorker),
                typeof(MigrationTransferCleanupWorker),
            });

        await app.StopAsync();
    }

    private static void AssertSignatureIsPublic(MethodInfo method)
    {
        var signatureTypes = method.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Append(method.ReturnType)
            .SelectMany(Flatten);

        foreach (var type in signatureTypes)
        {
            if (type.IsGenericParameter)
                continue;

            type.IsPublic.Should().BeTrue(
                $"{method.DeclaringType?.Name}.{method.Name} exposes non-public {type.FullName}");
        }
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;

        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var inner in Flatten(element))
                yield return inner;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var inner in Flatten(argument))
                    yield return inner;
            }
        }
    }

    private sealed class StubMigrationJobStore : IMigrationJobStore
    {
        public Task<MigrationJob?> GetAsync(Guid jobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MigrationJob>> GetJobsNeedingRecoveryAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MigrationIdempotencyResult<MigrationJob>> CreateAsync(
            MigrationDirection direction,
            string idempotencyKey,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MigrationJob> TransitionAsync(
            Guid jobId,
            MigrationJobState targetState,
            string leaseToken,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateProgressAsync(
            Guid jobId,
            MigrationProgress progress,
            string leaseToken,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string?> TryAcquireLeaseAsync(
            Guid jobId,
            TimeSpan leaseDuration,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> RenewLeaseAsync(
            Guid jobId,
            string leaseToken,
            TimeSpan leaseDuration,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ReleaseLeaseAsync(Guid jobId, string leaseToken, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task CancelAsync(Guid jobId, MigrationCancelRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MigrationJob> RetryAsync(
            Guid jobId,
            MigrationRetryRequest request,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingBookAssetStorage : IBookAssetStorage
    {
        public Task<string> SaveBookFileAsync(Guid bookId, Stream content, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> AdoptBookFileAsync(Guid bookId, string sourcePath, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredAssetInfo?> GetBookFileInfoAsync(Guid bookId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredAssetRead?> OpenBookFileAsync(Guid bookId, StorageByteRange? range = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteBookFileAsync(Guid bookId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteBookFilesAsync(Guid bookId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> SaveBookCoverAsync(Guid bookId, Stream content, string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredAssetInfo?> GetBookCoverInfoAsync(Guid bookId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredAssetRead?> OpenBookCoverAsync(Guid bookId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredAssetInfo?> GetBookCoverThumbnailInfoAsync(Guid bookId, int width, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<StoredAssetRead?> OpenBookCoverThumbnailAsync(Guid bookId, int width, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteCoverAsync(Guid bookId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingAiProviderSettingsService : IAiProviderSettingsService
    {
        public Task<EffectiveAiProviderConfig> GetEffectiveLlmAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<EffectiveAiProviderConfig> GetEffectiveSttAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<EffectiveAiProviderConfig> GetEffectiveEmbeddingAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AiProviderSettingsResponse> GetAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AiProviderSettingsResponse> UpdateAsync(
            AiProviderSettingsUpdateRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AiProviderModelsResponse> ListModelsAsync(
            AiProviderModelsRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AiProviderTestResult> TestAsync(
            AiProviderTestRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingSttProvider : ISTtProvider
    {
        public Task<SttResult> TranscribeAsync(
            Stream audio,
            string fileName,
            string? contentType,
            string? languageHint,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingAcquisitionJobManager : IAcquisitionJobManager
    {
        public AcquisitionJobStatus Start(AcquisitionRequest request) =>
            throw new NotSupportedException();

        public AcquisitionJobStatus? Get(string jobId) =>
            throw new NotSupportedException();

        public IReadOnlyList<AcquisitionJobStatus> List() =>
            throw new NotSupportedException();

        public IReadOnlyList<AcquisitionJobStatus> ListActive() =>
            throw new NotSupportedException();

        public bool Cancel(string jobId) =>
            throw new NotSupportedException();
    }
}
