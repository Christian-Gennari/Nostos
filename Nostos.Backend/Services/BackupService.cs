using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services;

public class BackupService : IBackupService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorageService _fileStorage;
    private readonly IWebHostEnvironment _env;
    private readonly BackupSettingsProvider _settingsProvider;
    private readonly ILogger<BackupService> _logger;
    private readonly string _localBackupDir;

    private BackupSettings Settings => _settingsProvider.Current;

    private string LocalBackupDir => _localBackupDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    public BackupService(
        IServiceScopeFactory scopeFactory,
        IFileStorageService fileStorage,
        IWebHostEnvironment env,
        IOptions<FileStorageOptions> storageOptions,
        BackupSettingsProvider settingsProvider,
        ILogger<BackupService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _fileStorage = fileStorage;
        _env = env;
        _settingsProvider = settingsProvider;
        _logger = logger;

        // Resolved through the same options as the book storage root, so the
        // archive location follows a relocated library instead of assuming the
        // application's own content directory. See
        // FileStorageOptions.ResolveBackupsRoot for why that matters.
        _localBackupDir = FileStorageOptions.ResolveBackupsRoot(
            env.ContentRootPath, fileStorage.StorageRoot, storageOptions.Value);
    }

    public async Task<BackupStatusDto> GetStatusAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();

        var lastRecord = await db
            .BackupRecords.OrderByDescending(b => b.CreatedAt)
            .FirstOrDefaultAsync();

        return new BackupStatusDto(
            IsEnabled: Settings.IsEnabled,
            Provider: Settings.Provider,
            LastBackupAt: lastRecord?.CreatedAt,
            LastBackupStatus: lastRecord?.Status,
            IncludeBookFiles: Settings.IncludeBookFiles,
            IntervalHours: Settings.IntervalHours,
            MaxBackups: Settings.MaxBackups
        );
    }

    public Task<BackupSettingsDto> GetSettingsAsync()
    {
        var s = Settings;
        return Task.FromResult(
            new BackupSettingsDto(
                IsEnabled: s.IsEnabled,
                Provider: s.Provider,
                IncludeBookFiles: s.IncludeBookFiles,
                IntervalHours: s.IntervalHours,
                MaxBackups: s.MaxBackups
            )
        );
    }

    public Task<BackupSettingsDto> UpdateSettingsAsync(UpdateBackupSettingsDto dto)
    {
        var updated = _settingsProvider.Update(s =>
        {
            if (dto.IsEnabled.HasValue) s.IsEnabled = dto.IsEnabled.Value;
            if (dto.Provider != null) s.Provider = dto.Provider;
            if (dto.IncludeBookFiles.HasValue) s.IncludeBookFiles = dto.IncludeBookFiles.Value;
            if (dto.IntervalHours.HasValue) s.IntervalHours = Math.Max(1, dto.IntervalHours.Value);
            if (dto.MaxBackups.HasValue) s.MaxBackups = Math.Max(1, dto.MaxBackups.Value);
        });

        return Task.FromResult(
            new BackupSettingsDto(
                IsEnabled: updated.IsEnabled,
                Provider: updated.Provider,
                IncludeBookFiles: updated.IncludeBookFiles,
                IntervalHours: updated.IntervalHours,
                MaxBackups: updated.MaxBackups
            )
        );
    }

    public async Task<TriggerBackupResultDto> CreateBackupAsync(CancellationToken ct = default)
    {
        // Non-waiting admission also permits a call from an already admitted HTTP
        // request: a closing gate cannot deadlock that request on a nested lease.
        await using var operation = _settingsProvider.Maintenance.TryEnterOperation()
            ?? throw LibraryMaintenanceCoordinator.Busy();
        _logger.LogInformation("Starting backup creation...");

        _settingsProvider.StartProgress(5);

        await MarkStaleInProgressAsync(ct);

        _settingsProvider.UpdateProgress("Preparing backup", 5, 1, 5);

        var recordId = Guid.Empty;
        var recordCreatedAt = DateTime.UtcNow;
        var recordStatus = BackupStatus.InProgress;
        var recordSizeBytes = 0L;

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            var record = new BackupRecord
            {
                IncludeBookFiles = Settings.IncludeBookFiles,
                Status = BackupStatus.InProgress,
            };
            db.BackupRecords.Add(record);
            await db.SaveChangesAsync(ct);
            recordId = record.Id;
            recordCreatedAt = record.CreatedAt;
        }

        string? archivePath = null;
        string? persistentLocalPath = null;
        try
        {
            archivePath = await BuildArchiveAsync(recordId, ct);
            _settingsProvider.UpdateProgress("Verifying backup", 80, 4, 5);

            var fileSize = new FileInfo(archivePath).Length;
            var checksum = await ComputeFileChecksumAsync(archivePath);
            _settingsProvider.UpdateProgress("Finalizing", 95, 5, 5);

            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
                var record = await db.BackupRecords.FindAsync([recordId], ct);
                if (record != null)
                {
                    record.SizeBytes = fileSize;
                    var manifest = new BackupManifestDto(
                        Version: "1",
                        Timestamp: recordCreatedAt,
                        DatabaseSizeBytes: 0,
                        BookFileCount: 0,
                        TotalSizeBytes: fileSize,
                        Checksum: checksum
                    );
                    record.ManifestJson = JsonSerializer.Serialize(manifest, JsonOpts);

                    // Always save a local copy for recoverability
                    Directory.CreateDirectory(LocalBackupDir);
                    persistentLocalPath = Path.Combine(LocalBackupDir, $"{recordId}.nostos");
                    File.Copy(archivePath, persistentLocalPath, true);
                    record.LocalArchivePath = persistentLocalPath;
                    record.Provider = BackupProvider.Local;

                    record.Status = BackupStatus.Completed;
                    recordSizeBytes = fileSize;
                    recordStatus = BackupStatus.Completed;
                    await db.SaveChangesAsync(ct);
                }
            }

            _logger.LogInformation("Backup {Id} completed ({Size} bytes)", recordId, recordSizeBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup {Id} failed", recordId);
            recordStatus = BackupStatus.Failed;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
                var record = await db.BackupRecords.FindAsync([recordId], ct);
                if (record != null)
                {
                    record.Status = BackupStatus.Failed;
                    record.ErrorMessage = ex.Message;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "Failed to persist failure status for backup {Id}", recordId);
            }
        }

        _settingsProvider.ClearProgress();

        await EnforceMaxBackupsAsync(ct);

        // Clean up temp archive (persistent copy is already in Storage/backups/)
        if (archivePath != null && File.Exists(archivePath))
        {
            try { File.Delete(archivePath); } catch { /* cleanup */ }
        }

        return new TriggerBackupResultDto(recordId, recordStatus, recordSizeBytes, recordCreatedAt);
    }

    public async Task<RestoreResultDto> RestoreBackupAsync(Guid backupId, CancellationToken ct = default)
    {
        BackupRecord? record;
        using (var operation = _settingsProvider.Maintenance.TryEnterOperation()
            ?? throw LibraryMaintenanceCoordinator.Busy())
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            record = await db.BackupRecords.FindAsync([backupId], ct);
        }

        if (record is null)
            return new RestoreResultDto(false, "Backup record not found.");

        _logger.LogInformation("Starting restore from backup {Id}...", backupId);
        _settingsProvider.StartProgress(3);
        _settingsProvider.UpdateProgress("Verifying backup", 5, 1, 3);

        string? archivePath = null;
        IAsyncDisposable? maintenance = null;
        try
        {
            if (!string.IsNullOrEmpty(record.LocalArchivePath) && File.Exists(record.LocalArchivePath))
            {
                archivePath = record.LocalArchivePath;
                _logger.LogInformation("Restoring from local archive: {Path}", archivePath);
            }

            if (archivePath is null)
                return new RestoreResultDto(false, "Could not retrieve backup archive. The local copy is unavailable.");

            var actualChecksum = await ComputeFileChecksumAsync(archivePath);
            var expectedChecksum = "";

            if (!string.IsNullOrEmpty(record.ManifestJson))
            {
                var dbManifest = JsonSerializer.Deserialize<BackupManifestDto>(record.ManifestJson, JsonOpts);
                expectedChecksum = dbManifest?.Checksum ?? "";
            }

            if (!string.IsNullOrEmpty(expectedChecksum) && !string.Equals(actualChecksum, expectedChecksum, StringComparison.OrdinalIgnoreCase))
                return new RestoreResultDto(false, $"Archive integrity check failed. Expected {expectedChecksum} but got {actualChecksum}.");

            _settingsProvider.UpdateProgress("Restoring data", 15, 2, 3);
            maintenance = await _settingsProvider.Maintenance.EnterExclusiveAsync(
                LibraryMaintenanceReason.BackupRestore, ct);
            _logger.LogInformation("Restore acquired exclusive library maintenance after draining operations.");

            _settingsProvider.UpdateProgress("Restoring data", 30, 2, 3);
            await RestoreFromArchiveAsync(archivePath, ct);

            _settingsProvider.UpdateProgress("Completing restore", 90, 3, 3);
            _logger.LogInformation("Restore from backup {Id} completed.", backupId);
            return new RestoreResultDto(true, "Restore completed successfully. Please restart the application to apply changes.");
        }
        catch (Portability.MigrationActivationException ex) when (ex.Code == Portability.MigrationActivationErrorCodes.Busy)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore from backup {Id} failed", backupId);
            return new RestoreResultDto(false, $"Restore failed: {ex.Message}");
        }
        finally
        {
            try
            {
                if (maintenance is not null) await maintenance.DisposeAsync();
            }
            finally { _settingsProvider.ClearProgress(); }
        }
    }

    public async Task<List<BackupHistoryDto>> GetHistoryAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();

        return await db
            .BackupRecords.OrderByDescending(b => b.CreatedAt)
            .Select(b => new BackupHistoryDto(
                b.Id,
                b.CreatedAt,
                b.SizeBytes,
                b.Provider,
                b.Status,
                b.IncludeBookFiles,
                b.ErrorMessage
            ))
            .ToListAsync();
    }

    public async Task DeleteBackupRecordAsync(Guid id)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();

        var record = await db.BackupRecords.FindAsync(id);
        if (record is null)
            return;

        if (!string.IsNullOrEmpty(record.LocalArchivePath) && File.Exists(record.LocalArchivePath))
        {
            DeleteArchiveIfOwned(record.LocalArchivePath);
        }

        db.BackupRecords.Remove(record);
        await db.SaveChangesAsync();
    }

    public string? GetLocalArchivePath(Guid id)
    {
        var path = Path.Combine(LocalBackupDir, $"{id}.nostos");
        return File.Exists(path) ? path : null;
    }

    public async Task<List<BackupHistoryDto>> ImportExistingBackupsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(LocalBackupDir))
            return [];

        var imported = new List<BackupHistoryDto>();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();

        var trackedPaths = await db.BackupRecords
            .Where(b => b.LocalArchivePath != null)
            .Select(b => b.LocalArchivePath!)
            .ToHashSetAsync(ct);

        foreach (var file in Directory.GetFiles(LocalBackupDir, "*.nostos"))
        {
            if (trackedPaths.Contains(file))
                continue;

            try
            {
                using var archive = ZipFile.OpenRead(file);
                var hasManifest = archive.GetEntry("manifest.json") != null;
                var hasDb = archive.GetEntry("database/nostos.db") != null;

                if (!hasDb)
                {
                    _logger.LogWarning("Skipping untracked file {File}: missing database/nostos.db", file);
                    continue;
                }

                BackupManifestDto? manifest = null;
                var manifestEntry = archive.GetEntry("manifest.json");
                if (manifestEntry != null)
                {
                    using var manifestStream = manifestEntry.Open();
                    manifest = await JsonSerializer.DeserializeAsync<BackupManifestDto>(manifestStream, JsonOpts, ct);
                }

                var fileInfo = new FileInfo(file);
                var id = Guid.Parse(Path.GetFileNameWithoutExtension(file));

                // Real creation time: prefer the manifest timestamp (the
                // archive's own record), fall back to the file's write time.
                // Defaulting to UtcNow here made every import look brand-new,
                // which broke pruner ordering and could delete recent backups.
                var createdAt = manifest is not null && manifest.Timestamp != default
                    ? manifest.Timestamp.ToUniversalTime()
                    : fileInfo.LastWriteTimeUtc;

                var record = new BackupRecord
                {
                    Id = id,
                    CreatedAt = createdAt,
                    SizeBytes = fileInfo.Length,
                    Provider = BackupProvider.Local,
                    Status = BackupStatus.Completed,
                    LocalArchivePath = file,
                    IncludeBookFiles = manifest?.BookFileCount > 0,
                    ManifestJson = manifest != null ? JsonSerializer.Serialize(manifest, JsonOpts) : null,
                };

                db.BackupRecords.Add(record);
                imported.Add(new BackupHistoryDto(
                    record.Id,
                    record.CreatedAt,
                    record.SizeBytes,
                    record.Provider,
                    record.Status,
                    record.IncludeBookFiles,
                    record.ErrorMessage
                ));

                _logger.LogInformation("Imported backup {Id} from {File}", id, file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping untracked file {File}: not a valid archive", file);
            }
        }

        if (imported.Count > 0)
            await db.SaveChangesAsync(ct);

        return imported;
    }

    private async Task<string> BuildArchiveAsync(Guid backupId, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nostos-backup-{backupId}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dbDir = Path.Combine(tempDir, "database");
            Directory.CreateDirectory(dbDir);
            var backupDbPath = Path.Combine(dbDir, "nostos.db");

            _settingsProvider.UpdateProgress("Collecting files", 10, 2, 5);
            var sanitizedPath = backupDbPath.Replace("'", "''");
            await ExecuteVacuumWithRetryAsync(sanitizedPath, ct);

            var booksDir = Path.Combine(tempDir, "books");
            Directory.CreateDirectory(booksDir);

            if (Settings.IncludeBookFiles)
            {
                _settingsProvider.UpdateProgress("Collecting files", 25, 2, 5);
                var storageRoot = _fileStorage.StorageRoot;
                if (Directory.Exists(storageRoot))
                {
                    foreach (var bookFolder in Directory.EnumerateDirectories(storageRoot))
                    {
                        var folderName = Path.GetFileName(bookFolder);
                        var destFolder = Path.Combine(booksDir, folderName);
                        Directory.CreateDirectory(destFolder);

                        foreach (var file in Directory.EnumerateFiles(bookFolder))
                        {
                            var destFile = Path.Combine(destFolder, Path.GetFileName(file));
                            using var src = File.OpenRead(file);
                            using var dst = File.Create(destFile);
                            await src.CopyToAsync(dst, ct);
                        }
                    }
                }
            }

            _settingsProvider.UpdateProgress("Collecting files", 40, 2, 5);
            var metadataDir = Path.Combine(tempDir, "metadata");
            Directory.CreateDirectory(metadataDir);

            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();

                // Membership lives in the join table, so the backup has to carry
                // it explicitly — the book row alone no longer records which
                // collections a book belongs to. Acquisition provenance lives in
                // its own table for the same reason: without this Include a
                // backup would silently drop where imported books came from.
                var books = await db.Books
                    .Include(b => b.BookCollections)
                    .Include(b => b.Acquisition)
                    .ToListAsync(ct);
                await WriteJsonAsync(Path.Combine(metadataDir, "books.json"), books, ct);

                var notes = await db
                    .Notes.Include(n => n.Book)
                    .Include(n => n.NoteTopics)
                    .ThenInclude(nc => nc.Topic)
                    .ToListAsync(ct);
                await WriteJsonAsync(Path.Combine(metadataDir, "notes.json"), notes, ct);

                var topics = await db
                    .Topics.Include(c => c.NoteTopics)
                    .ThenInclude(nc => nc.Note)
                    .ToListAsync(ct);
                await WriteJsonAsync(Path.Combine(metadataDir, "topics.json"), topics, ct);

                var collections = await db.Collections.ToListAsync(ct);
                await WriteJsonAsync(Path.Combine(metadataDir, "collections.json"), collections, ct);

                var writings = await db.Writings.ToListAsync(ct);
                await WriteJsonAsync(Path.Combine(metadataDir, "writings.json"), writings, ct);
            }

            var dbFileSize = new FileInfo(backupDbPath).Length;
            var bookFileCount = Directory.GetDirectories(booksDir).Length;
            var manifest = new BackupManifestDto(
                Version: "1",
                Timestamp: DateTime.UtcNow,
                DatabaseSizeBytes: dbFileSize,
                BookFileCount: bookFileCount,
                TotalSizeBytes: 0,
                Checksum: ""
            );

            _settingsProvider.UpdateProgress("Compressing backup", 55, 3, 5);

            var archivePath = Path.Combine(
                Path.GetTempPath(),
                $"nostos-backup-{DateTime.UtcNow:yyyy-MM-dd'T'HHmmss}.nostos"
            );

            using (var archiveStream = new FileStream(archivePath, FileMode.Create))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry("manifest.json");
                using (var manifestStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOpts, ct);
                }

                AddDirectoryToArchive(archive, tempDir, "");
            }

            return archivePath;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* cleanup */ }
        }
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string sourceDir, string entryPrefix, int maxDepth = 100)
    {
        var stack = new Stack<(string dir, string prefix, int depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        stack.Push((sourceDir, entryPrefix, 0));

        while (stack.Count > 0)
        {
            var (dir, prefix, depth) = stack.Pop();

            if (depth > maxDepth)
                throw new InvalidOperationException($"Directory nesting exceeds maximum depth of {maxDepth}. Path: {dir}");

            var realPath = Path.GetFullPath(dir);
            if (!visited.Add(realPath))
                throw new InvalidOperationException($"Circular symlink detected. Path: {dir}");

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var entryName = string.IsNullOrEmpty(prefix)
                    ? Path.GetFileName(file)
                    : $"{prefix}/{Path.GetFileName(file)}";

                archive.CreateEntryFromFile(file, entryName);
            }

            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                var subRealPath = Path.GetFullPath(subDir);
                if (subRealPath != realPath)
                {
                    var dirName = Path.GetFileName(subDir);
                    var subPrefix = string.IsNullOrEmpty(prefix) ? dirName : $"{prefix}/{dirName}";
                    stack.Push((subDir, subPrefix, depth + 1));
                }
            }
        }
    }

    private static async Task<string> ComputeFileChecksumAsync(string filePath)
    {
        using var sha256 = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var hash = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task ExecuteVacuumWithRetryAsync(string sanitizedPath, CancellationToken ct)
    {
        var delays = new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) };

        for (var attempt = 0; attempt <= delays.Length; attempt++)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{sanitizedPath}'", ct);
