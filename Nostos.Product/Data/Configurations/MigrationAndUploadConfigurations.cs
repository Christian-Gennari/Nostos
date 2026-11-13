using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability;

namespace Nostos.Backend.Data.Configurations;

// --- DURABLE MIGRATION TRANSFER RECORDS (issue #679) ---
// Provider-portable operational state: Guid keys, int enums, long byte
// counts and concurrency versions, and UTC DateTime instants converted
// by UtcDateTimeValueConverter (the repo-wide convention, see
// ConfigureConventions). DateTime — not DateTimeOffset — because the
// SQLite provider can compare and order DateTime in SQL, which the
// lease/expiry conditional updates and sweeps require. Bounded strings
// and JSON recovery state as ordinary text. Check constraints use only
// SQL accepted by both SQLite and PostgreSQL; semantic validation
// remains in services. No absolute paths and no account identifiers
// are persisted here.

internal sealed class MigrationJobRecordConfiguration : IEntityTypeConfiguration<MigrationJobRecord>
{
    public void Configure(EntityTypeBuilder<MigrationJobRecord> builder)
    {
        builder.HasKey(j => j.Id);
        builder.Property(j => j.IdempotencyKey).IsRequired();
        builder.Property(j => j.CreationPayloadHash).IsRequired();
        builder.Property(j => j.Version).IsConcurrencyToken();

        // Installation-scoped owner uniqueness; the private hosted adapter
        // applies its own owner-scoped uniqueness behind the same contract.
        builder.HasIndex(j => j.IdempotencyKey).IsUnique();
        builder.HasIndex(j => new { j.State, j.LeaseExpiresAtUtc });
        builder.HasIndex(j => j.ExpiresAtUtc);
        builder.HasIndex(j => j.UpdatedAtUtc);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_MigrationJobRecords_ReservedStorageBytes",
                "\"ReservedStorageBytes\" >= 0");
            t.HasCheckConstraint(
                "CK_MigrationJobRecords_AttemptNumber",
                "\"AttemptNumber\" >= 1");
            t.HasCheckConstraint(
                "CK_MigrationJobRecords_IdempotencyKey",
                "length(\"IdempotencyKey\") > 0 AND length(\"IdempotencyKey\") <= 128");
            t.HasCheckConstraint(
                "CK_MigrationJobRecords_LeaseToken",
                "\"MigrationLeaseToken\" IS NULL OR " +
                "(length(\"MigrationLeaseToken\") > 0 AND length(\"MigrationLeaseToken\") <= 128)");
        });
    }
}

internal sealed class MigrationSessionRecordConfiguration : IEntityTypeConfiguration<MigrationSessionRecord>
{
    public void Configure(EntityTypeBuilder<MigrationSessionRecord> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.FileIdentitySha256).IsRequired();
        builder.Property(s => s.IdempotencyKey).IsRequired();
        builder.Property(s => s.CreationPayloadHash).IsRequired();
        builder.Property(s => s.StorageKey).IsRequired();
        builder.Property(s => s.Version).IsConcurrencyToken();

        // Sessions are owned by their job; explicit retention cleanup
        // deletes the job and cascades to its sessions and receipts.
        builder.HasOne<MigrationJobRecord>()
            .WithMany()
            .HasForeignKey(s => s.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.JobId, s.IdempotencyKey }).IsUnique();
        builder.HasIndex(s => new { s.JobId, s.State });
        builder.HasIndex(s => s.ExpiresAtUtc);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_MigrationSessionRecords_TotalBytes",
                "\"TotalBytes\" > 0");
            t.HasCheckConstraint(
                "CK_MigrationSessionRecords_ChunkSize",
                $"\"ChunkSize\" >= {MigrationContractLimits.MinChunkBytes} AND " +
                $"\"ChunkSize\" <= {MigrationContractLimits.MaxChunkBytes}");
            t.HasCheckConstraint(
                "CK_MigrationSessionRecords_TotalChunks",
                "\"TotalChunks\" > 0");
            t.HasCheckConstraint(
                "CK_MigrationSessionRecords_FileIdentitySize",
                "\"FileIdentitySizeBytes\" = \"TotalBytes\"");
            t.HasCheckConstraint(
                "CK_MigrationSessionRecords_ReceivedBytes",
                "\"ReceivedBytes\" >= 0 AND \"ReceivedBytes\" <= \"TotalBytes\"");
        });
    }
}

