using System.Reflection;
using System.IO.Compression;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class PortablePreparedImportContractTests
{
    private static readonly DateTime FixedUtc =
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

        var exposedPropertyNames = new[]
            {
                typeof(IPreparedPortableImport),
                typeof(PreparedPortableImportMetadata),
                typeof(PortablePreparedMedia),
                typeof(PortableStagedMediaReference),
                typeof(PortableStagingPayloadWrite),
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
                typeof(PortableStagingWrite),
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
        methods[nameof(IPortableImportStaging.CompleteDataAsync)]
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().ContainInOrder(
                typeof(PortableStagingId),
                typeof(PortableStagingPayloadWrite),
                typeof(CancellationToken));
        methods[nameof(IPortableImportStaging.OpenDataReadAsync)]
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().ContainInOrder(
                typeof(PortableStagingId),
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
                typeof(PortableStagingPayloadWrite),
                typeof(CancellationToken));
        methods[nameof(IPortableImportStaging.OpenManifestReadAsync)]
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().ContainInOrder(
                typeof(PortableStagingId),
                typeof(CancellationToken));
        methods[nameof(IPortableImportStaging.CommitPreparedImportAsync)]
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().ContainInOrder(
                typeof(PortableStagingId),
                typeof(PreparedPortableImportMetadata),
                typeof(CancellationToken));

        typeof(IPortableImportStaging).GetInterfaces()
            .Should().Contain(typeof(IAsyncDisposable));

        typeof(PortableStagingPayloadWrite).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().ContainInOrder(nameof(PortableStagingPayloadWrite.Stream));
    }

    [Fact]
    public void Staging_exception_exposes_stable_typed_codes()
    {
        var codes = new[]
        {
            PortableStagingException.NotFoundCode,
            PortableStagingException.InvalidReferenceCode,
            PortableStagingException.IntegrityMismatchCode,
            PortableStagingException.ConflictCode,
            PortableStagingException.LimitExceededCode,
            PortableStagingException.AlreadyCommittedCode,
        };

        codes.Should().OnlyHaveUniqueItems();
        codes.Should().OnlyContain(code => code.StartsWith("staging_", StringComparison.Ordinal));

        new PortableStagingException(PortableStagingException.NotFoundCode, "x")
            .IsNotFound.Should().BeTrue();
        new PortableStagingException(PortableStagingException.ConflictCode, "x")
            .IsNotFound.Should().BeFalse();
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
    public void Counts_function_populates_every_migration_count_property()
    {
        var data = FullyPopulatedLibraryData();

        var counts = PortableLibraryCounts.ComputeCounts(data, mediaEntries: 12);

        counts.Works.Should().Be(1);
        counts.Books.Should().Be(2);
        counts.Collections.Should().Be(3);
        counts.CollectionMemberships.Should().Be(4);
        counts.Notes.Should().Be(5);
        counts.Topics.Should().Be(6);
        counts.NoteTopics.Should().Be(7);
        counts.Writings.Should().Be(8);
        counts.WritingNotes.Should().Be(9);
        counts.Acquisitions.Should().Be(10);
        counts.AssistantSettings.Should().Be(1);
        counts.NoteImportBookLinks.Should().Be(11);
        counts.MediaEntries.Should().Be(12);
        counts.TotalRows.Should().Be(67);

        foreach (var property in typeof(MigrationArchiveCounts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var value = (long)property.GetValue(counts)!;
            value.Should().BeGreaterThan(
                0,
                $"{property.Name} must be populated from the validated payload or media count");
        }
    }

    [Fact]
    public void Counts_function_counts_every_portable_library_property()
    {
        var propertyNames = typeof(PortableLibraryData)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        propertyNames.Should().Equal(
            nameof(PortableLibraryData.AssistantSettings),
            nameof(PortableLibraryData.BookAcquisitions),
            nameof(PortableLibraryData.BookCollections),
            nameof(PortableLibraryData.Books),
            nameof(PortableLibraryData.Collections),
            nameof(PortableLibraryData.NoteImportBookLinks),
            nameof(PortableLibraryData.NoteTopics),
            nameof(PortableLibraryData.Notes),
            nameof(PortableLibraryData.Topics),
            nameof(PortableLibraryData.Version),
            nameof(PortableLibraryData.Works),
            nameof(PortableLibraryData.WritingNotes),
            nameof(PortableLibraryData.Writings));
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

    private static PortableLibraryData FullyPopulatedLibraryData() =>
        new(
            Version: 3,
            Works: Enumerable.Range(0, 1)
                .Select(_ => new PortableWork(Guid.NewGuid(), "Work", null, FixedUtc))
                .ToList(),
            Books: Enumerable.Range(0, 2).Select(_ => Book()).ToList(),
            Collections: Enumerable.Range(0, 3)
                .Select(_ => new PortableCollection(Guid.NewGuid(), "Collection", null))
                .ToList(),
            BookCollections: Enumerable.Range(0, 4)
                .Select(_ => new PortableBookCollection(Guid.NewGuid(), Guid.NewGuid(), FixedUtc))
                .ToList(),
            Notes: Enumerable.Range(0, 5).Select(_ => Note()).ToList(),
            Topics: Enumerable.Range(0, 6)
                .Select(_ => new PortableTopic(Guid.NewGuid(), "topic"))
                .ToList(),
            NoteTopics: Enumerable.Range(0, 7)
                .Select(_ => new PortableNoteTopic(Guid.NewGuid(), Guid.NewGuid()))
                .ToList(),
            Writings: Enumerable.Range(0, 8)
                .Select(_ => new PortableWriting(
                    Guid.NewGuid(), "Writing", "essay", null, null, FixedUtc, FixedUtc))
                .ToList(),
            BookAcquisitions: Enumerable.Range(0, 10)
                .Select(_ => new PortableBookAcquisition(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "provider",
                    "Provider",
                    "external-id",
                    "asset-id",
                    null,
                    null,
                    null,
                    null,
                    FixedUtc))
                .ToList(),
            AssistantSettings: new PortableAssistantSettings("manual", FixedUtc),
            WritingNotes: Enumerable.Range(0, 9)
                .Select(_ => new PortableWritingNote(Guid.NewGuid(), Guid.NewGuid(), FixedUtc))
                .ToList(),
            NoteImportBookLinks: Enumerable.Range(0, 11)
                .Select(_ => new PortableNoteImportBookLink(
                    Guid.NewGuid(), "kobo", "source-key", Guid.NewGuid(), FixedUtc))
                .ToList());

    private static PortableBook Book() =>
        new(
            Id: Guid.NewGuid(),
            WorkId: Guid.NewGuid(),
            Type: "ebook",
            Status: "reading",
            StatusMessage: null,
            Title: "Book",
            Author: null,
            Metadata: new PortableBookMetadata(
                null, null, null, null, null, null, null, null, null, null, null, null),
            Progress: new PortableReadingProgress(null, 0, 0, false, null, null, null),
            CreatedAt: FixedUtc,
            Isbn: null,
            PageCount: null,
            Asin: null,
            Duration: null,
            Narrator: null,
            ChaptersJson: null,
            HasBookFile: false,
            HasCover: false);

    private static PortableNote Note() =>
        new(
            Id: Guid.NewGuid(),
            Content: "note",
            CfiRange: null,
            SelectedText: null,
            CreatedAt: FixedUtc,
            BookId: Guid.NewGuid(),
            RawContent: null,
            CaptureSource: "manual",
            ProcessingMode: "raw",
            SourceAnchorKind: "none",
            SourceAnchorValue: null,
            AnchorVerified: false);
}