#pragma warning restore EF1002
                return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (attempt < delays.Length && (ex.SqliteExtendedErrorCode == 5 || ex.SqliteExtendedErrorCode == 6))
            {
                _logger.LogWarning(ex, "VACUUM INTO attempt {Attempt} failed (SQLITE_BUSY/LOCKED), retrying after {Delay}ms", attempt + 1, delays[attempt].TotalMilliseconds);
                await Task.Delay(delays[attempt], ct);
            }
        }
    }

    private async Task RestoreFromArchiveAsync(string archivePath, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nostos-restore-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            ZipFile.ExtractToDirectory(archivePath, tempDir);

            var dbPath = Path.Combine(tempDir, "database", "nostos.db");
            if (File.Exists(dbPath))
            {
                var activeDbPath = ResolveActiveDatabasePath();
                var preRestoreBackup = $"{activeDbPath}.pre-restore-{DateTime.UtcNow:yyyyMMddHHmmss}";

                if (File.Exists(activeDbPath))
                    File.Copy(activeDbPath, preRestoreBackup, true);

                SqliteConnection.ClearAllPools();
                await Task.Delay(200, ct);

                File.Copy(dbPath, activeDbPath, true);
            }

            var booksSrcDir = Path.Combine(tempDir, "books");
            if (Directory.Exists(booksSrcDir))
            {
                _settingsProvider.UpdateProgress("Restoring data", 60, 2, 3);
                var storageRoot = _fileStorage.StorageRoot;
                Directory.CreateDirectory(storageRoot);

                foreach (var bookFolder in Directory.EnumerateDirectories(booksSrcDir))
                {
                    var folderName = Path.GetFileName(bookFolder);
                    var destFolder = Path.Combine(storageRoot, folderName);
                    Directory.CreateDirectory(destFolder);

                    foreach (var file in Directory.EnumerateFiles(bookFolder))
                    {
                        var destFile = Path.Combine(destFolder, Path.GetFileName(file));
                        File.Copy(file, destFile, true);
                    }
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* cleanup */ }
        }
    }

    private string ResolveActiveDatabasePath()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
        var dataSource = db.Database.GetDbConnection().DataSource;

        if (string.IsNullOrWhiteSpace(dataSource) ||
            string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Backup restore requires a file-backed SQLite database.");
        }

        return Path.GetFullPath(dataSource);
    }

    private async Task EnforceMaxBackupsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();

        var records = await db
            .BackupRecords.Where(b => b.Status == BackupStatus.Completed)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

        var toRemove = records.Skip(Settings.MaxBackups).ToList();
        foreach (var record in toRemove)
        {
            if (!string.IsNullOrEmpty(record.LocalArchivePath))
            {
                DeleteArchiveIfOwned(record.LocalArchivePath);
            }

            db.BackupRecords.Remove(record);
        }

        if (toRemove.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deletes an archive file, but only when it really lives in this
    /// deployment's backup directory.
    ///
    /// <see cref="BackupRecordModel.LocalArchivePath"/> is persisted as an
    /// absolute path, so a row can name a file anywhere on the machine —
    /// including another installation's archives. That is not hypothetical: the
    /// development worktree is seeded with a COPY of the production database, so
    /// running the application there presents production's archive paths to this
    /// very method, and an unguarded delete removes the running installation's
    /// backups from disk. Pruning decides which backups THIS deployment keeps,
    /// so it may only remove files this deployment owns; anything else is
    /// reported and left alone.
    /// </summary>
    private void DeleteArchiveIfOwned(string archivePath)
    {
        if (!File.Exists(archivePath))
            return;

        if (!IsInsideBackupDir(archivePath))
        {
            _logger.LogWarning(
                "Not deleting backup archive {ArchivePath}: it is outside this deployment's backup directory ({BackupDir}). " +
                "It belongs to another installation or an earlier location, so it is left on disk.",
                archivePath, LocalBackupDir);
            return;
        }

        try { File.Delete(archivePath); } catch { /* best effort */ }
    }

    /// <summary>
    /// Whether a path is inside the resolved backup directory. Compared against
    /// the directory prefix INCLUDING its trailing separator, so a sibling named
    /// <c>backups-old</c> cannot pass as being inside <c>backups</c>.
    /// </summary>
    private bool IsInsideBackupDir(string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var root = Path.GetFullPath(LocalBackupDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return Path.GetFullPath(path).StartsWith(root, comparison);
    }

    private async Task MarkStaleInProgressAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();

        var stale = await db
            .BackupRecords.Where(b => b.Status == BackupStatus.InProgress && b.CreatedAt < DateTime.UtcNow.AddHours(-1))
            .ToListAsync(ct);

        foreach (var record in stale)
        {
            _logger.LogWarning("Marking stale InProgress backup {Id} as Failed", record.Id);
            record.Status = BackupStatus.Failed;
        }

        if (stale.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private static async Task WriteJsonAsync<T>(string path, T data, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, data, JsonOpts, ct);
    }
}
