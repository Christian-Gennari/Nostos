using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data.Configurations;

internal sealed class BookModelConfiguration : IEntityTypeConfiguration<BookModel>
{
    public void Configure(EntityTypeBuilder<BookModel> builder)
    {
        // --- POLYMORPHIC CONFIGURATION ---
        builder
            .HasDiscriminator<string>("BookType") // Creates a hidden column 'BookType'
            .HasValue<PhysicalBookModel>("physical")
            .HasValue<EBookModel>("ebook")
            .HasValue<AudioBookModel>("audiobook");

        // --- INDEXES ---
        builder.HasIndex(b => b.Title);
        builder.HasIndex(b => b.Author);

        builder.HasOne(bm => bm.Work)
            .WithMany(w => w.Books)
            .HasForeignKey(bm => bm.WorkId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(bm => bm.WorkId);

        // The book's rating, favourite and review live on the work, so an
        // edition read without it would silently report rating 0.
        builder.Navigation(bm => bm.Work).AutoInclude();

        // Normalized identity uniqueness (filtered: NULLs are unlimited).
        builder.HasIndex(b => b.NormalizedIsbn)
            .IsUnique()
            .HasFilter("\"NormalizedIsbn\" IS NOT NULL");
        builder.HasIndex(b => b.NormalizedAsin)
            .IsUnique()
            .HasFilter("\"NormalizedAsin\" IS NOT NULL");
    }
}

internal sealed class WorkModelConfiguration : IEntityTypeConfiguration<WorkModel>
{
    public void Configure(EntityTypeBuilder<WorkModel> builder)
    {
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Title).IsRequired();
        builder.Property(w => w.NormalizedTitle).IsRequired();
        builder.HasIndex(w => w.NormalizedTitle);
        builder.HasIndex(w => w.NormalizedAuthor);
    }
}

