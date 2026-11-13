# Backend — Database & Migrations

## Database Engine

Nostos uses **SQLite** via Entity Framework Core 10. The database file defaults
to `nostos.db` beside the content root, and `Persistence:DatabasePath` moves it
(see below).

SQLite is the only runtime provider with a migration chain in this repository.
The hosted product (PostgreSQL) lives in its own repository and owns its
migration history; the SQL in migrations here is nevertheless written to be
valid on both SQLite and PostgreSQL wherever the two agree, so a hosted copy of a
data fix stays a translation rather than a rewrite. `PostgreSqlCompatibilitySpikeTests`
keeps the model itself portable.

### Connection String

Resolved by `Nostos.Backend/Configuration/PersistenceRegistration.cs`, not in
`Program.cs`. `Persistence:DatabasePath` wins when set — an absolute path allows
a dedicated volume such as `/data/nostos.db`, and a relative one is resolved
from the content root. Unset, the historical `<contentRoot>/nostos.db` location
is kept. The parent directory is created, and the migrations assembly is pinned
to the backend assembly:

```csharp
var dbPath = ResolveDatabasePath(
    configuration[DatabasePathConfigurationKey],   // "Persistence:DatabasePath"
    contentRootPath);
options.UseSqlite(
    $"Data Source={fullPath}",
    sqlite => sqlite.MigrationsAssembly(typeof(PersistenceRegistration).Assembly.FullName));
```

The same path drives backup-settings storage, backup restore, and the
activation/recovery layout, so all of them follow a configured data root.

### Journal Mode

The database stays in SQLite's default `journal_mode=delete`. WAL was measured and left out (#1323): it would keep reads moving during a book-text write, but a copy of `nostos.db` alone then loses committed rows, and it cannot be shown safe on the network and FUSE paths that self-hosters use. `SqliteBookTextIndex.ReplaceReadyAsync` keeps its dirty pages in memory instead, so only the commit blocks readers.

## Startup Migration

`Nostos.Backend/Data/DatabaseBootstrapService.cs` owns the database lifecycle
and is called once from `InitializeSelfHostedHostAsync`
(`Nostos.Backend/Composition/SelfHostedHttpPipeline.cs`) before the host serves
traffic:

- A **truly empty** database (new file, or a file with no tables) is built from
  the complete current EF model and stamped with an accurate migration-history
  baseline in **one transaction**, because this repository's migration chain has
  no initial baseline migration.
- An **existing** database goes through the ordinary
  `Database.MigrateAsync()` path and is never rebaselined. A partial or unknown
  schema fails closed instead of being stamped.
- After either path it backfills normalized library identity, folds topic case
  variants, rebuilds the writing-citation index, backfills note location
  anchors, and seeds the `LibraryState` singleton row.

This means **no manual migration steps are needed** for deployment.

## Migration History

The authoritative list is `Nostos.Backend/Migrations/` itself — one
`<timestamp>_<Name>.cs` plus its `.Designer.cs` per migration, alongside the
model snapshot. At the time of writing there are **63 migrations**; the newest is
`20261113123425_ConvertLegacyPhysicalBooksWithFiles`. Do not maintain a copy of
the list here: it drifts within days.

## Creating New Migrations

Migrations are added in the backend project, which is also the startup project:

```bash
dotnet ef migrations add <MigrationName> \
  --project Nostos.Backend --startup-project Nostos.Backend
```

Never invent a migration filename: let `dotnet ef` create it and then patch the
file it wrote. Tests use `EnsureCreated` and never execute migrations, so any
hand-written backfill or guard must be proven on a migrated database copy.

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

**Collections:** the `Collection` self-reference is `Restrict`, and book
membership lives in `BookCollections` with asymmetric delete behaviour — see
the next section.

### Many-to-Many (Notes ↔ Topics)

```csharp
modelBuilder.Entity<NoteTopicModel>().HasKey(nc => new { nc.NoteId, nc.TopicId });
```

### Book ↔ Collection Membership

`BookCollectionModelConfiguration` in
`Nostos.Product/Data/Configurations/LibraryCatalogConfigurations.cs` is where the
behaviour lives:

```csharp
builder.HasKey(bc => new { bc.BookId, bc.CollectionId });
builder.HasOne(bc => bc.Book).WithMany(b => b.BookCollections)
       .HasForeignKey(bc => bc.BookId).OnDelete(DeleteBehavior.Cascade);
builder.HasOne(bc => bc.Collection).WithMany()
       .HasForeignKey(bc => bc.CollectionId).OnDelete(DeleteBehavior.Restrict);
builder.HasIndex(bc => bc.CollectionId);
```

Deliberately asymmetric: `Cascade` from Book so deleting a book cannot strand
membership rows, `Restrict` from Collection so a non-empty collection cannot be
removed by raw SQL behind the service's back (the service deletes membership
rows first, then the collection). The join table is the **only** record of
membership; `Books.CollectionId` no longer exists.

## Database Sets

`NostosDbContext` (`Nostos.Product/Data/NostosDbContext.cs`) declares **42
`DbSet<>` properties**, so the class is the authoritative inventory rather than
a table here. They fall into these groups:

- **Library:** `Books`, `Works`, `AlignmentAnchors`, `Collections`,
  `BookCollections`, `ReadingListEntries`, `BookAcquisitions`,
  `ExternalMediaLinks`
- **Knowledge:** `Notes`, `Topics`, `NoteTopics`, `NoteCommandReceipts`,
  `NoteImportBookLinks`, `NoteImportBatches`, `NoteImportBatchNotes`,
  `BookImportBatches`, `BookImportBatchBooks`
- **Writing:** `Writings`, `WritingNotes`, `WritingCitations`
- **Library service:** `LibraryCommandReceipts`, `LibraryStates`
- **Operations:** `BackupRecords`, the five `Migration*` records, the two
  `BookUpload*` records, `ProviderPreferences`, `AiProviderSettings`,
  `AssistantSettings`, `ReadwiseConnections`
- **Access:** `McpAccessTokens`, `McpOAuthGrants`, `McpOAuthTokens`
- **Carrel:** `CarrelDevices`, `CarrelPairings`, `CarrelFiles`,
  `CarrelLibraryItems`, `CarrelSyncStates`

The provider-specific migration contexts reuse the same model with their own
`DbContext` type and migration history.
