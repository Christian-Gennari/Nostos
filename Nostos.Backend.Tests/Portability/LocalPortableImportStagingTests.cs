using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Services.Portability;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

public sealed class LocalPortableImportStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nostos-capacity-{Guid.NewGuid():N}");

    [Fact]
    public async Task EnsureCapacity_accepts_exactly_the_budget_and_rejects_one_byte_more()
    {
        // 1000 available, 20% margin -> 800 staged bytes are exactly admissible.
        await using var staging = new LocalPortableImportStaging(_root, _ => 1000);

        staging.EnsureCapacity(800);

        var act = () => staging.EnsureCapacity(801);
        var exception = act.Should().Throw<PortableArchiveException>();
        exception.Which.Code.Should().Be("insufficient_temp_space");
    }

    [Fact]
    public async Task EnsureCapacity_reports_temp_space_unavailable_when_capacity_cannot_be_determined()
    {
        await using var staging = new LocalPortableImportStaging(_root, _ => null);

        var act = () => staging.EnsureCapacity(1);
        var exception = act.Should().Throw<PortableArchiveException>();
        exception.Which.Code.Should().Be("temp_space_unavailable");
    }

    [Fact]
    public async Task EnsureCapacity_reports_temp_space_unavailable_when_the_probe_throws_io_failure()
    {
        await using var staging = new LocalPortableImportStaging(
            _root,
            _ => throw new IOException("probe failed"));

        var act = () => staging.EnsureCapacity(1);
        var exception = act.Should().Throw<PortableArchiveException>();
        exception.Which.Code.Should().Be("temp_space_unavailable");
    }

    [Fact]
    public async Task EnsureCapacity_skips_the_probe_for_zero_bytes()
    {
        var probed = false;
        await using var staging = new LocalPortableImportStaging(_root, _ =>
        {
            probed = true;
            return 0;
        });

        staging.EnsureCapacity(0);

        probed.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureCapacity_probes_the_staging_root()
    {
        string? probedRoot = null;
        await using var staging = new LocalPortableImportStaging(_root, root =>
        {
            probedRoot = root;
            return long.MaxValue;
        });

        staging.EnsureCapacity(1);

        probedRoot.Should().Be(Path.GetFullPath(_root));
    }

    [Fact]
    public async Task Failed_descriptor_write_does_not_latch_committed_state()
    {
        await using var staging = new LocalPortableImportStaging(_root);
        var id = await staging.CreateAsync();
        var metadata = await StageMinimalImportAsync(staging, id);

        var preparedPath = Path.Combine(_root, id.Value.ToString("N"), "prepared.json");
        Directory.CreateDirectory(preparedPath);

        var commit = async () => await staging.CommitPreparedImportAsync(id, metadata);
        var failure = await commit.Should().ThrowAsync<Exception>();
        failure.Which.Should().NotBeOfType<PortableStagingException>(
            "the descriptor write failure must not be reported as a staging-contract conflict");

        // The failed descriptor write must leave the previous (uncommitted) state.
        var rebuild = async () => await staging.RebuildPreparedImportAsync(id);
        var notFound = await rebuild.Should().ThrowAsync<PortableStagingException>();
        notFound.Which.Code.Should().Be(PortableStagingException.NotFoundCode);

        await using (var data = await staging.OpenDataReadAsync(id))
        {
            using var copy = new MemoryStream();
            await data.CopyToAsync(copy);
            copy.Length.Should().BeGreaterThan(0);
        }

        Directory.Delete(preparedPath);
        await staging.CommitPreparedImportAsync(id, metadata);

        var rebuilt = await staging.RebuildPreparedImportAsync(id);
        rebuilt.Metadata.Should().Be(metadata);
    }

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

    private static async Task<PreparedPortableImportMetadata> StageMinimalImportAsync(
        LocalPortableImportStaging staging,
        PortableStagingId id)
    {
        var dataBytes = Encoding.UTF8.GetBytes("{\"version\":3}");
        var dataWrite = await staging.OpenDataWriteAsync(
            id,
            new PortableArchivePayload(
                "data/library.json",
                dataBytes.LongLength,
                Sha256Hex(dataBytes)));
        await dataWrite.Stream.WriteAsync(dataBytes);
        await staging.CompleteDataAsync(id, dataWrite);
        await dataWrite.DisposeAsync();

        var manifestBytes = Encoding.UTF8.GetBytes("{\"format\":\"nostos-portable\"}");
        var manifestWrite = await staging.OpenManifestWriteAsync(
            id,
            new PortableArchivePayload(
                "manifest.json",
                manifestBytes.LongLength,
                Sha256Hex(manifestBytes)));
        await manifestWrite.Stream.WriteAsync(manifestBytes);
        await staging.CompleteManifestAsync(id, manifestWrite);
        await manifestWrite.DisposeAsync();

        var mediaBytes = new byte[] { 1, 2, 3 };
        var mediaWrite = await staging.OpenMediaWriteAsync(
            id,
            new PortableArchiveMediaEntry(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "book",
                "media/book.bin",
                "book.bin",
                "application/octet-stream",
                mediaBytes.LongLength,
                Sha256Hex(mediaBytes)));
        await mediaWrite.Stream.WriteAsync(mediaBytes);
        await staging.CompleteMediaAsync(id, mediaWrite);
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

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
