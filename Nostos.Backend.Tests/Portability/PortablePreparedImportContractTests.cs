using System.Reflection;
using System.IO.Compression;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortablePreparedImportContractTests
{
    [Fact]
    public void Staging_identifiers_and_references_are_provider_neutral()
    {
        typeof(PortableStagingId).IsValueType.Should().BeTrue();
        typeof(PortableStagingId).GetProperty(nameof(PortableStagingId.Value))!
            .PropertyType.Should().Be(typeof(Guid));

        typeof(PortableStagedMediaReference).IsValueType.Should().BeTrue();
        typeof(PortableStagedMediaReference)
            .GetProperty(nameof(PortableStagedMediaReference.Value))!
            .PropertyType.Should().Be(typeof(string));

        var exposedPropertyNames = new[]
            {
                typeof(IPreparedPortableImport),
                typeof(PreparedPortableImportMetadata),
                typeof(PortablePreparedMedia),
                typeof(PortableStagedMediaReference),
            }
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(property => property.Name)
            .ToArray();

        exposedPropertyNames.Should().NotContain(name =>
            name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("objectkey", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("account", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("bucket", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Staging_contract_supports_incremental_write_verify_read_and_delete()
    {
        var methods = typeof(IPortableImportStaging)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(method => method.Name);

        methods[nameof(IPortableImportStaging.CreateAsync)].ReturnType
            .Should().Be(typeof(Task<PortableStagingId>));
        methods[nameof(IPortableImportStaging.OpenMediaWriteAsync)].ReturnType
            .Should().Be(typeof(Task<PortableStagingWrite>));
        methods[nameof(IPortableImportStaging.CompleteMediaAsync)].ReturnType
            .Should().Be(typeof(Task));
        methods[nameof(IPortableImportStaging.OpenMediaReadAsync)].ReturnType
            .Should().Be(typeof(Task<Stream>));
        methods[nameof(IPortableImportStaging.DeleteAsync)].ReturnType
            .Should().Be(typeof(Task));

        methods[nameof(IPortableImportStaging.OpenMediaWriteAsync)]
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().ContainInOrder(
                typeof(PortableStagingId),
                typeof(PortableArchiveMediaEntry),
                typeof(CancellationToken));
        methods[nameof(IPortableImportStaging.CompleteMediaAsync)]
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().ContainInOrder(
                typeof(PortableStagedMediaReference),
                typeof(long),
                typeof(string),
                typeof(CancellationToken));

        typeof(IPortableImportStaging).GetInterfaces()
            .Should().Contain(typeof(IAsyncDisposable));
    }

    [Fact]
    public void Prepared_import_metadata_uses_portable_counts_and_carries_verified_media_hashes()
    {
        typeof(PreparedPortableImportMetadata)
            .GetProperty(nameof(PreparedPortableImportMetadata.Counts))!
            .PropertyType.Should().Be(typeof(PortableArchiveCounts));
        typeof(PreparedPortableImportMetadata)
            .GetProperty(nameof(PreparedPortableImportMetadata.IntegrityVerified))!
            .PropertyType.Should().Be(typeof(bool));

        typeof(PortablePreparedMedia)
            .GetProperty(nameof(PortablePreparedMedia.Descriptor))!
            .PropertyType.Should().Be(typeof(PortableArchiveMediaEntry));
        typeof(PortableArchiveMediaEntry)
            .GetProperty(nameof(PortableArchiveMediaEntry.Sha256))!
            .PropertyType.Should().Be(typeof(string));
        typeof(PortablePreparedMedia)
            .GetProperty(nameof(PortablePreparedMedia.Reference))!
            .PropertyType.Should().Be(typeof(PortableStagedMediaReference));

        typeof(PortablePreparedImport).GetInterfaces()
            .Should().Contain(typeof(IPreparedPortableImport));
    }

    [Fact]
    public void Archive_progress_is_separate_from_migration_job_progress()
    {
        Enum.GetValues<PortableArchiveProgressPhase>().Should().Equal(
            PortableArchiveProgressPhase.Snapshotting,
            PortableArchiveProgressPhase.IndexingMedia,
            PortableArchiveProgressPhase.WritingArchive,
            PortableArchiveProgressPhase.InspectingArchive,
            PortableArchiveProgressPhase.ValidatingData,
            PortableArchiveProgressPhase.StagingMedia,
            PortableArchiveProgressPhase.Prepared);

        typeof(PortableArchiveProgress).GetProperties()
            .Select(property => property.Name)
            .Should().ContainInOrder(
                nameof(PortableArchiveProgress.Phase),
                nameof(PortableArchiveProgress.BytesProcessed),
                nameof(PortableArchiveProgress.TotalBytes),
                nameof(PortableArchiveProgress.ItemsProcessed),
                nameof(PortableArchiveProgress.TotalItems),
                nameof(PortableArchiveProgress.Message));

        typeof(PortableArchiveProgressPhase).Should().NotBe(typeof(MigrationProgressPhase));
    }

    [Fact]
    public void Existing_stream_archive_service_signatures_remain_unchanged()
    {
        typeof(IPortableArchiveService)
            .GetMethod(
                nameof(IPortableArchiveService.ExportAsync),
                new[] { typeof(Stream), typeof(CancellationToken) })!
            .ReturnType.Should().Be(typeof(Task<PortableExportResult>));

        typeof(IPortableArchiveService)
            .GetMethod(
                nameof(IPortableArchiveService.ImportAsync),
                new[] { typeof(Stream), typeof(CancellationToken) })!
            .ReturnType.Should().Be(typeof(Task<PortableImportResult>));
    }

    [Fact]
    public void Prepared_import_contract_does_not_expose_zip_types()
    {
        var contractTypes = new[]
        {
            typeof(IPortableImportStaging),
            typeof(PortableStagingWrite),
            typeof(IPreparedPortableImport),
            typeof(PreparedPortableImportMetadata),
            typeof(PortablePreparedMedia),
            typeof(PortablePreparedImport),
            typeof(PortableArchiveProgress),
        };

        var exposedTypes = contractTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)))
            .Concat(contractTypes.SelectMany(type => type.GetProperties()
                .Select(property => property.PropertyType)));

        exposedTypes.Should().NotContain(type =>
            type == typeof(ZipArchive) || type.Namespace == typeof(ZipArchive).Namespace);
    }
}
