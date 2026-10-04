using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Nostos.Backend.Data.Models;
using Xunit;

namespace Nostos.Backend.Tests.Portability;

// Slice 2 of issue #679: migration job/session/receipt/artifact/reservation
// rows are host-local operational state and must never leak into a portable
// export. This exercises a real export with every record type present and
// scans every archive entry for their distinctive identifiers.
public sealed class PortableExportExcludesMigrationRecordsTests
{
    private const string JobKeyMarker = "HOST-OPERATIONAL-JOB-KEY-MUST-NOT-EXPORT";
    private const string LeaseMarker = "HOST-OPERATIONAL-LEASE-TOKEN-MUST-NOT-EXPORT";
    private const string SessionKeyMarker = "HOST-OPERATIONAL-SESSION-KEY-MUST-NOT-EXPORT";
    private const string StorageKeyMarker = "uploads/HOST-OPERATIONAL-STORAGE-MUST-NOT-EXPORT/archive.part";
    private const string ArtifactMarker = "exports/HOST-OPERATIONAL-ARTIFACT-MUST-NOT-EXPORT/library.nostos";

    [Fact]
    public async Task Export_omits_host_operational_migration_records()
    {
        await using var source = await LocalPortableTestLibrary.CreateAsync();

        var work = new WorkModel
        {
            Id = Guid.NewGuid(),
            Title = "Exportable Work",
            NormalizedTitle = "EXPORTABLE WORK",
            NormalizedAuthor = string.Empty,
            CreatedAt = DateTime.UtcNow,
        };
        var book = new PhysicalBookModel
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Title = "Exportable Book",
            CreatedAt = DateTime.UtcNow,
        };
        source.Db.Works.Add(work);
        source.Db.Books.Add(book);

        var now = DateTimeOffset.UtcNow;
        var job = new MigrationJobRecord
        {
            Direction = 0,
            State = 2,
            IdempotencyKey = JobKeyMarker,
            CreationPayloadHash = new string('a', 64),
            MigrationLeaseToken = LeaseMarker,
            LeaseExpiresAtUtc = now.AddMinutes(5),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(7),
            ReservedStorageBytes = 1024,
        };
        var session = new MigrationSessionRecord
        {
            JobId = job.Id,
            Purpose = 0,
            State = 1,
            TotalBytes = 32 * 1024 * 1024,
            ChunkSize = 16 * 1024 * 1024,
            TotalChunks = 2,
            FileIdentitySizeBytes = 32 * 1024 * 1024,
            FileIdentitySha256 = new string('b', 64),
            IdempotencyKey = SessionKeyMarker,
            CreationPayloadHash = new string('c', 64),
            ReceivedBytes = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(24),
            StorageKey = StorageKeyMarker,
        };

        source.Db.MigrationJobRecords.Add(job);
        source.Db.MigrationSessionRecords.Add(session);
        source.Db.MigrationChunkReceiptRecords.Add(new MigrationChunkReceiptRecord
        {
            SessionId = session.Id,
            ChunkIndex = 0,
            OffsetBytes = 0,
            LengthBytes = 1024,
            Sha256 = new string('d', 64),
            ReceivedAtUtc = now,
        });
        source.Db.MigrationExportArtifactRecords.Add(new MigrationExportArtifactRecord
        {
            JobId = job.Id,
            State = 1,
            StorageKey = ArtifactMarker,
            FileName = "library.nostos",
            ContentType = "application/vnd.nostos.portable+zip",
            SizeBytes = 1024,
            Sha256 = new string('e', 64),
            CreatedAtUtc = now,
            AvailableAtUtc = now,
            ExpiresAtUtc = now.AddDays(7),
        });
        source.Db.MigrationStorageReservations.Add(new MigrationStorageReservationRecord
        {
            Purpose = 0,
            ReservedBytes = 64 * 1024 * 1024,
            MaterializedBytes = 0,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(15),
            ClaimedJobId = job.Id,
        });

        await source.Db.SaveChangesAsync();

        using var archive = new MemoryStream();
        var exported = await source.Portability().ExportAsync(archive);
        exported.Counts.Books.Should().Be(1);
        exported.Counts.Works.Should().Be(1);

        archive.Position = 0;
        var entryCount = 0;
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
        {
            foreach (var entry in zip.Entries)
            {
                entryCount++;
                entry.FullName.Should().NotContain("Migration");
                entry.FullName.Should().NotContain("HOST-OPERATIONAL");

                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var text = await reader.ReadToEndAsync();
                text.Should().NotContain(JobKeyMarker);
                text.Should().NotContain(LeaseMarker);
                text.Should().NotContain(SessionKeyMarker);
                text.Should().NotContain(StorageKeyMarker);
                text.Should().NotContain(ArtifactMarker);
            }
        }

        entryCount.Should().BeGreaterThan(0, "the export must have produced archive entries");
    }
}
