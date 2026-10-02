using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;

namespace Nostos.Backend.Data;

public class NostosDbContext : DbContext
{
    public NostosDbContext(DbContextOptions<NostosDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Provider-specific migration contexts reuse the exact Nostos model while
    /// carrying their own DbContext type and migration history.
    /// </summary>
    protected NostosDbContext(DbContextOptions options)
        : base(options)
    {
    }
    public DbSet<BookModel> Books => Set<BookModel>();
    public DbSet<WorkModel> Works => Set<WorkModel>();
    public DbSet<WritingModel> Writings => Set<WritingModel>();
    public DbSet<WritingNoteModel> WritingNotes => Set<WritingNoteModel>();

    public DbSet<NoteModel> Notes => Set<NoteModel>();
    public DbSet<CollectionModel> Collections => Set<CollectionModel>();
    public DbSet<BookCollectionModel> BookCollections => Set<BookCollectionModel>();
    public DbSet<ConceptModel> Concepts => Set<ConceptModel>();
    public DbSet<NoteConceptModel> NoteConcepts => Set<NoteConceptModel>();
    public DbSet<BackupRecord> BackupRecords => Set<BackupRecord>();

    // Provenance for externally acquired books (issue #166). Generic columns
    // only — see BookAcquisitionModel.
    public DbSet<BookAcquisitionModel> BookAcquisitions => Set<BookAcquisitionModel>();

    // Register Library domain (issue #34)
    public DbSet<LibraryCommandReceipt> LibraryCommandReceipts => Set<LibraryCommandReceipt>();
    public DbSet<LibraryState> LibraryStates => Set<LibraryState>();

    // Exact-once command record for assistant note mutations (issue #260 §2, §4).
    public DbSet<NoteCommandReceipt> NoteCommandReceipts => Set<NoteCommandReceipt>();

    // E-reader highlight import (issue #656): remembered device-book -> library
    // -book decisions, and per-file batches so an import can be undone.
    public DbSet<NoteImportBookLink> NoteImportBookLinks => Set<NoteImportBookLink>();
    public DbSet<NoteImportBatch> NoteImportBatches => Set<NoteImportBatch>();
    public DbSet<NoteImportBatchNote> NoteImportBatchNotes => Set<NoteImportBatchNote>();

    // Server-wide AI provider overrides (assistant-milestone plan, "AI provider
    // settings"). One row; NULL columns fall back to appsettings/env.
    public DbSet<AiProviderSettingsModel> AiProviderSettings => Set<AiProviderSettingsModel>();

    // The assistant choices the owner makes once (issue #262 §7), kept in their
    // own row: the provider table above holds provider configuration and
    // encrypted keys, so reusing it would make its name a lie.
    public DbSet<AssistantSettingsModel> AssistantSettings => Set<AssistantSettingsModel>();

    // A few legacy import/repository paths still add a BookModel directly.
    // Keep those writes valid now that WorkId is a required foreign key. The
    // library service always assigns the work explicitly; this is only a
    // compatibility guard for rows that arrive with the old default value.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AssignMissingWorks();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override int SaveChanges() => SaveChanges(acceptAllChangesOnSuccess: true);

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        return SaveChangesAsyncCore(acceptAllChangesOnSuccess, cancellationToken);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsyncCore(acceptAllChangesOnSuccess: true, cancellationToken);

    private async Task<int> SaveChangesAsyncCore(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        await AssignMissingWorksAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void AssignMissingWorks()
    {
        var pending = MissingWorkBooks();
        if (pending.Count == 0)
            return;

        AssignMissingWorks(pending, KnownWorks());
    }

    private async Task AssignMissingWorksAsync(CancellationToken cancellationToken)
    {
        var pending = MissingWorkBooks();
        if (pending.Count == 0)
            return;

        AssignMissingWorks(pending, await KnownWorksAsync(cancellationToken));
    }

    private void AssignMissingWorks(List<BookModel> pending, List<WorkModel> works)
    {
        foreach (var book in pending)
        {
            var normalizedTitle = BookIdentityNormalizer.NormalizeTitle(book.Title);
            var normalizedAuthor = BookIdentityNormalizer.NormalizeAuthor(book.Author);
            var work = works.FirstOrDefault(w =>
                w.NormalizedTitle == normalizedTitle &&
                w.NormalizedAuthor == normalizedAuthor);

            if (work is null)
            {
                work = new WorkModel
                {
                    Title = book.Title,
                    Author = book.Author,
                    NormalizedTitle = normalizedTitle,
                    NormalizedAuthor = normalizedAuthor,
                    CreatedAt = book.CreatedAt,
                };
                Works.Add(work);
                works.Add(work);
            }

            book.Work = work;
            book.WorkId = work.Id;
        }
    }

    private List<WorkModel> KnownWorks()
    {
        var works = TrackedWorks();
        AddMissingWorks(works, Works.ToList());
        return works;
    }

    private async Task<List<WorkModel>> KnownWorksAsync(CancellationToken cancellationToken)
    {
        var works = TrackedWorks();
        AddMissingWorks(works, await Works.ToListAsync(cancellationToken));
        return works;
    }

    private List<WorkModel> TrackedWorks() =>
        ChangeTracker.Entries<WorkModel>()
            .Where(entry => entry.State != EntityState.Deleted)
            .Select(entry => entry.Entity)
            .ToList();

    private static void AddMissingWorks(List<WorkModel> target, IEnumerable<WorkModel> source)
    {
        var knownIds = target.Select(work => work.Id).ToHashSet();
        foreach (var work in source)
        {
            if (knownIds.Add(work.Id))
                target.Add(work);
        }
    }

    private List<BookModel> MissingWorkBooks() => ChangeTracker.Entries<BookModel>()
        .Where(entry => entry.State == EntityState.Added && entry.Entity.WorkId == Guid.Empty)
        .Select(entry => entry.Entity)
        .ToList();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeValueConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeValueConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // --- POLYMORPHIC CONFIGURATION ---
        modelBuilder
            .Entity<BookModel>()
            .HasDiscriminator<string>("BookType") // Creates a hidden column 'BookType'
            .HasValue<PhysicalBookModel>("physical")
            .HasValue<EBookModel>("ebook")
            .HasValue<AudioBookModel>("audiobook");

        modelBuilder
            .Entity<WritingModel>()
            .HasOne(w => w.Parent)
            .WithMany(w => w.Children)
            .HasForeignKey(w => w.ParentId)
            .OnDelete(DeleteBehavior.Cascade); // If you delete a folder, delete its contents

        // Configure Many-to-Many for Notes <-> Concepts
        modelBuilder.Entity<NoteConceptModel>().HasKey(nc => new { nc.NoteId, nc.ConceptId });

        modelBuilder
            .Entity<NoteConceptModel>()
            .HasOne(nc => nc.Note)
            .WithMany(n => n.NoteConcepts)
            .HasForeignKey(nc => nc.NoteId);

        modelBuilder
            .Entity<NoteConceptModel>()
            .HasOne(nc => nc.Concept)
            .WithMany(c => c.NoteConcepts)
            .HasForeignKey(nc => nc.ConceptId);

        // --- INDEXES ---
        modelBuilder.Entity<BookModel>().HasIndex(b => b.Title);

        modelBuilder.Entity<BookModel>().HasIndex(b => b.Author);

        modelBuilder.Entity<WorkModel>(b =>
        {
            b.HasKey(w => w.Id);
            b.Property(w => w.Title).IsRequired();
            b.Property(w => w.NormalizedTitle).IsRequired();
            b.HasIndex(w => w.NormalizedTitle);
            b.HasIndex(w => w.NormalizedAuthor);
        });

        modelBuilder.Entity<BookModel>(b =>
        {
            b.HasOne(bm => bm.Work)
                .WithMany(w => w.Books)
                .HasForeignKey(bm => bm.WorkId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(bm => bm.WorkId);
        });

        // Normalized identity uniqueness (filtered: NULLs are unlimited).
        modelBuilder.Entity<BookModel>()
            .HasIndex(b => b.NormalizedIsbn)
            .IsUnique()
            .HasFilter("\"NormalizedIsbn\" IS NOT NULL");
        modelBuilder.Entity<BookModel>()
            .HasIndex(b => b.NormalizedAsin)
            .IsUnique()
            .HasFilter("\"NormalizedAsin\" IS NOT NULL");

        modelBuilder.Entity<NoteModel>(e =>
        {
            e.HasIndex(n => n.BookId);

            // Capture-provenance defaults (issue #260 §2, §4). Declared in the
            // EF model, not only as CLR property initialisers, because EF
            // ignores those when emitting the AddColumn default. Existing note
            // rows must land as text/verbatim/unknown, not an empty string.
            e.Property(n => n.CaptureSource).HasDefaultValue("text");
            e.Property(n => n.ProcessingMode).HasDefaultValue("verbatim");
            e.Property(n => n.SourceAnchorKind).HasDefaultValue("unknown");
        });

        modelBuilder.Entity<CollectionModel>().HasIndex(c => c.ParentId);

        // --- COLLECTIONS PHASE 1: EXPLICIT RESTRICTIVE FKs ---
        // A collection that has children or books must never be deleted by
        // a raw SQL DELETE: the database itself rejects it (the service
        // unlinks books and refuses children first with typed codes).
        modelBuilder.Entity<CollectionModel>()
            .HasOne(c => c.Parent)
            .WithMany(c => c.Children)
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        // --- MULTI-COLLECTION MEMBERSHIP (single source of truth) ---
        // A book may belong to many collections. This join table is the ONLY
        // place membership lives — the former Books.CollectionId single-value
        // column was dropped, because a one-slot column cannot represent the
        // model and keeping it in sync was a standing source of drift.
        modelBuilder.Entity<BookCollectionModel>()
            .HasKey(bc => new { bc.BookId, bc.CollectionId });

        modelBuilder.Entity<BookCollectionModel>()
            .HasOne(bc => bc.Book)
            .WithMany(b => b.BookCollections)
            .HasForeignKey(bc => bc.BookId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<BookCollectionModel>()
            .HasOne(bc => bc.Collection)
            .WithMany()
            .HasForeignKey(bc => bc.CollectionId)
            .OnDelete(DeleteBehavior.Restrict);

        // The PK covers BookId-first lookups ("which collections is this book
        // in"); this index backs the inverse ("which books are in this
        // collection"), which the sidebar counts and the subtree filter use.
        modelBuilder.Entity<BookCollectionModel>().HasIndex(bc => bc.CollectionId);

        modelBuilder.Entity<WritingModel>().HasIndex(w => w.ParentId);

        // --- WRITING ↔ CHOSEN SOURCE NOTES MEMBERSHIP ---
        // Both sides cascade: deleting a writing (or folder) removes membership
        // rows; deleting a note removes its membership rows; and deleting a book
        // cascades to its notes, which would throw an FK violation if NoteId were
        // restrictive. NoteConceptModel is the precedent here.
        modelBuilder.Entity<WritingNoteModel>(e =>
        {
            e.HasKey(wn => new { wn.WritingId, wn.NoteId });

            e.HasOne(wn => wn.Writing)
                .WithMany()
                .HasForeignKey(wn => wn.WritingId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(wn => wn.Note)
                .WithMany()
                .HasForeignKey(wn => wn.NoteId)
                .OnDelete(DeleteBehavior.Cascade);

            // The PK backs WritingId-first lookups; this index backs NoteId lookups.
            e.HasIndex(wn => wn.NoteId);
        });

        // --- EXTERNALLY ACQUIRED BOOK PROVENANCE (issue #166) ---
        // One optional row per book, in its own table: provider identity is not
        // bibliographic identity, so it does not belong on Books, and it is not
        // a second identity system either — matching still happens in
        // ILibraryService.
        modelBuilder.Entity<BookAcquisitionModel>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasOne(a => a.Book)
                .WithOne(b => b.Acquisition)
                .HasForeignKey<BookAcquisitionModel>(a => a.BookId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deterministic repeated/retried acquisition: one row per
            // (provider, item, asset). This index is what lets acquisition
            // recognise an item it has already imported and skip it entirely.
            e.HasIndex(a => new { a.ProviderId, a.ExternalId, a.AssetId }).IsUnique();
        });

        modelBuilder.Entity<ConceptModel>().HasIndex(c => c.Concept).IsUnique();

        // Idempotent receipt with bounded inputs (SQLite enforces the limits
        // via the CHECK constraint, not the metadata-only MaxLength). The
        // CreatedAt index backs the retention prune (issue #51).
        modelBuilder.Entity<LibraryCommandReceipt>(e =>
        {
            e.HasIndex(x => new { x.ClientId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_LibraryCommandReceipts_Bounds",
                "length(\"ClientId\") <= 64 AND length(\"IdempotencyKey\") <= 128 AND " +
                "length(\"CommandKind\") <= 32 AND length(\"ResponseJson\") <= 131072"));
        });

        // --- LIBRARY DOMAIN (issue #34) ---

        // Singleton library state row: exactly one LibraryState. The fixed
        // sentinel is enforced by a CHECK constraint; the unique index on
        // SingletonSlot then allows at most one row.
        modelBuilder.Entity<LibraryState>(e =>
        {
            e.HasIndex(s => s.SingletonSlot).IsUnique();
            e.ToTable(t => t.HasCheckConstraint(
                "CK_LibraryStates_SingletonSlot",
                $"\"SingletonSlot\" = {LibraryState.SingletonSentinel}"));
        });

        // Exact-once command idempotency for assistant note mutations
        // (issue #260 §2, §4), with bounded inputs enforced by SQLite rather
        // than the metadata-only MaxLength annotations. A separate table from
        // LibraryCommandReceipts so a note command can never replay a library
        // response (and vice versa). The CreatedAtUtc index backs retention
        // pruning, mirroring the library receipt's CreatedAt index.
        modelBuilder.Entity<NoteCommandReceipt>(e =>
        {
            e.HasIndex(x => new { x.ClientId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => x.CreatedAtUtc);
            e.ToTable(t => t.HasCheckConstraint(
                "CK_NoteCommandReceipts_Bounds",
                "length(\"ClientId\") <= 64 AND length(\"IdempotencyKey\") <= 128 AND " +
                "length(\"Command\") <= 32 AND length(\"ResultJson\") <= 131072"));
        });

        // --- E-READER HIGHLIGHT IMPORT (issue #656) ---
        // Every FK cascades: a link or a batch row is bookkeeping about a book
        // or a note and must never block deleting either.
        modelBuilder.Entity<NoteImportBookLink>(e =>
        {
            e.HasIndex(x => new { x.Source, x.SourceKey }).IsUnique();
            e.HasOne(x => x.Book)
                .WithMany()
                .HasForeignKey(x => x.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NoteImportBatch>().HasIndex(x => x.CreatedAtUtc);

        modelBuilder.Entity<NoteImportBatchNote>(e =>
        {
            e.HasKey(x => new { x.BatchId, x.NoteId });
            e.HasOne(x => x.Batch)
                .WithMany(b => b.Notes)
                .HasForeignKey(x => x.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Note)
                .WithMany()
                .HasForeignKey(x => x.NoteId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.NoteId);
        });

        // --- AI PROVIDER SETTINGS (assistant-milestone plan) ---
        // A single server-wide settings row. The primary key is fixed and a
        // CHECK constraint pins it, so no second row can ever be written. Every
        // value column is nullable: NULL means "no override", which is what lets
        // an explicit `enabled: false` be told apart from "never set".
        modelBuilder.Entity<AiProviderSettingsModel>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint(
                "CK_AiProviderSettings_SingletonId",
                $"\"Id\" = {AiProviderSettingsModel.SingletonId}"));
        });

        // --- ASSISTANT SETTINGS (issue #262 §7) ---
        // The same one-row shape as the provider settings above: a fixed primary
        // key pinned by a CHECK constraint, so a second row can never be written.
        // The value column is nullable: NULL means "never chosen", which is what
        // keeps a stored `verbatim` distinguishable from the default.
        modelBuilder.Entity<AssistantSettingsModel>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint(
                "CK_AssistantSettings_SingletonId",
                $"\"Id\" = {AssistantSettingsModel.SingletonId}"));
        });
    }
}
