using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nostos.Backend.Configuration;
using Nostos.Backend.Data;
using Nostos.Backend.Services;

namespace Nostos.Backend.Tests.Support;

public class LibraryEndpointFactory : WebApplicationFactory<Program>
{
    public const string SpaShellMarker = "<!doctype html>";
    private readonly string _dbRoot;
    private readonly string _dbPath;
    private readonly string _cleanSnapshotPath;
    private readonly string _storageTempRoot;
    private readonly string _booksRoot;

    public string DatabasePath => _dbPath;
    public string StorageTempRoot => _storageTempRoot;
    public string BooksRootPath => _booksRoot;

    public LibraryEndpointFactory()
    {
        _dbRoot = Path.Combine(
            Path.GetTempPath(),
            $"nostos-library-db-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dbRoot, "nostos-library.db");
        _cleanSnapshotPath = _dbPath + ".clean-snapshot";
        _storageTempRoot = Path.Combine(
            Path.GetTempPath(),
            $"nostos-library-storage-{Guid.NewGuid():N}");
        _booksRoot = Path.Combine(_storageTempRoot, "books");
        LibraryEndpointBootstrap.EnsureSchemaAndHistory(_dbPath);
    }

    /// <summary>
    /// Resets the shared host's SQLite database and disposable storage roots
    /// back to their clean post-startup state so tests in a class can share
    /// one host via <c>IClassFixture&lt;&gt;</c> without cross-test leakage.
    /// </summary>
    public async Task ResetStateAsync(CancellationToken cancellationToken = default)
    {
        // Ensure host startup (including IDatabaseBootstrapService) has completed.
        _ = Services;

        var maintenance = Services.GetService<ILibraryMaintenanceCoordinator>();
        await using var exclusive = maintenance is null
            ? null
            : await maintenance.EnterExclusiveAsync(LibraryMaintenanceReason.BackupRestore, cancellationToken);

        var liveConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false,
        }.ToString();
        var snapshotConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _cleanSnapshotPath,
            Pooling = false,
        }.ToString();

        ClearPoolBestEffort(_dbPath);

        if (!File.Exists(_cleanSnapshotPath))
        {
            await BackupWithRetryAsync(liveConnectionString, snapshotConnectionString, cancellationToken);
        }
        else
        {
            await BackupWithRetryAsync(snapshotConnectionString, liveConnectionString, cancellationToken);
        }

        ClearDirectoryContents(_booksRoot);
        ClearDirectoryContents(Path.Combine(_storageTempRoot, "calibre-uploads"));
        ClearDirectoryContents(Path.Combine(_storageTempRoot, "transfers"));
    }

    private static async Task BackupWithRetryAsync(
        string sourceConnectionString,
        string destinationConnectionString,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var source = new SqliteConnection(sourceConnectionString);
                using var destination = new SqliteConnection(destinationConnectionString);
                await source.OpenAsync(cancellationToken);
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);
                return;
            }
            catch (SqliteException ex) when (attempt < maxAttempts && ex.SqliteErrorCode is 5 or 6)
            {
                await Task.Delay(Math.Min(25 * attempt, 200), cancellationToken);
            }
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        builder.UseSetting(PersistenceRegistration.DatabasePathConfigurationKey, _dbPath);
        builder.UseSetting("Storage:BooksRoot", _booksRoot);
        builder.UseSetting(
            "CalibreUpload:RootPath",
            Path.Combine(_storageTempRoot, "calibre-uploads"));
        builder.ConfigureAppConfiguration((ctx, config) =>
        {
            foreach (var source in config.Sources.OfType<FileConfigurationSource>())
            {
                source.ReloadOnChange = false;
            }

            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={_dbPath}",
                [PersistenceRegistration.DatabasePathConfigurationKey] = _dbPath,
                ["hostBuilder:reloadConfigOnChange"] = "false",
            });
        });
        builder.ConfigureServices(services =>
        {
            var descriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<NostosDbContext>)
                         || d.ServiceType == typeof(IDbContextFactory<NostosDbContext>))
                .ToList();
            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContextFactory<NostosDbContext>(options =>
            {
                options.UseSqlite(
                    $"Data Source={_dbPath}",
                    sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName));
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            CleanupResources();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        CleanupResources();
        GC.SuppressFinalize(this);
    }

    private void CleanupResources()
    {
        ClearPoolBestEffort(_dbPath);
        ClearPoolBestEffort(_cleanSnapshotPath);
        DeleteDbFiles(_dbPath);
        DeleteDbFiles(_cleanSnapshotPath);
        TryDeleteDirectory(_dbRoot);
        TryDeleteDirectory(_storageTempRoot);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup only.
        }
    }

    internal static void DeleteDbFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var file = dbPath + suffix;
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static void ClearPoolBestEffort(string dbPath)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            SqliteConnection.ClearPool(connection);
        }
        catch (SqliteException)
        {
            // Best-effort cleanup only.
        }
    }

    private static void ClearDirectoryContents(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
            catch (IOException)
            {
                // Best-effort cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup only.
            }
        }
    }
}

internal static class LibraryEndpointBootstrap
{
    private static readonly object Lock = new();
    private static string? _templateDbPath;
    private static bool _cleanupRegistered;

    public static void EnsureSchemaAndHistory(string dbPath)
    {
        var templatePath = EnsureTemplate();
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        LibraryEndpointFactory.DeleteDbFiles(dbPath);
        File.Copy(templatePath, dbPath, overwrite: true);
    }

    private static string EnsureTemplate()
    {
        lock (Lock)
        {
            if (_templateDbPath is not null && File.Exists(_templateDbPath))
            {
                return _templateDbPath;
            }

            var templatePath = Path.Combine(
                Path.GetTempPath(),
                $"nostos-library-template-{Environment.ProcessId}-{Guid.NewGuid():N}.db");

            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = templatePath,
                    Pooling = false,
                }.ToString();
                var options = new DbContextOptionsBuilder<NostosDbContext>()
                    .UseSqlite(
                        connectionString,
                        sqlite => sqlite.MigrationsAssembly(typeof(Program).Assembly.FullName))
                    .Options;

                using (var db = new NostosDbContext(options))
                {
                    db.Database.EnsureCreated();

                    var migrationsAssembly = db.GetService<IMigrationsAssembly>();
                    var allMigrations = migrationsAssembly.Migrations.Keys.OrderBy(id => id).ToList();

                    using var transaction = db.Database.BeginTransaction();
                    db.Database.ExecuteSqlRaw(
                        "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (" +
                        "\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, " +
                        "\"ProductVersion\" TEXT NOT NULL)");

                    foreach (var migrationId in allMigrations)
                    {
                        db.Database.ExecuteSqlRaw(
                            "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1})",
                            migrationId,
                            "10.0.0");
                    }

                    transaction.Commit();
                }

                foreach (var suffix in new[] { "-shm", "-wal" })
                {
                    var sidecar = templatePath + suffix;
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                }

                _templateDbPath = templatePath;
                if (!_cleanupRegistered)
                {
                    _cleanupRegistered = true;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                    {
                        if (_templateDbPath is { } path)
                        {
                            LibraryEndpointFactory.DeleteDbFiles(path);
                        }
                    };
                }

                return templatePath;
            }
            catch
            {
                LibraryEndpointFactory.DeleteDbFiles(templatePath);
                throw;
            }
        }
    }
}
