using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data.Configurations;

// --- EXTERNALLY ACQUIRED BOOK PROVENANCE (issue #166) ---
// One optional row per book, in its own table: provider identity is not
// bibliographic identity, so it does not belong on Books, and it is not
// a second identity system either — matching still happens in
// ILibraryService.
internal sealed class BookAcquisitionModelConfiguration : IEntityTypeConfiguration<BookAcquisitionModel>
{
    public void Configure(EntityTypeBuilder<BookAcquisitionModel> builder)
    {
        builder.HasKey(a => a.Id);
        builder.HasOne(a => a.Book)
            .WithOne(b => b.Acquisition)
            .HasForeignKey<BookAcquisitionModel>(a => a.BookId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deterministic repeated/retried acquisition: one row per
        // (provider, item, asset). This index is what lets acquisition
        // recognise an item it has already imported and skip it entirely.
        builder.HasIndex(a => new { a.ProviderId, a.ExternalId, a.AssetId }).IsUnique();
    }
}

// --- E-READER HIGHLIGHT IMPORT (issue #656) ---
// Every FK cascades: a link or a batch row is bookkeeping about a book
// or a note and must never block deleting either.
internal sealed class NoteImportBookLinkConfiguration : IEntityTypeConfiguration<NoteImportBookLink>
{
    public void Configure(EntityTypeBuilder<NoteImportBookLink> builder)
    {
        builder.HasIndex(x => new { x.Source, x.SourceKey }).IsUnique();
        builder.HasOne(x => x.Book)
            .WithMany()
            .HasForeignKey(x => x.BookId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class NoteImportBatchConfiguration : IEntityTypeConfiguration<NoteImportBatch>
{
    public void Configure(EntityTypeBuilder<NoteImportBatch> builder)
    {
        builder.HasIndex(x => x.CreatedAtUtc);
    }
}

internal sealed class NoteImportBatchNoteConfiguration : IEntityTypeConfiguration<NoteImportBatchNote>
{
    public void Configure(EntityTypeBuilder<NoteImportBatchNote> builder)
    {
        builder.HasKey(x => new { x.BatchId, x.NoteId });
        builder.HasOne(x => x.Batch)
            .WithMany(b => b.Notes)
            .HasForeignKey(x => x.BatchId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Note)
            .WithMany()
            .HasForeignKey(x => x.NoteId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.NoteId);
    }
}

// --- READING-HISTORY IMPORT (issue #1521) ---
// A batch and its rows are bookkeeping; deleting the book it touched
// deletes the row, and deleting the batch never touches the book.
internal sealed class BookImportBatchConfiguration : IEntityTypeConfiguration<BookImportBatch>
{
    public void Configure(EntityTypeBuilder<BookImportBatch> builder)
    {
        builder.HasIndex(x => x.CreatedAtUtc);
    }
}

internal sealed class BookImportBatchBookConfiguration : IEntityTypeConfiguration<BookImportBatchBook>
{
    public void Configure(EntityTypeBuilder<BookImportBatchBook> builder)
    {
        builder.HasOne(x => x.Batch)
            .WithMany(b => b.Books)
            .HasForeignKey(x => x.BatchId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Book)
            .WithMany()
            .HasForeignKey(x => x.BookId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.BookId);
    }
}

// --- READWISE EXPORT API (issue #1525) ---
// One connection per library, pinned to a single row like the settings
// tables. The token is ciphertext written only by the connection
// service; the watermark is the last import's start in UTC.
internal sealed class ReadwiseConnectionModelConfiguration : IEntityTypeConfiguration<ReadwiseConnectionModel>
{
    public void Configure(EntityTypeBuilder<ReadwiseConnectionModel> builder)
    {
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ReadwiseConnections_SingletonId",
            $"\"Id\" = {ReadwiseConnectionModel.SingletonId}"));

        builder.Property(x => x.TokenEncrypted).IsRequired();
        builder.Property(x => x.UpdatedAfterUtc).IsRequired(false);
        builder.Property(x => x.LastImportedAtUtc).IsRequired(false);
    }
}

// --- PROVIDER ENABLEMENT (issue #774) ---
// One row per provider the user has explicitly chosen. The id is the
// registry id, which is already the durable provenance key; the CHECK
// constraint pins the same bounds the registry validates, so a direct
// write cannot create a row no provider can ever resolve.
internal sealed class ProviderPreferenceModelConfiguration : IEntityTypeConfiguration<ProviderPreferenceModel>
{
    public void Configure(EntityTypeBuilder<ProviderPreferenceModel> builder)
    {
        builder.HasKey(p => p.ProviderId);
        builder.Property(p => p.ProviderId).HasMaxLength(32);
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ProviderPreferences_ProviderId",
            "length(\"ProviderId\") > 0 AND length(\"ProviderId\") <= 32"));
    }
}
