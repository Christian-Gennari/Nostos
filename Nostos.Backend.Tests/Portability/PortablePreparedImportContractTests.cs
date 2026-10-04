using System.Reflection;
using System.IO.Compression;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortablePreparedImportContractTests
{
    [Fact]
    public void Staging_identifier_and_reference_property_names_stay_provider_neutral()
    {
        typeof(PortableStagingId).IsValueType.Should().BeTrue();
        typeof(PortableStagingId).GetProperty(nameof(PortableStagingId.Value))!
            .PropertyType.Should().Be(typeof(Guid));

        typeof(PortableStagedMediaReference).IsValueType.Should().BeTrue();
        typeof(PortableStagedMediaReference)
            .GetProperty(nameof(PortableStagedMediaReference.Value))!
            .PropertyType.Should().Be(typeof(string));

        typeof(PortableStagedPayloadReference).IsValueType.Should().BeTrue();
        typeof(PortableStagedPayloadReference)
            .GetProperty(nameof(PortableStagedPayloadReference.Value))!
            .PropertyType.Should().Be(typeof(string));

        var exposedPropertyNames = new[]
            {
                typeof(IPreparedPortableImport),
                typeof(PreparedPortableImportMetadata),
                typeof(PortablePreparedMedia),
                typeof(PortableStagedMediaReference),
                typeof(PortableStagedPayloadReference),
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
    public void Staging_contract_exposes_expected_staging_api_shape()
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
        methods[nameof(IPortableImportStaging.ListMediaAsync)].ReturnType
            .Should().Be(typeof(Task<IReadOnlyList<PortablePreparedMedia>>));
        methods[nameof(IPortableImportStaging.OpenDataWriteAsync)].ReturnType
            .Should().Be(typeof(Task<PortableStagingPayloadWrite>));
        methods[nameof(IPortableImportStaging.CompleteDataAsync)].ReturnType
            .Should().Be(typeof(Task));
        methods[nameof(IPortableImportStaging.OpenDataReadAsync)].ReturnType
            .Should().Be(typeof(Task<Stream>));
        methods[nameof(IPortableImportStaging.OpenManifestWriteAsync)].ReturnType
            .Should().Be(typeof(Task<PortableStagingPayloadWrite>));
        methods[nameof(IPortableImportStaging.CompleteManifestAsync)].ReturnType
            .Should().Be(typeof(Task));
        methods[nameof(IPortableImportStaging.OpenManifestReadAsync)].ReturnType
            .Should().Be(typeof(Task<Stream>));
        methods[nameof(IPortableImportStaging.CommitPreparedImportAsync)].ReturnType
            .Should().Be(typeof(Task));
        methods[nameof(IPortableImportStaging.RebuildPreparedImportAsync)].ReturnType
            .Should().Be(typeof(Task<IPreparedPortableImport>));
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
                typeof(PortableStagingId),
                typeof(PortableStagedMediaReference),
                typeof(long),
                typeof(string),
                typeof(CancellationToken));
        methods[nameof(IPortableImportStaging.OpenMediaReadAsync)]
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().ContainInOrder(
                typeof(PortableStagingId),
                typeof(PortableStagedMediaReference),
                typeof(CancellationToken));
        methods[nameof(IPortableImportStaging.OpenDataWriteAsync)]
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().ContainInOrder(
                typeof(PortableStagingId),
                typeof(PortableArchivePayload),
                typeof(CancellationToken));
        methods[nameof(IPortableImportStaging.OpenManifestWriteAsync)]
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().ContainInOrder(
                typeof(PortableStagingId),
                typeof(PortableArchivePayload),
                typeof(CancellationToken));
        methods[nameof(IPortableImportStaging.CompleteManifestAsync)]
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().ContainInOrder(
                typeof(PortableStagingId),
                typeof(PortableStagedPayloadReference),
                typeof(long),
                typeof(string),
                typeof(CancellationToken));
        methods[nameof(IPortableImportStaging.CommitPreparedImportAsync)]
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().ContainInOrder(
                typeof(PortableStagingId),
                typeof(PreparedPortableImportMetadata),
                typeof(CancellationToken));

        typeof(IPortableImportStaging).GetInterfaces()
            .Should().Contain(typeof(IAsyncDisposable));
    }

    [Fact]
    public void Prepared_import_metadata_uses_migration_counts_and_binds_verified_data_identity()
    {
        typeof(PreparedPortableImportMetadata)
            .GetProperty(nameof(PreparedPortableImportMetadata.Counts))!
            .PropertyType.Should().Be(typeof(MigrationArchiveCounts));
        typeof(PreparedPortableImportMetadata)
            .GetProperty(nameof(PreparedPortableImportMetadata.DataBytes))!
            .PropertyType.Should().Be(typeof(long));
        typeof(PreparedPortableImportMetadata)
            .GetProperty(nameof(PreparedPortableImportMetadata.DataSha256))!
            .PropertyType.Should().Be(typeof(string));
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

        typeof(PortablePreparedImport).GetProperty(nameof(PortablePreparedImport.Counts))!
            .PropertyType.Should().Be(typeof(MigrationArchiveCounts));
        typeof(PortablePreparedImport).GetInterfaces()
            .Should().Contain(typeof(IPreparedPortableImport));
    }

    [Fact]
    public void Archive_counts_conversion_covers_every_property_of_both_count_types()
    {
        var archivePropertyNames = typeof(PortableArchiveCounts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        archivePropertyNames.Should().Equal(
            nameof(PortableArchiveCounts.BookAcquisitions),
            nameof(PortableArchiveCounts.BookCollections),
            nameof(PortableArchiveCounts.Books),
            nameof(PortableArchiveCounts.Collections),
            nameof(PortableArchiveCounts.NoteTopics),
            nameof(PortableArchiveCounts.Notes),
            nameof(PortableArchiveCounts.Topics),
            nameof(PortableArchiveCounts.Works),
            nameof(PortableArchiveCounts.Writings));

        var migrationPropertyNames = typeof(MigrationArchiveCounts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        migrationPropertyNames.Should().Equal(
            nameof(MigrationArchiveCounts.Acquisitions),
            nameof(MigrationArchiveCounts.AssistantSettings),
            nameof(MigrationArchiveCounts.Books),
            nameof(MigrationArchiveCounts.CollectionMemberships),
            nameof(MigrationArchiveCounts.Collections),
            nameof(MigrationArchiveCounts.MediaEntries),
            nameof(MigrationArchiveCounts.NoteImportBookLinks),
            nameof(MigrationArchiveCounts.NoteTopics),
            nameof(MigrationArchiveCounts.Notes),
            nameof(MigrationArchiveCounts.Topics),
            nameof(MigrationArchiveCounts.TotalRows),
            nameof(MigrationArchiveCounts.Works),
            nameof(MigrationArchiveCounts.WritingNotes),
            nameof(MigrationArchiveCounts.Writings));

        var converted = new PortableArchiveCounts(
            Works: 1,
            Books: 2,
            Collections: 3,
            BookCollections: 4,
            Notes: 5,
            Topics: 6,
            NoteTopics: 7,
            Writings: 8,
            BookAcquisitions: 9)
            .ToMigrationArchiveCounts(mediaEntries: 10);

        converted.Works.Should().Be(1);
        converted.Books.Should().Be(2);
        converted.Collections.Should().Be(3);
        converted.CollectionMemberships.Should().Be(4);
        converted.Notes.Should().Be(5);
        converted.Topics.Should().Be(6);
        converted.NoteTopics.Should().Be(7);
        converted.Writings.Should().Be(8);
        converted.Acquisitions.Should().Be(9);
        converted.MediaEntries.Should().Be(10);
        converted.WritingNotes.Should().Be(0);
        converted.AssistantSettings.Should().Be(0);
        converted.NoteImportBookLinks.Should().Be(0);
        converted.TotalRows.Should().Be(45);
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
            typeof(PortableStagingPayloadWrite),
            typeof(PortableStagingId),
            typeof(PortableStagedMediaReference),
            typeof(PortableStagedPayloadReference),
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
