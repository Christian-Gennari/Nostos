using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

/// <summary>
/// Provider-independent behavioural contract for <see cref="IPortableImportStaging"/>.
/// A real staging provider can run the same suite by subclassing and returning its
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

        await staging.CompleteMediaAsync(id, write);
        await write.DisposeAsync();

        await using var read = await staging.OpenMediaReadAsync(id, write.Reference);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task Abandoned_media_write_is_never_visible_and_may_be_replaced()
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

        var completion = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(id, write));
        completion.Code.Should().Be(PortableStagingException.NotFoundCode);

        var replacement = await staging.OpenMediaWriteAsync(id, descriptor);
        await replacement.Stream.WriteAsync(content);
        await staging.CompleteMediaAsync(id, replacement);
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

        var whileOpen = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaWriteAsync(id, descriptor));
        whileOpen.Code.Should().Be(PortableStagingException.ConflictCode);

        await write.Stream.WriteAsync(new byte[] { 1, 2, 3 });
        await staging.CompleteMediaAsync(id, write);
        await write.DisposeAsync();

        var afterComplete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaWriteAsync(id, descriptor));
        afterComplete.Code.Should().Be(PortableStagingException.ConflictCode);
    }

    [Fact]
    public async Task Write_after_payload_completion_is_rejected()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();

        var dataBytes = Encoding.UTF8.GetBytes("{\"version\":3}");
        var dataWrite = await staging.OpenDataWriteAsync(
            id,
            new PortableArchivePayload("data/library.json", dataBytes.LongLength, Sha256Hex(dataBytes)));
        await dataWrite.Stream.WriteAsync(dataBytes);
        await staging.CompleteDataAsync(id, dataWrite);

        var rejectedData = await Assert.ThrowsAsync<PortableStagingException>(
            () => dataWrite.Stream.WriteAsync(new byte[] { 0x7D }).AsTask());
        rejectedData.Code.Should().Be(PortableStagingException.ConflictCode);

        var manifestBytes = Encoding.UTF8.GetBytes("{\"format\":\"nostos-portable\"}");
        var manifestWrite = await staging.OpenManifestWriteAsync(
            id,
            new PortableArchivePayload("manifest.json", manifestBytes.LongLength, Sha256Hex(manifestBytes)));
        await manifestWrite.Stream.WriteAsync(manifestBytes);
        await staging.CompleteManifestAsync(id, manifestWrite);

        var rejectedManifest = await Assert.ThrowsAsync<PortableStagingException>(
            () => manifestWrite.Stream.WriteAsync(new byte[] { 0x7D }).AsTask());
        rejectedManifest.Code.Should().Be(PortableStagingException.ConflictCode);

        await using var dataRead = await staging.OpenDataReadAsync(id);
        using var dataCopy = new MemoryStream();
        await dataRead.CopyToAsync(dataCopy);
        dataCopy.ToArray().Should().Equal(dataBytes);

        await using var manifestRead = await staging.OpenManifestReadAsync(id);
        using var manifestCopy = new MemoryStream();
        await manifestRead.CopyToAsync(manifestCopy);
        manifestCopy.ToArray().Should().Equal(manifestBytes);
    }

    [Fact]
    public async Task Completion_rejects_wrong_payload_hash_and_discards_the_payload()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var dataBytes = Encoding.UTF8.GetBytes("{\"version\":3}");
        var descriptor = new PortableArchivePayload(
            "data/library.json",
            dataBytes.LongLength,
            new string('0', 64));

        var dataWrite = await staging.OpenDataWriteAsync(id, descriptor);
        await dataWrite.Stream.WriteAsync(dataBytes);

        var failure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteDataAsync(id, dataWrite));
        failure.Code.Should().Be(PortableStagingException.IntegrityMismatchCode);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenDataReadAsync(id));

        var replacement = await staging.OpenDataWriteAsync(id, descriptor);
        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task Completion_rejects_wrong_length_and_discards_the_item()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 1, 2, 3 };
        var descriptor = MediaDescriptor(content) with
        {
            Length = content.LongLength + 2,
        };

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);

        var failure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(id, write));
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
        var descriptor = MediaDescriptor(content) with
        {
            Sha256 = new string('0', 64),
        };

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);

        var failure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(id, write));
        failure.Code.Should().Be(PortableStagingException.IntegrityMismatchCode);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
    }

    [Fact]
    public async Task Recompleting_a_completed_handle_is_idempotent_and_keeps_bytes()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 8, 8, 8, 8 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);
        await staging.CompleteMediaAsync(id, write);
        await staging.CompleteMediaAsync(id, write);

        await write.DisposeAsync();

        await using var read = await staging.OpenMediaReadAsync(id, write.Reference);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task Write_after_completion_is_rejected_and_bytes_stay_verified()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 2, 2, 2 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);
        await staging.CompleteMediaAsync(id, write);

        var sealedWrite = await Assert.ThrowsAsync<PortableStagingException>(
            () => write.Stream.WriteAsync(new byte[] { 0xFF }).AsTask());
        sealedWrite.Code.Should().Be(PortableStagingException.ConflictCode);

        await write.DisposeAsync();

        await using var read = await staging.OpenMediaReadAsync(id, write.Reference);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task Unknown_staging_identifier_yields_the_same_not_found_outcome()
    {
        await using var staging = CreateStaging();
        var known = await staging.CreateAsync();
        var unknown = new PortableStagingId(Guid.NewGuid());
        var reference = new PortableStagedMediaReference("0123456789abcdef0123456789abcdef");
        var descriptor = MediaDescriptor(new byte[] { 1 });
        var dataDescriptor = new PortableArchivePayload("data/library.json", 1, Sha256Hex(new byte[] { 0x7B }));

        var mediaRead = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(unknown, reference));
        mediaRead.Code.Should().Be(PortableStagingException.NotFoundCode);

        var mediaWrite = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaWriteAsync(unknown, descriptor));
        mediaWrite.Code.Should().Be(PortableStagingException.NotFoundCode);

        var dataRead = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenDataReadAsync(unknown));
        dataRead.Code.Should().Be(PortableStagingException.NotFoundCode);

        var defaultRead = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenDataReadAsync(default));
        defaultRead.Code.Should().Be(PortableStagingException.NotFoundCode);

        var manifestRead = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenManifestReadAsync(unknown));
        manifestRead.Code.Should().Be(PortableStagingException.NotFoundCode);

        var inventory = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.ListMediaAsync(unknown));
        inventory.Code.Should().Be(PortableStagingException.NotFoundCode);

        var rebuild = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.RebuildPreparedImportAsync(unknown));
        rebuild.Code.Should().Be(PortableStagingException.NotFoundCode);

        var commit = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(
                unknown,
                MetadataFor(unknown)));
        commit.Code.Should().Be(PortableStagingException.NotFoundCode);

        var fromKnown = await staging.OpenMediaWriteAsync(known, descriptor);
        var complete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(unknown, fromKnown));
        complete.Code.Should().Be(PortableStagingException.NotFoundCode);

        var payloadWrite = await staging.OpenDataWriteAsync(known, dataDescriptor);
        var completeData = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteDataAsync(unknown, payloadWrite));

        completeData.Code.Should().Be(PortableStagingException.NotFoundCode);

        await fromKnown.DisposeAsync();
        await payloadWrite.DisposeAsync();
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
        await staging.CompleteMediaAsync(owner, write);

        var read = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(other, write.Reference));
        read.Code.Should().Be(PortableStagingException.NotFoundCode);

        var complete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(other, write));
        complete.Code.Should().Be(PortableStagingException.NotFoundCode);

        await using var ownerRead = await staging.OpenMediaReadAsync(owner, write.Reference);
        using var copy = new MemoryStream();
        await ownerRead.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task Payload_handles_from_another_staging_area_are_rejected()
    {
        await using var staging = CreateStaging();
        var owner = await staging.CreateAsync();
        var other = await staging.CreateAsync();
        var dataBytes = Encoding.UTF8.GetBytes("{\"version\":3}");
        var descriptor = new PortableArchivePayload(
            "data/library.json",
            dataBytes.LongLength,
            Sha256Hex(dataBytes));

        var dataWrite = await staging.OpenDataWriteAsync(owner, descriptor);
        await dataWrite.Stream.WriteAsync(dataBytes);

        var wrongOwner = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteDataAsync(other, dataWrite));
        wrongOwner.Code.Should().Be(PortableStagingException.NotFoundCode);

        var otherRead = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenDataReadAsync(other));
        otherRead.Code.Should().Be(PortableStagingException.NotFoundCode);

        await staging.CompleteDataAsync(owner, dataWrite);
        await dataWrite.DisposeAsync();

        await using var read = await staging.OpenDataReadAsync(owner);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(dataBytes);
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
        await staging.CompleteMediaAsync(id, write);

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
        await staging.CompleteMediaAsync(id, firstWrite);
        await firstWrite.DisposeAsync();

        var discardedWrite = await staging.OpenMediaWriteAsync(id, MediaDescriptor(new byte[] { 9 }, "media/c.bin"));
        await discardedWrite.Stream.WriteAsync(new byte[] { 9 });
        await discardedWrite.DisposeAsync();

        var secondWrite = await staging.OpenMediaWriteAsync(id, secondDescriptor);
        await secondWrite.Stream.WriteAsync(secondContent);
        await staging.CompleteMediaAsync(id, secondWrite);
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
        var staged = await StageCompleteImportAsync(staging, id);
        await staging.CommitPreparedImportAsync(id, staged.Metadata);

        await staging.DeleteAsync(id);
        await staging.DeleteAsync(id);
        await staging.DeleteAsync(new PortableStagingId(Guid.NewGuid()));
        await staging.DeleteAsync(default);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, staged.MediaWrite.Reference));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenDataReadAsync(id));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenManifestReadAsync(id));
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
            () => staging.CompleteMediaAsync(id, write));
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
            () => staging.CompleteMediaAsync(id, write, cancelled.Token));
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

        var oversizedData = new PortableArchivePayload(
            "data/library.json",
            PortableArchiveLimits.MaxDataBytes + 1,
            new string('a', 64));
        var dataFailure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenDataWriteAsync(id, oversizedData));
        dataFailure.Code.Should().Be(PortableStagingException.LimitExceededCode);

        var oversizedManifest = new PortableArchivePayload(
            "manifest.json",
            PortableArchiveLimits.MaxManifestBytes + 1,
            new string('a', 64));
        var manifestFailure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenManifestWriteAsync(id, oversizedManifest));
        manifestFailure.Code.Should().Be(PortableStagingException.LimitExceededCode);
    }

    [Fact]
    public async Task Writing_past_the_manifest_limit_discards_the_item()
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

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenManifestReadAsync(id));
        var complete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteManifestAsync(id, write));
        complete.Code.Should().Be(PortableStagingException.NotFoundCode);

        var deadHandle = await Assert.ThrowsAsync<PortableStagingException>(
            () => write.Stream.WriteAsync(new byte[] { 0 }).AsTask());
        deadHandle.Code.Should().Be(PortableStagingException.ConflictCode);

        var replacement = await staging.OpenManifestWriteAsync(id, descriptor);
        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task Media_write_past_its_declared_length_discards_the_item()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 1, 2, 3, 4 };
        var descriptor = MediaDescriptor(content);

        var write = await staging.OpenMediaWriteAsync(id, descriptor);
        await write.Stream.WriteAsync(content);

        var failure = await Assert.ThrowsAsync<PortableStagingException>(
            () => write.Stream.WriteAsync(new byte[] { 5 }).AsTask());
        failure.Code.Should().Be(PortableStagingException.LimitExceededCode);

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        (await staging.ListMediaAsync(id)).Should().BeEmpty();

        var complete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(id, write));
        complete.Code.Should().Be(PortableStagingException.NotFoundCode);

        var replacement = await staging.OpenMediaWriteAsync(id, descriptor);
        await replacement.DisposeAsync();
    }

    [Fact]
    public async Task Relational_data_and_manifest_survive_restart_and_are_readable_by_staging_id_alone()
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
            () => first.OpenDataReadAsync(id));
        dataBeforeComplete.Code.Should().Be(PortableStagingException.NotFoundCode);

        await first.CompleteDataAsync(id, dataWrite);
        await dataWrite.DisposeAsync();

        var manifestWrite = await first.OpenManifestWriteAsync(id, manifestDescriptor);
        await manifestWrite.Stream.WriteAsync(manifestBytes);
        await first.CompleteManifestAsync(id, manifestWrite);
        await manifestWrite.DisposeAsync();

        var mediaWrite = await first.OpenMediaWriteAsync(id, mediaDescriptor);
        await mediaWrite.Stream.WriteAsync(mediaBytes);
        await first.CompleteMediaAsync(id, mediaWrite);
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

        await using var rebuiltData = await second.OpenDataReadAsync(id);
        using var dataCopy = new MemoryStream();
        await rebuiltData.CopyToAsync(dataCopy);
        dataCopy.ToArray().Should().Equal(dataBytes);

        await using var rebuiltManifest = await second.OpenManifestReadAsync(id);
        using var manifestCopy = new MemoryStream();
        await rebuiltManifest.CopyToAsync(manifestCopy);
        manifestCopy.ToArray().Should().Equal(manifestBytes);

        foreach (var item in rebuilt.Media)
        {
            await using var mediaRead = await second.OpenMediaReadAsync(id, item.Reference);
            using var mediaCopy = new MemoryStream();
            await mediaRead.CopyToAsync(mediaCopy);
            mediaCopy.ToArray().Should().Equal(mediaBytes);
        }

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
        var staged = await StageCompleteImportAsync(staging, id);

        // The normative contract requires a typed conflict for every
        // descriptor-versus-staged-state disagreement, not an integrity mismatch.
        var wrongMediaCount = staged.Metadata with { MediaFiles = staged.Metadata.MediaFiles + 1 };
        var countFailure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, wrongMediaCount));
        countFailure.Code.Should().Be(PortableStagingException.ConflictCode);

        var wrongDataHash = staged.Metadata with { DataSha256 = new string('0', 64) };
        var dataFailure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, wrongDataHash));
        dataFailure.Code.Should().Be(PortableStagingException.ConflictCode);

        var wrongMediaBytes = staged.Metadata with { MediaBytes = staged.Metadata.MediaBytes + 1 };
        var mediaBytesFailure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, wrongMediaBytes));
        mediaBytesFailure.Code.Should().Be(PortableStagingException.ConflictCode);

        var wrongCounts = staged.Metadata with
        {
            Counts = staged.Metadata.Counts with { MediaEntries = staged.Metadata.MediaFiles + 1 },
        };
        var countsFailure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, wrongCounts));
        countsFailure.Code.Should().Be(PortableStagingException.ConflictCode);

        // A descriptor for another staging area is likewise a conflict.
        var wrongStaging = staged.Metadata with { StagingId = new PortableStagingId(Guid.NewGuid()) };
        var stagingFailure = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, wrongStaging));
        stagingFailure.Code.Should().Be(PortableStagingException.ConflictCode);

        await staging.CommitPreparedImportAsync(id, staged.Metadata);
    }

    [Fact]
    public async Task Commit_rejects_without_completed_payloads()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();

        var conflict = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, MetadataFor(id)));
        conflict.Code.Should().Be(PortableStagingException.ConflictCode);
    }

    [Fact]
    public async Task Commit_rejects_while_any_write_is_still_open()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var staged = await StageCompleteImportAsync(staging, id);

        var extra = await staging.OpenMediaWriteAsync(
            id,
            MediaDescriptor(new byte[] { 1 }, "media/extra.bin"));
        await extra.Stream.WriteAsync(new byte[] { 1 });

        var conflict = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, staged.Metadata));
        conflict.Code.Should().Be(PortableStagingException.ConflictCode);

        await extra.DisposeAsync();

        await staging.CommitPreparedImportAsync(id, staged.Metadata);
        var rebuilt = await staging.RebuildPreparedImportAsync(id);
        rebuilt.Media.Should().ContainSingle();
    }

    [Fact]
    public async Task Commit_is_idempotent_for_identical_descriptors_and_conflicts_for_different_ones()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var staged = await StageCompleteImportAsync(staging, id);

        await staging.CommitPreparedImportAsync(id, staged.Metadata);
        await staging.CommitPreparedImportAsync(id, staged.Metadata);

        var different = staged.Metadata with { ArchiveBytes = staged.Metadata.ArchiveBytes + 1 };
        var conflict = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CommitPreparedImportAsync(id, different));
        conflict.Code.Should().Be(PortableStagingException.ConflictCode);

        var rebuilt = await staging.RebuildPreparedImportAsync(id);
        rebuilt.Metadata.Should().Be(staged.Metadata);
    }

    [Fact]
    public async Task Writes_and_completions_after_commit_fail_with_already_committed()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var staged = await StageCompleteImportAsync(staging, id);
        await staging.CommitPreparedImportAsync(id, staged.Metadata);

        var mediaWrite = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaWriteAsync(id, MediaDescriptor(new byte[] { 1 }, "media/after.bin")));
        mediaWrite.Code.Should().Be(PortableStagingException.AlreadyCommittedCode);

        var dataWrite = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenDataWriteAsync(
                id,
                new PortableArchivePayload("data/library.json", 0, new string('a', 64))));
        dataWrite.Code.Should().Be(PortableStagingException.AlreadyCommittedCode);

        var manifestWrite = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenManifestWriteAsync(
                id,
                new PortableArchivePayload("manifest.json", 0, new string('a', 64))));
        manifestWrite.Code.Should().Be(PortableStagingException.AlreadyCommittedCode);

        var mediaComplete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(id, staged.MediaWrite));
        mediaComplete.Code.Should().Be(PortableStagingException.AlreadyCommittedCode);

        var dataComplete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteDataAsync(id, staged.DataWrite));
        dataComplete.Code.Should().Be(PortableStagingException.AlreadyCommittedCode);

        var manifestComplete = await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteManifestAsync(id, staged.ManifestWrite));
        manifestComplete.Code.Should().Be(PortableStagingException.AlreadyCommittedCode);

        var rebuilt = await staging.RebuildPreparedImportAsync(id);
        rebuilt.Metadata.Should().Be(staged.Metadata);

        await using var dataRead = await staging.OpenDataReadAsync(id);
        using var dataCopy = new MemoryStream();
        await dataRead.CopyToAsync(dataCopy);
        dataCopy.ToArray().Should().Equal(staged.DataBytes);

        (await staging.ListMediaAsync(id)).Should().ContainSingle();
        await using var mediaRead = await staging.OpenMediaReadAsync(id, staged.MediaWrite.Reference);
        using var mediaCopy = new MemoryStream();
        await mediaRead.CopyToAsync(mediaCopy);
        mediaCopy.ToArray().Should().Equal(staged.MediaBytes);

        await staging.DeleteAsync(id);
        await staging.DeleteAsync(id);
    }

    [Fact]
    public async Task Concurrent_delete_and_completion_settle_in_legal_outcomes()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 1, 2, 3 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Run(async () =>
        {
            await start.Task;
            try
            {
                await staging.CompleteMediaAsync(id, write);
                return null;
            }
            catch (PortableStagingException exception)
            {
                return exception;
            }
        });
        var deletion = Task.Run(async () =>
        {
            await start.Task;
            await staging.DeleteAsync(id);
        });

        start.SetResult();
        var failure = await completion;
        await deletion;

        if (failure is not null)
        {
            failure.Code.Should().Be(PortableStagingException.NotFoundCode);
        }

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.OpenMediaReadAsync(id, write.Reference));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.RebuildPreparedImportAsync(id));
        await staging.DeleteAsync(id);
        await write.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_writes_and_delete_never_publish_partial_items()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var write = await staging.OpenMediaWriteAsync(
            id,
            MediaDescriptor(new byte[8]));
        await write.Stream.WriteAsync(new byte[4]);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWrite = Task.Run(async () =>
        {
            await start.Task;
            try
            {
                await write.Stream.WriteAsync(new byte[4]);
                return null;
            }
            catch (PortableStagingException exception)
            {
                return exception;
            }
        });
        var deletion = Task.Run(async () =>
        {
            await start.Task;
            await staging.DeleteAsync(id);
        });

        start.SetResult();
        var failure = await secondWrite;
        await deletion;

        if (failure is not null)
        {
            failure.Code.Should().Be(PortableStagingException.NotFoundCode);
        }

        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.ListMediaAsync(id));
        await Assert.ThrowsAsync<PortableStagingException>(
            () => staging.CompleteMediaAsync(id, write));
        await write.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_identical_completions_of_the_same_handle_are_idempotent()
    {
        await using var staging = CreateStaging();
        var id = await staging.CreateAsync();
        var content = new byte[] { 5, 5, 5, 5 };
        var write = await staging.OpenMediaWriteAsync(id, MediaDescriptor(content));
        await write.Stream.WriteAsync(content);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            await start.Task;
            await staging.CompleteMediaAsync(id, write);
        });
        var second = Task.Run(async () =>
        {
            await start.Task;
            await staging.CompleteMediaAsync(id, write);
        });

        start.SetResult();
        await Task.WhenAll(first, second);

        await using var read = await staging.OpenMediaReadAsync(id, write.Reference);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(content);
    }

    private static async Task<StagedImport> StageCompleteImportAsync(
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
        await staging.CompleteDataAsync(id, dataWrite);
        await dataWrite.DisposeAsync();

        var manifestDescriptor = new PortableArchivePayload(
            "manifest.json",
            manifestBytes.LongLength,
            Sha256Hex(manifestBytes));
        var manifestWrite = await staging.OpenManifestWriteAsync(id, manifestDescriptor);
        await manifestWrite.Stream.WriteAsync(manifestBytes);
        await staging.CompleteManifestAsync(id, manifestWrite);
        await manifestWrite.DisposeAsync();

        var mediaDescriptor = MediaDescriptor(mediaBytes);
        var mediaWrite = await staging.OpenMediaWriteAsync(id, mediaDescriptor);
        await mediaWrite.Stream.WriteAsync(mediaBytes);
        await staging.CompleteMediaAsync(id, mediaWrite);
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

        return new StagedImport(
            metadata,
            dataWrite,
            manifestWrite,
            mediaWrite,
            dataBytes,
            manifestBytes,
            mediaBytes);
    }

    private static PreparedPortableImportMetadata MetadataFor(PortableStagingId id) =>
        new(
            StagingId: id,
            FormatVersion: 1,
            DataVersion: 3,
            DataBytes: 0,
            DataSha256: new string('0', 64),
            Counts: MigrationArchiveCounts.Empty,
            MediaFiles: 0,
            MediaBytes: 0,
            ArchiveBytes: 0,
            PreparedAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IntegrityVerified: false);

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

    private sealed record StagedImport(
        PreparedPortableImportMetadata Metadata,
        PortableStagingPayloadWrite DataWrite,
        PortableStagingPayloadWrite ManifestWrite,
        PortableStagingWrite MediaWrite,
        byte[] DataBytes,
        byte[] ManifestBytes,
        byte[] MediaBytes);
}

public sealed class InMemoryPortableImportStagingContractTests : PortableImportStagingContractTests
{
    private readonly InMemoryPortableImportStagingStore _store = new();

    protected override IPortableImportStaging CreateStaging() =>
        new InMemoryPortableImportStaging(_store);
}

public sealed class LocalPortableImportStagingContractTests
    : PortableImportStagingContractTests, IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nostos-local-staging-{Guid.NewGuid():N}");

    protected override IPortableImportStaging CreateStaging() =>
        new LocalPortableImportStaging(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }
}
