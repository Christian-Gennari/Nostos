using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.SavePipeline;

namespace Nostos.Backend.Data;

public class NostosDbContext : DbContext
{
    private readonly PortableSavePipeline _savePipeline = new();

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

    /// <summary>
    /// Every host's contexts announce committed revision advances to the
    /// library change feed, however the host builds its options.
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(LibraryChangeCommitInterceptor.Instance);
        base.OnConfiguring(optionsBuilder);
    }

    public DbSet<BookModel> Books => Set<BookModel>();
    public DbSet<AlignmentAnchorModel> AlignmentAnchors => Set<AlignmentAnchorModel>();
    public DbSet<WorkModel> Works => Set<WorkModel>();
    public DbSet<WritingModel> Writings => Set<WritingModel>();
    public DbSet<WritingNoteModel> WritingNotes => Set<WritingNoteModel>();
    public DbSet<WritingCitationModel> WritingCitations => Set<WritingCitationModel>();

    public DbSet<NoteModel> Notes => Set<NoteModel>();
    public DbSet<CollectionModel> Collections => Set<CollectionModel>();
    public DbSet<BookCollectionModel> BookCollections => Set<BookCollectionModel>();
    public DbSet<ReadingListEntryModel> ReadingListEntries => Set<ReadingListEntryModel>();
    public DbSet<TopicModel> Topics => Set<TopicModel>();
    public DbSet<NoteTopicModel> NoteTopics => Set<NoteTopicModel>();
    public DbSet<BackupRecord> BackupRecords => Set<BackupRecord>();

    // Provenance for externally acquired books (issue #166). Generic columns
    // only — see BookAcquisitionModel.
    public DbSet<BookAcquisitionModel> BookAcquisitions => Set<BookAcquisitionModel>();

    // Media read in place from an administrator-mounted folder (issue #843).
    // Host-local references, not portable library content.
    public DbSet<ExternalMediaLinkModel> ExternalMediaLinks => Set<ExternalMediaLinkModel>();

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

    // Reading-history import (issue #1521): one Goodreads or StoryGraph export
    // per batch, with a row of what each import row changed so it can be undone.
    public DbSet<BookImportBatch> BookImportBatches => Set<BookImportBatch>();
    public DbSet<BookImportBatchBook> BookImportBatchBooks => Set<BookImportBatchBook>();

    // Server-wide AI provider overrides (assistant-milestone plan, "AI provider
    // settings"). One row; NULL columns fall back to appsettings/env.
    public DbSet<AiProviderSettingsModel> AiProviderSettings => Set<AiProviderSettingsModel>();

    // The assistant choices the owner makes once (issue #262 §7), kept in their
    // own row: the provider table above holds provider configuration and
    // encrypted keys, so reusing it would make its name a lie.
    public DbSet<AssistantSettingsModel> AssistantSettings => Set<AssistantSettingsModel>();

    // Explicit user choices about which free book sources participate in
    // discovery and acquisition (issue #774). One row per chosen provider;
    // absence means "use the provider's declared default".
    public DbSet<ProviderPreferenceModel> ProviderPreferences => Set<ProviderPreferenceModel>();

    // Durable operational transfer records for library migration jobs
    // (issue #679). Host-local state — leases, sessions, chunk receipts,
    // export artifacts and storage reservations — explicitly excluded from
    // portable archives.
    public DbSet<MigrationJobRecord> MigrationJobRecords => Set<MigrationJobRecord>();
    public DbSet<MigrationSessionRecord> MigrationSessionRecords => Set<MigrationSessionRecord>();
    public DbSet<MigrationChunkReceiptRecord> MigrationChunkReceiptRecords => Set<MigrationChunkReceiptRecord>();
    public DbSet<MigrationExportArtifactRecord> MigrationExportArtifactRecords => Set<MigrationExportArtifactRecord>();
    public DbSet<MigrationStorageReservationRecord> MigrationStorageReservations => Set<MigrationStorageReservationRecord>();

    // Resumable book-file uploads (#1266). Host-local operational state: the
    // book row stays visible while a file is arriving, and this session is the
    // only record that it is in progress.
    public DbSet<BookUploadSessionRecord> BookUploadSessionRecords => Set<BookUploadSessionRecord>();
    public DbSet<BookUploadPartReceiptRecord> BookUploadPartReceiptRecords => Set<BookUploadPartReceiptRecord>();

    // The library's Readwise export-API connection (issue #1525). Host-local
    // credential and import watermark: the token is Data-Protection
    // ciphertext, never returned, and never part of a portable archive.
    public DbSet<ReadwiseConnectionModel> ReadwiseConnections => Set<ReadwiseConnectionModel>();

    // SelfHosted access tokens for the MCP endpoint (issue #1469). Host-local
    // credentials: only a hash is stored, never the token, and they are never
    // part of a portable archive.
    public DbSet<McpAccessTokenModel> McpAccessTokens => Set<McpAccessTokenModel>();

    // SelfHosted OAuth for claude.ai and ChatGPT (issue #1470): one grant per
    // approved app, and its access and refresh tokens as hashes.
    public DbSet<McpOAuthGrantModel> McpOAuthGrants => Set<McpOAuthGrantModel>();
    public DbSet<McpOAuthTokenModel> McpOAuthTokens => Set<McpOAuthTokenModel>();

    // Carrel Sync Protocol v1 (WP8): paired writing devices, pairing attempts,
    // the file ↔ Writing Studio draft map, the library's exported-note memory
    // and the two opaque change cursors. Host-local operational state: never
    // part of a portable archive. Only the device token's hash is kept.
    public DbSet<CarrelDeviceModel> CarrelDevices => Set<CarrelDeviceModel>();
    public DbSet<CarrelPairingModel> CarrelPairings => Set<CarrelPairingModel>();
    public DbSet<CarrelFileModel> CarrelFiles => Set<CarrelFileModel>();
    public DbSet<CarrelLibraryItemModel> CarrelLibraryItems => Set<CarrelLibraryItemModel>();
    public DbSet<CarrelSyncStateModel> CarrelSyncStates => Set<CarrelSyncStateModel>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        _savePipeline.SaveChanges(this, acceptAllChangesOnSuccess, base.SaveChanges);

    public override int SaveChanges() => SaveChanges(acceptAllChangesOnSuccess: true);

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default) =>
        _savePipeline.SaveChangesAsync(
            this,
            acceptAllChangesOnSuccess,
            (accept, ct) => base.SaveChangesAsync(accept, ct),
            cancellationToken);

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);

    internal bool TryGetCachedLibraryRevision(out string value) =>
        _savePipeline.TryGetCachedLibraryRevision(this, out value);

    internal void MarkLibraryRevisionAdvanced(IDbContextTransaction? transaction, string value) =>
        _savePipeline.MarkLibraryRevisionAdvanced(transaction, value);

    internal void ClearLibraryRevisionCache() =>
        _savePipeline.ClearLibraryRevisionCache();

    internal void NoteLibraryChangeKind(bool positionOnly) =>
        _savePipeline.NoteLibraryChangeKind(this, positionOnly);

    internal bool TryTakeCommittedLibraryChange(Guid transactionId, out LibraryChange change) =>
        _savePipeline.TryTakeCommittedLibraryChange(transactionId, out change);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeValueConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeValueConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NostosDbContext).Assembly);
    }
}