internal sealed class MigrationChunkReceiptRecordConfiguration : IEntityTypeConfiguration<MigrationChunkReceiptRecord>
{
    public void Configure(EntityTypeBuilder<MigrationChunkReceiptRecord> builder)
    {
        builder.HasKey(c => new { c.SessionId, c.ChunkIndex });
        builder.Property(c => c.Sha256).IsRequired();

        builder.HasOne<MigrationSessionRecord>()
            .WithMany()
            .HasForeignKey(c => c.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_MigrationChunkReceiptRecords_Bounds",
            "\"ChunkIndex\" >= 0 AND \"OffsetBytes\" >= 0 AND \"LengthBytes\" > 0"));
    }
}

internal sealed class MigrationExportArtifactRecordConfiguration : IEntityTypeConfiguration<MigrationExportArtifactRecord>
{
    public void Configure(EntityTypeBuilder<MigrationExportArtifactRecord> builder)
    {
        builder.HasKey(a => a.JobId);
        builder.Property(a => a.StorageKey).IsRequired();
        builder.Property(a => a.FileName).IsRequired();
        builder.Property(a => a.ContentType).IsRequired();
        builder.Property(a => a.Version).IsConcurrencyToken();

        builder.HasOne<MigrationJobRecord>()
            .WithMany()
            .HasForeignKey(a => a.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.State, a.ExpiresAtUtc });
    }
}

internal sealed class MigrationStorageReservationRecordConfiguration : IEntityTypeConfiguration<MigrationStorageReservationRecord>
{
    public void Configure(EntityTypeBuilder<MigrationStorageReservationRecord> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Version).IsConcurrencyToken();

        // Expired/unclaimed reservation sweep and capacity accounting both
        // filter on the expiry instant.
        builder.HasIndex(r => r.ExpiresAtUtc);
    }
}

internal sealed class BookUploadSessionRecordConfiguration : IEntityTypeConfiguration<BookUploadSessionRecord>
{
    public void Configure(EntityTypeBuilder<BookUploadSessionRecord> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.OwnerKey).IsRequired();
        builder.Property(s => s.FileName).IsRequired();
        builder.Property(s => s.FileSha256).IsRequired();
        builder.Property(s => s.Version).IsConcurrencyToken();

        // One active upload per (owner, book); NULL for every terminal row,
        // and SQLite allows many NULLs in a unique index.
        builder.HasIndex(s => s.ActiveSessionKey).IsUnique();
        builder.HasIndex(s => new { s.OwnerKey, s.BookId });
        builder.HasIndex(s => s.ExpiresAtUtc);

        builder.HasOne<BookModel>()
            .WithMany()
            .HasForeignKey(s => s.BookId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_BookUploadSessionRecords_Bounds",
                "\"TotalBytes\" > 0 AND \"ReceivedBytes\" >= 0 " +
                "AND \"ReceivedBytes\" <= \"TotalBytes\" " +
                "AND \"ChunkSize\" > 0 AND \"TotalChunks\" > 0");
            t.HasCheckConstraint(
                "CK_BookUploadSessionRecords_State",
                "\"State\" >= 0 AND \"State\" <= 7");
        });
    }
}

internal sealed class BookUploadPartReceiptRecordConfiguration : IEntityTypeConfiguration<BookUploadPartReceiptRecord>
{
    public void Configure(EntityTypeBuilder<BookUploadPartReceiptRecord> builder)
    {
        builder.HasKey(r => new { r.SessionId, r.PartIndex });
        builder.Property(r => r.Sha256).IsRequired();

        builder.HasOne<BookUploadSessionRecord>()
            .WithMany()
            .HasForeignKey(r => r.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_BookUploadPartReceiptRecords_Bounds",
            "\"PartIndex\" >= 0 AND \"OffsetBytes\" >= 0 AND \"LengthBytes\" > 0"));
    }
}
