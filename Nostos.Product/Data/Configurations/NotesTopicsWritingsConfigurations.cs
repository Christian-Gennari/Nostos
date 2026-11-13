using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data.Configurations;

internal sealed class NoteModelConfiguration : IEntityTypeConfiguration<NoteModel>
{
    public void Configure(EntityTypeBuilder<NoteModel> builder)
    {
        builder.HasIndex(n => n.BookId);

        // Capture-provenance defaults (issue #260 §2, §4). Declared in the
        // EF model, not only as CLR property initialisers, because EF
        // ignores those when emitting the AddColumn default. Existing note
        // rows must land as text/verbatim/unknown, not an empty string.
        builder.Property(n => n.CaptureSource).HasDefaultValue("text");
        builder.Property(n => n.ProcessingMode).HasDefaultValue("verbatim");
        builder.Property(n => n.SourceAnchorKind).HasDefaultValue("unknown");
        // Every note that existed before passages (issue #1014) is a note.
        builder.Property(n => n.Kind).HasDefaultValue("note");
    }
}

internal sealed class TopicModelConfiguration : IEntityTypeConfiguration<TopicModel>
{
    public void Configure(EntityTypeBuilder<TopicModel> builder)
    {
        builder.Property(t => t.Description)
            .HasMaxLength(TopicModel.MaxDescriptionLength);

        // A topic is one per name in any case: the folded key is what is unique.
        builder.HasIndex(c => c.NormalizedName).IsUnique();
    }
}

// Configure Many-to-Many for Notes <-> Topics
internal sealed class NoteTopicModelConfiguration : IEntityTypeConfiguration<NoteTopicModel>
{
    public void Configure(EntityTypeBuilder<NoteTopicModel> builder)
    {
        builder.HasKey(nc => new { nc.NoteId, nc.TopicId });

        builder.HasOne(nc => nc.Note)
            .WithMany(n => n.NoteTopics)
            .HasForeignKey(nc => nc.NoteId);

        builder.HasOne(nc => nc.Topic)
            .WithMany(c => c.NoteTopics)
            .HasForeignKey(nc => nc.TopicId);
    }
}

// Exact-once command idempotency for assistant note mutations
// (issue #260 §2, §4), with bounded inputs enforced by SQLite rather
// than the metadata-only MaxLength annotations. A separate table from
// LibraryCommandReceipts so a note command can never replay a library
// response (and vice versa). The CreatedAtUtc index backs retention
// pruning, mirroring the library receipt's CreatedAt index.
internal sealed class NoteCommandReceiptConfiguration : IEntityTypeConfiguration<NoteCommandReceipt>
{
    public void Configure(EntityTypeBuilder<NoteCommandReceipt> builder)
    {
        builder.HasIndex(x => new { x.ClientId, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => x.CreatedAtUtc);
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_NoteCommandReceipts_Bounds",
            "length(\"ClientId\") <= 64 AND length(\"IdempotencyKey\") <= 128 AND " +
            "length(\"Command\") <= 32 AND length(\"ResultJson\") <= 131072"));
    }
}

internal sealed class WritingModelConfiguration : IEntityTypeConfiguration<WritingModel>
{
    public void Configure(EntityTypeBuilder<WritingModel> builder)
    {
        builder.HasOne(w => w.Parent)
            .WithMany(w => w.Children)
            .HasForeignKey(w => w.ParentId)
            .OnDelete(DeleteBehavior.Cascade); // If you delete a folder, delete its contents

        builder.HasIndex(w => w.ParentId);

        builder.Property(w => w.PageSetup)
            .HasMaxLength(WritingModel.MaxPageSetupLength);
    }
}

// --- WRITING ↔ CHOSEN SOURCE NOTES MEMBERSHIP ---
// Both sides cascade: deleting a writing (or folder) removes membership
// rows; deleting a note removes its membership rows; and deleting a book
// cascades to its notes, which would throw an FK violation if NoteId were
// restrictive. NoteTopicModel is the precedent here.
internal sealed class WritingNoteModelConfiguration : IEntityTypeConfiguration<WritingNoteModel>
{
    public void Configure(EntityTypeBuilder<WritingNoteModel> builder)
    {
        builder.HasKey(wn => new { wn.WritingId, wn.NoteId });

        builder.HasOne(wn => wn.Writing)
            .WithMany()
            .HasForeignKey(wn => wn.WritingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(wn => wn.Note)
            .WithMany()
            .HasForeignKey(wn => wn.NoteId)
            .OnDelete(DeleteBehavior.Cascade);

        // The PK backs WritingId-first lookups; this index backs NoteId lookups.
        builder.HasIndex(wn => wn.NoteId);
    }
}

// --- WRITING → CITED NOTES (derived index, issue #1162) ---
// Rebuilt from the writing's content on every save; never portable.
// Both sides cascade for the same reasons as WritingNotes above:
// deleting a writing or folder removes its rows, and deleting a note
// (or the book whose notes cascade) removes the rows that cite it, so
// no orphan row can outlive either end.
internal sealed class WritingCitationModelConfiguration : IEntityTypeConfiguration<WritingCitationModel>
{
    public void Configure(EntityTypeBuilder<WritingCitationModel> builder)
    {
        builder.HasKey(wc => new { wc.WritingId, wc.NoteId });

        builder.HasOne(wc => wc.Writing)
            .WithMany()
            .HasForeignKey(wc => wc.WritingId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(wc => wc.Note)
            .WithMany()
            .HasForeignKey(wc => wc.NoteId)
            .OnDelete(DeleteBehavior.Cascade);

        // The PK backs WritingId-first lookups; this index backs the topic
        // page's NoteId-first join ("which writings cite these notes").
        builder.HasIndex(wc => wc.NoteId);
    }
}
