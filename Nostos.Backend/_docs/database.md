# Backend — Database & Migrations

## Database Engine

Nostos uses **SQLite** via Entity Framework Core 10. The database file is `nostos.db` in the backend's working directory.

### Connection String

Configured in `Program.cs` to ensure an absolute path is used regardless of the application's working directory:

```csharp
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "nostos.db");
options.UseSqlite($"Data Source={dbPath}");
```

### Journal Mode

The database stays in SQLite's default `journal_mode=delete`. WAL was measured and left out (#1323): it would keep reads moving during a book-text write, but a copy of `nostos.db` alone then loses committed rows, and it cannot be shown safe on the network and FUSE paths that self-hosters use. `SqliteBookTextIndex.ReplaceReadyAsync` keeps its dirty pages in memory instead, so only the commit blocks readers.

## Auto-Migration

Migrations are applied automatically on startup:

```csharp
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
    db.Database.Migrate();
}
```

This means **no manual migration steps are needed** for deployment. The database schema is always up-to-date when the app starts.

## Migration History

| Migration                          | Date       | Description                                                                                        |
| ---------------------------------- | ---------- | -------------------------------------------------------------------------------------------------- |
| `AddNarratorFieldForBookMetadata1` | 2025-12-09 | Added `Narrator` field to audiobook metadata                                                       |
| `AddHarvardMetadata`               | 2025-12-09 | Added academic citation fields (editor, translator, place of publication, edition, series, volume) |
| `AddLastReadAtField`               | 2025-12-11 | Added `LastReadAt` timestamp to reading progress                                                   |
| `AddSelectedTextFieldOnNoteUpdate` | 2025-12-11 | Added `SelectedText` field to notes                                                                |
| `AddAudioBookReaderToCField`       | 2025-12-16 | Added `Narrator` field for audiobook reader                                                        |
| `RefactorBookModel`                | 2025-12-16 | Refactored book model with TPH inheritance                                                         |
| `AddLocationsJsonToBook`           | 2026-01-18 | Added `LocationsJson` for epub cached locations                                                    |
| `AddIndexes`                       | 2026-03-01 | Added performance indexes on frequently-queried columns                                            |
| `AddBackupRecords`                 | 2026-04-26 | Added `BackupRecords` table for archive tracking                                                   |
| `AddLocalArchivePath`              | 2026-04-26 | Added `LocalArchivePath` column to track backup files on disk                                      |
| `AddErrorMessageToBackupRecord`    | 2026-04-26 | Added `ErrorMessage` field for failure diagnostics                                                 |
| `RemoveGoogleDriveFields`          | 2026-04-26 | Cleaned up schema to remove deprecated cloud provider fields                                       |

## Creating New Migrations

```bash
cd Nostos.Backend
dotnet ef migrations add <MigrationName>
```

## DbContext Configuration

### TPH Discriminator (Books)

```csharp
modelBuilder.Entity<BookModel>()
    .HasDiscriminator<string>("BookType")
    .HasValue<PhysicalBookModel>("physical")
    .HasValue<EBookModel>("ebook")
    .HasValue<AudioBookModel>("audiobook");
```

### Self-Referencing Trees

**Writings:**

```csharp
modelBuilder.Entity<WritingModel>()
    .HasOne(w => w.Parent)
    .WithMany(w => w.Children)
    .HasForeignKey(w => w.ParentId)
    .OnDelete(DeleteBehavior.Cascade);
```

Cascade delete ensures deleting a folder removes all descendants.

**Collections:** Uses default EF Core conventions (no explicit cascade — books are unlinked manually before delete).

### Many-to-Many (Notes ↔ Concepts)

```csharp
modelBuilder.Entity<NoteConceptModel>().HasKey(nc => new { nc.NoteId, nc.ConceptId });
```

## Database Sets

| DbSet           | Model               | Description                  |
| --------------- | ------------------- | ---------------------------- |
| `Books`         | `BookModel` (base)  | All book types via TPH       |
| `PhysicalBooks` | `PhysicalBookModel` | Physical books only          |
| `EBooks`        | `EBookModel`        | E-books only                 |
| `AudioBooks`    | `AudioBookModel`    | Audiobooks only              |
| `Writings`      | `WritingModel`      | Writing studio files/folders |
| `Notes`         | `NoteModel`         | Book annotations             |
| `Collections`   | `CollectionModel`   | Book collections (folders)   |
| `Concepts`      | `ConceptModel`      | Auto-extracted concepts      |
| `NoteConcepts`  | `NoteConceptModel`  | Note ↔ Concept join table    |
| `BackupRecords` | `BackupRecord`      | Backup archive history       |