// --- DERIVED AUDIOBOOK / EPUB ALIGNMENT ANCHORS ---
// Both foreign keys refer to the same TPH book table. Database-level
// cascade delete removes anchors when either edition is deleted.
// Anchors are derived and excluded from portable library state.
internal sealed class AlignmentAnchorModelConfiguration : IEntityTypeConfiguration<AlignmentAnchorModel>
{
    public void Configure(EntityTypeBuilder<AlignmentAnchorModel> builder)
    {
        builder.HasKey(a => a.Id);

        builder.HasOne(a => a.AudioBook)
            .WithMany()
            .HasForeignKey(a => a.AudioBookId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.TextBook)
            .WithMany()
            .HasForeignKey(a => a.TextBookId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(a => a.AudioRevision)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(a => a.TextRevision)
            .IsRequired()
            .HasMaxLength(512);

        builder.Property(a => a.ResourceHref)
            .IsRequired()
            .HasMaxLength(2048);

        builder.Property(a => a.Cfi)
            .HasMaxLength(2048);

        // Explicit index names: the generated ones exceed PostgreSQL's
        // 63-character identifier limit and would collide once truncated.
        builder.HasIndex(a => new
        {
            a.AudioBookId,
            a.TextBookId,
            a.AudioRevision,
            a.TextRevision,
            a.AudioPositionMs,
        }).HasDatabaseName("IX_AlignmentAnchors_Pair_AudioPosition");

        builder.HasIndex(a => new
        {
            a.AudioBookId,
            a.TextBookId,
            a.AudioRevision,
            a.TextRevision,
            a.TextGlobalOffset,
        }).HasDatabaseName("IX_AlignmentAnchors_Pair_TextOffset");
    }
}

// --- COLLECTIONS PHASE 1: EXPLICIT RESTRICTIVE FKs ---
// A collection that has children or books must never be deleted by
// a raw SQL DELETE: the database itself rejects it (the service
// unlinks books and refuses children first with typed codes).
internal sealed class CollectionModelConfiguration : IEntityTypeConfiguration<CollectionModel>
{
    public void Configure(EntityTypeBuilder<CollectionModel> builder)
    {
        builder.HasIndex(c => c.ParentId);

        builder.HasOne(c => c.Parent)
            .WithMany(c => c.Children)
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

// --- MULTI-COLLECTION MEMBERSHIP (single source of truth) ---
// A book may belong to many collections. This join table is the ONLY
// place membership lives — the former Books.CollectionId single-value
// column was dropped, because a one-slot column cannot represent the
// model and keeping it in sync was a standing source of drift.
internal sealed class BookCollectionModelConfiguration : IEntityTypeConfiguration<BookCollectionModel>
{
    public void Configure(EntityTypeBuilder<BookCollectionModel> builder)
    {
        builder.HasKey(bc => new { bc.BookId, bc.CollectionId });

        builder.HasOne(bc => bc.Book)
            .WithMany(b => b.BookCollections)
            .HasForeignKey(bc => bc.BookId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(bc => bc.Collection)
            .WithMany()
            .HasForeignKey(bc => bc.CollectionId)
            .OnDelete(DeleteBehavior.Restrict);

        // The PK covers BookId-first lookups ("which collections is this book
        // in"); this index backs the inverse ("which books are in this
        // collection"), which the sidebar counts and the subtree filter use.
        builder.HasIndex(bc => bc.CollectionId);
    }
}

// --- READING LIST ---
// Wishes for books the user does not have. No FK to Books on purpose:
// an entry is not a book, and it is removed when it becomes one.
internal sealed class ReadingListEntryModelConfiguration : IEntityTypeConfiguration<ReadingListEntryModel>
{
    public void Configure(EntityTypeBuilder<ReadingListEntryModel> builder)
    {
        builder.ToTable("ReadingListEntries");
        builder.HasIndex(x => x.NextPosition);
        builder.HasIndex(x => x.CreatedAt);
    }
}

// --- EXTERNAL MEDIA REFERENCES (issue #843) ---
// Deleting a book deletes its references and nothing else: the files
// they point at are not Nostos's to remove.
internal sealed class ExternalMediaLinkModelConfiguration : IEntityTypeConfiguration<ExternalMediaLinkModel>
{
    public void Configure(EntityTypeBuilder<ExternalMediaLinkModel> builder)
    {
        builder.HasKey(l => l.Id);
        builder.HasOne(l => l.Book)
            .WithMany()
            .HasForeignKey(l => l.BookId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Property(l => l.RelativePath).HasMaxLength(ExternalMediaLinkModel.MaxRelativePathLength);

        // One reference per file slot of a book, and one book per file:
        // the second index is what makes a repeated import idempotent.
        builder.HasIndex(l => new { l.BookId, l.TrackNumber }).IsUnique();
        builder.HasIndex(l => new { l.RootId, l.RelativePath }).IsUnique();
    }
}

// Idempotent receipt with bounded inputs (SQLite enforces the limits
// via the CHECK constraint, not the metadata-only MaxLength). The
// CreatedAt index backs the retention prune (issue #51).
internal sealed class LibraryCommandReceiptConfiguration : IEntityTypeConfiguration<LibraryCommandReceipt>
{
    public void Configure(EntityTypeBuilder<LibraryCommandReceipt> builder)
    {
        builder.HasIndex(x => new { x.ClientId, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => x.CreatedAt);
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_LibraryCommandReceipts_Bounds",
            "length(\"ClientId\") <= 64 AND length(\"IdempotencyKey\") <= 128 AND " +
            "length(\"CommandKind\") <= 32 AND length(\"ResponseJson\") <= 131072"));
    }
}

// --- LIBRARY DOMAIN (issue #34) ---
// Singleton library state row: exactly one LibraryState. The fixed
// sentinel is enforced by a CHECK constraint; the unique index on
// SingletonSlot then allows at most one row.
internal sealed class LibraryStateConfiguration : IEntityTypeConfiguration<LibraryState>
{
    public void Configure(EntityTypeBuilder<LibraryState> builder)
    {
        builder.HasIndex(s => s.SingletonSlot).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_LibraryStates_SingletonSlot",
            $"\"SingletonSlot\" = {LibraryState.SingletonSentinel}"));
    }
}
