using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Portability.Activation;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services;

internal sealed record BackupExtractionLimits(int MaxEntries, long MaxEntryBytes, long MaxTotalBytes)
{
    public static readonly BackupExtractionLimits Default = new(
        MaxEntries: 50_000,
        MaxEntryBytes: 4L * 1024L * 1024L * 1024L,
        MaxTotalBytes: 16L * 1024L * 1024L * 1024L);
}

public class BackupService : IBackupService
{
    private const long MaxManifestEntryBytes = 1024 * 1024;

    private static readonly ConcurrentDictionary<string, byte> ActiveStagingPaths =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFileStorageService _fileStorage;
    private readonly BackupSettingsProvider _settingsProvider;
    private readonly ILogger<BackupService> _logger;
    private readonly string _localBackupDir;

    private BackupSettings Settings => _settingsProvider.Current;

    private string LocalBackupDir => _localBackupDir;
    internal string StagingDir => Path.Combine(_localBackupDir, ".staging");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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
        await using var scope = _scopeFactory.CreateAsyncScope();
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

        await using (var scope = _scopeFactory.CreateAsyncScope())
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
            var built = await BuildArchiveAsync(recordId, ct);
            archivePath = built.ArchivePath;
            _settingsProvider.UpdateProgress("Verifying backup", 80, 4, 5);

            var fileSize = new FileInfo(archivePath).Length;
            var checksum = await ComputeFileChecksumAsync(archivePath);
            _settingsProvider.UpdateProgress("Finalizing", 95, 5, 5);

            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
                var record = await db.BackupRecords.FindAsync([recordId], ct);
                if (record != null)
                {
                    record.SizeBytes = fileSize;
                    var manifest = new BackupManifestDto(
                        Version: "1",
                        Timestamp: recordCreatedAt,
                        DatabaseSizeBytes: built.DatabaseSizeBytes,
                        BookFileCount: built.BookFileCount,
                        TotalSizeBytes: fileSize,
                        Checksum: checksum
                    );
                    record.ManifestJson = JsonSerializer.Serialize(manifest, JsonOpts);

                    // Always save a local copy for recoverability (same volume rename from .staging).
                    Directory.CreateDirectory(LocalBackupDir);
                    persistentLocalPath = Path.Combine(LocalBackupDir, $"{recordId}.nostos");
                    File.Move(archivePath, persistentLocalPath, overwrite: true);
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
                await using var scope = _scopeFactory.CreateAsyncScope();
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
        finally
        {
            // Clean up temp archive if it was not moved (or failed mid-way).
            if (archivePath != null)
            {
                if (File.Exists(archivePath))
                {
                    try { File.Delete(archivePath); } catch { /* cleanup */ }
                }

                ActiveStagingPaths.TryRemove(Path.GetFullPath(archivePath), out _);
            }
        }

        _settingsProvider.ClearProgress();

        await EnforceMaxBackupsAsync(ct);

        return new TriggerBackupResultDto(recordId, recordStatus, recordSizeBytes, recordCreatedAt);
    }

    public async Task<RestoreResultDto> RestoreBackupAsync(Guid backupId, CancellationToken ct = default)
    {
        BackupRecord? record;
        using (var operation = _settingsProvider.Maintenance.TryEnterOperation()
            ?? throw LibraryMaintenanceCoordinator.Busy())
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();
            record = await db.BackupRecords.FindAsync([backupId], ct);
        }

        if (record is null)
            return new RestoreResultDto(false, "Backup not found");

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
                return new RestoreResultDto(false, "Could not retrieve the backup archive. The local copy is unavailable.");

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
            var safetyCopyName = await RestoreFromArchiveAsync(archivePath, ct);

            _settingsProvider.UpdateProgress("Completing restore", 90, 3, 3);
            await ReconcileBackupRecordsAfterRestoreAsync(record, archivePath, ct);
            _logger.LogInformation("Restore from backup {Id} completed.", backupId);

