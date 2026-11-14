using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Services;
using Nostos.Backend.Services.Portability;
using Nostos.Backend.Services.Portability.Verification;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// The compatibility import no longer carries its own relational/media check: it
/// runs the same comparison tables as activation. These tests exercise that one
/// path directly, including the failure contract it keeps for the import
/// transaction (<c>integrity_failed</c>).
/// </summary>
[Collection(PortableLibraryVerificationCollection.Name)]
public sealed class PortableImportIntegrityVerifierTests
{
    [Fact]
    public async Task Just_imported_library_passes_the_shared_import_verification()
    {
        await using var scenario = await ImportScenario.CreateAsync();

        await scenario.VerifyAsync();
    }

    [Fact]
    public async Task Relational_row_the_archive_declares_is_rejected()
    {
        await using var scenario = await ImportScenario.CreateAsync();
        var note = await scenario.Library.Db.Notes.FirstAsync();
        scenario.Library.Db.Notes.Remove(note);
        await scenario.Library.Db.SaveChangesAsync();
        scenario.Library.Db.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<PortableArchiveException>(
            () => scenario.VerifyAsync());

        exception.Code.Should().Be(PortableImportIntegrityVerifier.FailureCode);
        exception.Message.Should().Contain(
            PortableLibraryVerificationErrorCodes.MissingEntity);
    }

    [Fact]
    public async Task Stored_media_is_hashed_by_the_shared_import_verification()
    {
        await using var scenario = await ImportScenario.CreateAsync();
        var bookFile = scenario.Manifest.Media.First(
            item => item.Kind == PortableArchiveFormat.BookMediaKind);
        File.Delete(Path.Combine(
            scenario.Library.Storage.StorageRoot,
            bookFile.BookId.ToString(),
            bookFile.FileName));

        var exception = await Assert.ThrowsAsync<PortableArchiveException>(
            () => scenario.VerifyAsync());

        exception.Code.Should().Be(PortableImportIntegrityVerifier.FailureCode);
        exception.Message.Should().Contain(
            PortableLibraryVerificationErrorCodes.MediaMissing);
    }

    [Fact]
    public async Task Archive_count_that_disagrees_with_its_payload_is_rejected()
    {
        await using var scenario = await ImportScenario.CreateAsync();
        var fabricated = scenario.Manifest with
        {
            Counts = scenario.Manifest.Counts with
            {
                Works = scenario.Manifest.Counts.Works + 1,
            },
        };

        var exception = await Assert.ThrowsAsync<PortableArchiveException>(
            () => scenario.VerifyAsync(fabricated.Counts));

        exception.Code.Should().Be(PortableImportIntegrityVerifier.FailureCode);
        exception.Message.Should().Contain(
            PortableLibraryVerificationErrorCodes.CountMismatch);
    }

    private sealed class ImportScenario(
        LocalPortableTestLibrary source,
        LocalPortableTestLibrary library,
        PortableLibraryData data,
        PortableArchiveManifest manifest) : IAsyncDisposable
    {
        public LocalPortableTestLibrary Library { get; } = library;

        public PortableLibraryData Data { get; } = data;

        public PortableArchiveManifest Manifest { get; } = manifest;

        public static async Task<ImportScenario> CreateAsync()
        {
            var source = await LocalPortableTestLibrary.CreateAsync();
            try
            {
                await PortableArchiveTestSupport.PopulateRepresentativeAsync(
                    source.Db,
                    source.Storage);
                var archive = await SelfHostedActivationTestSupport.ExportArchiveAsync(source);

                var store = new InMemoryPortableImportStagingStore();
                await using var staging = new InMemoryPortableImportStaging(store);
                var prepared = await SelfHostedActivationTestSupport.PrepareStagedAsync(
                    archive,
                    staging);

                var manifest = await ReadManifestAsync(staging, prepared);
                var data = await PortableLibraryDatabaseMaterializer.ReadRelationalDataAsync(
                    staging,
                    prepared.StagingId,
                    prepared.Media,
                    default);

                var library = await LocalPortableTestLibrary.CreateAsync();
                using var input = new MemoryStream(archive, writable: false);
                await library.Portability().ImportAsync(input);

                return new ImportScenario(source, library, data, manifest);
            }
            catch
            {
                await source.DisposeAsync();
                throw;
            }
        }

        public Task VerifyAsync() => VerifyAsync(Manifest.Counts);

        public Task VerifyAsync(PortableArchiveCounts counts) =>
            PortableImportIntegrityVerifier.VerifyAppliedAsync(
                Library.Db,
                Library.Storage,
                Library.Storage,
                Data,
                counts,
                Manifest.Media,
                default);

        private static async Task<PortableArchiveManifest> ReadManifestAsync(
            InMemoryPortableImportStaging staging,
            PortablePreparedImport prepared)
        {
            await using var stream = await staging.OpenManifestReadAsync(prepared.StagingId);
            return (await JsonSerializer.DeserializeAsync<PortableArchiveManifest>(
                stream,
                PortableArchiveJson.Options))!;
        }

        public async ValueTask DisposeAsync()
        {
            await Library.DisposeAsync();
            await source.DisposeAsync();
        }
    }
}
