using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Provider-independent behavioural contract for <see cref="IPortableImportStaging"/>.
/// A real staging provider can run the same suite by subclasses that return its
/// instances from <see cref="CreateStaging"/>; calling it again must return an
/// instance over the same durable state so restart behaviour is exercised.
/// </summary>
public abstract class PortableImportStagingContractTests
{
    protected abstract IPortableImportStaging CreateStaging();

    [Fact]
    public async Task Create_returns_distinct_non_empty_staging_identifiers()
    {
        await using var staging = CreateStaging();

        var first = await staging.CreateAsync();
        var second = await staging.CreateAsync();

        first.Value.Should().NotBe(Guid.Empty);
        second.Value.Should().NotBe(Guid.Empty);
        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Media_write_is_invisible_until_completed_and_reads_back_identical_bytes()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);

        var beforeComplete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        beforeComplete.Code.Should().Be(PortableStagingException.NotFoundCode);

        await staging.CompleteMediaAsync(id, write.Reference, content.LongLength, Sha256Hex(content));
        await write.DisposeAsync();

        await using var read = await staging.OpenMediaReadAsync(id, write.Reference);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task Abandoned_media_write_is_never_visible()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 4, 4, 4 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);
        await write.DisposeAsync();

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        var inventory = await staging.ListMediaAsync(id);
        inventory.Should().BeEmpty();

        var replacement = await staging.OpenMediaWriteAsync(id, descriptor);
        await replacement.Stream.WriteAsync(content);
        await staging.CompleteMediaAsync(
            id,
            replacement.Reference,
            content.LongLength,
            Sha256Hex(content));
        await replacement.DisposeAsync();

        await using var read = await staging.OpenMediaReadAsync(id, replacement.Reference);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task Second_write_for_the_same_media_descriptor_conflicts_while_open()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var descriptor = MediaDescriptor(new byte[] { 1, 2, 3 });

        var write = await staging.OpenMediaWriteAsync(id, descriptor);

        var conflict = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaWriteAsync(id, descriptor));
        conflict.Code.Should().Be(PortableStagingException.ConflictCode);

        await write.DisposeAsync();
    }

    [Fact]
    public async Task Completion_rejects_wrong_length_and_discards_the_item()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 1, 2, 3 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);

        var failure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(
                id,
                write.Reference,
                content.LongLength + 1,
                Sha256Hex(content)));
        failure.Code.Should().Be(PortableStagingException.IntegrityMismatchCode);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        (await staging.ListMediaAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Completion_rejects_wrong_hash_and_discards_the_item()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 5, 6, 7 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);

        var failure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(
                id,
                write.Reference,
                content.LongLength,
                new string('0', 64)));
        failure.Code.Should().Be(PortableStagingException.IntegrityMismatchCode);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
    }

    [Fact]
    public async Task Completion_is_idempotent_for_identical_values_and_conflicts_for_different_values()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 8, 8, 8, 8 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);
        await staging.CompleteMediaAsync(id, write.Reference, content.LongLength, Sha256Hex(content));

        await staging.CompleteMediaAsync(id, write.Reference, content.LongLength, Sha256Hex(content));

        var conflict = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(
                id,
                write.Reference,
                content.LongLength + 1,
                Sha256Hex(content)));
        conflict.Code.Should().Be(PortableStagingException.ConflictCode);

        await using var read = await staging.OpenMediaReadAsync(id, write.Reference);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task Unknown_staging_identifier_yields_the_same_not_found_outcome()
    {
        await using var staging = CreateStaging();
        var unknown = new PortableStagingId(Guid.NewGuid());
        var reference = new PortableStagedMediaReference("0123456789abcdef0123456789abcdef");
        var payloadReference = new PortableStagedPayloadReference("fedcba9876543210fedcba9876543210");
        var descriptor = MediaDescriptor(new byte[] { 1 });

        var read = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(unknown, reference));
        read.Code.Should().Be(PortableStagingException.NotFoundCode);

        var write = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaWriteAsync(unknown, descriptor));
        write.Code.Should().Be(PortableStagingException.NotFoundCode);

        var complete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(unknown, reference, 1, Sha256Hex(new byte[] { 1 })));
        complete.Code.Should().Be(PortableStagingException.NotFoundCode);

        var inventory = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.ListMediaAsync(unknown));
        inventory.Code.Should().Be(PortableStagingException.NotFoundCode);

        var dataRead = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenDataReadAsync(unknown, payloadReference));
        dataRead.Code.Should().Be(PortableStagingException.NotFoundCode);

        var manifestRead = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenManifestReadAsync(unknown, payloadReference));
        manifestRead.Code.Should().Be(PortableStagingException.NotFoundCode);

        var rebuild = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.RebuildPreparedImportAsync(unknown));
        rebuild.Code.Should().Be(PortableStagingException.NotFoundCode);
    }

    [Fact]
    public async Task References_from_another_staging_area_are_not_found_there()
    {
        await using var staging = CreateStaging();
        var owner = await staging.CreateAsync();
        var other = await staging.CreateAsync();
        var content = new byte[] { 2, 4, 6 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(owner, descriptor);
        await write.Stream.WriteAsync(content);
        await staging.CompleteMediaAsync(owner, write.Reference, content.LongLength, Sha256Hex(content));

        var read = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(other, write.Reference));
        read.Code.Should().Be(PortableStagingException.NotFoundCode);

        var complete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(
                other,
                write.Reference,
                content.LongLength,
                Sha256Hex(content)));
        complete.Code.Should().Be(PortableStagingException.NotFoundCode);

        await using var ownerRead = await staging.OpenMediaReadAsync(owner, write.Reference);
        using var copy = new MemoryStream();
        await ownerRead.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task References_with_path_separators_or_parent_segments_are_rejected()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 3, 3, 3 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);
        await staging.CompleteMediaAsync(id, write.Reference, content.LongLength, Sha256Hex(content));

        foreach (var value in new[] { "", "../escape", "media/book.bin", @"media\book.bin", "..", "a..b/c" })
        {
            var rejected = await Assert.ThrowsAsync<PortableStagingException>(
                () => staging.OpenMediaReadAsync(id, new PortableStagedMediaReference(value)));
            rejected.Code.Should().Be(PortableStagingException.InvalidReferenceCode);
        }

        await using var read = await staging.OpenMediaReadAsync(id, write.Reference);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task Media_inventory_lists_only_completed_items_in_order()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();

        var firstContent = new byte[] { 1 };
        var secondContent = new byte[] { 2, 2 };
        var firstDescriptor = MediaDescriptor(firstContent, "media/a.bin");
        var secondDescriptor = MediaDescriptor(secondContent, "media/b.bin");

        var firstWrite = await staging.OpenMediaWriteAsync(id, firstDescriptor);
        await firstWrite.Stream.WriteAsync(firstContent);
        await staging.CompleteMediaAsync(id, firstWrite.Reference, firstContent.LongLength, Sha256Hex(firstContent));
        await firstWrite.DisposeAsync();

        var abandonedWrite = await staging.OpenMediaWriteAsync(id, MediaDescriptor(new byte[] { 9 }, "media/c.bin"));
        await abandonedWrite.Stream.WriteAsync(new byte[] { 9 });
        await abandonedWrite.DisposeAsync();

        var secondWrite = await staging.OpenMediaWriteAsync(id, secondDescriptor);
        await secondWrite.Stream.WriteAsync(secondContent);
        await staging.CompleteMediaAsync(id, secondWrite.Reference, secondContent.LongLength, Sha256Hex(secondContent));
        await secondWrite.DisposeAsync();

        var inventory = await staging.ListMediaAsync(id);

        inventory.Select(item => item.Descriptor.Path)
            .Should().Equal("media/a.bin", "media/b.bin");
        inventory[0].Reference.Should().Be(firstWrite.Reference);
        inventory[1].Reference.Should().Be(secondWrite.Reference);
        inventory.Select(item => item.Descriptor.BookId).Should().OnlyContain(bookId => bookId != Guid.Empty);
    }

    [Fact]
    public async Task Delete_is_idempotent_and_removes_everything_for_the_staging_id()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var metadata = await StageCompleteImportAsync(staging, id);
        await staging.CommitPreparedImportAsync(id, metadata);

        await staging.DeleteAsync(id);
        await staging.DeleteAsync(id);
        await staging.DeleteAsync(new PortableStagingId(Guid.NewGuid()));
        await staging.DeleteAsync(default);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, new PortableStagedMediaReference("missing")));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.ListMediaAsync(id));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.RebuildPreparedImportAsync(id));
    }

    [Fact]
    public async Task Delete_discards_in_flight_writes()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 7, 7 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);

        await staging.DeleteAsync(id);

        var complete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(id, write.Reference, content.LongLength, Sha256Hex(content)));
        complete.Code.Should().Be(PortableStagingException.NotFoundCode);

        var writeAfterDelete = await Assert.ThrowsAsync<PortableStagingException>(
            () => write.Stream.WriteAsync(new byte[] { 1 }).AsTask());
        writeAfterDelete.Code.Should().Be(PortableStagingException.NotFoundCode);
    }

    [Fact]
    public async Task Cancellation_leaves_no_partially_visible_item()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 6, 6, 6 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);
        await write.DisposeAsync();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => staging.CompleteMediaAsync(
                id,
                write.Reference,
                content.LongLength,
                Sha256Hex(content),
                cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => staging.OpenDataWriteAsync(
                id,
                new PortableArchivePayload("data/library.json", 0, new string('a', 64)),
                cancelled.Token));

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        (await staging.ListMediaAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Declared_length_over_the_item_limit_is_rejected_at_open()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var descriptor = MediaDescriptor(new byte[] { 1 }) with
        {
            Length = PortableArchiveLimits.MaxSingleEntryBytes + 1,
        };

        var failure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaWriteAsync(id, descriptor));
        failure.Code.Should().Be(PortableStagingException.LimitExceededCode);
    }

    [Fact]
    public async Task Writing_past_the_manifest_limit_fails_during_write()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var descriptor = new PortableArchivePayload(
            "manifest.json",
            PortableArchiveLimits.MaxManifestBytes,
            new string('a', 64));

        var write = await staging.OpenManifestWriteAsync(id, descriptor);
        var chunk = new byte[1024 * 1024];
        long written = 0;
        while (written < PortableArchiveLimits.MaxManifestBytes)
        {
            await write.Stream.WriteAsync(chunk);
            written += chunk.Length;
        }

        var failure = await Assert.ThrowsAsync<PortableStagingException>(
            () => write.Stream.WriteAsync(new byte[] { 0 }).AsTask());
        failure.Code.Should().Be(PortableStagingException.LimitExceededCode);
    }

    [Fact]
    public async Task Relational_data_and_manifest_are_readable_and_a_committed_import_survives_restart()
    {
        var first = CreateStaging();
        var id = await first.CreateAsync();

        var dataBytes = Encoding.UTF8.GetBytes("{\"version\":3}");
        var manifestBytes = Encoding.UTF8.GetBytes("{\"format\":\"nostos-portable\"}");
        var mediaBytes = new byte[] { 9, 8, 7, 6 };

        var dataDescriptor = new PortableArchivePayload(
            "data/library.json",
            dataBytes.LongLength,
            Sha256Hex(dataBytes));
        var manifestDescriptor = new PortableArchivePayload(
            "manifest.json",
            manifestBytes.LongLength,
            Sha256Hex(manifestBytes));
        var mediaDescriptor = MediaDescriptor(mediaBytes);

        var dataWrite = await first.OpenDataWriteAsync(id, dataDescriptor);
        await dataWrite.Stream.WriteAsync(dataBytes);

        var dataBeforeComplete = await Assert.ThrowsAsync<PortableStagingException>(
            () => first.OpenDataReadAsync(id, dataWrite.Reference));
        dataBeforeComplete.Code.Should().Be(PortableStagingException.NotFoundCode);

        await first.CompleteDataAsync(id, dataWrite.Reference, dataBytes.LongLength, Sha256Hex(dataBytes));
        await dataWrite.DisposeAsync();

        var manifestWrite = await first.OpenManifestWriteAsync(id, manifestDescriptor);
        await manifestWrite.Stream.WriteAsync(manifestBytes);
        await first.CompleteManifestAsync(
            id,
            manifestWrite.Reference,
            manifestBytes.LongLength,
            Sha256Hex(manifestBytes));
        await manifestWrite.DisposeAsync();

        var mediaWrite = await first.OpenMediaWriteAsync(id, mediaDescriptor);
        await mediaWrite.Stream.WriteAsync(mediaBytes);
        await first.CompleteMediaAsync(
            id,
            mediaWrite.Reference,
            mediaBytes.LongLength,
            Sha256Hex(mediaBytes));
        await mediaWrite.DisposeAsync();

        var metadata = new PreparedPortableImportMetadata(
            StagingId: id,
            FormatVersion: 1,
            DataVersion: 3,
            DataBytes: dataBytes.LongLength,
            DataSha256: Sha256Hex(dataBytes),
            Counts: new MigrationArchiveCounts(Books: 1, MediaEntries: 1),
            MediaFiles: 1,
            MediaBytes: mediaBytes.LongLength,
            ArchiveBytes: 4096,
            PreparedAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IntegrityVerified: true);

        await first.CommitPreparedImportAsync(id, metadata);
        await first.DisposeAsync();

        var second = CreateStaging();

        var rebuilt = await second.RebuildPreparedImportAsync(id);
        rebuilt.Metadata.Should().Be(metadata);
        rebuilt.Media.Should().ContainSingle();
        rebuilt.Media[0].Descriptor.Should().Be(mediaDescriptor);
        rebuilt.Media[0].Reference.Should().Be(mediaWrite.Reference);

        await using var rebuiltData = await second.OpenDataReadAsync(id, dataWrite.Reference);
        using var dataCopy = new MemoryStream();
        await rebuiltData.CopyToAsync(dataCopy);
        dataCopy.ToArray().Should().Equal(dataBytes);

        await using var rebuiltManifest = await second.OpenManifestReadAsync(id, manifestWrite.Reference);
        using var manifestCopy = new MemoryStream();
        await rebuiltManifest.CopyToAsync(manifestCopy);
        manifestCopy.ToArray().Should().Equal(manifestBytes);

        await second.DisposeAsync();
    }

    [Fact]
    public async Task Rebuild_before_commit_is_not_found()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();

        var failure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.RebuildPreparedImportAsync(id));
        failure.Code.Should().Be(PortableStagingException.NotFoundCode);

        await staging.DeleteAsync(id);
    }

    [Fact]
    public async Task Commit_rejects_a_descriptor_that_disagrees_with_staged_state()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var metadata = await StageCompleteImportAsync(staging, id);

        var wrongMediaCount = metadata with { MediaFiles = metadata.MediaFiles + 1 };
        var countFailure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, wrongMediaCount));
        countFailure.Code.Should().Be(PortableStagingException.IntegrityMismatchCode);

        var wrongDataHash = metadata with { DataSha256 = new string('0', 64) };
        var dataFailure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, wrongDataHash));
        dataFailure.Code.Should().Be(PortableStagingException.IntegrityMismatchCode);

        await staging.CommitPreparedImportAsync(id, metadata);
    }

    [Fact]
    public async Task Commit_rejects_while_any_write_is_still_open()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var metadata = await StageCompleteImportAsync(staging, id);

        var extra = await staging.OpenMediaWriteAsync(
            id,
            MediaDescriptor(new byte[] { 1 }, "media/extra.bin"));
        await extra.Stream.WriteAsync(new byte[] { 1 });

        var conflict = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, metadata));
        conflict.Code.Should().Be(PortableStagingException.ConflictCode);

        await extra.DisposeAsync();

        await staging.CommitPreparedImportAsync(id, metadata);
        var rebuilt = await staging.RebuildPreparedImportAsync(id);
        rebuilt.Media.Should().ContainSingle();
    }

    [Fact]
    public async Task Commit_is_idempotent_for_identical_descriptors_and_conflicts_for_different_ones()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var metadata = await StageCompleteImportAsync(staging, id);

        await staging.CommitPreparedImportAsync(id, metadata);
        await staging.CommitPreparedImportAsync(id, metadata);

        var different = metadata with { ArchiveBytes = metadata.ArchiveBytes + 1 };
        var conflict = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, different));
        conflict.Code.Should().Be(PortableStagingException.ConflictCode);

        var rebuilt = await staging.RebuildPreparedImportAsync(id);
        rebuilt.Metadata.Should().Be(metadata);
    }

    private static async Task<PreparedPortableImportMetadata> StageCompleteImportAsync(
        IPortableImportStaging staging,
        PortableStagingId id)
    {
        var dataBytes = Encoding.UTF8.GetBytes("{\"version\":3}");
        var manifestBytes = Encoding.UTF8.GetBytes("{\"format\":\"nostos-portable\"}");
        var mediaBytes = new byte[] { 9, 8, 7, 6 };

        var dataDescriptor = new PortableArchivePayload(
            "data/library.json",
            dataBytes.LongLength,
            Sha256Hex(dataBytes));
        var dataWrite = await staging.OpenDataWriteAsync(id, dataDescriptor);
        await dataWrite.Stream.WriteAsync(dataBytes);
        await staging.CompleteDataAsync(id, dataWrite.Reference, dataBytes.LongLength, Sha256Hex(dataBytes));
        await dataWrite.DisposeAsync();

        var manifestDescriptor = new PortableArchivePayload(
            "manifest.json",
            manifestBytes.LongLength,
            Sha256Hex(manifestBytes));
        var manifestWrite = await staging.OpenManifestWriteAsync(id, manifestDescriptor);
        await manifestWrite.Stream.WriteAsync(manifestBytes);
        await staging.CompleteManifestAsync(
            id,
            manifestWrite.Reference,
            manifestBytes.LongLength,
            Sha256Hex(manifestBytes));
        await manifestWrite.DisposeAsync();

        var mediaDescriptor = MediaDescriptor(mediaBytes);
        var mediaWrite = await staging.OpenMediaWriteAsync(id, mediaDescriptor);
        await mediaWrite.Stream.WriteAsync(mediaBytes);
        await staging.CompleteMediaAsync(
            id,
            mediaWrite.Reference,
            mediaBytes.LongLength,
            Sha256Hex(mediaBytes));
        await mediaWrite.DisposeAsync();

        return new PreparedPortableImportMetadata(
            StagingId: id,
            FormatVersion: 1,
            DataVersion: 3,
            DataBytes: dataBytes.LongLength,
            DataSha256: Sha256Hex(dataBytes),
            Counts: new MigrationArchiveCounts(Books: 1, MediaEntries: 1),
            MediaFiles: 1,
            MediaBytes: mediaBytes.LongLength,
            ArchiveBytes: 4096,
            PreparedAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IntegrityVerified: true);
    }

    private static PortableArchiveMediaEntry MediaDescriptor(
        byte[] content,
        string path = "media/book.bin") =>
        new(
            BookId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Kind: "book",
            Path: path,
            FileName: System.IO.Path.GetFileName(path),
            ContentType: "application/octet-stream",
            Length: content.LongLength,
            Sha256: Sha256Hex(content));

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

public sealed class InMemoryPortableImportStagingContractTests : PortableImportStagingContractTests
{
    private readonly InMemoryPortableImportStagingStore _store = new();

    protected override IPortableImportStaging CreateStaging() =>
        new InMemoryPortableImportStaging(_store);
}
