using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

[Collection(PortableLibraryVerificationCollection.Name)]
public sealed class PortableLibraryVerifierPreparedImportTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly PortableLibraryVerifier _verifier = new();

    [Fact]
    public async Task Prepared_import_passes_full_verification_and_rehashes_media()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        verification.Passed.Should().BeTrue(
            string.Join("; ", verification.Failures.Select(failure => $"{failure.Code}:{failure.Field}")));
        verification.Failures.Should().BeEmpty();
        verification.Metadata.Should().Be(prepared.Metadata);
        verification.Media.Should().BeEquivalentTo(prepared.Media.Select(item => item.Descriptor));
        verification.Counts.MediaEntries.Should().Be(prepared.Media.Count);
    }

    [Fact]
    public async Task Same_length_staged_data_corruption_is_detected()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var state = staging.Store.Areas[prepared.StagingId.Value].Data!.State;
        var bytes = state.Buffer.ToArray();
        bytes[0] ^= 0xff;
        state.Buffer.SetLength(0);
        state.Buffer.Write(bytes);

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        verification.Passed.Should().BeFalse();
        verification.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.DataHashMismatch);
    }

    [Fact]
    public async Task Same_length_staged_media_corruption_is_detected()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        foreach (var item in staging.Store.Areas[prepared.StagingId.Value].MediaByIdentity.Values)
        {
            var bytes = item.State.Buffer.ToArray();
            bytes[0] ^= 0xff;
            item.State.Buffer.SetLength(0);
            item.State.Buffer.Write(bytes);
        }

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        verification.Passed.Should().BeFalse();
        verification.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MediaHashMismatch);
    }

    [Fact]
    public async Task Shortened_staged_media_length_is_detected()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        foreach (var item in staging.Store.Areas[prepared.StagingId.Value].MediaByIdentity.Values)
        {
            var bytes = item.State.Buffer.ToArray();
            item.State.Buffer.SetLength(0);
            item.State.Buffer.Write(bytes[..^1]);
        }

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        verification.Passed.Should().BeFalse();
        verification.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MediaLengthMismatch);
    }

    [Fact]
    public async Task Prepared_descriptor_count_disagreement_is_detected()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var fabricated = prepared with
        {
            Metadata = prepared.Metadata with
            {
                Counts = prepared.Metadata.Counts with { Books = prepared.Metadata.Counts.Books + 1 },
            },
        };

        var verification = await _verifier.VerifyPreparedImportAsync(staging, fabricated);

        verification.Passed.Should().BeFalse();
        verification.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.CountMismatch
            && failure.Field == nameof(MigrationArchiveCounts.Books));
    }

    [Fact]
    public async Task Manifest_count_disagreement_is_detected()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var manifest = staging.Store.Areas[prepared.StagingId.Value].Manifest!.State;
        var node = JsonNode.Parse(Encoding.UTF8.GetString(manifest.Buffer.ToArray()))!.AsObject();
        node["counts"]!["works"] = 999;
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        manifest.Buffer.SetLength(0);
        manifest.Buffer.Write(bytes);

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        verification.Passed.Should().BeFalse();
        verification.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.CountMismatch
            && failure.Entity == "manifest");
    }

    [Fact]
    public async Task Missing_staged_media_inventory_entry_is_detected()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var area = staging.Store.Areas[prepared.StagingId.Value];
        var key = area.MediaByIdentity
            .Single(pair => pair.Value.Reference == prepared.Media[0].Reference.Value)
            .Key;
        area.MediaByIdentity.Remove(key);
        area.MediaByReference.Remove(prepared.Media[0].Reference.Value);

        var verification = await _verifier.VerifyPreparedImportAsync(staging, prepared);

        verification.Passed.Should().BeFalse();
        verification.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.InventoryMismatch);
        verification.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.MediaMissing);
    }

    [Fact]
    public async Task Import_not_marked_integrity_verified_fails_closed()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var fabricated = prepared with
        {
            Metadata = prepared.Metadata with { IntegrityVerified = false },
        };

        var verification = await _verifier.VerifyPreparedImportAsync(staging, fabricated);

        verification.Passed.Should().BeFalse();
        verification.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.ImportNotIntegrityVerified);
    }

    [Fact]
    public async Task Empty_staging_identifier_fails_closed()
    {
        var (staging, prepared) = await PrepareAsync();
        await using var _ = staging;
        var fabricated = prepared with
        {
            Metadata = prepared.Metadata with { StagingId = default },
        };

        var verification = await _verifier.VerifyPreparedImportAsync(staging, fabricated);

        verification.Passed.Should().BeFalse();
        verification.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.StagingUnavailable);
    }

    [Fact]
    public async Task Relational_validation_is_reused_for_a_broken_note_book_relationship()
    {
        var (sourceStaging, prepared) = await PrepareAsync();
        await using var _ = sourceStaging;
        var sourceData = await ReadDataAsync(sourceStaging, prepared);
        var broken = sourceData with
        {
            Notes = [sourceData.Notes[0] with { BookId = Guid.NewGuid() }],
        };
        var (fabricatedStaging, fabricatedPrepared) = await BuildConsistentStagingAsync(
            sourceStaging,
            prepared,
            broken);

        var verification = await _verifier.VerifyPreparedImportAsync(
            fabricatedStaging,
            fabricatedPrepared);
        await using var __ = fabricatedStaging;

        verification.Passed.Should().BeFalse();
        verification.Failures.Should().Contain(failure =>
            failure.Code == PortableLibraryVerificationErrorCodes.RelationalInvalid);
    }

    private static async Task<(InMemoryPortableImportStaging Staging, PortablePreparedImport Prepared)> PrepareAsync()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();
        await PortableArchiveTestSupport.PopulateRepresentativeAsync(source.Db, source.Storage);
        var archive = await SelfHostedActivationTestSupport.ExportArchiveAsync(source);
        var store = new InMemoryPortableImportStagingStore();
        var staging = new InMemoryPortableImportStaging(store);
        var prepared = await SelfHostedActivationTestSupport.PrepareStagedAsync(archive, staging);
        return (staging, prepared);
    }

    private static async Task<PortableLibraryData> ReadDataAsync(
        InMemoryPortableImportStaging staging,
        PortablePreparedImport prepared)
    {
        await using var stream = await staging.OpenDataReadAsync(prepared.StagingId);
        return (await JsonSerializer.DeserializeAsync<PortableLibraryData>(stream, JsonOptions))!;
    }

    private static async Task<(
        InMemoryPortableImportStaging Staging,
        PortablePreparedImport Prepared)> BuildConsistentStagingAsync(
        InMemoryPortableImportStaging source,
        PortablePreparedImport prepared,
        PortableLibraryData data)
    {
        var store = new InMemoryPortableImportStagingStore();
        var staging = new InMemoryPortableImportStaging(store);
        var stagingId = await staging.CreateAsync();

        var dataBytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        var dataWrite = await staging.OpenDataWriteAsync(
            stagingId,
            new PortableArchivePayload(
                PortableArchiveFormat.DataPath,
                dataBytes.LongLength,
                SelfHostedActivationTestSupport.Sha256Hex(dataBytes)));
        await dataWrite.Stream.WriteAsync(dataBytes);
        await staging.CompleteDataAsync(stagingId, dataWrite);
        await dataWrite.DisposeAsync();

        await using var manifestStream = await source.OpenManifestReadAsync(prepared.StagingId);
        using var manifestBuffer = new MemoryStream();
        await manifestStream.CopyToAsync(manifestBuffer);
        var originalManifest = JsonSerializer.Deserialize<PortableArchiveManifest>(
            manifestBuffer.ToArray(),
            JsonOptions)!;
        var manifest = originalManifest with
        {
            Data = new PortableArchivePayload(
                PortableArchiveFormat.DataPath,
                dataBytes.LongLength,
                SelfHostedActivationTestSupport.Sha256Hex(dataBytes)),
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        var manifestWrite = await staging.OpenManifestWriteAsync(
            stagingId,
            new PortableArchivePayload(
                PortableArchiveFormat.ManifestPath,
                manifestBytes.LongLength,
                SelfHostedActivationTestSupport.Sha256Hex(manifestBytes)));
        await manifestWrite.Stream.WriteAsync(manifestBytes);
        await staging.CompleteManifestAsync(stagingId, manifestWrite);
        await manifestWrite.DisposeAsync();

        var media = new List<PortablePreparedMedia>();
        foreach (var item in prepared.Media)
        {
            await using var content = await source.OpenMediaReadAsync(
                prepared.StagingId,
                item.Reference);
            var write = await staging.OpenMediaWriteAsync(stagingId, item.Descriptor);
            await content.CopyToAsync(write.Stream);
            await staging.CompleteMediaAsync(stagingId, write);
            await write.DisposeAsync();
        }

        media.AddRange(await staging.ListMediaAsync(stagingId));
        var metadata = new PreparedPortableImportMetadata(
            stagingId,
            prepared.Metadata.FormatVersion,
            data.Version,
            dataBytes.LongLength,
            SelfHostedActivationTestSupport.Sha256Hex(dataBytes),
            PortableLibraryCounts.ComputeCounts(data, media.Count),
            media.Count,
            media.Sum(item => item.Descriptor.Length),
            prepared.Metadata.ArchiveBytes,
            DateTime.UtcNow,
            IntegrityVerified: true);
        await staging.CommitPreparedImportAsync(stagingId, metadata);

        return (staging, new PortablePreparedImport(metadata, media));
    }
}
