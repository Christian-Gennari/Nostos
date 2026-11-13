using System.Text.Json;
using Microsoft.Extensions.Options;
using Nostos.Backend.Composition;
using Nostos.Backend.Configuration;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services;

public class BackupSettingsProvider
{
    private readonly string _filePath;
    private readonly BackupSettings _defaults;
    private BackupSettings _current;
    private readonly object _lock = new();
    private readonly object _maintenanceLock = new();
    private IAsyncDisposable? _compatibilityLease;
    private int _compatibilityReferences;
    private BackupProgressDto? _progress;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public BackupSettingsProvider(IWebHostEnvironment env, IOptions<BackupSettings> defaults,
        ILibraryMaintenanceCoordinator? maintenance = null, IConfiguration? configuration = null,
        SelfHostedStoragePaths? storagePaths = null)
    {
        Maintenance = maintenance ?? new LibraryMaintenanceCoordinator();
        _filePath = storagePaths is not null
            ? Path.Combine(Path.GetDirectoryName(storagePaths.DatabasePath) ?? env.ContentRootPath, FileName)
            : ResolveSettingsPath(
                configuration?[PersistenceRegistration.DatabasePathConfigurationKey], env.ContentRootPath);
        MigrateLegacyFile(Path.Combine(env.ContentRootPath, FileName), _filePath);
        _defaults = defaults.Value;
        _current = LoadFromFile() ?? CloneDefaults();
    }

    private const string FileName = "backup-settings.json";

    /// <summary>
    /// The settings live beside the database (the data volume in Docker), so
    /// they survive a container being recreated. Unset, that is the content
    /// root, the historical location.
    /// </summary>
    public static string ResolveSettingsPath(string? configuredDatabasePath, string contentRootPath)
    {
        var dbPath = PersistenceRegistration.ResolveDatabasePath(configuredDatabasePath, contentRootPath);
        return Path.Combine(Path.GetDirectoryName(dbPath) ?? contentRootPath, FileName);
    }

    // Installs that stored the file in the app folder keep their settings: the
    // old file is moved over once, only when the new location has none yet.
    private static void MigrateLegacyFile(string legacyPath, string newPath)
    {
        try
        {
            if (Path.GetFullPath(legacyPath) == Path.GetFullPath(newPath)
                || File.Exists(newPath) || !File.Exists(legacyPath))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.Copy(legacyPath, newPath);
            File.Delete(legacyPath);
        }
        catch
        {
            // Best effort: defaults apply if the old file cannot be moved.
        }
    }

    public BackupSettings Current
    {
        get
        {
            lock (_lock) { return _current; }
        }
    }

    public BackupSettings Update(Action<BackupSettings> apply)
    {
        lock (_lock)
        {
            apply(_current);
            PersistToFile();
            return _current;
        }
    }

    public ILibraryMaintenanceCoordinator Maintenance { get; }
    public bool IsInMaintenanceMode => Maintenance.IsMaintenanceActive;

    public BackupProgressDto? Progress => Volatile.Read(ref _progress);

    public void StartProgress(int totalSteps)
    {
        Volatile.Write(ref _progress, new BackupProgressDto(true, "Starting", 0, DateTime.UtcNow, 1, totalSteps));
    }

    public void UpdateProgress(string step, int percent, int stepNumber, int totalSteps)
    {
        var startedAt = Volatile.Read(ref _progress)?.StartedAt;
        Volatile.Write(ref _progress, new BackupProgressDto(true, step, percent, startedAt, stepNumber, totalSteps));
    }

    public void ClearProgress()
    {
        Volatile.Write(ref _progress, null);
    }

    // Compatibility entry points for existing callers. New asynchronous restore
    // code uses Maintenance directly. These references own ONE coordinator lease.
    public void EnterMaintenanceMode()
    {
        lock (_maintenanceLock)
        {
            if (_compatibilityReferences == 0)
                _compatibilityLease = Maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.BackupRestore)
                    .GetAwaiter().GetResult();
            _compatibilityReferences++;
        }
    }

    public void ExitMaintenanceMode()
    {
        lock (_maintenanceLock)
        {
            if (_compatibilityReferences == 0)
                throw new InvalidOperationException("ExitMaintenanceMode called without matching EnterMaintenanceMode.");
            if (--_compatibilityReferences == 0)
            {
                var lease = _compatibilityLease;
                _compatibilityLease = null;
                lease!.DisposeAsync().GetAwaiter().GetResult();
            }
        }
    }

    private BackupSettings? LoadFromFile()
    {
        if (!File.Exists(_filePath))
            return null;

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<BackupSettings>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private void PersistToFile()
    {
        var json = JsonSerializer.Serialize(_current, JsonOpts);
        var tmpPath = _filePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* cleanup */ }
            throw;
        }
    }

    private BackupSettings CloneDefaults() => new()
    {
        IsEnabled = _defaults.IsEnabled,
        IntervalHours = _defaults.IntervalHours,
        Provider = _defaults.Provider,
        MaxBackups = _defaults.MaxBackups,
        IncludeBookFiles = _defaults.IncludeBookFiles,
    };
}