            var message = "Backup restored. No restart is needed.";
            if (safetyCopyName is not null)
                message += $" A safety copy of the previous database was saved as {safetyCopyName}, next to the database file. Only the latest safety copy is kept.";
            return new RestoreResultDto(true, message);
        }
        catch (Portability.MigrationActivationException ex) when (ex.Code == Portability.MigrationActivationErrorCodes.Busy)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore from backup {Id} failed", backupId);
            return new RestoreResultDto(false, $"Could not restore the backup: {ex.Message}");
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
        await using var scope = _scopeFactory.CreateAsyncScope();
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
        await using var scope = _scopeFactory.CreateAsyncScope();
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

        await using var scope = _scopeFactory.CreateAsyncScope();
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
                    if (manifestEntry.Length > MaxManifestEntryBytes)
                        throw new InvalidDataException($"manifest.json exceeds maximum allowed size ({MaxManifestEntryBytes} bytes).");
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

    private sealed record BuiltArchive(string ArchivePath, long DatabaseSizeBytes, int BookFileCount);

    private async Task<BuiltArchive> BuildArchiveAsync(Guid backupId, CancellationToken ct)
    {
        Directory.CreateDirectory(StagingDir);
        var tempDir = Path.GetFullPath(Path.Combine(StagingDir, $"backup-{backupId:N}"));
        ActiveStagingPaths.TryAdd(tempDir, 0);
        Directory.CreateDirectory(tempDir);

        string? archivePath = null;
        try
        {
            var dbDir = Path.Combine(tempDir, "database");
            Directory.CreateDirectory(dbDir);
            var backupDbPath = Path.Combine(dbDir, "nostos.db");

            _settingsProvider.UpdateProgress("Collecting files", 10, 2, 5);
            var sanitizedPath = backupDbPath.Replace("'", "''");
            await ExecuteVacuumWithRetryAsync(sanitizedPath, ct);
            // The copy is a whole-file snapshot; host credentials must not
            // travel in it (issue #1525).
            await BackupSecretScrubber.ScrubAsync(backupDbPath, ct);

            var booksDir = Path.Combine(tempDir, "books");
            Directory.CreateDirectory(booksDir);
            var copiedBookFiles = 0;

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

                        foreach (var file in Directory.EnumerateFiles(bookFolder))
                        {
                            Directory.CreateDirectory(destFolder);
                            copiedBookFiles++;
                            var destFile = Path.Combine(destFolder, Path.GetFileName(file));
                            using var src = File.OpenRead(file);
                            using var dst = File.Create(destFile);
                            await src.CopyToAsync(dst, ct);
                        }
                    }
                }
            }

            var dbFileSize = new FileInfo(backupDbPath).Length;
            // The manifest cannot hold the archive's own size or checksum (they
            // exist only once the archive is written; the database record keeps
            // them). It holds what is known now: the real number of book files
            // copied and the uncompressed size of the database snapshot plus
            // those book files — the only payload restore actually reads. Older
            // archives may also carry a metadata/ JSON dump; restore ignores it.
            var contentBytes = new DirectoryInfo(tempDir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
            var manifest = new BackupManifestDto(
                Version: "1",
                Timestamp: DateTime.UtcNow,
                DatabaseSizeBytes: dbFileSize,
                BookFileCount: copiedBookFiles,
                TotalSizeBytes: contentBytes,
                Checksum: ""
            );

            _settingsProvider.UpdateProgress("Compressing backup", 55, 3, 5);

            archivePath = Path.GetFullPath(
                Path.Combine(StagingDir, $"nostos-backup-{backupId:N}-{Guid.NewGuid():N}.nostos"));
            ActiveStagingPaths.TryAdd(archivePath, 0);

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

            var built = new BuiltArchive(archivePath, dbFileSize, copiedBookFiles);
            archivePath = null; // Ownership transferred to CreateBackupAsync.
            return built;
        }
        finally
        {
            if (archivePath != null)
            {
                if (File.Exists(archivePath))
                {
                    try { File.Delete(archivePath); } catch { /* cleanup */ }
                }

                ActiveStagingPaths.TryRemove(archivePath, out _);
            }

            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch
            {
                /* cleanup */
            }
            finally
            {
                ActiveStagingPaths.TryRemove(tempDir, out _);
            }
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
                await using var scope = _scopeFactory.CreateAsyncScope();
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

    /// <summary>
    /// Removes orphaned staging directories/files left under <see cref="StagingDir"/>
    /// from an interrupted backup or restore run. Active in-process staging paths
    /// are never removed.
    /// </summary>
    internal void CleanupOrphanedStaging()
    {
        if (!Directory.Exists(StagingDir))
            return;

        foreach (var dir in Directory.EnumerateDirectories(StagingDir))
        {
            var fullDir = Path.GetFullPath(dir);
            if (ActiveStagingPaths.ContainsKey(fullDir))
                continue;

            try
            {
                Directory.Delete(fullDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove orphaned backup staging directory {Path}", fullDir);
            }
        }

        foreach (var file in Directory.EnumerateFiles(StagingDir))
        {
            var fullFile = Path.GetFullPath(file);
            if (ActiveStagingPaths.ContainsKey(fullFile))
                continue;

            try
            {
                File.Delete(fullFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove orphaned backup staging file {Path}", fullFile);
            }
        }
    }

    /// <summary>
    /// Extracts a <c>.nostos</c> ZIP archive into <paramref name="destinationDir"/>
    /// using an explicit entry loop that rejects Zip Slip path traversal, rooted
    /// paths, symlinks, and archives exceeding entry count or extracted size bounds.
    /// </summary>
    internal static async Task ExtractArchiveSafelyAsync(
        string archivePath,
        string destinationDir,
        BackupExtractionLimits? limits = null,
        CancellationToken ct = default)
    {
        var effectiveLimits = limits ?? BackupExtractionLimits.Default;
        var fullDestRoot = Path.GetFullPath(destinationDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        using var archive = ZipFile.OpenRead(archivePath);
        var entryCount = 0;
        long totalBytes = 0;
        var buffer = new byte[81_920];

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            entryCount++;
            if (entryCount > effectiveLimits.MaxEntries)
            {
                throw new InvalidDataException(
                    $"Backup archive exceeds the maximum entry count of {effectiveLimits.MaxEntries}.");
            }

            var fullName = entry.FullName;
            if (string.IsNullOrEmpty(fullName) || fullName.Contains('\0'))
            {
                throw new InvalidDataException("Backup archive contains an entry with an invalid name.");
            }

            if (fullName.StartsWith('/')
                || fullName.StartsWith('\\')
                || Path.IsPathRooted(fullName)
                || (fullName.Length >= 2 && fullName[1] == ':'))
            {
                throw new InvalidDataException(
                    $"Backup archive contains a rooted entry path '{fullName}', which is not permitted.");
            }

            var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if ((unixMode & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Backup archive contains a symbolic link entry '{fullName}', which is not permitted.");
            }

            var normalized = fullName.Replace('\\', '/');
            var isDirectory = normalized.EndsWith('/')
                || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0;

            var trimmed = normalized.TrimEnd('/');
            if (trimmed.Length == 0)
            {
                throw new InvalidDataException($"Backup archive contains an empty entry path '{fullName}'.");
            }

            foreach (var segment in trimmed.Split('/'))
            {
                if (segment.Length == 0 || segment is "." or "..")
                {
                    throw new InvalidDataException(
                        $"Backup archive contains an unsafe entry path '{fullName}'.");
                }
            }

            var relativePlatformPath = Path.DirectorySeparatorChar == '/'
                ? trimmed
                : trimmed.Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Combine(fullDestRoot, relativePlatformPath));
            if (!targetPath.StartsWith(fullDestRoot, comparison))
            {
                throw new InvalidDataException(
                    $"Backup archive entry '{fullName}' escapes the extraction directory.");
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            if (entry.Length < 0 || entry.Length > effectiveLimits.MaxEntryBytes)
            {
                throw new InvalidDataException(
                    $"Backup archive entry '{fullName}' exceeds the maximum allowed entry size of {effectiveLimits.MaxEntryBytes} bytes.");
            }

            if (entry.Length > effectiveLimits.MaxTotalBytes - totalBytes)
            {
                throw new InvalidDataException(
                    $"Backup archive exceeds the maximum total extracted size of {effectiveLimits.MaxTotalBytes} bytes.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            await using var entryStream = entry.Open();
            await using var destStream = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                useAsync: true);

            long entryBytesWritten = 0;
            int read;
            while ((read = await entryStream.ReadAsync(buffer, ct)) > 0)
            {
                entryBytesWritten = checked(entryBytesWritten + read);
                if (entryBytesWritten > effectiveLimits.MaxEntryBytes)
                {
                    throw new InvalidDataException(
                        $"Backup archive entry '{fullName}' exceeds the maximum allowed entry size of {effectiveLimits.MaxEntryBytes} bytes.");
                }

                totalBytes = checked(totalBytes + read);
                if (totalBytes > effectiveLimits.MaxTotalBytes)
                {
                    throw new InvalidDataException(
                        $"Backup archive exceeds the maximum total extracted size of {effectiveLimits.MaxTotalBytes} bytes.");
                }

                await destStream.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }

    /// <returns>The file name of the safety copy of the previous database, or null when none was made.</returns>
    private async Task<string?> RestoreFromArchiveAsync(string archivePath, CancellationToken ct)
    {
        Directory.CreateDirectory(StagingDir);
        var tempDir = Path.GetFullPath(Path.Combine(StagingDir, $"restore-{Guid.NewGuid():N}"));
        ActiveStagingPaths.TryAdd(tempDir, 0);
        Directory.CreateDirectory(tempDir);
        string? safetyCopyName = null;

        try
        {
            await ExtractArchiveSafelyAsync(archivePath, tempDir, BackupExtractionLimits.Default, ct);

            var dbPath = Path.Combine(tempDir, "database", "nostos.db");
            if (File.Exists(dbPath))
            {
                // Verify the extracted database passes SQLite integrity checks
                // before touching the live database file.
                SelfHostedSqliteFile.VerifyIntegrity(dbPath);
                SelfHostedSqliteFile.ClearPools();

                var activeDbPath = ResolveActiveDatabasePath();
                var activeDir = Path.GetDirectoryName(activeDbPath)!;
                Directory.CreateDirectory(activeDir);

                // Clean up any leftover temp staging files from an interrupted restore.
                var activeFileName = Path.GetFileName(activeDbPath);
                foreach (var leftover in Directory.GetFiles(activeDir, $"{activeFileName}.restoring-*.tmp")
                    .Concat(Directory.GetFiles(activeDir, $"{activeFileName}.restore-safety-*.tmp")))
                {
                    try { File.Delete(leftover); } catch { /* best effort */ }
                }

                SelfHostedSqliteFile.QuiesceForReplacement(activeDbPath, _logger);

                var preRestoreBackup = $"{activeDbPath}.pre-restore-{DateTime.UtcNow:yyyyMMddHHmmss}";
                if (File.Exists(activeDbPath))
                {
                    var preRestoreTemp = $"{activeDbPath}.restore-safety-{Guid.NewGuid():N}.tmp";
                    try
                    {
                        File.Copy(activeDbPath, preRestoreTemp, overwrite: true);
                        File.Move(preRestoreTemp, preRestoreBackup, overwrite: true);
                        safetyCopyName = Path.GetFileName(preRestoreBackup);
                    }
                    finally
                    {
                        if (File.Exists(preRestoreTemp))
                        {
                            try { File.Delete(preRestoreTemp); } catch { /* best effort */ }
                        }
                    }

                    // Keep only the latest safety copy; older ones are never
                    // shown anywhere and would pile up beside the database.
                    foreach (var old in Directory.GetFiles(activeDir, $"{activeFileName}.pre-restore-*"))
                    {
                        if (!string.Equals(old, preRestoreBackup, StringComparison.Ordinal))
                        {
                            try { File.Delete(old); } catch { /* best effort */ }
                        }
                    }
                }

                // Stage the new DB next to activeDbPath and remove any pre-existing
                // -wal / -shm / -journal sidecars before swapping it into place.
                var stagedLivePath = $"{activeDbPath}.restoring-{Guid.NewGuid():N}.tmp";
                var replacedLive = false;
                try
                {
                    File.Copy(dbPath, stagedLivePath, overwrite: true);
                    SelfHostedSqliteFile.DeleteSidecars(stagedLivePath);

                    SelfHostedSqliteFile.ClearPools();
                    SelfHostedSqliteFile.DeleteSidecars(activeDbPath);

                    File.Move(stagedLivePath, activeDbPath, overwrite: true);
                    replacedLive = true;

                    await RenewCoverVersionsAsync(activeDbPath, ct);

                    SelfHostedSqliteFile.ClearPools();
                    SelfHostedSqliteFile.VerifyIntegrity(activeDbPath);
                    SelfHostedSqliteFile.ClearPools();
                }
                catch
                {
                    if (replacedLive && safetyCopyName is not null && File.Exists(preRestoreBackup))
                    {
                        try
                        {
                            SelfHostedSqliteFile.ClearPools();
                            SelfHostedSqliteFile.DeleteSidecars(activeDbPath);
                            File.Copy(preRestoreBackup, activeDbPath, overwrite: true);
                            SelfHostedSqliteFile.ClearPools();
                        }
                        catch (Exception rollbackEx)
                        {
                            _logger.LogError(
                                rollbackEx,
                                "Failed to roll back live database from safety copy {SafetyCopy}",
                                preRestoreBackup);
                        }
                    }

                    throw;
                }
                finally
                {
                    if (File.Exists(stagedLivePath))
                    {
                        try { File.Delete(stagedLivePath); } catch { /* best effort */ }
                    }
                }
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
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch
            {
                /* cleanup */
            }
            finally
            {
                ActiveStagingPaths.TryRemove(tempDir, out _);
            }
        }

        return safetyCopyName;
    }

    /// <summary>
    /// A restored database carries the backup records as they were when that
    /// backup was taken: the backup's own row is still InProgress with 0 bytes,
    /// and rows for backups retention deleted since are back. Brings the records
    /// in line with the archive files on disk. Adds no rows.
    /// </summary>
    private async Task ReconcileBackupRecordsAfterRestoreAsync(
        BackupRecord restored, string archivePath, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();

        var rows = await db.BackupRecords.ToListAsync(ct);
        foreach (var row in rows)
        {
            if (row.Id == restored.Id)
            {
                row.Status = BackupStatus.Completed;
                row.SizeBytes = new FileInfo(archivePath).Length;
                row.Provider = BackupProvider.Local;
                row.LocalArchivePath = archivePath;
                row.ErrorMessage = null;
                // The snapshot predates the manifest; keep the live one so a
                // later restore still verifies the checksum.
                row.ManifestJson ??= restored.ManifestJson;
                continue;
            }

            // Only finished-looking rows can be ghosts; a Failed row never had a file.
            if (row.Status is not (BackupStatus.Completed or BackupStatus.InProgress))
                continue;

            var path = ArchivePathFor(row);
            if (File.Exists(path))
            {
                if (row.Status == BackupStatus.InProgress)
                {
                    row.Status = BackupStatus.Completed;
                    row.SizeBytes = new FileInfo(path).Length;
                }
            }
            else
            {
                _logger.LogInformation("Dropping backup record {Id}: its archive is gone", row.Id);
                db.BackupRecords.Remove(row);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private string ArchivePathFor(BackupRecord row) =>
        !string.IsNullOrEmpty(row.LocalArchivePath)
            ? row.LocalArchivePath
            : Path.Combine(LocalBackupDir, $"{row.Id}.nostos");

    /// <summary>
    /// A restored database brings back cover versions the browser may already
    /// hold cached for different bytes (a cover replaced after the backup, or a
    /// backup taken without book files). Fresh tokens give every restored cover
    /// a URL no browser has seen. A backup older than the CoverVersion column
    /// has none to renew: the migration that adds it on start-up stamps them.
    /// </summary>
    private static async Task RenewCoverVersionsAsync(string databasePath, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync(ct);

        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('Books') WHERE name = 'FileDetails_CoverVersion'";
            if (Convert.ToInt64(await probe.ExecuteScalarAsync(ct)) == 0)
                return;
        }

        await using var renew = connection.CreateCommand();
        renew.CommandText =
            "UPDATE \"Books\" SET \"FileDetails_CoverVersion\" = lower(hex(randomblob(8))) " +
            "WHERE \"FileDetails_CoverFileName\" IS NOT NULL";
        await renew.ExecuteNonQueryAsync(ct);
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
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NostosDbContext>();

        var rows = await db.BackupRecords.ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);

        // Retention counts what is on disk: every archive file in the backup
        // folder, whatever state its row is in (a restored or stuck row must not
        // hide a file from the limit), plus Completed rows that name a file
        // elsewhere. Completed rows whose file is gone are ghosts and are dropped.
        var kept = new List<(BackupRecord? Row, string Path, DateTime At)>();
        var seen = new HashSet<Guid>();
        if (Directory.Exists(LocalBackupDir))
        {
            foreach (var file in Directory.GetFiles(LocalBackupDir, "*.nostos"))
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id))
                    continue;
                byId.TryGetValue(id, out var row);
                seen.Add(id);
                kept.Add((row, file, row?.CreatedAt ?? File.GetLastWriteTimeUtc(file)));
            }
        }

        foreach (var row in rows.Where(r => r.Status == BackupStatus.Completed && !seen.Contains(r.Id)))
        {
            var path = ArchivePathFor(row);
            if (File.Exists(path))
                kept.Add((row, path, row.CreatedAt));
            else
                db.BackupRecords.Remove(row);
        }

        var changed = db.ChangeTracker.HasChanges();
        foreach (var (row, path, _) in kept.OrderByDescending(k => k.At).Skip(Settings.MaxBackups))
        {
            DeleteArchiveIfOwned(path);
            if (row is not null)
                db.BackupRecords.Remove(row);
            changed = true;
        }

        if (changed)
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
        await using var scope = _scopeFactory.CreateAsyncScope();
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
}
