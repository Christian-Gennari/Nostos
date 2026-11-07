// One deterministic host per quality-bed run, one database per scenario
// (issue #1474).
//
// Building a LibraryEndpointFactory host per scenario (schema, host startup,
// seed, then a cold first request) cost about two seconds before the first
// turn ran, which was most of the bed's wall clock. Here the host is built
// and seeded once; the seeded database is copied to a template with SQLite's
// online backup, and every scenario starts by copying the template back over
// the live file the same way. The storage root is restored from a snapshot
// too. A scenario therefore sees exactly the seeded library, never the
// previous scenario's writes (QualityBedIsolationTests proves it).
//
// In-memory assistant state (changesets, continuations, turn registry) lives
// on for the run, as it does in production between conversations; it is keyed
// by conversation id, and every scenario uses a fresh one.
namespace Nostos.Backend.Tests.Assistant.QualityBed;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal sealed class QualityBedSharedHost : IDisposable
{
    private readonly string _liveConnection;
    private readonly string _templateConnection;
    private readonly string _templatePath;
    private readonly string _storageRoot;
    private readonly string _storageTemplate;

    public QualityBedHost Host { get; }

    private QualityBedSharedHost(QualityBedHost host)
    {
        Host = host;
        var databasePath = host.Factory.DatabasePath;
        _templatePath = databasePath + ".template";
        // Unpooled: the restore connections must not linger next to the
        // host's own pool, which keeps using the plain "Data Source=" key.
        _liveConnection = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        _templateConnection = new SqliteConnectionStringBuilder
        {
            DataSource = _templatePath,
            Pooling = false,
        }.ToString();
        _storageRoot = host.Factory.StorageTempRoot;
        _storageTemplate = _storageRoot + ".template";
    }

    /// <summary>Builds the host, seeds it once and takes the template.</summary>
    public static QualityBedSharedHost CreateDeterministic(string keyVariable)
    {
        var host = QualityBedHost.CreateDeterministic(new QualityMetricsSink(), keyVariable);
        try
        {
            using (var scope = host.Services.CreateScope())
                QualityLibrarySeeder.SeedAsync(scope.ServiceProvider).GetAwaiter().GetResult();

            var shared = new QualityBedSharedHost(host);
            shared.TakeTemplate();
            return shared;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The host for one scenario, with the seeded library restored. Disposing
    /// the lease does not stop the host.
    /// </summary>
    public QualityBedHost Lease()
    {
        Restore();
        return Host.Borrow();
    }

    /// <summary>Puts the seeded database and storage root back.</summary>
    public void Restore()
    {
        using (var template = new SqliteConnection(_templateConnection))
        using (var live = new SqliteConnection(_liveConnection))
        {
            template.Open();
            live.Open();
            // The backup API replaces every page of the live database in one
            // write transaction, whatever its journal mode, and the host's
            // pooled connections see the new schema/data on their next read.
            template.BackupDatabase(live);
        }

        if (Directory.Exists(_storageRoot))
            Directory.Delete(_storageRoot, recursive: true);
        if (Directory.Exists(_storageTemplate))
            CopyDirectory(_storageTemplate, _storageRoot);
    }

    /// <summary>
    /// Removes the app's own background workers, keeping the framework's
    /// hosted services (the web host itself is one).
    /// </summary>
    public static void RemoveAppWorkers(IServiceCollection services)
    {
        var workers = services
            .Where(d => d.ServiceType == typeof(IHostedService) && !IsFramework(d))
            .ToList();
        foreach (var worker in workers)
            services.Remove(worker);

        static bool IsFramework(ServiceDescriptor descriptor)
        {
            var type = descriptor.ImplementationType
                ?? descriptor.ImplementationInstance?.GetType()
                ?? descriptor.ImplementationFactory?.Method.DeclaringType;
            return type?.Assembly.GetName().Name?.StartsWith("Microsoft.", StringComparison.Ordinal) == true;
        }
    }

    public void Dispose()
    {
        Host.Dispose();
        TryDelete(() => File.Delete(_templatePath));
        TryDelete(() =>
        {
            if (Directory.Exists(_storageTemplate))
                Directory.Delete(_storageTemplate, recursive: true);
        });
    }

    private void TakeTemplate()
    {
        using (var live = new SqliteConnection(_liveConnection))
        using (var template = new SqliteConnection(_templateConnection))
        {
            live.Open();
            template.Open();
            live.BackupDatabase(template);
        }

        if (Directory.Exists(_storageRoot))
            CopyDirectory(_storageRoot, _storageTemplate);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
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
